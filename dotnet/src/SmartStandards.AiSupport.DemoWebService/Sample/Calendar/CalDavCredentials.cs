using System;
using System.Text;

namespace AI.SmartStandards.DemoWebService.Sample.Calendar {

  /// <summary>
  /// Immutable CalDAV credentials (user name and password) which are applied per request (HTTP Basic).
  /// The values are never part of ToString() to avoid accidental logging.
  /// </summary>
  public sealed class CalDavCredentials {

    private readonly string _UserName;
    private readonly string _Password;

    /// <summary>
    /// Creates a new credential value object.
    /// </summary>
    public CalDavCredentials(string userName, string password) {
      if (string.IsNullOrWhiteSpace(userName)) {
        throw new ArgumentException("The user name must not be empty.", nameof(userName));
      }
      if (string.IsNullOrEmpty(password)) {
        throw new ArgumentException("The password must not be empty.", nameof(password));
      }
      if (userName.Contains(":")) {
        throw new ArgumentException("The user name must not contain ':'.", nameof(userName));
      }
      _UserName = userName.Trim();
      _Password = password;
    }

    public string UserName {
      get {
        return _UserName;
      }
    }

    public string Password {
      get {
        return _Password;
      }
    }

    /// <summary>
    /// Builds the value (parameter part) of a HTTP Basic Authorization header.
    /// </summary>
    public string CreateBasicAuthorizationParameter() {
      byte[] raw = Encoding.UTF8.GetBytes(_UserName + ":" + _Password);
      return Convert.ToBase64String(raw);
    }

    /// <summary>
    /// Returns a representation without any secret.
    /// </summary>
    public override string ToString() {
      return "CalDavCredentials(user: " + _UserName + ")";
    }

  }

  /// <summary>
  /// Provides the CalDAV credentials of the current call (e.g. resolved server-side from an access token).
  /// Returning null means: no credentials available, requests are sent unauthenticated.
  /// </summary>
  public interface ICalDavCredentialSource {

    /// <summary>
    /// Returns the credentials for the current call or null.
    /// </summary>
    CalDavCredentials GetCurrentCredentials();

  }

  /// <summary>
  /// Validates CalDAV credentials against the calendar backend (real CalDAV server or local mock).
  /// Used by the OAuth login - there is no separate demo password.
  /// </summary>
  public interface ICalDavCredentialValidator {

    /// <summary>
    /// Validates the given credentials against the calendar backend.
    /// </summary>
    CalDavCredentialValidationResult ValidateCredentials(CalDavCredentials credentials);

  }

  /// <summary>
  /// Result of a credential validation against the CalDAV server.
  /// </summary>
  public enum CalDavCredentialValidationResult {
    Valid = 1,
    Rejected = 0,
    ServerUnavailable = -1
  }

}
