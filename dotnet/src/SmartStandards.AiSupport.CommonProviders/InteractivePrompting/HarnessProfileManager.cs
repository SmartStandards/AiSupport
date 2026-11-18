using AI.SmartStandards.InteractivePrompting.Harness;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Manages the harness profiles of a running client (PRF-1..12): it owns the active 'HarnessProfileRuntime' and
  /// switches it on 'ActivateProfile'. The DI-facing services ('ActiveProfileUIBackend',
  /// 'ActiveProfileSettingsService', 'ActiveProfileExtensionService') forward every call to the active runtime,
  /// so the UJMW controllers never have to be re-registered. Running turns keep the runtime of their start (SEC-6).
  /// </summary>
  public class HarnessProfileManager : IPromptingProfileService {

    private readonly FileBasedPromptingProfileService _ProfileStore;

    private readonly Action<HarnessProfileRuntime> _ConfigureRuntime;

    private readonly IHarnessToolProviderFactory[] _ToolProviderFactories;

    private readonly object _SyncRoot = new object();

    private readonly List<HarnessProfileRuntime> _RetiredRuntimes = new List<HarnessProfileRuntime>();

    private HarnessProfileRuntime _ActiveRuntime;

    private HarnessProfileRuntime _DefaultRuntime;

    private Func<int> _UserLevelGetter;

    /// <summary>
    /// Creates the manager and activates the remembered profile (or the given startup profile).
    /// </summary>
    /// <param name="profileStore">Persistence of the profiles.</param>
    /// <param name="startupProfileId">Optional profile to activate instead of the remembered one (PRF-8).</param>
    /// <param name="configureRuntime">
    /// Optional hook which is invoked for every created runtime (before its backend is created), for example to
    /// register host-specific tools.
    /// </param>
    public HarnessProfileManager(
      FileBasedPromptingProfileService profileStore, string startupProfileId, Action<HarnessProfileRuntime> configureRuntime
    ) : this(profileStore, startupProfileId, null, configureRuntime) {
    }

    /// <summary>
    /// Creates the manager and activates the remembered profile (or the given startup profile).
    /// </summary>
    /// <param name="profileStore">Persistence of the profiles.</param>
    /// <param name="startupProfileId">Optional profile to activate instead of the remembered one (PRF-8).</param>
    /// <param name="toolProviderFactories">
    /// Optional catalog of host-contributed tool sources (SRV-1). Every created runtime gets its own providers,
    /// which are registered as built-in tool sources.
    /// </param>
    /// <param name="configureRuntime">
    /// Optional hook which is invoked for every created runtime (before its backend is created), for example to
    /// register host-specific tools.
    /// </param>
    public HarnessProfileManager(
      FileBasedPromptingProfileService profileStore, string startupProfileId,
      IHarnessToolProviderFactory[] toolProviderFactories, Action<HarnessProfileRuntime> configureRuntime
    ) {

      if (profileStore == null) {
        throw new ArgumentNullException(nameof(profileStore));
      }

      _ProfileStore = profileStore;
      _ConfigureRuntime = configureRuntime;

      if (toolProviderFactories == null) {
        _ToolProviderFactories = new IHarnessToolProviderFactory[0];
      }
      else {
        _ToolProviderFactories = toolProviderFactories.ToArray();
      }


      string profileId = profileStore.GetActiveProfileId();

      //a startup profile (for example '--profile') overrides the remembered one WITHOUT being remembered (PRF-8)
      if (!string.IsNullOrWhiteSpace(startupProfileId)) {

        HarnessProfileInfo startupProfile = profileStore.FindProfile(startupProfileId);

        if (startupProfile == null) {
          throw new ArgumentException($"Unknown profile '{startupProfileId}'.", nameof(startupProfileId));
        }

        profileId = startupProfile.ProfileId;
      }

      _ActiveRuntime = this.CreateRuntime(profileId);

    }

    /// <summary>
    /// Persistence of the profiles.
    /// </summary>
    public FileBasedPromptingProfileService ProfileStore {
      get {
        return _ProfileStore;
      }
    }

    /// <summary>
    /// The runtime of the currently active profile.
    /// </summary>
    public HarnessProfileRuntime ActiveRuntime {
      get {
        lock (_SyncRoot) {
          return _ActiveRuntime;
        }
      }
    }

    /// <summary>
    /// Display name of the active profile (for example for the window title, PRF-10).
    /// </summary>
    public string ActiveProfileDisplayName {
      get {

        string activeId = this.ActiveRuntime.ProfileId;

        HarnessProfileInfo profile = _ProfileStore.GetProfiles().FirstOrDefault(
          (p) => string.Equals(p.ProfileId, activeId, StringComparison.OrdinalIgnoreCase)
        );

        if (profile == null) {
          return activeId;
        }

        return profile.DisplayName;
      }
    }

    /// <summary>
    /// Is raised after a profile switch (for example to update the window title).
    /// </summary>
    public event EventHandler ActiveProfileChanged;

    /// <summary>
    /// Optional resolver of the level of the current user (ROL). It is applied to the profile store and to every
    /// runtime (also to runtimes created later).
    /// </summary>
    public Func<int> UserLevelGetter {
      get {
        return _UserLevelGetter;
      }
      set {

        _UserLevelGetter = value;
        _ProfileStore.UserLevelGetter = value;

        lock (_SyncRoot) {

          _ActiveRuntime.ApplyUserLevelGetter(value);

          if (_DefaultRuntime != null) {
            _DefaultRuntime.ApplyUserLevelGetter(value);
          }

        }

      }
    }

    /// <summary>
    /// Returns the runtime which is responsible for the given chat: the active runtime, or a retired runtime
    /// which still knows the chat (for example a running turn which was started before a profile switch).
    /// </summary>
    internal HarnessProfileRuntime GetRuntimeForChat(string chatId) {

      lock (_SyncRoot) {

        if (string.IsNullOrWhiteSpace(chatId) || _ActiveRuntime.SessionStore.SessionExists(chatId)) {
          return _ActiveRuntime;
        }

        for (int i = _RetiredRuntimes.Count - 1; i >= 0; i--) {

          HarnessProfileRuntime retired = _RetiredRuntimes[i];

          if (retired.SessionStore.SessionExists(chatId)) {
            return retired;
          }

        }

        return _ActiveRuntime;
      }

    }

    /// <summary>
    /// Returns the settings service of the default profile, which holds the settings shared by all profiles
    /// (the UI theme, PRF-2).
    /// </summary>
    internal FileBasedPromptingSettingsService GetSharedSettingsService() {

      lock (_SyncRoot) {

        if (FileBasedPromptingProfileService.IsDefaultProfileId(_ActiveRuntime.ProfileId)) {
          return _ActiveRuntime.SettingsService;
        }

        if (_DefaultRuntime == null) {
          _DefaultRuntime = this.CreateRuntime(FileBasedPromptingProfileService.DefaultProfileId);
        }

        return _DefaultRuntime.SettingsService;
      }

    }

    #region " IPromptingProfileService "

    public HarnessProfileInfo[] GetProfiles() {

      string activeId = this.ActiveRuntime.ProfileId;
      HarnessProfileInfo[] profiles = _ProfileStore.GetProfiles();

      //the running client may use a startup profile which is not the remembered one (PRF-8)
      foreach (HarnessProfileInfo profile in profiles) {
        profile.IsActive = string.Equals(profile.ProfileId, activeId, StringComparison.OrdinalIgnoreCase);
      }

      return profiles;
    }

    public string GetActiveProfileId() {
      return this.ActiveRuntime.ProfileId;
    }

    public string CreateProfile(string copyFromProfileId, string displayName) {

      if (string.IsNullOrWhiteSpace(copyFromProfileId)) {
        copyFromProfileId = this.ActiveRuntime.ProfileId;
      }

      return _ProfileStore.CreateProfile(copyFromProfileId, displayName);
    }

    public void RenameProfile(string profileId, string displayName) {

      _ProfileStore.RenameProfile(profileId, displayName);

      this.RaiseActiveProfileChanged();

    }

    public void DeleteProfile(string profileId) {

      if (string.Equals(profileId, this.ActiveRuntime.ProfileId, StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidOperationException("The active profile cannot be deleted. Please switch to another profile first.");
      }

      lock (_SyncRoot) {
        _RetiredRuntimes.RemoveAll((r) => string.Equals(r.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
      }

      _ProfileStore.DeleteProfile(profileId);

    }

    /// <summary>
    /// Activates the given profile: it is remembered for the next start and applied to the running client
    /// (new runtime). Turns which are currently running continue within the previous runtime.
    /// </summary>
    public void ActivateProfile(string profileId) {

      _ProfileStore.ActivateProfile(profileId);

      string activeId = _ProfileStore.GetActiveProfileId();

      lock (_SyncRoot) {

        if (string.Equals(activeId, _ActiveRuntime.ProfileId, StringComparison.OrdinalIgnoreCase)) {
          return;
        }

        HarnessProfileRuntime previous = _ActiveRuntime;

        _RetiredRuntimes.RemoveAll((r) => string.Equals(r.ProfileId, previous.ProfileId, StringComparison.OrdinalIgnoreCase));
        _RetiredRuntimes.Add(previous);

        HarnessProfileRuntime reusable = null;

        if (_DefaultRuntime != null && FileBasedPromptingProfileService.IsDefaultProfileId(activeId)) {
          reusable = _DefaultRuntime;
          _DefaultRuntime = null;
        }

        if (reusable == null) {
          _ActiveRuntime = this.CreateRuntime(activeId);
        }
        else {
          _ActiveRuntime = reusable;
        }

        _RetiredRuntimes.Remove(_ActiveRuntime);

      }

      this.RaiseActiveProfileChanged();

    }

    public bool GetAskAtStartup() {
      return _ProfileStore.GetAskAtStartup();
    }

    public void SetAskAtStartup(bool enabled) {
      _ProfileStore.SetAskAtStartup(enabled);
    }

    #endregion

    private HarnessProfileRuntime CreateRuntime(string profileId) {

      HarnessProfileRuntime runtime = new HarnessProfileRuntime(
        profileId, _ProfileStore.GetProfileDirectory(profileId), _ToolProviderFactories, _ConfigureRuntime
      );

      runtime.ApplyUserLevelGetter(_UserLevelGetter);

      return runtime;
    }

    private void RaiseActiveProfileChanged() {

      EventHandler handler = this.ActiveProfileChanged;

      if (handler != null) {
        handler.Invoke(this, EventArgs.Empty);
      }

    }

  }

}
