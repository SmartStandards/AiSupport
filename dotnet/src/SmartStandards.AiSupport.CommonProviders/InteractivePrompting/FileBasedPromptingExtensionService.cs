using AI.SmartStandards.InteractivePrompting.Harness;
using Logging.SmartStandards.CopyForAI.SmartStandards;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Extension service which persists the registered skill sources as JSON file (non-secret metadata only,
  /// default: '%APPDATA%\SmartStandards\AiSupport\extensions.json') and provides the built-in sources
  /// 'global-user-preferences' and 'global-system-preferences'. Referenced files are never copied: their content
  /// is read on demand. If a toolbox is attached, the sources and their skills are kept synchronized with it.
  /// </summary>
  public partial class FileBasedPromptingExtensionService : IPromptingExtensionService {

    /// <summary>
    /// Id (and skill name) of the built-in source carrying the preferences of the current user.
    /// </summary>
    public const string UserPreferencesSourceId = "global-user-preferences";

    /// <summary>
    /// Id (and skill name) of the built-in source carrying the system-wide preferences.
    /// </summary>
    public const string SystemPreferencesSourceId = "global-system-preferences";

    private readonly string _ExtensionsFileFullName;

    private readonly string _UserPreferencesFileFullName;

    private readonly string _DefaultSystemPreferencesFileFullName;

    private readonly object _SyncRoot = new object();

    private readonly List<string> _RegisteredSourceIds = new List<string>();

    private HarnessToolbox _AttachedToolbox;

    private Func<string, string, string> _RemoteContentLoader = LoadRemoteContent;

    private ISecretProtector _SecretProtector = new KeyFileSecretProtector();

    private Func<int> _UserLevelGetter;

    /// <summary>
    /// Optional resolver of the level of the current user (see 'PromptingUserLevels', ROL-1..13). All modifying
    /// operations of this service (incl. the provider-specific tool settings, ROL-10) and the file system browsing
    /// (ROL-13) require the level 'Admin'. Without resolver, the current user is 'Admin' (no restriction).
    /// </summary>
    public Func<int> UserLevelGetter {
      get {
        return _UserLevelGetter;
      }
      set {
        _UserLevelGetter = value;
      }
    }

    /// <summary>
    /// Creates the service using the default locations
    /// user preferences within '%USERPROFILE%\.agents\skills', system preferences within '%PUBLIC%\.agents\skills').
    /// </summary>
    public FileBasedPromptingExtensionService() : this(
      GetDefaultExtensionsFileFullName(), GetDefaultUserPreferencesFileFullName(), GetDefaultSystemPreferencesFileFullName()
    ) {
    }

    /// <summary>
    /// Creates the service using explicit locations.
    /// </summary>
    /// <param name="extensionsFileFullName">JSON file which persists the registered sources.</param>
    /// <param name="userPreferencesFileFullName">'SKILL.md' of the built-in source 'global-user-preferences'.</param>
    /// <param name="defaultSystemPreferencesFileFullName">
    /// 'SKILL.md' of the built-in source 'global-system-preferences' (used if no location was configured).
    /// </param>
    public FileBasedPromptingExtensionService(
      string extensionsFileFullName, string userPreferencesFileFullName, string defaultSystemPreferencesFileFullName
    ) {

      if (string.IsNullOrWhiteSpace(extensionsFileFullName)) {
        throw new ArgumentNullException(nameof(extensionsFileFullName));
      }

      if (string.IsNullOrWhiteSpace(userPreferencesFileFullName)) {
        throw new ArgumentNullException(nameof(userPreferencesFileFullName));
      }

      if (string.IsNullOrWhiteSpace(defaultSystemPreferencesFileFullName)) {
        throw new ArgumentNullException(nameof(defaultSystemPreferencesFileFullName));
      }

      _ExtensionsFileFullName = extensionsFileFullName;
      _UserPreferencesFileFullName = userPreferencesFileFullName;
      _DefaultSystemPreferencesFileFullName = defaultSystemPreferencesFileFullName;

    }

    /// <summary>
    /// Returns '%APPDATA%\SmartStandards\AiSupport\extensions.json'.
    /// </summary>
    public static string GetDefaultExtensionsFileFullName() {

      string roamingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

      return Path.Combine(roamingDirectory, "SmartStandards", "AiSupport", "extensions.json");
    }

    /// <summary>
    /// Returns '%USERPROFILE%\.agents\skills\global-user-preferences\SKILL.md'.
    /// </summary>
    public static string GetDefaultUserPreferencesFileFullName() {

      string userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

      return Path.Combine(userProfileDirectory, ".agents", "skills", UserPreferencesSourceId, SkillFile.SkillFileName);
    }

    /// <summary>
    /// Returns '%PUBLIC%\.agents\skills\global-system-preferences\SKILL.md'
    /// (falls back to the common application data folder if '%PUBLIC%' is not defined).
    /// </summary>
    public static string GetDefaultSystemPreferencesFileFullName() {

      string publicDirectory = Environment.GetEnvironmentVariable("PUBLIC");

      if (string.IsNullOrWhiteSpace(publicDirectory)) {
        publicDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
      }

      return Path.Combine(publicDirectory, ".agents", "skills", SystemPreferencesSourceId, SkillFile.SkillFileName);
    }

    public string ExtensionsFileFullName {
      get {
        return _ExtensionsFileFullName;
      }
    }

    public string UserPreferencesFileFullName {
      get {
        return _UserPreferencesFileFullName;
      }
    }

    /// <summary>
    /// Directory which contains the offline copies of the sources with 'secure availability'
    /// (one sub directory per SourceId, located beside the extensions file).
    /// </summary>
    public string OfflineCopiesDirectory {
      get {

        string directory = Path.GetDirectoryName(_ExtensionsFileFullName);

        if (string.IsNullOrWhiteSpace(directory)) {
          directory = Directory.GetCurrentDirectory();
        }

        return Path.Combine(directory, "SkillSourceOfflineCopies");
      }
    }

    /// <summary>
    /// File which contains the credentials (PAT/token) of remote sources, separated from the non-secret
    /// metadata within the extensions file (located beside the extensions file).
    /// </summary>
    public string CredentialsFileFullName {
      get {

        string directory = Path.GetDirectoryName(_ExtensionsFileFullName);

        if (string.IsNullOrWhiteSpace(directory)) {
          directory = Directory.GetCurrentDirectory();
        }

        return Path.Combine(directory, "extensions.credentials.json");
      }
    }

    /// <summary>
    /// Loads the content of a remote URL: (url, credential) -> content, null if the resource does not exist (HTTP 404).
    /// Throws on any other failure. Replaceable (for example for tests or proxies), default: HttpClient.
    /// </summary>
    public Func<string, string, string> RemoteContentLoader {
      get {
        return _RemoteContentLoader;
      }
      set {

        if (value == null) {
          throw new ArgumentNullException(nameof(value));
        }

        _RemoteContentLoader = value;

      }
    }

    /// <summary>
    /// Protects the credentials (PAT/token) within the credentials file. Default: 'KeyFileSecretProtector'
    /// (AES + HMAC with a machine-local key file). Plain text values of older versions are still accepted and
    /// will be protected on the next write.
    /// </summary>
    public ISecretProtector SecretProtector {
      get {
        return _SecretProtector;
      }
      set {

        if (value == null) {
          throw new ArgumentNullException(nameof(value));
        }

        _SecretProtector = value;

      }
    }

    /// <summary>
    /// Refreshes the offline copies of all sources with 'secure availability' and reloads all remote sources
    /// ('Url', 'GitHub') into their local cache (intended to be called once on every new chat start).
    /// Unreachable sources keep their last offline copy.
    /// </summary>
    public void RefreshOfflineCopies() {

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();
        Dictionary<string, string> credentials = this.ReadCredentials();
        bool anyRefreshed = false;

        foreach (PersistedSkillSource source in persisted.SkillSources) {

          if (IsRemoteKind(source.Kind)) {

            //the state (timestamp or last error) of a remote source changes in any case
            this.TryRefreshRemoteCopy(source, GetCredential(credentials, source.SourceId));
            anyRefreshed = true;

          }
          else if (source.SecureAvailability && this.TryRefreshOfflineCopy(source)) {
            anyRefreshed = true;
          }

        }

        if (anyRefreshed) {
          this.WritePersisted(persisted);
        }

        this.ResolveAndSynchronize(persisted);
      }

    }

    /// <summary>
    /// Attaches the toolbox, into which the sources and skills are registered (and kept synchronized on every change).
    /// </summary>
    public void AttachToolbox(HarnessToolbox toolbox) {

      if (toolbox == null) {
        throw new ArgumentNullException(nameof(toolbox));
      }

      lock (_SyncRoot) {
        _AttachedToolbox = toolbox;
      }

      this.Refresh();
      this.RefreshToolSources();

    }

    /// <summary>
    /// Re-resolves all sources (detects new, changed or broken references) and synchronizes the attached toolbox.
    /// </summary>
    public void Refresh() {

      lock (_SyncRoot) {
        this.ResolveAndSynchronize(this.ReadPersisted());
      }

    }

    #region " IPromptingExtensionService "

    /// <summary>
    /// Returns all skill sources (built-in sources first). Broken references are flagged (never removed).
    /// </summary>
    public SkillSourceInfo[] GetSkillSources() {

      lock (_SyncRoot) {

        List<ResolvedSource> resolvedSources = this.ResolveAndSynchronize(this.ReadPersisted());

        return resolvedSources.Select((r) => r.Info).ToArray();
      }

    }

    /// <summary>
    /// Adds a reference to a single skill file (local or UNC path). The file is never copied.
    /// </summary>
    public string AddSkillFileReference(string filePath) {
      return this.AddSkillSource(SkillSourceKinds.File, filePath, null);
    }

    /// <summary>
    /// Adds a skill source of the given kind ('File', 'Folder', 'Url' or 'GitHub'). Credentials (PAT/token) are only
    /// supported for remote sources and are stored separately from the metadata. Remote sources are loaded immediately
    /// into their local cache (a failed load keeps the source, flagged as broken).
    /// </summary>
    public string AddSkillSource(string kind, string location, string credential) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(AddSkillSource));

      if (string.IsNullOrWhiteSpace(location)) {
        throw new ArgumentNullException(nameof(location));
      }

      string normalizedKind = NormalizeKind(kind);

      if (IsRemoteKind(normalizedKind)) {
        return this.AddRemoteSkillSource(normalizedKind, location, credential);
      }

      if (normalizedKind != SkillSourceKinds.File && normalizedKind != SkillSourceKinds.Folder) {
        throw new NotSupportedException($"Skill sources of kind '{kind}' are not supported.");
      }

      if (!string.IsNullOrWhiteSpace(credential)) {
        throw new NotSupportedException("Credentials are not supported for local skill sources.");
      }

      string fullLocation = Path.GetFullPath(location.Trim().Trim('"'));

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        PersistedSkillSource existing = persisted.SkillSources.FirstOrDefault(
          (s) => s.Kind == normalizedKind && string.Equals(s.Location, fullLocation, StringComparison.OrdinalIgnoreCase)
        );

        if (existing != null) {
          return existing.SourceId;
        }

        PersistedSkillSource source = new PersistedSkillSource();
        source.SourceId = CreateSourceId(normalizedKind, fullLocation, persisted);
        source.Kind = normalizedKind;
        source.Location = fullLocation;
        source.DisplayName = DeriveDisplayName(normalizedKind, fullLocation);
        source.Level = SkillSourceLevels.OnDemand;

        persisted.SkillSources.Add(source);

        this.WritePersisted(persisted);
        this.ResolveAndSynchronize(persisted);

        return source.SourceId;
      }

    }

    /// <summary>
    /// Removes a (non-fixed) skill source. Unknown ids are ignored.
    /// </summary>
    public void RemoveSkillSource(string sourceId) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(RemoveSkillSource));

      if (IsBuiltInSourceId(sourceId)) {
        throw new InvalidOperationException($"The built-in skill source '{sourceId}' cannot be removed.");
      }

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        int removedCount = persisted.SkillSources.RemoveAll(
          (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
        );

        if (removedCount > 0) {
          this.WritePersisted(persisted);
          this.DeleteOfflineCopy(sourceId);
          this.SetCredential(sourceId, null);
        }

        this.ResolveAndSynchronize(persisted);
      }

    }

    /// <summary>
    /// Sets the global default level (see 'SkillSourceLevels') of a (non-fixed) skill source.
    /// </summary>
    public void SetSkillSourceLevel(string sourceId, string level) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetSkillSourceLevel));

      if (IsBuiltInSourceId(sourceId)) {
        throw new InvalidOperationException($"The level of the built-in skill source '{sourceId}' cannot be changed.");
      }

      string normalizedLevel = NormalizeSkillLevel(level);

      if (normalizedLevel == null) {
        throw new ArgumentException($"Unknown skill source level '{level}'.", nameof(level));
      }

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        PersistedSkillSource source = persisted.SkillSources.FirstOrDefault(
          (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
        );

        if (source == null) {
          throw new ArgumentException($"Unknown skill source '{sourceId}'.", nameof(sourceId));
        }

        source.Level = normalizedLevel;

        this.WritePersisted(persisted);
        this.ResolveAndSynchronize(persisted);
      }

    }

    /// <summary>
    /// Sets the location (local or UNC path to a 'SKILL.md' or to its folder) of 'global-system-preferences'.
    /// An empty value restores the default location.
    /// </summary>
    public void SetSystemPreferencesLocation(string location) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetSystemPreferencesLocation));

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        if (string.IsNullOrWhiteSpace(location)) {
          persisted.SystemPreferencesLocation = null;
        }
        else {
          persisted.SystemPreferencesLocation = location.Trim().Trim('"');
        }

        this.WritePersisted(persisted);
        this.ResolveAndSynchronize(persisted);
      }

    }

    /// <summary>
    /// Returns the configured location of 'global-system-preferences' (empty string = default location).
    /// </summary>
    public string GetSystemPreferencesLocation() {

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        if (persisted.SystemPreferencesLocation == null) {
          return string.Empty;
        }

        return persisted.SystemPreferencesLocation;
      }

    }

    /// <summary>
    /// Enables or disables 'secure availability' (local offline copy) of a file or folder source.
    /// Enabling creates the offline copy immediately (if the original location is reachable), disabling deletes it.
    /// </summary>
    public void SetSkillSourceSecureAvailability(string sourceId, bool enabled) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetSkillSourceSecureAvailability));

      if (IsBuiltInSourceId(sourceId)) {
        throw new InvalidOperationException($"Secure availability is not supported for the built-in skill source '{sourceId}'.");
      }

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        PersistedSkillSource source = persisted.SkillSources.FirstOrDefault(
          (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
        );

        if (source == null) {
          throw new ArgumentException($"Unknown skill source '{sourceId}'.", nameof(sourceId));
        }

        if (!SupportsSecureAvailability(source.Kind)) {
          throw new NotSupportedException($"Secure availability is not supported for skill sources of kind '{source.Kind}'.");
        }

        source.SecureAvailability = enabled;

        if (enabled) {
          this.TryRefreshOfflineCopy(source);
        }
        else {
          this.DeleteOfflineCopy(source.SourceId);
          source.OfflineCopyTimestampUtc = null;
        }

        this.WritePersisted(persisted);
        this.ResolveAndSynchronize(persisted);
      }

    }

    /// <summary>
    /// Sets (or with an empty value removes) the credential (PAT/token) of a remote source and reloads its content.
    /// </summary>
    public void SetSkillSourceCredential(string sourceId, string credential) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetSkillSourceCredential));

      if (IsBuiltInSourceId(sourceId)) {
        throw new InvalidOperationException($"Credentials are not supported for the built-in skill source '{sourceId}'.");
      }

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        PersistedSkillSource source = persisted.SkillSources.FirstOrDefault(
          (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
        );

        if (source == null) {
          throw new ArgumentException($"Unknown skill source '{sourceId}'.", nameof(sourceId));
        }

        if (!IsRemoteKind(source.Kind)) {
          throw new NotSupportedException($"Credentials are not supported for skill sources of kind '{source.Kind}'.");
        }

        this.SetCredential(source.SourceId, credential);
        this.TryRefreshRemoteCopy(source, NormalizeCredential(credential));

        this.WritePersisted(persisted);
        this.ResolveAndSynchronize(persisted);
      }

    }

    /// <summary>
    /// Sets the (freely selectable) label of a skill source. An empty value restores the derived default label.
    /// </summary>
    public void SetSkillSourceDisplayName(string sourceId, string displayName) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetSkillSourceDisplayName));

      if (IsBuiltInSourceId(sourceId)) {
        throw new InvalidOperationException($"The label of the built-in skill source '{sourceId}' cannot be changed.");
      }

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        PersistedSkillSource source = persisted.SkillSources.FirstOrDefault(
          (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
        );

        if (source == null) {
          throw new ArgumentException($"Unknown skill source '{sourceId}'.", nameof(sourceId));
        }

        if (string.IsNullOrWhiteSpace(displayName)) {
          source.DisplayName = DeriveDisplayName(source.Kind, source.Location);
        }
        else {
          source.DisplayName = displayName.Trim();
        }

        this.WritePersisted(persisted);
        this.ResolveAndSynchronize(persisted);
      }

    }

    /// <summary>
    /// Lists the entries of a (server-side) directory (empty path = root entries) for the browse dialog.
    /// </summary>
    public FileSystemEntryInfo[] BrowseFileSystem(string path, bool includeFiles) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(BrowseFileSystem));

      if (includeFiles) {
        return this.BrowseFileSystemCore(path, "*.md");
      }

      return this.BrowseFileSystemCore(path, null);
    }

    /// <summary>
    /// Lists the entries of a (server-side) directory with the files matching the pattern ('*.md' or '*.dll').
    /// </summary>
    public FileSystemEntryInfo[] BrowseFileSystemFiltered(string path, string filePattern) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(BrowseFileSystemFiltered));

      string pattern = null;

      if (string.Equals(filePattern, "*.md", StringComparison.OrdinalIgnoreCase) || string.Equals(filePattern, "*.dll", StringComparison.OrdinalIgnoreCase)) {
        pattern = filePattern.ToLowerInvariant();
      }
      else if (!string.IsNullOrWhiteSpace(filePattern)) {
        throw new ArgumentException($"The file pattern '{filePattern}' is not supported.", nameof(filePattern));
      }

      return this.BrowseFileSystemCore(path, pattern);
    }

    private FileSystemEntryInfo[] BrowseFileSystemCore(string path, string filePattern) {

      List<FileSystemEntryInfo> result = new List<FileSystemEntryInfo>();

      if (string.IsNullOrWhiteSpace(path)) {

        foreach (DriveInfo drive in DriveInfo.GetDrives()) {

          if (!drive.IsReady) {
            continue;
          }

          FileSystemEntryInfo entry = new FileSystemEntryInfo();
          entry.Name = drive.Name;
          entry.FullName = drive.RootDirectory.FullName;
          entry.IsDirectory = true;
          result.Add(entry);
        }

        return result.ToArray();
      }

      DirectoryInfo directory = new DirectoryInfo(path.Trim());

      if (!directory.Exists) {
        throw new DirectoryNotFoundException($"The directory '{path}' does not exist or is not accessible.");
      }

      try {

        foreach (DirectoryInfo subDirectory in directory.GetDirectories().OrderBy((d) => d.Name, StringComparer.OrdinalIgnoreCase)) {

          if ((subDirectory.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) {
            continue;
          }

          FileSystemEntryInfo entry = new FileSystemEntryInfo();
          entry.Name = subDirectory.Name;
          entry.FullName = subDirectory.FullName;
          entry.IsDirectory = true;
          result.Add(entry);
        }

        if (filePattern != null) {

          foreach (FileInfo file in directory.GetFiles(filePattern).OrderBy((f) => f.Name, StringComparer.OrdinalIgnoreCase)) {
            FileSystemEntryInfo entry = new FileSystemEntryInfo();
            entry.Name = file.Name;
            entry.FullName = file.FullName;
            entry.IsDirectory = false;
            result.Add(entry);
          }

        }

      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        throw new UnauthorizedAccessException($"Access to the directory '{path}' is denied.");
      }

      return result.ToArray();
    }

    #endregion

    #region " Remote sources "

    private string AddRemoteSkillSource(string kind, string location, string credential) {

      string normalizedLocation;

      if (kind == SkillSourceKinds.GitHub) {
        normalizedLocation = GitHubLocation.Parse(location).ToString();
      }
      else {
        normalizedLocation = NormalizeUrl(location);
      }

      lock (_SyncRoot) {

        PersistedExtensions persisted = this.ReadPersisted();

        PersistedSkillSource existing = persisted.SkillSources.FirstOrDefault(
          (s) => s.Kind == kind && string.Equals(s.Location, normalizedLocation, StringComparison.OrdinalIgnoreCase)
        );

        if (existing != null) {
          return existing.SourceId;
        }

        PersistedSkillSource source = new PersistedSkillSource();
        source.SourceId = CreateSourceId(kind, normalizedLocation, persisted);
        source.Kind = kind;
        source.Location = normalizedLocation;
        source.DisplayName = DeriveDisplayName(kind, normalizedLocation);
        source.Level = SkillSourceLevels.OnDemand;

        persisted.SkillSources.Add(source);

        this.SetCredential(source.SourceId, credential);
        this.TryRefreshRemoteCopy(source, NormalizeCredential(credential));

        this.WritePersisted(persisted);
        this.ResolveAndSynchronize(persisted);

        return source.SourceId;
      }

    }

    /// <summary>
    /// Loads the skills of a remote source into its local cache (atomic replacement). On failure the existing cache
    /// is kept and the reason is persisted as 'LastRemoteError'.
    /// </summary>
    private bool TryRefreshRemoteCopy(PersistedSkillSource source, string credential) {

      List<KeyValuePair<string, string>> loadedSkills;

      try {

        if (source.Kind == SkillSourceKinds.GitHub) {
          loadedSkills = this.LoadGitHubSkills(GitHubLocation.Parse(source.Location), credential);
        }
        else {
          loadedSkills = this.LoadUrlSkill(source.Location, credential);
        }

      }
      catch (Exception ex) {
        DevLogger.LogError(ex);
        source.LastRemoteError = $"Loading '{source.Location}' failed: {ex.Message}";
        return false;
      }

      List<SkillFile> skillFiles = new List<SkillFile>();

      foreach (KeyValuePair<string, string> loadedSkill in loadedSkills) {

        SkillFile skillFile = SkillFile.Parse(loadedSkill.Value);

        if (string.IsNullOrWhiteSpace(skillFile.Name)) {
          skillFile.Name = loadedSkill.Key;
        }

        skillFiles.Add(skillFile);
      }

      if (!this.TryReplaceOfflineCopy(source.SourceId, skillFiles)) {
        source.LastRemoteError = $"The local cache of '{source.Location}' could not be written.";
        return false;
      }

      source.LastRemoteError = null;
      source.OfflineCopyTimestampUtc = DateTime.UtcNow.ToString("o");

      return true;
    }

    private List<KeyValuePair<string, string>> LoadUrlSkill(string url, string credential) {

      string content = _RemoteContentLoader.Invoke(url, credential);

      if (content == null) {
        throw new InvalidOperationException("The resource does not exist (HTTP 404).");
      }

      Uri uri = new Uri(url);
      List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
      result.Add(new KeyValuePair<string, string>(DeriveNameFromUrlPath(uri.AbsolutePath, uri.Host), content));

      return result;
    }

    /// <summary>
    /// Loads the skills of a GitHub location via the REST API ('contents' endpoint): either a single skill
    /// (file or folder containing 'SKILL.md') or a container of skill folders ('&lt;skill&gt;/SKILL.md').
    /// </summary>
    private List<KeyValuePair<string, string>> LoadGitHubSkills(GitHubLocation location, string credential) {

      List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
      string fallbackName = DeriveNameFromUrlPath(location.SubPath, location.Repository);

      string listing = _RemoteContentLoader.Invoke(location.GetContentsApiUrl(location.SubPath), credential);

      if (listing == null) {
        throw new InvalidOperationException("The repository or path does not exist or is not accessible (HTTP 404).");
      }

      JToken parsed = JToken.Parse(listing);

      if (parsed is JObject) {
        result.Add(new KeyValuePair<string, string>(fallbackName, DecodeGitHubFileContent((JObject)parsed)));
        return result;
      }

      JArray entries = (JArray)parsed;

      bool containsSkillFile = entries.Any(
        (e) => (string)e["type"] == "file" && string.Equals((string)e["name"], SkillFile.SkillFileName, StringComparison.OrdinalIgnoreCase)
      );

      if (containsSkillFile) {

        string skillContent = this.LoadGitHubFile(location, CombineUrlPath(location.SubPath, SkillFile.SkillFileName), credential);

        if (skillContent != null) {
          result.Add(new KeyValuePair<string, string>(fallbackName, skillContent));
        }

        return result;
      }

      string[] directoryNames = entries
        .Where((e) => (string)e["type"] == "dir")
        .Select((e) => (string)e["name"])
        .OrderBy((n) => n, StringComparer.OrdinalIgnoreCase)
        .ToArray();

      foreach (string directoryName in directoryNames) {

        string skillFilePath = CombineUrlPath(CombineUrlPath(location.SubPath, directoryName), SkillFile.SkillFileName);
        string skillContent = this.LoadGitHubFile(location, skillFilePath, credential);

        if (skillContent != null) {
          result.Add(new KeyValuePair<string, string>(directoryName, skillContent));
        }

      }

      if (result.Count == 0) {
        throw new InvalidOperationException($"The location does not contain any '{SkillFile.SkillFileName}'.");
      }

      return result;
    }

    /// <summary>
    /// Returns the decoded content of a file within the repository or null if it does not exist.
    /// </summary>
    private string LoadGitHubFile(GitHubLocation location, string path, string credential) {

      string json = _RemoteContentLoader.Invoke(location.GetContentsApiUrl(path), credential);

      if (json == null) {
        return null;
      }

      return DecodeGitHubFileContent(JObject.Parse(json));
    }

    private static string DecodeGitHubFileContent(JObject file) {

      string content = (string)file["content"];
      string encoding = (string)file["encoding"];

      if (content == null) {
        throw new InvalidOperationException("The GitHub response does not contain any file content.");
      }

      if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)) {
        return content;
      }

      byte[] bytes = Convert.FromBase64String(content.Replace("\n", string.Empty).Replace("\r", string.Empty));

      return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Default implementation of 'RemoteContentLoader' (HttpClient, bearer authentication if a credential is given).
    /// </summary>
    private static string LoadRemoteContent(string url, string credential) {

      using (HttpClient client = new HttpClient()) {

        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartStandards-AiSupport");

        if (url.StartsWith(GitHubLocation.ApiBaseUrl, StringComparison.OrdinalIgnoreCase)) {
          client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        if (!string.IsNullOrWhiteSpace(credential)) {
          client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();

        if (response.StatusCode == HttpStatusCode.NotFound) {
          return null;
        }

        string content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode) {
          throw new HttpRequestException($"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        return content;
      }

    }

    private static bool IsRemoteKind(string kind) {
      return kind == SkillSourceKinds.Url || kind == SkillSourceKinds.GitHub;
    }

    private static string NormalizeUrl(string location) {

      Uri uri;
      string trimmed = location.Trim().Trim('"');

      if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
        throw new ArgumentException($"'{location}' is not a valid http(s) URL.", nameof(location));
      }

      return uri.AbsoluteUri;
    }

    /// <summary>
    /// Derives a skill name from an URL path: the parent segment for 'SKILL.md', otherwise the file name without extension.
    /// </summary>
    private static string DeriveNameFromUrlPath(string path, string fallbackName) {

      string[] segments = new string[0];

      if (path != null) {
        segments = path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
      }

      if (segments.Length == 0) {
        return fallbackName;
      }

      string lastSegment = Uri.UnescapeDataString(segments[segments.Length - 1]);

      if (string.Equals(lastSegment, SkillFile.SkillFileName, StringComparison.OrdinalIgnoreCase)) {

        if (segments.Length >= 2) {
          return Uri.UnescapeDataString(segments[segments.Length - 2]);
        }

        return fallbackName;
      }

      return Path.GetFileNameWithoutExtension(lastSegment);
    }

    private static string CombineUrlPath(string basePath, string name) {

      if (string.IsNullOrEmpty(basePath)) {
        return name;
      }

      return basePath + "/" + name;
    }

    /// <summary>
    /// Parsed GitHub location: 'https://github.com/{owner}/{repo}[/tree|blob/{ref}/{path}]' or '{owner}/{repo}'.
    /// </summary>
    private sealed class GitHubLocation {

      public const string ApiBaseUrl = "https://api.github.com/";

      private string _Owner;
      private string _Repository;
      private string _Ref;
      private string _SubPath = string.Empty;

      public string Repository {
        get {
          return _Repository;
        }
      }

      public string SubPath {
        get {
          return _SubPath;
        }
      }

      public static GitHubLocation Parse(string location) {

        string path = location.Trim().Trim('"').TrimEnd('/');
        string[] prefixes = new string[] { "https://github.com/", "http://github.com/", "https://www.github.com/", "github.com/" };

        foreach (string prefix in prefixes) {
          if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
            path = path.Substring(prefix.Length);
            break;
          }
        }

        if (path.Contains("://")) {
          throw new ArgumentException($"'{location}' is not a GitHub repository location.", nameof(location));
        }

        string[] segments = path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2) {
          throw new ArgumentException($"'{location}' does not contain owner and repository.", nameof(location));
        }

        GitHubLocation result = new GitHubLocation();
        result._Owner = segments[0];
        result._Repository = segments[1];

        if (result._Repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) {
          result._Repository = result._Repository.Substring(0, result._Repository.Length - 4);
        }

        if (segments.Length >= 4 && (segments[2] == "tree" || segments[2] == "blob")) {
          result._Ref = segments[3];
          result._SubPath = string.Join("/", segments.Skip(4));
        }
        else if (segments.Length > 2) {
          throw new ArgumentException(
            $"'{location}' is not supported (expected '{{owner}}/{{repo}}' optionally followed by '/tree/{{ref}}/{{path}}').", nameof(location)
          );
        }

        return result;
      }

      /// <summary>
      /// Returns the URL of the GitHub REST 'contents' endpoint for the given repository path.
      /// </summary>
      public string GetContentsApiUrl(string path) {

        StringBuilder url = new StringBuilder();
        url.Append(ApiBaseUrl).Append("repos/");
        url.Append(Uri.EscapeDataString(_Owner)).Append('/').Append(Uri.EscapeDataString(_Repository));
        url.Append("/contents");

        if (!string.IsNullOrEmpty(path)) {

          foreach (string segment in path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) {
            url.Append('/').Append(Uri.EscapeDataString(segment));
          }

        }

        if (!string.IsNullOrEmpty(_Ref)) {
          url.Append("?ref=").Append(Uri.EscapeDataString(_Ref));
        }

        return url.ToString();
      }

      public override string ToString() {

        string result = "https://github.com/" + _Owner + "/" + _Repository;

        if (!string.IsNullOrEmpty(_Ref)) {

          result = result + "/tree/" + _Ref;

          if (!string.IsNullOrEmpty(_SubPath)) {
            result = result + "/" + _SubPath;
          }

        }

        return result;
      }

    }

    #endregion

    #region " Credentials "

    /// <summary>
    /// Stores (or with an empty value removes) the credential of a source within the separate credentials file.
    /// </summary>
    private void SetCredential(string sourceId, string credential) {

      Dictionary<string, string> credentials = this.ReadCredentials();
      string normalizedCredential = NormalizeCredential(credential);

      if (normalizedCredential == null) {

        if (!credentials.Remove(sourceId)) {
          return;
        }

      }
      else {
        credentials[sourceId] = normalizedCredential;
      }

      if (credentials.Count == 0) {

        if (File.Exists(this.CredentialsFileFullName)) {
          File.Delete(this.CredentialsFileFullName);
        }

        return;
      }

      string directory = Path.GetDirectoryName(this.CredentialsFileFullName);

      if (!string.IsNullOrWhiteSpace(directory)) {
        Directory.CreateDirectory(directory);
      }

      Dictionary<string, string> protectedCredentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      foreach (KeyValuePair<string, string> entry in credentials) {
        protectedCredentials[entry.Key] = _SecretProtector.Protect(entry.Value);
      }

      File.WriteAllText(this.CredentialsFileFullName, JsonConvert.SerializeObject(protectedCredentials, Formatting.Indented));

    }

    /// <summary>
    /// Reads the credentials file and returns the plain credentials per SourceId. Values which cannot be
    /// unprotected (manipulated or protected with another key) are logged and treated as missing.
    /// </summary>
    private Dictionary<string, string> ReadCredentials() {

      Dictionary<string, string> persistedCredentials = null;
      Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      if (File.Exists(this.CredentialsFileFullName)) {
        persistedCredentials = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(this.CredentialsFileFullName));
      }

      if (persistedCredentials == null) {
        return result;
      }

      foreach (KeyValuePair<string, string> entry in persistedCredentials) {

        if (string.IsNullOrWhiteSpace(entry.Value)) {
          continue;
        }

        if (!_SecretProtector.IsProtected(entry.Value)) {
          result[entry.Key] = entry.Value;
          continue;
        }

        try {
          result[entry.Key] = _SecretProtector.Unprotect(entry.Value);
        }
        catch (Exception ex) {
          DevLogger.LogError(ex);
        }

      }

      return result;
    }

    private static string GetCredential(Dictionary<string, string> credentials, string sourceId) {

      string credential;

      if (credentials.TryGetValue(sourceId, out credential)) {
        return credential;
      }

      return null;
    }

    private static string NormalizeCredential(string credential) {

      if (string.IsNullOrWhiteSpace(credential)) {
        return null;
      }

      return credential.Trim();
    }

    private static string MaskCredential(string credential) {

      if (credential.Length <= 8) {
        return "****";
      }

      return credential.Substring(0, 3) + "..." + credential.Substring(credential.Length - 4);
    }

    #endregion

    #region " Resolving "

    /// <summary>
    /// Resolves all sources (built-in + persisted) and synchronizes the attached toolbox (if any).
    /// Must be called within the lock.
    /// </summary>
    private List<ResolvedSource> ResolveAndSynchronize(PersistedExtensions persisted) {

      List<ResolvedSource> resolvedSources = new List<ResolvedSource>();

      resolvedSources.Add(this.ResolveUserPreferences());
      resolvedSources.Add(this.ResolveSystemPreferences(persisted));

      Dictionary<string, string> credentials = this.ReadCredentials();

      foreach (PersistedSkillSource source in persisted.SkillSources) {

        if (IsRemoteKind(source.Kind)) {
          resolvedSources.Add(this.ResolveRemoteSource(source, GetCredential(credentials, source.SourceId)));
        }
        else {
          resolvedSources.Add(this.ResolvePersistedSourceWithOfflineCopy(source));
        }

      }

      if (_AttachedToolbox != null) {
        this.SynchronizeToolbox(_AttachedToolbox, resolvedSources);
      }

      return resolvedSources;
    }

    private ResolvedSource ResolveUserPreferences() {

      SkillSourceInfo info = CreateBuiltInInfo(UserPreferencesSourceId, "Global user preferences", _UserPreferencesFileFullName);
      ResolvedSource resolved = new ResolvedSource(info);

      //a missing file is not an error: the user simply has not deposited any preferences
      if (File.Exists(_UserPreferencesFileFullName)) {
        AddSkillFromFile(resolved, _UserPreferencesFileFullName, UserPreferencesSourceId);
      }

      return resolved;
    }

    private ResolvedSource ResolveSystemPreferences(PersistedExtensions persisted) {

      bool isConfigured = !string.IsNullOrWhiteSpace(persisted.SystemPreferencesLocation);
      string fileFullName = _DefaultSystemPreferencesFileFullName;

      if (isConfigured) {
        fileFullName = persisted.SystemPreferencesLocation;

        if (Directory.Exists(fileFullName)) {
          fileFullName = Path.Combine(fileFullName, SkillFile.SkillFileName);
        }

      }

      SkillSourceInfo info = CreateBuiltInInfo(SystemPreferencesSourceId, "Global system preferences", fileFullName);
      ResolvedSource resolved = new ResolvedSource(info);

      if (File.Exists(fileFullName)) {
        AddSkillFromFile(resolved, fileFullName, SystemPreferencesSourceId);
      }
      else if (isConfigured) {
        //only an explicitly configured location is a broken reference (the default location is optional)
        MarkBroken(resolved, $"The file '{fileFullName}' does not exist or is not reachable.");
      }

      return resolved;
    }

    private static SkillSourceInfo CreatePersistedInfo(PersistedSkillSource source) {

      SkillSourceInfo info = new SkillSourceInfo();
      info.SourceId = source.SourceId;
      info.Kind = source.Kind;
      info.DisplayName = source.DisplayName;
      info.Location = source.Location;
      info.Level = NormalizeSkillLevel(source.Level);
      info.IsFixed = false;

      if (info.Level == null) {
        info.Level = SkillSourceLevels.OnDemand;
      }

      return info;
    }

    /// <summary>
    /// Resolves a remote source from its local cache (remote content is only loaded on add, on credential change
    /// and on every new chat start). The cache is always kept, so remote sources are implicitly 'secured'.
    /// </summary>
    private ResolvedSource ResolveRemoteSource(PersistedSkillSource source, string credential) {

      SkillSourceInfo info = CreatePersistedInfo(source);
      info.SupportsSecureAvailability = false;
      info.SecureAvailability = true;
      info.OfflineCopyTimestampUtc = source.OfflineCopyTimestampUtc;
      info.HasCredentials = (credential != null);

      if (credential != null) {
        info.CredentialHint = MaskCredential(credential);
      }

      ResolvedSource resolved = new ResolvedSource(info);

      this.AddSkillsFromOfflineCopy(resolved, source.SourceId);

      if (!string.IsNullOrWhiteSpace(source.LastRemoteError)) {

        string reason = source.LastRemoteError;

        if (resolved.SkillFiles.Count > 0) {
          reason = reason + $" The cached copy (from {source.OfflineCopyTimestampUtc} UTC) is used.";
        }

        MarkBroken(resolved, reason);

      }
      else if (resolved.SkillFiles.Count == 0) {
        MarkBroken(resolved, $"The source '{source.Location}' has not been loaded yet.");
      }

      info.SkillNames = resolved.SkillFiles.Select((f) => f.Key).ToArray();

      return resolved;
    }

    /// <summary>
    /// Adds all skills of the offline copy (cache) of the given source.
    /// </summary>
    private void AddSkillsFromOfflineCopy(ResolvedSource resolved, string sourceId) {

      string offlineDirectory = this.GetOfflineCopyDirectory(sourceId);

      if (!Directory.Exists(offlineDirectory)) {
        return;
      }

      string[] subDirectories = Directory.GetDirectories(offlineDirectory).OrderBy((d) => d, StringComparer.OrdinalIgnoreCase).ToArray();

      foreach (string subDirectory in subDirectories) {

        string skillFile = Path.Combine(subDirectory, SkillFile.SkillFileName);

        if (File.Exists(skillFile)) {
          AddSkillFromFile(resolved, skillFile, null);
        }

      }

    }

    private static ResolvedSource ResolvePersistedSource(PersistedSkillSource source) {

      ResolvedSource resolved = new ResolvedSource(CreatePersistedInfo(source));

      if (source.Kind == SkillSourceKinds.File) {

        if (File.Exists(source.Location)) {
          AddSkillFromFile(resolved, source.Location, null);
        }
        else {
          MarkBroken(resolved, $"The file '{source.Location}' does not exist or is not reachable.");
        }

      }
      else if (source.Kind == SkillSourceKinds.Folder) {

        if (Directory.Exists(source.Location)) {
          ResolveFolder(resolved, source.Location);
        }
        else {
          MarkBroken(resolved, $"The folder '{source.Location}' does not exist or is not reachable.");
        }

      }
      else {
        MarkBroken(resolved, $"Skill sources of kind '{source.Kind}' are not supported yet.");
      }

      resolved.Info.SkillNames = resolved.SkillFiles.Select((f) => f.Key).ToArray();

      return resolved;
    }

    /// <summary>
    /// Resolves a persisted source. If 'secure availability' is enabled, the offline copy is registered as fallback
    /// for every skill body - or, if the original location is not reachable, used instead of the original.
    /// </summary>
    private ResolvedSource ResolvePersistedSourceWithOfflineCopy(PersistedSkillSource source) {

      ResolvedSource resolved = ResolvePersistedSource(source);

      resolved.Info.SupportsSecureAvailability = SupportsSecureAvailability(source.Kind);
      resolved.Info.SecureAvailability = source.SecureAvailability;
      resolved.Info.OfflineCopyTimestampUtc = source.OfflineCopyTimestampUtc;

      if (!source.SecureAvailability) {
        return resolved;
      }

      string offlineDirectory = this.GetOfflineCopyDirectory(source.SourceId);

      if (!resolved.Info.IsReferenceBroken) {

        foreach (KeyValuePair<string, string> skillFile in resolved.SkillFiles) {
          resolved.FallbackFiles[skillFile.Key] = GetOfflineSkillFileFullName(offlineDirectory, skillFile.Key);
        }

        return resolved;
      }

      if (!Directory.Exists(offlineDirectory)) {
        return resolved;
      }

      string originalError = resolved.Info.LastError;
      ResolvedSource offline = new ResolvedSource(resolved.Info);

      this.AddSkillsFromOfflineCopy(offline, source.SourceId);

      if (offline.SkillFiles.Count == 0) {
        resolved.Info.LastError = originalError;
        return resolved;
      }

      //the reference stays flagged as broken (the user should know), but the skills remain available
      offline.Info.IsReferenceBroken = true;
      offline.Info.LastError = originalError + $" The offline copy (from {source.OfflineCopyTimestampUtc} UTC) is used.";
      offline.Info.SkillNames = offline.SkillFiles.Select((f) => f.Key).ToArray();

      return offline;
    }

    /// <summary>
    /// Replaces the offline copy of the given source by the current content of its original location.
    /// Returns false (and keeps the existing copy) if the original location is not (completely) reachable.
    /// </summary>
    private bool TryRefreshOfflineCopy(PersistedSkillSource source) {

      ResolvedSource original = ResolvePersistedSource(source);

      if (original.Info.IsReferenceBroken || original.SkillFiles.Count == 0) {
        return false;
      }

      List<SkillFile> skillFiles = new List<SkillFile>();

      try {

        foreach (KeyValuePair<string, string> skillFile in original.SkillFiles) {
          SkillFile loaded = SkillFile.Load(skillFile.Value);
          loaded.Name = skillFile.Key;
          skillFiles.Add(loaded);
        }

      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        return false;
      }

      if (!this.TryReplaceOfflineCopy(source.SourceId, skillFiles)) {
        return false;
      }

      source.OfflineCopyTimestampUtc = DateTime.UtcNow.ToString("o");

      return true;
    }

    /// <summary>
    /// Atomically replaces the offline copy (cache) of the given source by the given skill files.
    /// Returns false (and keeps the existing copy) if writing fails.
    /// </summary>
    private bool TryReplaceOfflineCopy(string sourceId, List<SkillFile> skillFiles) {

      string targetDirectory = this.GetOfflineCopyDirectory(sourceId);
      string tempDirectory = targetDirectory + ".refreshing";

      try {

        if (Directory.Exists(tempDirectory)) {
          Directory.Delete(tempDirectory, true);
        }

        Directory.CreateDirectory(tempDirectory);

        foreach (SkillFile skillFile in skillFiles) {

          string targetFile = GetOfflineSkillFileFullName(tempDirectory, skillFile.Name);

          //duplicate skill names within one source: the first one wins (like within the toolbox)
          if (File.Exists(targetFile)) {
            continue;
          }

          skillFile.Save(targetFile);
        }

        if (Directory.Exists(targetDirectory)) {
          Directory.Delete(targetDirectory, true);
        }

        Directory.Move(tempDirectory, targetDirectory);

      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
        return false;
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
        return false;
      }

      return true;
    }

    private void DeleteOfflineCopy(string sourceId) {

      string directory = this.GetOfflineCopyDirectory(sourceId);

      if (Directory.Exists(directory)) {
        Directory.Delete(directory, true);
      }

    }

    private string GetOfflineCopyDirectory(string sourceId) {
      return Path.Combine(this.OfflineCopiesDirectory, ToSlug(sourceId));
    }

    private static string GetOfflineSkillFileFullName(string offlineDirectory, string skillName) {
      return Path.Combine(offlineDirectory, ToSlug(skillName), SkillFile.SkillFileName);
    }

    private static bool SupportsSecureAvailability(string kind) {
      return kind == SkillSourceKinds.File || kind == SkillSourceKinds.Folder;
    }

    /// <summary>
    /// A folder is either a skill folder itself ('SKILL.md' directly inside)
    /// or a container of skill folders ('&lt;skill&gt;/SKILL.md').
    /// </summary>
    private static void ResolveFolder(ResolvedSource resolved, string folderPath) {

      string directSkillFile = Path.Combine(folderPath, SkillFile.SkillFileName);

      if (File.Exists(directSkillFile)) {
        AddSkillFromFile(resolved, directSkillFile, null);
        return;
      }

      string[] subDirectories = Directory.GetDirectories(folderPath).OrderBy((d) => d, StringComparer.OrdinalIgnoreCase).ToArray();

      foreach (string subDirectory in subDirectories) {

        string skillFile = Path.Combine(subDirectory, SkillFile.SkillFileName);

        if (File.Exists(skillFile)) {
          AddSkillFromFile(resolved, skillFile, null);
        }

      }

      if (resolved.SkillFiles.Count == 0) {
        MarkBroken(resolved, $"The folder '{folderPath}' does not contain any '{SkillFile.SkillFileName}'.");
      }

    }

    /// <summary>
    /// Reads name and description of a skill file (the body is read on demand later).
    /// If 'forcedName' is given, it is used as skill name (built-in sources).
    /// </summary>
    private static void AddSkillFromFile(ResolvedSource resolved, string fileFullName, string forcedName) {

      SkillFile skillFile;

      try {
        skillFile = SkillFile.Load(fileFullName);
      }
      catch (IOException ex) {
        MarkBroken(resolved, ex.Message);
        return;
      }
      catch (UnauthorizedAccessException ex) {
        MarkBroken(resolved, ex.Message);
        return;
      }

      string skillName = skillFile.Name;

      if (forcedName != null) {
        skillName = forcedName;
      }

      resolved.SkillFiles.Add(new KeyValuePair<string, string>(skillName, fileFullName));
      resolved.SkillDescriptions[skillName] = skillFile.Description;
      resolved.Info.SkillNames = resolved.SkillFiles.Select((f) => f.Key).ToArray();

    }

    private static void MarkBroken(ResolvedSource resolved, string reason) {

      resolved.Info.IsReferenceBroken = true;
      resolved.Info.LastError = reason;

    }

    /// <summary>
    /// Replaces all sources previously registered by this service within the toolbox.
    /// </summary>
    private void SynchronizeToolbox(HarnessToolbox toolbox, List<ResolvedSource> resolvedSources) {

      foreach (string sourceId in _RegisteredSourceIds) {
        toolbox.RemoveSource(sourceId);
      }

      _RegisteredSourceIds.Clear();

      foreach (ResolvedSource resolved in resolvedSources) {

        SkillSourceInfo info = resolved.Info;
        HarnessSource source = new HarnessSource(info.SourceId, false, info.DisplayName, info.Level, info.IsFixed);

        toolbox.AddSource(source);
        _RegisteredSourceIds.Add(info.SourceId);

        foreach (KeyValuePair<string, string> skillFile in resolved.SkillFiles) {

          string description;
          resolved.SkillDescriptions.TryGetValue(skillFile.Key, out description);

          string fileFullName = skillFile.Value;
          string fallbackFileFullName;
          resolved.FallbackFiles.TryGetValue(skillFile.Key, out fallbackFileFullName);

          HarnessSkill skill = new HarnessSkill(
            skillFile.Key, description, () => ReadSkillBody(fileFullName, fallbackFileFullName), false
          );

          toolbox.AddSkill(skill, info.SourceId);
        }

      }

    }

    /// <summary>
    /// Reads the current body of a referenced skill file. If it is not reachable, the offline copy (if any) is used,
    /// otherwise the body is empty (so the skill is omitted instead of breaking the chat).
    /// </summary>
    private static string ReadSkillBody(string fileFullName, string fallbackFileFullName) {

      string body;

      if (TryReadSkillBody(fileFullName, out body)) {
        return body;
      }

      if (fallbackFileFullName != null && TryReadSkillBody(fallbackFileFullName, out body)) {
        return body;
      }

      return string.Empty;
    }

    private static bool TryReadSkillBody(string fileFullName, out string body) {

      body = null;

      try {

        if (!File.Exists(fileFullName)) {
          return false;
        }

        body = SkillFile.Load(fileFullName).Body;

        return true;
      }
      catch (IOException) {
        return false;
      }
      catch (UnauthorizedAccessException) {
        return false;
      }

    }

    private static SkillSourceInfo CreateBuiltInInfo(string sourceId, string displayName, string fileFullName) {

      SkillSourceInfo info = new SkillSourceInfo();
      info.SourceId = sourceId;
      info.Kind = SkillSourceKinds.BuiltIn;
      info.DisplayName = displayName;
      info.Location = fileFullName;
      info.Level = SkillSourceLevels.InitializeAtStart;
      info.IsFixed = true;

      return info;
    }

    #endregion

    #region " Helpers "

    private static bool IsBuiltInSourceId(string sourceId) {

      bool isUserPreferences = string.Equals(sourceId, UserPreferencesSourceId, StringComparison.OrdinalIgnoreCase);
      bool isSystemPreferences = string.Equals(sourceId, SystemPreferencesSourceId, StringComparison.OrdinalIgnoreCase);

      return isUserPreferences || isSystemPreferences;
    }

    private static string NormalizeKind(string kind) {

      string[] knownKinds = new string[] {
        SkillSourceKinds.File, SkillSourceKinds.Folder, SkillSourceKinds.Url, SkillSourceKinds.GitHub, SkillSourceKinds.BuiltIn
      };

      foreach (string knownKind in knownKinds) {
        if (string.Equals(knownKind, kind, StringComparison.OrdinalIgnoreCase)) {
          return knownKind;
        }
      }

      return kind;
    }

    /// <summary>
    /// Returns the normalized skill source level or null if it is unknown.
    /// </summary>
    private static string NormalizeSkillLevel(string level) {

      int rank = HarnessToolbox.GetSkillLevelRank(level);

      if (rank == 0) {
        return SkillSourceLevels.Hidden;
      }
      else if (rank == 1) {
        return SkillSourceLevels.OnDemand;
      }
      else if (rank == 2) {
        return SkillSourceLevels.InitializeAtStart;
      }

      return null;
    }

    private static string DeriveDisplayName(string kind, string fullLocation) {

      if (kind == SkillSourceKinds.File) {
        return SkillFile.DeriveNameFromPath(fullLocation);
      }

      if (kind == SkillSourceKinds.GitHub) {

        GitHubLocation gitHubLocation = GitHubLocation.Parse(fullLocation);

        return DeriveNameFromUrlPath(gitHubLocation.SubPath, gitHubLocation.Repository);
      }

      if (kind == SkillSourceKinds.Url) {

        Uri uri = new Uri(fullLocation);

        return DeriveNameFromUrlPath(uri.AbsolutePath, uri.Host);
      }

      string folderName = Path.GetFileName(fullLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

      if (string.IsNullOrWhiteSpace(folderName)) {
        return fullLocation;
      }

      return folderName;
    }

    /// <summary>
    /// Creates a readable, unique source id like 'file-my-skill' or 'folder-skills-2'.
    /// </summary>
    private static string CreateSourceId(string kind, string fullLocation, PersistedExtensions persisted) {

      string slug = ToSlug(DeriveDisplayName(kind, fullLocation));

      string prefix = kind.ToLowerInvariant() + "-" + slug;
      string candidate = prefix;
      int counter = 2;

      while (IsBuiltInSourceId(candidate) || persisted.SkillSources.Any((s) => string.Equals(s.SourceId, candidate, StringComparison.OrdinalIgnoreCase))) {
        candidate = prefix + "-" + counter.ToString();
        counter++;
      }

      return candidate;
    }

    /// <summary>
    /// Converts a name into a lower case, file system safe slug (for example 'My Skill!' -> 'my-skill').
    /// </summary>
    private static string ToSlug(string value) {

      string lowerValue = string.Empty;

      if (value != null) {
        lowerValue = value.ToLowerInvariant();
      }

      char[] characters = lowerValue.ToCharArray();

      for (int i = 0; i < characters.Length; i++) {
        if (!char.IsLetterOrDigit(characters[i]) || characters[i] >= 128) {
          characters[i] = '-';
        }
      }

      string slug = new string(characters).Trim('-');

      if (slug.Length == 0) {
        slug = "source";
      }

      return slug;
    }

    #endregion

    #region " Persistence "

    private PersistedExtensions ReadPersisted() {

      PersistedExtensions persisted = null;

      if (File.Exists(_ExtensionsFileFullName)) {
        string json = File.ReadAllText(_ExtensionsFileFullName);
        persisted = JsonConvert.DeserializeObject<PersistedExtensions>(json);
      }

      if (persisted == null) {
        persisted = new PersistedExtensions();
      }

      if (persisted.SkillSources == null) {
        persisted.SkillSources = new List<PersistedSkillSource>();
      }

      persisted.SkillSources.RemoveAll((s) => s == null || string.IsNullOrWhiteSpace(s.SourceId));

      return persisted;
    }

    private void WritePersisted(PersistedExtensions persisted) {

      string directory = Path.GetDirectoryName(_ExtensionsFileFullName);

      if (!string.IsNullOrWhiteSpace(directory)) {
        Directory.CreateDirectory(directory);
      }

      string json = JsonConvert.SerializeObject(persisted, Formatting.Indented);

      File.WriteAllText(_ExtensionsFileFullName, json);

    }

    private sealed class ResolvedSource {

      private readonly SkillSourceInfo _Info;

      private readonly List<KeyValuePair<string, string>> _SkillFiles = new List<KeyValuePair<string, string>>();

      private readonly Dictionary<string, string> _SkillDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      private readonly Dictionary<string, string> _FallbackFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      public ResolvedSource(SkillSourceInfo info) {
        _Info = info;
      }

      public SkillSourceInfo Info {
        get {
          return _Info;
        }
      }

      /// <summary>
      /// Skill name -> file full name.
      /// </summary>
      public List<KeyValuePair<string, string>> SkillFiles {
        get {
          return _SkillFiles;
        }
      }

      public Dictionary<string, string> SkillDescriptions {
        get {
          return _SkillDescriptions;
        }
      }

      /// <summary>
      /// Skill name -> file full name of the offline copy (used if the original file is not reachable).
      /// </summary>
      public Dictionary<string, string> FallbackFiles {
        get {
          return _FallbackFiles;
        }
      }

    }

    internal sealed class PersistedExtensions {

      private string _SystemPreferencesLocation;
      private List<PersistedSkillSource> _SkillSources = new List<PersistedSkillSource>();

      public string SystemPreferencesLocation {
        get {
          return _SystemPreferencesLocation;
        }
        set {
          _SystemPreferencesLocation = value;
        }
      }

      public List<PersistedSkillSource> SkillSources {
        get {
          return _SkillSources;
        }
        set {
          _SkillSources = value;
        }
      }

    }

    internal sealed class PersistedSkillSource {

      private string _SourceId;
      private string _Kind;
      private string _DisplayName;
      private string _Location;
      private string _Level;
      private bool _SecureAvailability;
      private string _OfflineCopyTimestampUtc;
      private string _LastRemoteError;

      /// <summary>
      /// Reason of the last failed load of a remote source (null if the last load succeeded).
      /// </summary>
      public string LastRemoteError {
        get {
          return _LastRemoteError;
        }
        set {
          _LastRemoteError = value;
        }
      }

      public bool SecureAvailability {
        get {
          return _SecureAvailability;
        }
        set {
          _SecureAvailability = value;
        }
      }

      public string OfflineCopyTimestampUtc {
        get {
          return _OfflineCopyTimestampUtc;
        }
        set {
          _OfflineCopyTimestampUtc = value;
        }
      }

      public string SourceId {
        get {
          return _SourceId;
        }
        set {
          _SourceId = value;
        }
      }

      public string Kind {
        get {
          return _Kind;
        }
        set {
          _Kind = value;
        }
      }

      public string DisplayName {
        get {
          return _DisplayName;
        }
        set {
          _DisplayName = value;
        }
      }

      public string Location {
        get {
          return _Location;
        }
        set {
          _Location = value;
        }
      }

      public string Level {
        get {
          return _Level;
        }
        set {
          _Level = value;
        }
      }

    }

    #endregion

  }

}
