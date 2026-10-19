using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;

namespace AI.SmartStandards.DemoWebService.Sample.Calendar {

  /// <summary>
  /// Minimal iCalendar (RFC 5545) reader for VEVENT components.
  /// Supported: line unfolding, text unescaping, DTSTART/DTEND/DURATION (UTC, TZID, floating, DATE),
  /// SUMMARY, DESCRIPTION, LOCATION, UID, RRULE and RECURRENCE-ID detection.
  /// Not supported: own recurrence expansion (the CalDAV server is asked to expand instead), VTIMEZONE conversion.
  /// </summary>
  internal static class ICalendarParser {

    /// <summary>
    /// Parses all VEVENT components of an iCalendar document.
    /// </summary>
    public static CalendarEvent[] ParseEvents(string iCalendarText) {
      List<CalendarEvent> events = new List<CalendarEvent>();
      Stack<string> componentStack = new Stack<string>();
      CalendarEvent currentEvent = null;
      TimeSpan? currentDuration = null;
      bool hasEnd = false;

      foreach (string line in UnfoldLines(iCalendarText)) {
        ContentLine contentLine = ContentLine.Parse(line);
        if (contentLine == null) {
          continue;
        }

        if (contentLine.Name == "BEGIN") {
          string componentName = contentLine.Value.ToUpperInvariant();
          componentStack.Push(componentName);
          if (componentName == "VEVENT" && componentStack.Count == 2) {
            currentEvent = new CalendarEvent();
            currentDuration = null;
            hasEnd = false;
          }
          continue;
        }

        if (contentLine.Name == "END") {
          if (componentStack.Count > 0) {
            string componentName = componentStack.Pop();
            if (componentName == "VEVENT" && currentEvent != null && componentStack.Count == 1) {
              CompleteEnd(currentEvent, hasEnd, currentDuration);
              events.Add(currentEvent);
              currentEvent = null;
            }
          }
          continue;
        }

        if (currentEvent == null || componentStack.Count == 0 || componentStack.Peek() != "VEVENT") {
          continue;
        }

        switch (contentLine.Name) {
          case "UID":
            currentEvent.Uid = contentLine.Value;
            break;
          case "SUMMARY":
            currentEvent.Summary = UnescapeText(contentLine.Value);
            break;
          case "DESCRIPTION":
            currentEvent.Description = UnescapeText(contentLine.Value);
            break;
          case "LOCATION":
            currentEvent.Location = UnescapeText(contentLine.Value);
            break;
          case "DTSTART":
            ApplyStart(currentEvent, contentLine);
            break;
          case "DTEND":
            currentEvent.End = ParseDateTime(contentLine);
            hasEnd = true;
            break;
          case "DURATION":
            currentDuration = ParseDuration(contentLine.Value);
            break;
          case "RRULE":
            currentEvent.IsRecurring = true;
            currentEvent.RecurrenceRule = contentLine.Value;
            break;
          case "RECURRENCE-ID":
            currentEvent.IsRecurring = true;
            break;
        }
      }

      return events.ToArray();
    }

    /// <summary>
    /// Applies DTSTART including all-day and time zone information.
    /// </summary>
    private static void ApplyStart(CalendarEvent calendarEvent, ContentLine contentLine) {
      calendarEvent.Start = ParseDateTime(contentLine);
      calendarEvent.IsAllDay = IsDateOnly(contentLine);

      if (calendarEvent.IsAllDay) {
        calendarEvent.TimeZoneId = null;
      }
      else if (contentLine.Value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) {
        calendarEvent.TimeZoneId = "UTC";
      }
      else if (contentLine.Parameters.ContainsKey("TZID")) {
        calendarEvent.TimeZoneId = contentLine.Parameters["TZID"];
      }
      else {
        calendarEvent.TimeZoneId = null;
      }
    }

    /// <summary>
    /// Derives the end from DURATION or the RFC 5545 defaults if DTEND is missing.
    /// </summary>
    private static void CompleteEnd(CalendarEvent calendarEvent, bool hasEnd, TimeSpan? duration) {
      if (hasEnd) {
        return;
      }
      if (duration.HasValue) {
        calendarEvent.End = calendarEvent.Start.Add(duration.Value);
      }
      else if (calendarEvent.IsAllDay) {
        calendarEvent.End = calendarEvent.Start.AddDays(1);
      }
      else {
        calendarEvent.End = calendarEvent.Start;
      }
    }

