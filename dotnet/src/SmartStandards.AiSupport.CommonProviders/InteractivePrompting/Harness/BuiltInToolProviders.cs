using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Base class of the built-in (OOB) tool providers.
  /// </summary>
  public abstract class BuiltInToolProviderBase : IHarnessToolProvider {

    public abstract string ProviderId { get; }

    public abstract string DisplayName { get; }

    public virtual string DefaultLevel {
      get {
        return ToolSourceLevels.RequiresConfirmation;
      }
    }

    public virtual bool SupportsCredential {
      get {
        return false;
      }
    }

    public virtual string GetDefaultSettingsJson() {
      return "{}";
    }

    public virtual string GetSettingsUiHtml() {
      return null;
    }

    /// <summary>
    /// Ensures that the settings are a JSON object and returns them (indented).
    /// </summary>
    public virtual string ValidateSettingsJson(string settingsJson) {

      if (string.IsNullOrWhiteSpace(settingsJson)) {
        return this.GetDefaultSettingsJson();
      }

      JObject settings;

      try {
        settings = JObject.Parse(settingsJson);
      }
      catch (Newtonsoft.Json.JsonException ex) {
        throw new ArgumentException("The settings are not a valid JSON object: " + ex.Message);
      }

      this.ValidateSettings(settings);

      return settings.ToString(Newtonsoft.Json.Formatting.Indented);
    }

    /// <summary>
    /// Validates the parsed settings object (throws an 'ArgumentException' if invalid).
    /// </summary>
    protected virtual void ValidateSettings(JObject settings) {
    }

    public abstract IHarnessTool[] CreateTools(IToolProviderContext context);

    protected static void ValidateArrayProperty(JObject settings, string propertyName) {

      JToken token = settings[propertyName];

      if (token != null && token.Type != JTokenType.Array && token.Type != JTokenType.Null) {
        throw new ArgumentException($"The property '{propertyName}' must be an array.");
      }

    }

  }

  /// <summary>
  /// OOB-1: overwrites or appends the content of 'global-user-preferences' / 'global-system-preferences'.
  /// Reading is done via the built-in skills (not via this tool).
  /// </summary>
  public class PreferencesToolProvider : BuiltInToolProviderBase {

    public const string Id = "oob-preferences";

    private readonly Func<string> _UserPreferencesFileGetter;

    private readonly Func<string> _SystemPreferencesFileGetter;

    /// <summary>
    /// Creates the provider.
    /// </summary>
    /// <param name="userPreferencesFileGetter">Returns the 'SKILL.md' of 'global-user-preferences'.</param>
    /// <param name="systemPreferencesFileGetter">Returns the location of 'global-system-preferences' (file, folder or URL).</param>
    public PreferencesToolProvider(Func<string> userPreferencesFileGetter, Func<string> systemPreferencesFileGetter) {

      if (userPreferencesFileGetter == null) {
        throw new ArgumentNullException(nameof(userPreferencesFileGetter));
      }

      if (systemPreferencesFileGetter == null) {
        throw new ArgumentNullException(nameof(systemPreferencesFileGetter));
      }

      _UserPreferencesFileGetter = userPreferencesFileGetter;
      _SystemPreferencesFileGetter = systemPreferencesFileGetter;

    }

    public override string ProviderId {
      get {
        return Id;
      }
    }

    public override string DisplayName {
      get {
        return "Preferences update";
      }
    }

    public override IHarnessTool[] CreateTools(IToolProviderContext context) {

      string schema = (
        "{\"type\":\"object\",\"properties\":{" +
        "\"target\":{\"type\":\"string\",\"enum\":[\"user\",\"system\"],\"description\":\"'user' = global-user-preferences, 'system' = global-system-preferences\"}," +
        "\"mode\":{\"type\":\"string\",\"enum\":[\"append\",\"overwrite\"]}," +
        "\"content\":{\"type\":\"string\",\"description\":\"Markdown content (without front matter)\"}" +
        "},\"required\":[\"target\",\"mode\",\"content\"]}"
      );

      IHarnessTool tool = new DelegateHarnessTool(
        "preferences_update",
        "Overwrites or appends the preferences (rules/conventions) of the user or of the system. Use it only if the user explicitly asks to remember something.",
        schema,
        (arguments) => this.UpdatePreferences(arguments)
      );

      return new IHarnessTool[] { tool };
    }

    private string UpdatePreferences(JObject arguments) {

      string target = DelegateHarnessTool.GetRequiredString(arguments, "target");
      string mode = DelegateHarnessTool.GetRequiredString(arguments, "mode");
      string content = DelegateHarnessTool.GetRequiredString(arguments, "content");

      string fileFullName;
      string skillName;

      if (string.Equals(target, "user", StringComparison.OrdinalIgnoreCase)) {
        fileFullName = _UserPreferencesFileGetter.Invoke();
        skillName = "global-user-preferences";
      }
      else if (string.Equals(target, "system", StringComparison.OrdinalIgnoreCase)) {
        fileFullName = _SystemPreferencesFileGetter.Invoke();
        skillName = "global-system-preferences";
      }
      else {
        throw new ArgumentException("The argument 'target' must be 'user' or 'system'.");
      }

      if (string.IsNullOrWhiteSpace(fileFullName)) {
        throw new InvalidOperationException($"The location of '{skillName}' is not configured.");
      }

      if (fileFullName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || fileFullName.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidOperationException($"'{skillName}' points to a URL ({fileFullName}) and cannot be written. Ask the user to update it at its origin.");
      }

      if (Directory.Exists(fileFullName)) {
        fileFullName = Path.Combine(fileFullName, SkillFile.SkillFileName);
      }

      SkillFile skillFile;

      if (File.Exists(fileFullName)) {
        skillFile = SkillFile.Load(fileFullName);
      }
      else {
        skillFile = SkillFile.Parse(string.Empty);
        skillFile.Name = skillName;
      }

      if (string.Equals(mode, "overwrite", StringComparison.OrdinalIgnoreCase)) {
        skillFile.Body = content;
      }
      else {

        string body = skillFile.Body;

        if (string.IsNullOrWhiteSpace(body)) {
          body = content;
        }
        else {
          body = body.TrimEnd() + Environment.NewLine + Environment.NewLine + content;
        }

        skillFile.Body = body;
      }

      skillFile.Save(fileFullName);

      return $"'{skillName}' was updated ({mode}).";
    }

  }

  /// <summary>
  /// OOB-2: file system access, guarded by the allow-list 'allowedPaths' ([{ "path": "...", "access": "RO|RW" }]).
  /// </summary>
  public class FileSystemToolProvider : BuiltInToolProviderBase {

    public const string Id = "oob-filesystem";

    private const int _MaxReadLength = 200000;

    public override string ProviderId {
      get {
        return Id;
      }
    }

    public override string DisplayName {
      get {
        return "File system access";
      }
    }

    public override string GetDefaultSettingsJson() {
      return "{\r\n  \"allowedPaths\": []\r\n}";
    }

    protected override void ValidateSettings(JObject settings) {

      ValidateArrayProperty(settings, "allowedPaths");

      JArray paths = settings["allowedPaths"] as JArray;

      if (paths == null) {
        return;
      }

      foreach (JToken entry in paths) {

        JObject entryObject = entry as JObject;

        if (entryObject == null || entryObject["path"] == null || string.IsNullOrWhiteSpace(entryObject["path"].ToString())) {
          throw new ArgumentException("Every entry of 'allowedPaths' needs a 'path'.");
        }

        string access = "";

        if (entryObject["access"] != null) {
          access = entryObject["access"].ToString();
        }

        if (access != HarnessGuards.ReadOnlyAccess && access != HarnessGuards.ReadWriteAccess) {
          throw new ArgumentException($"The access of '{entryObject["path"]}' must be 'RO' or 'RW'.");
        }

      }

    }

    public override IHarnessTool[] CreateTools(IToolProviderContext context) {

      string pathSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}";
      string writeSchema = (
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}," +
        "\"append\":{\"type\":\"boolean\"}},\"required\":[\"path\",\"content\"]}"
      );

      return new IHarnessTool[] {
        new DelegateHarnessTool("fs_list_directory", "Lists the files and sub directories of a directory (local or UNC path).", pathSchema,
          (arguments) => ListDirectory(context, arguments)),
        new DelegateHarnessTool("fs_read_file", "Reads a text file (local or UNC path).", pathSchema,
          (arguments) => ReadFile(context, arguments)),
        new DelegateHarnessTool("fs_write_file", "Writes (or appends to) a text file (local or UNC path).", writeSchema,
          (arguments) => WriteFile(context, arguments))
      };
    }

    private static string ListDirectory(IToolProviderContext context, JObject arguments) {

      string fullPath = HarnessGuards.EnsurePathAllowed(context, DelegateHarnessTool.GetRequiredString(arguments, "path"), false);

      if (!Directory.Exists(fullPath)) {
        throw new DirectoryNotFoundException($"The directory '{fullPath}' does not exist.");
      }

      StringBuilder result = new StringBuilder();

      foreach (string directory in Directory.GetDirectories(fullPath).OrderBy((d) => d, StringComparer.OrdinalIgnoreCase)) {
        result.AppendLine("[DIR]  " + Path.GetFileName(directory));
      }

      foreach (string file in Directory.GetFiles(fullPath).OrderBy((f) => f, StringComparer.OrdinalIgnoreCase)) {
        result.AppendLine("[FILE] " + Path.GetFileName(file) + " (" + new FileInfo(file).Length.ToString() + " bytes)");
      }

      return result.ToString();
    }

    private static string ReadFile(IToolProviderContext context, JObject arguments) {

      string fullPath = HarnessGuards.EnsurePathAllowed(context, DelegateHarnessTool.GetRequiredString(arguments, "path"), false);
      string content = File.ReadAllText(fullPath);

      if (content.Length > _MaxReadLength) {
        content = content.Substring(0, _MaxReadLength) + Environment.NewLine + "... (truncated)";
      }

      return content;
    }

    private static string WriteFile(IToolProviderContext context, JObject arguments) {

      string fullPath = HarnessGuards.EnsurePathAllowed(context, DelegateHarnessTool.GetRequiredString(arguments, "path"), true);
      string content = DelegateHarnessTool.GetOptionalString(arguments, "content");
      bool append = false;

      if (content == null) {
        content = string.Empty;
      }

      if (arguments["append"] != null && arguments["append"].Type == JTokenType.Boolean) {
        append = arguments["append"].Value<bool>();
      }

      string directory = Path.GetDirectoryName(fullPath);

      if (!string.IsNullOrWhiteSpace(directory)) {
        Directory.CreateDirectory(directory);
      }

      if (append) {
        File.AppendAllText(fullPath, content);
      }
      else {
        File.WriteAllText(fullPath, content);
      }

      return $"'{fullPath}' was written ({content.Length} characters).";
    }

  }

  /// <summary>
  /// OOB-3: internet access (HTTP GET), guarded by the allow-list 'allowedDomains' (["host", "*.domain"]).
  /// </summary>
  public class InternetToolProvider : BuiltInToolProviderBase {

    public const string Id = "oob-internet";

    private const int _MaxContentLength = 200000;

    private static readonly HttpClient _HttpClient = CreateHttpClient();

    public override string ProviderId {
      get {
        return Id;
      }
    }

    public override string DisplayName {
      get {
        return "Internet access";
      }
    }

    public override string GetDefaultSettingsJson() {
      return "{\r\n  \"allowedDomains\": []\r\n}";
    }

    protected override void ValidateSettings(JObject settings) {

      ValidateArrayProperty(settings, "allowedDomains");

      JArray domains = settings["allowedDomains"] as JArray;

      if (domains != null && domains.Any((d) => d.Type != JTokenType.String || string.IsNullOrWhiteSpace(d.ToString()))) {
        throw new ArgumentException("Every entry of 'allowedDomains' must be a non-empty string.");
      }

    }

    public override IHarnessTool[] CreateTools(IToolProviderContext context) {

      string schema = "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}";

      IHarnessTool tool = new DelegateHarnessTool(
        "web_get", "Loads the content of a URL (HTTP GET, text only).", schema, (arguments) => Get(context, arguments)
      );

      return new IHarnessTool[] { tool };
    }

    private static HttpClient CreateHttpClient() {

      HttpClient client = new HttpClient();
      client.Timeout = TimeSpan.FromSeconds(60);

      return client;
    }

    private static string Get(IToolProviderContext context, JObject arguments) {

      string url = DelegateHarnessTool.GetRequiredString(arguments, "url");
      Uri uri;

      if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
        throw new ArgumentException($"'{url}' is not a valid http(s) URL.");
      }

      HarnessGuards.EnsureDomainAllowed(context, uri);

      using (HttpResponseMessage response = _HttpClient.GetAsync(uri).Result) {

        string content = response.Content.ReadAsStringAsync().Result;

        if (content.Length > _MaxContentLength) {
          content = content.Substring(0, _MaxContentLength) + Environment.NewLine + "... (truncated)";
        }

        if (!response.IsSuccessStatusCode) {
          throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {content}");
        }

        return content;
      }

    }

  }

  /// <summary>
  /// OOB-4: shell access (cmd / PowerShell), guarded by the allow-list 'allowedCommands'
  /// ([{ "pattern": "...", "shell": "cmd|powershell" }], literal, wildcard or 'regex:').
  /// </summary>
  public class ShellToolProvider : BuiltInToolProviderBase {

    public const string Id = "oob-shell";

    private const int _MaxOutputLength = 100000;

    public override string ProviderId {
      get {
        return Id;
      }
    }

    public override string DisplayName {
      get {
        return "Shell access";
      }
    }

    public override string GetDefaultSettingsJson() {
      return "{\r\n  \"allowedCommands\": [],\r\n  \"timeoutSeconds\": 120\r\n}";
    }

    protected override void ValidateSettings(JObject settings) {

      ValidateArrayProperty(settings, "allowedCommands");

      JToken timeout = settings["timeoutSeconds"];

      if (timeout != null && timeout.Type != JTokenType.Integer) {
        throw new ArgumentException("The property 'timeoutSeconds' must be an integer.");
      }

    }

    public override IHarnessTool[] CreateTools(IToolProviderContext context) {

      string schema = (
        "{\"type\":\"object\",\"properties\":{\"shell\":{\"type\":\"string\",\"enum\":[\"cmd\",\"powershell\"]}," +
        "\"command\":{\"type\":\"string\"},\"workingDirectory\":{\"type\":\"string\"}},\"required\":[\"shell\",\"command\"]}"
      );

      IHarnessTool tool = new DelegateHarnessTool(
        "shell_execute", "Executes a command line within cmd or PowerShell and returns exit code and output.", schema,
        (arguments) => Execute(context, arguments)
      );

      return new IHarnessTool[] { tool };
    }

    private static string Execute(IToolProviderContext context, JObject arguments) {

      string shell = DelegateHarnessTool.GetRequiredString(arguments, "shell").ToLowerInvariant();
      string command = DelegateHarnessTool.GetRequiredString(arguments, "command");
      string workingDirectory = DelegateHarnessTool.GetOptionalString(arguments, "workingDirectory");

      if (shell != "cmd" && shell != "powershell") {
        throw new ArgumentException("The argument 'shell' must be 'cmd' or 'powershell'.");
      }

      HarnessGuards.EnsureCommandAllowed(context, shell, command);

      if (!string.IsNullOrWhiteSpace(workingDirectory)) {
        workingDirectory = HarnessGuards.EnsurePathAllowed(context, workingDirectory, false);
      }

      int timeoutSeconds = 120;
      JObject settings = HarnessGuards.ParseSettings(context.SettingsJson);

      if (settings["timeoutSeconds"] != null && settings["timeoutSeconds"].Type == JTokenType.Integer) {
        timeoutSeconds = settings["timeoutSeconds"].Value<int>();
      }

      ProcessStartInfo startInfo = new ProcessStartInfo();
      startInfo.UseShellExecute = false;
      startInfo.RedirectStandardOutput = true;
      startInfo.RedirectStandardError = true;
      startInfo.CreateNoWindow = true;

      if (shell == "cmd") {
        startInfo.FileName = "cmd.exe";
        startInfo.Arguments = "/c " + command;
      }
      else {
        startInfo.FileName = "powershell.exe";
        startInfo.Arguments = "-NoProfile -NonInteractive -Command \"" + command.Replace("\"", "\\\"") + "\"";
      }

      if (!string.IsNullOrWhiteSpace(workingDirectory)) {
        startInfo.WorkingDirectory = workingDirectory;
      }

      StringBuilder output = new StringBuilder();

      using (Process process = new Process()) {

        process.StartInfo = startInfo;
        process.OutputDataReceived += (sender, e) => { AppendLine(output, e.Data); };
        process.ErrorDataReceived += (sender, e) => { AppendLine(output, e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutSeconds * 1000)) {

          process.Kill(true);

          throw new TimeoutException($"The command did not finish within {timeoutSeconds} seconds.");
        }

        process.WaitForExit();

        string text;

        lock (output) {
          text = output.ToString();
        }

        if (text.Length > _MaxOutputLength) {
          text = text.Substring(0, _MaxOutputLength) + Environment.NewLine + "... (truncated)";
        }

        return "Exit code: " + process.ExitCode.ToString() + Environment.NewLine + text;
      }

    }

    private static void AppendLine(StringBuilder output, string line) {

      if (line == null) {
        return;
      }

      lock (output) {
        output.AppendLine(line);
      }

    }

  }

}
