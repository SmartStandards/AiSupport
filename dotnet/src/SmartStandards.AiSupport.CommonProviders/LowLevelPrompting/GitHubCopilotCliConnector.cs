using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AI.SmartStandards.LowLevelPrompting {

  /// <summary>
  /// Chat provider which drives the locally installed GitHub Copilot CLI ('copilot').
  /// Session mode (default): one long-running 'copilot --acp' process (Agent Client Protocol) per chat. The native
  /// session of Copilot holds the context, its id is returned as 'contextStore' (prefix 'copilot-acp:'), so only the new
  /// messages are transferred. Idle processes are terminated after 'SessionIdleTimeout'. A terminated session is
  /// restored via 'session/load' (if supported and allowed), otherwise a 'ContextStoreInvalidException' lets the
  /// caller (Layer-2 harness) rebuild the context from the outer chat history.
  /// Prompt mode (session mode opted out): one 'copilot -p ... -s' process per chat turn with self-maintained context.
  /// In both modes no tool permissions are granted, so the CLI acts as a pure chat model and never touches files or
  /// runs shell commands. Authentication is done by the CLI itself ('copilot' -> '/login') or via an optional GitHub
  /// token (passed as environment variable 'COPILOT_GITHUB_TOKEN').
  /// </summary>
  public partial class GitHubCopilotCliConnector : ICommonLLM {

    public const string ProviderName = "GitHubCopilotCli";

    /// <summary>
    /// Model placeholder meaning "do not pass '--model' (use the default model of the CLI)".
    /// </summary>
    public const string DefaultModelPlaceholder = "default";

    /// <summary>
    /// Environment variable which can be used to point to a specific 'copilot' executable.
    /// </summary>
    public const string ExecutableEnvironmentVariable = "COPILOT_CLI_PATH";

    // Windows limits the whole command line to 32767 characters
    private const int _MaxPromptLength = 30000;

    private readonly string _GitHubToken;

    private readonly string _Model;

    private readonly TimeSpan _Timeout;

    private bool _UseSessionMode = true;

    private bool _KeepSessionAlive = true;

    private bool _AllowSessionResume = true;

    /// <summary>
    /// If true (default), the native ACP session of Copilot is used (capability 'ProviderSideContext').
    /// If false, every turn is executed in prompt mode with the complete (self-maintained) conversation.
    /// </summary>
    public bool UseSessionMode {
      get {
        return _UseSessionMode;
      }
      set {
        _UseSessionMode = value;
      }
    }

    /// <summary>
    /// If true (default), the ACP process of a chat stays alive between the turns until it is idle for
    /// 'SessionIdleTimeout' (capability 'PersistentSession'). If false, the process is terminated after every turn.
    /// </summary>
    public bool KeepSessionAlive {
      get {
        return _KeepSessionAlive;
      }
      set {
        _KeepSessionAlive = value;
      }
    }

    /// <summary>
    /// If true (default), a terminated session is restored via 'session/load' using the session id from the
    /// 'contextStore' (capability 'SessionResume'). If false (or not supported by the CLI), the caller has to
    /// rebuild the context from the outer chat history.
    /// </summary>
    public bool AllowSessionResume {
      get {
        return _AllowSessionResume;
      }
      set {
        _AllowSessionResume = value;
      }
    }

    /// <summary>
    /// Creates the connector using the CLI login, the default model and a timeout of 5 minutes.
    /// </summary>
    public GitHubCopilotCliConnector() : this(null, null) {
    }

    /// <summary>
    /// Creates the connector. 'gitHubToken' is optional (if empty, the login of the CLI is used).
    /// 'model' is optional (if empty or 'default', the default model of the CLI is used).
    /// </summary>
    public GitHubCopilotCliConnector(string gitHubToken, string model) : this(gitHubToken, model, TimeSpan.FromMinutes(5)) {
    }

    /// <summary>
    /// Creates the connector with an explicit timeout per chat turn.
    /// </summary>
    public GitHubCopilotCliConnector(string gitHubToken, string model, TimeSpan timeout) {
      _GitHubToken = gitHubToken;
      _Model = model;
      _Timeout = timeout;
    }

    /// <summary>
    /// Sends the complete (self-maintained) conversation to the Copilot CLI and returns its answer.
    /// </summary>
    public string CallChatApi(ChatTurn[] newMessages, ref string contextStore) {

      if (newMessages == null || newMessages.Length == 0) {
        throw new ArgumentException("At least one message is required.", nameof(newMessages));
      }

      if (this.UseSessionMode) {
        return this.CallInSessionMode(newMessages, ref contextStore, true);
      }

      // Prompt mode has no provider-side context, so the caller always passes the full history
      contextStore = null;

      string prompt = BuildPrompt(newMessages);

      if (prompt.Length > _MaxPromptLength) {
        throw new InvalidOperationException(
          "The conversation is too long to be passed to the GitHub Copilot CLI. Please compress the chat context or start a new chat."
        );
      }

      return this.RunCli(prompt, true);
    }

    /// <summary>
    /// Renders the given turns into a single prompt. A single user turn is passed unchanged; longer conversations
    /// are rendered as transcript followed by an instruction to answer the last user message.
    /// </summary>
    public static string BuildPrompt(ChatTurn[] turns) {

      if (turns.Length == 1 && string.Equals(turns[0].Role, "user", StringComparison.OrdinalIgnoreCase)) {
        return turns[0].Content;
      }

      StringBuilder sb = new StringBuilder();
      sb.AppendLine("The following is a conversation between a user and you (the assistant).");
      sb.AppendLine("Continue it by answering the LAST user message. Reply with the answer text only.");
      sb.AppendLine();

      foreach (ChatTurn turn in turns) {

        string label;

        if (string.Equals(turn.Role, "system", StringComparison.OrdinalIgnoreCase)) {
          label = "[CONTEXT]";
        }
        else if (string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)) {
          label = "[ASSISTANT]";
        }
        else {
          label = "[USER]";
        }

        sb.AppendLine(label);
        sb.AppendLine(turn.Content);
        sb.AppendLine();
      }

      return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Quotes a single command line argument according to the rules of 'CommandLineToArgvW'
    /// (backslashes are only escaped if they precede a double quote).
    /// </summary>
    public static string QuoteArgument(string argument) {

      if (argument == null) {
        argument = string.Empty;
      }

      StringBuilder sb = new StringBuilder();
      sb.Append('"');

      int pendingBackslashes = 0;

      foreach (char c in argument) {

        if (c == '\\') {
          pendingBackslashes++;
          continue;
        }

        if (c == '"') {
          sb.Append('\\', pendingBackslashes * 2 + 1);
          sb.Append('"');
        }
        else {
          sb.Append('\\', pendingBackslashes);
          sb.Append(c);
        }

        pendingBackslashes = 0;
      }

      // Backslashes before the closing quote must be doubled
      sb.Append('\\', pendingBackslashes * 2);
      sb.Append('"');

      return sb.ToString();
    }

    /// <summary>
    /// Runs the CLI in prompt mode. If 'allowInteractiveLogin' is true and the CLI reports missing authentication,
    /// an interactive login console is opened and the call is retried once.
    /// </summary>
    private string RunCli(string prompt, bool allowInteractiveLogin) {

      CliLaunchInfo launchInfo = ResolveCli();

      StringBuilder arguments = new StringBuilder();

      if (!string.IsNullOrWhiteSpace(launchInfo.LeadingArguments)) {
        arguments.Append(launchInfo.LeadingArguments);
        arguments.Append(' ');
      }

      arguments.Append("-p ");
      arguments.Append(QuoteArgument(prompt));
      arguments.Append(" -s --no-color");

      if (!string.IsNullOrWhiteSpace(_Model) && !string.Equals(_Model, DefaultModelPlaceholder, StringComparison.OrdinalIgnoreCase)) {
        arguments.Append(" --model ");
        arguments.Append(QuoteArgument(_Model.Trim()));
      }

      // Run in an empty scratch directory so that the CLI never sees (or touches) a real repository
      string workingDirectory = Path.Combine(Path.GetTempPath(), "SmartStandards.AiSupport", "CopilotCli");
      Directory.CreateDirectory(workingDirectory);

      ProcessStartInfo startInfo = new ProcessStartInfo();
      startInfo.FileName = launchInfo.FileName;
      startInfo.Arguments = arguments.ToString();
      startInfo.WorkingDirectory = workingDirectory;
      startInfo.UseShellExecute = false;
      startInfo.CreateNoWindow = true;
      startInfo.RedirectStandardInput = true;
      startInfo.RedirectStandardOutput = true;
      startInfo.RedirectStandardError = true;
      startInfo.StandardOutputEncoding = Encoding.UTF8;
      startInfo.StandardErrorEncoding = Encoding.UTF8;
      startInfo.EnvironmentVariables["NO_COLOR"] = "1";

      if (!string.IsNullOrWhiteSpace(_GitHubToken)) {
        startInfo.EnvironmentVariables["COPILOT_GITHUB_TOKEN"] = _GitHubToken.Trim();
      }

      using (Process process = new Process()) {

        process.StartInfo = startInfo;

        try {
          process.Start();
        }
        catch (Exception ex) {
          throw new InvalidOperationException(
            $"The GitHub Copilot CLI could not be started ('{launchInfo.FileName}'): {ex.Message}", ex
          );
        }

        // Nothing is passed via stdin - closing it prevents the CLI from waiting for input
        process.StandardInput.Close();

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)_Timeout.TotalMilliseconds)) {

          try {
            process.Kill();
          }
          catch (InvalidOperationException) {
            // the process has already exited in the meantime
          }

          throw new TimeoutException($"The GitHub Copilot CLI did not answer within {_Timeout.TotalSeconds} seconds.");
        }

        string stdOut = stdOutTask.Result.Trim();
        string stdErr = stdErrTask.Result.Trim();

        if (process.ExitCode != 0) {

          string details = stdErr;

          if (string.IsNullOrWhiteSpace(details)) {
            details = stdOut;
          }

          bool canLoginInteractively = (
            allowInteractiveLogin &&
            AllowInteractiveInstallation &&
            string.IsNullOrWhiteSpace(_GitHubToken) &&
            IsAuthenticationError(details)
          );

          if (canLoginInteractively) {

            lock (_InstallationSyncRoot) {
              RunInteractiveLogin(launchInfo);
            }

            // Retry exactly once - a second authentication error is reported to the user
            return this.RunCli(prompt, false);
          }

          throw new InvalidOperationException(
            $"The GitHub Copilot CLI failed (exit code {process.ExitCode}): {details}" + Environment.NewLine +
            "Hint: make sure that you are logged in (run 'copilot' once and use '/login') or store a GitHub token in the settings."
          );
        }

        if (string.IsNullOrWhiteSpace(stdOut)) {
          throw new InvalidOperationException("The GitHub Copilot CLI returned an empty answer. " + stdErr);
        }

        return stdOut;
      }

    }

    #region " CLI Resolution "

    private sealed class CliLaunchInfo {

      private string _FileName;
      private string _LeadingArguments;

      public string FileName {
        get {
          return _FileName;
        }
        set {
          _FileName = value;
        }
      }

      public string LeadingArguments {
        get {
          return _LeadingArguments;
        }
        set {
          _LeadingArguments = value;
        }
      }

    }

    /// <summary>
    /// Locates the 'copilot' executable (env var 'COPILOT_CLI_PATH' or PATH). If only the npm '.cmd' shim is found,
    /// the underlying node script is launched directly, because 'cmd.exe' would break multi-line prompts.
    /// </summary>
    private static CliLaunchInfo ResolveCli() {

      string executable = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);

      if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) {
        executable = FindOnPath("copilot");
      }

      if (executable == null && AllowInteractiveInstallation) {

        lock (_InstallationSyncRoot) {

          // another chat turn may have installed it in the meantime
          executable = FindOnPath("copilot");

          if (executable == null) {
            RunInteractiveInstallation();
            executable = FindOnPath("copilot");
          }

        }

      }

      if (executable == null) {
        throw new InvalidOperationException(
          "The GitHub Copilot CLI ('copilot') is not installed (or the installation was declined/failed). " +
          "Send a message again to restart the setup, or select another provider in the settings."
        );
      }

      CliLaunchInfo launchInfo = new CliLaunchInfo();
      string extension = Path.GetExtension(executable);

      bool isBatchShim = (
        string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
      );

      if (!isBatchShim) {
        launchInfo.FileName = executable;
        return launchInfo;
      }

      string packageDirectory = Path.Combine(Path.GetDirectoryName(executable), "node_modules", "@github", "copilot");
      string script = FindNodeEntryScript(packageDirectory);
      string node = FindOnPath("node");

      if (script == null || node == null) {
        throw new InvalidOperationException(
          $"The GitHub Copilot CLI was found as batch file ('{executable}'), but the underlying node script " +
          "could not be resolved. Please set the environment variable '" + ExecutableEnvironmentVariable + "' to a native executable."
        );
      }

      launchInfo.FileName = node;
      launchInfo.LeadingArguments = QuoteArgument(script);

      return launchInfo;
    }

    private static string FindNodeEntryScript(string packageDirectory) {

      string[] candidates = new string[] { "npm-loader.js", "index.js" };

      foreach (string candidate in candidates) {

        string fullName = Path.Combine(packageDirectory, candidate);

        if (File.Exists(fullName)) {
          return fullName;
        }

      }

      return null;
    }

    private static string FindOnPath(string commandName) {

      string[] extensions = new string[] { ".exe", ".cmd", ".bat", "" };

      foreach (string directory in GetSearchDirectories()) {

        if (string.IsNullOrWhiteSpace(directory)) {
          continue;
        }

        foreach (string extension in extensions) {

          string fullName;

          try {
            fullName = Path.Combine(directory.Trim().Trim('"'), commandName + extension);
          }
          catch (ArgumentException) {
            // ignore malformed PATH entries
            continue;
          }

          if (File.Exists(fullName)) {
            return fullName;
          }

        }

      }

      return null;
    }

    /// <summary>
    /// Returns the directories of the process PATH, the persisted Machine/User PATH (which already contains
    /// changes of installations done after the start of this process) and the WinGet links folder.
    /// </summary>
    private static string[] GetSearchDirectories() {

      List<string> directories = new List<string>();

      AddPathDirectories(directories, Environment.GetEnvironmentVariable("PATH"));

      if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
        AddPathDirectories(directories, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        AddPathDirectories(directories, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddPathDirectories(directories, Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));

        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AddPathDirectories(directories, Path.Combine(roamingAppData, "npm"));
      }

      return directories.ToArray();
    }

    private static void AddPathDirectories(List<string> target, string pathVariable) {

      if (string.IsNullOrWhiteSpace(pathVariable)) {
        return;
      }

      foreach (string entry in pathVariable.Split(Path.PathSeparator)) {

        string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));

        if (directory.Length > 0 && !target.Contains(directory, StringComparer.OrdinalIgnoreCase)) {
          target.Add(directory);
        }

      }

    }

    #endregion

    #region " Interactive Installation "

    private static readonly object _InstallationSyncRoot = new object();

    private static bool _AllowInteractiveInstallation = Environment.UserInteractive;

    /// <summary>
    /// If true (default for interactive desktop processes), a console window is opened when the CLI is missing,
    /// which asks 'Do you wish to install it now? [Y/n]', installs the CLI (winget, fallback npm) and starts
    /// 'copilot' once so that the user can log in. Should be false for server processes.
    /// </summary>
    public static bool AllowInteractiveInstallation {
      get {
        return _AllowInteractiveInstallation;
      }
      set {
        _AllowInteractiveInstallation = value;
      }
    }

    private const string _InstallerScript = @"