    /// <summary>
    /// Returns true if the property holds a DATE (not a DATE-TIME) value.
    /// </summary>
    private static bool IsDateOnly(ContentLine contentLine) {
      if (contentLine.Parameters.ContainsKey("VALUE") && string.Equals(contentLine.Parameters["VALUE"], "DATE", StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
      return contentLine.Value.Length == 8;
    }

    /// <summary>
    /// Parses DATE / DATE-TIME values (UTC values get DateTimeKind.Utc, all others DateTimeKind.Unspecified).
    /// </summary>
    private static DateTime ParseDateTime(ContentLine contentLine) {
      string value = contentLine.Value.Trim();

      if (IsDateOnly(contentLine)) {
        return DateTime.ParseExact(value.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None);
      }

      if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) {
        DateTime utcValue = DateTime.ParseExact(value.Substring(0, value.Length - 1), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        return DateTime.SpecifyKind(utcValue, DateTimeKind.Utc);
      }

      return DateTime.ParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    /// <summary>
    /// Parses an RFC 5545 duration (e.g. "PT1H30M", "P1D", "P2W").
    /// </summary>
    private static TimeSpan? ParseDuration(string value) {
      string normalized = value.Trim();
      bool negative = false;
      if (normalized.StartsWith("-", StringComparison.Ordinal)) {
        negative = true;
        normalized = normalized.Substring(1);
      }
      else if (normalized.StartsWith("+", StringComparison.Ordinal)) {
        normalized = normalized.Substring(1);
      }

      TimeSpan duration;
      if (normalized.EndsWith("W", StringComparison.OrdinalIgnoreCase)) {
        int weeks;
        if (!int.TryParse(normalized.Substring(1, normalized.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out weeks)) {
          return null;
        }
        duration = TimeSpan.FromDays(weeks * 7);
      }
      else {
        try {
          duration = XmlConvert.ToTimeSpan(normalized);
        }
        catch (FormatException) {
          return null;
        }
      }

      if (negative) {
        return duration.Negate();
      }
      return duration;
    }

    /// <summary>
    /// Unescapes iCalendar TEXT values.
    /// </summary>
    private static string UnescapeText(string value) {
      StringBuilder result = new StringBuilder(value.Length);
      for (int i = 0; i < value.Length; i++) {
        char current = value[i];
        if (current == '\\' && i + 1 < value.Length) {
          char next = value[i + 1];
          if (next == 'n' || next == 'N') {
            result.Append('\n');
          }
          else {
            result.Append(next);
          }
          i++;
        }
        else {
          result.Append(current);
        }
      }
      return result.ToString();
    }

    /// <summary>
    /// Splits the document into logical lines (continuation lines start with a space or a tab).
    /// </summary>
    private static List<string> UnfoldLines(string text) {
      List<string> lines = new List<string>();
      string[] physicalLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
      StringBuilder current = null;

      foreach (string physicalLine in physicalLines) {
        if (physicalLine.Length > 0 && (physicalLine[0] == ' ' || physicalLine[0] == '\t') && current != null) {
          current.Append(physicalLine, 1, physicalLine.Length - 1);
          continue;
        }
        if (current != null) {
          lines.Add(current.ToString());
        }
        current = new StringBuilder(physicalLine);
      }
      if (current != null) {
        lines.Add(current.ToString());
      }

      return lines;
    }

    /// <summary>
    /// A single iCalendar content line ("NAME;PARAM=VALUE:value").
    /// </summary>
    private sealed class ContentLine {

      private string _Name;
      private string _Value;
      private Dictionary<string, string> _Parameters;

      public string Name {
        get {
          return _Name;
        }
      }

      public string Value {
        get {
          return _Value;
        }
      }

      public Dictionary<string, string> Parameters {
        get {
          return _Parameters;
        }
      }

      /// <summary>
      /// Parses a content line; returns null for empty or malformed lines.
      /// </summary>
      public static ContentLine Parse(string line) {
        if (string.IsNullOrWhiteSpace(line)) {
          return null;
        }

        int colonIndex = -1;
        bool insideQuotes = false;
        for (int i = 0; i < line.Length; i++) {
          if (line[i] == '"') {
            insideQuotes = !insideQuotes;
          }
          else if (line[i] == ':' && !insideQuotes) {
            colonIndex = i;
            break;
          }
        }
        if (colonIndex <= 0) {
          return null;
        }

        ContentLine contentLine = new ContentLine();
        contentLine._Value = line.Substring(colonIndex + 1);
        contentLine._Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string[] nameAndParameters = line.Substring(0, colonIndex).Split(';');
        contentLine._Name = nameAndParameters[0].Trim().ToUpperInvariant();
        for (int i = 1; i < nameAndParameters.Length; i++) {
          int equalsIndex = nameAndParameters[i].IndexOf('=');
          if (equalsIndex > 0) {
            string parameterName = nameAndParameters[i].Substring(0, equalsIndex).Trim();
            string parameterValue = nameAndParameters[i].Substring(equalsIndex + 1).Trim('"');
            contentLine._Parameters[parameterName] = parameterValue;
          }
        }

        return contentLine;
      }

    }

  }

}
