using AI.SmartStandards.DemoWebService.Sample.Calendar;
using Microsoft.AspNetCore.Http;
using System;

namespace AI.SmartStandards.DemoWebService.Sample.OAuth {

  /// <summary>
  /// Authenticates requests against the calendar demo (MCP endpoint and UJMW calendar controller).
  /// Missing, unknown or expired tokens result in HTTP 401 including a RFC 9728 Bearer challenge
  /// ('resource_metadata'), insufficient scopes in HTTP 403. Token values are never logged.
  /// </summary>
  public sealed class CalendarRequestAuthenticator {

    private readonly CalendarGrantStore _GrantStore;
    private readonly string _MetadataPath;

    /// <summary>
    /// Creates the authenticator.
    /// </summary>
    /// <param name="grantStore">store of the issued access tokens</param>
    /// <param name="metadataPath">path of the protected-resource metadata (relative to the request base URL)</param>
    public CalendarRequestAuthenticator(CalendarGrantStore grantStore, string metadataPath) {
      if (grantStore == null) {
        throw new ArgumentNullException(nameof(grantStore));
      }
      if (string.IsNullOrWhiteSpace(metadataPath)) {
        throw new ArgumentException("The metadata path must not be empty.", nameof(metadataPath));
      }
      _GrantStore = grantStore;
      if (!metadataPath.StartsWith("/", StringComparison.Ordinal)) {
        metadataPath = "/" + metadataPath;
      }
      _MetadataPath = metadataPath;
    }

    /// <summary>
    /// Evaluates the 'Authorization' header of the request. On failure, the HTTP rejection (status code and
    /// 'WWW-Authenticate' challenge) is written to the response and false is returned.
    /// Signature is compatible with 'McpFacadeOptions.RequestAuthenticator'.
    /// </summary>
    public bool Authenticate(HttpContext context) {
      string authHeader = context.Request.Headers.Authorization.ToString();
      int statusCode;
      string error;
      string errorDescription;
      CalendarGrant grant;
      if (this.TryAuthorize(authHeader, out grant, out statusCode, out error, out errorDescription)) {
        return true;
      }

      context.Response.StatusCode = statusCode;
      context.Response.Headers.WWWAuthenticate = this.BuildChallenge(context.Request, error, errorDescription);
      context.Response.Headers.CacheControl = "no-store";
      return false;
    }

    /// <summary>
    /// Transport-neutral evaluation: returns true if the token is valid and grants the calendar scope.
    /// </summary>
    public bool TryAuthorize(string authHeader, out CalendarGrant grant, out int statusCode, out string error, out string errorDescription) {
      CalendarTokenState state = _GrantStore.Evaluate(authHeader, out grant);
      error = null;
      errorDescription = null;

      if (state == CalendarTokenState.Missing) {
        statusCode = 401;
        errorDescription = "An access token is required.";
        return false;
      }
      if (state == CalendarTokenState.Expired) {
        statusCode = 401;
        error = "invalid_token";
        errorDescription = "The access token expired.";
        return false;
      }
      if (state == CalendarTokenState.Unknown) {
        //unknown tokens (also after a restart, because grants are held in-memory) need a refresh or a new login
        statusCode = 401;
        error = "invalid_token";
        errorDescription = "The access token is invalid.";
        return false;
      }
      if (!grant.HasScope(CalendarGrantStore.CalendarScope)) {
        statusCode = 403;
        error = "insufficient_scope";
        errorDescription = "The access token does not grant the scope '" + CalendarGrantStore.CalendarScope + "'.";
        grant = null;
        return false;
      }

      statusCode = 200;
      return true;
    }

    /// <summary>
    /// Builds the absolute protected-resource metadata URL for the given request.
    /// </summary>
    public string ResolveMetadataUrl(HttpRequest request) {
      return request.Scheme + "://" + request.Host.ToUriComponent() + request.PathBase.ToUriComponent() + _MetadataPath;
    }

    /// <summary>
    /// Builds the RFC 6750 / RFC 9728 Bearer challenge.
    /// </summary>
    public string BuildChallenge(HttpRequest request, string error, string errorDescription) {
      string challenge = "Bearer resource_metadata=\"" + EscapeQuoted(this.ResolveMetadataUrl(request)) + "\"";
      challenge = challenge + ", scope=\"" + CalendarGrantStore.CalendarScope + "\"";
      if (!string.IsNullOrEmpty(error)) {
        challenge = challenge + ", error=\"" + EscapeQuoted(error) + "\"";
      }
      if (!string.IsNullOrEmpty(errorDescription)) {
        challenge = challenge + ", error_description=\"" + EscapeQuoted(errorDescription) + "\"";
      }
      return challenge;
    }

    private static string EscapeQuoted(string value) {
      return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

  }

  /// <summary>
  /// Resolves the CalDAV credentials of the current call server-side from the access token
  /// in the 'Authorization' header of the current HTTP request (never from tool arguments).
  /// </summary>
  public sealed class AccessTokenCalDavCredentialSource : ICalDavCredentialSource {

    private readonly IHttpContextAccessor _HttpContextAccessor;
    private readonly CalendarGrantStore _GrantStore;

    public AccessTokenCalDavCredentialSource(IHttpContextAccessor httpContextAccessor, CalendarGrantStore grantStore) {
      if (httpContextAccessor == null) {
        throw new ArgumentNullException(nameof(httpContextAccessor));
      }
      if (grantStore == null) {
        throw new ArgumentNullException(nameof(grantStore));
      }
      _HttpContextAccessor = httpContextAccessor;
      _GrantStore = grantStore;
    }

    /// <summary>
    /// Returns the credentials of the valid access token of the current request, or null.
    /// </summary>
    public CalDavCredentials GetCurrentCredentials() {
      HttpContext context = _HttpContextAccessor.HttpContext;
      if (context == null) {
        return null;
      }
      CalendarGrant grant;
      CalendarTokenState state = _GrantStore.Evaluate(context.Request.Headers.Authorization.ToString(), out grant);
      if (state != CalendarTokenState.Valid) {
        return null;
      }
      return grant.Credentials;
    }

  }

}
