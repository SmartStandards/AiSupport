using AI.SmartStandards.DemoWebService.Sample.Calendar;
using AI.SmartStandards.InteractivePrompting;
using AI.SmartStandards.InteractivePrompting.Harness;
using AI.SmartStandards.DemoWebService.Sample.OAuth;
using CyclicTriggering;
using KnowledgeManagement.SmartStandards;
using KnowledgeManagement.SmartStandards.Endpoints.Joplin;
using KnowledgeManagement.SmartStandards.Providers;
using KnowledgeManagement.SmartStandards.Wrappers;
using Logging.SmartStandards;
using Logging.SmartStandards.AspSupport;
using Microsoft.AspNetCore;
using Security.AccessTokenHandling;
using Security.AccessTokenHandling.OAuth.Server;
using System.Net;
using System.Reflection;
using System.Web.UJMW;

namespace AI.SmartStandards.DemoWebService {

  public static partial class Program {

    /// <summary>
    /// The scope that is required for the calendar tools.
    /// </summary>
    private const string _CalendarScope = "calendar";

    /// <summary>
    /// The API group (= logical name for the AccessTokenManager) of the calendar sample.
    /// </summary>
    private const string _CalendarApiGroup = "Calendar-Demo";

    /// <summary>
    /// Mount path of the embedded chat UI.
    /// </summary>
    private const string _ChatUiMountPath = "chat";

    /// <summary>
    /// Tool source of the reading calendar tools within the chat.
    /// </summary>
    private const string _CalendarReadToolSourceId = "calendar-read";

    /// <summary>
    /// Tool source of the modifying calendar tools within the chat.
    /// </summary>
    private const string _CalendarWriteToolSourceId = "calendar-write";

    private static IServiceProvider _ServiceProvider;

    private static bool _ChatUiEnabled;

