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

    private static readonly CalendarGrantStore _CalendarGrantStore = new CalendarGrantStore();

    private static readonly CalendarRequestAuthenticator _CalendarAuthenticator = new CalendarRequestAuthenticator(
      _CalendarGrantStore, "/.well-known/oauth-protected-resource/mcp"
    );

    static partial void OnConfigureServices(IServiceCollection services, IConfiguration config) {

      services.AddSmartStandardsLogging(config);

      string outDir = AppDomain.CurrentDomain.BaseDirectory;

      services.AddControllers();

      UjmwHostConfiguration.EnableApiGroupNameFallback = true;

      UjmwHostConfiguration.AuthHeaderEvaluator = (
        (string rawAuthHeader, Type contractType, MethodInfo targetContractMethod, string callingMachine, ref int httpReturnCode, ref string failedReason) => {
         
          if(contractType == typeof(ICyclicTriggerReceiver)) {
            return true;
          }

          //calendar sample: requires a valid access token with scope 'calendar' (no bypass)
          if (contractType == typeof(ICalendarAccess)) {
            CalendarGrant grant;
            int statusCode;
            string error;
            string errorDescription;
            if (_CalendarAuthenticator.TryAuthorize(rawAuthHeader, out grant, out statusCode, out error, out errorDescription)) {
              return true;
            }
            httpReturnCode = statusCode;
            failedReason = errorDescription;
            return false;
          }

          //in this demo - any auth header is ok - but there must be one ;-)
          if (string.IsNullOrWhiteSpace(rawAuthHeader)) {
            httpReturnCode = 403;
            failedReason = "This demo requires at least ANY string as authheader!";
            return false;
          }
          return true;
        }
      );

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

      services.AddSingleton(_CalendarGrantStore);
      services.AddSingleton(_CalendarAuthenticator);
      services.AddSingleton<ICalDavCredentialSource, AccessTokenCalDavCredentialSource>();

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

      //OAuth authorization server (login = calendar credentials, code flow + PKCE, rotating refresh tokens)
      CalendarOAuthClientRegistry clientRegistry = new CalendarOAuthClientRegistry();
      //optional: encrypted persistence of grants + client registrations (logins survive a restart)
      string oAuthPersistDir = config.GetValue<string>("OAuth:PersistToDir");
      CalendarOAuthStatePersistence oAuthPersistence = null;
      if (!string.IsNullOrWhiteSpace(oAuthPersistDir)) {
        oAuthPersistence = new CalendarOAuthStatePersistence(oAuthPersistDir);
        _CalendarGrantStore.AttachPersistence(oAuthPersistence);
        clientRegistry.AttachPersistence(oAuthPersistence);
        DevLogger.LogInformation("OAuth state is persisted (encrypted) into '" + oAuthPersistence.FilePath + "'.");
      }
      services.AddSingleton(clientRegistry);
      services.AddSingleton<IOAuthService>((sp) => {
        CalendarOAuthService oAuthService = new CalendarOAuthService(
          _CalendarGrantStore,
          sp.GetRequiredService<ICalDavCredentialValidator>(),
          clientRegistry
        );
        if (oAuthPersistence != null) {
          oAuthService.AttachPersistence(oAuthPersistence);
        }
        return oAuthService;
      });
      services.AddSingleton<IAuthPageBuilder>(new DefaultAuthPageBuilder("Calendar Login", "/", "/"));
      services.AddOAuthServerController();

      //RFC 9728 discovery for the MCP resource ('/.well-known/oauth-protected-resource/mcp')
      services.AddWellKnownOAuthProtectedResourceEndpoint((o) => {
        o.ScopesSupported = new string[] { CalendarGrantStore.CalendarScope };
        o.AuthorizationServers = new string[] { "/" };
      });

      services.AddDynamicUjmwControllers((r) => {

        r.AddControllerFor<ICalendarAccess>((options) => {
          options.ControllerRoute = "calendar";
          options.EnableRequestSidechannel = false;
          options.EnableResponseSidechannel = false;
          options.ApiGroupName = "Calendar-Demo";
        });

      });

      services.AddAiSupport((ai) => {
        ai.AddMcpFacade((mcp) => {
          mcp.EndpointPattern = "/mcp";
          mcp.ServerName = "SmartStandards.AiSupport.DemoWebService";
          //mcp.IncludeOperation("calendar_list_calendars", "POST", "calendar/GetCalendars");
          //mcp.IncludeOperation("calendar_list_events", "POST", "calendar/GetEvents");
          mcp.IncludeOperationsFromApiGroup("Calendar-Demo");
          //authentication BEFORE any JSON-RPC processing (real HTTP 401 + Bearer challenge)
          mcp.RequestAuthenticator = _CalendarAuthenticator.Authenticate;
          //DEVELOPMENT ONLY: GET '/mcp?login' runs the OAuth flow like an MCP client and stores the tokens as cookies
          mcp.EnableBrowserLogin = !config.GetValue<bool>("ProdMode");
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
        endpoints.MapControllers();
        endpoints.MapAiSupportMcpFacade();
        endpoints.MapCalendarOAuthEndpoints();
      });

      int tid = Thread.CurrentThread.ManagedThreadId;
      DevLogger.LogDebug($"  [#{tid}]  OnRunApplication COMPLETED!");
    }

  }

}
