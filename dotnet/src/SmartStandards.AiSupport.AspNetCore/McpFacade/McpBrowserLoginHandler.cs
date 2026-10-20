using Logging.SmartStandards.CopyForAI.SmartStandards;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI.SmartStandards {

  /// <summary>
  /// DEVELOPMENT/DIAGNOSTICS: lets the human-readable GET variant of the MCP endpoint act like an MCP client
  /// (see 'McpFacadeOptions.EnableBrowserLogin'). It performs the same discovery and authorization code + PKCE flow
  /// as MCP clients do, redeems the code via a real HTTP POST on the token endpoint, stores the received tokens as
  /// HttpOnly cookies and documents every step (secrets masked, never logged) as trace on the page.
  /// Query parameters: '?login' (start), '?logout' (remove cookies), '?refresh' (force refresh token grant),
  /// 'code'/'error' + 'state' (authorization response - the MCP endpoint itself is the redirect URI).
  /// The cookies are only evaluated for GET requests (never for the JSON-RPC POST requests).
  /// </summary>
  internal sealed class McpBrowserLoginHandler {

    private const string _StateCookieName = "mcp_login_state";
    private const string _AccessTokenCookieName = "mcp_access_token";
    private const string _RefreshTokenCookieName = "mcp_refresh_token";
    private const string _ClientCookieName = "mcp_login_client";
    private const string _TraceCookieName = "mcp_login_trace";
    private const string _RefreshAttemptedItemKey = "McpBrowserLogin.RefreshAttempted";

    private const string _ClientName = "SmartStandards MCP Browser Login";

    private static readonly TimeSpan _FlowLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _TraceLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _ClientLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan _RefreshTokenCookieLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan _DefaultAccessTokenCookieLifetime = TimeSpan.FromMinutes(5);
    private static readonly HttpClient _HttpClient = CreateHttpClient();

    private static readonly string[] _SecretPropertyNames = new string[] {
      "access_token", "refresh_token", "id_token", "code", "code_verifier", "client_secret"
    };

    private readonly IOptions<McpFacadeOptions> _Options;
    private readonly IDataProtector _ClientCookieProtector;
    private readonly ConcurrentDictionary<string, PendingLogin> _PendingLoginsByState;
    private readonly ConcurrentDictionary<string, StoredTrace> _TracesByKey;

    public McpBrowserLoginHandler(IOptions<McpFacadeOptions> options, IDataProtectionProvider dataProtectionProvider) {
      _Options = options;
      //the client binding (client_id, token endpoint, resource) is stored as protected cookie, so that
      //it survives server restarts and cannot be manipulated (e.g. to redirect the refresh token to another endpoint)
      _ClientCookieProtector = dataProtectionProvider.CreateProtector("AI.SmartStandards.McpBrowserLoginHandler.ClientBinding.v1");
      _PendingLoginsByState = new ConcurrentDictionary<string, PendingLogin>(StringComparer.Ordinal);
      _TracesByKey = new ConcurrentDictionary<string, StoredTrace>(StringComparer.Ordinal);
    }

    /// <summary>
    /// True, if the browser login is enabled via 'McpFacadeOptions.EnableBrowserLogin'.
    /// </summary>
    public bool IsEnabled {
      get {
        return _Options.Value.EnableBrowserLogin;
      }
    }

    /// <summary>
    /// Processes the browser login query parameters of a GET request. If the returned result reports
    /// 'ResponseWritten', the response (a redirect) is complete. Otherwise the request continues
    /// (if a token cookie is present, it has been applied as 'Authorization' header of THIS GET request).
    /// </summary>
    public McpBrowserLoginResult Process(HttpContext context) {
      McpBrowserLoginResult result = new McpBrowserLoginResult();
      this.RemoveExpiredEntries();

      IQueryCollection query = context.Request.Query;
      string endpointUrl = GetEndpointUrl(context.Request);

      if (query.ContainsKey("logout")) {
        this.DeleteLoginCookies(context);
        Redirect(context, GetEndpointPath(context.Request), result);
        return result;
      }

      if (query.ContainsKey("login")) {
        this.StartLogin(context, endpointUrl, result);
        return result;
      }

      if (query.ContainsKey("state") && (query.ContainsKey("code") || query.ContainsKey("error"))) {
        this.CompleteLogin(context, result);
        return result;
      }

      if (query.ContainsKey("refresh")) {
        string refreshedAccessToken = this.TryRefresh(context, result);
        if (refreshedAccessToken != null) {
          this.StoreTraceForRedirect(context, result);
          Redirect(context, GetEndpointPath(context.Request), result);
        }
        return result;
      }

      this.RestoreTrace(context, result);
      this.ApplyCookieToken(context, result);
      return result;
    }

    #region " Login (discovery + authorization request) "

    /// <summary>
    /// Runs the discovery chain like an MCP client and redirects the browser to the authorization endpoint.
    /// On failure, the result contains the trace (no redirect).
    /// </summary>
    private void StartLogin(HttpContext context, string endpointUrl, McpBrowserLoginResult result) {
      result.AddInfo("Start", "MCP endpoint (resource + redirect URI): " + endpointUrl);

      //step 1: unauthenticated MCP request -> expected: HTTP 401 + Bearer challenge with 'resource_metadata'
      HttpRequestMessage probeRequest = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
      JObject initialize = new JObject();
      initialize["jsonrpc"] = "2.0";
      initialize["id"] = 1;
      initialize["method"] = "initialize";
      JObject initializeParams = new JObject();
      initializeParams["protocolVersion"] = "2025-06-18";
      initializeParams["capabilities"] = new JObject();
      JObject clientInfo = new JObject();
      clientInfo["name"] = "mcp-browser-login";
      clientInfo["version"] = "1.0";
      initializeParams["clientInfo"] = clientInfo;
      initialize["params"] = initializeParams;
      probeRequest.Content = new StringContent(initialize.ToString(Formatting.None), Encoding.UTF8, "application/json");
      probeRequest.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

      HttpCallResult probe = this.Send(probeRequest, "1. Unauthenticated MCP request (POST " + endpointUrl + ")", null, result);
      if (probe == null) {
        return;
      }
      if (probe.StatusCode != 401) {
        result.AddWarning("1. Unauthenticated MCP request", "Expected HTTP 401, but got " + probe.StatusCode.ToString() + ".");
      }
      string metadataUrl = ParseChallengeParameter(probe.WwwAuthenticate, "resource_metadata");
      string challengeScope = ParseChallengeParameter(probe.WwwAuthenticate, "scope");
      if (string.IsNullOrWhiteSpace(metadataUrl)) {
        Uri endpointUri = new Uri(endpointUrl);
        metadataUrl = endpointUri.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource" + endpointUri.AbsolutePath.TrimEnd('/');
        result.AddWarning("1. Unauthenticated MCP request", "No 'resource_metadata' in the WWW-Authenticate challenge - falling back to " + metadataUrl);
      }

      //step 2: protected resource metadata (RFC 9728)
      JObject resourceMetadata = this.GetJson(metadataUrl, "2. Protected resource metadata (RFC 9728)", result);
      if (resourceMetadata == null) {
        return;
      }
      string resource = resourceMetadata.Value<string>("resource");
      if (string.IsNullOrWhiteSpace(resource)) {
        result.AddWarning("2. Protected resource metadata", "The property 'resource' is missing.");
        resource = endpointUrl;
      }
      else if (!string.Equals(resource.TrimEnd('/'), endpointUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) {
        result.AddWarning(
          "2. Protected resource metadata",
          "'resource' (" + resource + ") does not match the MCP endpoint URL (" + endpointUrl + "). MCP clients validate this and may abort the flow."
        );
      }
      JArray authorizationServers = resourceMetadata["authorization_servers"] as JArray;
      if (authorizationServers == null || authorizationServers.Count == 0) {
        result.AddError("2. Protected resource metadata", "The property 'authorization_servers' is missing or empty.");
        return;
      }
      string authorizationServer = authorizationServers[0].ToString();
      string scope = JoinScopes(resourceMetadata["scopes_supported"] as JArray);
      if (string.IsNullOrWhiteSpace(scope)) {
        scope = challengeScope;
      }

      //step 3: authorization server metadata (RFC 8414)
      string authorizationServerMetadataUrl = BuildAuthorizationServerMetadataUrl(authorizationServer, endpointUrl);
      if (authorizationServerMetadataUrl == null) {
        result.AddError("3. Authorization server metadata", "Invalid authorization server URL: " + authorizationServer);
        return;
      }
      JObject serverMetadata = this.GetJson(authorizationServerMetadataUrl, "3. Authorization server metadata (RFC 8414)", result);
      if (serverMetadata == null) {
        return;
      }
      string issuer = serverMetadata.Value<string>("issuer");
      if (!string.Equals(TrimSlash(issuer), TrimSlash(authorizationServer), StringComparison.Ordinal)) {
        result.AddWarning(
          "3. Authorization server metadata",
          "'issuer' (" + issuer + ") is not identical to the authorization server URL from the resource metadata (" + authorizationServer + "). RFC 8414 requires identity; strict clients abort."
        );
      }
      string authorizationEndpoint = serverMetadata.Value<string>("authorization_endpoint");
      string tokenEndpoint = serverMetadata.Value<string>("token_endpoint");
      string registrationEndpoint = serverMetadata.Value<string>("registration_endpoint");
      if (string.IsNullOrWhiteSpace(authorizationEndpoint) || string.IsNullOrWhiteSpace(tokenEndpoint)) {
        result.AddError("3. Authorization server metadata", "'authorization_endpoint' or 'token_endpoint' is missing.");
        return;
      }
      if (!ContainsValue(serverMetadata["code_challenge_methods_supported"] as JArray, "S256")) {
        result.AddWarning("3. Authorization server metadata", "'code_challenge_methods_supported' does not contain 'S256' - MCP clients refuse to continue.");
      }
      if (string.IsNullOrWhiteSpace(registrationEndpoint)) {
        result.AddError("3. Authorization server metadata", "'registration_endpoint' is missing (dynamic client registration required).");
        return;
      }

      //step 4: dynamic client registration (RFC 7591)
      JObject registration = new JObject();
      registration["client_name"] = _ClientName;
      registration["redirect_uris"] = new JArray(endpointUrl);
      registration["grant_types"] = new JArray("authorization_code", "refresh_token");
      registration["response_types"] = new JArray("code");
      registration["token_endpoint_auth_method"] = "none";
      if (!string.IsNullOrWhiteSpace(scope)) {
        registration["scope"] = scope;
      }
      HttpRequestMessage registrationRequest = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint);
      registrationRequest.Content = new StringContent(registration.ToString(Formatting.None), Encoding.UTF8, "application/json");
      HttpCallResult registrationResponse = this.Send(
        registrationRequest, "4. Dynamic client registration (POST " + registrationEndpoint + ")", registration.ToString(Formatting.Indented), result
      );
      if (registrationResponse == null) {
        return;
      }
      JObject registrationResult = ParseJsonObject(registrationResponse.Body);
      string clientId = null;
      if (registrationResult != null) {
        clientId = registrationResult.Value<string>("client_id");
      }
      if (registrationResponse.StatusCode != 201 && registrationResponse.StatusCode != 200) {
        result.AddError("4. Dynamic client registration", "Unexpected HTTP status " + registrationResponse.StatusCode.ToString() + ".");
        return;
      }
      if (string.IsNullOrWhiteSpace(clientId)) {
        result.AddError("4. Dynamic client registration", "The response does not contain a 'client_id'.");
        return;
      }

      //step 5: authorization request (code flow + PKCE S256)
      string codeVerifier = CreateRandomBase64Url(32);
      string codeChallenge = ComputeS256Challenge(codeVerifier);
      string state = CreateRandomBase64Url(24);

      StringBuilder authorizeUrl = new StringBuilder(authorizationEndpoint);
      if (authorizationEndpoint.IndexOf('?') >= 0) {
        authorizeUrl.Append('&');
      }
      else {
        authorizeUrl.Append('?');
      }
      authorizeUrl.Append("response_type=code");
      authorizeUrl.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
      authorizeUrl.Append("&redirect_uri=").Append(Uri.EscapeDataString(endpointUrl));
      authorizeUrl.Append("&state=").Append(Uri.EscapeDataString(state));
      if (!string.IsNullOrWhiteSpace(scope)) {
        authorizeUrl.Append("&scope=").Append(Uri.EscapeDataString(scope));
      }
      authorizeUrl.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
      authorizeUrl.Append("&code_challenge_method=S256");
      authorizeUrl.Append("&resource=").Append(Uri.EscapeDataString(resource));

      result.AddInfo(
        "5. Authorization request (browser redirect)",
        "GET " + authorizationEndpoint + "\nclient_id=" + clientId + "\nredirect_uri=" + endpointUrl + "\nscope=" + scope +
        "\ncode_challenge=" + codeChallenge + " (S256)\nresource=" + resource
      );

      PendingLogin pending = new PendingLogin(
        clientId, endpointUrl, tokenEndpoint, resource, codeVerifier, result.GetEntries(), DateTime.UtcNow.Add(_FlowLifetime)
      );
      _PendingLoginsByState[state] = pending;
      SetCookie(context, _StateCookieName, state, DateTime.UtcNow.Add(_FlowLifetime));
      Redirect(context, authorizeUrl.ToString(), result);
    }

    #endregion

    #region " Authorization response + token request "

    /// <summary>
    /// Handles the authorization response (the MCP endpoint is the redirect URI) and redeems the code
    /// at the token endpoint via a real HTTP POST (exactly like an MCP client).
    /// </summary>
    private void CompleteLogin(HttpContext context, McpBrowserLoginResult result) {
      IQueryCollection query = context.Request.Query;
      string state = query["state"].ToString();

      PendingLogin pending;
      if (!_PendingLoginsByState.TryRemove(state, out pending)) {
        result.AddError("6. Authorization response", "Unknown or expired 'state' (login has to be restarted, e.g. after a server restart).");
        return;
      }
      result.AddEntries(pending.Trace);

      string stateCookie = context.Request.Cookies[_StateCookieName];
      DeleteCookie(context, _StateCookieName);
      if (!string.Equals(stateCookie, state, StringComparison.Ordinal)) {
        result.AddError("6. Authorization response", "The 'state' does not belong to this browser (CSRF protection).");
        return;
      }
      if (pending.ExpiresUtc <= DateTime.UtcNow) {
        result.AddError("6. Authorization response", "The login flow expired.");
        return;
      }

      string error = query["error"].ToString();
      if (!string.IsNullOrEmpty(error)) {
        result.AddError("6. Authorization response", "error=" + error + "\nerror_description=" + query["error_description"].ToString());
        return;
      }

      string code = query["code"].ToString();
      if (string.IsNullOrWhiteSpace(code)) {
        result.AddError("6. Authorization response", "The 'code' is empty.");
        return;
      }
      result.AddInfo("6. Authorization response", "code=" + MaskSecret(code) + "\nstate=OK (matches cookie)");

      Dictionary<string, string> form = new Dictionary<string, string>(StringComparer.Ordinal);
      form["grant_type"] = "authorization_code";
      form["code"] = code;
      form["redirect_uri"] = pending.RedirectUri;
      form["client_id"] = pending.ClientId;
      form["code_verifier"] = pending.CodeVerifier;
      form["resource"] = pending.Resource;

      JObject tokens = this.PostTokenRequest(pending.TokenEndpoint, form, "7. Token request (grant_type=authorization_code)", result);
      if (tokens == null) {
        return;
      }

      DateTime clientExpiresUtc = DateTime.UtcNow.Add(_ClientLifetime);
      ClientBinding client = new ClientBinding(pending.ClientId, pending.TokenEndpoint, pending.Resource, clientExpiresUtc);
      SetCookie(context, _ClientCookieName, this.ProtectClientBinding(client), clientExpiresUtc);
      this.StoreTokenCookies(context, tokens);

      result.AddInfo("8. Done", "The tokens have been stored as HttpOnly cookies (path " + GetCookiePath(context.Request) + ").");
      this.StoreTraceForRedirect(context, result);
      Redirect(context, GetEndpointPath(context.Request), result);
    }

    /// <summary>
    /// Executes the refresh token grant (with the refresh token cookie) and stores the rotated tokens.
    /// Returns the new access token or null.
    /// </summary>
    private string TryRefresh(HttpContext context, McpBrowserLoginResult result) {
      string refreshToken = context.Request.Cookies[_RefreshTokenCookieName];
      if (string.IsNullOrWhiteSpace(refreshToken)) {
        result.AddError("Refresh", "There is no refresh token cookie.");
        return null;
      }
      ClientBinding client = this.GetClientBinding(context);
      if (client == null) {
        result.AddError("Refresh", "The client registration cookie of the browser login is missing, expired or invalid (e.g. changed data protection keys) - please log in again.");
        this.DeleteLoginCookies(context);
        return null;
      }

      Dictionary<string, string> form = new Dictionary<string, string>(StringComparer.Ordinal);
      form["grant_type"] = "refresh_token";
      form["refresh_token"] = refreshToken;
      form["client_id"] = client.ClientId;
      form["resource"] = client.Resource;
      context.Items[_RefreshAttemptedItemKey] = true;

      JObject tokens = this.PostTokenRequest(client.TokenEndpoint, form, "Token request (grant_type=refresh_token)", result);
      if (tokens == null) {
        DeleteCookie(context, _AccessTokenCookieName);
        DeleteCookie(context, _RefreshTokenCookieName);
        return null;
      }
      this.StoreTokenCookies(context, tokens);
      return tokens.Value<string>("access_token");
    }

    /// <summary>
    /// Posts a form-urlencoded token request and validates the response strictly (as MCP client SDKs do).
    /// Returns the token response or null (the trace contains the reason).
    /// </summary>
    private JObject PostTokenRequest(string tokenEndpoint, Dictionary<string, string> form, string stepTitle, McpBrowserLoginResult result) {
      StringBuilder requestDescription = new StringBuilder();
      requestDescription.Append("Content-Type: application/x-www-form-urlencoded\n");
      foreach (KeyValuePair<string, string> entry in form) {
        string shownValue = entry.Value;
        if (IsSecretPropertyName(entry.Key)) {
          shownValue = MaskSecret(entry.Value);
        }
        requestDescription.Append(entry.Key).Append('=').Append(shownValue).Append('\n');
      }

      HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
      request.Content = new FormUrlEncodedContent(form);
      request.Headers.TryAddWithoutValidation("Accept", "application/json");
      HttpCallResult response = this.Send(request, stepTitle + " (POST " + tokenEndpoint + ")", requestDescription.ToString(), result);
      if (response == null) {
        return null;
      }
      return AnalyzeTokenResponse(response, stepTitle, result);
    }

    /// <summary>
    /// Validates a token endpoint response against RFC 6749 (5.1/5.2) and the stricter expectations of
    /// MCP client SDKs (schema validation: optional fields must be omitted instead of being null).
    /// </summary>
    private static JObject AnalyzeTokenResponse(HttpCallResult response, string stepTitle, McpBrowserLoginResult result) {
      if (response.ContentType == null || response.ContentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0) {
        result.AddWarning(stepTitle, "Content-Type is '" + response.ContentType + "' (expected 'application/json').");
      }
      JObject tokens = ParseJsonObject(response.Body);
      if (tokens == null) {
        result.AddError(stepTitle, "The response is not a JSON object.");
        return null;
      }

      foreach (JProperty property in tokens.Properties()) {
        if (property.Value.Type == JTokenType.Null) {
          result.AddWarning(
            stepTitle,
            "Property '" + property.Name + "' is null. Strict clients (schema-validated MCP SDKs) reject null for optional fields - they must be omitted."
          );
        }
      }

      string error = tokens.Value<string>("error");
      if (!string.IsNullOrEmpty(error)) {
        if (response.StatusCode == 200) {
          result.AddWarning(stepTitle, "Error response with HTTP 200 (RFC 6749 5.2 requires HTTP 400/401).");
        }
        result.AddError(stepTitle, "error=" + error + "\nerror_description=" + tokens.Value<string>("error_description"));
        return null;
      }
      if (response.StatusCode != 200) {
        result.AddError(stepTitle, "Unexpected HTTP status " + response.StatusCode.ToString() + ".");
        return null;
      }

      JToken accessToken = tokens["access_token"];
      if (accessToken == null || accessToken.Type != JTokenType.String || string.IsNullOrWhiteSpace(accessToken.ToString())) {
        result.AddError(stepTitle, "'access_token' is missing.");
        return null;
      }
      string tokenType = tokens.Value<string>("token_type");
      if (string.IsNullOrWhiteSpace(tokenType)) {
        result.AddError(stepTitle, "'token_type' is missing (required by RFC 6749 5.1).");
        return null;
      }
      if (!string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase)) {
        result.AddWarning(stepTitle, "'token_type' is '" + tokenType + "' (expected 'Bearer').");
      }
      JToken expiresIn = tokens["expires_in"];
      if (expiresIn != null && expiresIn.Type != JTokenType.Integer && expiresIn.Type != JTokenType.Null) {
        result.AddWarning(stepTitle, "'expires_in' is not a number.");
      }
      JToken refreshToken = tokens["refresh_token"];
      if (refreshToken == null || refreshToken.Type != JTokenType.String) {
        result.AddWarning(stepTitle, "No 'refresh_token' was issued (no silent renewal possible).");
      }
      result.AddInfo(stepTitle, "Token response is valid.");
      return tokens;
    }

    #endregion

    #region " Cookies "

    /// <summary>
    /// Called after the access token from the cookie was REJECTED by the request authenticator
    /// (e.g. unknown after a server restart without persistence, or revoked). Uses the refresh token cookie
    /// once and applies the new access token as 'Authorization' header. Returns true if the request should be
    /// authenticated again (the previous rejection response headers have been reset).
    /// </summary>
    public bool TryRecoverRejectedCookieToken(HttpContext context, McpBrowserLoginResult result) {
      if (!result.TokenFromCookie || !result.HasRefreshToken) {
        return false;
      }
      if (context.Items.ContainsKey(_RefreshAttemptedItemKey)) {
        //the refresh token cookie of THIS request was already redeemed (rotation) - reusing it would revoke the token family
        return false;
      }
      result.AddWarning("Access token", "The access token from the cookie was rejected by the MCP endpoint - trying the refresh token.");
      string accessToken = this.TryRefresh(context, result);
      result.HasRefreshToken = (accessToken != null);
      if (accessToken == null) {
        result.TokenFromCookie = false;
        context.Request.Headers.Remove("Authorization");
        return false;
      }
      context.Response.StatusCode = StatusCodes.Status200OK;
      context.Response.Headers.Remove("WWW-Authenticate");
      context.Request.Headers.Authorization = "Bearer " + accessToken;
      return true;
    }

    /// <summary>
    /// Applies the access token cookie as 'Authorization' header of the current GET request
    /// (if there is no explicit header). If the access token cookie expired, the refresh token is used.
    /// </summary>
    private void ApplyCookieToken(HttpContext context, McpBrowserLoginResult result) {
      result.HasRefreshToken = !string.IsNullOrWhiteSpace(context.Request.Cookies[_RefreshTokenCookieName]);

      if (!string.IsNullOrWhiteSpace(context.Request.Headers.Authorization.ToString())) {
        return;
      }

      string accessToken = context.Request.Cookies[_AccessTokenCookieName];
      if (string.IsNullOrWhiteSpace(accessToken) && result.HasRefreshToken) {
        accessToken = this.TryRefresh(context, result);
        result.HasRefreshToken = (accessToken != null);
      }
      if (string.IsNullOrWhiteSpace(accessToken)) {
        return;
      }

      context.Request.Headers.Authorization = "Bearer " + accessToken;
      result.TokenFromCookie = true;
    }

    private void StoreTokenCookies(HttpContext context, JObject tokens) {
      TimeSpan accessTokenLifetime = _DefaultAccessTokenCookieLifetime;
      JToken expiresIn = tokens["expires_in"];
      if (expiresIn != null && expiresIn.Type == JTokenType.Integer) {
        int seconds = expiresIn.Value<int>();
        if (seconds > 30) {
          accessTokenLifetime = TimeSpan.FromSeconds(seconds - 30);
        }
      }
      SetCookie(context, _AccessTokenCookieName, tokens.Value<string>("access_token"), DateTime.UtcNow.Add(accessTokenLifetime));

      JToken refreshToken = tokens["refresh_token"];
      if (refreshToken != null && refreshToken.Type == JTokenType.String) {
        SetCookie(context, _RefreshTokenCookieName, refreshToken.ToString(), DateTime.UtcNow.Add(_RefreshTokenCookieLifetime));
      }
    }

    private void DeleteLoginCookies(HttpContext context) {
      DeleteCookie(context, _AccessTokenCookieName);
      DeleteCookie(context, _RefreshTokenCookieName);
      DeleteCookie(context, _ClientCookieName);
      DeleteCookie(context, _StateCookieName);
    }

    /// <summary>
    /// Serializes and protects (encrypts + signs) the client binding for the client cookie.
    /// </summary>
    private string ProtectClientBinding(ClientBinding client) {
      JObject content = new JObject();
      content["client_id"] = client.ClientId;
      content["token_endpoint"] = client.TokenEndpoint;
      content["resource"] = client.Resource;
      content["exp"] = new DateTimeOffset(client.ExpiresUtc, TimeSpan.Zero).ToUnixTimeSeconds();
      return _ClientCookieProtector.Protect(content.ToString(Formatting.None));
    }

    /// <summary>
    /// Restores the client binding from the protected client cookie (null if missing, invalid or expired).
    /// </summary>
    private ClientBinding GetClientBinding(HttpContext context) {
      string protectedContent = context.Request.Cookies[_ClientCookieName];
      if (string.IsNullOrEmpty(protectedContent)) {
        return null;
      }
      JObject content;
      try {
        content = JObject.Parse(_ClientCookieProtector.Unprotect(protectedContent));
      }
      catch (CryptographicException ex) {
        DevLogger.LogError(ex);
        return null;
      }
      catch (JsonException ex) {
        DevLogger.LogError(ex);
        return null;
      }
      DateTime expiresUtc = DateTimeOffset.FromUnixTimeSeconds(content.Value<long>("exp")).UtcDateTime;
      if (expiresUtc <= DateTime.UtcNow) {
        return null;
      }
      return new ClientBinding(
        content.Value<string>("client_id"), content.Value<string>("token_endpoint"), content.Value<string>("resource"), expiresUtc
      );
    }

    private static void SetCookie(HttpContext context, string name, string value, DateTime expiresUtc) {
      CookieOptions options = CreateCookieOptions(context);
      options.Expires = new DateTimeOffset(expiresUtc, TimeSpan.Zero);
      context.Response.Cookies.Append(name, value, options);
    }

    private static void DeleteCookie(HttpContext context, string name) {
      context.Response.Cookies.Delete(name, CreateCookieOptions(context));
    }

    /// <summary>
    /// HttpOnly (no script access), SameSite=Lax (sent on the top-level redirect back from the authorization endpoint),
    /// restricted to the MCP endpoint path, Secure on HTTPS.
    /// </summary>
    private static CookieOptions CreateCookieOptions(HttpContext context) {
      CookieOptions options = new CookieOptions();
      options.HttpOnly = true;
      options.Secure = context.Request.IsHttps;
      options.SameSite = SameSiteMode.Lax;
      options.Path = GetCookiePath(context.Request);
      options.IsEssential = true;
      return options;
    }

    #endregion

    #region " Trace transfer over redirects "

    private void StoreTraceForRedirect(HttpContext context, McpBrowserLoginResult result) {
      string traceKey = CreateRandomBase64Url(16);
      _TracesByKey[traceKey] = new StoredTrace(result.GetEntries(), DateTime.UtcNow.Add(_TraceLifetime));
      SetCookie(context, _TraceCookieName, traceKey, DateTime.UtcNow.Add(_TraceLifetime));
    }

    private void RestoreTrace(HttpContext context, McpBrowserLoginResult result) {
      string traceKey = context.Request.Cookies[_TraceCookieName];
      if (string.IsNullOrEmpty(traceKey)) {
        return;
      }
      DeleteCookie(context, _TraceCookieName);
      StoredTrace trace;
      if (_TracesByKey.TryRemove(traceKey, out trace)) {
        result.AddEntries(trace.Entries);
      }
    }

    private void RemoveExpiredEntries() {
      DateTime now = DateTime.UtcNow;
      foreach (KeyValuePair<string, PendingLogin> entry in _PendingLoginsByState) {
        if (entry.Value.ExpiresUtc <= now) {
          PendingLogin removed;
          _PendingLoginsByState.TryRemove(entry.Key, out removed);
        }
      }
      foreach (KeyValuePair<string, StoredTrace> entry in _TracesByKey) {
        if (entry.Value.ExpiresUtc <= now) {
          StoredTrace removed;
          _TracesByKey.TryRemove(entry.Key, out removed);
        }
      }
    }

    #endregion

    #region " HTTP "

    private static HttpClient CreateHttpClient() {
      HttpClientHandler handler = new HttpClientHandler();
      handler.AllowAutoRedirect = false;
      handler.UseCookies = false;
      HttpClient client = new HttpClient(handler);
      client.Timeout = TimeSpan.FromSeconds(30);
      return client;
    }

    /// <summary>
    /// Sends a request synchronously and records request + response (secrets masked) as trace entry.
    /// Returns null if the request could not be executed (the trace contains the reason).
    /// </summary>
    private HttpCallResult Send(HttpRequestMessage request, string stepTitle, string requestDescription, McpBrowserLoginResult result) {
      HttpCallResult callResult;
      try {
        using (HttpResponseMessage response = _HttpClient.Send(request)) {
          callResult = new HttpCallResult();
          callResult.StatusCode = (int)response.StatusCode;
          if (response.Content.Headers.ContentType != null) {
            callResult.ContentType = response.Content.Headers.ContentType.ToString();
          }
          if (response.Headers.WwwAuthenticate.Count > 0) {
            callResult.WwwAuthenticate = string.Join(", ", response.Headers.WwwAuthenticate);
          }
          if (response.Headers.Location != null) {
            callResult.Location = response.Headers.Location.ToString();
          }
          using (Stream stream = response.Content.ReadAsStream()) {
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) {
              callResult.Body = reader.ReadToEnd();
            }
          }
        }
      }
      catch (HttpRequestException ex) {
        DevLogger.LogError(ex);
        result.AddError(stepTitle, "HTTP request failed: " + ex.Message);
        return null;
      }
      catch (TaskCanceledException ex) {
        DevLogger.LogError(ex);
        result.AddError(stepTitle, "HTTP request timed out.");
        return null;
      }
      finally {
        request.Dispose();
      }

      StringBuilder detail = new StringBuilder();
      if (!string.IsNullOrEmpty(requestDescription)) {
        detail.Append("REQUEST:\n").Append(requestDescription.TrimEnd()).Append("\n\n");
      }
      detail.Append("RESPONSE: HTTP ").Append(callResult.StatusCode.ToString()).Append('\n');
      if (!string.IsNullOrEmpty(callResult.ContentType)) {
        detail.Append("Content-Type: ").Append(callResult.ContentType).Append('\n');
      }
      if (!string.IsNullOrEmpty(callResult.WwwAuthenticate)) {
        detail.Append("WWW-Authenticate: ").Append(callResult.WwwAuthenticate).Append('\n');
      }
      if (!string.IsNullOrEmpty(callResult.Location)) {
        detail.Append("Location: ").Append(callResult.Location).Append('\n');
      }
      detail.Append('\n').Append(MaskBody(callResult.Body));
      result.AddInfo(stepTitle, detail.ToString());

      if (callResult.StatusCode >= 300 && callResult.StatusCode < 400) {
        result.AddWarning(stepTitle, "Redirect response (HTTP " + callResult.StatusCode.ToString() + ") - clients usually do not follow redirects here (e.g. HTTP->HTTPS redirection). Use the HTTPS URL of the MCP endpoint.");
      }
      return callResult;
    }

    private JObject GetJson(string url, string stepTitle, McpBrowserLoginResult result) {
      HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
      request.Headers.TryAddWithoutValidation("Accept", "application/json");
      HttpCallResult response = this.Send(request, stepTitle + " (GET " + url + ")", null, result);
      if (response == null) {
        return null;
      }
      if (response.StatusCode != 200) {
        result.AddError(stepTitle, "Unexpected HTTP status " + response.StatusCode.ToString() + ".");
        return null;
      }
      JObject document = ParseJsonObject(response.Body);
      if (document == null) {
        result.AddError(stepTitle, "The response is not a JSON object.");
      }
      return document;
    }

    #endregion

    #region " Helpers "

    private static void Redirect(HttpContext context, string location, McpBrowserLoginResult result) {
      context.Response.StatusCode = StatusCodes.Status302Found;
      context.Response.Headers.Location = location;
      context.Response.Headers.CacheControl = "no-store";
      result.ResponseWritten = true;
    }

    private static string GetEndpointPath(HttpRequest request) {
      string path = request.PathBase.Add(request.Path).ToUriComponent();
      if (string.IsNullOrEmpty(path)) {
        return "/";
      }
      return path;
    }

    private static string GetCookiePath(HttpRequest request) {
      string path = GetEndpointPath(request).TrimEnd('/');
      if (string.IsNullOrEmpty(path)) {
        return "/";
      }
      return path;
    }

    private static string GetEndpointUrl(HttpRequest request) {
      return request.Scheme + "://" + request.Host.ToUriComponent() + GetEndpointPath(request);
    }

    /// <summary>
    /// RFC 8414: the well-known suffix is inserted between host and path of the issuer.
    /// Relative issuer URLs are resolved against the MCP endpoint URL.
    /// </summary>
    private static string BuildAuthorizationServerMetadataUrl(string issuer, string endpointUrl) {
      Uri issuerUri;
      if (!Uri.TryCreate(issuer, UriKind.Absolute, out issuerUri)) {
        if (!Uri.TryCreate(new Uri(endpointUrl), issuer, out issuerUri)) {
          return null;
        }
      }
      string origin = issuerUri.GetLeftPart(UriPartial.Authority);
      string path = issuerUri.AbsolutePath.TrimEnd('/');
      return origin + "/.well-known/oauth-authorization-server" + path;
    }

    private static string ParseChallengeParameter(string challenge, string parameterName) {
      if (string.IsNullOrEmpty(challenge)) {
        return null;
      }
      Match match = Regex.Match(challenge, "(?:^|[\\s,])" + Regex.Escape(parameterName) + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
      if (!match.Success) {
        return null;
      }
      return match.Groups[1].Value;
    }

    private static string JoinScopes(JArray scopes) {
      if (scopes == null) {
        return null;
      }
      List<string> values = new List<string>();
      foreach (JToken scope in scopes) {
        values.Add(scope.ToString());
      }
      return string.Join(" ", values);
    }

    private static bool ContainsValue(JArray array, string value) {
      if (array == null) {
        return false;
      }
      foreach (JToken entry in array) {
        if (string.Equals(entry.ToString(), value, StringComparison.Ordinal)) {
          return true;
        }
      }
      return false;
    }

    private static string TrimSlash(string value) {
      if (value == null) {
        return string.Empty;
      }
      return value.TrimEnd('/');
    }

    private static JObject ParseJsonObject(string json) {
      if (string.IsNullOrWhiteSpace(json)) {
        return null;
      }
      try {
        return JToken.Parse(json) as JObject;
      }
      catch (JsonReaderException) {
        return null;
      }
    }

    private static bool IsSecretPropertyName(string name) {
      foreach (string secretName in _SecretPropertyNames) {
        if (string.Equals(secretName, name, StringComparison.OrdinalIgnoreCase)) {
          return true;
        }
      }
      return false;
    }

    /// <summary>
    /// Returns the (indented) body with masked secret values (tokens, codes, verifiers are never shown completely).
    /// </summary>
    private static string MaskBody(string body) {
      if (string.IsNullOrEmpty(body)) {
        return "(empty body)";
      }
      JObject document = ParseJsonObject(body);
      if (document == null) {
        if (body.Length > 2000) {
          return body.Substring(0, 2000) + "...";
        }
        return body;
      }
      foreach (JProperty property in document.Properties()) {
        if (IsSecretPropertyName(property.Name) && property.Value.Type == JTokenType.String) {
          property.Value = MaskSecret(property.Value.ToString());
        }
      }
      return document.ToString(Formatting.Indented);
    }

    private static string MaskSecret(string value) {
      if (value == null) {
        return "(null)";
      }
      if (value.Length <= 12) {
        return new string('*', value.Length);
      }
      return value.Substring(0, 4) + "**********" + value.Substring(value.Length - 3) + " (" + value.Length.ToString() + " chars)";
    }

    private static string CreateRandomBase64Url(int byteCount) {
      return ToBase64Url(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static string ComputeS256Challenge(string codeVerifier) {
      return ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    private static string ToBase64Url(byte[] bytes) {
      return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    #endregion

    #region " Nested types "

    private sealed class HttpCallResult {

      private int _StatusCode;
      private string _ContentType;
      private string _WwwAuthenticate;
      private string _Location;
      private string _Body;

      public int StatusCode {
        get {
          return _StatusCode;
        }
        set {
          _StatusCode = value;
        }
      }

      public string ContentType {
        get {
          return _ContentType;
        }
        set {
          _ContentType = value;
        }
      }

      public string WwwAuthenticate {
        get {
          return _WwwAuthenticate;
        }
        set {
          _WwwAuthenticate = value;
        }
      }

      public string Location {
        get {
          return _Location;
        }
        set {
          _Location = value;
        }
      }

      public string Body {
        get {
          return _Body;
        }
        set {
          _Body = value;
        }
      }

    }

    private sealed class PendingLogin {

      private readonly string _ClientId;
      private readonly string _RedirectUri;
      private readonly string _TokenEndpoint;
      private readonly string _Resource;
      private readonly string _CodeVerifier;
      private readonly McpBrowserLoginTraceEntry[] _Trace;
      private readonly DateTime _ExpiresUtc;

      public PendingLogin(
        string clientId, string redirectUri, string tokenEndpoint, string resource,
        string codeVerifier, McpBrowserLoginTraceEntry[] trace, DateTime expiresUtc
      ) {
        _ClientId = clientId;
        _RedirectUri = redirectUri;
        _TokenEndpoint = tokenEndpoint;
        _Resource = resource;
        _CodeVerifier = codeVerifier;
        _Trace = trace;
        _ExpiresUtc = expiresUtc;
      }

      public string ClientId {
        get {
          return _ClientId;
        }
      }

      public string RedirectUri {
        get {
          return _RedirectUri;
        }
      }

      public string TokenEndpoint {
        get {
          return _TokenEndpoint;
        }
      }

      public string Resource {
        get {
          return _Resource;
        }
      }

      public string CodeVerifier {
        get {
          return _CodeVerifier;
        }
      }

      public McpBrowserLoginTraceEntry[] Trace {
        get {
          return _Trace;
        }
      }

      public DateTime ExpiresUtc {
        get {
          return _ExpiresUtc;
        }
      }

    }

    private sealed class ClientBinding {

      private readonly string _ClientId;
      private readonly string _TokenEndpoint;
      private readonly string _Resource;
      private readonly DateTime _ExpiresUtc;

      public ClientBinding(string clientId, string tokenEndpoint, string resource, DateTime expiresUtc) {
        _ClientId = clientId;
        _TokenEndpoint = tokenEndpoint;
        _Resource = resource;
        _ExpiresUtc = expiresUtc;
      }

      public string ClientId {
        get {
          return _ClientId;
        }
      }

      public string TokenEndpoint {
        get {
          return _TokenEndpoint;
        }
      }

      public string Resource {
        get {
          return _Resource;
        }
      }

      public DateTime ExpiresUtc {
        get {
          return _ExpiresUtc;
        }
      }

    }

    private sealed class StoredTrace {

      private readonly McpBrowserLoginTraceEntry[] _Entries;
      private readonly DateTime _ExpiresUtc;

      public StoredTrace(McpBrowserLoginTraceEntry[] entries, DateTime expiresUtc) {
        _Entries = entries;
        _ExpiresUtc = expiresUtc;
      }

      public McpBrowserLoginTraceEntry[] Entries {
        get {
          return _Entries;
        }
      }

      public DateTime ExpiresUtc {
        get {
          return _ExpiresUtc;
        }
      }

    }

    #endregion

  }

  /// <summary>
  /// Outcome of 'McpBrowserLoginHandler.Process' for one GET request.
  /// </summary>
  internal sealed class McpBrowserLoginResult {

    private readonly List<McpBrowserLoginTraceEntry> _Entries = new List<McpBrowserLoginTraceEntry>();
    private bool _ResponseWritten;
    private bool _TokenFromCookie;
    private bool _HasRefreshToken;

    /// <summary>
    /// True, if the response (e.g. a redirect) is complete and nothing else must be written.
    /// </summary>
    public bool ResponseWritten {
      get {
        return _ResponseWritten;
      }
      set {
        _ResponseWritten = value;
      }
    }

    /// <summary>
    /// True, if the 'Authorization' header of the current request was taken from the access token cookie.
    /// </summary>
    public bool TokenFromCookie {
      get {
        return _TokenFromCookie;
      }
      set {
        _TokenFromCookie = value;
      }
    }

    public bool HasRefreshToken {
      get {
        return _HasRefreshToken;
      }
      set {
        _HasRefreshToken = value;
      }
    }

    /// <summary>
    /// True, if at least one trace entry has the kind 'Error'.
    /// </summary>
    public bool HasErrors {
      get {
        foreach (McpBrowserLoginTraceEntry entry in _Entries) {
          if (entry.Kind == McpBrowserLoginTraceKind.Error) {
            return true;
          }
        }
        return false;
      }
    }

    public void AddInfo(string title, string detail) {
      _Entries.Add(new McpBrowserLoginTraceEntry(McpBrowserLoginTraceKind.Info, title, detail));
    }

    public void AddWarning(string title, string detail) {
      _Entries.Add(new McpBrowserLoginTraceEntry(McpBrowserLoginTraceKind.Warning, title, detail));
    }

    public void AddError(string title, string detail) {
      _Entries.Add(new McpBrowserLoginTraceEntry(McpBrowserLoginTraceKind.Error, title, detail));
    }

    public void AddEntries(McpBrowserLoginTraceEntry[] entries) {
      if (entries != null) {
        _Entries.AddRange(entries);
      }
    }

    public McpBrowserLoginTraceEntry[] GetEntries() {
      return _Entries.ToArray();
    }

  }

  internal enum McpBrowserLoginTraceKind {
    Info = 0,
    Warning = 1,
    Error = 2
  }

  internal sealed class McpBrowserLoginTraceEntry {

    private readonly McpBrowserLoginTraceKind _Kind;
    private readonly string _Title;
    private readonly string _Detail;

    public McpBrowserLoginTraceEntry(McpBrowserLoginTraceKind kind, string title, string detail) {
      _Kind = kind;
      _Title = title;
      _Detail = detail;
    }

    public McpBrowserLoginTraceKind Kind {
      get {
        return _Kind;
      }
    }

    public string Title {
      get {
        return _Title;
      }
    }

    public string Detail {
      get {
        return _Detail;
      }
    }

  }

}
