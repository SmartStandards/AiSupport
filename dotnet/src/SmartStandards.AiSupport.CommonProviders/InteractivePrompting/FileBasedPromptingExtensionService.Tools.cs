using AI.SmartStandards.InteractivePrompting.Harness;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Tool source part of the extension service: built-in tool providers (registered by the host), MCP servers and
  /// UJMW contract interfaces of uploaded assemblies. Non-secret metadata (level, settings object) is persisted within
  /// 'tools.json' beside the extensions file, tokens within the separate (protected) credentials file.
  /// </summary>
  public partial class FileBasedPromptingExtensionService {

    private const string _ToolCredentialPrefix = "tool:";

    private readonly List<IHarnessToolProvider> _BuiltInToolProviders = new List<IHarnessToolProvider>();

    private readonly Dictionary<string, ToolSourceState> _ToolSourceStates = new Dictionary<string, ToolSourceState>(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _RegisteredToolSourceIds = new List<string>();

    /// <summary>
    /// File which persists the tool sources (located beside the extensions file).
    /// </summary>
    public string ToolsFileFullName {
      get {
        return Path.Combine(this.GetExtensionsDirectory(), "tools.json");
      }
    }

    /// <summary>
    /// Directory which contains the uploaded tool assemblies (one sub directory per AssemblyId).
    /// </summary>
    public string ToolAssembliesDirectory {
      get {
        return Path.Combine(this.GetExtensionsDirectory(), "ToolAssemblies");
      }
    }

    /// <summary>
    /// Registers a built-in tool provider (for example the OOB tools). Its ProviderId is used as SourceId.
    /// </summary>
    public void RegisterBuiltInToolProvider(IHarnessToolProvider provider) {

      if (provider == null) {
        throw new ArgumentNullException(nameof(provider));
      }

      lock (_SyncRoot) {

        _BuiltInToolProviders.RemoveAll((p) => string.Equals(p.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase));
        _BuiltInToolProviders.Add(provider);

        if (_AttachedToolbox != null) {
          this.SynchronizeToolSources(_AttachedToolbox);
        }

      }

    }

    /// <summary>
    /// Registers the out-of-the-box tool providers (preferences, file system, internet, shell).
    /// </summary>
    public void RegisterOutOfTheBoxToolProviders() {

      this.RegisterBuiltInToolProvider(new PreferencesToolProvider(
        () => _UserPreferencesFileFullName, () => this.GetEffectiveSystemPreferencesLocation()
      ));

      this.RegisterBuiltInToolProvider(new FileSystemToolProvider());
      this.RegisterBuiltInToolProvider(new InternetToolProvider());
      this.RegisterBuiltInToolProvider(new ShellToolProvider());

    }

    /// <summary>
    /// Recreates the tools of all tool sources (for example to reconnect MCP servers).
    /// </summary>
    public void RefreshToolSources() {

      lock (_SyncRoot) {

        if (_AttachedToolbox != null) {
          this.SynchronizeToolSources(_AttachedToolbox);
        }

      }

    }

    #region " IPromptingExtensionService (tools) "

    /// <summary>
    /// Returns all tool sources (built-in tool providers first, then MCP servers and contract interfaces).
    /// </summary>
    public ToolSourceInfo[] GetToolSources() {

      lock (_SyncRoot) {

        PersistedTools persisted = this.ReadPersistedTools();
        Dictionary<string, string> credentials = this.ReadCredentials();
        List<ToolSourceInfo> result = new List<ToolSourceInfo>();

        foreach (IHarnessToolProvider provider in _BuiltInToolProviders) {

          PersistedToolSource source = GetOrCreateBuiltInEntry(persisted, provider);
          result.Add(this.CreateToolSourceInfo(source, provider, credentials));
        }

        foreach (PersistedToolSource source in persisted.ToolSources.Where((s) => s.Kind != ToolSourceKinds.BuiltIn)) {
          result.Add(this.CreateToolSourceInfo(source, this.CreateProvider(source), credentials));
        }

        return result.ToArray();
      }

    }

    /// <summary>
    /// Registers an MCP server (Streamable HTTP transport) as tool source. Returns the SourceId.
    /// </summary>
    public string AddMcpServer(string url, string token) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(AddMcpServer));

      Uri uri;

      if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) {
        throw new ArgumentException($"'{url}' is not a valid URL.", nameof(url));
      }

      if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) {
        throw new ArgumentException("Only http(s) URLs are supported for MCP servers.", nameof(url));
      }

      lock (_SyncRoot) {

        PersistedTools persisted = this.ReadPersistedTools();

        PersistedToolSource source = new PersistedToolSource();
        source.Kind = ToolSourceKinds.Mcp;
        source.Location = uri.ToString();
        source.DisplayName = "MCP " + uri.Host;
        source.SourceId = CreateToolSourceId("mcp-" + uri.Host, persisted);
        source.Level = ToolSourceLevels.RequiresConfirmation;
        source.SettingsJson = "{}";

        persisted.ToolSources.Add(source);
        this.WritePersistedTools(persisted);
        this.SetCredential(_ToolCredentialPrefix + source.SourceId, token);

        if (_AttachedToolbox != null) {
          this.SynchronizeToolSources(_AttachedToolbox);
        }

        return source.SourceId;
      }

    }

    /// <summary>
    /// Stores an uploaded assembly (base64) within a managed folder and returns its public interfaces.
    /// </summary>
    public ContractInterfaceInfo[] UploadToolAssembly(string fileName, string contentBase64) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(UploadToolAssembly));

      if (string.IsNullOrWhiteSpace(fileName)) {
        throw new ArgumentNullException(nameof(fileName));
      }

      if (string.IsNullOrWhiteSpace(contentBase64)) {
        throw new ArgumentNullException(nameof(contentBase64));
      }

      string safeFileName = Path.GetFileName(fileName.Trim());

      if (!safeFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
        throw new ArgumentException("Only .dll files are supported.", nameof(fileName));
      }

      byte[] content;

      try {
        content = Convert.FromBase64String(contentBase64);
      }
      catch (FormatException) {
        throw new ArgumentException("The content is not valid base64.", nameof(contentBase64));
      }

      string assemblyId = Guid.NewGuid().ToString("N");
      string directory = Path.Combine(this.ToolAssembliesDirectory, assemblyId);
      Directory.CreateDirectory(directory);

      string fileFullName = Path.Combine(directory, safeFileName);
      File.WriteAllBytes(fileFullName, content);

      return ContractInterfaceToolProvider.GetContractInterfaces(assemblyId, fileFullName);
    }

    /// <summary>
    /// TDL-1: references an assembly in place (not copied) and returns its public interfaces. The 'AssemblyId' of the
    /// returned interfaces is the full path of the assembly.
    /// </summary>
    public ContractInterfaceInfo[] AddToolAssemblyReference(string fullPath) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(AddToolAssemblyReference));

      string fileFullName = NormalizeToolAssemblyPath(fullPath);

      if (!File.Exists(fileFullName)) {
        throw new FileNotFoundException($"The file '{fileFullName}' does not exist or is not accessible.", fileFullName);
      }

      return ContractInterfaceToolProvider.GetContractInterfaces(fileFullName, fileFullName);
    }

    private static string NormalizeToolAssemblyPath(string fullPath) {

      if (string.IsNullOrWhiteSpace(fullPath)) {
        throw new ArgumentNullException(nameof(fullPath));
      }

      string trimmed = fullPath.Trim().Trim('"');

      if (!Path.IsPathRooted(trimmed)) {
        throw new ArgumentException($"'{fullPath}' is not an absolute (local or UNC) path.", nameof(fullPath));
      }

      if (!trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
        throw new ArgumentException("Only .dll files are supported.", nameof(fullPath));
      }

      return Path.GetFullPath(trimmed);
    }

    /// <summary>
    /// An assembly id is either the id of an uploaded copy (TDL-4) or the full path of a referenced assembly (TDL-1).
    /// </summary>
    private static bool IsAssemblyReference(string assemblyId) {

      if (string.IsNullOrWhiteSpace(assemblyId)) {
        return false;
      }

      return Path.IsPathRooted(assemblyId.Trim());
    }

    /// <summary>
    /// Registers a contract interface of an uploaded assembly as tool source. Returns the SourceId.
    /// </summary>
    public string RegisterContractInterface(string assemblyId, string interfaceName, string url, string token) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(RegisterContractInterface));

      if (string.IsNullOrWhiteSpace(interfaceName)) {
        throw new ArgumentNullException(nameof(interfaceName));
      }

      Uri uri;

      if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) {
        throw new ArgumentException($"'{url}' is not a valid URL.", nameof(url));
      }

      string assemblyFileFullName;

      if (IsAssemblyReference(assemblyId)) {

        assemblyFileFullName = NormalizeToolAssemblyPath(assemblyId);
        assemblyId = assemblyFileFullName;

        if (!File.Exists(assemblyFileFullName)) {
          throw new FileNotFoundException($"The file '{assemblyFileFullName}' does not exist or is not accessible.", assemblyFileFullName);
        }

      }
      else {
        assemblyFileFullName = this.GetUploadedAssemblyFileFullName(assemblyId);
      }

      bool interfaceExists = ContractInterfaceToolProvider.GetContractInterfaces(assemblyId, assemblyFileFullName).Any(
        (i) => string.Equals(i.InterfaceName, interfaceName, StringComparison.Ordinal)
      );

      if (!interfaceExists) {
        throw new ArgumentException($"The interface '{interfaceName}' was not found within the assembly.", nameof(interfaceName));
      }

      lock (_SyncRoot) {

        PersistedTools persisted = this.ReadPersistedTools();

        string shortName = interfaceName.Substring(interfaceName.LastIndexOf('.') + 1);

        PersistedToolSource source = new PersistedToolSource();
        source.Kind = ToolSourceKinds.ContractInterface;
        source.Location = uri.ToString();
        source.AssemblyId = assemblyId;
        source.InterfaceName = interfaceName;
        source.DisplayName = shortName + " (" + uri.Host + ")";
        source.SourceId = CreateToolSourceId("contract-" + shortName, persisted);
        source.Level = ToolSourceLevels.RequiresConfirmation;
        source.SettingsJson = "{}";

        persisted.ToolSources.Add(source);
        this.WritePersistedTools(persisted);
        this.SetCredential(_ToolCredentialPrefix + source.SourceId, token);

        if (_AttachedToolbox != null) {
          this.SynchronizeToolSources(_AttachedToolbox);
        }

        return source.SourceId;
      }

    }

    /// <summary>
    /// Removes a (non-fixed) tool source. Unknown ids are ignored.
    /// </summary>
    public void RemoveToolSource(string sourceId) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(RemoveToolSource));

      lock (_SyncRoot) {

        if (this.FindBuiltInToolProvider(sourceId) != null) {
          throw new InvalidOperationException($"The built-in tool source '{sourceId}' cannot be removed.");
        }

        PersistedTools persisted = this.ReadPersistedTools();

        int removedCount = persisted.ToolSources.RemoveAll(
          (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
        );

        if (removedCount > 0) {
          this.WritePersistedTools(persisted);
          this.SetCredential(_ToolCredentialPrefix + sourceId, null);
        }

        if (_AttachedToolbox != null) {
          this.SynchronizeToolSources(_AttachedToolbox);
        }

      }

    }

    /// <summary>
    /// Sets the global level (see 'ToolSourceLevels') of a tool source.
    /// </summary>
    public void SetToolSourceLevel(string sourceId, string level) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetToolSourceLevel));

      if (HarnessToolbox.GetToolLevelRank(level) < 0) {
        throw new ArgumentException($"Unknown tool source level '{level}'.", nameof(level));
      }

      this.UpdateToolSource(sourceId, (source) => {
        source.Level = NormalizeToolLevel(level);
      });

    }

    /// <summary>
    /// Sets (or with an empty value removes) the credential (token) of a tool source.
    /// </summary>
    public void SetToolSourceCredential(string sourceId, string credential) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetToolSourceCredential));

      lock (_SyncRoot) {

        IHarnessToolProvider provider = this.GetToolProviderOrThrow(sourceId);

        if (!provider.SupportsCredential) {
          throw new NotSupportedException($"The tool source '{sourceId}' does not support credentials.");
        }

        this.SetCredential(_ToolCredentialPrefix + sourceId, credential);

        if (_AttachedToolbox != null) {
          this.SynchronizeToolSources(_AttachedToolbox);
        }

      }

    }

    /// <summary>
    /// Changes the endpoint URL of a tool source which is bound to an URL ('Mcp' or 'ContractInterface').
    /// </summary>
    public void SetToolSourceLocation(string sourceId, string url) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetToolSourceLocation));

      Uri uri;

      if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) {
        throw new ArgumentException($"'{url}' is not a valid URL.", nameof(url));
      }

      if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) {
        throw new ArgumentException("Only http(s) URLs are supported.", nameof(url));
      }

      this.UpdateToolSource(sourceId, (source) => {

        if (source.Kind != ToolSourceKinds.Mcp && source.Kind != ToolSourceKinds.ContractInterface) {
          throw new NotSupportedException($"The tool source '{sourceId}' is not bound to an URL.");
        }

        source.Location = uri.ToString();

        // the display name is derived from the server name, so it has to follow the new URL
        if (source.Kind == ToolSourceKinds.Mcp) {
          source.DisplayName = "MCP " + uri.Host;
        }
        else {

          string interfaceName = string.Empty;

          if (source.InterfaceName != null) {
            interfaceName = source.InterfaceName;
          }

          string shortName = interfaceName.Substring(interfaceName.LastIndexOf('.') + 1);
          source.DisplayName = shortName + " (" + uri.Host + ")";
        }

      });

    }

    /// <summary>
    /// Sets the (optional) additional description of a tool source (provided to the model). Null or empty removes it.
    /// </summary>
    public void SetToolSourceAdditionalDescription(string sourceId, string additionalDescription) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SetToolSourceAdditionalDescription));

      string normalized = null;

      if (!string.IsNullOrWhiteSpace(additionalDescription)) {
        normalized = additionalDescription.Trim();
      }

      this.UpdateToolSource(sourceId, (source) => {
        source.AdditionalDescription = normalized;
      });

    }

    /// <summary>
    /// Returns the HTML page of the settings detail tab of a tool source or null.
    /// </summary>
    public string GetToolProviderSettingsUi(string sourceId) {

      lock (_SyncRoot) {
        return this.GetToolProviderOrThrow(sourceId).GetSettingsUiHtml();
      }

    }

    /// <summary>
    /// Returns the JSON settings object of a tool source.
    /// </summary>
    public string GetToolProviderSettings(string sourceId) {

      lock (_SyncRoot) {

        this.GetToolProviderOrThrow(sourceId);

        return this.GetToolSourceSettings(sourceId);
      }

    }

    /// <summary>
    /// Validates (by the provider) and saves the JSON settings object of a tool source.
    /// </summary>
    public void SaveToolProviderSettings(string sourceId, string settingsJson) {

      PromptingUserLevels.Demand(_UserLevelGetter, PromptingUserLevels.Admin, nameof(SaveToolProviderSettings));

      string validatedJson;

      lock (_SyncRoot) {
        validatedJson = this.GetToolProviderOrThrow(sourceId).ValidateSettingsJson(settingsJson);
      }

      this.UpdateToolSource(sourceId, (source) => {
        source.SettingsJson = validatedJson;
      });

    }

    #endregion

    #region " Tool sources (internals) "

    private string GetExtensionsDirectory() {

      string directory = Path.GetDirectoryName(_ExtensionsFileFullName);

      if (string.IsNullOrWhiteSpace(directory)) {
        directory = Directory.GetCurrentDirectory();
      }

      return directory;
    }

    private string GetEffectiveSystemPreferencesLocation() {

      string configured = this.GetSystemPreferencesLocation();

      if (string.IsNullOrWhiteSpace(configured)) {
        return _DefaultSystemPreferencesFileFullName;
      }

      return configured;
    }

    private string GetUploadedAssemblyFileFullName(string assemblyId) {

      if (string.IsNullOrWhiteSpace(assemblyId) || assemblyId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || assemblyId.Contains("..")) {
        throw new ArgumentException($"Invalid assembly id '{assemblyId}'.", nameof(assemblyId));
      }

      string directory = Path.Combine(this.ToolAssembliesDirectory, assemblyId);

      if (!Directory.Exists(directory)) {
        throw new ArgumentException($"Unknown assembly id '{assemblyId}'.", nameof(assemblyId));
      }

      string fileFullName = Directory.GetFiles(directory, "*.dll").FirstOrDefault();

      if (fileFullName == null) {
        throw new ArgumentException($"The assembly '{assemblyId}' contains no .dll file.", nameof(assemblyId));
      }

      return fileFullName;
    }

    private IHarnessToolProvider FindBuiltInToolProvider(string sourceId) {

      return _BuiltInToolProviders.FirstOrDefault(
        (p) => string.Equals(p.ProviderId, sourceId, StringComparison.OrdinalIgnoreCase)
      );
    }

    /// <summary>
    /// Must be called within the lock.
    /// </summary>
    private IHarnessToolProvider GetToolProviderOrThrow(string sourceId) {

      IHarnessToolProvider builtIn = this.FindBuiltInToolProvider(sourceId);

      if (builtIn != null) {
        return builtIn;
      }

      PersistedToolSource source = this.ReadPersistedTools().ToolSources.FirstOrDefault(
        (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
      );

      if (source == null) {
        throw new ArgumentException($"Unknown tool source '{sourceId}'.", nameof(sourceId));
      }

      return this.CreateProvider(source);
    }

    /// <summary>
    /// Creates the provider of a persisted (non built-in) source or null if the kind is unknown.
    /// </summary>
    private IHarnessToolProvider CreateProvider(PersistedToolSource source) {

      if (source.Kind == ToolSourceKinds.Mcp) {
        return new McpToolProvider(source.SourceId);
      }

      if (source.Kind == ToolSourceKinds.ContractInterface) {

        string assemblyFileFullName;

        if (IsAssemblyReference(source.AssemblyId)) {
          //TDL-3: a missing referenced file is reported as broken by the provider, the registration is kept
          assemblyFileFullName = source.AssemblyId.Trim();
        }
        else {

          try {
            assemblyFileFullName = this.GetUploadedAssemblyFileFullName(source.AssemblyId);
          }
          catch (ArgumentException) {
            assemblyFileFullName = Path.Combine(this.ToolAssembliesDirectory, "missing.dll");
          }

        }

        return new ContractInterfaceToolProvider(assemblyFileFullName, source.InterfaceName, source.SourceId);
      }

      return this.FindBuiltInToolProvider(source.SourceId);
    }

    private void UpdateToolSource(string sourceId, Action<PersistedToolSource> update) {

      lock (_SyncRoot) {

        PersistedTools persisted = this.ReadPersistedTools();
        IHarnessToolProvider builtIn = this.FindBuiltInToolProvider(sourceId);
        PersistedToolSource source;

        if (builtIn != null) {
          source = GetOrCreateBuiltInEntry(persisted, builtIn);
        }
        else {
          source = persisted.ToolSources.FirstOrDefault(
            (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
          );
        }

        if (source == null) {
          throw new ArgumentException($"Unknown tool source '{sourceId}'.", nameof(sourceId));
        }

        update.Invoke(source);
        this.WritePersistedTools(persisted);

        if (_AttachedToolbox != null) {
          this.SynchronizeToolSources(_AttachedToolbox);
        }

      }

    }

    /// <summary>
    /// Returns the persisted entry of a built-in provider (created with the defaults of the provider if missing).
    /// </summary>
    private static PersistedToolSource GetOrCreateBuiltInEntry(PersistedTools persisted, IHarnessToolProvider provider) {

      PersistedToolSource source = persisted.ToolSources.FirstOrDefault(
        (s) => string.Equals(s.SourceId, provider.ProviderId, StringComparison.OrdinalIgnoreCase)
      );

      if (source == null) {
        source = new PersistedToolSource();
        source.SourceId = provider.ProviderId;
        source.Kind = ToolSourceKinds.BuiltIn;
        source.Level = provider.DefaultLevel;
        source.SettingsJson = provider.GetDefaultSettingsJson();
        persisted.ToolSources.Add(source);
      }

      source.DisplayName = provider.DisplayName;

      return source;
    }

    private string GetToolSourceSettings(string sourceId) {

      lock (_SyncRoot) {

        PersistedTools persisted = this.ReadPersistedTools();
        IHarnessToolProvider builtIn = this.FindBuiltInToolProvider(sourceId);

        if (builtIn != null) {
          return GetOrCreateBuiltInEntry(persisted, builtIn).SettingsJson;
        }

        PersistedToolSource source = persisted.ToolSources.FirstOrDefault(
          (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
        );

        if (source == null || string.IsNullOrWhiteSpace(source.SettingsJson)) {
          return "{}";
        }

        return source.SettingsJson;
      }

    }

    private void SaveToolSourceSettingsFromTool(string sourceId, string settingsJson) {

      lock (_SyncRoot) {

        PersistedTools persisted = this.ReadPersistedTools();
        IHarnessToolProvider builtIn = this.FindBuiltInToolProvider(sourceId);
        PersistedToolSource source;

        if (builtIn != null) {
          source = GetOrCreateBuiltInEntry(persisted, builtIn);
        }
        else {
          source = persisted.ToolSources.FirstOrDefault(
            (s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
          );
        }

        if (source == null) {
          return;
        }

        //(the tools read the settings on every call, so no resynchronization is needed)
        source.SettingsJson = settingsJson;
        this.WritePersistedTools(persisted);
      }

    }

    private string GetToolSourceCredential(string sourceId) {

      lock (_SyncRoot) {
        return GetCredential(this.ReadCredentials(), _ToolCredentialPrefix + sourceId);
      }

    }

    /// <summary>
    /// Replaces all tool sources previously registered by this service within the toolbox. Sources whose tools
    /// cannot be created are registered without tools and flagged as broken. Must be called within the lock.
    /// </summary>
    private void SynchronizeToolSources(HarnessToolbox toolbox) {

      foreach (string sourceId in _RegisteredToolSourceIds) {
        toolbox.RemoveSource(sourceId);
      }

      _RegisteredToolSourceIds.Clear();
      _ToolSourceStates.Clear();

      PersistedTools persisted = this.ReadPersistedTools();
      List<KeyValuePair<PersistedToolSource, IHarnessToolProvider>> sources = new List<KeyValuePair<PersistedToolSource, IHarnessToolProvider>>();

      foreach (IHarnessToolProvider provider in _BuiltInToolProviders) {
        sources.Add(new KeyValuePair<PersistedToolSource, IHarnessToolProvider>(GetOrCreateBuiltInEntry(persisted, provider), provider));
      }

      foreach (PersistedToolSource source in persisted.ToolSources.Where((s) => s.Kind != ToolSourceKinds.BuiltIn)) {
        sources.Add(new KeyValuePair<PersistedToolSource, IHarnessToolProvider>(source, this.CreateProvider(source)));
      }

      foreach (KeyValuePair<PersistedToolSource, IHarnessToolProvider> entry in sources) {

        PersistedToolSource source = entry.Key;
        IHarnessToolProvider provider = entry.Value;
        ToolSourceState state = new ToolSourceState();
        _ToolSourceStates[source.SourceId] = state;

        toolbox.AddSource(new HarnessSource(source.SourceId, true, source.DisplayName, NormalizeToolLevel(source.Level), false, source.AdditionalDescription));
        _RegisteredToolSourceIds.Add(source.SourceId);

        if (provider == null) {
          state.BrokenReason = $"The tool source kind '{source.Kind}' is not supported.";
          continue;
        }

        string sourceId = source.SourceId;

        ToolProviderContext context = new ToolProviderContext(
          sourceId, source.Location,
          () => this.GetToolSourceSettings(sourceId),
          () => this.GetToolSourceCredential(sourceId),
          (json) => this.SaveToolSourceSettingsFromTool(sourceId, json)
        );

        try {

          IHarnessTool[] tools = provider.CreateTools(context);

          foreach (IHarnessTool tool in tools) {
            toolbox.AddTool(tool, sourceId);
          }

          state.ToolNames = tools.Select((t) => t.Name).ToArray();
        }
        catch (Exception ex) {
          Exception inner = ex;

          while (inner is AggregateException && inner.InnerException != null) {
            inner = inner.InnerException;
          }

          state.BrokenReason = inner.Message;
        }

      }

    }

    private ToolSourceInfo CreateToolSourceInfo(PersistedToolSource source, IHarnessToolProvider provider, Dictionary<string, string> credentials) {

      ToolSourceInfo info = new ToolSourceInfo();
      info.SourceId = source.SourceId;
      info.Kind = source.Kind;
      info.DisplayName = source.DisplayName;
      info.Location = source.Location;
      info.Level = NormalizeToolLevel(source.Level);
      info.AdditionalDescription = source.AdditionalDescription;
      info.IsFixed = false;
      info.HasCredentials = (GetCredential(credentials, _ToolCredentialPrefix + source.SourceId) != null);

      if (provider != null) {
        info.ProviderId = provider.ProviderId;
        info.DefaultLevel = provider.DefaultLevel;
        info.SupportsCredential = provider.SupportsCredential;
        info.HasSettingsUi = (provider.GetSettingsUiHtml() != null);
        info.HasSettings = true;
      }

      ToolSourceState state;

      if (_ToolSourceStates.TryGetValue(source.SourceId, out state)) {
        info.ToolNames = state.ToolNames;
        info.IsReferenceBroken = (state.BrokenReason != null);
        info.BrokenReason = state.BrokenReason;
      }

      return info;
    }

    private static string NormalizeToolLevel(string level) {

      int rank = HarnessToolbox.GetToolLevelRank(level);

      if (rank == 0) {
        return ToolSourceLevels.Hidden;
      }
      else if (rank == 2) {
        return ToolSourceLevels.NoConfirmation;
      }

      return ToolSourceLevels.RequiresConfirmation;
    }

    private static string CreateToolSourceId(string baseName, PersistedTools persisted) {

      string prefix = ToSlug(baseName);
      string candidate = prefix;
      int counter = 2;

      while (persisted.ToolSources.Any((s) => string.Equals(s.SourceId, candidate, StringComparison.OrdinalIgnoreCase))) {
        candidate = prefix + "-" + counter.ToString();
        counter++;
      }

      return candidate;
    }

    private PersistedTools ReadPersistedTools() {

      PersistedTools persisted = null;

      if (File.Exists(this.ToolsFileFullName)) {
        persisted = JsonConvert.DeserializeObject<PersistedTools>(File.ReadAllText(this.ToolsFileFullName));
      }

      if (persisted == null) {
        persisted = new PersistedTools();
      }

      if (persisted.ToolSources == null) {
        persisted.ToolSources = new List<PersistedToolSource>();
      }

      persisted.ToolSources.RemoveAll((s) => s == null || string.IsNullOrWhiteSpace(s.SourceId));

      return persisted;
    }

    private void WritePersistedTools(PersistedTools persisted) {

      Directory.CreateDirectory(this.GetExtensionsDirectory());
      File.WriteAllText(this.ToolsFileFullName, JsonConvert.SerializeObject(persisted, Formatting.Indented));

    }

    private sealed class ToolSourceState {

      private string[] _ToolNames = new string[0];

      private string _BrokenReason;

      public string[] ToolNames {
        get {
          return _ToolNames;
        }
        set {
          _ToolNames = value;
        }
      }

      public string BrokenReason {
        get {
          return _BrokenReason;
        }
        set {
          _BrokenReason = value;
        }
      }

    }

    internal sealed class PersistedTools {

      private List<PersistedToolSource> _ToolSources = new List<PersistedToolSource>();

      public List<PersistedToolSource> ToolSources {
        get {
          return _ToolSources;
        }
        set {
          _ToolSources = value;
        }
      }

    }

    internal sealed class PersistedToolSource {

      private string _SourceId;
      private string _Kind;
      private string _DisplayName;
      private string _Location;
      private string _Level;
      private string _SettingsJson;
      private string _AssemblyId;
      private string _InterfaceName;
      private string _AdditionalDescription;

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

      public string SettingsJson {
        get {
          return _SettingsJson;
        }
        set {
          _SettingsJson = value;
        }
      }

      public string AssemblyId {
        get {
          return _AssemblyId;
        }
        set {
          _AssemblyId = value;
        }
      }

      public string InterfaceName {
        get {
          return _InterfaceName;
        }
        set {
          _InterfaceName = value;
        }
      }

      /// <summary>
      /// User-maintained description for the model (purpose / infrastructure of this source instance).
      /// </summary>
      public string AdditionalDescription {
        get {
          return _AdditionalDescription;
        }
        set {
          _AdditionalDescription = value;
        }
      }

    }

    #endregion

  }

}
