using AI.SmartStandards.InteractivePrompting.Harness;
using AI.SmartStandards.LowLevelPrompting;
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

    private readonly Func<ICommonLLM> _LLMFactory;

    private readonly Func<string> _AssistantNameGetter;

    private readonly string _UserName;

    private readonly object _SyncRoot = new object();

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

      ChatSession session = new ChatSession();
      session.Id = Guid.NewGuid().ToString("N");
      session.LastAction = DateTime.UtcNow;

      if (string.IsNullOrWhiteSpace(name)) {
        session.Name = DefaultChatName;
      }
      else {
        session.Name = name.Trim();
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
    /// Sends a new user message to the LLM and returns the answer (which is also persisted within the chat).
    /// </summary>
    public ChatMessage SendMessage(CancellationToken cancellationToken, string chatId, string message) {

      if (string.IsNullOrWhiteSpace(message)) {
        throw new ArgumentNullException(nameof(message));
      }

      cancellationToken.ThrowIfCancellationRequested();

      ICommonLLM llm = _LLMFactory.Invoke();

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

      //the hidden harness prompt (pinned skills, skill catalog, tools) is evaluated for every turn,
      //so that changed user instructions are effective immediately
      string harnessPrompt = null;
      string harnessFingerprint = null;

      if (_Toolbox != null) {
        harnessPrompt = _Toolbox.BuildHarnessPrompt();
        harnessFingerprint = _Toolbox.BuildHarnessPromptFingerprint();
      }

      if (contextStore != null) {

        List<ChatTurn> newTurns = new List<ChatTurn>();

        //the provider-side context does not yet know the current harness prompt
        if (harnessPrompt != null && harnessFingerprint != session.HarnessPromptFingerprint) {
          newTurns.Add(new ChatTurn(ChatRoles.System, harnessPrompt));
        }

        newTurns.AddRange(BuildTurns(session.Messages.Skip(session.ContextStoreCoveredMessageCount)));

        try {
          answer = this.InvokeWithToolLoop(llm, newTurns.ToArray(), ref contextStore, cancellationToken);
        }
        catch (ContextStoreInvalidException) {
          //the provider-side context has expired: retry once using the self-maintained context
          contextStore = null;
        }

      }

      if (contextStore == null && answer == null) {
        ChatTurn[] allTurns = BuildSelfMaintainedContext(session, harnessPrompt);
        answer = this.InvokeWithToolLoop(llm, allTurns, ref contextStore, cancellationToken);
      }

      ChatMessage assistantMessage = this.CreateMessage(ChatRoles.Assistant, this.GetAssistantName(), answer);

      lock (_SyncRoot) {

        //reload, because the session could have been renamed in the meantime
        ChatSession current = this.LoadSessionOrThrow(chatId);
        current.Messages = current.Messages.Concat(new ChatMessage[] { assistantMessage }).ToArray();
        current.LastAction = assistantMessage.LastAction;
        current.ContextStore = contextStore;

        if (contextStore == null) {
          current.ContextStoreCoveredMessageCount = 0;
          current.HarnessPromptFingerprint = null;
        }
        else {
          current.ContextStoreCoveredMessageCount = current.Messages.Length;
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

      ICommonLLM llm = _LLMFactory.Invoke();
      ChatSession session = this.LoadSessionOrThrow(chatId);

      if (session.Messages.Length == 0) {
        return;
      }

      List<ChatTurn> turns = new List<ChatTurn>(BuildSelfMaintainedContext(session));
      turns.Add(new ChatTurn(ChatRoles.User, _CompressionPrompt));

      string noContextStore = null;
      string summary = InvokeChatApi(llm, turns.ToArray(), ref noContextStore, cancellationToken);

      lock (_SyncRoot) {

        ChatSession current = this.LoadSessionOrThrow(chatId);
        current.ContextBaseline = summary;
        current.ContextBaselineCoveredMessageCount = session.Messages.Length;

        //the provider-side context would still contain the full history
        current.ContextStore = null;
        current.ContextStoreCoveredMessageCount = 0;
        current.HarnessPromptFingerprint = null;

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

      return messages.Select((m) => new ChatTurn(m.Role, m.Content)).ToArray();
    }

    /// <summary>
    /// Invokes the LLM and executes requested harness tools (text protocol) until the model answers without
    /// tool calls. The intermediate tool exchange is not persisted as chat message (it is part of the
    /// provider-side context only, if there is one).
    /// </summary>
    private string InvokeWithToolLoop(
      ICommonLLM llm, ChatTurn[] turns, ref string contextStore, CancellationToken cancellationToken
    ) {

      string answer = InvokeChatApi(llm, turns, ref contextStore, cancellationToken);

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

        string toolResponses = _Toolbox.ExecuteToolCalls(calls);
        ChatTurn toolResponseTurn = new ChatTurn(ChatRoles.User, toolResponses);

        conversation.Add(new ChatTurn(ChatRoles.Assistant, answer));
        conversation.Add(toolResponseTurn);

        if (contextStore != null) {
          //the provider already knows the conversation including the tool call
          answer = InvokeChatApi(llm, new ChatTurn[] { toolResponseTurn }, ref contextStore, cancellationToken);
        }
        else {
          answer = InvokeChatApi(llm, conversation.ToArray(), ref contextStore, cancellationToken);
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
    /// (a late answer is discarded).
    /// </summary>
    private static string InvokeChatApi(
      ICommonLLM llm, ChatTurn[] turns, ref string contextStore, CancellationToken cancellationToken
    ) {

      string contextStoreIn = contextStore;
      string contextStoreOut = null;

      Task<string> llmTask = Task.Run(
        () => {
          string localContextStore = contextStoreIn;
          string result = llm.CallChatApi(turns, ref localContextStore);
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
