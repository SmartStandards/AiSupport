using AI.SmartStandards.InteractivePrompting;
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

      //the LLM is created per call, so that changed settings are effective immediately
      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(
        sessionStore,
        () => settingsService.CreateConfiguredLLM(),
        () => settingsService.GetConfiguredLLMDisplayName(),
        Environment.UserName
      );

      services.AddSingleton<IPromptingSettingsService>(settingsService);
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
