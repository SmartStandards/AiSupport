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

    private string _MockToLocalDir;
    private string _MockAuthUser;
    private string _MockAuthPass;

    /// <summary>
    /// LOCAL TESTING ONLY: if set, a local mock (persisting *.ics files below this directory) is used
    /// instead of a real CalDAV server. The complete OAuth/MCP authentication flow stays active.
    /// </summary>
    public string MockToLocalDir {
      get {
        return _MockToLocalDir;
      }
      set {
        _MockToLocalDir = value;
      }
    }

    /// <summary>
    /// LOCAL TESTING ONLY: the only user name which is accepted by the local mock.
    /// </summary>
    public string MockAuthUser {
      get {
        return _MockAuthUser;
      }
      set {
        _MockAuthUser = value;
      }
    }

    /// <summary>
    /// LOCAL TESTING ONLY: the only password which is accepted by the local mock.
    /// </summary>
    public string MockAuthPass {
      get {
        return _MockAuthPass;
      }
      set {
        _MockAuthPass = value;
      }
    }

    /// <summary>
    /// True if the local mock should be used (setting 'MockToLocalDir' is not empty).
    /// </summary>
    public bool IsMockMode {
      get {
        return !string.IsNullOrWhiteSpace(_MockToLocalDir);
      }
    }

  }

}