$Host.UI.RawUI.WindowTitle = 'GitHub Copilot CLI - Setup'
Write-Host ''
Write-Host 'The GitHub Copilot CLI (copilot) is required for the selected chat provider, but it is not installed.' -ForegroundColor Yellow
Write-Host ''
$answer = Read-Host 'Do you wish to install it now? [Y/n]'
if ($answer -and @('y','yes','j','ja') -notcontains $answer.Trim().ToLower()) {
  Write-Host 'Installation declined.'
  Start-Sleep -Seconds 2
  exit 2
}

if (Get-Command winget -ErrorAction SilentlyContinue) {
  Write-Host 'Installing via winget...' -ForegroundColor Cyan
  winget install --id GitHub.Copilot -e --accept-source-agreements --accept-package-agreements
}
elseif (Get-Command npm -ErrorAction SilentlyContinue) {
  Write-Host 'Installing via npm...' -ForegroundColor Cyan
  npm install -g @github/copilot
}
else {
  Write-Host 'Neither winget nor npm is available on this machine.' -ForegroundColor Red
  Write-Host 'Please install the GitHub Copilot CLI manually: https://github.com/github/copilot-cli'
  Read-Host 'Press Enter to close this window'
  exit 3
}

$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User') + ';' + $env:LOCALAPPDATA + '\Microsoft\WinGet\Links'

