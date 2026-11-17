using System;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Factory for the tools of a tool source. Built-in (OOB) tools and tools from foreign DLLs implement this.
  /// Every provider owns exactly one JSON settings object (see 'GetDefaultSettingsJson'), which is persisted
  /// by the extension service. Secrets are never part of that object (they use the credential of the source).
  /// </summary>
  public interface IHarnessToolProvider {

    /// <summary>
    /// Stable id of the provider (for built-in providers also used as SourceId).
    /// </summary>
    string ProviderId { get; }

    string DisplayName { get; }

    /// <summary>
    /// Default level (see 'ToolSourceLevels') of the tool source if the user did not set one.
    /// </summary>
    string DefaultLevel { get; }

    /// <summary>
    /// True if the source supports a credential (PAT/token), which is stored separately and never returned.
    /// </summary>
    bool SupportsCredential { get; }

    /// <summary>
    /// Default settings object (JSON) for a new instance (null or empty if the provider has no settings).
    /// </summary>
    string GetDefaultSettingsJson();

    /// <summary>
    /// Self-contained HTML page (inline JS/CSS) of the detail tab, or null (a plain JSON editor is used then).
    /// The page is rendered within a sandboxed iframe and communicates exclusively via 'postMessage'
    /// ('settings.load', 'settings.changed', 'settings.ready').
    /// </summary>
    string GetSettingsUiHtml();

    /// <summary>
    /// Validates (and optionally normalizes) a settings object before it is saved.
    /// Throws an 'ArgumentException' with a readable message if it is invalid.
    /// </summary>
    string ValidateSettingsJson(string settingsJson);

    /// <summary>
    /// Creates the tools for the given (persisted) configuration. Throws if the source is not reachable
    /// (the source is flagged as broken then).
    /// </summary>
    IHarnessTool[] CreateTools(IToolProviderContext context);

  }

  /// <summary>
  /// Gives a provider access to the persisted configuration of its tool source.
  /// </summary>
  public interface IToolProviderContext {

    string SourceId { get; }

    /// <summary>
    /// Location of the source (for example the URL of an MCP server), may be null.
    /// </summary>
    string Location { get; }

    /// <summary>
    /// The current settings object (JSON).
    /// </summary>
    string SettingsJson { get; }

    /// <summary>
    /// The credential (PAT/token) of the source or null.
    /// </summary>
    string Credential { get; }

    /// <summary>
    /// Persists a changed settings object (for example a guard allow-list entry added by 'AllowPermanently').
    /// </summary>
    void SaveSettingsJson(string settingsJson);

  }

  /// <summary>
  /// Default implementation of 'IToolProviderContext'.
  /// </summary>
  public class ToolProviderContext : IToolProviderContext {

    private readonly string _SourceId;

    private readonly string _Location;

    private readonly Func<string> _SettingsGetter;

    private readonly Func<string> _CredentialGetter;

    private readonly Action<string> _SettingsSaver;

    public ToolProviderContext(
      string sourceId, string location, Func<string> settingsGetter, Func<string> credentialGetter, Action<string> settingsSaver
    ) {

      if (settingsGetter == null) {
        throw new ArgumentNullException(nameof(settingsGetter));
      }

      if (credentialGetter == null) {
        throw new ArgumentNullException(nameof(credentialGetter));
      }

      _SourceId = sourceId;
      _Location = location;
      _SettingsGetter = settingsGetter;
      _CredentialGetter = credentialGetter;
      _SettingsSaver = settingsSaver;

    }

    public string SourceId {
      get {
        return _SourceId;
      }
    }

    public string Location {
      get {
        return _Location;
      }
    }

    /// <summary>
    /// Always returns the currently persisted settings (changes are effective without recreating the tools).
    /// </summary>
    public string SettingsJson {
      get {
        return _SettingsGetter.Invoke();
      }
    }

    public string Credential {
      get {
        return _CredentialGetter.Invoke();
      }
    }

    public void SaveSettingsJson(string settingsJson) {

      if (_SettingsSaver == null) {
        throw new NotSupportedException("The settings of this tool source cannot be saved.");
      }

      _SettingsSaver.Invoke(settingsJson);

    }

  }

}
