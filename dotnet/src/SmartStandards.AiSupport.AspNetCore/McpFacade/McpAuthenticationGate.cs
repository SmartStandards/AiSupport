using Microsoft.AspNetCore.Http;
using Security.AccessTokenHandling;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AI.SmartStandards {

  /// <summary>
  /// Default authentication gate of the MCP facade (used if no 'McpFacadeOptions.RequestAuthenticator' is configured).
  /// It does NOT implement any authentication itself, but delegates completely to AuthTokenHandling:
  /// the authentication requirements of all published tools are resolved once via the UC-4 resolver
  /// ('EndpointAuthResolver') and every MCP request is validated via 'AccessTokenValidator' (incl. the
  /// 'WWW-Authenticate' challenge for the OAuth discovery). If all published tools are anonymous or the application
  /// does not use AuthTokenHandling at all (no token source registered), the gate is inactive.
  /// </summary>
  internal sealed class McpAuthenticationGate {

    private readonly IServiceProvider _Services;
    private readonly McpToolCatalog _ToolCatalog;
    private readonly object _SyncRoot = new object();

    private bool _IsResolved;
    private bool _IsTokenRequired;
    private string[] _LogicalNames;

    public McpAuthenticationGate(IServiceProvider services, McpToolCatalog toolCatalog) {
      _Services = services;
      _ToolCatalog = toolCatalog;
    }

    /// <summary>
    /// True, if at least one published tool requires an access token.
    /// </summary>
    public bool IsTokenRequired {
      get {
        this.EnsureResolved();
        return _IsTokenRequired;
      }
    }

    /// <summary>
    /// Validates the 'Authorization' header of the MCP request. On rejection, the HTTP status (401/403) and the
    /// challenge headers delivered by AuthTokenHandling are written to the response and false is returned.
    /// Signature is compatible with 'McpFacadeOptions.RequestAuthenticator'.
    /// </summary>
    public bool Authenticate(HttpContext context) {
      this.EnsureResolved();
      if (!_IsTokenRequired) {
        return true;
      }

      string rawAuthHeader = context.Request.Headers.Authorization.ToString();
      string callingMachine = string.Empty;
      if (context.Connection.RemoteIpAddress != null) {
        callingMachine = context.Connection.RemoteIpAddress.ToString();
      }

      int httpReturnCode = StatusCodes.Status200OK;
      string failedReason = string.Empty;
      KeyValuePair<string, string>[] responseHeaders = new KeyValuePair<string, string>[] { };

      bool accessGranted = AccessTokenValidator.TryValidateHttpAuthHeader(
        rawAuthHeader, null, null, string.Empty, callingMachine,
        ref httpReturnCode, ref failedReason, ref responseHeaders,
        new string[] { }, _LogicalNames
      );
      if (accessGranted) {
        return true;
      }

      if (httpReturnCode < 400) {
        httpReturnCode = StatusCodes.Status401Unauthorized;
      }
      WriteRejection(context, httpReturnCode, responseHeaders);
      return false;
    }

    /// <summary>
    /// Writes the rejection status and the headers, resolving the '{RequestBaseUrl}' placeholder
    /// (official convention of AuthTokenHandling for HTTP adapters).
    /// </summary>
    internal static void WriteRejection(HttpContext context, int statusCode, KeyValuePair<string, string>[] responseHeaders) {
      context.Response.StatusCode = statusCode;
      context.Response.Headers.CacheControl = "no-store";
      if (responseHeaders == null) {
        return;
      }
      string requestBaseUrl = GetRequestBaseUrl(context.Request);
      foreach (KeyValuePair<string, string> header in responseHeaders) {
        if (string.IsNullOrWhiteSpace(header.Key)) {
          continue;
        }
        context.Response.Headers[header.Key] = AccessTokenManager.ResolveRequestBaseUrlPlaceholder(header.Value, requestBaseUrl);
      }
    }

    /// <summary>
    /// Returns 'scheme://host[:port]' plus path base, without trailing slash.
    /// </summary>
    internal static string GetRequestBaseUrl(HttpRequest request) {
      string baseUrl = request.Scheme + "://" + request.Host.ToUriComponent() + request.PathBase.ToUriComponent();
      return baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Resolves (once) the authentication requirements of all published tools via the UC-4 resolver.
    /// The logical names of all non-anonymous tools are combined (in order of appearance), so that the token source
    /// and the required scopes are resolved by the AccessTokenManager exactly like for the target endpoints.
    /// </summary>
    private void EnsureResolved() {
      if (_IsResolved) {
        return;
      }
      lock (_SyncRoot) {
        if (_IsResolved) {
          return;
        }

        List<string> logicalNames = new List<string>();
        bool isTokenRequired = false;

        if (AccessTokenManager.GetRegisteredTokenSourceIds().Length > 0) {
          foreach (McpToolDefinition tool in _ToolCatalog.GetTools()) {
            //the resolver is async only because of the ASP.NET Core policy provider; it is evaluated
            //only once (startup-like), so blocking is acceptable here
            EndpointAuthInfo authInfo = EndpointAuthResolver.ResolveEndpointAuthAsync(
              tool.ApiDescription.ActionDescriptor, _Services
            ).GetAwaiter().GetResult();

            if (authInfo.IsAnonymous) {
              continue;
            }
            isTokenRequired = true;
            foreach (string logicalName in authInfo.LogicalNames) {
              if (!logicalNames.Contains(logicalName, StringComparer.OrdinalIgnoreCase)) {
                logicalNames.Add(logicalName);
              }
            }
          }
        }

        _LogicalNames = logicalNames.ToArray();
        _IsTokenRequired = isTokenRequired;
        _IsResolved = true;
      }
    }

  }

}
