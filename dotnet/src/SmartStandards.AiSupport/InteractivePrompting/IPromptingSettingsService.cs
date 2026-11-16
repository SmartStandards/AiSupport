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

  }

  public class LLMProviderInfo {

    private string _Name;
    private string _DisplayName;
    private string _DefaultModel;
    private bool _RequiresApiKey;

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

  }

  public class PromptingSettingsInfo {

    private string _ProviderName;
    private string _Model;
    private bool _HasApiKey;
    private string _ApiKeyHint;
    private bool _IsReady;

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
