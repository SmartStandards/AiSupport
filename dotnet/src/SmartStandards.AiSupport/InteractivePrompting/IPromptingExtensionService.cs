using System;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Service which manages the global extension sources of the prompting UI (skill sources, later also tool sources).
  /// Credentials (PATs/tokens) are never returned to the caller.
  /// </summary>
#if NET_FX
  [ServiceContract()]
#endif
  public interface IPromptingExtensionService {

    /// <summary>
    /// Returns all skill sources (built-in sources first). Broken references are flagged (never removed).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    SkillSourceInfo[] GetSkillSources();

    /// <summary>
    /// Adds a reference to a single skill file (local or UNC path). The file is never copied. Returns the SourceId
    /// (an already registered reference to the same file returns its existing SourceId).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string AddSkillFileReference(string filePath);

    /// <summary>
    /// Adds a skill source of the given kind (see 'SkillSourceKinds'). Returns the SourceId.
    /// For 'Url' and 'GitHub' an optional credential (PAT/token) can be given, which is stored separately
    /// and never returned.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string AddSkillSource(string kind, string location, string credential);

    /// <summary>
    /// Removes a (non-fixed) skill source. Unknown ids are ignored.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void RemoveSkillSource(string sourceId);

    /// <summary>
    /// Sets the global default level (see 'SkillSourceLevels') of a (non-fixed) skill source.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetSkillSourceLevel(string sourceId, string level);

    /// <summary>
    /// Sets the location (local or UNC path to a 'SKILL.md' or to its folder) of the built-in source
    /// 'global-system-preferences'. An empty value restores the default location.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetSystemPreferencesLocation(string location);

    /// <summary>
    /// Returns the configured location of 'global-system-preferences' (empty string = default location).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string GetSystemPreferencesLocation();

    /// <summary>
    /// Enables or disables 'secure availability' of a skill source: a local offline copy of its skill files is kept,
    /// which is refreshed once on every new chat start and used as fallback while the original location is not reachable.
    /// Enabling creates the offline copy immediately (if the original location is reachable). Disabling deletes it.
    /// Only allowed for sources with 'SupportsSecureAvailability'.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetSkillSourceSecureAvailability(string sourceId, bool enabled);

    /// <summary>
    /// Sets (or with an empty value removes) the credential (PAT/token) of a remote skill source ('Url' or 'GitHub').
    /// The content is reloaded with the next access.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetSkillSourceCredential(string sourceId, string credential);

    /// <summary>
    /// Sets the freely selectable label of a skill source (for example for a folder source).
    /// An empty value restores the derived default label. Not allowed for built-in sources.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetSkillSourceDisplayName(string sourceId, string displayName);

    /// <summary>
    /// Lists the entries of a (server-side) directory to support a browse dialog for 'File' and 'Folder' skill sources.
    /// An empty path returns the root entries (drives / file system root). Only directories and (if 'includeFiles')
    /// '.md' files are returned. Inaccessible entries are skipped.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    FileSystemEntryInfo[] BrowseFileSystem(string path, bool includeFiles);

    /// <summary>
    /// Returns all tool sources (built-in tool providers first, then MCP servers and contract interfaces).
    /// Sources whose tools could not be created are flagged (never removed).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ToolSourceInfo[] GetToolSources();

    /// <summary>
    /// Registers an MCP server (Streamable HTTP transport) as tool source. The optional token is sent as
    /// bearer token, stored separately and never returned. Returns the SourceId.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string AddMcpServer(string url, string token);

    /// <summary>
    /// Uploads a .NET assembly (base64 encoded content) which contains UJMW contract interfaces and returns
    /// the contained public interfaces (see 'RegisterContractInterface').
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ContractInterfaceInfo[] UploadToolAssembly(string fileName, string contentBase64);

    /// <summary>
    /// Registers a contract interface of an uploaded assembly as tool source: every method becomes a tool,
    /// which is called through the UJMW dynamic client against the given URL. The optional token is sent as
    /// bearer token (or as given, if it already contains a scheme like 'Basic ...'). Returns the SourceId.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string RegisterContractInterface(string assemblyId, string interfaceName, string url, string token);

    /// <summary>
    /// Removes a (non-fixed) tool source. Unknown ids are ignored.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void RemoveToolSource(string sourceId);

    /// <summary>
    /// Sets the global level (see 'ToolSourceLevels') of a tool source.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetToolSourceLevel(string sourceId, string level);

    /// <summary>
    /// Sets (or with an empty value removes) the credential (token) of a tool source.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetToolSourceCredential(string sourceId, string credential);

    /// <summary>
    /// Changes the endpoint URL of a tool source which is bound to an URL ('Mcp' or 'ContractInterface').
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetToolSourceLocation(string sourceId, string url);

    /// <summary>
    /// Sets the (optional) additional description of a tool source, which is provided to the model to clarify
    /// the purpose of the source (or - if there are several instances - which infrastructure it points to).
    /// Null or empty removes the description.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetToolSourceAdditionalDescription(string sourceId, string additionalDescription);

    /// <summary>
    /// Returns the self-contained HTML page of the settings detail tab of a tool source or null
    /// (the UI shows a plain JSON editor for the settings object then).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string GetToolProviderSettingsUi(string sourceId);

    /// <summary>
    /// Returns the (one) JSON settings object of a tool source. Secrets are never part of it.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string GetToolProviderSettings(string sourceId);

    /// <summary>
    /// Validates (by the provider) and saves the JSON settings object of a tool source.
    /// Invalid objects are rejected with a message.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SaveToolProviderSettings(string sourceId, string settingsJson);

  }

  /// <summary>
  /// An entry of a directory listing (see 'BrowseFileSystem').
  /// </summary>
  public class FileSystemEntryInfo {

    private string _Name;
    private string _FullName;
    private bool _IsDirectory;

    public string Name {
      get {
        return _Name;
      }
      set {
        _Name = value;
      }
    }

    public string FullName {
      get {
        return _FullName;
      }
      set {
        _FullName = value;
      }
    }

    public bool IsDirectory {
      get {
        return _IsDirectory;
      }
      set {
        _IsDirectory = value;
      }
    }

  }

  /// <summary>
  /// Well-known kinds of tool sources.
  /// </summary>
  public static class ToolSourceKinds {

    /// <summary>
    /// A tool provider registered by the host (for example the out-of-the-box tools). Not removable.
    /// </summary>
    public const string BuiltIn = "BuiltIn";

    /// <summary>
    /// An MCP server (Streamable HTTP transport).
    /// </summary>
    public const string Mcp = "Mcp";

    /// <summary>
    /// A UJMW contract interface of an uploaded assembly, called through the UJMW dynamic client.
    /// </summary>
    public const string ContractInterface = "ContractInterface";

  }

  /// <summary>
  /// Describes a tool source.
  /// </summary>
  public class ToolSourceInfo {

    private string _SourceId;
    private string _Kind;
    private string _ProviderId;
    private string _DisplayName;
    private string _Location;
    private string _Level;
    private string _DefaultLevel;
    private bool _IsFixed;
    private bool _SupportsCredential;
    private bool _HasCredentials;
    private bool _HasSettings;
    private bool _HasSettingsUi;
    private string[] _ToolNames = new string[0];
    private bool _IsReferenceBroken;
    private string _BrokenReason;
    private string _AdditionalDescription;

    public string SourceId {
      get {
        return _SourceId;
      }
      set {
        _SourceId = value;
      }
    }

    /// <summary>
    /// See 'ToolSourceKinds'.
    /// </summary>
    public string Kind {
      get {
        return _Kind;
      }
      set {
        _Kind = value;
      }
    }

    /// <summary>
    /// Id of the tool provider which creates the tools of this source.
    /// </summary>
    public string ProviderId {
      get {
        return _ProviderId;
      }
      set {
        _ProviderId = value;
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

    /// <summary>
    /// URL of an MCP server or of a contract interface (empty for built-in providers).
    /// </summary>
    public string Location {
      get {
        return _Location;
      }
      set {
        _Location = value;
      }
    }

    /// <summary>
    /// The global level (see 'ToolSourceLevels').
    /// </summary>
    public string Level {
      get {
        return _Level;
      }
      set {
        _Level = value;
      }
    }

    /// <summary>
    /// The default level of the provider (used if the user did not set one).
    /// </summary>
    public string DefaultLevel {
      get {
        return _DefaultLevel;
      }
      set {
        _DefaultLevel = value;
      }
    }

    /// <summary>
    /// True if the source cannot be removed (built-in providers).
    /// </summary>
    public bool IsFixed {
      get {
        return _IsFixed;
      }
      set {
        _IsFixed = value;
      }
    }

    /// <summary>
    /// True if a credential (token) can be deposited for this source.
    /// </summary>
    public bool SupportsCredential {
      get {
        return _SupportsCredential;
      }
      set {
        _SupportsCredential = value;
      }
    }

    /// <summary>
    /// True if a credential is deposited (the credential itself is never returned).
    /// </summary>
    public bool HasCredentials {
      get {
        return _HasCredentials;
      }
      set {
        _HasCredentials = value;
      }
    }

    /// <summary>
    /// True if the provider has a configurable settings object (see 'GetToolProviderSettings').
    /// </summary>
    public bool HasSettings {
      get {
        return _HasSettings;
      }
      set {
        _HasSettings = value;
      }
    }

    /// <summary>
    /// True if the provider delivers an own settings page (see 'GetToolProviderSettingsUi').
    /// </summary>
    public bool HasSettingsUi {
      get {
        return _HasSettingsUi;
      }
      set {
        _HasSettingsUi = value;
      }
    }

    /// <summary>
    /// Names of the tools of this source.
    /// </summary>
    public string[] ToolNames {
      get {
        return _ToolNames;
      }
      set {
        _ToolNames = value;
      }
    }

    /// <summary>
    /// True if the tools of this source could not be created (for example an unreachable MCP server).
    /// </summary>
    public bool IsReferenceBroken {
      get {
        return _IsReferenceBroken;
      }
      set {
        _IsReferenceBroken = value;
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

    /// <summary>
    /// Optional additional description which is provided to the model (purpose / infrastructure of this source
    /// instance). Null if not maintained.
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

  /// <summary>
  /// A public interface of an uploaded tool assembly (see 'UploadToolAssembly').
  /// </summary>
  public class ContractInterfaceInfo {

    private string _AssemblyId;
    private string _InterfaceName;
    private string[] _MethodNames = new string[0];

    /// <summary>
    /// Id of the uploaded assembly (to be passed to 'RegisterContractInterface').
    /// </summary>
    public string AssemblyId {
      get {
        return _AssemblyId;
      }
      set {
        _AssemblyId = value;
      }
    }

    /// <summary>
    /// Full name of the interface.
    /// </summary>
    public string InterfaceName {
      get {
        return _InterfaceName;
      }
      set {
        _InterfaceName = value;
      }
    }

    public string[] MethodNames {
      get {
        return _MethodNames;
      }
      set {
        _MethodNames = value;
      }
    }

  }

  /// <summary>
  /// Well-known kinds of skill sources.
  /// </summary>
  public static class SkillSourceKinds {

    /// <summary>
    /// A single skill file (pure reference, never copied).
    /// </summary>
    public const string File = "File";

    /// <summary>
    /// A folder containing skills ('&lt;skill&gt;/SKILL.md') or a single 'SKILL.md'.
    /// </summary>
    public const string Folder = "Folder";

    /// <summary>
    /// A URL (http/https) to a single skill file. The content is loaded once per chat start.
    /// </summary>
    public const string Url = "Url";

    /// <summary>
    /// A GitHub repository ('https://github.com/{owner}/{repo}' optionally followed by '/tree/{ref}/{path}'),
    /// which contains skills ('&lt;skill&gt;/SKILL.md'). The content is loaded once per chat start.
    /// </summary>
    public const string GitHub = "GitHub";

    /// <summary>
    /// A built-in source (not removable, level not changeable).
    /// </summary>
    public const string BuiltIn = "BuiltIn";

  }

  /// <summary>
  /// Describes a skill source.
  /// </summary>
  public class SkillSourceInfo {

    private string _SourceId;
    private string _Kind;
    private string _DisplayName;
    private string _Location;
    private bool _HasCredentials;
    private string _CredentialHint;
    private string _Level;
    private bool _IsFixed;
    private string[] _SkillNames = new string[0];
    private bool _IsReferenceBroken;
    private string _LastError;
    private bool _SecureAvailability;
    private bool _SupportsSecureAvailability;
    private string _OfflineCopyTimestampUtc;

    /// <summary>
    /// Stable id (also used as key for per-chat overrides and credentials).
    /// </summary>
    public string SourceId {
      get {
        return _SourceId;
      }
      set {
        _SourceId = value;
      }
    }

    /// <summary>
    /// See 'SkillSourceKinds'.
    /// </summary>
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

    /// <summary>
    /// File path (local or UNC), folder path, URL or repository URL.
    /// </summary>
    public string Location {
      get {
        return _Location;
      }
      set {
        _Location = value;
      }
    }

    /// <summary>
    /// True if a PAT/token is present (the value itself is never returned).
    /// </summary>
    public bool HasCredentials {
      get {
        return _HasCredentials;
      }
      set {
        _HasCredentials = value;
      }
    }

    public string CredentialHint {
      get {
        return _CredentialHint;
      }
      set {
        _CredentialHint = value;
      }
    }

    /// <summary>
    /// Global default level (see 'SkillSourceLevels').
    /// </summary>
    public string Level {
      get {
        return _Level;
      }
      set {
        _Level = value;
      }
    }

    /// <summary>
    /// True for built-in sources (not removable, level not changeable).
    /// </summary>
    public bool IsFixed {
      get {
        return _IsFixed;
      }
      set {
        _IsFixed = value;
      }
    }

    /// <summary>
    /// The resolved skill names (for display).
    /// </summary>
    public string[] SkillNames {
      get {
        return _SkillNames;
      }
      set {
        _SkillNames = value;
      }
    }

    /// <summary>
    /// True if the referenced location cannot be read (the UI shows an exclamation mark).
    /// </summary>
    public bool IsReferenceBroken {
      get {
        return _IsReferenceBroken;
      }
      set {
        _IsReferenceBroken = value;
      }
    }

    /// <summary>
    /// Reason of a broken reference (null if fine).
    /// </summary>
    public string LastError {
      get {
        return _LastError;
      }
      set {
        _LastError = value;
      }
    }

    /// <summary>
    /// True if a local offline copy is kept ('secure availability', see 'SetSkillSourceSecureAvailability').
    /// </summary>
    public bool SecureAvailability {
      get {
        return _SecureAvailability;
      }
      set {
        _SecureAvailability = value;
      }
    }

    /// <summary>
    /// True if 'secure availability' can be enabled for this source.
    /// </summary>
    public bool SupportsSecureAvailability {
      get {
        return _SupportsSecureAvailability;
      }
      set {
        _SupportsSecureAvailability = value;
      }
    }

    /// <summary>
    /// ISO 8601 timestamp of the last successful offline copy refresh (null if none).
    /// </summary>
    public string OfflineCopyTimestampUtc {
      get {
        return _OfflineCopyTimestampUtc;
      }
      set {
        _OfflineCopyTimestampUtc = value;
      }
    }

  }

}
