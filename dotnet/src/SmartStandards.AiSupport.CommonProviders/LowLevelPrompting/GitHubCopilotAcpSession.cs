using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AI.SmartStandards.LowLevelPrompting {

  /// <summary>
  /// Minimal client for the Agent Client Protocol (ACP, JSON-RPC 2.0 as newline-delimited JSON over stdio), as spoken by
  /// 'copilot --acp'. One instance represents one connection to one agent process and holds (at most) one session.
  /// It is used as pure chat: the client advertises no file system or terminal capabilities and refuses every
  /// permission request of the agent, so the agent never touches files or runs commands.
  /// The transport is abstracted via TextReader/TextWriter, so that the protocol handling can be tested without a process.
  /// </summary>
  public class GitHubCopilotAcpSession : IDisposable {

    /// <summary>
    /// ACP protocol version which is requested during 'initialize'.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// JSON-RPC error code which ACP uses for 'authentication required'.
    /// </summary>
    public const int AuthRequiredErrorCode = -32000;

    private const int _MethodNotFoundErrorCode = -32601;

    private readonly TextReader _Input;

    private readonly TextWriter _Output;

    private readonly IDisposable _Owner;

    private readonly Func<string> _DiagnosticsProvider;

    private readonly object _WriteSyncRoot = new object();

    private readonly object _TurnSyncRoot = new object();

    private readonly object _AnswerSyncRoot = new object();

    private readonly Dictionary<long, TaskCompletionSource<JToken>> _PendingRequests = new Dictionary<long, TaskCompletionSource<JToken>>();

    private readonly Thread _ReaderThread;

    private long _LastRequestId = 0;

    private volatile bool _IsClosed = false;

    private string _CloseReason = null;

    private StringBuilder _CurrentAnswer = null;

    private Action<string, int, int> _CurrentProgress = null;

    private DateTime _LastProgressUtc = DateTime.MinValue;

    private static readonly TimeSpan _ProgressThrottle = TimeSpan.FromMilliseconds(250);

    private bool _SupportsLoadSession = false;

    private bool _IsInitialized = false;

    private string _SessionId = null;

    /// <summary>
    /// Creates the client on top of an already established transport.
    /// </summary>
    /// <param name="input">Messages from the agent (for example the stdout of the agent process).</param>
    /// <param name="output">Messages to the agent (for example the stdin of the agent process).</param>
    /// <param name="owner">Optional resource which is disposed together with this instance (for example the process).</param>
    /// <param name="diagnosticsProvider">Optional source of additional diagnostics for error messages (for example stderr).</param>
    public GitHubCopilotAcpSession(TextReader input, TextWriter output, IDisposable owner, Func<string> diagnosticsProvider) {

      if (input == null) {
        throw new ArgumentNullException(nameof(input));
      }

      if (output == null) {
        throw new ArgumentNullException(nameof(output));
      }

      _Input = input;
      _Output = output;
      _Owner = owner;
      _DiagnosticsProvider = diagnosticsProvider;

      _ReaderThread = new Thread(this.ReadLoop);
      _ReaderThread.IsBackground = true;
      _ReaderThread.Name = "ACP reader";
      _ReaderThread.Start();

    }

    /// <summary>
    /// True as long as the connection to the agent is open.
    /// </summary>
    public bool IsAlive {
      get {
        return !_IsClosed;
      }
    }

    /// <summary>
    /// True if the agent advertised 'loadSession' during 'initialize'.
    /// </summary>
    public bool SupportsLoadSession {
      get {
        return _SupportsLoadSession;
      }
    }

    /// <summary>
    /// Id of the current session (null before 'CreateSession' or 'TryLoadSession').
    /// </summary>
    public string SessionId {
      get {
        return _SessionId;
      }
    }

    #region " Protocol operations "

    /// <summary>
    /// Performs the ACP 'initialize' handshake. Must be called once before any session operation.
    /// </summary>
    public void Initialize(TimeSpan timeout) {

      JObject fileSystemCapabilities = new JObject();
      fileSystemCapabilities["readTextFile"] = false;
      fileSystemCapabilities["writeTextFile"] = false;

      JObject clientCapabilities = new JObject();
      clientCapabilities["fs"] = fileSystemCapabilities;
      clientCapabilities["terminal"] = false;

      JObject clientInfo = new JObject();
      clientInfo["name"] = "SmartStandards.AiSupport";
      clientInfo["version"] = "1.0";

      JObject parameters = new JObject();
      parameters["protocolVersion"] = ProtocolVersion;
      parameters["clientCapabilities"] = clientCapabilities;
      parameters["clientInfo"] = clientInfo;

      JToken result = this.SendRequest("initialize", parameters, timeout);

      JObject resultObject = result as JObject;

      if (resultObject != null) {

        JObject agentCapabilities = resultObject["agentCapabilities"] as JObject;

        if (agentCapabilities != null && agentCapabilities["loadSession"] != null) {
          _SupportsLoadSession = (agentCapabilities["loadSession"].Type == JTokenType.Boolean && (bool)agentCapabilities["loadSession"]);
        }

      }

      _IsInitialized = true;

    }

    /// <summary>
    /// Creates a new session ('session/new') and returns its id.
    /// </summary>
    public string CreateSession(string workingDirectory, TimeSpan timeout) {

      this.EnsureInitialized();

      JObject parameters = new JObject();
      parameters["cwd"] = workingDirectory;
      parameters["mcpServers"] = new JArray();

      JToken result = this.SendRequest("session/new", parameters, timeout);

      JObject resultObject = result as JObject;
      string sessionId = null;

      if (resultObject != null) {
        sessionId = (string)resultObject["sessionId"];
      }

      if (string.IsNullOrWhiteSpace(sessionId)) {
        throw new InvalidOperationException("The ACP agent did not return a session id for 'session/new'.");
      }

      _SessionId = sessionId;

      return sessionId;
    }

    /// <summary>
    /// Tries to restore an ended session by its id ('session/load'). Returns false if the agent does not support
    /// loading sessions or rejects the id. Authentication errors are NOT swallowed.
    /// The history which the agent replays during the load is ignored (the caller already knows it).
    /// </summary>
    public bool TryLoadSession(string sessionId, string workingDirectory, TimeSpan timeout) {

      this.EnsureInitialized();

      if (!_SupportsLoadSession || string.IsNullOrWhiteSpace(sessionId)) {
        return false;
      }

      JObject parameters = new JObject();
      parameters["sessionId"] = sessionId;
      parameters["cwd"] = workingDirectory;
      parameters["mcpServers"] = new JArray();

      try {
        this.SendRequest("session/load", parameters, timeout);
      }
      catch (GitHubCopilotAcpException ex) {

        if (ex.IsAuthenticationError) {
          throw;
        }

        return false;
      }

      _SessionId = sessionId;

      return true;
    }

    /// <summary>
    /// Sends a prompt to the current session ('session/prompt') and returns the collected answer text
    /// (all 'agent_message_chunk' updates of this turn). On timeout, the turn is cancelled ('session/cancel').
    /// </summary>
    public string Prompt(string text, TimeSpan timeout) {

      return this.Prompt(text, timeout, CancellationToken.None, null);
    }

    /// <summary>
    /// Sends a prompt to the current session ('session/prompt') and returns the collected answer text.
    /// On timeout or cancellation, the turn is cancelled ('session/cancel'). 'onProgress' (optional) receives the last
    /// non-empty line of the streamed answer, 'thinking ...' or the title of a tool call of the agent (throttled).
    /// </summary>
    public string Prompt(string text, TimeSpan timeout, CancellationToken cancellationToken, Action<string, int, int> onProgress) {

      if (_SessionId == null) {
        throw new InvalidOperationException("There is no ACP session. Call 'CreateSession' or 'TryLoadSession' first.");
      }

      lock (_TurnSyncRoot) {

        StringBuilder answer = new StringBuilder();

        lock (_AnswerSyncRoot) {
          _CurrentAnswer = answer;
          _CurrentProgress = onProgress;
          _LastProgressUtc = DateTime.MinValue;
        }

        if (onProgress != null) {

          try {
            onProgress.Invoke("waiting for GitHub Copilot", 0, 0);
          }
          catch (Exception) {
            // progress is purely informative
          }

        }

        try {

          JObject textBlock = new JObject();
          textBlock["type"] = "text";
          textBlock["text"] = text;

          JObject parameters = new JObject();
          parameters["sessionId"] = _SessionId;
          parameters["prompt"] = new JArray(textBlock);

          JToken result;

          try {
            result = this.SendRequest("session/prompt", parameters, timeout, cancellationToken);
          }
          catch (TimeoutException) {
            this.SendCancel();
            throw;
          }
          catch (OperationCanceledException) {
            this.SendCancel();
            throw;
          }

          string stopReason = null;
          JObject resultObject = result as JObject;

          if (resultObject != null) {
            stopReason = (string)resultObject["stopReason"];
          }

          string answerText;

          lock (_AnswerSyncRoot) {
            answerText = answer.ToString().Trim();
          }

          if (string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase)) {
            throw new OperationCanceledException("The ACP agent cancelled the turn.");
          }

          if (answerText.Length == 0) {

            if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase)) {
              return "(The model refused to answer.)";
            }

            throw new InvalidOperationException($"The ACP agent returned an empty answer (stop reason: '{stopReason}').");
          }

          return answerText;
        }
        finally {

          lock (_AnswerSyncRoot) {
            _CurrentAnswer = null;
            _CurrentProgress = null;
          }

        }

      }

    }

    /// <summary>
    /// Closes the connection and disposes the owner (for example kills the agent process).
    /// </summary>
    public void Dispose() {

      this.Close("The ACP session was disposed.");

      try {
        _Output.Dispose();
      }
      catch (IOException) {
        // the pipe is already broken
      }

      if (_Owner != null) {
        _Owner.Dispose();
      }

    }

    #endregion

    #region " Transport "

    private void EnsureInitialized() {

      if (!_IsInitialized) {
        throw new InvalidOperationException("The ACP connection is not initialized. Call 'Initialize' first.");
      }

    }

    private void SendCancel() {

      if (_SessionId == null || _IsClosed) {
        return;
      }

      JObject parameters = new JObject();
      parameters["sessionId"] = _SessionId;

      JObject notification = new JObject();
      notification["jsonrpc"] = "2.0";
      notification["method"] = "session/cancel";
      notification["params"] = parameters;

      this.TryWriteMessage(notification);

    }

    private JToken SendRequest(string method, JObject parameters, TimeSpan timeout) {

      return this.SendRequest(method, parameters, timeout, CancellationToken.None);
    }

    private JToken SendRequest(string method, JObject parameters, TimeSpan timeout, CancellationToken cancellationToken) {

      long id = Interlocked.Increment(ref _LastRequestId);
      TaskCompletionSource<JToken> completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);

      lock (_PendingRequests) {

        if (_IsClosed) {
          throw new IOException(this.BuildClosedMessage());
        }

        _PendingRequests[id] = completion;
      }

      JObject request = new JObject();
      request["jsonrpc"] = "2.0";
      request["id"] = id;
      request["method"] = method;
      request["params"] = parameters;

      if (!this.TryWriteMessage(request)) {
        this.RemovePendingRequest(id);
        throw new IOException(this.BuildClosedMessage());
      }

      bool completed;

      try {
        completed = completion.Task.Wait((int)timeout.TotalMilliseconds, cancellationToken);
      }
      catch (OperationCanceledException) {
        this.RemovePendingRequest(id);
        throw;
      }
      catch (AggregateException ex) {

        if (ex.InnerExceptions.Count == 1) {
          System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        throw;
      }

      if (!completed) {
        this.RemovePendingRequest(id);
        throw new TimeoutException($"The ACP agent did not answer '{method}' within {timeout.TotalSeconds} seconds.");
      }

      return completion.Task.Result;
    }

    private void RemovePendingRequest(long id) {

      lock (_PendingRequests) {
        _PendingRequests.Remove(id);
      }

    }

    private bool TryWriteMessage(JObject message) {

      string line = message.ToString(Formatting.None);

      lock (_WriteSyncRoot) {

        if (_IsClosed) {
          return false;
        }

        try {
          _Output.WriteLine(line);
          _Output.Flush();
        }
        catch (IOException ex) {
          this.Close("Writing to the ACP agent failed: " + ex.Message);
          return false;
        }
        catch (ObjectDisposedException) {
          this.Close("The connection to the ACP agent is closed.");
          return false;
        }

      }

      return true;
    }

    private void ReadLoop() {

      try {

        while (true) {

          string line = _Input.ReadLine();

          if (line == null) {
            break;
          }

          if (string.IsNullOrWhiteSpace(line)) {
            continue;
          }

          JObject message;

          try {
            message = JObject.Parse(line);
          }
          catch (JsonReaderException) {
            // non-protocol output (for example log lines) is ignored
            continue;
          }

          this.HandleMessage(message);
        }

        this.Close("The ACP agent closed the connection.");

      }
      catch (IOException ex) {
        this.Close("Reading from the ACP agent failed: " + ex.Message);
      }
      catch (ObjectDisposedException) {
        this.Close("The connection to the ACP agent is closed.");
      }

    }

    private void HandleMessage(JObject message) {

      JToken idToken = message["id"];
      string method = null;

      if (message["method"] != null && message["method"].Type == JTokenType.String) {
        method = (string)message["method"];
      }

      JObject parameters = message["params"] as JObject;

      if (method == null) {
        this.HandleResponse(idToken, message);
        return;
      }

      if (idToken == null || idToken.Type == JTokenType.Null) {
        this.HandleNotification(method, parameters);
        return;
      }

      this.HandleAgentRequest(idToken, method);

    }

    private void HandleResponse(JToken idToken, JObject message) {

      if (idToken == null) {
        return;
      }

      long id;

      if (!long.TryParse(idToken.ToString(), out id)) {
        return;
      }

      TaskCompletionSource<JToken> completion;

      lock (_PendingRequests) {

        if (!_PendingRequests.TryGetValue(id, out completion)) {
          return;
        }

        _PendingRequests.Remove(id);
      }

      JObject error = message["error"] as JObject;

      if (error != null) {

        int code = 0;

        if (error["code"] != null && error["code"].Type == JTokenType.Integer) {
          code = (int)error["code"];
        }

        string errorMessage = (string)error["message"];

        if (error["data"] != null) {
          errorMessage = errorMessage + " " + error["data"].ToString(Formatting.None);
        }

        completion.TrySetException(new GitHubCopilotAcpException(code, errorMessage));
        return;
      }

      completion.TrySetResult(message["result"]);

    }

    private void HandleNotification(string method, JObject parameters) {

      if (method != "session/update" || parameters == null) {
        return;
      }

      JObject update = parameters["update"] as JObject;

      if (update == null) {
        return;
      }

      if (!string.Equals((string)parameters["sessionId"], _SessionId, StringComparison.Ordinal)) {
        return;
      }

      string updateKind = (string)update["sessionUpdate"];

      if (updateKind == "agent_thought_chunk") {
        this.ReportProgress("thinking ...", false);
        return;
      }

      if (updateKind == "tool_call" || updateKind == "tool_call_update") {

        string title = (string)update["title"];
        string toolStatus = (string)update["status"];

        if (!string.IsNullOrWhiteSpace(title)) {

          string toolText = "Copilot: " + title.Trim();

          if (!string.IsNullOrWhiteSpace(toolStatus)) {
            toolText = toolText + " (" + toolStatus.Trim() + ")";
          }

          //tool calls are rare and meaningful - they are never throttled away
          this.ReportProgress(toolText, true);
        }

        return;
      }

      if (updateKind != "agent_message_chunk") {
        return;
      }

      JObject content = update["content"] as JObject;

      if (content == null || (string)content["type"] != "text") {
        return;
      }

      string lastLine = null;

      lock (_AnswerSyncRoot) {

        // updates outside of a prompt (for example the history replay of 'session/load') are ignored
        if (_CurrentAnswer != null) {
          _CurrentAnswer.Append((string)content["text"]);
          lastLine = GetLastNonEmptyLine(_CurrentAnswer.ToString());
        }

      }

      if (lastLine != null) {
        this.ReportProgress(lastLine, false);
      }

    }

    /// <summary>
    /// Forwards a status text to the progress delegate of the current turn (throttled unless 'force' is true).
    /// </summary>
    private void ReportProgress(string statusText, bool force) {

      Action<string, int, int> progress;

      lock (_AnswerSyncRoot) {

        progress = _CurrentProgress;

        if (progress == null) {
          return;
        }

        DateTime now = DateTime.UtcNow;

        if (!force && now - _LastProgressUtc < _ProgressThrottle) {
          return;
        }

        _LastProgressUtc = now;
      }

      try {
        progress.Invoke(statusText, 0, 0);
      }
      catch (Exception) {
        // progress is purely informative and must never break the protocol handling
      }

    }

    /// <summary>
    /// Returns the last non-empty line of the given text (max. 200 characters), or null.
    /// </summary>
    public static string GetLastNonEmptyLine(string text) {

      if (string.IsNullOrEmpty(text)) {
        return null;
      }

      string[] lines = text.Split('\n');

      for (int i = lines.Length - 1; i >= 0; i--) {

        string line = lines[i].Trim();

        if (line.Length > 0) {

          if (line.Length > 200) {
            line = line.Substring(line.Length - 200);
          }

          return line;
        }

      }

      return null;
    }

    private void HandleAgentRequest(JToken idToken, string method) {

      JObject response = new JObject();
      response["jsonrpc"] = "2.0";
      response["id"] = idToken.DeepClone();

      if (method == "session/request_permission") {

        // pure chat mode: the inner harness must never use its own tools
        JObject outcome = new JObject();
        outcome["outcome"] = "cancelled";

        JObject result = new JObject();
        result["outcome"] = outcome;

        response["result"] = result;
      }
      else {

        JObject error = new JObject();
        error["code"] = _MethodNotFoundErrorCode;
        error["message"] = $"Method '{method}' is not supported by this client.";

        response["error"] = error;
      }

      this.TryWriteMessage(response);

    }

    private void Close(string reason) {

      List<TaskCompletionSource<JToken>> pending;

      lock (_PendingRequests) {

        if (_IsClosed) {
          return;
        }

        _IsClosed = true;
        _CloseReason = reason;

        pending = new List<TaskCompletionSource<JToken>>(_PendingRequests.Values);
        _PendingRequests.Clear();
      }

      string message = this.BuildClosedMessage();

      foreach (TaskCompletionSource<JToken> completion in pending) {
        completion.TrySetException(new IOException(message));
      }

    }

    private string BuildClosedMessage() {

      string message = _CloseReason;

      if (string.IsNullOrWhiteSpace(message)) {
        message = "The connection to the ACP agent is closed.";
      }

      if (_DiagnosticsProvider != null) {

        string diagnostics = _DiagnosticsProvider.Invoke();

        if (!string.IsNullOrWhiteSpace(diagnostics)) {
          message = message + " " + diagnostics.Trim();
        }

      }

      return message;
    }

    #endregion

  }

  /// <summary>
  /// JSON-RPC error which was returned by the ACP agent.
  /// </summary>
  public class GitHubCopilotAcpException : Exception {

    private readonly int _Code;

    public GitHubCopilotAcpException(int code, string message) : base(message) {
      _Code = code;
    }

    /// <summary>
    /// The JSON-RPC error code.
    /// </summary>
    public int Code {
      get {
        return _Code;
      }
    }

    /// <summary>
    /// True if the error indicates missing or invalid authentication.
    /// </summary>
    public bool IsAuthenticationError {
      get {
        return (_Code == GitHubCopilotAcpSession.AuthRequiredErrorCode || GitHubCopilotCliConnector.IsAuthenticationError(this.Message));
      }
    }

  }

}
