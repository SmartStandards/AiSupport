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
      string accept = context.Request.Headers["Accept"].ToString();
      bool wantsEventStream = accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;

      if (wantsEventStream || !_Options.Value.EnableHumanReadableIndex) {
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
    /// Renders the tool overview as standalone HTML page (all dynamic values are HTML-encoded).
    /// </summary>
    internal string RenderHtml(string endpointPath) {
      McpFacadeOptions options = _Options.Value;
      McpToolDefinition[] tools = _Catalog.GetTools();
      McpRejectedOperation[] rejected = _Catalog.GetRejectedOperations();

      StringBuilder html = new StringBuilder();
      html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>");
      html.Append(Encode(options.ServerName));
      html.Append(" - MCP Tools</title><style>");
      html.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:2em;color:#222}");
      html.Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccc;padding:6px 10px;text-align:left;vertical-align:top}");
      html.Append("th{background:#f0f0f0}code{font-family:Consolas,monospace}.muted{color:#777}ul{margin:0;padding-left:1.2em}");
      html.Append("</style></head><body>");

      html.Append("<h1>").Append(Encode(options.ServerName)).Append(" <span class=\"muted\">").Append(Encode(options.ServerVersion)).Append("</span></h1>");
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
