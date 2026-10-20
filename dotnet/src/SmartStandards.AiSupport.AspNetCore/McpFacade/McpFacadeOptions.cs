using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;

namespace AI.SmartStandards {

  public sealed class McpFacadeOptions {

    private string _EndpointPattern;
    private string _ServerName;
    private string _ServerVersion;
    private bool _EnableHumanReadableIndex;
    private readonly List<McpOperationRegistration> _IncludedOperations;
    private readonly List<string> _IncludedApiGroups;
    private Func<HttpContext, bool> _RequestAuthenticator;
    private bool _EnableBrowserLogin;

    public McpFacadeOptions() {
      _EndpointPattern = "/mcp";
      _ServerName = "SmartStandards.AiSupport";
      _ServerVersion = "0.1.0";
      _EnableHumanReadableIndex = true;
      _IncludedOperations = new List<McpOperationRegistration>();
      _IncludedApiGroups = new List<string>();
      _RequestAuthenticator = null;
      _EnableBrowserLogin = false;
    }

    /// <summary>
    /// If true, the human-readable GET variant of the MCP endpoint acts like an MCP client (DEVELOPMENT/DIAGNOSTICS):
    /// '?login' runs the complete OAuth discovery (401 challenge, protected-resource metadata, authorization server
    /// metadata, dynamic client registration) and the authorization code flow with PKCE, receives the authorization
    /// response on the MCP endpoint itself, redeems the code via a real HTTP POST on the token endpoint and stores the
    /// received tokens as HttpOnly cookies (path = MCP endpoint). '?logout' removes the cookies.
    /// The cookies are ONLY evaluated for GET requests - the JSON-RPC POST requests still require a Bearer header.
    /// Every step (including token response format warnings) is shown as diagnostic trace on the page.
    /// Default: false.
    /// </summary>
    public bool EnableBrowserLogin {
      get {
        return _EnableBrowserLogin;
      }
      set {
        _EnableBrowserLogin = value;
      }
    }

    /// <summary>
    /// Optional authentication hook which is executed for every MCP request BEFORE any JSON-RPC processing.
    /// It must return true to continue. If it returns false, it is responsible for having written the
    /// HTTP rejection (e.g. status 401 including a 'WWW-Authenticate' challenge, or 403) and the request ends there.
    /// If null (default), no pre-authentication happens (target authorization of the invoked actions stays active anyway).
    /// </summary>
    public Func<HttpContext, bool> RequestAuthenticator {
      get {
        return _RequestAuthenticator;
      }
      set {
        _RequestAuthenticator = value;
      }
    }

    /// <summary>
    /// If true, a GET request on the MCP endpoint (without 'Accept: text/event-stream') returns a human-readable
    /// HTML table of all offered tools. SSE stream requests are always answered with 405 (as allowed by the MCP spec).
    /// </summary>
    public bool EnableHumanReadableIndex {
      get {
        return _EnableHumanReadableIndex;
      }
      set {
        _EnableHumanReadableIndex = value;
      }
    }

    public string EndpointPattern {
      get {
        return _EndpointPattern;
      }
      set {
        if (string.IsNullOrWhiteSpace(value)) {
          throw new ArgumentException("The MCP endpoint pattern must not be empty.", nameof(value));
        }
        _EndpointPattern = value;
      }
    }

    public string ServerName {
      get {
        return _ServerName;
      }
      set {
        if (string.IsNullOrWhiteSpace(value)) {
          throw new ArgumentException("The MCP server name must not be empty.", nameof(value));
        }
        _ServerName = value;
      }
    }

    public string ServerVersion {
      get {
        return _ServerVersion;
      }
      set {
        if (string.IsNullOrWhiteSpace(value)) {
          throw new ArgumentException("The MCP server version must not be empty.", nameof(value));
        }
        _ServerVersion = value;
      }
    }

    public void IncludeOperation(string toolName, string httpMethod, string relativePath) {
      this.IncludeOperation(toolName, httpMethod, relativePath, null);
    }

    public void IncludeOperation(string toolName, string httpMethod, string relativePath, string description) {
      McpOperationRegistration registration = new McpOperationRegistration(toolName, httpMethod, relativePath, description);
      _IncludedOperations.Add(registration);
    }

    public McpOperationRegistration[] GetIncludedOperations() {
      return _IncludedOperations.ToArray();
    }

    /// <summary>
    /// Publishes every API operation of the given ApiExplorer group (e.g. the 'ApiGroupName' of a UJMW controller
    /// or the [ApiExplorerSettings(GroupName = ...)] of a regular controller) as MCP tool.
    /// Tool names are derived deterministically from the relative path; collisions are rejected at catalog build time.
    /// </summary>
    public void IncludeOperationsFromApiGroup(string apiGroupName) {
      if (string.IsNullOrWhiteSpace(apiGroupName)) {
        throw new ArgumentException("The API group name must not be empty.", nameof(apiGroupName));
      }
      _IncludedApiGroups.Add(apiGroupName);
    }

    /// <summary>
    /// Returns the names of all API groups whose operations are published.
    /// </summary>
    public string[] GetIncludedApiGroups() {
      return _IncludedApiGroups.ToArray();
    }

  }

  public sealed class McpOperationRegistration {

    private readonly string _ToolName;
    private readonly string _HttpMethod;
    private readonly string _RelativePath;
    private readonly string _Description;

    public McpOperationRegistration(string toolName, string httpMethod, string relativePath, string description) {
      if (string.IsNullOrWhiteSpace(toolName)) {
        throw new ArgumentException("The MCP tool name must not be empty.", nameof(toolName));
      }
      if (string.IsNullOrWhiteSpace(httpMethod)) {
        throw new ArgumentException("The HTTP method must not be empty.", nameof(httpMethod));
      }
      if (string.IsNullOrWhiteSpace(relativePath)) {
        throw new ArgumentException("The relative path must not be empty.", nameof(relativePath));
      }
      _ToolName = toolName;
      _HttpMethod = httpMethod.ToUpperInvariant();
      _RelativePath = NormalizeRelativePath(relativePath);
      _Description = description;
    }

    public string ToolName {
      get {
        return _ToolName;
      }
    }

    public string HttpMethod {
      get {
        return _HttpMethod;
      }
    }

    public string RelativePath {
      get {
        return _RelativePath;
      }
    }

    public string Description {
      get {
        return _Description;
      }
    }

    private static string NormalizeRelativePath(string relativePath) {
      string normalized = relativePath.Trim();
      if (normalized.StartsWith("/", StringComparison.Ordinal)) {
        normalized = normalized.Substring(1);
      }
      return normalized;
    }

  }

}
