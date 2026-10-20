using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

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
        (HttpContext context, McpProtocolHandler handler) => {
          if (!Authenticate(context, options.Value)) {
            return Task.CompletedTask;
          }
          return handler.HandleAsync(context);
        }
      ).WithDisplayName("SmartStandards AiSupport MCP Facade");

      group.MapGet(
        string.Empty,
        (HttpContext context, McpIndexPageRenderer renderer, McpBrowserLoginHandler browserLogin) => {
          bool useBrowserLogin = (
            browserLogin.IsEnabled && options.Value.EnableHumanReadableIndex && !renderer.IsEventStreamRequest(context)
          );
          if (!useBrowserLogin) {
            if (!Authenticate(context, options.Value)) {
              return Task.CompletedTask;
            }
            return renderer.HandleAsync(context);
          }

          //browser login (GET only): may redirect, or applies the token cookie as 'Authorization' header of this request
          McpBrowserLoginResult login = browserLogin.Process(context);
          if (login.ResponseWritten) {
            return Task.CompletedTask;
          }
          bool authenticated = Authenticate(context, options.Value);
          return renderer.HandleBrowserLoginAsync(context, login, authenticated);
        }
      ).WithDisplayName("SmartStandards AiSupport MCP Facade (Tool Overview)");

      return group;
    }

    /// <summary>
    /// Executes the optional request authenticator (before any JSON-RPC processing).
    /// Returns false if the request was rejected (the authenticator has written the HTTP response).
    /// </summary>
    private static bool Authenticate(HttpContext context, McpFacadeOptions options) {
      Func<HttpContext, bool> authenticator = options.RequestAuthenticator;
      if (authenticator == null) {
        return true;
      }
      bool authenticated = authenticator.Invoke(context);
      if (!authenticated && context.Response.StatusCode < 400) {
        //fail closed, if the authenticator forgot to set a rejection status
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
      }
      return authenticated;
    }

  }

}