    static partial void OnConfigureServices(IServiceCollection services, IConfiguration config) {

      services.AddSmartStandardsLogging(config);

      string outDir = AppDomain.CurrentDomain.BaseDirectory;

      services.AddControllers();

      UjmwHostConfiguration.EnableApiGroupNameFallback = true;

      UjmwHostConfiguration.SetAuthHeaderEvaluator(
        (
          string rawAuthHeader, Type contractType, MethodInfo targetContractMethod, string apiGroupName, string callingMachine,
          ref int httpReturnCode, ref string failedReason, ref KeyValuePair<string, string>[] responseHeaders
        ) => {

          if (contractType == typeof(ICyclicTriggerReceiver)) {
            return true;
          }

          //the chat UI exposes local settings (incl. API keys) and skill sources of the hosting user:
          //it is only available in development mode and only for local callers
          if (IsChatUiContract(contractType)) {

            if (_ChatUiEnabled && IsLocalCaller(callingMachine)) {
              return true;
            }

            httpReturnCode = 403;
            failedReason = "The chat UI is only available for local callers in development mode.";
            return false;
          }

          //everything else is validated by AuthTokenHandling (token source, required scopes, 401 challenge)
          return AccessTokenValidator.TryValidateHttpAuthHeader(
            rawAuthHeader, contractType, targetContractMethod, apiGroupName, callingMachine,
            ref httpReturnCode, ref failedReason, ref responseHeaders
          );
        }
      );

      //token validation via the AccessTokenManager (the Cheap OAuth Server registers itself as token source 'default');
      //the calendar API group requires the scope 'calendar' (prefixed with the configured API permission prefix)
      AccessTokenValidator.ConfigureTokenValidationViaAccessTokenManager();
      AccessTokenManager.RequireScope(_CalendarScope, _CalendarApiGroup);

      #region " Knowledge WIKI " 

      //AggregatedKnowledgeRepository agg = new AggregatedKnowledgeRepository();
      //agg.Add(new FileBasedKnowledgeRepository("C:\\Temp\\_OneNoteExport", false, true));

      //agg.Add(
      //  new GitBasedKnowledgeRepository(
      //    "https://github.com/SmartStandards/FUSE-fx.RepositoryContract",
      //    true, "","/doc/"
      //  ),
      //  "/FUSE-fx.RepositoryContract/"
      //);

      //services.AddSingleton<IKnowledgeRepository>(agg);

      //services.AddSingleton<IJoplinWebDavAuthenticationValidator>(
      //  new DelegateBasedJoplinWebDavAuthenticationValidator((userName, password, syncId, context) => {
      //    if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) {
      //      return false;
      //    }
      //    return true;
      //  })
      //);

      ////services.AddSingleton<IJoplinSyncStateStore>(
      ////  new FileBasedJoplinSyncStateStore("C:\\Temp\\Joplin")
      ////);

      //services.AddSingleton<IJoplinSyncStateStoreFactory>(
      //  new FileBasedJoplinSyncStateStoreFactory("C:\\Temp\\Joplin")
      //);

      #endregion

      //SAMPLE: CalDAV calendar access (proxy to a configured CalDAV calendar home)
      //The CalDAV credentials are resolved server-side per call from the access token of the current request.
      CalDavCalendarAccessOptions calDavOptions = new CalDavCalendarAccessOptions();

      config.GetSection("CalDav").Bind(calDavOptions);

      services.AddSingleton(calDavOptions);

      services.AddHttpContextAccessor();

      services.AddSingleton<ICalDavCredentialSource, CheapOAuthCalDavCredentialSource>();

      if (calDavOptions.IsMockMode) {
        //LOCAL TESTING ONLY: persists into 'CalDav:MockToLocalDir' and accepts only 'MockAuthUser'/'MockAuthPass'
        //(the complete OAuth + MCP authentication flow stays active)
        services.AddSingleton<LocalDirCalendarAccess>((sp) => new LocalDirCalendarAccess(
          sp.GetRequiredService<CalDavCalendarAccessOptions>(),
          sp.GetRequiredService<ICalDavCredentialSource>()
        ));
        services.AddSingleton<ICalendarAccess>((sp) => sp.GetRequiredService<LocalDirCalendarAccess>());
        services.AddSingleton<ICalDavCredentialValidator>((sp) => sp.GetRequiredService<LocalDirCalendarAccess>());
        DevLogger.LogWarning("CalDAV MOCK MODE is active: calendar data is persisted locally (CalDav:MockToLocalDir).");
      }
      else {
        services.AddSingleton<CalDavCalendarAccess>((sp) => new CalDavCalendarAccess(
          sp.GetRequiredService<CalDavCalendarAccessOptions>(),
          sp.GetRequiredService<ICalDavCredentialSource>()
        ));
        services.AddSingleton<ICalendarAccess>((sp) => sp.GetRequiredService<CalDavCalendarAccess>());
        services.AddSingleton<ICalDavCredentialValidator>((sp) => sp.GetRequiredService<CalDavCalendarAccess>());
      }

      //login page of the Cheap OAuth Server = calendar credentials
      CalDavCheapOAuthLoginValidator loginValidator = new CalDavCheapOAuthLoginValidator(
        () => _ServiceProvider.GetRequiredService<ICalDavCredentialValidator>(),
        new string[] { _CalendarScope }
      );

      //OAuth authorization server (login = calendar credentials, code flow + PKCE, rotating refresh tokens,
      //dynamic client registration, RFC 8414 + RFC 9728 discovery) - provided completely by AuthTokenHandling
      services.AddCheapOAuthServer((o) => {
        o.PersistToDir = config.GetValue<string>("OAuth:PersistToDir");
        o.ScopesSupported = new string[] { _CalendarScope };
        o.LoginPageTitle = "Calendar Login";
        //the CalDAV credentials are retained in the grant (required server-side for the backend calls)
        o.ValidateLogin = loginValidator.ValidateLogin;
      });

      services.AddDynamicUjmwControllers((r) => {

        r.AddControllerFor<ICalendarAccess>((options) => {
          options.ControllerRoute = "calendar";
          options.EnableRequestSidechannel = true;
          options.EnableResponseSidechannel = false;
          options.ApiGroupName = _CalendarApiGroup;
        });

      });

      UjmwHostConfiguration.ConfigureRequestSidechannel((c,cfg) => {

        cfg.AcceptUjmwUnderlineProperty();
        cfg.AcceptNoChannelProvided();
        cfg.ProcessDataVia((m,kvps) => {
          DevLogger.LogInformation("INCOMMING SIDECHANNEL");
          foreach (var kvp in kvps) DevLogger.LogInformation($"{kvp.Key}: {kvp.Value}");
        });


      });

      services.AddAiSupport((ai) => {
        ai.AddMcpFacade((mcp) => {
          mcp.EndpointPattern = "/mcp";
          mcp.ServerName = "SmartStandards.AiSupport.DemoWebService";
          mcp.AddSideChannelFieldDescription("Tenant","the current Tenant ID");
          mcp.AddSideChannelFieldDescription("Legitimation.Salt", "For elevation of permissions - a Salt the ai will generate new for each request");
          mcp.AddSideChannelFieldDescription("Legitimation.Hash", "For elevation of permissions - a MD5 from '<salt><sharedsecret-provided-by-user>'");
          mcp.EnableDiagnosticTracing = config.GetValue<bool>("Mcp:EnableDiagnosticTracing");
          //mcp.IncludeOperation("calendar_list_calendars", "POST", "calendar/GetCalendars");
          //mcp.IncludeOperation("calendar_list_events", "POST", "calendar/GetEvents");
          mcp.IncludeOperationsFromApiGroup(_CalendarApiGroup);
          //DEVELOPMENT ONLY: GET '/mcp?login' runs the OAuth flow like an MCP client and stores the tokens as cookies
          mcp.EnableBrowserLogin = !config.GetValue<bool>("ProdMode");
          //DEVELOPMENT ONLY: local MCP clients (localhost) can connect without the OAuth flow
          //mcp.SkipRequestAuthenticatorForLocalhost = false; !config.GetValue<bool>("ProdMode");
          //no 'RequestAuthenticator': the MCP endpoint is protected via AuthTokenHandling (UC-4) like the published operations
        });
      });

      _ChatUiEnabled = !config.GetValue<bool>("ProdMode");

      if (_ChatUiEnabled) {
        RegisterChatUiServices(
          services, CreateChatCalendarAccess(config, calDavOptions)
        );
        services.AddChatUi(_ChatUiMountPath);
      }

      services.AddSwaggerGenSmartStandardsFlavored();

      int tid = Thread.CurrentThread.ManagedThreadId;
      DevLogger.LogDebug($"  [#{tid}]  OnConfigureServices COMPLETED!");
    }

