using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// TLM-2: publishes the tools of a remote MCP server (Streamable HTTP transport, JSON-RPC 2.0) as harness tools.
  /// One instance per registered server. Tool names are '{prefix}__{remote name}' (sanitized).
  /// The optional token of the source is sent as bearer token.
  /// </summary>
  public class McpToolProvider : IHarnessToolProvider {

    private const string _ProtocolVersion = "2025-03-26";

    private static readonly HttpClient _HttpClient = CreateHttpClient();

    private readonly string _ToolNamePrefix;

    private readonly object _SyncRoot = new object();

    private string _SessionId;

    private int _RequestId;

    public McpToolProvider(string toolNamePrefix) {

      if (string.IsNullOrWhiteSpace(toolNamePrefix)) {
        throw new ArgumentNullException(nameof(toolNamePrefix));
      }

      _ToolNamePrefix = SanitizeName(toolNamePrefix);

    }

    public string ProviderId {
      get {
        return "mcp";
      }
    }

    public string DisplayName {
      get {
        return "MCP server";
      }
    }

    public string DefaultLevel {
      get {
        return ToolSourceLevels.RequiresConfirmation;
      }
    }

    public bool SupportsCredential {
      get {
        return true;
      }
    }

    public string GetDefaultSettingsJson() {
      return "{}";
    }

    public string GetSettingsUiHtml() {
      return null;
    }

    public string ValidateSettingsJson(string settingsJson) {

      if (string.IsNullOrWhiteSpace(settingsJson)) {
        return "{}";
      }

      try {
        return JObject.Parse(settingsJson).ToString(Formatting.Indented);
      }
      catch (JsonException ex) {
        throw new ArgumentException("The settings are not a valid JSON object: " + ex.Message);
      }

    }

    /// <summary>
    /// Initializes the session and lists the remote tools (throws if the server is not reachable).
    /// </summary>
    public IHarnessTool[] CreateTools(IToolProviderContext context) {

      if (string.IsNullOrWhiteSpace(context.Location)) {
        throw new InvalidOperationException("The URL of the MCP server is missing.");
      }

      lock (_SyncRoot) {
        _SessionId = null;
      }

      this.Initialize(context);

      JObject result = this.Send(context, "tools/list", new JObject());
      JArray remoteTools = result["tools"] as JArray;
      List<IHarnessTool> tools = new List<IHarnessTool>();

      if (remoteTools == null) {
        return tools.ToArray();
      }

      foreach (JToken remoteTool in remoteTools) {

        string remoteName = remoteTool.Value<string>("name");

        if (string.IsNullOrWhiteSpace(remoteName)) {
          continue;
        }

        string description = remoteTool.Value<string>("description");
        string schema = "{\"type\":\"object\"}";

        if (remoteTool["inputSchema"] != null && remoteTool["inputSchema"].Type == JTokenType.Object) {
          schema = remoteTool["inputSchema"].ToString(Formatting.None);
        }

        string localName = _ToolNamePrefix + "__" + SanitizeName(remoteName);

        tools.Add(new DelegateHarnessTool(localName, description, schema, (arguments) => this.CallTool(context, remoteName, arguments)));
      }

      return tools.ToArray();
    }

    private void Initialize(IToolProviderContext context) {

      JObject parameters = new JObject();
      parameters["protocolVersion"] = _ProtocolVersion;
      parameters["capabilities"] = new JObject();
      parameters["clientInfo"] = new JObject(new JProperty("name", "SmartStandards.AiSupport"), new JProperty("version", "1.0"));

      this.Send(context, "initialize", parameters);
      this.Notify(context, "notifications/initialized");

    }

    private string CallTool(IToolProviderContext context, string remoteName, JObject arguments) {

      JObject parameters = new JObject();
      parameters["name"] = remoteName;
      parameters["arguments"] = arguments;

      JObject result = this.Send(context, "tools/call", parameters);
      StringBuilder text = new StringBuilder();
      JArray content = result["content"] as JArray;

      if (content != null) {

        foreach (JToken item in content) {

          if (text.Length > 0) {
            text.AppendLine();
          }

          if (string.Equals(item.Value<string>("type"), "text", StringComparison.Ordinal)) {
            text.Append(item.Value<string>("text"));
          }
          else {
            text.Append(item.ToString(Formatting.None));
          }

        }

      }

      if (result["structuredContent"] != null && text.Length == 0) {
        text.Append(result["structuredContent"].ToString(Formatting.None));
      }

      if (result["isError"] != null && result["isError"].Type == JTokenType.Boolean && result["isError"].Value<bool>()) {
        throw new InvalidOperationException(text.ToString());
      }

      return text.ToString();
    }

    private JObject Send(IToolProviderContext context, string method, JObject parameters) {

      int id;

      lock (_SyncRoot) {
        _RequestId++;
        id = _RequestId;
      }

      JObject request = new JObject();
      request["jsonrpc"] = "2.0";
      request["id"] = id;
      request["method"] = method;
      request["params"] = parameters;

      string responseText = this.Post(context, request);
      JObject response = ParseResponse(responseText, id);

      if (response["error"] != null && response["error"].Type == JTokenType.Object) {
        throw new InvalidOperationException("MCP error: " + response["error"].Value<string>("message"));
      }

      JObject result = response["result"] as JObject;

      if (result == null) {
        return new JObject();
      }

      return result;
    }

    private void Notify(IToolProviderContext context, string method) {

      JObject request = new JObject();
      request["jsonrpc"] = "2.0";
      request["method"] = method;

      this.Post(context, request);

    }

    private string Post(IToolProviderContext context, JObject body) {

      using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, context.Location)) {

        message.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        string credential = context.Credential;

        if (!string.IsNullOrWhiteSpace(credential)) {
          message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        string sessionId;

        lock (_SyncRoot) {
          sessionId = _SessionId;
        }

        if (sessionId != null) {
          message.Headers.Add("Mcp-Session-Id", sessionId);
        }

        using (HttpResponseMessage response = _HttpClient.SendAsync(message).Result) {

          IEnumerable<string> sessionHeaders;

          if (response.Headers.TryGetValues("Mcp-Session-Id", out sessionHeaders)) {

            lock (_SyncRoot) {
              _SessionId = sessionHeaders.FirstOrDefault();
            }

          }

          string text = response.Content.ReadAsStringAsync().Result;

          if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException($"The MCP server answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
          }

          return text;
        }

      }

    }

    /// <summary>
    /// Parses a JSON-RPC response which is either plain JSON or a Server-Sent-Events stream ('data:' lines).
    /// </summary>
    internal static JObject ParseResponse(string responseText, int id) {

      if (string.IsNullOrWhiteSpace(responseText)) {
        throw new InvalidOperationException("The MCP server returned an empty response.");
      }

      string trimmed = responseText.TrimStart();

      if (trimmed.StartsWith("{", StringComparison.Ordinal)) {
        return JObject.Parse(trimmed);
      }

      using (StringReader reader = new StringReader(responseText)) {

        StringBuilder data = new StringBuilder();
        string line = reader.ReadLine();

        while (line != null) {

          if (line.StartsWith("data:", StringComparison.Ordinal)) {
            data.Append(line.Substring(5).Trim());
          }
          else if (line.Length == 0 && data.Length > 0) {

            JObject message = TryParseMessage(data.ToString(), id);

            if (message != null) {
              return message;
            }

            data.Clear();
          }

          line = reader.ReadLine();
        }

        if (data.Length > 0) {

          JObject message = TryParseMessage(data.ToString(), id);

          if (message != null) {
            return message;
          }

        }

      }

      throw new InvalidOperationException("The MCP server returned no response for the request.");
    }

    private static JObject TryParseMessage(string json, int id) {

      JObject message;

      try {
        message = JObject.Parse(json);
      }
      catch (JsonException) {
        return null;
      }

      if (message["id"] != null && message["id"].Type == JTokenType.Integer && message["id"].Value<int>() == id) {
        return message;
      }

      return null;
    }

    private static HttpClient CreateHttpClient() {

      HttpClient client = new HttpClient();
      client.Timeout = TimeSpan.FromMinutes(5);

      return client;
    }

    internal static string SanitizeName(string name) {

      string sanitized = Regex.Replace(name, "[^A-Za-z0-9_]", "_");

      if (sanitized.Length == 0) {
        sanitized = "tool";
      }

      return sanitized;
    }

  }

}
