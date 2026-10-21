# Quickstart

## Let an LLM "implement" an interface

Prerequisites: an OpenAI API key and a reference to `SmartStandards.AiSupport.CommonProviders`.

```csharp
using AI.SmartStandards.LowLevelPrompting;
using AI.SmartStandards.UjmwSupport;

public interface IMyTool {
  /// <summary>Multiplies the difference of two numbers by itself.</summary>
  int SquareTheDifference(int numberA, int numberB);
}

DynamicAiServiceFactory.AiOperationsProvider = new OpenAiLLMConnector("<OPENAI_API_KEY>");

IMyTool tool = DynamicAiServiceFactory.CreateInstance<IMyTool>();
int result = tool.SquareTheDifference(3, 7); // expected: 16
```

The method name, XML doc comments and arguments become the prompt. Meaningful comments improve the result.
The mode `ImplementationMode.AiGeneratedInmemoryCode` is prepared but not yet implemented.

## Call an LLM directly

```csharp
ICommonLLM llm = new OpenAiLLMConnector("<OPENAI_API_KEY>");

string text = llm.CallWebSearchApi("Summarize the latest changes in .NET 10 in 5 bullet points.");

MyDto[] data = llm.CallWebSearchApi<MyDto[]>("Provide 3 examples …", inputData: new { Topic = "…" });

byte[] png = llm.CallImageGeneratorApi("A lighthouse in the morning fog");
```

## Run the demo web service

1. Open `dotnet/SmartStandards.AiSupport.sln` and select `SmartStandards.AiSupport.DemoWebService` as the startup project.
2. Adjust the local paths configured in `Program.cs` (e.g. `C:\Temp\_OneNoteExport`, `C:\Temp\Joplin`).
3. Start it. The UJMW endpoints are available via Swagger. The demo requires any non-empty `Authorization` header.

## Expose selected controller actions as MCP tools

Register the facade and explicitly include the API operations that should become tools:

```csharp
services.AddControllers();
services.AddAiSupport((ai) => {
  ai.AddMcpFacade((mcp) => {
    mcp.EndpointPattern = "/mcp";
    mcp.IncludeOperation(
      "get_recent_chats",
      "GET",
      "api/prompting/recent-chats",
      "Returns the most recent prompting chats."
    );
  });
});
```

Map controllers and the MCP facade through endpoint routing:

```csharp
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.UseEndpoints((endpoints) => {
  endpoints.MapControllers();
  endpoints.MapAiSupportMcpFacade();
});
```

The MCP endpoint supports `initialize`, `tools/list` and `tools/call`. Tool arguments are generated from ASP.NET Core
metadata for route, query and JSON-body parameters. Form, file, multipart and streaming shapes are rejected instead of
being exposed as misleading tools.

## Diagnose MCP authentication and hanging tool calls

MCP diagnostic tracing is an opt-in library feature for distinguishing authentication failures, blocked controller
execution and response handling. Unlike raw HTTP payload logging, it correlates the facade's internal phases without
recording tokens, cookies, query strings, arguments or response bodies. It does not change authentication, OAuth
recovery, cancellation or tool error behavior.

### Enable tracing

In the demo, set `Mcp:EnableDiagnosticTracing` to `true` in `appsettings.Development.json` or configuration overrides.
The checked-in default is `false`. Restart the service manually after changing configuration. For other hosts, set
`McpFacadeOptions.EnableDiagnosticTracing = true` in the existing `AddMcpFacade` configuration delegate.

The SmartStandards logging sink must accept Trace: `Logging:SmartStandards:FileLogging:DefaultLogLevelFilter = 0`.
The demo file sink already does. To see events on the console too, set
`Logging:SmartStandards:ConsoleLogging:DefaultLogLevelFilter = 0`; its default Information filter hides these events.
The file target remains `logs\{application}-{timestamp:yyyy-MM-dd}.log`, resolved by the host logging configuration.

