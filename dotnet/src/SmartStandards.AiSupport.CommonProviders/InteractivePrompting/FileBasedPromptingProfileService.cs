using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Persists the harness profiles (PRF-1..12) within 'profiles.json' (default: '%APPDATA%\SmartStandards\AiSupport').
  /// The profile 'default' uses the root directory itself (= the location of installations without profiles,
  /// so existing configurations become the 'Default' profile without any migration, PRF-9). All other profiles use
  /// 'profiles\&lt;profileId&gt;\'. A profile directory contains 'settings.json', 'extensions.json',
  /// 'extensions.credentials.json' (protected), 'tools.json', the uploaded tool assemblies and the chats.
  /// This class only manages the persisted state; applying a profile to a running client is done by the
  /// 'HarnessProfileManager'.
  /// </summary>
  public class FileBasedPromptingProfileService : IPromptingProfileService {

    /// <summary>
    /// Id of the default profile (its files are located within the root directory).
    /// </summary>
    public const string DefaultProfileId = "default";

    /// <summary>
    /// Display name of the default profile (as long as it was not renamed).
    /// </summary>
    public const string DefaultProfileDisplayName = "Default";

    private const string _ProfilesFileName = "profiles.json";

    private const string _ProfilesDirectoryName = "profiles";

    /// <summary>
    /// Files which are copied into a new profile (the credentials file stays protected, SEC-5).
    /// </summary>
    private static readonly string[] _ProfileFileNames = new string[] {
      "settings.json", "extensions.json", "extensions.credentials.json", "tools.json"
    };

    /// <summary>
    /// Directories which are copied into a new profile (uploaded tool assemblies are referenced by their id).
    /// </summary>
    private static readonly string[] _ProfileDirectoryNames = new string[] { "ToolAssemblies" };

    private readonly string _RootDirectory;

    private readonly object _SyncRoot = new object();

    private Func<int> _UserLevelGetter;

    /// <summary>
    /// Creates the service using the default root directory ('%APPDATA%\SmartStandards\AiSupport').
    /// </summary>
    public FileBasedPromptingProfileService() : this(GetDefaultRootDirectory()) {
    }

    /// <summary>
    /// Creates the service using the given root directory (= the directory of the default profile).
    /// </summary>
    public FileBasedPromptingProfileService(string rootDirectory) {

      if (string.IsNullOrWhiteSpace(rootDirectory)) {
        throw new ArgumentNullException(nameof(rootDirectory));
      }

      _RootDirectory = rootDirectory;

    }

    /// <summary>
    /// Returns '%APPDATA%\SmartStandards\AiSupport'.
    /// </summary>
    public static string GetDefaultRootDirectory() {

      string roamingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

      return Path.Combine(roamingDirectory, "SmartStandards", "AiSupport");
    }

    public string RootDirectory {
      get {
        return _RootDirectory;
      }
    }

    public string ProfilesFileFullName {
      get {
        return Path.Combine(_RootDirectory, _ProfilesFileName);
      }
    }

    /// <summary>
    /// Optional resolver of the level of the current user (see 'PromptingUserLevels'). All modifying operations
    /// require the level 'Admin'. Without resolver, the current user is 'Admin' (no restriction).
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
    /// Returns the directory which contains the files of the given profile.
    /// </summary>
    public string GetProfileDirectory(string profileId) {

      if (IsDefaultProfileId(profileId)) {
        return _RootDirectory;
      }

      ValidateProfileIdFormat(profileId);

      return Path.Combine(_RootDirectory, _ProfilesDirectoryName, profileId);
    }

    /// <summary>
    /// Finds a profile by its id or (case-insensitive) display name, for example for the command-line argument
    /// '--profile' (PRF-8). Returns null if there is no such profile.
    /// </summary>
    public HarnessProfileInfo FindProfile(string profileIdOrName) {

      if (string.IsNullOrWhiteSpace(profileIdOrName)) {
        return null;
      }

      string key = profileIdOrName.Trim();
      HarnessProfileInfo[] profiles = this.GetProfiles();

      HarnessProfileInfo byId = profiles.FirstOrDefault(
        (p) => string.Equals(p.ProfileId, key, StringComparison.OrdinalIgnoreCase)
      );

      if (byId != null) {
        return byId;
      }

      return profiles.FirstOrDefault(
        (p) => string.Equals(p.DisplayName, key, StringComparison.OrdinalIgnoreCase)
      );
    }

    #region " IPromptingProfileService "

    /// <summary>
    /// Returns all profiles (the default profile first, then by creation time).
    /// </summary>
    public HarnessProfileInfo[] GetProfiles() {

      lock (_SyncRoot) {

        PersistedProfiles persisted = this.ReadProfiles();
        List<HarnessProfileInfo> result = new List<HarnessProfileInfo>();

        foreach (PersistedProfile profile in persisted.Profiles) {

          HarnessProfileInfo info = new HarnessProfileInfo();
          info.ProfileId = profile.ProfileId;
          info.DisplayName = profile.DisplayName;
          info.CreatedUtc = profile.CreatedUtc;
          info.IsDefault = IsDefaultProfileId(profile.ProfileId);
          info.IsActive = string.Equals(profile.ProfileId, persisted.ActiveProfileId, StringComparison.OrdinalIgnoreCase);
          result.Add(info);
        }

        return result.ToArray();
      }

    }

    /// <summary>
    /// Returns the id of the remembered active profile.
    /// </summary>
    public string GetActiveProfileId() {

      lock (_SyncRoot) {
        return this.ReadProfiles().ActiveProfileId;
      }

    }

    /// <summary>
    /// Creates a new profile as copy of the given profile (empty = the active profile), including the protected
    /// API keys and source tokens (PRF-3). The chats are not copied.
    /// </summary>
    public string CreateProfile(string copyFromProfileId, string displayName) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(CreateProfile));

      string normalizedName = NormalizeDisplayName(displayName);

      lock (_SyncRoot) {

        PersistedProfiles persisted = this.ReadProfiles();

        if (string.IsNullOrWhiteSpace(copyFromProfileId)) {
          copyFromProfileId = persisted.ActiveProfileId;
        }

        PersistedProfile source = GetProfileOrThrow(persisted, copyFromProfileId);
        EnsureUniqueDisplayName(persisted, normalizedName, null);

        PersistedProfile profile = new PersistedProfile();
        profile.ProfileId = CreateProfileId(persisted);
        profile.DisplayName = normalizedName;
        profile.CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        this.CopyProfileFiles(this.GetProfileDirectory(source.ProfileId), this.GetProfileDirectory(profile.ProfileId));

        persisted.Profiles.Add(profile);
        this.WriteProfiles(persisted);

        return profile.ProfileId;
      }

    }

    /// <summary>
    /// Renames the given profile (the display name must be unique).
    /// </summary>
    public void RenameProfile(string profileId, string displayName) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(RenameProfile));

      string normalizedName = NormalizeDisplayName(displayName);

      lock (_SyncRoot) {

        PersistedProfiles persisted = this.ReadProfiles();
        PersistedProfile profile = GetProfileOrThrow(persisted, profileId);

        EnsureUniqueDisplayName(persisted, normalizedName, profile.ProfileId);

        profile.DisplayName = normalizedName;
        this.WriteProfiles(persisted);
      }

    }

    /// <summary>
    /// Deletes the given profile incl. its files and chats. The active profile and the default profile
    /// cannot be deleted (PRF-5).
    /// </summary>
    public void DeleteProfile(string profileId) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(DeleteProfile));

      lock (_SyncRoot) {

        PersistedProfiles persisted = this.ReadProfiles();
        PersistedProfile profile = GetProfileOrThrow(persisted, profileId);

        if (IsDefaultProfileId(profile.ProfileId)) {
          throw new InvalidOperationException("The default profile cannot be deleted.");
        }

        if (string.Equals(profile.ProfileId, persisted.ActiveProfileId, StringComparison.OrdinalIgnoreCase)) {
          throw new InvalidOperationException("The active profile cannot be deleted. Please switch to another profile first.");
        }

        persisted.Profiles.Remove(profile);
        this.WriteProfiles(persisted);

        string directory = this.GetProfileDirectory(profile.ProfileId);

        if (Directory.Exists(directory)) {
          Directory.Delete(directory, true);
        }

      }

    }

    /// <summary>
    /// Remembers the given profile as active profile (PRF-7). Applying it to a running client is done by the
    /// 'HarnessProfileManager'.
    /// </summary>
    public void ActivateProfile(string profileId) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(ActivateProfile));

      lock (_SyncRoot) {

        PersistedProfiles persisted = this.ReadProfiles();
        PersistedProfile profile = GetProfileOrThrow(persisted, profileId);

        persisted.ActiveProfileId = profile.ProfileId;
        this.WriteProfiles(persisted);
      }

    }

    /// <summary>
    /// Returns true if the client should ask for the profile at start (PRF-6).
    /// </summary>
    public bool GetAskAtStartup() {

      lock (_SyncRoot) {
        return this.ReadProfiles().AskAtStartup;
      }

    }

    /// <summary>
    /// Enables or disables the profile selection at start (PRF-6).
    /// </summary>
    public void SetAskAtStartup(bool enabled) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetAskAtStartup));

      lock (_SyncRoot) {

        PersistedProfiles persisted = this.ReadProfiles();
        persisted.AskAtStartup = enabled;
        this.WriteProfiles(persisted);
      }

    }

    #endregion

    #region " Internals "

    /// <summary>
    /// True for the id of the default profile (null/empty is treated as default profile as well).
    /// </summary>
    public static bool IsDefaultProfileId(string profileId) {

      if (string.IsNullOrWhiteSpace(profileId)) {
        return true;
      }

      return string.Equals(profileId.Trim(), DefaultProfileId, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateProfileIdFormat(string profileId) {

      bool invalid = (
        profileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        profileId.Contains("..") ||
        profileId.Contains("/") ||
        profileId.Contains("\\")
      );

      if (invalid) {
        throw new ArgumentException($"Invalid profile id '{profileId}'.", nameof(profileId));
      }

    }

    private static string NormalizeDisplayName(string displayName) {

      if (string.IsNullOrWhiteSpace(displayName)) {
        throw new ArgumentException("The name of the profile must not be empty.", nameof(displayName));
      }

      return displayName.Trim();
    }

    private static PersistedProfile GetProfileOrThrow(PersistedProfiles persisted, string profileId) {

      string key = DefaultProfileId;

      if (!string.IsNullOrWhiteSpace(profileId)) {
        key = profileId.Trim();
      }

      PersistedProfile profile = persisted.Profiles.FirstOrDefault(
        (p) => string.Equals(p.ProfileId, key, StringComparison.OrdinalIgnoreCase)
      );

      if (profile == null) {
        throw new ArgumentException($"Unknown profile '{profileId}'.", nameof(profileId));
      }

      return profile;
    }

    private static void EnsureUniqueDisplayName(PersistedProfiles persisted, string displayName, string exceptProfileId) {

      bool exists = persisted.Profiles.Any(
        (p) => (
          string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase) &&
          !string.Equals(p.ProfileId, exceptProfileId, StringComparison.OrdinalIgnoreCase)
        )
      );

      if (exists) {
        throw new ArgumentException($"A profile with the name '{displayName}' already exists.", nameof(displayName));
      }

    }

    private static string CreateProfileId(PersistedProfiles persisted) {

      while (true) {

        string candidate = "p-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        bool exists = persisted.Profiles.Any(
          (p) => string.Equals(p.ProfileId, candidate, StringComparison.OrdinalIgnoreCase)
        );

        if (!exists) {
          return candidate;
        }

      }

    }

    private void CopyProfileFiles(string sourceDirectory, string targetDirectory) {

      Directory.CreateDirectory(targetDirectory);

      foreach (string fileName in _ProfileFileNames) {

        string sourceFile = Path.Combine(sourceDirectory, fileName);

        if (File.Exists(sourceFile)) {
          File.Copy(sourceFile, Path.Combine(targetDirectory, fileName), true);
        }

      }

      foreach (string directoryName in _ProfileDirectoryNames) {

        string sourceSubDirectory = Path.Combine(sourceDirectory, directoryName);

        if (Directory.Exists(sourceSubDirectory)) {
          CopyDirectory(sourceSubDirectory, Path.Combine(targetDirectory, directoryName));
        }

      }

    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory) {

      Directory.CreateDirectory(targetDirectory);

      foreach (string file in Directory.GetFiles(sourceDirectory)) {
        File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), true);
      }

      foreach (string subDirectory in Directory.GetDirectories(sourceDirectory)) {
        CopyDirectory(subDirectory, Path.Combine(targetDirectory, Path.GetFileName(subDirectory)));
      }

    }

    /// <summary>
    /// Reads 'profiles.json'. A missing or incomplete file results in (at least) the default profile.
    /// Must be called within the lock.
    /// </summary>
    private PersistedProfiles ReadProfiles() {

      PersistedProfiles persisted = null;

      if (File.Exists(this.ProfilesFileFullName)) {
        persisted = JsonConvert.DeserializeObject<PersistedProfiles>(File.ReadAllText(this.ProfilesFileFullName));
      }

      if (persisted == null) {
        persisted = new PersistedProfiles();
      }

      if (persisted.Profiles == null) {
        persisted.Profiles = new List<PersistedProfile>();
      }

      persisted.Profiles.RemoveAll((p) => p == null || string.IsNullOrWhiteSpace(p.ProfileId));

      PersistedProfile defaultProfile = persisted.Profiles.FirstOrDefault((p) => IsDefaultProfileId(p.ProfileId));

      if (defaultProfile == null) {
        defaultProfile = new PersistedProfile();
        defaultProfile.ProfileId = DefaultProfileId;
        defaultProfile.DisplayName = DefaultProfileDisplayName;
        defaultProfile.CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        persisted.Profiles.Insert(0, defaultProfile);
      }
      else {
        defaultProfile.ProfileId = DefaultProfileId;
      }

      if (string.IsNullOrWhiteSpace(defaultProfile.DisplayName)) {
        defaultProfile.DisplayName = DefaultProfileDisplayName;
      }

      bool activeExists = persisted.Profiles.Any(
        (p) => string.Equals(p.ProfileId, persisted.ActiveProfileId, StringComparison.OrdinalIgnoreCase)
      );

      if (!activeExists) {
        persisted.ActiveProfileId = DefaultProfileId;
      }

      return persisted;
    }

    /// <summary>
    /// Must be called within the lock.
    /// </summary>
    private void WriteProfiles(PersistedProfiles persisted) {

      Directory.CreateDirectory(_RootDirectory);

      File.WriteAllText(this.ProfilesFileFullName, JsonConvert.SerializeObject(persisted, Formatting.Indented));

    }

    internal sealed class PersistedProfiles {

      private string _ActiveProfileId = DefaultProfileId;
      private bool _AskAtStartup;
      private List<PersistedProfile> _Profiles = new List<PersistedProfile>();

      public string ActiveProfileId {
        get {
          return _ActiveProfileId;
        }
        set {
          _ActiveProfileId = value;
        }
      }

      public bool AskAtStartup {
        get {
          return _AskAtStartup;
        }
        set {
          _AskAtStartup = value;
        }
      }

      public List<PersistedProfile> Profiles {
        get {
          return _Profiles;
        }
        set {
          _Profiles = value;
        }
      }

    }

    internal sealed class PersistedProfile {

      private string _ProfileId;
      private string _DisplayName;
      private string _CreatedUtc;

      public string ProfileId {
        get {
          return _ProfileId;
        }
        set {
          _ProfileId = value;
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

      public string CreatedUtc {
        get {
          return _CreatedUtc;
        }
        set {
          _CreatedUtc = value;
        }
      }

    }

    #endregion

  }

}
