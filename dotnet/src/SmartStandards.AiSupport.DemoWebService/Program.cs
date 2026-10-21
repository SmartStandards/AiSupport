using AI.SmartStandards.DemoWebService.Sample.Calendar;
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

    private static IServiceProvider _ServiceProvider;

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
          options.EnableRequestSidechannel = false;
          options.EnableResponseSidechannel = false;
          options.ApiGroupName = _CalendarApiGroup;
        });

      });

      services.AddAiSupport((ai) => {
        ai.AddMcpFacade((mcp) => {
          mcp.EndpointPattern = "/mcp";
          mcp.ServerName = "SmartStandards.AiSupport.DemoWebService";
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
      });

      int tid = Thread.CurrentThread.ManagedThreadId;
      DevLogger.LogDebug($"  [#{tid}]  OnRunApplication COMPLETED!");
    }

  }

}
