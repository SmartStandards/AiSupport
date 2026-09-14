using Microsoft.AspNetCore.Builder;
using System;

namespace AI.SmartStandards.KnowledgeAccess {

  /// <summary>
  /// Provides ASP.NET Core pipeline registration for the Joplin knowledge-repository
  /// WebDAV endpoint.
  /// </summary>
  public static class JoplinKnowledgeRepositoryWebDavExtensions {

    /// <summary>
    /// Registers the Joplin WebDAV protocol middleware.
    /// 
    /// This call should be placed before Swagger and endpoint-routing/controller
    /// middleware. The middleware short-circuits only requests below the supplied path and
    /// leaves every other request unchanged.
    /// </summary>
    /// <param name="app">The ASP.NET Core application builder.</param>
    /// <param name="endpointPath">
    /// The application-relative path exposed to Joplin, for example
    /// "/api/knowledge/joplin".
    /// </param>
    /// <returns>The original application builder.</returns>
    public static IApplicationBuilder UseJoplinKnowledgeRepositoryWebDav(
      this IApplicationBuilder app,
      string endpointPath = "/api/knowledge/joplin"
    ) {
      if (app == null) {
        throw new ArgumentNullException(nameof(app));
      }

      if (string.IsNullOrWhiteSpace(endpointPath)) {
        throw new ArgumentException(
          "A Joplin WebDAV endpoint path is required.",
          nameof(endpointPath)
        );
      }

      app.UseMiddleware<JoplinKnowledgeRepositoryWebDavMiddleware>(
        endpointPath
      );

      return app;
    }
  }
}
