using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AI.SmartStandards.LowLevelPrompting {

  /// <summary>
  /// Session mode of the 'GitHubCopilotCliConnector': one long-running 'copilot --acp' process per chat.
  /// </summary>
  public partial class GitHubCopilotCliConnector {

    /// <summary>
    /// Prefix of the 'contextStore' value which holds the id of a Copilot ACP session.
    /// </summary>
    public const string SessionContextStorePrefix = "copilot-acp:";

    private static readonly TimeSpan _HandshakeTimeout = TimeSpan.FromMinutes(1);

    private static readonly object _PoolSyncRoot = new object();

    private static readonly Dictionary<string, PooledSession> _SessionPool = new Dictionary<string, PooledSession>(StringComparer.Ordinal);

    private static Timer _IdleReaper = null;

    private static bool _ProcessExitHooked = false;

    private static TimeSpan _SessionIdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Time after which an unused ACP process is terminated (default: 15 minutes). The session itself can be
    /// restored later (see 'AllowSessionResume') or rebuilt from the outer chat history.
    /// </summary>
    public static TimeSpan SessionIdleTimeout {
      get {
        return _SessionIdleTimeout;
      }
      set {
        _SessionIdleTimeout = value;
      }
    }

    /// <summary>
    /// Returns the number of currently running ACP processes (one per active chat).
    /// </summary>
    public static int ActiveSessionCount {
      get {
        lock (_PoolSyncRoot) {
          return _SessionPool.Count;
        }
      }
    }

    /// <summary>
    /// Terminates all running ACP processes (the sessions can be restored later).
    /// </summary>
    public static void ShutdownAllSessions() {

      List<PooledSession> sessions;

      lock (_PoolSyncRoot) {
        sessions = _SessionPool.Values.ToList();
        _SessionPool.Clear();
      }

      foreach (PooledSession session in sessions) {
        session.Connection.Dispose();
      }

    }

    /// <summary>
    /// Extracts the ACP session id from a 'contextStore' value (null if it is not a Copilot session reference).
    /// </summary>
    public static string GetSessionIdFromContextStore(string contextStore) {

      if (string.IsNullOrWhiteSpace(contextStore)) {
        return null;
      }

      if (!contextStore.StartsWith(SessionContextStorePrefix, StringComparison.Ordinal)) {
        return null;
      }

      string sessionId = contextStore.Substring(SessionContextStorePrefix.Length).Trim();

      if (sessionId.Length == 0) {
        return null;
      }

      return sessionId;
    }

    private sealed class PooledSession {

      private GitHubCopilotAcpSession _Connection;
      private string _Model;
      private DateTime _LastUsedUtc;
      private bool _IsBusy;

      public GitHubCopilotAcpSession Connection {
        get {
          return _Connection;
        }
        set {
          _Connection = value;
        }
      }

      public string Model {
        get {
          return _Model;
        }
        set {
          _Model = value;
        }
      }

      public DateTime LastUsedUtc {
        get {
          return _LastUsedUtc;
        }
        set {
          _LastUsedUtc = value;
        }
      }

      public bool IsBusy {
        get {
          return _IsBusy;
        }
        set {
          _IsBusy = value;
        }
      }

    }

    /// <summary>
    /// Executes a chat turn using the native ACP session of the chat (reusing, resuming or creating it).
    /// Without 'contextStore', a new session is created and the given turns (full outer history) are sent once.
    /// </summary>
    private string CallInSessionMode(
      ChatTurn[] newMessages, ref string contextStore, bool allowInteractiveLogin,
      CancellationToken cancellationToken, Action<string, int, int> onProgress
    ) {

      string requestedSessionId = null;

      if (!string.IsNullOrWhiteSpace(contextStore)) {

        requestedSessionId = GetSessionIdFromContextStore(contextStore);

        if (requestedSessionId == null) {
          throw new ContextStoreInvalidException("The context store does not reference a GitHub Copilot session.");
        }

      }

      string prompt = BuildPrompt(newMessages);

      if (requestedSessionId == null) {
        // a new session has just received the complete context history: repeat the tool rules explicitly at its end
        prompt = AppendWrapperToolReminder(prompt);
      }

      PooledSession pooled;

      if (onProgress != null) {

        if (requestedSessionId == null) {
          onProgress.Invoke("starting GitHub Copilot session", 0, 0);
        }
        else {
          onProgress.Invoke("resuming GitHub Copilot session", 0, 0);
        }

      }

      try {

        if (requestedSessionId == null) {
          pooled = this.StartNewSession();
        }
        else {
          pooled = this.AcquireExistingSession(requestedSessionId);
        }

      }
      catch (GitHubCopilotAcpException ex) {

        bool canLoginInteractively = (
          ex.IsAuthenticationError &&
          allowInteractiveLogin &&
          AllowInteractiveInstallation &&
          string.IsNullOrWhiteSpace(_GitHubToken)
        );

        if (canLoginInteractively) {

          lock (_InstallationSyncRoot) {
            RunInteractiveLogin(ResolveCli());
          }

          // Retry exactly once - a second authentication error is reported to the user
          return this.CallInSessionMode(newMessages, ref contextStore, false, cancellationToken, onProgress);
        }

        throw new InvalidOperationException(
          $"The GitHub Copilot CLI (ACP) failed: {ex.Message}" + Environment.NewLine +
          "Hint: make sure that you are logged in (run 'copilot' once and use '/login') or store a GitHub token in the settings.", ex
        );
      }

      string sessionId = pooled.Connection.SessionId;
      string answer;

      try {
        answer = pooled.Connection.Prompt(prompt, _Timeout, cancellationToken, onProgress);
      }
      catch (IOException ex) {

        this.DiscardSession(sessionId, pooled);

        if (requestedSessionId != null) {
          // the process died: let the caller rebuild the context from the outer chat history
          throw new ContextStoreInvalidException("The GitHub Copilot session was terminated unexpectedly: " + ex.Message);
        }

        throw new InvalidOperationException("The GitHub Copilot CLI (ACP) terminated unexpectedly: " + ex.Message, ex);
      }
      catch (GitHubCopilotTurnStoppedException) {

        // the process and the session are still healthy - only this turn produced no answer
        this.ReleaseSession(sessionId, pooled);
        contextStore = SessionContextStorePrefix + sessionId;

        throw;
      }
      catch (Exception) {
        this.DiscardSession(sessionId, pooled);
        throw;
      }

      this.ReleaseSession(sessionId, pooled);

      contextStore = SessionContextStorePrefix + sessionId;

      return answer;
    }

    /// <summary>
    /// Returns the running session of the chat if it is alive, otherwise tries to resume it in a new process.
    /// Throws a 'ContextStoreInvalidException' if the session cannot be continued.
    /// </summary>
    private PooledSession AcquireExistingSession(string sessionId) {

      PooledSession stale = null;

      lock (_PoolSyncRoot) {

        PooledSession pooled;

        if (_SessionPool.TryGetValue(sessionId, out pooled)) {

          if (pooled.IsBusy) {
            throw new InvalidOperationException("The GitHub Copilot session of this chat is still processing a previous message.");
          }

          if (pooled.Connection.IsAlive && string.Equals(pooled.Model, this.GetEffectiveModel(), StringComparison.Ordinal)) {
            pooled.IsBusy = true;
            pooled.LastUsedUtc = DateTime.UtcNow;
            return pooled;
          }

          // dead process or changed model: restart (and resume) it
          _SessionPool.Remove(sessionId);
          stale = pooled;
        }

      }

      if (stale != null) {
        stale.Connection.Dispose();
      }

      if (!this.AllowSessionResume) {
        throw new ContextStoreInvalidException("The GitHub Copilot session has ended (resume is disabled).");
      }

      GitHubCopilotAcpSession connection = this.StartAcpProcess();
      bool loaded;

      try {
        loaded = connection.TryLoadSession(sessionId, GetWorkingDirectory(), _HandshakeTimeout);
      }
      catch (Exception) {
        connection.Dispose();
        throw;
      }

      if (!loaded) {
        connection.Dispose();
        throw new ContextStoreInvalidException("The GitHub Copilot session has ended and could not be resumed.");
      }

      return this.AddToPool(connection);
    }

    /// <summary>
    /// Appends the explicit instruction to prefer the tools of the wrapper (harness) to a prompt which starts a new
    /// session, so that it is the LAST thing Copilot reads after the streamed context history.
    /// </summary>
    public static string AppendWrapperToolReminder(string prompt) {

      StringBuilder sb = new StringBuilder();
      sb.AppendLine(prompt);
      sb.AppendLine();
      sb.AppendLine(WrapperToolReminder);

      return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Explicit instruction (sent at the start of every new session after the context history) that the tools of the
    /// wrapper (harness) have to be preferred over the native tools of Copilot.
    /// </summary>
    public const string WrapperToolReminder = (
      "[IMPORTANT HOST INSTRUCTION]\n" +
      "ALWAYS PREFER THE TOOLS OF THE WRAPPER (the harness tools described in the context above). " +
      "Invoke them ONLY by writing a <tool_call> block as plain TEXT into your answer and then wait for the <tool_response>. " +
      "Your own native tools (shell, file access, web fetch, ...) are NOT permitted in this environment - every attempt " +
      "to use them is refused and ends your turn without an answer. If no wrapper tool fits, answer with text only."
    );

    private PooledSession StartNewSession() {

      GitHubCopilotAcpSession connection = this.StartAcpProcess();

      try {
        connection.CreateSession(GetWorkingDirectory(), _HandshakeTimeout);
      }
      catch (Exception) {
        connection.Dispose();
        throw;
      }

      return this.AddToPool(connection);
    }

    private PooledSession AddToPool(GitHubCopilotAcpSession connection) {

      PooledSession pooled = new PooledSession();
      pooled.Connection = connection;
      pooled.Model = this.GetEffectiveModel();
      pooled.LastUsedUtc = DateTime.UtcNow;
      pooled.IsBusy = true;

      PooledSession replaced;

      lock (_PoolSyncRoot) {

        if (!_SessionPool.TryGetValue(connection.SessionId, out replaced)) {
          replaced = null;
        }

        _SessionPool[connection.SessionId] = pooled;

        EnsureBackgroundServices();
      }

      if (replaced != null) {
        replaced.Connection.Dispose();
      }

      return pooled;
    }

    private void ReleaseSession(string sessionId, PooledSession pooled) {

      if (!this.KeepSessionAlive) {
        this.DiscardSession(sessionId, pooled);
        return;
      }

      lock (_PoolSyncRoot) {
        pooled.IsBusy = false;
        pooled.LastUsedUtc = DateTime.UtcNow;
      }

    }

    private void DiscardSession(string sessionId, PooledSession pooled) {

      lock (_PoolSyncRoot) {

        PooledSession current;

        if (sessionId != null && _SessionPool.TryGetValue(sessionId, out current) && object.ReferenceEquals(current, pooled)) {
          _SessionPool.Remove(sessionId);
        }

      }

      pooled.Connection.Dispose();

    }

    private string GetEffectiveModel() {

      if (string.IsNullOrWhiteSpace(_Model) || string.Equals(_Model, DefaultModelPlaceholder, StringComparison.OrdinalIgnoreCase)) {
        return string.Empty;
      }

      return _Model.Trim();
    }

    private static string GetWorkingDirectory() {

      // An empty scratch directory, so that the CLI never sees (or touches) a real repository
      string workingDirectory = Path.Combine(Path.GetTempPath(), "SmartStandards.AiSupport", "CopilotCli");
      Directory.CreateDirectory(workingDirectory);

      return workingDirectory;
    }

    /// <summary>
    /// Starts 'copilot --acp' (stdio) and performs the 'initialize' handshake.
    /// </summary>
    private GitHubCopilotAcpSession StartAcpProcess() {

      CliLaunchInfo launchInfo = ResolveCli();

      StringBuilder arguments = new StringBuilder();

      if (!string.IsNullOrWhiteSpace(launchInfo.LeadingArguments)) {
        arguments.Append(launchInfo.LeadingArguments);
        arguments.Append(' ');
      }

      arguments.Append("--acp");

      string model = this.GetEffectiveModel();

      if (model.Length > 0) {
        arguments.Append(" --model ");
        arguments.Append(QuoteArgument(model));
      }

      ProcessStartInfo startInfo = new ProcessStartInfo();
      startInfo.FileName = launchInfo.FileName;
      startInfo.Arguments = arguments.ToString();
      startInfo.WorkingDirectory = GetWorkingDirectory();
      startInfo.UseShellExecute = false;
      startInfo.CreateNoWindow = true;
      startInfo.RedirectStandardInput = true;
      startInfo.RedirectStandardOutput = true;
      startInfo.RedirectStandardError = true;
      startInfo.StandardInputEncoding = new UTF8Encoding(false);
      startInfo.StandardOutputEncoding = Encoding.UTF8;
      startInfo.StandardErrorEncoding = Encoding.UTF8;
      startInfo.EnvironmentVariables["NO_COLOR"] = "1";

      if (!string.IsNullOrWhiteSpace(_GitHubToken)) {
        startInfo.EnvironmentVariables["COPILOT_GITHUB_TOKEN"] = _GitHubToken.Trim();
      }

      Process process = new Process();
      process.StartInfo = startInfo;

      try {
        process.Start();
      }
      catch (Exception ex) {
        process.Dispose();
        throw new InvalidOperationException(
          $"The GitHub Copilot CLI could not be started ('{launchInfo.FileName}'): {ex.Message}", ex
        );
      }

      ProcessOwner owner = new ProcessOwner(process);
      process.ErrorDataReceived += (sender, e) => owner.AppendError(e.Data);
      process.BeginErrorReadLine();

      GitHubCopilotAcpSession connection = new GitHubCopilotAcpSession(
        process.StandardOutput, process.StandardInput, owner, () => owner.GetErrorText()
      );

      try {
        connection.Initialize(_HandshakeTimeout);
      }
      catch (Exception) {
        connection.Dispose();
        throw;
      }

      return connection;
    }

    private static void EnsureBackgroundServices() {

      if (_IdleReaper == null) {
        _IdleReaper = new Timer((state) => ReapIdleSessions(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
      }

      if (!_ProcessExitHooked) {
        // child processes are not terminated automatically when the host process ends
        AppDomain.CurrentDomain.ProcessExit += (sender, e) => ShutdownAllSessions();
        _ProcessExitHooked = true;
      }

    }

    private static void ReapIdleSessions() {

      List<PooledSession> expired = new List<PooledSession>();
      DateTime threshold = DateTime.UtcNow - SessionIdleTimeout;

      lock (_PoolSyncRoot) {

        foreach (KeyValuePair<string, PooledSession> entry in _SessionPool.ToArray()) {

          bool isExpired = (!entry.Value.IsBusy && (entry.Value.LastUsedUtc < threshold || !entry.Value.Connection.IsAlive));

          if (isExpired) {
            _SessionPool.Remove(entry.Key);
            expired.Add(entry.Value);
          }

        }

      }

      foreach (PooledSession session in expired) {
        session.Connection.Dispose();
      }

    }

    /// <summary>
    /// Owns the ACP process: collects the tail of stderr (for diagnostics) and kills the process tree on dispose.
    /// </summary>
    private sealed class ProcessOwner : IDisposable {

      private const int _MaxErrorTextLength = 4000;

      private readonly Process _Process;

      private readonly StringBuilder _ErrorText = new StringBuilder();

      private bool _IsDisposed = false;

      public ProcessOwner(Process process) {
        _Process = process;
      }

      public void AppendError(string line) {

        if (line == null) {
          return;
        }

        lock (_ErrorText) {

          _ErrorText.AppendLine(line);

          if (_ErrorText.Length > _MaxErrorTextLength) {
            _ErrorText.Remove(0, _ErrorText.Length - _MaxErrorTextLength);
          }

        }

      }

      public string GetErrorText() {

        lock (_ErrorText) {
          return _ErrorText.ToString();
        }

      }

      public void Dispose() {

        lock (_ErrorText) {

          if (_IsDisposed) {
            return;
          }

          _IsDisposed = true;
        }

        try {

          if (!_Process.HasExited) {
            _Process.Kill(true);
          }

        }
        catch (InvalidOperationException) {
          // the process has already exited in the meantime
        }
        catch (System.ComponentModel.Win32Exception) {
          // the process is terminating
        }

        _Process.Dispose();

      }

    }

  }

}
