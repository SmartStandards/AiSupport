using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Allow-list matching of the second line of defense. The guards are enforced inside the OOB tools,
  /// independently of the tool approval: an approved call can still be blocked.
  /// On a miss, the user is asked via 'HarnessToolContext.RequestInteraction'. 'AllowPermanently' adds the
  /// subject to the allow-list within the settings object of the tool source (global, not per chat).
  /// </summary>
  public static class HarnessGuards {

    public const string ReadOnlyAccess = "RO";

    public const string ReadWriteAccess = "RW";

    #region " PathGuard "

    /// <summary>
    /// Returns true if the (normalized) path is covered by an entry of the allow-list
    /// (array of { "path": "...", "access": "RO|RW" }, recursive, case-insensitive).
    /// </summary>
    public static bool IsPathAllowed(JArray allowedPaths, string fullPath, bool writeAccess) {

      if (allowedPaths == null) {
        return false;
      }

      foreach (JToken entry in allowedPaths) {

        string allowedPath = GetString(entry, "path");
        string access = GetString(entry, "access");

        if (string.IsNullOrWhiteSpace(allowedPath)) {
          continue;
        }

        if (writeAccess && !string.Equals(access, ReadWriteAccess, StringComparison.OrdinalIgnoreCase)) {
          continue;
        }

        string normalizedAllowedPath;

        try {
          normalizedAllowedPath = NormalizePath(allowedPath);
        }
        catch (Exception) {
          continue;
        }

        if (IsSameOrSubPath(fullPath, normalizedAllowedPath)) {
          return true;
        }

      }

      return false;
    }

    /// <summary>
    /// Normalizes a path: full path, '..' resolved, trailing separators removed and (if it exists)
    /// symbolic links / junctions of the path itself resolved.
    /// </summary>
    public static string NormalizePath(string path) {

      if (string.IsNullOrWhiteSpace(path)) {
        throw new ArgumentNullException(nameof(path));
      }

      string fullPath = Path.GetFullPath(path.Trim().Trim('"'));

#if NET_CORE
      FileSystemInfo info = null;

      if (Directory.Exists(fullPath)) {
        info = new DirectoryInfo(fullPath);
      }
      else if (File.Exists(fullPath)) {
        info = new FileInfo(fullPath);
      }

      if (info != null && info.LinkTarget != null) {

        FileSystemInfo target = info.ResolveLinkTarget(true);

        if (target != null) {
          fullPath = Path.GetFullPath(target.FullName);
        }

      }
#endif

      string root = Path.GetPathRoot(fullPath);

      if (fullPath.Length > root.Length) {
        fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      }

      return fullPath;
    }

    private static bool IsSameOrSubPath(string fullPath, string allowedPath) {

      if (string.Equals(fullPath, allowedPath, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }

      string prefix = allowedPath;

      if (!prefix.EndsWith(Path.DirectorySeparatorChar.ToString())) {
        prefix = prefix + Path.DirectorySeparatorChar;
      }

      return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensures that the path is allowed (or asks the user). Returns the normalized path, throws if denied.
    /// </summary>
    public static string EnsurePathAllowed(IToolProviderContext context, string path, bool writeAccess) {

      string fullPath = NormalizePath(path);
      JObject settings = ParseSettings(context.SettingsJson);

      if (IsPathAllowed(settings["allowedPaths"] as JArray, fullPath, writeAccess)) {
        return fullPath;
      }

      string accessText = ReadOnlyAccess;

      if (writeAccess) {
        accessText = ReadWriteAccess;
      }

      string decision = HarnessToolContext.RequestInteraction(PendingInteractionKinds.PathAccess, accessText + " " + fullPath);

      if (string.Equals(decision, ToolApprovalDecisions.AllowPermanently, StringComparison.OrdinalIgnoreCase)) {

        JObject entry = new JObject();
        entry["path"] = fullPath;
        entry["access"] = accessText;
        AppendAllowListEntry(context, "allowedPaths", entry);

        return fullPath;
      }

      EnsureNotDenied(decision, $"The access to the path '{fullPath}' was denied by the user.");

      return fullPath;
    }

    #endregion

    #region " DomainGuard "

    /// <summary>
    /// Returns true if the host matches an entry of the allow-list (exact host or '*.domain').
    /// </summary>
    public static bool IsDomainAllowed(JArray allowedDomains, string host) {

      if (allowedDomains == null || string.IsNullOrWhiteSpace(host)) {
        return false;
      }

      foreach (JToken entry in allowedDomains) {

        if (entry.Type != JTokenType.String) {
          continue;
        }

        string pattern = entry.Value<string>().Trim();

        if (pattern.StartsWith("*.", StringComparison.Ordinal)) {

          string suffix = pattern.Substring(1);

          if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
            return true;
          }

        }
        else if (string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase)) {
          return true;
        }

      }

      return false;
    }

    /// <summary>
    /// Ensures that the host of the URL is allowed (or asks the user). Throws if denied.
    /// </summary>
    public static void EnsureDomainAllowed(IToolProviderContext context, Uri uri) {

      string host = uri.Host;
      JObject settings = ParseSettings(context.SettingsJson);

      if (IsDomainAllowed(settings["allowedDomains"] as JArray, host)) {
        return;
      }

      string decision = HarnessToolContext.RequestInteraction(PendingInteractionKinds.DomainAccess, host);

      if (string.Equals(decision, ToolApprovalDecisions.AllowPermanently, StringComparison.OrdinalIgnoreCase)) {
        AppendAllowListEntry(context, "allowedDomains", new JValue(host));
        return;
      }

      EnsureNotDenied(decision, $"The access to the domain '{host}' was denied by the user.");

    }

    #endregion

    #region " ShellGuard "

    /// <summary>
    /// Returns true if the command line matches an entry of the allow-list
    /// (array of { "pattern": "...", "shell": "cmd|powershell" }). Patterns are literal (default),
    /// wildcards ('*', '?') or regular expressions (prefix 'regex:'), always over the full command line.
    /// </summary>
    public static bool IsCommandAllowed(JArray allowedCommands, string shell, string commandLine) {

      if (allowedCommands == null) {
        return false;
      }

      foreach (JToken entry in allowedCommands) {

        string pattern = GetString(entry, "pattern");
        string entryShell = GetString(entry, "shell");

        if (string.IsNullOrEmpty(pattern)) {
          continue;
        }

        if (!string.IsNullOrWhiteSpace(entryShell) && !string.Equals(entryShell, shell, StringComparison.OrdinalIgnoreCase)) {
          continue;
        }

        if (MatchesCommandPattern(pattern, commandLine)) {
          return true;
        }

      }

      return false;
    }

    /// <summary>
    /// Matches a single pattern against the full command line.
    /// </summary>
    public static bool MatchesCommandPattern(string pattern, string commandLine) {

      if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) {

        string expression = pattern.Substring("regex:".Length);

        try {
          return Regex.IsMatch(commandLine, "^(?:" + expression + ")$", RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException) {
          return false;
        }
        catch (RegexMatchTimeoutException) {
          return false;
        }

      }

      if (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0) {

        string expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

        return Regex.IsMatch(commandLine, expression, RegexOptions.Singleline, TimeSpan.FromSeconds(1));
      }

      return string.Equals(pattern, commandLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures that the command line is allowed (or asks the user). 'AllowPermanently' stores the exact
    /// command line literally. Throws if denied.
    /// </summary>
    public static void EnsureCommandAllowed(IToolProviderContext context, string shell, string commandLine) {

      JObject settings = ParseSettings(context.SettingsJson);

      if (IsCommandAllowed(settings["allowedCommands"] as JArray, shell, commandLine)) {
        return;
      }

      string decision = HarnessToolContext.RequestInteraction(PendingInteractionKinds.ShellCommand, shell + ": " + commandLine);

      if (string.Equals(decision, ToolApprovalDecisions.AllowPermanently, StringComparison.OrdinalIgnoreCase)) {

        JObject entry = new JObject();
        entry["pattern"] = commandLine;
        entry["shell"] = shell;
        AppendAllowListEntry(context, "allowedCommands", entry);

        return;
      }

      EnsureNotDenied(decision, "The command line was denied by the user.");

    }

    #endregion

    #region " Helpers "

    /// <summary>
    /// Parses a settings object (an empty/invalid value results in an empty object).
    /// </summary>
    public static JObject ParseSettings(string settingsJson) {

      if (string.IsNullOrWhiteSpace(settingsJson)) {
        return new JObject();
      }

      try {

        JObject parsed = JObject.Parse(settingsJson);

        return parsed;
      }
      catch (Newtonsoft.Json.JsonException) {
        return new JObject();
      }

    }

    private static void EnsureNotDenied(string decision, string message) {

      bool allowed = (
        string.Equals(decision, ToolApprovalDecisions.AllowOnce, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision, ToolApprovalDecisions.AllowForChat, StringComparison.OrdinalIgnoreCase)
      );

      if (!allowed) {
        throw new UnauthorizedAccessException(message);
      }

    }

    private static void AppendAllowListEntry(IToolProviderContext context, string listName, JToken entry) {

      JObject settings = ParseSettings(context.SettingsJson);
      JArray list = settings[listName] as JArray;

      if (list == null) {
        list = new JArray();
        settings[listName] = list;
      }

      list.Add(entry);
      context.SaveSettingsJson(settings.ToString(Newtonsoft.Json.Formatting.Indented));

    }

    private static string GetString(JToken entry, string propertyName) {

      JObject entryObject = entry as JObject;

      if (entryObject == null) {
        return null;
      }

      JToken value = entryObject[propertyName];

      if (value == null || value.Type != JTokenType.String) {
        return null;
      }

      return value.Value<string>();
    }

    #endregion

  }

}
