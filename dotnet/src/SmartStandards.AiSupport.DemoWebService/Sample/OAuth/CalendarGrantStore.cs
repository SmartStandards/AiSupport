using AI.SmartStandards.DemoWebService.Sample.Calendar;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AI.SmartStandards.DemoWebService.Sample.OAuth {

  /// <summary>
  /// In-memory store for short-lived access tokens which carry (server-side only) the CalDAV credentials of their subject.
  /// Raw tokens are never stored: the dictionary key is the SHA-256 hash of the token.
  /// Without 'AttachPersistence' all grants are lost on restart (clients then receive HTTP 401 and have to refresh or log in again).
  /// </summary>
  public sealed class CalendarGrantStore {

    /// <summary>
    /// The scope that is required for the calendar tools.
    /// </summary>
    public const string CalendarScope = "calendar";

    private const string _PersistenceSectionName = "accessGrants";

    private readonly ConcurrentDictionary<string, CalendarGrant> _GrantsByTokenHash;
    private readonly Func<DateTime> _UtcNow;
    private CalendarOAuthStatePersistence _Persistence;

    public CalendarGrantStore() : this(null) {
    }

    /// <summary>
    /// Creates a store with a custom clock (for tests).
    /// </summary>
    public CalendarGrantStore(Func<DateTime> utcNow) {
      _GrantsByTokenHash = new ConcurrentDictionary<string, CalendarGrant>(StringComparer.Ordinal);
      if (utcNow == null) {
        _UtcNow = () => DateTime.UtcNow;
      }
      else {
        _UtcNow = utcNow;
      }
    }

    /// <summary>
    /// Restores the persisted (not yet expired) grants and writes every subsequent change into the given persistence.
    /// Only token hashes are persisted - never raw tokens.
    /// </summary>
    public void AttachPersistence(CalendarOAuthStatePersistence persistence) {
      if (persistence == null) {
        throw new ArgumentNullException(nameof(persistence));
      }
      DateTime now = _UtcNow();
      JArray entries = persistence.Load(_PersistenceSectionName) as JArray;
      if (entries != null) {
        foreach (JToken token in entries) {
          JObject entry = (JObject)token;
          DateTime expiresUtc = CalendarOAuthStatePersistence.FromUnixMilliseconds(entry["exp"]);
          if (expiresUtc <= now) {
            continue;
          }
          CalendarGrant grant = new CalendarGrant(
            entry.Value<string>("sub"),
            CalendarOAuthStatePersistence.ReadCredentials(entry),
            CalendarOAuthStatePersistence.ToStringArray(entry["scopes"]),
            entry.Value<string>("resource"),
            expiresUtc
          );
          _GrantsByTokenHash[entry.Value<string>("hash")] = grant;
        }
      }
      _Persistence = persistence;
    }

    /// <summary>
    /// Issues a new random access token
    /// </summary>
    public string IssueAccessToken(string subject, CalDavCredentials credentials, string[] scopes, string resource, TimeSpan lifetime) {
      if (string.IsNullOrWhiteSpace(subject)) {
        throw new ArgumentException("The subject must not be empty.", nameof(subject));
      }
      if (credentials == null) {
        throw new ArgumentNullException(nameof(credentials));
      }
      if (lifetime <= TimeSpan.Zero) {
        throw new ArgumentException("The lifetime must be positive.", nameof(lifetime));
      }
      this.RemoveExpiredGrants();

      string token = CreateRandomToken();
      string[] grantedScopes = scopes;
      if (grantedScopes == null) {
        grantedScopes = new string[] { };
      }
      CalendarGrant grant = new CalendarGrant(subject, credentials, grantedScopes, resource, _UtcNow().Add(lifetime));
      _GrantsByTokenHash[HashToken(token)] = grant;
      this.Persist();
      return token;
    }

    /// <summary>
    /// Evaluates a raw access token (with or without 'Bearer ' prefix).
    /// </summary>
    public CalendarTokenState Evaluate(string rawTokenOrAuthHeader, out CalendarGrant grant) {
      grant = null;
      string token = StripBearerPrefix(rawTokenOrAuthHeader);
      if (string.IsNullOrWhiteSpace(token)) {
        return CalendarTokenState.Missing;
      }
      CalendarGrant found;
      if (!_GrantsByTokenHash.TryGetValue(HashToken(token), out found)) {
        return CalendarTokenState.Unknown;
      }
      if (found.ExpiresUtc <= _UtcNow()) {
        if (_GrantsByTokenHash.TryRemove(HashToken(token), out found)) {
          this.Persist();
        }
        return CalendarTokenState.Expired;
      }
      grant = found;
      return CalendarTokenState.Valid;
    }

    /// <summary>
    /// Revokes a single access token.
    /// </summary>
    public bool RevokeAccessToken(string rawTokenOrAuthHeader) {
      string token = StripBearerPrefix(rawTokenOrAuthHeader);
      if (string.IsNullOrWhiteSpace(token)) {
        return false;
      }
      CalendarGrant removed;
      bool wasRemoved = _GrantsByTokenHash.TryRemove(HashToken(token), out removed);
      if (wasRemoved) {
        this.Persist();
      }
      return wasRemoved;
    }

    /// <summary>
    /// Revokes all access tokens of the given subject (e.g. after refresh-token reuse was detected).
    /// </summary>
    public int RevokeAllAccessTokensOfSubject(string subject) {
      int count = 0;
      foreach (KeyValuePair<string, CalendarGrant> entry in _GrantsByTokenHash) {
        if (string.Equals(entry.Value.Subject, subject, StringComparison.OrdinalIgnoreCase)) {
          CalendarGrant removed;
          if (_GrantsByTokenHash.TryRemove(entry.Key, out removed)) {
            count++;
          }
        }
      }
      if (count > 0) {
        this.Persist();
      }
      return count;
    }

    /// <summary>
    /// Removes the 'Bearer ' prefix of an authorization header value (if present).
    /// </summary>
    public static string StripBearerPrefix(string rawTokenOrAuthHeader) {
      if (string.IsNullOrWhiteSpace(rawTokenOrAuthHeader)) {
        return null;
      }
      string value = rawTokenOrAuthHeader.Trim();
      if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
        value = value.Substring(7).Trim();
      }
      return value;
    }

    private void RemoveExpiredGrants() {
      DateTime now = _UtcNow();
      foreach (KeyValuePair<string, CalendarGrant> entry in _GrantsByTokenHash) {
        if (entry.Value.ExpiresUtc <= now) {
          CalendarGrant removed;
          _GrantsByTokenHash.TryRemove(entry.Key, out removed);
        }
      }
    }

    /// <summary>
    /// Writes a snapshot of all grants (if a persistence is attached).
    /// </summary>
    private void Persist() {
      CalendarOAuthStatePersistence persistence = _Persistence;
      if (persistence == null) {
        return;
      }
      persistence.Save(_PersistenceSectionName, () => {
        JArray entries = new JArray();
        foreach (KeyValuePair<string, CalendarGrant> entry in _GrantsByTokenHash) {
          JObject item = new JObject();
          item["hash"] = entry.Key;
          item["sub"] = entry.Value.Subject;
          CalendarOAuthStatePersistence.WriteCredentials(item, entry.Value.Credentials);
          item["scopes"] = CalendarOAuthStatePersistence.ToJArray(entry.Value.Scopes);
          item["resource"] = entry.Value.Resource;
          item["exp"] = CalendarOAuthStatePersistence.ToUnixMilliseconds(entry.Value.ExpiresUtc);
          entries.Add(item);
        }
        return entries;
      });
    }

    private static string CreateRandomToken() {
      byte[] bytes = RandomNumberGenerator.GetBytes(32);
      return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string token) {
      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
      return Convert.ToHexString(hash);
    }

  }

  /// <summary>
  /// Result of an access-token evaluation.
  /// </summary>
  public enum CalendarTokenState {
    Missing = 0,
    Unknown = 1,
    Expired = 2,
    Valid = 3
  }

  /// <summary>
  /// A server-side grant which belongs to an access token.
  /// </summary>
  public sealed class CalendarGrant {

    private readonly string _Subject;
    private readonly CalDavCredentials _Credentials;
    private readonly string[] _Scopes;
    private readonly string _Resource;
    private readonly DateTime _ExpiresUtc;

    public CalendarGrant(string subject, CalDavCredentials credentials, string[] scopes, string resource, DateTime expiresUtc) {
      _Subject = subject;
      _Credentials = credentials;
      _Scopes = scopes;
      _Resource = resource;
      _ExpiresUtc = expiresUtc;
    }

    public string Subject {
      get {
        return _Subject;
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

    /// <summary>
    /// Returns true if the given scope was granted.
    /// </summary>
    public bool HasScope(string scope) {
      foreach (string grantedScope in _Scopes) {
        if (string.Equals(grantedScope, scope, StringComparison.OrdinalIgnoreCase)) {
          return true;
        }
      }
      return false;
    }

  }

}
