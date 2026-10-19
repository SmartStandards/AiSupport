using System;
using System.Globalization;
using System.Text;

namespace AI.SmartStandards.DemoWebService.Sample.Calendar {

  /// <summary>
  /// Minimal iCalendar (RFC 5545) writer for single, non-recurring VEVENT resources.
  /// Timed events are always written as UTC values (time zones are converted here, so no VTIMEZONE is required);
  /// all-day events are written as DATE values.
  /// </summary>
  internal static class ICalendarWriter {

    private const int _MaxLineOctets = 75;

    /// <summary>
    /// Serializes an event into a complete VCALENDAR document.
    /// </summary>
    public static string WriteEvent(string uid, CalendarEvent calendarEvent, DateTime stampUtc) {
      StringBuilder builder = new StringBuilder();
      AppendLine(builder, "BEGIN:VCALENDAR");
      AppendLine(builder, "VERSION:2.0");
      AppendLine(builder, "PRODID:-//SmartStandards//AiSupport DemoWebService//EN");
      AppendLine(builder, "BEGIN:VEVENT");
      AppendLine(builder, "UID:" + uid);
      AppendLine(builder, "DTSTAMP:" + FormatUtc(stampUtc));

      if (calendarEvent.IsAllDay) {
        DateTime startDate = calendarEvent.Start.Date;
        DateTime endDate = calendarEvent.End.Date;
        if (endDate <= startDate) {
          endDate = startDate.AddDays(1);
        }
        AppendLine(builder, "DTSTART;VALUE=DATE:" + startDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        AppendLine(builder, "DTEND;VALUE=DATE:" + endDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
      }
      else {
        AppendLine(builder, "DTSTART:" + FormatUtc(ToUtc(calendarEvent.Start, calendarEvent.TimeZoneId)));
        AppendLine(builder, "DTEND:" + FormatUtc(ToUtc(calendarEvent.End, calendarEvent.TimeZoneId)));
      }

      AppendLine(builder, "SUMMARY:" + EscapeText(calendarEvent.Summary));
      if (!string.IsNullOrEmpty(calendarEvent.Description)) {
        AppendLine(builder, "DESCRIPTION:" + EscapeText(calendarEvent.Description));
      }
      if (!string.IsNullOrEmpty(calendarEvent.Location)) {
        AppendLine(builder, "LOCATION:" + EscapeText(calendarEvent.Location));
      }

      AppendLine(builder, "END:VEVENT");
      AppendLine(builder, "END:VCALENDAR");
      return builder.ToString();
    }

    /// <summary>
    /// Converts a value to UTC. UTC/unspecified values without time zone are treated as UTC;
    /// unspecified values with a TimeZoneId (IANA or Windows id) are interpreted in that time zone.
    /// </summary>
    private static DateTime ToUtc(DateTime value, string timeZoneId) {
      if (value.Kind == DateTimeKind.Utc) {
        return value;
      }
      if (value.Kind == DateTimeKind.Local) {
        return value.ToUniversalTime();
      }
      if (string.IsNullOrWhiteSpace(timeZoneId) || string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase)) {
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
      }

      TimeZoneInfo timeZone;
      try {
        timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
      }
      catch (TimeZoneNotFoundException) {
        throw new ArgumentException("The time zone '" + timeZoneId + "' is unknown. Use an IANA id like 'Europe/Berlin' or 'UTC'.");
      }
      return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), timeZone);
    }

    /// <summary>
    /// Formats a UTC DATE-TIME value.
    /// </summary>
    private static string FormatUtc(DateTime utcValue) {
      return utcValue.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) + "Z";
    }

    /// <summary>
    /// Escapes TEXT values according to RFC 5545 section 3.3.11.
    /// </summary>
    private static string EscapeText(string value) {
      if (value == null) {
        return string.Empty;
      }
      return value
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n");
    }

    /// <summary>
    /// Appends a content line, folded at 75 octets (UTF-8) and terminated with CRLF.
    /// </summary>
    private static void AppendLine(StringBuilder builder, string line) {
      int octets = 0;
      foreach (char character in line) {
        int characterOctets = Encoding.UTF8.GetByteCount(new char[] { character });
        if (char.IsLowSurrogate(character)) {
          characterOctets = 2;
        }
        else if (char.IsHighSurrogate(character)) {
          characterOctets = 2;
        }
        if (octets + characterOctets > _MaxLineOctets && !char.IsLowSurrogate(character)) {
          builder.Append("\r\n ");
          octets = 1;
        }
        builder.Append(character);
        octets += characterOctets;
      }
      builder.Append("\r\n");
    }

  }

}
