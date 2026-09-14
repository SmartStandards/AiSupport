using Logging.SmartStandards;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace AI.SmartStandards.KnowledgeAccess {

  /// <summary>
  /// Terminates the ASP.NET Core request pipeline for one Joplin WebDAV synchronization
  /// endpoint without involving MVC routing, ApiExplorer, Swagger or formatter-based
  /// content negotiation.
  /// 
  /// The middleware intentionally treats <see cref="HttpRequest.Method"/> as an opaque
  /// protocol token. WebDAV methods such as PROPFIND, MKCOL and MOVE are therefore
  /// dispatched exactly like ordinary HTTP methods once the underlying ASP.NET Core
  /// server has accepted the request.
  /// 
  /// The middleware must be registered before Swagger and endpoint routing middleware.
  /// Requests outside the configured Joplin path are passed to the next middleware
  /// unchanged.
  /// </summary>
  public sealed class JoplinKnowledgeRepositoryWebDavMiddleware {

    private static readonly object _SyncRoot = new object();

    private readonly RequestDelegate _Next;
    private readonly PathString _EndpointPath;

    /// <summary>
    /// Creates the WebDAV protocol middleware.
    /// </summary>
    /// <param name="next">The next ASP.NET Core middleware.</param>
    /// <param name="endpointPath">The absolute application-relative Joplin endpoint path.</param>
    public JoplinKnowledgeRepositoryWebDavMiddleware(
      RequestDelegate next,
      string endpointPath
    ) {
      if (next == null) {
        throw new ArgumentNullException(nameof(next));
      }

      if (string.IsNullOrWhiteSpace(endpointPath)) {
        throw new ArgumentException(
          "A Joplin WebDAV endpoint path is required.",
          nameof(endpointPath)
        );
      }

      _Next = next;
      _EndpointPath = new PathString(
        this.NormalizeEndpointPath(endpointPath)
      );
    }

    /// <summary>
    /// Handles requests below the configured Joplin WebDAV endpoint.
    /// 
    /// The injected repository and Joplin synchronization store are resolved per request,
    /// so both singleton and scoped DI registrations remain valid.
    /// </summary>
    public Task Invoke(
      HttpContext context,
      IKnowledgeRepository knowledgeRepository,
      IJoplinSyncStateStore syncStateStore
    ) {
      if (context == null) {
        throw new ArgumentNullException(nameof(context));
      }

      if (!context.Request.Path.StartsWithSegments(
            _EndpointPath,
            out PathString remainingPath
          )) {
        return _Next(context);
      }

      this.EnableSynchronousIoForWebDav(context);

      lock (_SyncRoot) {
        Task handlingTask = this.HandleRequest(
          context,
          remainingPath,
          knowledgeRepository,
          syncStateStore
        );

        handlingTask.GetAwaiter().GetResult();
        return Task.CompletedTask;
      }
    }

    /// <summary>
    /// Dispatches one raw HTTP/WebDAV method without any MVC verb metadata.
    /// </summary>
    private Task HandleRequest(
      HttpContext context,
      PathString remainingPath,
      IKnowledgeRepository knowledgeRepository,
      IJoplinSyncStateStore syncStateStore
    ) {
      JoplinKnowledgeRepositoryWebDavHandler handler =
        new JoplinKnowledgeRepositoryWebDavHandler(
          knowledgeRepository,
          syncStateStore
        );

      ControllerActionDescriptor actionDescriptor =
        new ControllerActionDescriptor();

      ActionContext actionContext = new ActionContext(
        context,
        new RouteData(),
        actionDescriptor
      );

      ControllerContext controllerContext =
        new ControllerContext(
          actionContext
        );

      handler.ControllerContext = controllerContext;

      string relativePath = this.GetRelativePath(
        remainingPath
      );

      string method = context.Request.Method;

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin WebDAV ASP dispatch: "
        + method
        + " "
        + context.Request.Path.Value
      );

      IActionResult result = this.Dispatch(
        handler,
        method,
        relativePath
      );

      if (result == null) {
        context.Response.Headers["Allow"] =
          "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL, MOVE";

        context.Response.StatusCode =
          StatusCodes.Status405MethodNotAllowed;

        return Task.CompletedTask;
      }

      return result.ExecuteResultAsync(
        actionContext
      );
    }

    /// <summary>
    /// Maps raw method strings to the WebDAV protocol handler.
    /// 
    /// No HTTP-method attribute or endpoint-routing method constraint is involved.
    /// </summary>
    private IActionResult Dispatch(
      JoplinKnowledgeRepositoryWebDavHandler handler,
      string method,
      string relativePath
    ) {
      bool root = string.IsNullOrEmpty(relativePath);

      if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)) {
        if (root) {
          return handler.OptionsRoot();
        }

        return handler.OptionsPath(relativePath);
      }

      if (string.Equals(method, "PROPFIND", StringComparison.OrdinalIgnoreCase)) {
        if (root) {
          return handler.PropFindRoot();
        }

        return handler.PropFindPath(relativePath);
      }

      if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) {
        if (root) {
          return handler.GetRoot();
        }

        return handler.GetPath(relativePath);
      }

      if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)) {
        if (root) {
          return handler.HeadRoot();
        }

        return handler.HeadPath(relativePath);
      }

      if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)) {
        if (root) {
          return new StatusCodeResult(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        return handler.PutPath(relativePath);
      }

      if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)) {
        if (root) {
          return new StatusCodeResult(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        return handler.DeletePath(relativePath);
      }

      if (string.Equals(method, "MKCOL", StringComparison.OrdinalIgnoreCase)) {
        if (root) {
          return new StatusCodeResult(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        return handler.MkColPath(relativePath);
      }

      if (string.Equals(method, "MOVE", StringComparison.OrdinalIgnoreCase)) {
        if (root) {
          return new StatusCodeResult(
            StatusCodes.Status405MethodNotAllowed
          );
        }

        return handler.MovePath(relativePath);
      }

      return null;
    }

    /// <summary>
    /// Enables synchronous body access only for this WebDAV protocol endpoint.
    /// 
    /// The existing protocol handler intentionally follows the project's synchronous
    /// programming model. ASP.NET Core servers may otherwise reject synchronous body IO.
    /// </summary>
    private void EnableSynchronousIoForWebDav(
      HttpContext context
    ) {
      IHttpBodyControlFeature bodyControlFeature =
        context.Features.Get<IHttpBodyControlFeature>();

      if (bodyControlFeature != null) {
        bodyControlFeature.AllowSynchronousIO = true;
      }
    }

    /// <summary>
    /// Converts the remaining ASP.NET Core path to the handler's catch-all path format.
    /// </summary>
    private string GetRelativePath(
      PathString remainingPath
    ) {
      string value = remainingPath.Value;

      if (string.IsNullOrWhiteSpace(value) ||
          string.Equals(value, "/", StringComparison.Ordinal)) {
        return string.Empty;
      }

      return value.TrimStart('/');
    }

    /// <summary>
    /// Normalizes the configured endpoint path.
    /// </summary>
    private string NormalizeEndpointPath(
      string endpointPath
    ) {
      string normalized = endpointPath.Trim();

      if (!normalized.StartsWith("/", StringComparison.Ordinal)) {
        normalized = "/" + normalized;
      }

      if (normalized.Length > 1 &&
          normalized.EndsWith("/", StringComparison.Ordinal)) {
        normalized = normalized.TrimEnd('/');
      }

      return normalized;
    }
  }
}
