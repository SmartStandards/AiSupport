using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace AI.SmartStandards {

  /// <summary>
  /// Handles GET requests on the MCP endpoint.
  /// MCP Streamable HTTP uses GET only to open an optional server-to-client SSE stream ('Accept: text/event-stream');
  /// servers that do not offer this stream must answer 405. This facade does not offer the stream, so such requests
  /// get 405 while all other GET requests (browsers) receive a human-readable HTML overview of the offered tools.
  /// </summary>
  internal sealed class McpIndexPageRenderer {

    private readonly IOptions<McpFacadeOptions> _Options;
    private readonly McpToolCatalog _Catalog;

    public McpIndexPageRenderer(IOptions<McpFacadeOptions> options, McpToolCatalog catalog) {
      _Options = options;
      _Catalog = catalog;
    }

    /// <summary>
    /// Answers a GET request on the MCP endpoint.
    /// </summary>
    public Task HandleAsync(HttpContext context) {
      if (this.IsEventStreamRequest(context) || !_Options.Value.EnableHumanReadableIndex) {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers["Allow"] = "POST";
        return Task.CompletedTask;
      }

      string html = this.RenderHtml(context.Request.Path);
      context.Response.StatusCode = StatusCodes.Status200OK;
      context.Response.ContentType = "text/html; charset=utf-8";
      return context.Response.WriteAsync(html, Encoding.UTF8);
    }

    /// <summary>
    /// Returns true if the GET request wants to open a SSE stream (MCP Streamable HTTP) instead of the HTML overview.
    /// </summary>
    public bool IsEventStreamRequest(HttpContext context) {
      string accept = context.Request.Headers["Accept"].ToString();
      return accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Answers a browser GET request when the browser login is enabled. If not authenticated, the status code
    /// (and the Bearer challenge) written by the request authenticator is kept and only the login box is shown.
    /// </summary>
    public Task HandleBrowserLoginAsync(HttpContext context, McpBrowserLoginResult login, bool authenticated) {
      string endpointPath = context.Request.PathBase.Add(context.Request.Path).ToUriComponent();
      string html;
      if (authenticated) {
        html = this.RenderHtml(endpointPath, login, true);
        context.Response.StatusCode = StatusCodes.Status200OK;
      }
      else {
        html = this.RenderHtml(endpointPath, login, false);
      }
      context.Response.Headers.CacheControl = "no-store";
      context.Response.ContentType = "text/html; charset=utf-8";
      return context.Response.WriteAsync(html, Encoding.UTF8);
    }

    /// <summary>
    /// Renders the tool overview as standalone HTML page (all dynamic values are HTML-encoded).
    /// </summary>
    internal string RenderHtml(string endpointPath) {
      return this.RenderHtml(endpointPath, null, true);
    }

    /// <summary>
    /// Renders the page: optional login box + login trace (browser login), tool overview only if authenticated.
    /// </summary>
    private string RenderHtml(string endpointPath, McpBrowserLoginResult login, bool showTools) {
      McpFacadeOptions options = _Options.Value;
      McpToolDefinition[] tools = _Catalog.GetTools();
      McpRejectedOperation[] rejected = _Catalog.GetRejectedOperations();

      StringBuilder html = new StringBuilder();
      html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>");
      html.Append(Encode(options.ServerName));
      html.Append(" - MCP Tools</title><style>");
      html.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:2em;color:#222}");
      html.Append(".login{border:1px solid #ccc;background:#fafafa;padding:8px 12px;margin-bottom:1em}");
      html.Append(".trace{margin:0 0 1em 0}.trace pre{white-space:pre-wrap;word-break:break-all;margin:4px 0 0 0;font-size:12px}");
      html.Append(".Info{border-left:4px solid #999;padding:4px 8px;margin:4px 0}");
      html.Append(".Warning{border-left:4px solid #e0a000;background:#fff8e0;padding:4px 8px;margin:4px 0}");
      html.Append(".Error{border-left:4px solid #c00;background:#ffecec;padding:4px 8px;margin:4px 0}");
      html.Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccc;padding:6px 10px;text-align:left;vertical-align:top}");
      html.Append("th{background:#f0f0f0}code{font-family:Consolas,monospace}.muted{color:#777}ul{margin:0;padding-left:1.2em}");
      html.Append("</style></head><body>");

      html.Append("<h1>").Append(Encode(options.ServerName)).Append(" <span class=\"muted\">").Append(Encode(options.ServerVersion)).Append("</span></h1>");

      if (login != null) {
        this.RenderLoginBox(html, endpointPath, login, showTools);
      }
      if (!showTools) {
        html.Append("</body></html>");
        return html.ToString();
      }

      html.Append("<p>MCP endpoint: <code>POST ").Append(Encode(endpointPath)).Append("</code> (JSON-RPC 2.0, methods <code>initialize</code>, <code>tools/list</code>, <code>tools/call</code>). ");
      html.Append(tools.Length.ToString()).Append(" tool(s) offered.</p>");

      html.Append("<table><thead><tr><th>Tool</th><th>Description</th><th>Arguments</th><th>Underlying operation</th></tr></thead><tbody>");
      foreach (McpToolDefinition tool in tools) {
        html.Append("<tr><td><code>").Append(Encode(tool.Name)).Append("</code></td>");
        html.Append("<td>").Append(Encode(tool.Description)).Append("</td>");
        html.Append("<td>").Append(this.RenderArguments(tool.InputSchema)).Append("</td>");
        html.Append("<td><code>").Append(Encode(tool.Registration.HttpMethod)).Append(" /").Append(Encode(tool.Registration.RelativePath)).Append("</code></td></tr>");
      }
      html.Append("</tbody></table>");

      if (rejected.Length > 0) {
        html.Append("<h2>Not published operations</h2><table><thead><tr><th>Operation</th><th>Reason</th></tr></thead><tbody>");
        foreach (McpRejectedOperation operation in rejected) {
          html.Append("<tr><td><code>").Append(Encode(operation.HttpMethod)).Append(" /").Append(Encode(operation.RelativePath)).Append("</code></td>");
          html.Append("<td>").Append(Encode(operation.Reason)).Append("</td></tr>");
        }
        html.Append("</tbody></table>");
      }

      html.Append("</body></html>");
      return html.ToString();
    }

    /// <summary>
    /// Renders the login state (with login/refresh/logout links) and the trace of the last login steps.
    /// </summary>
    private void RenderLoginBox(StringBuilder html, string endpointPath, McpBrowserLoginResult login, bool authenticated) {
      string encodedPath = Encode(endpointPath);
      html.Append("<div class=\"login\">");
      if (authenticated) {
        if (login.TokenFromCookie) {
          html.Append("Signed in (browser login, access token from HttpOnly cookie). ");
        }
        else {
          html.Append("Authenticated via 'Authorization' header. ");
        }
        html.Append("<a href=\"").Append(encodedPath).Append("?login\">Login again</a>");
        if (login.HasRefreshToken) {
          html.Append(" | <a href=\"").Append(encodedPath).Append("?refresh\">Refresh token</a>");
        }
        html.Append(" | <a href=\"").Append(encodedPath).Append("?logout\">Logout</a>");
      }
      else {
        html.Append("Not authenticated - the MCP endpoint requires an access token. ");
        html.Append("<a href=\"").Append(encodedPath).Append("?login\"><b>Login</b></a> ");
        html.Append("<span class=\"muted\">(runs the same discovery + OAuth code flow with PKCE as MCP clients and shows every step)</span>");
        if (login.HasRefreshToken) {
          html.Append(" | <a href=\"").Append(encodedPath).Append("?logout\">Logout</a>");
        }
      }
      html.Append("</div>");

      McpBrowserLoginTraceEntry[] entries = login.GetEntries();
      if (entries.Length == 0) {
        return;
      }
      html.Append("<div class=\"trace\"><h2>OAuth flow trace</h2>");
      if (login.HasErrors) {
        html.Append("<p><b>The flow FAILED</b> (see the red entries).</p>");
      }
      foreach (McpBrowserLoginTraceEntry entry in entries) {
        html.Append("<div class=\"").Append(entry.Kind.ToString()).Append("\"><b>");
        html.Append(Encode(entry.Kind.ToString().ToUpperInvariant())).Append(": ").Append(Encode(entry.Title)).Append("</b>");
        if (!string.IsNullOrEmpty(entry.Detail)) {
          html.Append("<pre>").Append(Encode(entry.Detail)).Append("</pre>");
        }
        html.Append("</div>");
      }
      html.Append("</div>");
    }

    /// <summary>
    /// Renders the top-level properties of an input schema as list (name, type, required flag, description).
    /// </summary>
    private string RenderArguments(JObject inputSchema) {
      JObject properties = inputSchema["properties"] as JObject;
      if (properties == null || !properties.HasValues) {
        return "<span class=\"muted\">(none)</span>";
      }

      HashSet<string> required = new HashSet<string>(StringComparer.Ordinal);
      JArray requiredArray = inputSchema["required"] as JArray;
      if (requiredArray != null) {
        foreach (JToken entry in requiredArray) {
          required.Add(entry.ToString());
        }
      }

      StringBuilder html = new StringBuilder("<ul>");
      foreach (JProperty property in properties.Properties()) {
        JObject propertySchema = property.Value as JObject;
        html.Append("<li><code>").Append(Encode(property.Name)).Append("</code>");
        if (propertySchema != null) {
          html.Append(" <span class=\"muted\">").Append(Encode(DescribeType(propertySchema))).Append("</span>");
        }
        if (required.Contains(property.Name)) {
          html.Append(" <b>required</b>");
        }
        if (propertySchema != null) {
          string description = propertySchema.Value<string>("description");
          if (!string.IsNullOrWhiteSpace(description)) {
            html.Append(" &ndash; ").Append(Encode(description));
          }
        }
        html.Append("</li>");
      }
      html.Append("</ul>");
      return html.ToString();
    }

    /// <summary>
    /// Returns a short type label for a JSON schema (e.g. "string (date-time)", "array of object").
    /// </summary>
    private static string DescribeType(JObject schema) {
      JToken type = schema["type"];
      string typeText;
      if (type == null) {
        typeText = "any";
      }
      else if (type.Type == JTokenType.Array) {
        List<string> parts = new List<string>();
        foreach (JToken part in (JArray)type) {
          parts.Add(part.ToString());
        }
        typeText = string.Join("|", parts);
      }
      else {
        typeText = type.ToString();
      }

      string format = schema.Value<string>("format");
      if (!string.IsNullOrEmpty(format)) {
        typeText = typeText + " (" + format + ")";
      }
      JObject items = schema["items"] as JObject;
      if (items != null) {
        typeText = typeText + " of " + DescribeType(items);
      }
      return typeText;
    }

    private static string Encode(string value) {
      if (value == null) {
        return string.Empty;
      }
      return WebUtility.HtmlEncode(value);
    }

  }

}
