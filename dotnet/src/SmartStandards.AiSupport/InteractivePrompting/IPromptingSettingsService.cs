using System;
using System.ComponentModel;
using System.Diagnostics.Contracts;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Small settings service which allows to select the LLM provider and to deposit its API key (PAT).
  /// The API key itself is never returned to the caller (only whether it is present).
  /// </summary>
#if NET_FX
  [ServiceContract()]
#endif
  public interface IPromptingSettingsService {

    /// <summary>
    /// Returns all LLM providers which can be selected.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    LLMProviderInfo[] GetAvailableProviders();

    /// <summary>
    /// Returns the current settings (without the API key itself).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    PromptingSettingsInfo GetSettings();

    /// <summary>
    /// Saves the settings. If 'apiKey' is null or empty, an already stored API key is kept.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SaveSettings(string providerName, string model, string apiKey);

    /// <summary>
    /// Removes the stored API key of the given provider.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void DeleteApiKey(string providerName);

    /// <summary>
    /// Returns the names of the capabilities which were disabled (opted out) by the user for the given provider.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string[] GetDisabledCapabilities(string providerName);

    /// <summary>
    /// Saves the names of the capabilities which should be disabled (opted out) for the given provider.
    /// Unknown or unavailable capability names are ignored.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SaveDisabledCapabilities(string providerName, string[] disabledCapabilityNames);

    /// <summary>
    /// Returns the (global) user instructions, which are provided to the model as the mandatory skill
    /// 'mustread-user-instructions' (empty string if none were deposited).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string GetUserInstructions();

    /// <summary>
    /// Saves the (global) user instructions. An empty value removes them.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SaveUserInstructions(string userInstructions);

    /// <summary>
    /// Returns the current UI theme (see 'UiThemeNames').
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string GetUiTheme();

    /// <summary>
    /// Saves the UI theme (see 'UiThemeNames'). Unknown values are treated as 'light'.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SaveUiTheme(string themeName);

  }

  /// <summary>
  /// Well-known names of the UI themes.
  /// </summary>
  public static class UiThemeNames {

    public const string Light = "light";

    public const string Dark = "dark";

    /// <summary>
    /// Returns the normalized theme name ('dark' or 'light'; unknown values fall back to 'light').
    /// </summary>
    public static string Normalize(string themeName) {

      if (themeName != null && string.Equals(themeName.Trim(), Dark, StringComparison.OrdinalIgnoreCase)) {
        return Dark;
      }

      return Light;
    }

  }

  /// <summary>
  /// Well-known names of optional provider capabilities. A capability means that the provider (or the SDK/harness
  /// behind it) can do something natively, which the Level-2 harness would otherwise have to emulate.
  /// </summary>
  public static class LLMCapabilityNames {

    /// <summary>
    /// The provider keeps the conversation context on its side and returns a 'contextStore' value, so that only
    /// new messages are transferred. If disabled (or unavailable), the harness sends its self-maintained context.
    /// </summary>
    public const string ProviderSideContext = "ProviderSideContext";

    /// <summary>
    /// The provider keeps a live session (for example a running CLI process) per chat between the turns.
    /// If disabled (or unavailable), a fresh session/process is used per turn.
    /// </summary>
    public const string PersistentSession = "PersistentSession";

    /// <summary>
    /// The provider can restore an ended session by its id (for example after an idle shutdown or an application
    /// restart). If disabled (or unavailable), the harness restores the context from the outer chat history.
    /// </summary>
    public const string SessionResume = "SessionResume";

    /// <summary>
    /// The provider (or its inner harness) can keep a pending tool call open for an arbitrary time, so that the
    /// harness can silently wait for an interactive approval of the user (Variant A). If disabled (or unavailable),
    /// the harness answers immediately with a 'permission is being requested' notice and continues with a control
    /// message after the decision (Variant B).
    /// </summary>
    public const string PausableToolCalls = "PausableToolCalls";

    /// <summary>
    /// Returns all well-known capability names (in display order).
    /// </summary>
    public static string[] GetAll() {

      return new string[] { ProviderSideContext, PersistentSession, SessionResume, PausableToolCalls };
    }

  }

  /// <summary>
  /// Describes one optional capability of a provider.
  /// </summary>
  public class LLMCapabilityInfo {

    private string _Name;
    private string _DisplayName;
    private string _Description;
    private bool _IsAvailable;

    /// <summary>
    /// Technical name (see 'LLMCapabilityNames').
    /// </summary>
    public string Name {
      get {
        return _Name;
      }
      set {
        _Name = value;
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

    public string Description {
      get {
        return _Description;
      }
      set {
        _Description = value;
      }
    }

    /// <summary>
    /// True if the provider supports this capability natively (only then it can be opted out).
    /// </summary>
    public bool IsAvailable {
      get {
        return _IsAvailable;
      }
      set {
        _IsAvailable = value;
      }
    }

  }

  public class LLMProviderInfo {

    private string _Name;
    private string _DisplayName;
    private string _DefaultModel;
    private bool _RequiresApiKey;
    private LLMCapabilityInfo[] _Capabilities = new LLMCapabilityInfo[0];
    private string[] _KnownModels = new string[0];

    /// <summary>
    /// All well-known capabilities, each flagged whether the provider supports it natively.
    /// </summary>
    public LLMCapabilityInfo[] Capabilities {
      get {
        return _Capabilities;
      }
      set {
        _Capabilities = value;
      }
    }

    /// <summary>
    /// Technical name (for example 'OpenAI').
    /// </summary>
    public string Name {
      get {
        return _Name;
      }
      set {
        _Name = value;
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

    public string DefaultModel {
      get {
        return _DefaultModel;
      }
      set {
        _DefaultModel = value;
      }
    }

    public bool RequiresApiKey {
      get {
        return _RequiresApiKey;
      }
      set {
        _RequiresApiKey = value;
      }
    }

    /// <summary>
    /// Optional list of model names known to the provider (offered in the model combo box, free text remains
    /// possible). Empty if the provider does not deliver such a list.
    /// </summary>
    public string[] KnownModels {
      get {
        return _KnownModels;
      }
      set {
        _KnownModels = value;
      }
    }

  }

  public class PromptingSettingsInfo {

    private string _ProviderName;
    private string _Model;
    private bool _HasApiKey;
    private string _ApiKeyHint;
    private bool _IsReady;
    private int _UserLevel;

    /// <summary>
    /// Level of the current user (see 'PromptingUserLevels'). Clients must treat unknown values (for example 0 from
    /// older servers) as 'Admin'. The UI only reflects the level, the restrictions are enforced by the services.
    /// </summary>
    public int UserLevel {
      get {
        return _UserLevel;
      }
      set {
        _UserLevel = value;
      }
    }

    public string ProviderName {
      get {
        return _ProviderName;
      }
      set {
        _ProviderName = value;
      }
    }

    public string Model {
      get {
        return _Model;
      }
      set {
        _Model = value;
      }
    }

    /// <summary>
    /// True if an API key is stored for the selected provider.
    /// </summary>
    public bool HasApiKey {
      get {
        return _HasApiKey;
      }
      set {
        _HasApiKey = value;
      }
    }

    /// <summary>
    /// Masked representation of the stored API key (for example 'sk-...AbCd'), never the key itself.
    /// </summary>
    public string ApiKeyHint {
      get {
        return _ApiKeyHint;
      }
      set {
        _ApiKeyHint = value;
      }
    }

    /// <summary>
    /// True if the settings are complete, so that chatting is possible.
    /// </summary>
    public bool IsReady {
      get {
        return _IsReady;
      }
      set {
        _IsReady = value;
      }
    }

  }

}
