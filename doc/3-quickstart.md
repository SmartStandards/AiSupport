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
