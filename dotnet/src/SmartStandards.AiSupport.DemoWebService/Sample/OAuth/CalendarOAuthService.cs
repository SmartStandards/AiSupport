using AI.SmartStandards.DemoWebService.Sample.Calendar;
using Newtonsoft.Json.Linq;
using Security.AccessTokenHandling;
using Security.AccessTokenHandling.OAuth;
using Security.AccessTokenHandling.OAuth.Server;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AI.SmartStandards.DemoWebService.Sample.OAuth {

  /// <summary>
  /// OAuth 2.1 authorization server logic for the calendar demo (used by the 'OAuth2Controller' of AuthTokenHandling).
  /// Login = CalDAV credentials, validated against the calendar backend (real CalDAV server or local mock).
  /// Supported: authorization code flow with mandatory PKCE (S256), one-time codes, rotating refresh tokens
  /// with reuse detection. NOT supported: implicit flow, client credentials, Windows pass-through.
  /// Without 'AttachPersistence' all state is held in-memory and lost on restart (clients have to log in again).
  /// With persistence, refresh grants survive a restart (login sessions and codes stay in-memory - they live only minutes).
  /// </summary>
  public sealed class CalendarOAuthService : IOAuthService {

    private const string _PersistenceSectionName = "refreshGrants";

    private static readonly TimeSpan _SessionLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _CodeLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan _RefreshTokenLifetime = TimeSpan.FromDays(30);

    private readonly CalendarGrantStore _GrantStore;
    private readonly ICalDavCredentialValidator _CredentialValidator;
    private readonly CalendarOAuthClientRegistry _ClientRegistry;
    private readonly Func<DateTime> _UtcNow;

    private readonly ConcurrentDictionary<string, LoginSession> _SessionsByHash = new ConcurrentDictionary<string, LoginSession>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCode> _CodesByHash = new ConcurrentDictionary<string, PendingCode>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RefreshGrant> _RefreshGrantsByHash = new ConcurrentDictionary<string, RefreshGrant>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _ConsumedRefreshHashesToFamily = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
    private CalendarOAuthStatePersistence _Persistence;

    public CalendarOAuthService(
      CalendarGrantStore grantStore, ICalDavCredentialValidator credentialValidator, CalendarOAuthClientRegistry clientRegistry
    ) : this(grantStore, credentialValidator, clientRegistry, null) {
    }

    /// <summary>
    /// Creates the service with a custom clock (for tests).
    /// </summary>
    public CalendarOAuthService(
      CalendarGrantStore grantStore, ICalDavCredentialValidator credentialValidator,
      CalendarOAuthClientRegistry clientRegistry, Func<DateTime> utcNow
    ) {
      if (grantStore == null) {
        throw new ArgumentNullException(nameof(grantStore));
      }
      if (credentialValidator == null) {
        throw new ArgumentNullException(nameof(credentialValidator));
      }
      if (clientRegistry == null) {
        throw new ArgumentNullException(nameof(clientRegistry));
      }
      _GrantStore = grantStore;
      _CredentialValidator = credentialValidator;
      _ClientRegistry = clientRegistry;
      if (utcNow == null) {
        _UtcNow = () => DateTime.UtcNow;
      }
      else {
        _UtcNow = utcNow;
      }
    }

    #region " Persistence "

    /// <summary>
    /// Restores the persisted (not yet expired) refresh grants including the reuse-detection state and writes
    /// every subsequent change into the given persistence. Only token hashes are persisted - never raw tokens.
    /// </summary>
    public void AttachPersistence(CalendarOAuthStatePersistence persistence) {
      if (persistence == null) {
        throw new ArgumentNullException(nameof(persistence));
      }
      DateTime now = _UtcNow();
      JObject section = persistence.Load(_PersistenceSectionName) as JObject;
      if (section != null) {
        JArray grants = section["grants"] as JArray;
        if (grants != null) {
          foreach (JToken token in grants) {
            JObject entry = (JObject)token;
            DateTime expiresUtc = CalendarOAuthStatePersistence.FromUnixMilliseconds(entry["exp"]);
            if (expiresUtc <= now) {
              continue;
            }
            _RefreshGrantsByHash[entry.Value<string>("hash")] = new RefreshGrant(
              entry.Value<string>("client"),
              CalendarOAuthStatePersistence.ReadCredentials(entry),
              CalendarOAuthStatePersistence.ToStringArray(entry["scopes"]),
              entry.Value<string>("family"),
              expiresUtc
            );
          }
        }
        JObject consumed = section["consumed"] as JObject;
        if (consumed != null) {
          foreach (JProperty entry in consumed.Properties()) {
            _ConsumedRefreshHashesToFamily[entry.Name] = entry.Value.ToString();
          }
        }
      }
      _Persistence = persistence;
    }

    /// <summary>
    /// Writes a snapshot of all refresh grants and consumed refresh token hashes (if a persistence is attached).
    /// </summary>
    private void Persist() {
      CalendarOAuthStatePersistence persistence = _Persistence;
      if (persistence == null) {
        return;
      }
      persistence.Save(_PersistenceSectionName, () => {
        JArray grants = new JArray();
        foreach (KeyValuePair<string, RefreshGrant> entry in _RefreshGrantsByHash) {
          JObject item = new JObject();
          item["hash"] = entry.Key;
          item["client"] = entry.Value.ClientId;
          CalendarOAuthStatePersistence.WriteCredentials(item, entry.Value.Credentials);
          item["scopes"] = CalendarOAuthStatePersistence.ToJArray(entry.Value.Scopes);
          item["family"] = entry.Value.FamilyId;
          item["exp"] = CalendarOAuthStatePersistence.ToUnixMilliseconds(entry.Value.ExpiresUtc);
          grants.Add(item);
        }
        JObject consumed = new JObject();
        foreach (KeyValuePair<string, string> entry in _ConsumedRefreshHashesToFamily) {
          consumed[entry.Key] = entry.Value;
        }
        JObject section = new JObject();
        section["grants"] = grants;
        section["consumed"] = consumed;
        return section;
      });
    }

    #endregion

    #region " Login (Step 1) "

    /// <summary>
    /// Validates the CalDAV credentials against the calendar backend and opens a short-lived login session.
    /// </summary>
    public bool TryAuthenticate(
      string apiClientId, string login, string password, bool noPasswordNeeded, string clientProvidedState,
      out string sessionId, out string message
    ) {
      sessionId = null;
      if (noPasswordNeeded) {
        message = "Pass-through login is not supported. Please enter your calendar credentials.";
        return false;
      }
      if (!_ClientRegistry.IsKnownClient(apiClientId)) {
        message = "Unknown client.";
        return false;
      }
      if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password) || login.Contains(":")) {
        message = "Invalid user name or password.";
        return false;
      }

      CalDavCredentials credentials = new CalDavCredentials(login, password);
      CalDavCredentialValidationResult validation = _CredentialValidator.ValidateCredentials(credentials);
      if (validation == CalDavCredentialValidationResult.ServerUnavailable) {
        message = "The calendar server is currently not reachable. Please try again later.";
        return false;
      }
      if (validation != CalDavCredentialValidationResult.Valid) {
        message = "Invalid user name or password.";
        return false;
      }

      this.RemoveExpiredEntries();
      string rawSessionId = CreateRandomToken();
      _SessionsByHash[HashToken(rawSessionId)] = new LoginSession(apiClientId, credentials, _UtcNow().Add(_SessionLifetime));
      sessionId = rawSessionId;
      message = null;
      return true;
    }

    /// <summary>
    /// Offers the (only) scope 'calendar' for confirmation.
    /// </summary>
    public bool TryGetAvailableScopesBySessionId(
      string apiClientId, string sessionId, string[] prefferedScopes,
      out ScopeDescriptor[] availableScopes, out string message
    ) {
      LoginSession session;
      if (!this.TryGetSession(apiClientId, sessionId, false, out session)) {
        availableScopes = new ScopeDescriptor[] { };
        message = "The login session is invalid or expired. Please log in again.";
        return false;
      }
      ScopeDescriptor calendarScope = new ScopeDescriptor();
      calendarScope.Expression = CalendarGrantStore.CalendarScope;
      calendarScope.Label = "Read and modify your calendar";
      calendarScope.Selected = true;
      calendarScope.ReadOnly = true;
      calendarScope.Invisible = false;
      availableScopes = new ScopeDescriptor[] { calendarScope };
      message = null;
      return true;
    }

    #endregion

    #region " Code Flow (Step 2 + 3) "

    /// <summary>
    /// Consumes the login session and creates a one-time retrieval code which is bound to client and PKCE challenge.
    /// </summary>
    public bool TryValidateSessionIdAndCreateRetrievalCode(
      string apiClientId, string sessionId, string[] selectedScopes, string codeChallenge,
      out string code, out string message
    ) {
      code = null;
      if (string.IsNullOrWhiteSpace(codeChallenge)) {
        message = "PKCE (code_challenge with method S256) is required.";
        return false;
      }
      LoginSession session;
      if (!this.TryGetSession(apiClientId, sessionId, true, out session)) {
        message = "The login session is invalid or expired. Please log in again.";
        return false;
      }
      string rawCode = CreateRandomToken();
      _CodesByHash[HashToken(rawCode)] = new PendingCode(
        apiClientId, session.Credentials, FilterScopes(selectedScopes), codeChallenge, _UtcNow().Add(_CodeLifetime)
      );
      code = rawCode;
      message = null;
      return true;
    }

    /// <summary>
    /// Redeems a one-time code (it is removed in any case) and issues access + refresh token.
    /// The PKCE verification is done by the controller with the returned (bound) code challenge.
    /// </summary>
    public TokenIssuingResult RetrieveTokenByCode(string clientId, string clientSecret, string code, out string codeChallenge) {
      codeChallenge = null;
      if (string.IsNullOrWhiteSpace(code)) {
        return TokenIssuingResult.FromError("invalid_grant", "The code is missing.");
      }
      PendingCode pending;
      if (!_CodesByHash.TryRemove(HashToken(code), out pending)) {
        return TokenIssuingResult.FromError("invalid_grant", "The code is invalid or was already used.");
      }
      if (pending.ExpiresUtc <= _UtcNow()) {
        return TokenIssuingResult.FromError("invalid_grant", "The code expired.");
      }
      if (!string.Equals(pending.ClientId, clientId, StringComparison.Ordinal)) {
        return TokenIssuingResult.FromError("invalid_grant", "The code was issued to another client.");
      }
      codeChallenge = pending.CodeChallenge;
      return this.IssueTokens(pending.ClientId, pending.Credentials, pending.Scopes, CreateRandomToken());
    }

    #endregion

    #region " Refresh Token Flow "

    /// <summary>
    /// Rotates the refresh token: the presented token is consumed and a new pair is issued.
    /// Presenting an already consumed refresh token revokes the whole token family (reuse detection).
    /// </summary>
    public TokenIssuingResult CreateFollowUpToken(string refreshToken) {
      if (string.IsNullOrWhiteSpace(refreshToken)) {
        return TokenIssuingResult.FromError("invalid_grant", "The refresh token is missing.");
      }
      string hash = HashToken(refreshToken);
      RefreshGrant grant;
      if (!_RefreshGrantsByHash.TryRemove(hash, out grant)) {
        string family;
        if (_ConsumedRefreshHashesToFamily.TryGetValue(hash, out family)) {
          this.RevokeFamily(family);
        }
        return TokenIssuingResult.FromError("invalid_grant", "The refresh token is invalid, expired or was already used.");
      }
      _ConsumedRefreshHashesToFamily[hash] = grant.FamilyId;
      if (grant.ExpiresUtc <= _UtcNow()) {
        this.Persist();
        return TokenIssuingResult.FromError("invalid_grant", "The refresh token expired.");
      }
      return this.IssueTokens(grant.ClientId, grant.Credentials, grant.Scopes, grant.FamilyId);
    }

    #endregion

    #region " Unsupported Flows "

    /// <summary>
    /// The implicit flow is not supported (OAuth 2.1).
    /// </summary>
    public bool TryValidateSessionIdAndCreateToken(
      string apiClientId, string sessionId, string[] selectedScopes, out TokenIssuingResult tokenResult
    ) {
      tokenResult = TokenIssuingResult.FromError("unsupported_response_type", "Only the authorization code flow (with PKCE) is supported.");
      return false;
    }

    /// <summary>
    /// The client credentials flow is not supported (calendar access always needs a user).
    /// </summary>
    public TokenIssuingResult ValidateClientAndCreateToken(string clientId, string clientSecret, string[] selectedScopes) {
      return TokenIssuingResult.FromError("unsupported_grant_type", "The client credentials flow is not supported.");
    }

    #endregion

    /// <summary>
    /// Validates the client and its redirect URI (exact match against the registration).
    /// </summary>
    public bool TryValidateApiClient(string apiClientId, string apiCallerHost, string redirectUri, out string message) {
      if (!_ClientRegistry.IsKnownClient(apiClientId)) {
        message = "Unknown client.";
        return false;
      }
      if (!_ClientRegistry.IsRegisteredRedirectUri(apiClientId, redirectUri)) {
        message = "The redirect URI is not registered for this client.";
        return false;
      }
      message = "Valid client";
      return true;
    }

    /// <summary>
    /// RFC 7662 introspection of access tokens issued by this service (never exposes credentials).
    /// </summary>
    public void IntrospectAccessToken(string rawToken, out bool isActive, out Dictionary<string, object> claims) {
      claims = new Dictionary<string, object>();
      CalendarGrant grant;
      if (_GrantStore.Evaluate(rawToken, out grant) != CalendarTokenState.Valid) {
        isActive = false;
        return;
      }
      isActive = true;
      claims["sub"] = grant.Subject;
      claims["scope"] = string.Join(" ", grant.Scopes);
      claims["exp"] = new DateTimeOffset(grant.ExpiresUtc).ToUnixTimeSeconds();
    }

    private TokenIssuingResult IssueTokens(string clientId, CalDavCredentials credentials, string[] scopes, string familyId) {
      this.RemoveExpiredEntries();
      string accessToken = _GrantStore.IssueAccessToken(credentials.UserName, credentials, scopes, null, _AccessTokenLifetime);
      string refreshToken = CreateRandomToken();
      _RefreshGrantsByHash[HashToken(refreshToken)] = new RefreshGrant(
        clientId, credentials, scopes, familyId, _UtcNow().Add(_RefreshTokenLifetime)
      );
      this.Persist();
      TokenIssuingResult result = new TokenIssuingResult();
      result.access_token = accessToken;
      result.token_type = "Bearer";
      result.expires_in = (int)_AccessTokenLifetime.TotalSeconds;
      result.refresh_token = refreshToken;
      result.scope = string.Join(" ", scopes);
      return result;
    }

    private void RevokeFamily(string familyId) {
      foreach (KeyValuePair<string, RefreshGrant> entry in _RefreshGrantsByHash) {
        if (string.Equals(entry.Value.FamilyId, familyId, StringComparison.Ordinal)) {
          RefreshGrant removed;
          if (_RefreshGrantsByHash.TryRemove(entry.Key, out removed)) {
            _GrantStore.RevokeAllAccessTokensOfSubject(removed.Credentials.UserName);
          }
        }
      }
      this.Persist();
    }

    private bool TryGetSession(string clientId, string sessionId, bool consume, out LoginSession session) {
      session = null;
      if (string.IsNullOrWhiteSpace(sessionId)) {
        return false;
      }
      string hash = HashToken(sessionId);
      LoginSession found;
      bool exists;
      if (consume) {
        exists = _SessionsByHash.TryRemove(hash, out found);
      }
      else {
        exists = _SessionsByHash.TryGetValue(hash, out found);
      }
      if (!exists || found.ExpiresUtc <= _UtcNow() || !string.Equals(found.ClientId, clientId, StringComparison.Ordinal)) {
        return false;
      }
      session = found;
      return true;
    }

    private void RemoveExpiredEntries() {
      DateTime now = _UtcNow();
      foreach (KeyValuePair<string, LoginSession> entry in _SessionsByHash) {
        if (entry.Value.ExpiresUtc <= now) {
          LoginSession removed;
          _SessionsByHash.TryRemove(entry.Key, out removed);
        }
      }
      foreach (KeyValuePair<string, PendingCode> entry in _CodesByHash) {
        if (entry.Value.ExpiresUtc <= now) {
          PendingCode removed;
          _CodesByHash.TryRemove(entry.Key, out removed);
        }
      }
      foreach (KeyValuePair<string, RefreshGrant> entry in _RefreshGrantsByHash) {
        if (entry.Value.ExpiresUtc <= now) {
          RefreshGrant removed;
          _RefreshGrantsByHash.TryRemove(entry.Key, out removed);
        }
      }
    }

    private static string[] FilterScopes(string[] selectedScopes) {
      //only the calendar scope exists (it is read-only on the consent page and therefore always granted)
      return new string[] { CalendarGrantStore.CalendarScope };
    }

    private static string CreateRandomToken() {
      byte[] bytes = RandomNumberGenerator.GetBytes(32);
      return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string token) {
      return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private sealed class LoginSession {

      private readonly string _ClientId;
      private readonly CalDavCredentials _Credentials;
      private readonly DateTime _ExpiresUtc;

      public LoginSession(string clientId, CalDavCredentials credentials, DateTime expiresUtc) {
        _ClientId = clientId;
        _Credentials = credentials;
        _ExpiresUtc = expiresUtc;
      }

      public string ClientId {
        get {
          return _ClientId;
        }
      }

      public CalDavCredentials Credentials {
        get {
          return _Credentials;
        }
      }

      public DateTime ExpiresUtc {
        get {
          return _ExpiresUtc;
        }
      }

    }

    private sealed class PendingCode {

      private readonly string _ClientId;
      private readonly CalDavCredentials _Credentials;
      private readonly string[] _Scopes;
      private readonly string _CodeChallenge;
      private readonly DateTime _ExpiresUtc;

      public PendingCode(string clientId, CalDavCredentials credentials, string[] scopes, string codeChallenge, DateTime expiresUtc) {
        _ClientId = clientId;
        _Credentials = credentials;
        _Scopes = scopes;
        _CodeChallenge = codeChallenge;
        _ExpiresUtc = expiresUtc;
      }

      public string ClientId {
        get {
          return _ClientId;
        }
      }

      public CalDavCredentials Credentials {
        get {
          return _Credentials;
        }
      }

      public string[] Scopes {
        get {
          return _Scopes;
        }
      }

      public string CodeChallenge {
        get {
          return _CodeChallenge;
        }
      }

      public DateTime ExpiresUtc {
        get {
          return _ExpiresUtc;
        }
      }

    }

    private sealed class RefreshGrant {

      private readonly string _ClientId;
      private readonly CalDavCredentials _Credentials;
      private readonly string[] _Scopes;
      private readonly string _FamilyId;
      private readonly DateTime _ExpiresUtc;

      public RefreshGrant(string clientId, CalDavCredentials credentials, string[] scopes, string familyId, DateTime expiresUtc) {
        _ClientId = clientId;
        _Credentials = credentials;
        _Scopes = scopes;
        _FamilyId = familyId;
        _ExpiresUtc = expiresUtc;
      }

      public string ClientId {
        get {
          return _ClientId;
        }
      }

      public CalDavCredentials Credentials {
        get {
          return _Credentials;
        }
      }

      public string[] Scopes {
        get {
          return _Scopes;
        }
      }

      public string FamilyId {
        get {
          return _FamilyId;
        }
      }

      public DateTime ExpiresUtc {
        get {
          return _ExpiresUtc;
        }
      }

    }

  }

}
