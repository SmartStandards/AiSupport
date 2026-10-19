using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AI.SmartStandards {

  internal sealed class McpProtocolHandler {

    private const string JsonRpcVersion = "2.0";
    private const string DefaultProtocolVersion = "2025-11-25";

    private readonly IOptions<McpFacadeOptions> _Options;
    private readonly McpToolCatalog _ToolCatalog;
    private readonly McpToolInvoker _ToolInvoker;

    public McpProtocolHandler(IOptions<McpFacadeOptions> options, McpToolCatalog toolCatalog, McpToolInvoker toolInvoker) {
      _Options = options;
      _ToolCatalog = toolCatalog;
      _ToolInvoker = toolInvoker;
    }

    public async Task HandleAsync(HttpContext context) {
      JObject request;
      try {
        request = await this.ReadRequestAsync(context).ConfigureAwait(false);
      }
      catch (Exception ex) {
        JObject parseError = this.CreateErrorResponse(null, -32700, "Invalid JSON-RPC request: " + ex.Message);
        await this.WriteJsonAsync(context, StatusCodes.Status400BadRequest, parseError).ConfigureAwait(false);
        return;
      }

      JToken id = request["id"];
      if (id == null) {
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        return;
      }

      string method = request.Value<string>("method");
      if (string.IsNullOrWhiteSpace(method)) {
        JObject methodError = this.CreateErrorResponse(id, -32600, "JSON-RPC method is missing.");
        await this.WriteJsonAsync(context, StatusCodes.Status400BadRequest, methodError).ConfigureAwait(false);
        return;
      }

      try {
        JObject response = await this.DispatchAsync(context, id, method, request["params"] as JObject).ConfigureAwait(false);
        await this.WriteJsonAsync(context, StatusCodes.Status200OK, response).ConfigureAwait(false);
      }
      catch (Exception ex) {
        JObject errorResponse = this.CreateErrorResponse(id, -32603, ex.Message);
        await this.WriteJsonAsync(context, StatusCodes.Status500InternalServerError, errorResponse).ConfigureAwait(false);
      }
    }

    private async Task<JObject> DispatchAsync(HttpContext context, JToken id, string method, JObject parameters) {
      if (string.Equals(method, "initialize", StringComparison.Ordinal)) {
        return this.CreateInitializeResponse(id, parameters);
      }
      if (string.Equals(method, "tools/list", StringComparison.Ordinal)) {
        return this.CreateToolsListResponse(id);
      }
      if (string.Equals(method, "tools/call", StringComparison.Ordinal)) {
        return await this.CreateToolsCallResponseAsync(context, id, parameters).ConfigureAwait(false);
      }

      return this.CreateErrorResponse(id, -32601, "Unsupported MCP method '" + method + "'.");
    }

    private JObject CreateInitializeResponse(JToken id, JObject parameters) {
      string protocolVersion = DefaultProtocolVersion;
      if (parameters != null && !string.IsNullOrWhiteSpace(parameters.Value<string>("protocolVersion"))) {
        protocolVersion = parameters.Value<string>("protocolVersion");
      }

      JObject result = new JObject();
      result["protocolVersion"] = protocolVersion;
      result["capabilities"] = new JObject(
        new JProperty("tools", new JObject(new JProperty("listChanged", false)))
      );
      result["serverInfo"] = new JObject(
        new JProperty("name", _Options.Value.ServerName),
        new JProperty("version", _Options.Value.ServerVersion)
      );

      return this.CreateSuccessResponse(id, result);
    }

    private JObject CreateToolsListResponse(JToken id) {
      JArray tools = new JArray();
      McpToolDefinition[] definitions = _ToolCatalog.GetTools();
      foreach (McpToolDefinition definition in definitions) {
        JObject tool = new JObject();
        tool["name"] = definition.Name;
        tool["description"] = definition.Description;
        tool["inputSchema"] = definition.InputSchema;
        tools.Add(tool);
      }

      JObject result = new JObject();
      result["tools"] = tools;
      return this.CreateSuccessResponse(id, result);
    }

    private async Task<JObject> CreateToolsCallResponseAsync(HttpContext context, JToken id, JObject parameters) {
      if (parameters == null) {
        return this.CreateErrorResponse(id, -32602, "tools/call parameters are missing.");
      }

      string toolName = parameters.Value<string>("name");
      if (string.IsNullOrWhiteSpace(toolName)) {
        return this.CreateErrorResponse(id, -32602, "tools/call parameter 'name' is missing.");
      }

      JObject arguments = parameters["arguments"] as JObject;
      if (arguments == null) {
        arguments = new JObject();
      }

      McpToolDefinition tool = _ToolCatalog.GetTool(toolName);
      if (tool == null) {
        return this.CreateErrorResponse(id, -32602, "Unknown MCP tool '" + toolName + "'.");
      }

      McpToolCallResult callResult = await _ToolInvoker.InvokeAsync(context, tool, arguments).ConfigureAwait(false);

      JObject result = new JObject();
      result["content"] = new JArray(
        new JObject(
          new JProperty("type", "text"),
          new JProperty("text", callResult.Text)
        )
      );
      result["structuredContent"] = callResult.StructuredContent;
      result["isError"] = callResult.IsError;
      return this.CreateSuccessResponse(id, result);
    }

    private async Task<JObject> ReadRequestAsync(HttpContext context) {
      using (StreamReader reader = new StreamReader(context.Request.Body, Encoding.UTF8, true, 1024, true)) {
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) {
          throw new InvalidOperationException("The request body is empty.");
        }
        JToken token = JToken.Parse(body);
        JObject request = token as JObject;
        if (request == null) {
          throw new InvalidOperationException("The request body must be a JSON object.");
        }
        return request;
      }
    }

    private JObject CreateSuccessResponse(JToken id, JObject result) {
      JObject response = new JObject();
      response["jsonrpc"] = JsonRpcVersion;
      response["id"] = id.DeepClone();
      response["result"] = result;
      return response;
    }

    private JObject CreateErrorResponse(JToken id, int code, string message) {
      JObject error = new JObject();
      error["code"] = code;
      error["message"] = message;

      JObject response = new JObject();
      response["jsonrpc"] = JsonRpcVersion;
      if (id == null) {
        response["id"] = null;
      }
      else {
        response["id"] = id.DeepClone();
      }
      response["error"] = error;
      return response;
    }

    private async Task WriteJsonAsync(HttpContext context, int statusCode, JObject response) {
      context.Response.StatusCode = statusCode;
      context.Response.ContentType = "application/json";
      string json = response.ToString(Formatting.None);
      await context.Response.WriteAsync(json).ConfigureAwait(false);
    }

  }

  internal sealed class McpToolCallResult {

    private readonly bool _IsError;
    private readonly string _Text;
    private readonly JToken _StructuredContent;

    public McpToolCallResult(bool isError, string text, JToken structuredContent) {
      _IsError = isError;
      _Text = text;
      _StructuredContent = structuredContent;
    }

    public bool IsError {
      get {
        return _IsError;
      }
    }

    public string Text {
      get {
        return _Text;
      }
    }

    public JToken StructuredContent {
      get {
        return _StructuredContent;
      }
    }

  }

}
