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

    internal static WebViewAspNetCoreAdapter AspApplication {
      get {
        return _AspApplication;
      }
    }

    /// <summary>
    /// Entry point.
    /// </summary>
    [STAThread]
    public static void Main() {

      Application.SetHighDpiMode(HighDpiMode.SystemAware);
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);

      FormMain mainWindow = null;

      try {

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
    /// Registers the settings service, the session store, the chat backend and the chat UI.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services, IConfiguration config) {

      FileBasedPromptingSettingsService settingsService = new FileBasedPromptingSettingsService();

      string chatsDirectory = Path.Combine(
        Path.GetDirectoryName(settingsService.SettingsFileFullName), "Chats"
      );

      FileBasedPromptingSessionStore sessionStore = new FileBasedPromptingSessionStore(chatsDirectory);

      //skill sources: built-in 'global-user-preferences' (= the user instructions of the settings, stored as
      //'%USERPROFILE%\.agents\skills\global-user-preferences\SKILL.md'), 'global-system-preferences' and
      //the persisted file/folder references
      FileBasedPromptingExtensionService extensionService = new FileBasedPromptingExtensionService();

      settingsService.UseUserInstructionsFile(
        extensionService.UserPreferencesFileFullName,
        FileBasedPromptingExtensionService.UserPreferencesSourceId,
        "Preferences of the user (conventions, rules). They MUST be read and followed for every task."
      );

      //tools for the model: the internal test tool which switches the UI theme
      HarnessToolbox toolbox = new HarnessToolbox();
      extensionService.RegisterOutOfTheBoxToolProviders();
      extensionService.AttachToolbox(toolbox);
      toolbox.AddTool(new SetUiThemeTool(settingsService));

      //the LLM is created per call (per chat provider/model), so that changed settings are effective immediately
      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(
        sessionStore,
        (IChatLLMFactory)settingsService,
        toolbox,
        () => settingsService.GetConfiguredLLMDisplayName(),
        Environment.UserName
      );

      //'secure availability': the offline copies of the skill sources are refreshed once on every new chat start
      backend.OnNewChatStarting = extensionService.RefreshOfflineCopies;

      services.AddSingleton<IPromptingSettingsService>(settingsService);
      services.AddSingleton<IPromptingExtensionService>(extensionService);
      services.AddSingleton<IPromptingSessionStore>(sessionStore);
      services.AddSingleton<IPromptingUIBackend>(backend);

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
