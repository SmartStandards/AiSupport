using AI.SmartStandards.InteractivePrompting.Harness;
using AI.SmartStandards.LowLevelPrompting;
using System;
using System.IO;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Bundles all services of ONE harness profile (settings incl. API keys, skill/tool sources, toolbox, chats and
  /// the chat backend). Profile switches create a new runtime; running turns keep the runtime which was active at
  /// their start (SEC-6).
  /// </summary>
  public class HarnessProfileRuntime {

    private readonly string _ProfileId;
    private readonly string _ProfileDirectory;
    private readonly FileBasedPromptingSettingsService _SettingsService;
    private readonly FileBasedPromptingExtensionService _ExtensionService;
    private readonly FileBasedPromptingSessionStore _SessionStore;
    private readonly HarnessToolbox _Toolbox;
    private CheapPromptingUIBackend _Backend;

    /// <summary>
    /// Creates the services of the profile within the given directory. The user instructions
    /// ('global-user-preferences') and the system preferences are shared by all profiles.
    /// </summary>
    /// <param name="profileId">Id of the profile.</param>
    /// <param name="profileDirectory">Directory which contains the files of the profile.</param>
    /// <param name="configureToolbox">
    /// Optional hook to register additional (host-specific) skills/tools before the backend is created.
    /// </param>
    public HarnessProfileRuntime(string profileId, string profileDirectory, Action<HarnessProfileRuntime> configureToolbox)
      : this(profileId, profileDirectory, null, configureToolbox) {
    }

    /// <summary>
    /// Creates the services of the profile within the given directory. The user instructions
    /// ('global-user-preferences') and the system preferences are shared by all profiles.
    /// </summary>
    /// <param name="profileId">Id of the profile.</param>
    /// <param name="profileDirectory">Directory which contains the files of the profile.</param>
    /// <param name="toolProviderFactories">
    /// Optional catalog of host-contributed tool sources (SRV-1). Every factory creates an own provider for this
    /// profile, which is registered as built-in tool source.
    /// </param>
    /// <param name="configureToolbox">
    /// Optional hook to register additional (host-specific) skills/tools before the backend is created.
    /// </param>
    public HarnessProfileRuntime(
      string profileId, string profileDirectory, IHarnessToolProviderFactory[] toolProviderFactories, Action<HarnessProfileRuntime> configureToolbox
    ) {

      if (string.IsNullOrWhiteSpace(profileDirectory)) {
        throw new ArgumentNullException(nameof(profileDirectory));
      }

      _ProfileId = profileId;
      _ProfileDirectory = profileDirectory;

      _SettingsService = new FileBasedPromptingSettingsService(Path.Combine(profileDirectory, "settings.json"));
      _SessionStore = new FileBasedPromptingSessionStore(Path.Combine(profileDirectory, "Chats"));

      _ExtensionService = new FileBasedPromptingExtensionService(
        Path.Combine(profileDirectory, "extensions.json"),
        FileBasedPromptingExtensionService.GetDefaultUserPreferencesFileFullName(),
        FileBasedPromptingExtensionService.GetDefaultSystemPreferencesFileFullName()
      );

      _SettingsService.UseUserInstructionsFile(
        _ExtensionService.UserPreferencesFileFullName,
        FileBasedPromptingExtensionService.UserPreferencesSourceId,
        "Preferences of the user (conventions, rules). They MUST be read and followed for every task."
      );

      //tools for the model: OOB providers incl. the 'Chat environment' (list/read chats, theme, rename)
      _Toolbox = new HarnessToolbox();
      _ExtensionService.RegisterOutOfTheBoxToolProviders();
      _ExtensionService.RegisterBuiltInToolProvider(new ChatEnvironmentToolProvider(_SettingsService, () => _Backend));

      if (toolProviderFactories != null) {
        this.RegisterFactoryToolProviders(toolProviderFactories);
      }

      _ExtensionService.AttachToolbox(_Toolbox);

      if (configureToolbox != null) {
        configureToolbox.Invoke(this);
      }

      //the LLM is created per call (per chat provider/model), so that changed settings are effective immediately
      FileBasedPromptingSettingsService settingsService = _SettingsService;

      _Backend = new CheapPromptingUIBackend(
        _SessionStore,
        (IChatLLMFactory)_SettingsService,
        _Toolbox,
        () => settingsService.GetConfiguredLLMDisplayName(),
        Environment.UserName
      );

      //'secure availability': the offline copies of the skill sources are refreshed once on every new chat start
      _Backend.OnNewChatStarting = _ExtensionService.RefreshOfflineCopies;

    }

    public string ProfileId {
      get {
        return _ProfileId;
      }
    }

    public string ProfileDirectory {
      get {
        return _ProfileDirectory;
      }
    }

    public FileBasedPromptingSettingsService SettingsService {
      get {
        return _SettingsService;
      }
    }

    public FileBasedPromptingExtensionService ExtensionService {
      get {
        return _ExtensionService;
      }
    }

    public FileBasedPromptingSessionStore SessionStore {
      get {
        return _SessionStore;
      }
    }

    public HarnessToolbox Toolbox {
      get {
        return _Toolbox;
      }
    }

    public CheapPromptingUIBackend Backend {
      get {
        return _Backend;
      }
    }

    /// <summary>
    /// Creates the providers of the given factories for this profile and registers them as built-in tool sources.
    /// </summary>
    private void RegisterFactoryToolProviders(IHarnessToolProviderFactory[] toolProviderFactories) {

      foreach (IHarnessToolProviderFactory factory in toolProviderFactories) {

        if (factory == null) {
          continue;
        }

        IHarnessToolProvider provider = factory.CreateProvider(_ProfileId);

        if (provider == null) {
          continue;
        }

        if (!string.Equals(provider.ProviderId, factory.SourceId, StringComparison.OrdinalIgnoreCase)) {
          throw new InvalidOperationException(
            $"The tool provider factory '{factory.SourceId}' created a provider with the different id '{provider.ProviderId}'."
          );
        }

        _ExtensionService.RegisterBuiltInToolProvider(provider);
      }

    }

    /// <summary>
    /// Applies the resolver of the user level (ROL) to all services of the runtime.
    /// </summary>
    public void ApplyUserLevelGetter(Func<int> userLevelGetter) {

      _SettingsService.UserLevelGetter = userLevelGetter;
      _ExtensionService.UserLevelGetter = userLevelGetter;
      _Backend.UserLevelGetter = userLevelGetter;

    }

  }

}