if (-not (Get-Command copilot -ErrorAction SilentlyContinue)) {
  Write-Host 'The installation did not succeed (see output above).' -ForegroundColor Red
  Read-Host 'Press Enter to close this window'
  exit 4
}

Write-Host ''
Write-Host 'Installation finished. Now please log in to GitHub:' -ForegroundColor Green
Write-Host '  1. type  /login  and press Enter, then follow the steps in the browser'
Write-Host '  2. afterwards type  /exit  to close this window and return to the chat'
Write-Host ''
copilot
exit 0
";

    /// <summary>
    /// Opens a visible console window which asks the user whether the CLI should be installed, performs the
    /// installation and lets the user log in. Blocks until the window is closed.
    /// </summary>
    private static void RunInteractiveInstallation() {

      if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
        return;
      }

      string workingDirectory = Path.Combine(Path.GetTempPath(), "SmartStandards.AiSupport", "CopilotCli");
      Directory.CreateDirectory(workingDirectory);

      string scriptFileFullName = Path.Combine(workingDirectory, "install-copilot-cli.ps1");
      File.WriteAllText(scriptFileFullName, _InstallerScript, new UTF8Encoding(true));

      ProcessStartInfo startInfo = new ProcessStartInfo();
      startInfo.FileName = "powershell.exe";
      startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(scriptFileFullName);
      startInfo.WorkingDirectory = workingDirectory;
      startInfo.UseShellExecute = true;
      startInfo.WindowStyle = ProcessWindowStyle.Normal;

      using (Process process = Process.Start(startInfo)) {
        process.WaitForExit();
      }

    }

    /// <summary>
    /// Returns true if the given CLI error output indicates missing or invalid GitHub authentication.
    /// </summary>
    public static bool IsAuthenticationError(string cliOutput) {

      if (string.IsNullOrWhiteSpace(cliOutput)) {
        return false;
      }

      string[] markers = new string[] {
        "No authentication information found",
        "not authenticated",
        "authentication failed",
        "/login"
      };

      foreach (string marker in markers) {
        if (cliOutput.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) {
          return true;
        }
      }

      return false;
    }

    private const string _LoginScript = @"
