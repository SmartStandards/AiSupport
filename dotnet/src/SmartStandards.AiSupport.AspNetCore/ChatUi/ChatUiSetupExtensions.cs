using AI.SmartStandards.InteractivePrompting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.UJMW;

namespace AI.SmartStandards {

  /// <summary>
  /// Setup extensions for the embedded (vanilla HTML/JS) chat UI. The UI files are served from embedded resources
  /// (no wwwroot) and use relative URLs only, so the UI can be mounted at any path (for example '/' or '/chat/').
  /// The implementations of 'IPromptingUIBackend' and 'IPromptingSettingsService' must be registered by the host
  /// within the DI container.
  /// </summary>
  public static class ChatUiSetupExtensions {

    /// <summary>
    /// Route segment (relative to the mount path) of the UJMW endpoint for 'IPromptingUIBackend'.
    /// </summary>
    public const string BackendRouteSegment = "chatApi/backend";

    /// <summary>
    /// Route segment (relative to the mount path) of the UJMW endpoint for 'IPromptingSettingsService'.
    /// </summary>
    public const string SettingsRouteSegment = "chatApi/settings";

    /// <summary>
    /// Route segment (relative to the mount path) of the UJMW endpoint for 'IPromptingExtensionService'.
    /// </summary>
    public const string ExtensionsRouteSegment = "chatApi/extensions";

    private const string _ResourcePrefix = "AiSupport.ChatUi.";

    /// <summary>
    /// Registers the UJMW controllers for the chat UI backend and the settings service under the given mount path.
    /// </summary>
    public static void AddChatUi(this IServiceCollection services, string mountPath = "/") {

      if (services == null) {
        throw new ArgumentNullException(nameof(services));
      }

      string routePrefix = NormalizeRoutePrefix(mountPath);

      services.AddDynamicUjmwControllers((registrar) => {

        registrar.AddControllerFor<IPromptingUIBackend>((options) => {
          options.ControllerRoute = routePrefix + BackendRouteSegment;
          options.ControllerTitle = "Chat UI Backend";
          options.EnableRequestSidechannel = false;
          options.EnableResponseSidechannel = false;
        });

        registrar.AddControllerFor<IPromptingSettingsService>((options) => {
          options.ControllerRoute = routePrefix + SettingsRouteSegment;
          options.ControllerTitle = "Chat UI Settings";
          options.EnableRequestSidechannel = false;
          options.EnableResponseSidechannel = false;
        });

        registrar.AddControllerFor<IPromptingExtensionService>((options) => {
          options.ControllerRoute = routePrefix + ExtensionsRouteSegment;
          options.ControllerTitle = "Chat UI Extensions";
          options.EnableRequestSidechannel = false;
          options.EnableResponseSidechannel = false;
        });

      });

    }

    /// <summary>
    /// Maps the embedded UI files (index.html, chat.js, chat.css) under the given mount path.
    /// </summary>
    public static void MapChatUi(this IEndpointRouteBuilder endpoints, string mountPath = "/") {

      if (endpoints == null) {
        throw new ArgumentNullException(nameof(endpoints));
      }

      string routePrefix = NormalizeRoutePrefix(mountPath);
      Assembly uiAssembly = typeof(ChatUiSetupExtensions).Assembly;

      Dictionary<string, byte[]> files = LoadEmbeddedFiles(uiAssembly);

      foreach (KeyValuePair<string, byte[]> file in files) {

        string fileName = file.Key;
        byte[] content = file.Value;
        string contentType = GetContentType(fileName);

        RequestDelegate handler = (HttpContext context) => WriteFile(context, content, contentType);

        endpoints.MapGet("/" + routePrefix + fileName, handler);

        if (string.Equals(fileName, "index.html", StringComparison.OrdinalIgnoreCase)) {
          endpoints.MapGet("/" + routePrefix, handler);
        }

      }

      if (routePrefix.Length > 0) {

        //relative URLs within the UI require the trailing slash
        string mountWithoutSlash = "/" + routePrefix.TrimEnd('/');
        string mountWithSlash = "/" + routePrefix;

        endpoints.MapGet(mountWithoutSlash, (HttpContext context) => {
          context.Response.Redirect(mountWithSlash);
          return Task.CompletedTask;
        });

      }

    }

    private static Dictionary<string, byte[]> LoadEmbeddedFiles(Assembly uiAssembly) {

      Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

      foreach (string resourceName in uiAssembly.GetManifestResourceNames()) {

        if (!resourceName.StartsWith(_ResourcePrefix, StringComparison.Ordinal)) {
          continue;
        }

        string fileName = resourceName.Substring(_ResourcePrefix.Length);

        using (Stream resourceStream = uiAssembly.GetManifestResourceStream(resourceName)) {
          using (MemoryStream buffer = new MemoryStream()) {
            resourceStream.CopyTo(buffer);
            files[fileName] = buffer.ToArray();
          }
        }

      }

      return files;
    }

    private static Task WriteFile(HttpContext context, byte[] content, string contentType) {

      context.Response.StatusCode = 200;
      context.Response.ContentType = contentType;
      context.Response.Headers["Cache-Control"] = "no-cache";
      context.Response.ContentLength = content.Length;

      return context.Response.Body.WriteAsync(content, 0, content.Length);
    }

    private static string GetContentType(string fileName) {

      string extension = Path.GetExtension(fileName).ToLowerInvariant();

      if (extension == ".html") {
        return "text/html; charset=utf-8";
      }
      else if (extension == ".js") {
        return "text/javascript; charset=utf-8";
      }
      else if (extension == ".css") {
        return "text/css; charset=utf-8";
      }
      else if (extension == ".svg") {
        return "image/svg+xml";
      }

      return "application/octet-stream";
    }

    /// <summary>
    /// Converts a mount path like '/', '/chat' or 'chat/' into a route prefix like '' or 'chat/'.
    /// </summary>
    private static string NormalizeRoutePrefix(string mountPath) {

      if (string.IsNullOrWhiteSpace(mountPath)) {
        return string.Empty;
      }

      string trimmed = mountPath.Trim().Trim('/');

      if (trimmed.Length == 0) {
        return string.Empty;
      }

      return trimmed + "/";
    }

  }

}