    static partial void OnRunApplication(
      WebApplication app, IConfiguration config, IServiceProvider services,
      IWebHostEnvironment environment, IHostApplicationLifetime lifetime
    ) {

      _ServiceProvider = services;

      app.UseAmbientFieldAdapterMiddleware();


      ///*
      // * "WebDAV" (NICHT Joplin Server)
      // http://localhost:55202/api/knowledge/joplin
       
      // */
      //app.UseJoplinKnowledgeRepositoryWebDav();

      //required for the www-root
      app.UseStaticFiles();

      if (!config.GetValue<bool>("ProdMode")) {
        app.UseDeveloperExceptionPage();
      }

      string baseUrl = config.GetValue<string>("BaseUrl");

      app.UseHttpsRedirection();

      app.UseRouting();

      //CORS: muss zwischen 'UseRouting' und 'UseEndpoints' liegen!
      app.UseCors((p) => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

      app.UseAuthentication(); //<< WINDOWS-AUTH
      app.UseAuthorization();

      app.UseEndpoints((endpoints) => {
        endpoints.MapControllers(); //incl. OAuth endpoints + discovery (Cheap OAuth Server)
        endpoints.MapAiSupportMcpFacade();

        if (_ChatUiEnabled) {
          endpoints.MapChatUi(_ChatUiMountPath);
        }

      });

      int tid = Thread.CurrentThread.ManagedThreadId;
      DevLogger.LogDebug($"  [#{tid}]  OnRunApplication COMPLETED!");
    }

    /// <summary>
    /// Registers the prompting services required by the embedded chat UI (same wiring as the desktop chat client):
    /// settings (incl. encrypted API keys), chat sessions, skill sources (incl. secure availability) and the backend.
    /// </summary>
    private static void RegisterChatUiServices(IServiceCollection services, ICalendarAccess chatCalendarAccess) {

      FileBasedPromptingSettingsService settingsService = new FileBasedPromptingSettingsService();

      string chatsDirectory = Path.Combine(
        Path.GetDirectoryName(settingsService.SettingsFileFullName), "Chats"
      );

      FileBasedPromptingSessionStore sessionStore = new FileBasedPromptingSessionStore(chatsDirectory);
      FileBasedPromptingExtensionService extensionService = new FileBasedPromptingExtensionService();

      settingsService.UseUserInstructionsFile(
        extensionService.UserPreferencesFileFullName,
        FileBasedPromptingExtensionService.UserPreferencesSourceId,
        "Preferences of the user (conventions, rules). They MUST be read and followed for every task."
      );

      HarnessToolbox toolbox = new HarnessToolbox();
      extensionService.RegisterOutOfTheBoxToolProviders();
      extensionService.AttachToolbox(toolbox);

      if (chatCalendarAccess != null) {
        RegisterCalendarTools(toolbox, chatCalendarAccess);
      }

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

    }

    /// <summary>
    /// Creates a dedicated calendar access for the chat tools. Chat UI requests carry no OAuth access token,
    /// so the CalDAV credentials are configured server-side ('Chat:CalDavUser' / 'Chat:CalDavPassword', for example
    /// via user secrets); in mock mode the mock credentials are used as fallback. Returns null if no credentials
    /// are available (the calendar tools are not offered then). The credentials are never visible to the model.
    /// </summary>
    private static ICalendarAccess CreateChatCalendarAccess(IConfiguration config, CalDavCalendarAccessOptions calDavOptions) {

      string userName = config.GetValue<string>("Chat:CalDavUser");
      string password = config.GetValue<string>("Chat:CalDavPassword");

      if (string.IsNullOrWhiteSpace(userName) && calDavOptions.IsMockMode) {
        userName = calDavOptions.MockAuthUser;
        password = calDavOptions.MockAuthPass;
      }

      if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) {
        DevLogger.LogWarning("Chat UI: no CalDAV credentials configured ('Chat:CalDavUser'/'Chat:CalDavPassword') - the calendar tools are not available within the chat.");
        return null;
      }

      FixedCalDavCredentialSource credentialSource = new FixedCalDavCredentialSource(new CalDavCredentials(userName, password));

      if (calDavOptions.IsMockMode) {
        return new LocalDirCalendarAccess(calDavOptions, credentialSource);
      }

      return new CalDavCalendarAccess(calDavOptions, credentialSource);
    }

