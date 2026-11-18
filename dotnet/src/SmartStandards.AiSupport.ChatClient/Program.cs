using AI.SmartStandards.InteractivePrompting;
using AI.SmartStandards.InteractivePrompting.Harness;
using AI.SmartStandards.LowLevelPrompting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace AI.SmartStandards.ChatClient {

  /// <summary>
  /// Desktop chat client: hosts the chat UI (embedded resources) within an in-memory ASP.NET Core engine,
  /// which is displayed via WebView2. Settings (incl. API keys) and chats are stored within the roaming profile.
  /// </summary>
  public static class Program {

    private static WebViewAspNetCoreAdapter _AspApplication;

    private static HarnessProfileManager _ProfileManager;

    internal static WebViewAspNetCoreAdapter AspApplication {
      get {
        return _AspApplication;
      }
    }

    internal static HarnessProfileManager ProfileManager {
      get {
        return _ProfileManager;
      }
    }

    /// <summary>
    /// Entry point. Optional argument: '--profile <id or name>' starts the client with the given profile (PRF-8).
    /// </summary>
    [STAThread]
    public static void Main(string[] args) {

      Application.SetHighDpiMode(HighDpiMode.SystemAware);
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);

      FormMain mainWindow = null;

      try {

        string startupProfileId = ResolveStartupProfile(args);

        if (startupProfileId == null) {
          return;
        }

        _ProfileManager = new HarnessProfileManager(
          new FileBasedPromptingProfileService(), startupProfileId, null
        );

        _AspApplication = new WebViewAspNetCoreAdapter(
          Assembly.GetExecutingAssembly(),
          (services, config) => ConfigureServices(services, config),
          (app, config, serviceProvider, environment, lifetime) => ConfigureApplication(app)
        );

        mainWindow = new FormMain();

        Application.Run(mainWindow);

      }
      finally {

        if (mainWindow != null) {
          mainWindow.Dispose();
        }

        if (_AspApplication != null) {
          _AspApplication.Dispose();
        }

      }

    }

    /// <summary>
    /// Determines the profile to start with: '--profile <id or name>' (PRF-8), otherwise the selection dialog
    /// (if enabled and more than one profile exists, PRF-6), otherwise the remembered profile (empty string).
    /// Returns null if the user cancelled the selection.
    /// </summary>
    private static string ResolveStartupProfile(string[] args) {

      FileBasedPromptingProfileService profileStore = new FileBasedPromptingProfileService();

      string requested = GetArgumentValue(args, "--profile");

      if (!string.IsNullOrWhiteSpace(requested)) {

        HarnessProfileInfo profile = profileStore.FindProfile(requested);

        if (profile != null) {
          return profile.ProfileId;
        }

        MessageBox.Show(
          $"The profile '{requested}' does not exist.",
          "Prompting UI", MessageBoxButtons.OK, MessageBoxIcon.Error
        );

        return null;
      }

      HarnessProfileInfo[] profiles = profileStore.GetProfiles();

      if (profiles.Length < 2 || !profileStore.GetAskAtStartup()) {
        return string.Empty;
      }

      using (FormProfileSelection dialog = new FormProfileSelection(profiles)) {

        if (dialog.ShowDialog() != DialogResult.OK) {
          return null;
        }

        //the selection of the start dialog is remembered (PRF-7)
        profileStore.ActivateProfile(dialog.SelectedProfileId);

        return string.Empty;
      }

    }

    private static string GetArgumentValue(string[] args, string name) {

      if (args == null) {
        return null;
      }

      for (int i = 0; i < args.Length; i++) {

        string arg = args[i];

        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) {
          return args[i + 1];
        }

        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) {
          return arg.Substring(name.Length + 1);
        }

      }

      return null;
    }

    /// <summary>
    /// Registers the profile-aware services (they forward to the runtime of the active profile) and the chat UI.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services, IConfiguration config) {

      services.AddSingleton<IPromptingProfileService>(_ProfileManager);
      services.AddSingleton<IPromptingSettingsService>(new ActiveProfileSettingsService(_ProfileManager));
      services.AddSingleton<IPromptingExtensionService>(new ActiveProfileExtensionService(_ProfileManager));
      services.AddSingleton<IPromptingUIBackend>(new ActiveProfileUIBackend(_ProfileManager));

      //WebView2 + System.Text.Json has a known issue within the TestServer pipeline (see template)
      services.AddControllers().AddNewtonsoftJson();

      services.AddChatUi("/");

    }

    /// <summary>
    /// Configures the request pipeline.
    /// </summary>
    private static void ConfigureApplication(WebApplication app) {

      app.UseRouting();

      app.MapControllers();
      app.MapChatUi("/");

    }

  }

}
