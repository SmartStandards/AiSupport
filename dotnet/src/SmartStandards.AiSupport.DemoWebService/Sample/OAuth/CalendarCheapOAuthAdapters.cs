using AI.SmartStandards.DemoWebService.Sample.Calendar;
using Microsoft.AspNetCore.Http;
using Security.AccessTokenHandling.OAuth.Server;
using System;

namespace AI.SmartStandards.DemoWebService.Sample.OAuth {

  /// <summary>
  /// Connects the login page of the Cheap OAuth Server (see 'AddCheapOAuthServer' of AuthTokenHandling)
  /// with the calendar backend: the login is valid if the CalDAV server (or the local mock) accepts the credentials.
  /// The password is retained in the grant, because it is required server-side for the backend calls.
  /// </summary>
  public sealed class CalDavCheapOAuthLoginValidator {

    private readonly Func<ICalDavCredentialValidator> _CalDavValidatorResolver;
    private readonly string[] _GrantedScopes;

    /// <summary>
    /// Creates the validator.
    /// </summary>
    /// <param name="calDavValidatorResolver">
    /// resolves the CalDAV validator lazily (the Cheap OAuth Server is configured before the service provider exists)
    /// </param>
    /// <param name="grantedScopes">the scopes which are granted on a successful login (without API permission prefix)</param>
    public CalDavCheapOAuthLoginValidator(Func<ICalDavCredentialValidator> calDavValidatorResolver, string[] grantedScopes) {
      if (calDavValidatorResolver == null) {
        throw new ArgumentNullException(nameof(calDavValidatorResolver));
      }
      if (grantedScopes == null) {
        throw new ArgumentNullException(nameof(grantedScopes));
      }
      _CalDavValidatorResolver = calDavValidatorResolver;
      _GrantedScopes = grantedScopes;
    }

    /// <summary>
    /// Validates the credentials against the calendar backend (signature of 'CheapOAuthOptions.ValidateLogin').
    /// </summary>
    public CheapOAuthLoginResult ValidateLogin(string userName, string password) {
      ICalDavCredentialValidator calDavValidator = _CalDavValidatorResolver.Invoke();
      CalDavCredentialValidationResult result = calDavValidator.ValidateCredentials(new CalDavCredentials(userName, password));
      if (result == CalDavCredentialValidationResult.Valid) {
        return CheapOAuthLoginResult.Valid(_GrantedScopes, password);
      }
      if (result == CalDavCredentialValidationResult.ServerUnavailable) {
        return CheapOAuthLoginResult.BackendUnavailable();
      }
      return CheapOAuthLoginResult.Invalid();
    }

  }

  /// <summary>
  /// Resolves the CalDAV credentials of the current call server-side from the access token
  /// in the 'Authorization' header of the current HTTP request (never from tool arguments).
  /// Requires the password to be retained in the grant (see 'CalDavCheapOAuthLoginValidator').
  /// </summary>
  public sealed class CheapOAuthCalDavCredentialSource : ICalDavCredentialSource {

    private readonly IHttpContextAccessor _HttpContextAccessor;
    private readonly CheapOAuthGrantStore _GrantStore;

    public CheapOAuthCalDavCredentialSource(IHttpContextAccessor httpContextAccessor, CheapOAuthGrantStore grantStore) {
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
      string authHeader = context.Request.Headers["Authorization"].ToString();
      if (string.IsNullOrWhiteSpace(authHeader)) {
        return null;
      }
      CheapOAuthGrant grant;
      if (!_GrantStore.TryGetGrant(authHeader, out grant)) {
        return null;
      }
      if (grant.RetainedSecret == null) {
        return null;
      }
      return new CalDavCredentials(grant.Subject, grant.RetainedSecret);
    }

  }

}
