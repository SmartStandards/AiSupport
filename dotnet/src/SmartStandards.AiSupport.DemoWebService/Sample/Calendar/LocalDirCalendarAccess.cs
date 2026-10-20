using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AI.SmartStandards.DemoWebService.Sample.Calendar {

  /// <summary>
  /// LOCAL TESTING ONLY: a CalDAV substitute which persists events as iCalendar files on the local disk
  /// ('{MockToLocalDir}\{userName}\{calendarId}\{uid}.ics'). It behaves like a CalDAV server regarding authentication:
  /// every operation requires the credentials of the current call (resolved server-side from the access token)
  /// and they must match 'MockAuthUser'/'MockAuthPass'. The OAuth/MCP flow in front of it stays fully active.
  /// </summary>
  public class LocalDirCalendarAccess : ICalendarAccess, ICalDavCredentialValidator {

    private const int _MaxRangeDays = 366;
    private const string _DefaultCalendarId = "personal";

    private static readonly object _FileSystemLock = new object();

    private readonly string _RootDirectory;
    private readonly string _MockAuthUser;
    private readonly string _MockAuthPass;
    private readonly ICalDavCredentialSource _CredentialSource;

    /// <summary>
    /// Creates the local mock. The credential source provides the credentials of the current call.
    /// </summary>
    public LocalDirCalendarAccess(CalDavCalendarAccessOptions options, ICalDavCredentialSource credentialSource) {
      if (options == null) {
        throw new ArgumentNullException(nameof(options));
      }
      if (credentialSource == null) {
        throw new ArgumentNullException(nameof(credentialSource));
      }
      if (string.IsNullOrWhiteSpace(options.MockToLocalDir)) {
        throw new ArgumentException("The setting 'CalDav:MockToLocalDir' is required for the local mock.");
      }
      if (string.IsNullOrWhiteSpace(options.MockAuthUser) || string.IsNullOrEmpty(options.MockAuthPass)) {
        throw new ArgumentException("The local CalDAV mock requires the settings 'CalDav:MockAuthUser' and 'CalDav:MockAuthPass'.");
      }
      _RootDirectory = Path.GetFullPath(options.MockToLocalDir);
      _MockAuthUser = options.MockAuthUser.Trim();
      _MockAuthPass = options.MockAuthPass;
      _CredentialSource = credentialSource;
      Directory.CreateDirectory(_RootDirectory);
    }

    /// <summary>
    /// Validates credentials like a CalDAV server would do (constant-time comparison of user and password).
    /// </summary>
    public CalDavCredentialValidationResult ValidateCredentials(CalDavCredentials credentials) {
      if (credentials == null) {
        return CalDavCredentialValidationResult.Rejected;
      }
      bool userMatches = FixedTimeEquals(credentials.UserName.ToLowerInvariant(), _MockAuthUser.ToLowerInvariant());
      bool passwordMatches = FixedTimeEquals(credentials.Password, _MockAuthPass);
      if (userMatches && passwordMatches) {
        return CalDavCredentialValidationResult.Valid;
      }
      return CalDavCredentialValidationResult.Rejected;
    }

    /// <summary>
    /// Lists the calendars (sub directories) of the authenticated user. A default calendar is created on first use.
    /// </summary>
    public CalendarInfo[] GetCalendars() {
      string userDirectory = this.GetAuthenticatedUserDirectory();
      lock (_FileSystemLock) {
        if (!Directory.EnumerateDirectories(userDirectory).Any()) {
          Directory.CreateDirectory(Path.Combine(userDirectory, _DefaultCalendarId));
        }
        List<CalendarInfo> calendars = new List<CalendarInfo>();
        foreach (string calendarDirectory in Directory.EnumerateDirectories(userDirectory)) {
          CalendarInfo calendar = new CalendarInfo();
          calendar.Id = Path.GetFileName(calendarDirectory) + "/";
          calendar.DisplayName = Path.GetFileName(calendarDirectory);
          calendar.Description = "Local mock calendar (" + calendarDirectory + ")";
          calendars.Add(calendar);
        }
        return calendars.OrderBy((c) => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
      }
    }

    /// <summary>
    /// Lists the events of a calendar which overlap the given time range.
    /// </summary>
    public CalendarEvent[] GetEvents(string calendarId, DateTime fromUtc, DateTime toUtc) {
      DateTime from = ToUtc(fromUtc);
      DateTime to = ToUtc(toUtc);
      if (to <= from) {
        throw new ArgumentException("The end of the time range must be after its start.");
      }
      if ((to - from).TotalDays > _MaxRangeDays) {
        throw new ArgumentException("The time range must not exceed " + _MaxRangeDays + " days.");
      }
      string calendarDirectory = this.ResolveCalendarDirectory(calendarId, false);
      List<CalendarEvent> result = new List<CalendarEvent>();
      lock (_FileSystemLock) {
        foreach (string file in Directory.EnumerateFiles(calendarDirectory, "*.ics")) {
          foreach (CalendarEvent calendarEvent in ReadEventFile(file)) {
            if (ToUtc(calendarEvent.Start) < to && ToUtc(calendarEvent.End) > from) {
              result.Add(calendarEvent);
            }
          }
        }
      }
      return result.OrderBy((e) => e.Start).ToArray();
    }

    /// <summary>
    /// Creates a new event file '{uid}.ics'.
    /// </summary>
    public CalendarEvent CreateEvent(string calendarId, CalendarEvent newEvent) {
      ValidateEventData(newEvent);
      string calendarDirectory = this.ResolveCalendarDirectory(calendarId, true);
      string uid = Guid.NewGuid().ToString("D");
      string file = Path.Combine(calendarDirectory, uid + ".ics");
      lock (_FileSystemLock) {
        WriteFileAtomically(file, ICalendarWriter.WriteEvent(uid, newEvent, DateTime.UtcNow));
        return ReadSingleEvent(file, uid);
      }
    }

    /// <summary>
    /// Replaces an existing event (optionally guarded by its ETag).
    /// </summary>
    public CalendarEvent UpdateEvent(string calendarId, CalendarEvent updatedEvent) {
      ValidateEventData(updatedEvent);
      if (string.IsNullOrWhiteSpace(updatedEvent.Uid)) {
        throw new ArgumentException("The Uid of the event to update is missing.");
      }
      string calendarDirectory = this.ResolveCalendarDirectory(calendarId, false);
      lock (_FileSystemLock) {
        string file = FindEventFile(calendarDirectory, updatedEvent.Uid);
        if (file == null) {
          throw new KeyNotFoundException("The event '" + updatedEvent.Uid + "' does not exist in calendar '" + calendarId + "'.");
        }
        EnsureETagMatches(file, updatedEvent.ETag);
        WriteFileAtomically(file, ICalendarWriter.WriteEvent(updatedEvent.Uid, updatedEvent, DateTime.UtcNow));
        return ReadSingleEvent(file, updatedEvent.Uid);
      }
    }

    /// <summary>
    /// Deletes the file which contains the event (optionally guarded by its ETag).
    /// </summary>
    public bool DeleteEvent(string calendarId, string eventUid, string eTag = null) {
      if (string.IsNullOrWhiteSpace(eventUid)) {
        throw new ArgumentException("The Uid of the event to delete is missing.");
      }
      string calendarDirectory = this.ResolveCalendarDirectory(calendarId, false);
      lock (_FileSystemLock) {
        string file = FindEventFile(calendarDirectory, eventUid);
        if (file == null) {
          return false;
        }
        EnsureETagMatches(file, eTag);
        File.Delete(file);
        return true;
      }
    }

    /// <summary>
    /// Authenticates the current call (like a CalDAV server answering HTTP 401) and returns the user directory.
    /// </summary>
    private string GetAuthenticatedUserDirectory() {
      CalDavCredentials credentials = _CredentialSource.GetCurrentCredentials();
      if (this.ValidateCredentials(credentials) != CalDavCredentialValidationResult.Valid) {
        throw new UnauthorizedAccessException("The calendar server rejected the request (HTTP 401). The credentials are missing, invalid or lack the required permissions.");
      }
      string userDirectory = this.CombineBelowRoot(_RootDirectory, ToSafeName(credentials.UserName.ToLowerInvariant()));
      Directory.CreateDirectory(userDirectory);
      return userDirectory;
    }

    /// <summary>
    /// Maps a calendar id (e.g. 'personal/') to its directory below the user directory (no path traversal possible).
    /// </summary>
    private string ResolveCalendarDirectory(string calendarId, bool createIfMissing) {
      string userDirectory = this.GetAuthenticatedUserDirectory();
      if (string.IsNullOrWhiteSpace(calendarId)) {
        throw new ArgumentException("The calendarId is missing.");
      }
      string trimmedId = calendarId.Trim().Trim('/');
      if (trimmedId.Length == 0 || trimmedId.Contains("/") || trimmedId.Contains("\\") || trimmedId == "." || trimmedId == ".." || trimmedId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
        throw new ArgumentException("The calendarId '" + calendarId + "' is invalid.");
      }
      string calendarDirectory = this.CombineBelowRoot(userDirectory, trimmedId);
      if (!Directory.Exists(calendarDirectory)) {
        if (createIfMissing || string.Equals(trimmedId, _DefaultCalendarId, StringComparison.OrdinalIgnoreCase)) {
          Directory.CreateDirectory(calendarDirectory);
        }
        else {
          throw new KeyNotFoundException("The calendar '" + calendarId + "' does not exist.");
        }
      }
      return calendarDirectory;
    }

    /// <summary>
    /// Combines a path and ensures that the result stays below the configured root directory.
    /// </summary>
    private string CombineBelowRoot(string parent, string childName) {
      string combined = Path.GetFullPath(Path.Combine(parent, childName));
      string rootWithSeparator = _RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
      if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) {
        throw new ArgumentException("The requested path is outside of the mock directory.");
      }
      return combined;
    }

    private static string FindEventFile(string calendarDirectory, string uid) {
      if (uid.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !uid.Contains("..")) {
        string direct = Path.Combine(calendarDirectory, uid + ".ics");
        if (File.Exists(direct)) {
          return direct;
        }
      }
      foreach (string file in Directory.EnumerateFiles(calendarDirectory, "*.ics")) {
        if (ReadEventFile(file).Any((e) => string.Equals(e.Uid, uid, StringComparison.Ordinal))) {
          return file;
        }
      }
      return null;
    }

    private static CalendarEvent[] ReadEventFile(string file) {
      string content = File.ReadAllText(file, Encoding.UTF8);
      string eTag = ComputeETag(content);
      CalendarEvent[] events = ICalendarParser.ParseEvents(content);
      foreach (CalendarEvent calendarEvent in events) {
        calendarEvent.ETag = eTag;
      }
      return events;
    }

    private static CalendarEvent ReadSingleEvent(string file, string uid) {
      CalendarEvent found = ReadEventFile(file).FirstOrDefault((e) => string.Equals(e.Uid, uid, StringComparison.Ordinal));
      if (found == null) {
        throw new InvalidOperationException("The event '" + uid + "' could not be read back after writing.");
      }
      return found;
    }

    private static void EnsureETagMatches(string file, string eTag) {
      if (string.IsNullOrWhiteSpace(eTag)) {
        return;
      }
      string currentETag = ComputeETag(File.ReadAllText(file, Encoding.UTF8));
      if (!string.Equals(currentETag, eTag.Trim(), StringComparison.Ordinal)) {
        throw new InvalidOperationException("The event was modified or created by someone else in the meantime. Read it again and retry.");
      }
    }

    private static string ComputeETag(string content) {
      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
      return "\"" + Convert.ToHexString(hash, 0, 8) + "\"";
    }

    private static void WriteFileAtomically(string file, string content) {
      string tempFile = file + ".tmp";
      File.WriteAllText(tempFile, content, new UTF8Encoding(false));
      File.Move(tempFile, file, true);
    }

    private static string ToSafeName(string value) {
      StringBuilder builder = new StringBuilder();
      foreach (char c in value) {
        if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == '@') {
          builder.Append(c);
        }
        else {
          builder.Append('_');
        }
      }
      string safe = builder.ToString().Trim('.');
      if (safe.Length == 0) {
        safe = "_";
      }
      return safe;
    }

    private static bool FixedTimeEquals(string left, string right) {
      byte[] leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
      byte[] rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
      return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    /// <summary>
    /// Validates the event data that is required for create/update (same rules as the real CalDAV connector).
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

    private static DateTime ToUtc(DateTime value) {
      if (value.Kind == DateTimeKind.Local) {
        return value.ToUniversalTime();
      }
      return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

  }

}
