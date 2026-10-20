using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace AI.SmartStandards.DemoWebService.Sample.Calendar {

  /// <summary>
  /// ICalendarAccess implementation that acts as a proxy to a CalDAV server (RFC 4791).
  /// Only resources below the configured calendar home are contacted; redirects are never followed.
  /// Credentials are resolved per call from an optional ICalDavCredentialSource and applied to each single
  /// HttpRequestMessage (never as default header of the shared HttpClient).
  /// </summary>
  public class CalDavCalendarAccess : ICalendarAccess, ICalDavCredentialValidator, IDisposable {

    private static readonly XNamespace _DavNs = "DAV:";
    private static readonly XNamespace _CalDavNs = "urn:ietf:params:xml:ns:caldav";
    private const int _MaxRangeDays = 366;

    private readonly Uri _CalendarHome;
    private readonly string _ConfigurationError;
    private readonly HttpClient _HttpClient;
    private readonly ICalDavCredentialSource _CredentialSource;

    /// <summary>
    /// Creates a new (unauthenticated) CalDAV proxy for the configured calendar home.
    /// An invalid or missing configuration does not fail here (the host must still start), but every call
    /// reports it as a clear InvalidOperationException.
    /// </summary>
    public CalDavCalendarAccess(CalDavCalendarAccessOptions options) : this(options, null, null) {
    }

    /// <summary>
    /// Creates a new CalDAV proxy which resolves the credentials for every call from the given source.
    /// </summary>
    public CalDavCalendarAccess(CalDavCalendarAccessOptions options, ICalDavCredentialSource credentialSource) : this(options, credentialSource, null) {
    }

    /// <summary>
    /// Creates a new CalDAV proxy using a custom HTTP message handler (e.g. a fake server for tests).
    /// If no handler is given, a handler without redirects, cookies and default credentials is created.
    /// </summary>
    public CalDavCalendarAccess(CalDavCalendarAccessOptions options, ICalDavCredentialSource credentialSource, HttpMessageHandler handler) {
      if (options == null) {
        throw new ArgumentNullException(nameof(options));
      }

      _CalendarHome = TryCreateCalendarHome(options.CalendarHomeUrl, out _ConfigurationError);
      _CredentialSource = credentialSource;

      if (handler == null) {
        HttpClientHandler defaultHandler = new HttpClientHandler();
        defaultHandler.AllowAutoRedirect = false;
        defaultHandler.UseCookies = false;
        defaultHandler.UseDefaultCredentials = false;
        handler = defaultHandler;
      }
      _HttpClient = new HttpClient(handler, true);
      _HttpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    /// <summary>
    /// Validates the given credentials with an authenticated PROPFIND (Depth: 0) on the configured calendar home.
    /// </summary>
    public CalDavCredentialValidationResult ValidateCredentials(CalDavCredentials credentials) {
      this.EnsureConfigured();
      if (credentials == null) {
        throw new ArgumentNullException(nameof(credentials));
      }
      string body = (
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>"
      );
      try {
        using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _CalendarHome)) {
          request.Headers.Add("Depth", "0");
          request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
          using (HttpResponseMessage response = this.Send(request, credentials)) {
            int statusCode = (int)response.StatusCode;
            if (statusCode == 207) {
              return CalDavCredentialValidationResult.Valid;
            }
            if (statusCode == 401 || statusCode == 403) {
              return CalDavCredentialValidationResult.Rejected;
            }
            return CalDavCredentialValidationResult.ServerUnavailable;
          }
        }
      }
      catch (HttpRequestException) {
        return CalDavCredentialValidationResult.ServerUnavailable;
      }
      catch (TaskCanceledException) {
        return CalDavCredentialValidationResult.ServerUnavailable;
      }
    }

    /// <summary>
    /// Sends a request with request-local credentials (resolved for the current call if not given explicitly).
    /// </summary>
    private HttpResponseMessage Send(HttpRequestMessage request, CalDavCredentials explicitCredentials) {
      CalDavCredentials credentials = explicitCredentials;
      if (credentials == null && _CredentialSource != null) {
        credentials = _CredentialSource.GetCurrentCredentials();
      }
      if (credentials != null) {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials.CreateBasicAuthorizationParameter());
      }
      return _HttpClient.Send(request);
    }

    /// <summary>
    /// Validates and normalizes the configured calendar home URL (returns null and an error message if invalid).
    /// </summary>
    private static Uri TryCreateCalendarHome(string calendarHomeUrl, out string configurationError) {
      configurationError = null;
      if (string.IsNullOrWhiteSpace(calendarHomeUrl)) {
        configurationError = "The calendar service is not configured (setting 'CalDav:CalendarHomeUrl' is missing).";
        return null;
      }

      Uri calendarHome;
      if (!Uri.TryCreate(calendarHomeUrl, UriKind.Absolute, out calendarHome)) {
        configurationError = "The calendar service is misconfigured (setting 'CalDav:CalendarHomeUrl' is not an absolute URL).";
        return null;
      }
      if (calendarHome.Scheme != Uri.UriSchemeHttps && calendarHome.Scheme != Uri.UriSchemeHttp) {
        configurationError = "The calendar service is misconfigured (setting 'CalDav:CalendarHomeUrl' must use http or https).";
        return null;
      }
      if (!calendarHome.AbsolutePath.EndsWith("/", StringComparison.Ordinal)) {
        calendarHome = new Uri(calendarHome.GetLeftPart(UriPartial.Path) + "/");
      }
      return calendarHome;
    }

    /// <summary>
    /// Throws a clear exception if the service is not (correctly) configured.
    /// </summary>
    private void EnsureConfigured() {
      if (_CalendarHome == null) {
        throw new InvalidOperationException(_ConfigurationError);
      }
    }

    /// <summary>
    /// Lists all calendar collections directly below the configured calendar home.
    /// </summary>
    public CalendarInfo[] GetCalendars() {
      this.EnsureConfigured();
      string body = (
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
        "<d:prop><d:resourcetype/><d:displayname/><c:calendar-description/></d:prop>" +
        "</d:propfind>"
      );

      XDocument multistatus = this.SendDavRequest("PROPFIND", _CalendarHome, "1", body);
      List<CalendarInfo> calendars = new List<CalendarInfo>();

      foreach (XElement response in multistatus.Root.Elements(_DavNs + "response")) {
        XElement prop = GetSuccessfulProp(response);
        if (prop == null) {
          continue;
        }

        XElement resourceType = prop.Element(_DavNs + "resourcetype");
        if (resourceType == null || resourceType.Element(_CalDavNs + "calendar") == null) {
          continue;
        }

        string calendarId = this.GetRelativeId(response.Element(_DavNs + "href"));
        if (string.IsNullOrEmpty(calendarId)) {
          continue;
        }

        CalendarInfo calendar = new CalendarInfo();
        calendar.Id = calendarId;
        calendar.DisplayName = GetElementValue(prop, _DavNs + "displayname");
        if (string.IsNullOrWhiteSpace(calendar.DisplayName)) {
          calendar.DisplayName = calendarId.TrimEnd('/');
        }
        calendar.Description = GetElementValue(prop, _CalDavNs + "calendar-description");
        calendars.Add(calendar);
      }

      return calendars.OrderBy((c) => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Lists the events of a calendar within the given time range via a CalDAV calendar-query REPORT.
    /// </summary>
    public CalendarEvent[] GetEvents(string calendarId, DateTime fromUtc, DateTime toUtc) {
      this.EnsureConfigured();
      DateTime from = ToUtc(fromUtc);
      DateTime to = ToUtc(toUtc);
      if (to <= from) {
        throw new ArgumentException("The end of the time range must be after its start.");
      }
      if ((to - from).TotalDays > _MaxRangeDays) {
        throw new ArgumentException("The time range must not exceed " + _MaxRangeDays + " days.");
      }

      Uri calendarUrl = this.ResolveCalendarUrl(calendarId);
      string start = from.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
      string end = to.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

      string body = (
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
        "<d:prop><d:getetag/><c:calendar-data><c:expand start=\"" + start + "\" end=\"" + end + "\"/></c:calendar-data></d:prop>" +
        "<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\">" +
        "<c:time-range start=\"" + start + "\" end=\"" + end + "\"/>" +
        "</c:comp-filter></c:comp-filter></c:filter>" +
        "</c:calendar-query>"
      );

      XDocument multistatus = this.SendDavRequest("REPORT", calendarUrl, "1", body);
      List<CalendarEvent> events = new List<CalendarEvent>();

      foreach (XElement response in multistatus.Root.Elements(_DavNs + "response")) {
        XElement prop = GetSuccessfulProp(response);
        if (prop == null) {
          continue;
        }

        string calendarData = GetElementValue(prop, _CalDavNs + "calendar-data");
        if (string.IsNullOrWhiteSpace(calendarData)) {
          continue;
        }

        string etag = GetElementValue(prop, _DavNs + "getetag");
        CalendarEvent[] parsedEvents = ICalendarParser.ParseEvents(calendarData);
        foreach (CalendarEvent parsedEvent in parsedEvents) {
          parsedEvent.ETag = etag;
          events.Add(parsedEvent);
        }
      }

      return events.OrderBy((e) => e.Start).ToArray();
    }

    /// <summary>
    /// Creates a new event as own calendar resource '{uid}.ics' (PUT with If-None-Match: *).
    /// </summary>
    public CalendarEvent CreateEvent(string calendarId, CalendarEvent newEvent) {
      this.EnsureConfigured();
      ValidateEventData(newEvent);
      Uri calendarUrl = this.ResolveCalendarUrl(calendarId);

      string uid = Guid.NewGuid().ToString("D");
      Uri resourceUrl = new Uri(calendarUrl, Uri.EscapeDataString(uid) + ".ics");
      string iCalendarText = ICalendarWriter.WriteEvent(uid, newEvent, DateTime.UtcNow);

      this.PutResource(resourceUrl, iCalendarText, null, true);
      return this.ReadEventResource(calendarUrl, uid);
    }

    /// <summary>
    /// Replaces an existing non-recurring event (PUT on its resource, optionally guarded by If-Match).
    /// </summary>
    public CalendarEvent UpdateEvent(string calendarId, CalendarEvent updatedEvent) {
      this.EnsureConfigured();
      ValidateEventData(updatedEvent);
      if (string.IsNullOrWhiteSpace(updatedEvent.Uid)) {
        throw new ArgumentException("The Uid of the event to update is missing.");
      }
      Uri calendarUrl = this.ResolveCalendarUrl(calendarId);

      EventResource existing = this.FindEventResource(calendarUrl, updatedEvent.Uid);
      if (existing == null) {
        throw new KeyNotFoundException("The event '" + updatedEvent.Uid + "' does not exist in calendar '" + calendarId + "'.");
      }
      if (existing.IsRecurring) {
        throw new NotSupportedException("Recurring events cannot be updated by this service (the recurrence rule would be lost).");
      }

      string iCalendarText = ICalendarWriter.WriteEvent(updatedEvent.Uid, updatedEvent, DateTime.UtcNow);
      this.PutResource(existing.Url, iCalendarText, updatedEvent.ETag, false);
      return this.ReadEventResource(calendarUrl, updatedEvent.Uid);
    }

    /// <summary>
    /// Deletes the resource that contains the event (optionally guarded by If-Match).
    /// </summary>
    public bool DeleteEvent(string calendarId, string eventUid, string eTag = null) {
      this.EnsureConfigured();
      if (string.IsNullOrWhiteSpace(eventUid)) {
        throw new ArgumentException("The Uid of the event to delete is missing.");
      }
      Uri calendarUrl = this.ResolveCalendarUrl(calendarId);

      EventResource existing = this.FindEventResource(calendarUrl, eventUid);
      if (existing == null) {
        return false;
      }

      using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, existing.Url)) {
        AddIfMatch(request, eTag);
        using (HttpResponseMessage response = this.Send(request, null)) {
          int statusCode = (int)response.StatusCode;
          ThrowOnCommonWriteFailures(statusCode);
          if (statusCode == 404) {
            return false;
          }
          if (statusCode < 200 || statusCode >= 300) {
            throw new InvalidOperationException("The calendar server rejected the deletion (HTTP " + statusCode + ").");
          }
          return true;
        }
      }
    }

    /// <summary>
    /// Validates the event data that is required for create/update.
    /// </summary>
    private static void ValidateEventData(CalendarEvent calendarEvent) {
      if (calendarEvent == null) {
        throw new ArgumentException("The event data is missing.");
      }
      if (string.IsNullOrWhiteSpace(calendarEvent.Summary)) {
        throw new ArgumentException("The event needs a Summary (title).");
      }
      if (calendarEvent.Start == DateTime.MinValue || calendarEvent.End == DateTime.MinValue) {
        throw new ArgumentException("The event needs a Start and an End.");
      }
      if (calendarEvent.End <= calendarEvent.Start) {
        throw new ArgumentException("The End of the event must be after its Start (for all-day events: End is exclusive).");
      }
      if (!string.IsNullOrWhiteSpace(calendarEvent.RecurrenceRule)) {
        throw new NotSupportedException("Creating or updating recurring events is not supported by this service.");
      }
    }

    /// <summary>
    /// Writes an iCalendar resource via PUT (create: If-None-Match: *, update: optional If-Match).
    /// </summary>
    private void PutResource(Uri resourceUrl, string iCalendarText, string eTag, bool createOnly) {
      using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, resourceUrl)) {
        request.Content = new StringContent(iCalendarText, Encoding.UTF8, "text/calendar");
        if (createOnly) {
          request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }
        else {
          AddIfMatch(request, eTag);
        }

        using (HttpResponseMessage response = this.Send(request, null)) {
          int statusCode = (int)response.StatusCode;
          ThrowOnCommonWriteFailures(statusCode);
          if (statusCode < 200 || statusCode >= 300) {
            string responseBody = ReadBody(response);
            string reason = string.Empty;
            if (!string.IsNullOrWhiteSpace(responseBody) && responseBody.Length <= 500) {
              reason = " " + responseBody.Trim();
            }
            throw new InvalidOperationException("The calendar server rejected the event (HTTP " + statusCode + ")." + reason);
          }
        }
      }
    }

    /// <summary>
    /// Adds an If-Match header for optimistic concurrency (ETags are passed through exactly as received).
    /// </summary>
    private static void AddIfMatch(HttpRequestMessage request, string eTag) {
      if (!string.IsNullOrWhiteSpace(eTag)) {
        request.Headers.TryAddWithoutValidation("If-Match", eTag.Trim());
      }
    }

    /// <summary>
    /// Maps status codes that are common for all write requests to clear exceptions.
    /// </summary>
    private static void ThrowOnCommonWriteFailures(int statusCode) {
      if (statusCode == 401 || statusCode == 403) {
        throw new UnauthorizedAccessException("The calendar server rejected the request (HTTP " + statusCode + "). The credentials are missing, invalid or lack the required permissions.");
      }
      if (statusCode >= 300 && statusCode < 400) {
        throw new InvalidOperationException("The calendar server answered with a redirect (HTTP " + statusCode + "), which is not followed for security reasons.");
      }
      if (statusCode == 412) {
        throw new InvalidOperationException("The event was modified or created by someone else in the meantime. Read it again and retry.");
      }
    }

    /// <summary>
    /// Reads the (first) event with the given UID including its ETag.
    /// </summary>
    private CalendarEvent ReadEventResource(Uri calendarUrl, string uid) {
      EventResource resource = this.FindEventResource(calendarUrl, uid);
      if (resource == null || resource.Events.Length == 0) {
        throw new InvalidOperationException("The event '" + uid + "' was written but could not be read back from the calendar server.");
      }
      return resource.Events[0];
    }

    /// <summary>
    /// Finds the calendar resource that contains the event with the given UID (calendar-query with UID text-match).
    /// Returns null if no such event exists.
    /// </summary>
    private EventResource FindEventResource(Uri calendarUrl, string uid) {
      string body = (
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
        "<d:prop><d:getetag/><c:calendar-data/></d:prop>" +
        "<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\">" +
        "<c:prop-filter name=\"UID\"><c:text-match collation=\"i;octet\">" + SecurityElementEscape(uid) + "</c:text-match></c:prop-filter>" +
        "</c:comp-filter></c:comp-filter></c:filter>" +
        "</c:calendar-query>"
      );

      XDocument multistatus = this.SendDavRequest("REPORT", calendarUrl, "1", body);
      foreach (XElement response in multistatus.Root.Elements(_DavNs + "response")) {
        XElement prop = GetSuccessfulProp(response);
        XElement hrefElement = response.Element(_DavNs + "href");
        if (prop == null || hrefElement == null) {
          continue;
        }

        Uri resourceUrl = new Uri(calendarUrl, hrefElement.Value.Trim());
        if (!this.IsBelowCalendarHome(resourceUrl)) {
          continue;
        }

        string calendarData = GetElementValue(prop, _CalDavNs + "calendar-data");
        if (string.IsNullOrWhiteSpace(calendarData)) {
          continue;
        }

        string etag = GetElementValue(prop, _DavNs + "getetag");
        CalendarEvent[] events = ICalendarParser.ParseEvents(calendarData).Where((e) => string.Equals(e.Uid, uid, StringComparison.Ordinal)).ToArray();
        if (events.Length == 0) {
          continue;
        }
        foreach (CalendarEvent calendarEvent in events) {
          calendarEvent.ETag = etag;
        }
        return new EventResource(resourceUrl, events);
      }
      return null;
    }

    /// <summary>
    /// Escapes a value for use as XML text content.
    /// </summary>
    private static string SecurityElementEscape(string value) {
      return new XText(value).ToString();
    }

    /// <summary>
    /// A calendar resource (.ics) and the events (master + overrides) it contains.
    /// </summary>
    private sealed class EventResource {

      private readonly Uri _Url;
      private readonly CalendarEvent[] _Events;

      public EventResource(Uri url, CalendarEvent[] events) {
        _Url = url;
        _Events = events;
      }

      public Uri Url {
        get {
          return _Url;
        }
      }

      public CalendarEvent[] Events {
        get {
          return _Events;
        }
      }

      public bool IsRecurring {
        get {
          return _Events.Length > 1 || _Events.Any((e) => e.IsRecurring);
        }
      }

    }

    /// <summary>
    /// Releases the underlying HTTP client.
    /// </summary>
    public void Dispose() {
      _HttpClient.Dispose();
    }

    /// <summary>
    /// Resolves a calendar id to an absolute URL and ensures that it stays below the configured calendar home.
    /// </summary>
    private Uri ResolveCalendarUrl(string calendarId) {
      if (string.IsNullOrWhiteSpace(calendarId)) {
        throw new ArgumentException("The calendar id must not be empty.");
      }
      if (calendarId.Contains("..") || calendarId.Contains("?") || calendarId.Contains("#") || calendarId.Contains("://") || calendarId.StartsWith("/", StringComparison.Ordinal)) {
        throw new ArgumentException("The calendar id '" + calendarId + "' is invalid. Use an id from the list of calendars.");
      }

      string relativePath = calendarId;
      if (!relativePath.EndsWith("/", StringComparison.Ordinal)) {
        relativePath = relativePath + "/";
      }

      Uri calendarUrl = new Uri(_CalendarHome, relativePath);
      if (!this.IsBelowCalendarHome(calendarUrl)) {
        throw new ArgumentException("The calendar id '" + calendarId + "' does not address a calendar below the configured calendar home.");
      }
      return calendarUrl;
    }

    /// <summary>
    /// Returns true if the given URL has the same origin as the calendar home and lies below its path.
    /// </summary>
    private bool IsBelowCalendarHome(Uri url) {
      if (Uri.Compare(url, _CalendarHome, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0) {
        return false;
      }
      return url.AbsolutePath.StartsWith(_CalendarHome.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Converts a DAV:href into an id relative to the calendar home (null for the home itself or foreign hrefs).
    /// </summary>
    private string GetRelativeId(XElement hrefElement) {
      if (hrefElement == null || string.IsNullOrWhiteSpace(hrefElement.Value)) {
        return null;
      }

      Uri absoluteUrl = new Uri(_CalendarHome, hrefElement.Value.Trim());
      if (!this.IsBelowCalendarHome(absoluteUrl)) {
        return null;
      }

      string relativePath = absoluteUrl.AbsolutePath.Substring(_CalendarHome.AbsolutePath.Length);
      return Uri.UnescapeDataString(relativePath);
    }

    /// <summary>
    /// Sends a WebDAV request and returns the parsed multistatus response. Throws on non-207 responses.
    /// </summary>
    private XDocument SendDavRequest(string method, Uri url, string depth, string xmlBody) {
      using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), url)) {
        request.Headers.Add("Depth", depth);
        request.Content = new StringContent(xmlBody, Encoding.UTF8, "application/xml");

        using (HttpResponseMessage response = this.Send(request, null)) {
          string responseBody = ReadBody(response);
          int statusCode = (int)response.StatusCode;

          if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) {
            throw new UnauthorizedAccessException("The CalDAV server rejected the request (HTTP " + statusCode + "). The credentials are missing, invalid or lack the required permissions.");
          }
          if (statusCode >= 300 && statusCode < 400) {
            throw new InvalidOperationException("The CalDAV server answered with a redirect (HTTP " + statusCode + "), which is not followed for security reasons. Check the configured calendar home URL.");
          }
          if (statusCode != 207) {
            throw new InvalidOperationException("The CalDAV server answered " + method + " with HTTP " + statusCode + " instead of 207 Multi-Status.");
          }

          try {
            return XDocument.Parse(responseBody);
          }
          catch (XmlException ex) {
            throw new InvalidOperationException("The CalDAV server returned an invalid multistatus document: " + ex.Message, ex);
          }
        }
      }
    }

    /// <summary>
    /// Reads the response body synchronously.
    /// </summary>
    private static string ReadBody(HttpResponseMessage response) {
      using (Stream stream = response.Content.ReadAsStream()) {
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) {
          return reader.ReadToEnd();
        }
      }
    }

    /// <summary>
    /// Returns the DAV:prop element of the first propstat with a 2xx status, or null.
    /// </summary>
    private static XElement GetSuccessfulProp(XElement response) {
      foreach (XElement propstat in response.Elements(_DavNs + "propstat")) {
        string status = GetElementValue(propstat, _DavNs + "status");
        if (status != null && status.Contains(" 200 ")) {
          return propstat.Element(_DavNs + "prop");
        }
      }
      return null;
    }

    /// <summary>
    /// Returns the trimmed value of a child element, or null if it does not exist.
    /// </summary>
    private static string GetElementValue(XElement parent, XName name) {
      XElement element = parent.Element(name);
      if (element == null) {
        return null;
      }
      return element.Value.Trim();
    }

    /// <summary>
    /// Normalizes a DateTime to UTC (unspecified values are treated as UTC).
    /// </summary>
    private static DateTime ToUtc(DateTime value) {
      if (value.Kind == DateTimeKind.Local) {
        return value.ToUniversalTime();
      }
      return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

  }

}
