using AI.SmartStandards.LowLevelPrompting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Settings service which persists the selected LLM provider, the model and the API keys (PATs) as JSON file
  /// within the roaming profile of the current user (default: '%APPDATA%\SmartStandards\AiSupport\settings.json').
  /// It also acts as factory for the configured 'ICommonLLM'.
  /// </summary>
  public class FileBasedPromptingSettingsService : IPromptingSettingsService {

    public const string OpenAiProviderName = "OpenAI";

    private readonly string _SettingsFileFullName;

    private readonly object _SyncRoot = new object();

    private readonly List<LLMProviderInfo> _Providers = new List<LLMProviderInfo>();

    private readonly Dictionary<string, Func<string, string, ICommonLLM>> _ProviderFactories = (
      new Dictionary<string, Func<string, string, ICommonLLM>>(StringComparer.OrdinalIgnoreCase)
    );

    /// <summary>
    /// Creates the service using the default settings file within the roaming profile of the current user.
    /// The OpenAI provider is registered out of the box.
    /// </summary>
    public FileBasedPromptingSettingsService() : this(GetDefaultSettingsFileFullName()) {
    }

    /// <summary>
    /// Creates the service using the given settings file. The OpenAI provider is registered out of the box.
    /// </summary>
    public FileBasedPromptingSettingsService(string settingsFileFullName) {

      if (string.IsNullOrWhiteSpace(settingsFileFullName)) {
        throw new ArgumentNullException(nameof(settingsFileFullName));
      }

      _SettingsFileFullName = settingsFileFullName;

      LLMProviderInfo openAi = new LLMProviderInfo();
      openAi.Name = OpenAiProviderName;
      openAi.DisplayName = "OpenAI (api.openai.com)";
      openAi.DefaultModel = OpenAiLLMConnector.DefaultChatModel;
      openAi.RequiresApiKey = true;

      this.RegisterProvider(openAi, (apiKey, model) => new OpenAiLLMConnector(apiKey, model));

      // Selectable mock (no API key required) to test chat UIs without costs or network access
      LLMProviderInfo stupidMock = new LLMProviderInfo();
      stupidMock.Name = StupidMockLLM.ProviderName;
      stupidMock.DisplayName = "Stupid Mock LLM (for UI tests)";
      stupidMock.DefaultModel = "stupid";
      stupidMock.RequiresApiKey = false;

      this.RegisterProvider(stupidMock, (apiKey, model) => new StupidMockLLM());

    }

    /// <summary>
    /// Returns '%APPDATA%\SmartStandards\AiSupport\settings.json'.
    /// </summary>
    public static string GetDefaultSettingsFileFullName() {

      string roamingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

      return Path.Combine(roamingDirectory, "SmartStandards", "AiSupport", "settings.json");
    }

    public string SettingsFileFullName {
      get {
        return _SettingsFileFullName;
      }
    }

    /// <summary>
    /// Registers an additional (or replaces an existing) LLM provider.
    /// The factory receives the stored API key and the selected model.
    /// </summary>
    public void RegisterProvider(LLMProviderInfo providerInfo, Func<string, string, ICommonLLM> factory) {

      if (providerInfo == null) {
        throw new ArgumentNullException(nameof(providerInfo));
      }

      if (factory == null) {
        throw new ArgumentNullException(nameof(factory));
      }

      lock (_SyncRoot) {
        _Providers.RemoveAll((p) => string.Equals(p.Name, providerInfo.Name, StringComparison.OrdinalIgnoreCase));
        _Providers.Add(providerInfo);
        _ProviderFactories[providerInfo.Name] = factory;
      }

    }

    /// <summary>
    /// Returns all LLM providers which can be selected.
    /// </summary>
    public LLMProviderInfo[] GetAvailableProviders() {

      lock (_SyncRoot) {
        return _Providers.ToArray();
      }

    }

    /// <summary>
    /// Returns the current settings (without the API key itself).
    /// </summary>
    public PromptingSettingsInfo GetSettings() {

      lock (_SyncRoot) {

        PersistedSettings persisted = this.ReadSettings();
        LLMProviderInfo provider = this.GetSelectedProvider(persisted);

        PromptingSettingsInfo info = new PromptingSettingsInfo();

        if (provider == null) {
          return info;
        }

        string apiKey = GetValueOrNull(persisted.ApiKeys, provider.Name);

        info.ProviderName = provider.Name;
        info.Model = this.GetSelectedModel(persisted, provider);
        info.HasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        info.ApiKeyHint = MaskApiKey(apiKey);
        info.IsReady = (info.HasApiKey || !provider.RequiresApiKey);

        return info;
      }

    }

    /// <summary>
    /// Saves the settings. If 'apiKey' is null or empty, an already stored API key is kept.
    /// </summary>
    public void SaveSettings(string providerName, string model, string apiKey) {

      lock (_SyncRoot) {

        LLMProviderInfo provider = this.FindProvider(providerName);

        if (provider == null) {
          throw new ArgumentException($"Unknown LLM provider '{providerName}'.", nameof(providerName));
        }

        PersistedSettings persisted = this.ReadSettings();

        persisted.ProviderName = provider.Name;

        if (string.IsNullOrWhiteSpace(model)) {
          persisted.Models.Remove(provider.Name);
        }
        else {
          persisted.Models[provider.Name] = model.Trim();
        }

        if (!string.IsNullOrWhiteSpace(apiKey)) {
          persisted.ApiKeys[provider.Name] = apiKey.Trim();
        }

        this.WriteSettings(persisted);
      }

    }

    /// <summary>
    /// Removes the stored API key of the given provider.
    /// </summary>
    public void DeleteApiKey(string providerName) {

      lock (_SyncRoot) {

        PersistedSettings persisted = this.ReadSettings();

        string keyToRemove = persisted.ApiKeys.Keys.FirstOrDefault(
          (k) => string.Equals(k, providerName, StringComparison.OrdinalIgnoreCase)
        );

        if (keyToRemove != null) {
          persisted.ApiKeys.Remove(keyToRemove);
          this.WriteSettings(persisted);
        }

      }

    }

    /// <summary>
    /// Creates an 'ICommonLLM' for the currently selected provider (throws an 'InvalidOperationException'
    /// with a user-readable message if the settings are incomplete).
    /// </summary>
    public ICommonLLM CreateConfiguredLLM() {

      Func<string, string, ICommonLLM> factory;
      string apiKey;
      string model;

      lock (_SyncRoot) {

        PersistedSettings persisted = this.ReadSettings();
        LLMProviderInfo provider = this.GetSelectedProvider(persisted);

        if (provider == null) {
          throw new InvalidOperationException("No LLM provider is configured. Please open the settings.");
        }

        apiKey = GetValueOrNull(persisted.ApiKeys, provider.Name);
        model = this.GetSelectedModel(persisted, provider);

        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey)) {
          throw new InvalidOperationException(
            $"No API key is stored for the LLM provider '{provider.DisplayName}'. Please open the settings."
          );
        }

        factory = _ProviderFactories[provider.Name];
      }

      return factory.Invoke(apiKey, model);
    }

    /// <summary>
    /// Returns a display name for the currently selected provider and model (for example 'OpenAI gpt-4o').
    /// </summary>
    public string GetConfiguredLLMDisplayName() {

      PromptingSettingsInfo info = this.GetSettings();

      if (string.IsNullOrWhiteSpace(info.ProviderName)) {
        return "AI";
      }

      return info.ProviderName + " " + info.Model;
    }

    #region " Persistence "

    private LLMProviderInfo FindProvider(string providerName) {

      if (string.IsNullOrWhiteSpace(providerName)) {
        return null;
      }

      return _Providers.FirstOrDefault(
        (p) => string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase)
      );
    }

    private LLMProviderInfo GetSelectedProvider(PersistedSettings persisted) {

      LLMProviderInfo provider = this.FindProvider(persisted.ProviderName);

      if (provider == null) {
        provider = _Providers.FirstOrDefault();
      }

      return provider;
    }

    private string GetSelectedModel(PersistedSettings persisted, LLMProviderInfo provider) {

      string model = GetValueOrNull(persisted.Models, provider.Name);

      if (string.IsNullOrWhiteSpace(model)) {
        model = provider.DefaultModel;
      }

      return model;
    }

    private static string GetValueOrNull(Dictionary<string, string> dict, string key) {

      foreach (KeyValuePair<string, string> entry in dict) {
        if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)) {
          return entry.Value;
        }
      }

      return null;
    }

    private static string MaskApiKey(string apiKey) {

      if (string.IsNullOrWhiteSpace(apiKey)) {
        return null;
      }

      if (apiKey.Length <= 8) {
        return "****";
      }

      return apiKey.Substring(0, 3) + "..." + apiKey.Substring(apiKey.Length - 4);
    }

    private PersistedSettings ReadSettings() {

      PersistedSettings persisted = null;

      if (File.Exists(_SettingsFileFullName)) {
        string json = File.ReadAllText(_SettingsFileFullName);
        persisted = JsonConvert.DeserializeObject<PersistedSettings>(json);
      }

      if (persisted == null) {
        persisted = new PersistedSettings();
      }

      if (persisted.Models == null) {
        persisted.Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      }

      if (persisted.ApiKeys == null) {
        persisted.ApiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      }

      return persisted;
    }

    private void WriteSettings(PersistedSettings persisted) {

      string directory = Path.GetDirectoryName(_SettingsFileFullName);

      if (!string.IsNullOrWhiteSpace(directory)) {
        Directory.CreateDirectory(directory);
      }

      string json = JsonConvert.SerializeObject(persisted, Formatting.Indented);

      File.WriteAllText(_SettingsFileFullName, json);

    }

    internal sealed class PersistedSettings {

      private string _ProviderName;
      private Dictionary<string, string> _Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      private Dictionary<string, string> _ApiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      public string ProviderName {
        get {
          return _ProviderName;
        }
        set {
          _ProviderName = value;
        }
      }

      public Dictionary<string, string> Models {
        get {
          return _Models;
        }
        set {
          _Models = value;
        }
      }

      public Dictionary<string, string> ApiKeys {
        get {
          return _ApiKeys;
        }
        set {
          _ApiKeys = value;
        }
      }

    }

    #endregion

  }

}
