using System;

namespace AI.SmartStandards.DemoWebService.Sample.Calendar {

  /// <summary>
  /// Configuration of the CalDAV proxy. Only the configured calendar home (and resources below it) is ever contacted.
  /// </summary>
  public class CalDavCalendarAccessOptions {

    private string _CalendarHomeUrl;
    private int _TimeoutSeconds = 30;

    /// <summary>
    /// Absolute URL of the CalDAV calendar home collection (e.g. "https://dav.example.com/caldav/user/").
    /// </summary>
    public string CalendarHomeUrl {
      get {
        return _CalendarHomeUrl;
      }
      set {
        _CalendarHomeUrl = value;
      }
    }

    /// <summary>
    /// Timeout for a single CalDAV request in seconds.
    /// </summary>
    public int TimeoutSeconds {
      get {
        return _TimeoutSeconds;
      }
      set {
        _TimeoutSeconds = value;
      }
    }

  }

}
