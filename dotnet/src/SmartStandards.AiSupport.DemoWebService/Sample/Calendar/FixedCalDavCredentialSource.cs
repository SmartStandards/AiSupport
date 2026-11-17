using System;

namespace AI.SmartStandards.DemoWebService.Sample.Calendar {

  /// <summary>
  /// Provides fixed, server-side configured CalDAV credentials (for example for the calendar tools of the
  /// embedded chat UI, whose requests do not carry an OAuth access token). The credentials are never
  /// exposed to the model (they are not part of any tool argument or result).
  /// </summary>
  public sealed class FixedCalDavCredentialSource : ICalDavCredentialSource {

    private readonly CalDavCredentials _Credentials;

    /// <summary>
    /// Creates the source for the given credentials.
    /// </summary>
    public FixedCalDavCredentialSource(CalDavCredentials credentials) {

      if (credentials == null) {
        throw new ArgumentNullException(nameof(credentials));
      }

      _Credentials = credentials;

    }

    /// <summary>
    /// Returns the configured credentials.
    /// </summary>
    public CalDavCredentials GetCurrentCredentials() {

      return _Credentials;
    }

  }

}