Filter for event kinds **75300-75399** or `MCP request=`. Each HTTP request gets a server-generated correlation ID,
an event stage, cumulative `elapsedMs`, current HTTP status and response-started flag. Successful POSTs, rejected
requests, browser GETs and stream requests all enter the same lifecycle tracing. A browser login redirect and the
subsequent callback are separate HTTP requests and therefore have separate IDs.

### Interpret the last phase

| Last event or event pair | Interpretation and next check |
| --- | --- |
| No `McpRequestStarted` | Check that tracing and the sink are enabled, then verify the request reaches this facade. Failures in earlier middleware or service activation are outside its scope. |
| `McpAuthenticationStarted` without `McpAuthenticationCompleted` | Inspect token validation, authentication requirement resolution or the configured authenticator. |
| `McpAuthenticationCompleted` with `authenticated=False` | Inspect HTTP status and `challengePresent`; challenge contents and tokens are deliberately omitted. |
| `McpAuthenticationBypassed` | The development-only localhost bypass was used; target authorization still applies. |
| `McpRequestBodyReading` without `McpRequestBodyRead` | The request body is still being read, or reading/parsing failed; look for `McpProtocolFailure`. |
| `McpProtocolDispatch` | Identifies `initialize`, `tools/list`, `tools/call` or `unsupported`, without echoing arbitrary client method names. |
| `McpTargetAuthorizationStarted` without its completion | Inspect ASP.NET Core target policy authorization. |
| `McpTargetInvocationStarted` without `McpTargetInvocationCompleted` | The MVC action or its filters/backend have not returned. Pause the debugger and inspect thread stacks. |
| `McpToolResultReading` without `McpToolCompleted` | Inspect result conversion and `McpToolFailure`, including a target HTTP 401. |
| `McpResponseWriting` without `McpResponseWritten` | Inspect response serialization, transport writes and request cancellation. |
| `McpResponseWritten`, then `McpRequestCompleted` | The server wrote the JSON-RPC response and the transport reported completion. This does not prove the IDE consumed or accepted it. |
| `McpRequestAborted` | ASP.NET Core observed cancellation/client disconnection. It does not prove a timeout or identify which side caused it. |

For an intermittent refresh failure, enable tracing, reconnect the MCP client and invoke one read-only calendar tool.
Group events by `request`, then compare the elapsed times of adjacent phases. Preserve each HTTP status: a tool
result can be an MCP error inside HTTP 200, while target authentication can produce a real HTTP 401. Inspect the
existing AuthTokenHandling logs separately for discovery, registration and token grants; those endpoints are not
instrumented by this facade. Never attach raw access/refresh tokens or backend credentials to a diagnostic report.

### Diagnostic contract and implementation

| Requirement | Behavior |
| --- | --- |
| Explicit opt-in | `EnableDiagnosticTracing` defaults to `false`; no request state or lifecycle callbacks are registered while disabled. |
| Stable event classification | Internal `MyLogEventKind` uses the user-approved range 75300-75399, with static Snowflake44 source-line IDs. |
| Secret protection | Trace output contains only server-owned phase metadata, registered tool names, booleans, status codes and exception type names. It excludes payloads, exception messages and client-supplied JSON-RPC IDs. |
| Existing logging | Events use the existing `DevLogger` integration; no additional logging package is required. |
| No protocol changes | Existing responses and authentication remain unchanged; diagnostics do not add retries or timeouts. |

`McpDiagnosticTrace` owns request correlation, timing and transport callbacks. `McpEndpointRouteBuilderExtensions`
records request admission, browser login and authentication. `McpProtocolHandler` records JSON-RPC phases and
response writing. `McpToolInvoker` records target authorization, invocation and result conversion.
`McpDiagnosticTraceTest` covers opt-in behavior, correlation, cancellation cleanup, rejection and payload secrecy.

Disable the option after collecting the relevant traces to avoid unnecessary log volume. Existing application and
dependency logs have their own content policies; enabling this feature does not redact those unrelated logs.
