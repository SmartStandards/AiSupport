using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AI.SmartStandards {

  public static class McpEndpointRouteBuilderExtensions {

    /// <summary>
    /// Maps the MCP facade: POST = MCP JSON-RPC, GET = human-readable tool overview (or 405 for SSE stream requests).
    /// </summary>
    public static IEndpointConventionBuilder MapAiSupportMcpFacade(this IEndpointRouteBuilder endpoints) {
      IOptions<McpFacadeOptions> options = endpoints.ServiceProvider.GetRequiredService<IOptions<McpFacadeOptions>>();
      string endpointPattern = options.Value.EndpointPattern;

      RouteGroupBuilder group = endpoints.MapGroup(endpointPattern);
      group.WithGroupName("MCP");

      group.MapPost(
        string.Empty,
        (HttpContext context, McpProtocolHandler handler) => handler.HandleAsync(context)
      ).WithDisplayName("SmartStandards AiSupport MCP Facade");

      group.MapGet(
        string.Empty,
        (HttpContext context, McpIndexPageRenderer renderer) => renderer.HandleAsync(context)
      ).WithDisplayName("SmartStandards AiSupport MCP Facade (Tool Overview)");

      return group;
    }

  }

}
