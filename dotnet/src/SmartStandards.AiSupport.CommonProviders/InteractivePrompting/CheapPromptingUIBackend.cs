using AI.SmartStandards.InteractivePrompting.Harness;
using AI.SmartStandards.LowLevelPrompting;
using Logging.SmartStandards.CopyForAI.SmartStandards;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Provider-neutral middle layer between a chat UI and an 'ICommonLLM'. It persists the full chat history
  /// via an 'IPromptingSessionStore' and builds the LLM context (using the opaque 'contextStore' of the provider
  /// if available, otherwise a self-maintained context consisting of an optional compressed baseline plus the
  /// subsequent messages).
  /// </summary>
  public class CheapPromptingUIBackend : IPromptingUIBackend {

    public const string DefaultChatName = "New chat";

    private const int _AutoChatNameMaxLength = 40;

    private const string _CompressionPrompt = (
      "Summarize the complete conversation so far into a compact but complete context summary. " +
      "Keep all facts, decisions, open questions, names, numbers and the language of the user. " +
      "The summary will replace the conversation history for all further turns. " +
      "Answer ONLY with the summary."
    );

    private const string _BaselineIntroduction = "Summary of the previous conversation (context baseline):";

    /// <summary>
    /// Maximum number of tool round trips per user message (protection against endless tool loops).
    /// </summary>
    public const int MaxToolRoundTrips = 5;

    private const string _ToolLoopLimitAnswer = "(The tool loop was aborted after the maximum number of tool calls.)";

    private readonly HarnessToolbox _Toolbox;

    private readonly IPromptingSessionStore _Store;

    private readonly IChatLLMFactory _LLMFactory;

    private readonly Func<string> _AssistantNameGetter;

    private readonly string _UserName;

    private readonly object _SyncRoot = new object();

    private Action _OnNewChatStarting;

    private readonly Dictionary<string, PendingApprovalEntry> _PendingApprovals = new Dictionary<string, PendingApprovalEntry>();

    private TimeSpan _ToolApprovalTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum time to wait for the confirmation of a tool call by the user (default: 5 minutes).
    /// Unanswered requests are denied (the model receives an error result).
    /// </summary>
    public TimeSpan ToolApprovalTimeout {
      get {
        return _ToolApprovalTimeout;
      }
      set {
        _ToolApprovalTimeout = value;
      }
    }

    /// <summary>
    /// Optional hook which is invoked once on every new chat start (before the chat is created), for example
    /// to refresh the offline copies of skill sources with 'secure availability'. Exceptions are logged and ignored,
    /// so that a chat can always be started.
    /// </summary>
    public Action OnNewChatStarting {
      get {
        return _OnNewChatStarting;
      }
      set {
        _OnNewChatStarting = value;
      }
    }

    /// <summary>
    /// Creates the backend.
    /// </summary>
    /// <param name="store">Persistence for the chat sessions.</param>
    /// <param name="llmFactory">Is called for every LLM interaction (allows to change the settings at runtime).</param>
    /// <param name="assistantNameGetter">Returns the author name for LLM answers (optional).</param>
    /// <param name="userName">Author name for user messages (optional, default: current user).</param>
    public CheapPromptingUIBackend(
      IPromptingSessionStore store, Func<ICommonLLM> llmFactory,
      Func<string> assistantNameGetter = null, string userName = null
    ) : this(store, llmFactory, null, assistantNameGetter, userName) {
    }

    /// <summary>
    /// Creates the backend with a harness toolbox, which provides skills (for example the mandatory
    /// 'mustread-user-instructions') and in-process tools to the model via the hidden harness prompt.
    /// </summary>
    /// <param name="store">Persistence for the chat sessions.</param>
    /// <param name="llmFactory">Is called for every LLM interaction (allows to change the settings at runtime).</param>
    /// <param name="toolbox">Skills and tools for the model (optional).</param>
    /// <param name="assistantNameGetter">Returns the author name for LLM answers (optional).</param>
    /// <param name="userName">Author name for user messages (optional, default: current user).</param>
    public CheapPromptingUIBackend(
      IPromptingSessionStore store, Func<ICommonLLM> llmFactory, HarnessToolbox toolbox,
      Func<string> assistantNameGetter = null, string userName = null
    ) : this(store, CreateDelegateFactory(llmFactory), toolbox, assistantNameGetter, userName) {
    }

    /// <summary>
    /// Creates the backend with a provider/model aware LLM factory, so that every chat can keep its own
    /// provider and model (snapshot of the defaults at chat creation, changeable via 'SetChatProvider').
    /// </summary>
    /// <param name="store">Persistence for the chat sessions.</param>
    /// <param name="llmFactory">Creates the LLM per provider/model (is called for every LLM interaction).</param>
    /// <param name="toolbox">Skills and tools for the model (optional).</param>
    /// <param name="assistantNameGetter">
    /// Returns the author name for LLM answers of chats without own provider (optional). Chats with own provider
    /// use '{provider} {model}' as author name.
    /// </param>
    /// <param name="userName">Author name for user messages (optional, default: current user).</param>
    public CheapPromptingUIBackend(
      IPromptingSessionStore store, IChatLLMFactory llmFactory, HarnessToolbox toolbox,
      Func<string> assistantNameGetter = null, string userName = null
    ) {

      if (store == null) {
        throw new ArgumentNullException(nameof(store));
      }

      if (llmFactory == null) {
        throw new ArgumentNullException(nameof(llmFactory));
      }

      _Store = store;
      _LLMFactory = llmFactory;
      _Toolbox = toolbox;
      _AssistantNameGetter = assistantNameGetter;

      if (string.IsNullOrWhiteSpace(userName)) {
        _UserName = Environment.UserName;
      }
      else {
        _UserName = userName;
      }

    }

    #region " Chat management "

    /// <summary>
    /// Returns all known chats, ordered by their last action (most recent first).
    /// </summary>
    public ChatNameAndId[] GetRecentChats() {

      return _Store.GetSessionHeaders().OrderByDescending((h) => h.LastAction).ToArray();
    }

    /// <summary>
    /// Creates a new (empty) chat and returns its id.
    /// </summary>
    public string BeginNewChat(string name) {

      if (_OnNewChatStarting != null) {

        try {
          _OnNewChatStarting.Invoke();
        }
        catch (Exception ex) {
          DevLogger.LogError(ex);
        }

      }

      ChatSession session = new ChatSession();
      session.Id = Guid.NewGuid().ToString("N");
      session.LastAction = DateTime.UtcNow;

      if (string.IsNullOrWhiteSpace(name)) {
        session.Name = DefaultChatName;
      }
      else {
        session.Name = name.Trim();
      }

      //snapshot of the default provider/model (kept per chat from now on)
      session.ProviderName = _LLMFactory.GetDefaultProviderName();

      if (!string.IsNullOrWhiteSpace(session.ProviderName)) {
        session.Model = _LLMFactory.GetDefaultModel(session.ProviderName);
      }

      lock (_SyncRoot) {
        _Store.SaveSession(session);
      }

      return session.Id;
    }

    /// <summary>
    /// Returns true if a chat with the given id exists.
    /// </summary>
    public bool ChatIdExists(string chatId) {

      return _Store.SessionExists(chatId);
    }

    /// <summary>
    /// Deletes the given chat.
    /// </summary>
    public void DeleteChat(string chatId) {

      lock (_SyncRoot) {
        _Store.DeleteSession(chatId);
      }

    }

    /// <summary>
    /// Renames the given chat.
    /// </summary>
    public void RenameChat(string chatId, string newName) {

      if (string.IsNullOrWhiteSpace(newName)) {
        throw new ArgumentNullException(nameof(newName));
      }

      lock (_SyncRoot) {
        ChatSession session = this.LoadSessionOrThrow(chatId);
        session.Name = newName.Trim();
        _Store.SaveSession(session);
      }

    }

    /// <summary>
    /// Returns the details of the given chat (name, effective provider/model, compression state).
    /// </summary>
    public ChatDetails GetChatDetails(string chatId) {

      ChatSession session = this.LoadSessionOrThrow(chatId);

      ChatDetails details = new ChatDetails();
      details.Id = session.Id;
      details.Name = session.Name;
      details.ProviderName = this.GetEffectiveProviderName(session);
      details.Model = this.GetEffectiveModel(session);
      details.MessageCount = session.Messages.Length;
      details.IsCompressed = !string.IsNullOrWhiteSpace(session.ContextBaseline);
      details.LastAction = session.LastAction;
      details.SkillSources = this.BuildSourceStates(session, false);
      details.ToolSources = this.BuildSourceStates(session, true);

      return details;
    }

    /// <summary>
    /// Raises the level of a skill source for the given chat (one-way) and pushes a control message.
    /// </summary>
    public void RaiseChatSkillSourceLevel(string chatId, string sourceId, string level) {

      HarnessSource source = this.GetSourceOrThrow(sourceId, false);

      if (HarnessToolbox.GetSkillLevelRank(level) < 0) {
        throw new ArgumentException($"Unknown skill source level '{level}'.", nameof(level));
      }

      if (source.IsFixed) {
        throw new InvalidOperationException($"The level of the skill source '{source.SourceId}' cannot be changed.");
      }

      lock (_SyncRoot) {

        ChatSession session = this.LoadSessionOrThrow(chatId);
        string currentLevel = _Toolbox.GetEffectiveSkillSourceLevel(source.SourceId, CreateScope(session));

        if (HarnessToolbox.GetSkillLevelRank(level) <= HarnessToolbox.GetSkillLevelRank(currentLevel)) {
          //one-way: loaded content cannot be removed from a context
          return;
        }

        session.SkillLevelOverrides[source.SourceId] = NormalizeSkillLevel(level);

        this.AppendControlMessage(
          session,
          $"Skill source '{source.DisplayName}' is now {DescribeSkillLevel(level)}."
        );

        _Store.SaveSession(session);
      }

    }

    /// <summary>
    /// Sets the level of a tool source for the given chat and pushes a control message.
    /// </summary>
    public void SetChatToolSourceLevel(string chatId, string sourceId, string level) {

      HarnessSource source = this.GetSourceOrThrow(sourceId, true);

      if (HarnessToolbox.GetToolLevelRank(level) < 0) {
        throw new ArgumentException($"Unknown tool source level '{level}'.", nameof(level));
      }

      if (source.IsFixed) {
        throw new InvalidOperationException($"The level of the tool source '{source.SourceId}' cannot be changed.");
      }

      lock (_SyncRoot) {

        ChatSession session = this.LoadSessionOrThrow(chatId);
        string currentLevel = _Toolbox.GetEffectiveToolSourceLevel(source.SourceId, CreateScope(session));

        //an explicit level change always revokes an 'AllowForChat' approval of the source
        bool wasApprovedForChat = session.ChatApprovals.Contains(source.SourceId, StringComparer.OrdinalIgnoreCase);
        session.ChatApprovals = session.ChatApprovals
          .Where((id) => !string.Equals(id, source.SourceId, StringComparison.OrdinalIgnoreCase))
          .ToArray();

        if (HarnessToolbox.GetToolLevelRank(level) == HarnessToolbox.GetToolLevelRank(currentLevel)) {

          if (wasApprovedForChat) {
            _Store.SaveSession(session);
          }

          return;
        }

        session.ToolLevelOverrides[source.SourceId] = NormalizeToolLevel(level);

        this.AppendControlMessage(
          session,
          $"Tool source '{source.DisplayName}' is now {DescribeToolLevel(level)}."
        );

        _Store.SaveSession(session);
      }

    }

    /// <summary>
    /// Switches the provider and/or model of the given chat. A provider switch discards the provider-side
    /// context ('ContextStore'), so that the next turn rebuilds it from the persisted history, the baseline
    /// and the hidden harness prompt. An empty 'model' selects the model configured for the provider.
    /// </summary>
    public void SetChatProvider(string chatId, string providerName, string model) {

      if (string.IsNullOrWhiteSpace(providerName)) {
        throw new ArgumentNullException(nameof(providerName));
      }

      providerName = providerName.Trim();

      if (!_LLMFactory.IsKnownProvider(providerName)) {
        throw new ArgumentException($"The LLM provider '{providerName}' is not available.", nameof(providerName));
      }

      if (string.IsNullOrWhiteSpace(model)) {
        model = _LLMFactory.GetDefaultModel(providerName);
      }
      else {
        model = model.Trim();
      }

      lock (_SyncRoot) {

        ChatSession session = this.LoadSessionOrThrow(chatId);

        bool providerChanged = !string.Equals(
          this.GetEffectiveProviderName(session), providerName, StringComparison.OrdinalIgnoreCase
        );

        session.ProviderName = providerName;
        session.Model = model;

        if (providerChanged) {
          //the opaque value of the old provider is meaningless for the new one
          ResetProviderContext(session);
        }

        _Store.SaveSession(session);
      }

    }

    #endregion

    #region " Message access "

    /// <summary>
    /// Returns the number of messages within the given chat.
    /// </summary>
    public int GetChatMessageCount(string chatId) {

      ChatSession session = this.LoadSessionOrThrow(chatId);

      return session.Messages.Length;
    }

    /// <summary>
    /// Returns the messages of the given chat within the (inclusive) index range.
    /// </summary>
    public ChatMessage[] GetChatMessages(string chatId, int startIndex, int endIndex) {

      ChatSession session = this.LoadSessionOrThrow(chatId);

      if (startIndex < 0) {
        startIndex = 0;
      }

      if (endIndex >= session.Messages.Length) {
        endIndex = session.Messages.Length - 1;
      }

      if (endIndex < startIndex) {
        return new ChatMessage[0];
      }

      return session.Messages.Skip(startIndex).Take(endIndex - startIndex + 1).ToArray();
    }

    /// <summary>
    /// Returns the messages of the given chat within the (inclusive) time range.
    /// </summary>
    public ChatMessage[] GetChatMessagesByTime(string chatId, DateTime from, DateTime to) {

      ChatSession session = this.LoadSessionOrThrow(chatId);

      return session.Messages.Where((m) => m.LastAction >= from && m.LastAction <= to).ToArray();
    }

    #endregion

    #region " LLM interaction "

    /// <summary>
    /// Returns the tool calls of the given chat which are currently waiting for a confirmation by the user.
    /// </summary>
    public PendingToolApproval[] GetPendingToolApprovals(string chatId) {

      lock (_PendingApprovals) {

        return _PendingApprovals.Values
          .Where((e) => string.Equals(e.ChatId, chatId, StringComparison.Ordinal))
          .Select((e) => e.Info)
          .OrderBy((i) => i.RequestedUtc)
          .ToArray();
      }

    }

    /// <summary>
    /// Answers a pending tool call with one of the 'ToolApprovalDecisions' ('Deny', 'AllowOnce', 'AllowForChat').
    /// Unknown or already answered approval ids are ignored, unknown decisions are treated as 'Deny'.
    /// </summary>
    public void AnswerToolApproval(string chatId, string approvalId, string decision) {

      if (string.IsNullOrWhiteSpace(approvalId)) {
        return;
      }

      PendingApprovalEntry entry;

      lock (_PendingApprovals) {

        if (!_PendingApprovals.TryGetValue(approvalId, out entry)) {
          return;
        }

        //the chat id must match, so that an approval id cannot be answered via another chat
        if (!string.Equals(entry.ChatId, chatId, StringComparison.Ordinal)) {
          return;
        }

        _PendingApprovals.Remove(approvalId);
      }

      entry.Decision = NormalizeApprovalDecision(decision);
      entry.Signal.Set();

    }

    /// <summary>
    /// Returns the audit log of all tool calls which were requested within the given chat (chronological).
    /// </summary>
    public ToolAuditEntry[] GetToolAuditLog(string chatId) {

      ChatSession session = this.LoadSessionOrThrow(chatId);

      return session.ToolAuditLog;
    }

    /// <summary>
    /// Maximum number of entries which are kept within the audit log of a chat (oldest entries are dropped).
    /// </summary>
    public const int MaxToolAuditEntries = 500;

    private const int _MaxArgumentSummaryLength = 300;

    private static readonly string[] _SensitiveArgumentNameParts = new string[] {
      "password", "passwd", "secret", "token", "apikey", "api_key", "credential", "authorization"
    };

    private static string NormalizeApprovalDecision(string decision) {

      if (string.Equals(decision, ToolApprovalDecisions.AllowOnce, StringComparison.OrdinalIgnoreCase)) {
        return ToolApprovalDecisions.AllowOnce;
      }

      if (string.Equals(decision, ToolApprovalDecisions.AllowForChat, StringComparison.OrdinalIgnoreCase)) {
        return ToolApprovalDecisions.AllowForChat;
      }

      if (string.Equals(decision, ToolApprovalDecisions.AllowPermanently, StringComparison.OrdinalIgnoreCase)) {
        return ToolApprovalDecisions.AllowPermanently;
      }

      return ToolApprovalDecisions.Deny;
    }

    /// <summary>
    /// Creates the harness scope for a running turn: tools of sources with level 'RequiresConfirmation'
    /// are executed directly if the source was approved with 'AllowForChat', otherwise they are published
    /// as pending approval and the tool loop waits until the user answers (or the timeout, or a cancellation
    /// occurs - both deny the call). Every call is recorded within the audit log of the chat.
    /// </summary>
    private HarnessScope CreateInteractiveScope(
      ChatSession session, CancellationToken cancellationToken, Action<string, int, int> onProgress
    ) {

      string chatId = session.Id;
      IDictionary<string, string> toolLevelOverrides = session.ToolLevelOverrides;

      //decisions of the current turn (per call), to be written into the audit log
      Dictionary<HarnessToolCall, string> decisions = new Dictionary<HarnessToolCall, string>();

      Func<HarnessToolCall, HarnessSource, bool> approver = (call, source) => {

        string decision;

        if (this.IsSourceApprovedForChat(chatId, source.SourceId)) {
          decision = ToolApprovalDecisions.Preapproved;
        }
        else {
          decision = this.WaitForToolApproval(chatId, call, source, cancellationToken, onProgress);

          if (decision == null) {
            decision = ToolApprovalDecisions.Deny;
          }
          else {
            this.ApplyApprovalDecision(chatId, source, decision, toolLevelOverrides);
          }

        }

        lock (decisions) {
          decisions[call] = decision;
        }

        return (decision != ToolApprovalDecisions.Deny);
      };

      Action<HarnessToolExecution> onToolExecuted = (execution) => {

        string decision = null;

        lock (decisions) {
          decisions.TryGetValue(execution.Call, out decision);
        }

        this.AppendToolAuditEntry(chatId, execution, decision);
      };

      HarnessScope scope = new HarnessScope(session.SkillLevelOverrides, toolLevelOverrides, approver, onToolExecuted);

      //guard requests (second line of defense) use the same pause mechanics as tool approvals
      scope.InteractionRequester = (request) => {

        string decision = this.WaitForInteraction(
          chatId, request.Call, request.Source, request.Kind, request.Subject, cancellationToken, onProgress
        );

        if (decision == null) {
          return ToolApprovalDecisions.Deny;
        }

        return decision;
      };

      return scope;
    }

    private bool IsSourceApprovedForChat(string chatId, string sourceId) {

      ChatSession session = _Store.LoadSession(chatId);

      if (session == null) {
        return false;
      }

      return session.ChatApprovals.Contains(sourceId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Persists the consequences of an interactive decision: 'AllowForChat' remembers the source as approved,
    /// 'Deny' hides the source for this chat (also within the running turn) and informs the model.
    /// </summary>
    private void ApplyApprovalDecision(
      string chatId, HarnessSource source, string decision, IDictionary<string, string> runningTurnToolLevelOverrides
    ) {

      if (decision == ToolApprovalDecisions.AllowOnce) {
        return;
      }

      if (decision == ToolApprovalDecisions.Deny) {
        //(further calls of the running turn must not ask again)
        runningTurnToolLevelOverrides[source.SourceId] = ToolSourceLevels.Hidden;
      }

      lock (_SyncRoot) {

        ChatSession session = _Store.LoadSession(chatId);

        if (session == null) {
          return;
        }

        if (decision == ToolApprovalDecisions.AllowForChat) {

          if (!session.ChatApprovals.Contains(source.SourceId, StringComparer.OrdinalIgnoreCase)) {
            session.ChatApprovals = session.ChatApprovals.Concat(new string[] { source.SourceId }).ToArray();
          }

        }
        else {

          session.ToolLevelOverrides[source.SourceId] = ToolSourceLevels.Hidden;
          session.ChatApprovals = session.ChatApprovals
            .Where((id) => !string.Equals(id, source.SourceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

          this.AppendControlMessage(
            session,
            $"The user denied a tool call - tool source '{source.DisplayName}' is now {DescribeToolLevel(ToolSourceLevels.Hidden)}."
          );

        }

        _Store.SaveSession(session);
      }

    }

    /// <summary>
    /// Appends an entry to the audit log of the chat (secret-free argument summary, no result payload).
    /// </summary>
    private void AppendToolAuditEntry(string chatId, HarnessToolExecution execution, string decision) {

      ToolAuditEntry entry = new ToolAuditEntry();
      entry.TimestampUtc = DateTime.UtcNow;
      entry.ToolName = execution.Call.Name;
      entry.ToolSourceId = execution.SourceId;
      entry.ArgumentSummary = BuildArgumentSummary(execution.Call);
      entry.DurationMs = execution.DurationMs;
      entry.Success = execution.Success;
      entry.ErrorMessage = execution.ErrorMessage;
      entry.ResultSize = execution.ResultSize;

      if (decision != null) {
        entry.Decision = decision;
      }
      else if (execution.Executed) {
        entry.Decision = ToolApprovalDecisions.Preapproved;
      }
      else if (execution.RequiredConfirmation) {
        entry.Decision = ToolApprovalDecisions.Deny;
      }
      else {
        entry.Decision = ToolApprovalDecisions.NotAvailable;
      }

      lock (_SyncRoot) {

        ChatSession session = _Store.LoadSession(chatId);

        if (session == null) {
          return;
        }

        List<ToolAuditEntry> log = new List<ToolAuditEntry>(session.ToolAuditLog);
        log.Add(entry);

        if (log.Count > MaxToolAuditEntries) {
          log.RemoveRange(0, log.Count - MaxToolAuditEntries);
        }

        session.ToolAuditLog = log.ToArray();
        _Store.SaveSession(session);
      }

    }

    /// <summary>
    /// Builds a shortened argument summary, in which values of sensitive looking properties are masked.
    /// </summary>
    public static string BuildArgumentSummary(HarnessToolCall call) {

      if (call.Arguments == null) {
        return "{}";
      }

      Newtonsoft.Json.Linq.JObject masked = (Newtonsoft.Json.Linq.JObject)call.Arguments.DeepClone();
      MaskSensitiveProperties(masked);

      string summary = masked.ToString(Newtonsoft.Json.Formatting.None);

      if (summary.Length > _MaxArgumentSummaryLength) {
        summary = summary.Substring(0, _MaxArgumentSummaryLength) + "...";
      }

      return summary;
    }

    private static void MaskSensitiveProperties(Newtonsoft.Json.Linq.JToken token) {

      Newtonsoft.Json.Linq.JObject obj = token as Newtonsoft.Json.Linq.JObject;

      if (obj != null) {

        foreach (Newtonsoft.Json.Linq.JProperty property in obj.Properties().ToArray()) {

          string lowerName = property.Name.ToLowerInvariant();

          if (_SensitiveArgumentNameParts.Any((part) => lowerName.Contains(part))) {
            property.Value = "***";
          }
          else {
            MaskSensitiveProperties(property.Value);
          }

        }

        return;
      }

      Newtonsoft.Json.Linq.JArray array = token as Newtonsoft.Json.Linq.JArray;

      if (array != null) {

        foreach (Newtonsoft.Json.Linq.JToken item in array) {
          MaskSensitiveProperties(item);
        }

      }

    }

    /// <summary>
    /// Publishes a pending approval and waits for the answer of the user. Returns the decision
    /// or null if the request was not answered within the timeout.
    /// </summary>
    private string WaitForToolApproval(
      string chatId, HarnessToolCall call, HarnessSource source, CancellationToken cancellationToken,
      Action<string, int, int> onProgress
    ) {

      return this.WaitForInteraction(
        chatId, call, source, PendingInteractionKinds.ToolApproval, null, cancellationToken, onProgress
      );
    }

    /// <summary>
    /// Publishes a pending request (tool approval or guard request) and waits for the answer of the user.
    /// Returns the decision or null if the request was not answered within the timeout.
    /// 'AllowPermanently' is only accepted for guard requests (otherwise it is treated as 'AllowOnce').
    /// </summary>
    private string WaitForInteraction(
      string chatId, HarnessToolCall call, HarnessSource source, string kind, string subject,
      CancellationToken cancellationToken, Action<string, int, int> onProgress
    ) {

      PendingToolApproval info = new PendingToolApproval();
      info.ApprovalId = Guid.NewGuid().ToString("N");
      info.Kind = kind;
      info.Subject = subject;
      info.ToolName = call.Name;
      info.RequestedUtc = DateTime.UtcNow;
      info.ExpiresUtc = info.RequestedUtc.Add(_ToolApprovalTimeout);

      if (source != null) {
        info.SourceId = source.SourceId;
        info.SourceDisplayName = source.DisplayName;
      }

      if (call.Arguments != null) {
        info.ArgumentsJson = call.Arguments.ToString(Newtonsoft.Json.Formatting.Indented);
      }
      else {
        info.ArgumentsJson = "{}";
      }

      PendingApprovalEntry entry = new PendingApprovalEntry(chatId, info);

      lock (_PendingApprovals) {
        _PendingApprovals[info.ApprovalId] = entry;
      }

      ReportProgress(onProgress, "waiting for confirmation of tool " + call.Name, 0, 0);

      try {

        bool answered = entry.Signal.Wait(_ToolApprovalTimeout, cancellationToken);

        if (!answered) {
          //(an unanswered request is denied, but the source is not hidden)
          return null;
        }

        bool isToolApproval = string.Equals(kind, PendingInteractionKinds.ToolApproval, StringComparison.Ordinal);

        if (isToolApproval && entry.Decision == ToolApprovalDecisions.AllowPermanently) {
          return ToolApprovalDecisions.AllowOnce;
        }

        if (!isToolApproval && entry.Decision == ToolApprovalDecisions.AllowForChat) {
          //(guard decisions are global, a per-chat decision is not supported for them)
          return ToolApprovalDecisions.AllowOnce;
        }

        return entry.Decision;
      }
      finally {

        lock (_PendingApprovals) {
          _PendingApprovals.Remove(info.ApprovalId);
        }

      }

    }

    /// <summary>
    /// A tool call waiting for the confirmation of the user.
    /// </summary>
    private sealed class PendingApprovalEntry {

      private readonly string _ChatId;

      private readonly PendingToolApproval _Info;

      private readonly ManualResetEventSlim _Signal = new ManualResetEventSlim(false);

      private volatile string _Decision = ToolApprovalDecisions.Deny;

      public PendingApprovalEntry(string chatId, PendingToolApproval info) {
        _ChatId = chatId;
        _Info = info;
      }

      public string ChatId {
        get {
          return _ChatId;
        }
      }

      public PendingToolApproval Info {
        get {
          return _Info;
        }
      }

      public ManualResetEventSlim Signal {
        get {
          return _Signal;
        }
      }

      public string Decision {
        get {
          return _Decision;
        }
        set {
          _Decision = value;
        }
      }

    }

    /// <summary>
    /// Sends a new user message to the LLM and returns the answer (which is also persisted within the chat).
    /// </summary>
    public ChatMessage SendMessage(CancellationToken cancellationToken, string chatId, string message) {

      return this.SendMessage(cancellationToken, chatId, message, null);
    }

    /// <summary>
    /// Sends a new user message to the LLM and returns the answer (which is also persisted within the chat).
    /// Progress (statusText, current, max) is reported via the optional callback 'onProgress'.
    /// </summary>
    public ChatMessage SendMessage(
      CancellationToken cancellationToken, string chatId, string message, Action<string, int, int> onProgress
    ) {

      if (string.IsNullOrWhiteSpace(message)) {
        throw new ArgumentNullException(nameof(message));
      }

      cancellationToken.ThrowIfCancellationRequested();

      //the LLM is created before the user message is persisted (incomplete settings fail early)
      ChatSession sessionForProvider = this.LoadSessionOrThrow(chatId);
      string providerName = sessionForProvider.ProviderName;
      ICommonLLM llm = _LLMFactory.CreateLLM(providerName, sessionForProvider.Model);
      string assistantName = this.GetAssistantName(sessionForProvider);

      ReportProgress(onProgress, "waiting for " + assistantName, 0, 0);

      //persist the user message first (it is part of the history, even if the LLM call fails)
      ChatSession session;

      lock (_SyncRoot) {

        session = this.LoadSessionOrThrow(chatId);

        ChatMessage userMessage = this.CreateMessage(ChatRoles.User, _UserName, message);
        session.Messages = session.Messages.Concat(new ChatMessage[] { userMessage }).ToArray();
        session.LastAction = userMessage.LastAction;

        if (session.Name == DefaultChatName) {
          session.Name = BuildAutoChatName(message);
        }

        _Store.SaveSession(session);
      }

      string contextStore = session.ContextStore;
      string answer = null;
      List<string> activities = new List<string>();

      //the hidden harness prompt (pinned skills, skill catalog, tools) is evaluated for every turn,
      //so that changed user instructions are effective immediately
      string harnessPrompt = null;
      string harnessFingerprint = null;
      HarnessScope scope = this.CreateInteractiveScope(session, cancellationToken, onProgress);
      int knownMessageCount = session.Messages.Length;

      if (_Toolbox != null) {
        harnessPrompt = _Toolbox.BuildHarnessPrompt(scope);
        harnessFingerprint = _Toolbox.BuildHarnessPromptFingerprint(scope);
      }

      if (contextStore != null) {

        List<ChatTurn> newTurns = new List<ChatTurn>();

        //the provider-side context does not yet know the current harness prompt
        if (harnessPrompt != null && harnessFingerprint != session.HarnessPromptFingerprint) {
          newTurns.Add(new ChatTurn(ChatRoles.System, harnessPrompt));
        }

        newTurns.AddRange(BuildTurns(session.Messages.Skip(session.ContextStoreCoveredMessageCount)));

        try {
          answer = this.InvokeWithToolLoop(llm, newTurns.ToArray(), ref contextStore, scope, cancellationToken, onProgress, activities);
        }
        catch (ContextStoreInvalidException) {
          //the provider-side context has expired: retry once using the self-maintained context
          contextStore = null;
          ReportProgress(onProgress, "provider context expired - rebuilding it from the chat history", 0, 0);
        }

      }

      if (contextStore == null && answer == null) {
        ChatTurn[] allTurns = BuildSelfMaintainedContext(session, harnessPrompt);
        answer = this.InvokeWithToolLoop(llm, allTurns, ref contextStore, scope, cancellationToken, onProgress, activities);
      }

      ChatMessage assistantMessage = this.CreateMessage(ChatRoles.Assistant, assistantName, answer);

      List<ChatMessage> activityMessages = new List<ChatMessage>();

      foreach (string activity in activities) {
        ChatMessage activityMessage = this.CreateMessage(ChatRoles.Assistant, "Harness", activity);
        activityMessage.Kind = ChatMessageKinds.Activity;
        activityMessages.Add(activityMessage);
      }

      lock (_SyncRoot) {

        //reload, because the session could have been renamed in the meantime
        ChatSession current = this.LoadSessionOrThrow(chatId);

        //control messages which were pushed while the turn was running are placed behind the answer,
        //so that they reach the model in front of the next user message
        int insertIndex = Math.Min(knownMessageCount, current.Messages.Length);
        List<ChatMessage> messages = new List<ChatMessage>(current.Messages);
        messages.InsertRange(insertIndex, activityMessages);
        insertIndex = insertIndex + activityMessages.Count;
        messages.Insert(insertIndex, assistantMessage);
        current.Messages = messages.ToArray();
        current.LastAction = assistantMessage.LastAction;

        bool providerChangedMeanwhile = !string.Equals(
          current.ProviderName, providerName, StringComparison.OrdinalIgnoreCase
        );

        if (contextStore == null || providerChangedMeanwhile) {
          //(a context store of the old provider must not be kept after a provider switch during the turn)
          ResetProviderContext(current);
        }
        else {
          current.ContextStore = contextStore;
          current.ContextStoreCoveredMessageCount = insertIndex + 1;
          current.HarnessPromptFingerprint = harnessFingerprint;
        }

        _Store.SaveSession(current);
      }

      return assistantMessage;
    }

    /// <summary>
    /// Lets the LLM compress the self-maintained context of the chat into a new baseline.
    /// The full message history is NOT affected. Any provider-side context is discarded, so that
    /// subsequent turns start from the (smaller) baseline.
    /// </summary>
    public void CompressChatContext(CancellationToken cancellationToken, string chatId) {

      cancellationToken.ThrowIfCancellationRequested();

      ChatSession session = this.LoadSessionOrThrow(chatId);

      if (session.Messages.Length == 0) {
        return;
      }

      ICommonLLM llm = _LLMFactory.CreateLLM(session.ProviderName, session.Model);

      List<ChatTurn> turns = new List<ChatTurn>(BuildSelfMaintainedContext(session));
      turns.Add(new ChatTurn(ChatRoles.User, _CompressionPrompt));

      string noContextStore = null;
      string summary = InvokeChatApi(llm, turns.ToArray(), ref noContextStore, cancellationToken, null);

      lock (_SyncRoot) {

        ChatSession current = this.LoadSessionOrThrow(chatId);
        current.ContextBaseline = summary;
        current.ContextBaselineCoveredMessageCount = session.Messages.Length;

        //the provider-side context would still contain the full history
        ResetProviderContext(current);

        _Store.SaveSession(current);
      }

    }

    /// <summary>
    /// Builds the self-maintained context: the baseline (if present) followed by all subsequent messages,
    /// otherwise the full history.
    /// </summary>
    internal static ChatTurn[] BuildSelfMaintainedContext(ChatSession session) {

      return BuildSelfMaintainedContext(session, null);
    }

    /// <summary>
    /// Builds the self-maintained context, starting with the hidden harness prompt (if not null).
    /// </summary>
    internal static ChatTurn[] BuildSelfMaintainedContext(ChatSession session, string harnessPrompt) {

      List<ChatTurn> turns = new List<ChatTurn>();

      if (harnessPrompt != null) {
        turns.Add(new ChatTurn(ChatRoles.System, harnessPrompt));
      }

      if (string.IsNullOrWhiteSpace(session.ContextBaseline)) {
        turns.AddRange(BuildTurns(session.Messages));
      }
      else {
        turns.Add(new ChatTurn(ChatRoles.System, _BaselineIntroduction + Environment.NewLine + session.ContextBaseline));
        turns.AddRange(BuildTurns(session.Messages.Skip(session.ContextBaselineCoveredMessageCount)));
      }

      return turns.ToArray();
    }

    private static ChatTurn[] BuildTurns(IEnumerable<ChatMessage> messages) {

      //activity notes are presentation-only (the model already knows its own tool calls)
      return messages.Where(
        (m) => !string.Equals(m.Kind, ChatMessageKinds.Activity, StringComparison.Ordinal)
      ).Select(
        (m) => new ChatTurn(m.Role, m.Content)
      ).ToArray();
    }

    /// <summary>
    /// Invokes the LLM and executes requested harness tools (text protocol) until the model answers without
    /// tool calls. The intermediate tool exchange is not persisted as chat message (it is part of the
    /// provider-side context only, if there is one).
    /// </summary>
    private string InvokeWithToolLoop(
      ICommonLLM llm, ChatTurn[] turns, ref string contextStore, HarnessScope scope, CancellationToken cancellationToken,
      Action<string, int, int> onProgress, List<string> activities
    ) {

      string answer = InvokeChatApi(llm, turns, ref contextStore, cancellationToken, onProgress);

      if (_Toolbox == null) {
        return answer;
      }

      List<ChatTurn> conversation = new List<ChatTurn>(turns);

      for (int roundTrip = 1; roundTrip <= MaxToolRoundTrips; roundTrip++) {

        HarnessToolCall[] calls = HarnessToolbox.ParseToolCalls(answer);

        if (calls.Length == 0) {
          return answer;
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (HarnessToolCall call in calls) {

          string activity = DescribeActivity(call);
          activities.Add(activity);
          ReportProgress(onProgress, ChatMessageKinds.ActivityProgressPrefix + activity, 0, 0);

        }

        if (onProgress != null) {
          onProgress.Invoke("calling tool " + string.Join(", ", calls.Select((c) => c.Name)), 0, 0);
        }

        string toolResponses = _Toolbox.ExecuteToolCalls(calls, scope);
        ChatTurn toolResponseTurn = new ChatTurn(ChatRoles.User, toolResponses);

        conversation.Add(new ChatTurn(ChatRoles.Assistant, answer));
        conversation.Add(toolResponseTurn);

        if (contextStore != null) {
          //the provider already knows the conversation including the tool call
          answer = InvokeChatApi(llm, new ChatTurn[] { toolResponseTurn }, ref contextStore, cancellationToken, onProgress);
        }
        else {
          answer = InvokeChatApi(llm, conversation.ToArray(), ref contextStore, cancellationToken, onProgress);
        }

      }

      string remainingText = HarnessToolbox.RemoveToolCalls(answer);

      if (string.IsNullOrWhiteSpace(remainingText)) {
        return _ToolLoopLimitAnswer;
      }

      return remainingText;
    }

    /// <summary>
    /// Invokes the (synchronous) LLM call on a worker thread, so that a cancellation is effective immediately
    /// (a late answer is discarded). The token and the progress delegate are passed to the provider as well,
    /// so that it can abort its HTTP request or CLI process.
    /// </summary>
    private static string InvokeChatApi(
      ICommonLLM llm, ChatTurn[] turns, ref string contextStore, CancellationToken cancellationToken,
      Action<string, int, int> onProgress
    ) {

      string contextStoreIn = contextStore;
      string contextStoreOut = null;

      Task<string> llmTask = Task.Run(
        () => {
          string localContextStore = contextStoreIn;
          string result = llm.CallChatApi(cancellationToken, turns, ref localContextStore, onProgress);
          contextStoreOut = localContextStore;
          return result;
        }
      );

      try {
        llmTask.Wait(cancellationToken);
      }
      catch (AggregateException ex) {
        if (ex.InnerExceptions.Count == 1) {
          System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
        throw;
      }

      contextStore = contextStoreOut;

      return llmTask.Result;
    }

    /// <summary>
    /// Creates the (user-facing) activity text for a harness tool call.
    /// </summary>
    private static string DescribeActivity(HarnessToolCall call) {

      if (string.Equals(call.Name, HarnessToolbox.LoadSkillToolName, StringComparison.OrdinalIgnoreCase)) {

        string skillName = null;

        if (call.Arguments != null) {
          skillName = (string)call.Arguments["name"];
        }

        if (!string.IsNullOrWhiteSpace(skillName)) {
          return "SKILL '" + skillName + "' loaded";
        }

      }

      return "Tool '" + call.Name + "' called";
    }

    /// <summary>
    /// Invokes the optional progress callback (statusText, current, max).
    /// </summary>
    private static void ReportProgress(Action<string, int, int> onProgress, string statusText, int current, int max) {

      if (onProgress != null) {
        onProgress.Invoke(statusText, current, max);
      }

    }

    #endregion

    #region " Helpers "

    private ChatSession LoadSessionOrThrow(string chatId) {

      ChatSession session = _Store.LoadSession(chatId);

      if (session == null) {
        throw new KeyNotFoundException($"There is no chat with id '{chatId}'.");
      }

      return session;
    }

    private ChatMessage CreateMessage(string role, string author, string content) {

      ChatMessage message = new ChatMessage();
      message.Id = Guid.NewGuid().ToString("N");
      message.Role = role;
      message.Author = author;
      message.Content = content;
      message.LastAction = DateTime.UtcNow;

      return message;
    }

    private string GetAssistantName() {

      if (_AssistantNameGetter == null) {
        return "AI";
      }

      return _AssistantNameGetter.Invoke();
    }

    /// <summary>
    /// Chats with own provider use '{provider} {model}', all others the (legacy) assistant name getter.
    /// </summary>
    private string GetAssistantName(ChatSession session) {

      if (string.IsNullOrWhiteSpace(session.ProviderName)) {
        return this.GetAssistantName();
      }

      string model = this.GetEffectiveModel(session);

      if (string.IsNullOrWhiteSpace(model)) {
        return session.ProviderName;
      }

      return session.ProviderName + " " + model;
    }

    private string GetEffectiveProviderName(ChatSession session) {

      if (!string.IsNullOrWhiteSpace(session.ProviderName)) {
        return session.ProviderName;
      }

      return _LLMFactory.GetDefaultProviderName();
    }

    private string GetEffectiveModel(ChatSession session) {

      if (!string.IsNullOrWhiteSpace(session.Model)) {
        return session.Model;
      }

      string providerName = this.GetEffectiveProviderName(session);

      if (string.IsNullOrWhiteSpace(providerName)) {
        return null;
      }

      return _LLMFactory.GetDefaultModel(providerName);
    }

    /// <summary>
    /// Discards the provider-side context, so that the next turn rebuilds it from the self-maintained context.
    /// </summary>
    private static void ResetProviderContext(ChatSession session) {

      session.ContextStore = null;
      session.ContextStoreCoveredMessageCount = 0;
      session.HarnessPromptFingerprint = null;

    }

    private static IChatLLMFactory CreateDelegateFactory(Func<ICommonLLM> llmFactory) {

      if (llmFactory == null) {
        throw new ArgumentNullException(nameof(llmFactory));
      }

      return new DelegateChatLLMFactory(llmFactory);
    }

    /// <summary>
    /// Creates the harness scope (per-chat source level overrides) of the given chat.
    /// </summary>
    private static HarnessScope CreateScope(ChatSession session) {

      return new HarnessScope(session.SkillLevelOverrides, session.ToolLevelOverrides, null);
    }

    private HarnessSource GetSourceOrThrow(string sourceId, bool isToolSource) {

      HarnessSource source = null;

      if (_Toolbox != null) {
        source = _Toolbox.GetSource(sourceId);
      }

      if (source == null || source.IsToolSource != isToolSource) {
        throw new KeyNotFoundException($"There is no source with id '{sourceId}'.");
      }

      return source;
    }

    private ChatSourceState[] BuildSourceStates(ChatSession session, bool toolSources) {

      if (_Toolbox == null) {
        return new ChatSourceState[0];
      }

      HarnessScope scope = CreateScope(session);
      List<ChatSourceState> states = new List<ChatSourceState>();

      foreach (HarnessSource source in _Toolbox.GetSources()) {

        if (source.IsToolSource != toolSources) {
          continue;
        }

        ChatSourceState state = new ChatSourceState();
        state.SourceId = source.SourceId;
        state.DisplayName = source.DisplayName;
        state.DefaultLevel = source.DefaultLevel;
        state.IsFixed = source.IsFixed;

        if (toolSources) {
          state.EffectiveLevel = _Toolbox.GetEffectiveToolSourceLevel(source.SourceId, scope);
          state.IsApprovedForChat = session.ChatApprovals.Contains(source.SourceId, StringComparer.OrdinalIgnoreCase);
          state.ItemNames = _Toolbox.GetTools()
            .Where((t) => string.Equals(_Toolbox.GetToolSourceId(t.Name), source.SourceId, StringComparison.OrdinalIgnoreCase))
            .Select((t) => t.Name)
            .ToArray();
        }
        else {
          state.EffectiveLevel = _Toolbox.GetEffectiveSkillSourceLevel(source.SourceId, scope);
          state.ItemNames = _Toolbox.GetSkills()
            .Where((s) => string.Equals(_Toolbox.GetSkillSourceId(s.Name), source.SourceId, StringComparison.OrdinalIgnoreCase))
            .Select((s) => s.Name)
            .ToArray();
        }

        states.Add(state);
      }

      return states.ToArray();
    }

    /// <summary>
    /// Appends a control message (ghost bubble in the UI). It does not trigger an LLM answer of its own,
    /// but reaches the model in front of the next user message (as part of the same turn).
    /// </summary>
    private void AppendControlMessage(ChatSession session, string notice) {

      ChatMessage controlMessage = this.CreateMessage(
        ChatRoles.User, "Harness", "<harness_notice>" + notice + "</harness_notice>"
      );
      controlMessage.Kind = ChatMessageKinds.Control;

      session.Messages = session.Messages.Concat(new ChatMessage[] { controlMessage }).ToArray();
      session.LastAction = controlMessage.LastAction;

    }

    private static string NormalizeSkillLevel(string level) {

      int rank = HarnessToolbox.GetSkillLevelRank(level);

      if (rank == 2) {
        return SkillSourceLevels.InitializeAtStart;
      }

      if (rank == 1) {
        return SkillSourceLevels.OnDemand;
      }

      return SkillSourceLevels.Hidden;
    }

    private static string NormalizeToolLevel(string level) {

      int rank = HarnessToolbox.GetToolLevelRank(level);

      if (rank == 2) {
        return ToolSourceLevels.NoConfirmation;
      }

      if (rank == 1) {
        return ToolSourceLevels.RequiresConfirmation;
      }

      return ToolSourceLevels.Hidden;
    }

    private static string DescribeSkillLevel(string level) {

      if (HarnessToolbox.GetSkillLevelRank(level) == 2) {
        return "pinned (its skills are part of your instructions from now on)";
      }

      return "available on demand (load its skills via '" + HarnessToolbox.LoadSkillToolName + "')";
    }

    private static string DescribeToolLevel(string level) {

      int rank = HarnessToolbox.GetToolLevelRank(level);

      if (rank == 2) {
        return "available (no confirmation required)";
      }

      if (rank == 1) {
        return "available (requires confirmation by the user)";
      }

      return "not available anymore - do not call its tools";
    }

    private static string BuildAutoChatName(string firstMessage) {

      string name = firstMessage.Replace("\r", " ").Replace("\n", " ").Trim();

      if (name.Length > _AutoChatNameMaxLength) {
        name = name.Substring(0, _AutoChatNameMaxLength).TrimEnd() + "...";
      }

      return name;
    }

    #endregion

  }

}