param([string]$CopilotFile, [string]$CopilotLeadingArgs)
$Host.UI.RawUI.WindowTitle = 'GitHub Copilot CLI - Login'
Write-Host ''
Write-Host 'The GitHub Copilot CLI is installed, but you are not logged in to GitHub yet.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Copilot will start now. Please:' -ForegroundColor Green
Write-Host '  1. type  /login  and press Enter'
Write-Host '  2. follow the steps (open the shown URL in the browser and enter the code)'
Write-Host '  3. afterwards type  /exit  to close this window and return to the chat'
Write-Host ''
Read-Host 'Press Enter to start Copilot'
if ($CopilotLeadingArgs) {
  & $CopilotFile $CopilotLeadingArgs
}
else {
  & $CopilotFile
}
exit 0
";

    /// <summary>
    /// Opens a visible console window which starts the CLI interactively, so that the user can log in via '/login'.
    /// Blocks until the window is closed.
    /// </summary>
    private static void RunInteractiveLogin(CliLaunchInfo launchInfo) {

      if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
        return;
      }

      string workingDirectory = Path.Combine(Path.GetTempPath(), "SmartStandards.AiSupport", "CopilotCli");
      Directory.CreateDirectory(workingDirectory);

      string scriptFileFullName = Path.Combine(workingDirectory, "login-copilot-cli.ps1");
      File.WriteAllText(scriptFileFullName, _LoginScript, new UTF8Encoding(true));

      // LeadingArguments is already quoted (node script path) - pass it unquoted as single value
      string leadingArguments = string.Empty;

      if (!string.IsNullOrWhiteSpace(launchInfo.LeadingArguments)) {
        leadingArguments = launchInfo.LeadingArguments.Trim('"');
      }

      StringBuilder arguments = new StringBuilder();
      arguments.Append("-NoProfile -ExecutionPolicy Bypass -File ");
      arguments.Append(QuoteArgument(scriptFileFullName));
      arguments.Append(" -CopilotFile ");
      arguments.Append(QuoteArgument(launchInfo.FileName));

      if (leadingArguments.Length > 0) {
        arguments.Append(" -CopilotLeadingArgs ");
        arguments.Append(QuoteArgument(leadingArguments));
      }

      ProcessStartInfo startInfo = new ProcessStartInfo();
      startInfo.FileName = "powershell.exe";
      startInfo.Arguments = arguments.ToString();
      startInfo.WorkingDirectory = workingDirectory;
      startInfo.UseShellExecute = true;
      startInfo.WindowStyle = ProcessWindowStyle.Normal;

      using (Process process = Process.Start(startInfo)) {
        process.WaitForExit();
      }

    }

    #endregion

    public string CallWebSearchApi(string prompt, object inputData = null) {
      throw new NotSupportedException("The GitHub Copilot CLI connector does not support web search.");
    }

    public T CallWebSearchApi<T>(string prompt, object inputData = null) where T : class {
      throw new NotSupportedException("The GitHub Copilot CLI connector does not support web search.");
    }

    public byte[] CallImageEditApi(byte[] inputImageBytes, string prompt, byte[] maskImageBytes = null) {
      throw new NotSupportedException("The GitHub Copilot CLI connector does not support image editing.");
    }

    public byte[] CallImageGeneratorApi(string prompt) {
      throw new NotSupportedException("The GitHub Copilot CLI connector does not support image generation.");
    }

  }

}
