using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace AI.SmartStandards.DemoWebService.Sample.OAuth {

  /// <summary>
  /// In-memory registry of OAuth clients (filled via RFC 7591 dynamic client registration, as used by MCP clients
  /// like ChatGPT, Claude or the MCP Inspector). Only public clients (PKCE, no secret) are supported.
  /// LIMITATION: registrations are lost on restart (clients then register again).
  /// </summary>
  public sealed class CalendarOAuthClientRegistry {

    private readonly ConcurrentDictionary<string, string[]> _RedirectUrisByClientId = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);

    /// <summary>
    /// Registers a new public client and returns its generated client id.
    /// </summary>
    public string RegisterClient(string[] redirectUris) {
      if (redirectUris == null || redirectUris.Length == 0) {
        throw new ArgumentException("At least one redirect URI is required.");
      }
      foreach (string redirectUri in redirectUris) {
        if (!IsAcceptableRedirectUri(redirectUri)) {
          throw new ArgumentException("The redirect URI '" + redirectUri + "' is not allowed (https or http on loopback required, no fragment).");
        }
      }
      string clientId = "mcp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
      _RedirectUrisByClientId[clientId] = redirectUris;
      return clientId;
    }

    /// <summary>
    /// Returns true if the client is registered.
    /// </summary>
    public bool IsKnownClient(string clientId) {
      if (string.IsNullOrWhiteSpace(clientId)) {
        return false;
      }
      return _RedirectUrisByClientId.ContainsKey(clientId);
    }

    /// <summary>
    /// Returns true if the redirect URI exactly matches one of the registered URIs of the client.
    /// </summary>
    public bool IsRegisteredRedirectUri(string clientId, string redirectUri) {
      string[] registered;
      if (string.IsNullOrWhiteSpace(clientId) || !_RedirectUrisByClientId.TryGetValue(clientId, out registered)) {
        return false;
      }
      foreach (string candidate in registered) {
        if (string.Equals(candidate, redirectUri, StringComparison.Ordinal)) {
          return true;
        }
      }
      return false;
    }

    private static bool IsAcceptableRedirectUri(string redirectUri) {
      Uri uri;
      if (string.IsNullOrWhiteSpace(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out uri)) {
        return false;
      }
      if (!string.IsNullOrEmpty(uri.Fragment)) {
        return false;
      }
      if (uri.Scheme == Uri.UriSchemeHttps) {
        return true;
      }
      if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) {
        return true;
      }
      return false;
    }

  }

  /// <summary>
  /// Maps the authorization server discovery (RFC 8414) and the dynamic client registration (RFC 7591).
  /// The authorization and token endpoints are provided by the 'OAuth2Controller' of AuthTokenHandling ('/oauth2/...').
  /// </summary>
  public static class CalendarOAuthEndpointRouteBuilderExtensions {

    /// <summary>
    /// Maps '/.well-known/oauth-authorization-server' and '/oauth2/register'.
    /// </summary>
    public static void MapCalendarOAuthEndpoints(this IEndpointRouteBuilder endpoints) {

      endpoints.MapGet("/.well-known/oauth-authorization-server", (HttpContext context) => {
        string issuer = GetBaseUrl(context.Request);
        JObject document = new JObject();
        document["issuer"] = issuer;
        document["authorization_endpoint"] = issuer + "/oauth2/authorize";
        document["token_endpoint"] = issuer + "/oauth2/token";
        document["registration_endpoint"] = issuer + "/oauth2/register";
        document["introspection_endpoint"] = issuer + "/oauth2/introspect";
        document["scopes_supported"] = new JArray(CalendarGrantStore.CalendarScope);
        document["response_types_supported"] = new JArray("code");
        document["grant_types_supported"] = new JArray("authorization_code", "refresh_token");
        document["code_challenge_methods_supported"] = new JArray("S256");
        document["token_endpoint_auth_methods_supported"] = new JArray("none");
        return Results.Content(document.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
      }).AllowAnonymous();

      endpoints.MapPost("/oauth2/register", (HttpContext context, System.Text.Json.JsonElement body) => {
        CalendarOAuthClientRegistry registry = context.RequestServices.GetRequiredService<CalendarOAuthClientRegistry>();
        JObject request = JObject.Parse(body.GetRawText());
        List<string> redirectUris = new List<string>();
        JArray redirectUriArray = request["redirect_uris"] as JArray;
        if (redirectUriArray != null) {
          foreach (JToken token in redirectUriArray) {
            redirectUris.Add(token.ToString());
          }
        }

        string clientId;
        try {
          clientId = registry.RegisterClient(redirectUris.ToArray());
        }
        catch (ArgumentException ex) {
          JObject error = new JObject();
          error["error"] = "invalid_redirect_uri";
          error["error_description"] = ex.Message;
          return Results.Content(error.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8", null, 400);
        }

        JObject response = new JObject();
        response["client_id"] = clientId;
        response["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        response["redirect_uris"] = new JArray(redirectUris.ToArray());
        response["token_endpoint_auth_method"] = "none";
        response["grant_types"] = new JArray("authorization_code", "refresh_token");
        response["response_types"] = new JArray("code");
        response["scope"] = CalendarGrantStore.CalendarScope;
        if (request["client_name"] != null) {
          response["client_name"] = request["client_name"];
        }
        return Results.Content(response.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8", null, 201);
      }).AllowAnonymous();

    }

    private static string GetBaseUrl(HttpRequest request) {
      return request.Scheme + "://" + request.Host.ToUriComponent() + request.PathBase.ToUriComponent();
    }

  }

}
