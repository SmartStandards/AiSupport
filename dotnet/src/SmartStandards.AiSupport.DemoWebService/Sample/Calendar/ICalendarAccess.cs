using System;

namespace AI.SmartStandards.DemoWebService.Sample.Calendar {

  /// <summary>
  /// Read access to the calendars of a single calendar account (sample contract of the demo service).
  /// The contract is UJMW-transportable: synchronous methods and simple DTOs only.
  /// </summary>
  public interface ICalendarAccess {

    /// <summary>
    /// Lists all calendars that are available within the configured calendar home.
    /// </summary>
    /// <returns>The available calendars, sorted by their display name.</returns>
    CalendarInfo[] GetCalendars();

    /// <summary>
    /// Lists the events of a calendar that overlap the given time range.
    /// Recurring events are expanded into individual occurrences if the server supports it.
    /// </summary>
    /// <param name="calendarId">The id of the calendar, as returned by <see cref="GetCalendars"/>.</param>
    /// <param name="fromUtc">Start of the time range (UTC, inclusive).</param>
    /// <param name="toUtc">End of the time range (UTC, exclusive). The range must not exceed 366 days.</param>
    /// <returns>The events within the time range, sorted by their start.</returns>
    CalendarEvent[] GetEvents(string calendarId, DateTime fromUtc, DateTime toUtc);

    /// <summary>
    /// Creates a new (non-recurring) event in a calendar.
    /// </summary>
    /// <param name="calendarId">The id of the calendar, as returned by <see cref="GetCalendars"/>.</param>
    /// <param name="newEvent">The event data. Summary, Start and End are required; Uid, ETag and the recurrence fields are ignored.
    /// For timed events, Start/End are interpreted as UTC unless TimeZoneId names an IANA/Windows time zone.
    /// For all-day events (IsAllDay) only the date part is used and End is exclusive (next day for a single-day event).</param>
    /// <returns>The created event including its assigned Uid and ETag.</returns>
    CalendarEvent CreateEvent(string calendarId, CalendarEvent newEvent);

    /// <summary>
    /// Replaces an existing (non-recurring) event. Unchanged values must be passed as well (full replacement).
    /// </summary>
    /// <param name="calendarId">The id of the calendar, as returned by <see cref="GetCalendars"/>.</param>
    /// <param name="updatedEvent">The complete event data. Uid identifies the event (as returned by <see cref="GetEvents"/>);
    /// if ETag is set, the update fails when the event was modified meanwhile.</param>
    /// <returns>The updated event including its new ETag.</returns>
    CalendarEvent UpdateEvent(string calendarId, CalendarEvent updatedEvent);

    /// <summary>
    /// Deletes an event (for recurring events: the whole series).
    /// </summary>
    /// <param name="calendarId">The id of the calendar, as returned by <see cref="GetCalendars"/>.</param>
    /// <param name="eventUid">The Uid of the event, as returned by <see cref="GetEvents"/>.</param>
    /// <param name="eTag">Optional ETag; if set, the deletion fails when the event was modified meanwhile.</param>
    /// <returns>True if the event was deleted, false if it did not exist.</returns>
    bool DeleteEvent(string calendarId, string eventUid, string eTag = null);

  }

  /// <summary>
  /// Describes a single calendar.
  /// </summary>
  public class CalendarInfo {

    private string _Id;
    private string _DisplayName;
    private string _Description;

    /// <summary>
    /// The calendar id (path of the calendar collection relative to the configured calendar home).
    /// </summary>
    public string Id {
      get {
        return _Id;
      }
      set {
        _Id = value;
      }
    }

    /// <summary>
    /// The human-readable name of the calendar.
    /// </summary>
    public string DisplayName {
      get {
        return _DisplayName;
      }
      set {
        _DisplayName = value;
      }
    }

    /// <summary>
    /// An optional description of the calendar.
    /// </summary>
    public string Description {
      get {
        return _Description;
      }
      set {
        _Description = value;
      }
    }

  }

  /// <summary>
  /// Describes a single calendar event (or a single occurrence of a recurring event).
  /// </summary>
  public class CalendarEvent {

    private string _Uid;
    private string _Summary;
    private string _Description;
    private string _Location;
    private DateTime _Start;
    private DateTime _End;
    private bool _IsAllDay;
    private string _TimeZoneId;
    private bool _IsRecurring;
    private string _RecurrenceRule;
    private string _ETag;

    /// <summary>
    /// The iCalendar UID of the event.
    /// </summary>
    public string Uid {
      get {
        return _Uid;
      }
      set {
        _Uid = value;
      }
    }

    /// <summary>
    /// The title of the event.
    /// </summary>
    public string Summary {
      get {
        return _Summary;
      }
      set {
        _Summary = value;
      }
    }

    /// <summary>
    /// The description of the event.
    /// </summary>
    public string Description {
      get {
        return _Description;
      }
      set {
        _Description = value;
      }
    }

    /// <summary>
    /// The location of the event.
    /// </summary>
    public string Location {
      get {
        return _Location;
      }
      set {
        _Location = value;
      }
    }

    /// <summary>
    /// The start of the event. UTC if the source value was UTC; otherwise local time of TimeZoneId (or floating time).
    /// </summary>
    public DateTime Start {
      get {
        return _Start;
      }
      set {
        _Start = value;
      }
    }

    /// <summary>
    /// The end of the event (exclusive). Same time semantics as Start.
    /// </summary>
    public DateTime End {
      get {
        return _End;
      }
      set {
        _End = value;
      }
    }

    /// <summary>
    /// True for all-day events (date values without a time).
    /// </summary>
    public bool IsAllDay {
      get {
        return _IsAllDay;
      }
      set {
        _IsAllDay = value;
      }
    }

    /// <summary>
    /// The iCalendar TZID of Start/End, "UTC" for UTC values, or null for floating/all-day values.
    /// </summary>
    public string TimeZoneId {
      get {
        return _TimeZoneId;
      }
      set {
        _TimeZoneId = value;
      }
    }

    /// <summary>
    /// True if the event belongs to a recurring series.
    /// </summary>
    public bool IsRecurring {
      get {
        return _IsRecurring;
      }
      set {
        _IsRecurring = value;
      }
    }

    /// <summary>
    /// The raw iCalendar RRULE if the server returned the unexpanded series master; otherwise null.
    /// </summary>
    public string RecurrenceRule {
      get {
        return _RecurrenceRule;
      }
      set {
        _RecurrenceRule = value;
      }
    }

    /// <summary>
    /// The ETag of the calendar resource that contains the event (basis for later concurrency checks).
    /// </summary>
    public string ETag {
      get {
        return _ETag;
      }
      set {
        _ETag = value;
      }
    }

  }

}