    /// <summary>
    /// Publishes the operations of the calendar contract as harness tools (same operations as the MCP facade).
    /// Reading tools are executed without confirmation, modifying tools require a confirmation by default
    /// (can be changed per chat via the tool source levels).
    /// </summary>
    private static void RegisterCalendarTools(HarnessToolbox toolbox, ICalendarAccess calendarAccess) {

      toolbox.AddSource(new HarnessSource(_CalendarReadToolSourceId, true, "Calendar (read)", ToolSourceLevels.NoConfirmation, false));
      toolbox.AddSource(new HarnessSource(_CalendarWriteToolSourceId, true, "Calendar (modify)", ToolSourceLevels.RequiresConfirmation, false));

      ContractMethodHarnessTool[] tools = ContractMethodHarnessTool.CreateTools<ICalendarAccess>(
        () => calendarAccess, "calendar_", null
      );

      foreach (ContractMethodHarnessTool tool in tools) {

        if (tool.Method.Name.StartsWith("Get", StringComparison.Ordinal)) {
          toolbox.AddTool(tool, _CalendarReadToolSourceId);
        }
        else {
          toolbox.AddTool(tool, _CalendarWriteToolSourceId);
        }

      }

    }

    /// <summary>
    /// True, if the given contract is one of the contracts published by the embedded chat UI.
    /// </summary>
    private static bool IsChatUiContract(Type contractType) {

      if (contractType == typeof(IPromptingUIBackend)) {
        return true;
      }

      if (contractType == typeof(IPromptingSettingsService)) {
        return true;
      }

      if (contractType == typeof(IPromptingExtensionService)) {
        return true;
      }

      return false;
    }

    /// <summary>
    /// True, if the calling machine is the local machine (loopback address or 'localhost').
    /// </summary>
    private static bool IsLocalCaller(string callingMachine) {

      if (string.IsNullOrWhiteSpace(callingMachine)) {
        return false;
      }

      if (string.Equals(callingMachine, "localhost", StringComparison.OrdinalIgnoreCase)) {
        return true;
      }

      IPAddress address;
      if (IPAddress.TryParse(callingMachine, out address)) {
        return IPAddress.IsLoopback(address);
      }

      return false;
    }

  }

}
