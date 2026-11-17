using System;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.Threading;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Backend contract for a chat UI. It manages persisted chats and forwards new messages to the configured LLM.
  /// </summary>
#if NET_FX
  [ServiceContract()]
#endif
  public interface IPromptingUIBackend {

    /// <summary>
    /// Returns all known chats, ordered by their last action (most recent first).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ChatNameAndId[] GetRecentChats();

    /// <summary>
    /// Returns the number of messages within the given chat.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    int GetChatMessageCount(string chatId);

    /// <summary>
    /// Returns the messages of the given chat within the (inclusive) index range.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ChatMessage[] GetChatMessages(string chatId, int startIndex, int endIndex);

    /// <summary>
    /// Returns the messages of the given chat within the (inclusive) time range.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ChatMessage[] GetChatMessagesByTime(string chatId, DateTime from, DateTime to);

#if NET_FX
    /// <summary>
    /// Sends a new user message to the LLM and returns the answer (which is also persisted within the chat).
    /// </summary>
    [OperationContract(), WebInvoke(Method = "POST")]
    ChatMessage SendMessage(string chatId, string message);
#else
    /// <summary>
    /// Sends a new user message to the LLM and returns the answer (which is also persisted within the chat).
    /// This is a UJMW long-running operation (the UI can poll and cancel it).
    /// The optional callback 'onProgress' (statusText, current, max) is delivered to UJMW clients
    /// via the polling responses ('onProgressInvokes'). Clients which do not know it can ignore it.
    /// </summary>
    ChatMessage SendMessage(CancellationToken cancellationToken, string chatId, string message, Action<string, int, int> onProgress);
#endif

#if NET_FX
    /// <summary>
    /// Lets the LLM compress the self-maintained context of the chat into a new baseline.
    /// The full message history is NOT affected.
    /// </summary>
    [OperationContract(), WebInvoke(Method = "POST")]
    void CompressChatContext(string chatId);
#else
    /// <summary>
    /// Lets the LLM compress the self-maintained context of the chat into a new baseline.
    /// The full message history is NOT affected.
    /// This is a UJMW long-running operation (the UI can poll and cancel it).
    /// </summary>
    void CompressChatContext(CancellationToken cancellationToken, string chatId);
#endif

    /// <summary>
    /// Deletes the given chat.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void DeleteChat(string chatId);

    /// <summary>
    /// Returns true if a chat with the given id exists.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    bool ChatIdExists(string chatId);

    /// <summary>
    /// Creates a new (empty) chat and returns its id.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string BeginNewChat(string name);

    /// <summary>
    /// Renames the given chat.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void RenameChat(string chatId, string newName);

    /// <summary>
    /// Returns the details of the given chat (name, provider, model, compression state).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ChatDetails GetChatDetails(string chatId);

    /// <summary>
    /// Switches the LLM provider and/or model of the given chat (kept per chat).
    /// If the provider changes, the provider-side context is discarded and rebuilt from the persisted history
    /// with the next message. An empty 'model' selects the model configured for the provider.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetChatProvider(string chatId, string providerName, string model);

    /// <summary>
    /// Raises the level of a skill source for the given chat (see 'SkillSourceLevels'). This is one-way:
    /// a level below the current effective level is ignored, because loaded content cannot be removed from a context.
    /// The model is informed via a control message in front of the next user message.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void RaiseChatSkillSourceLevel(string chatId, string sourceId, string level);

    /// <summary>
    /// Sets the level of a tool source for the given chat (see 'ToolSourceLevels').
    /// The model is informed via a control message in front of the next user message.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetChatToolSourceLevel(string chatId, string sourceId, string level);

    /// <summary>
    /// Returns the tool calls of the given chat which are currently waiting for a confirmation by the user
    /// (tools of sources with level 'RequiresConfirmation' during a running 'SendMessage').
    /// UIs should poll this while a message is being sent. Unanswered requests are denied after a timeout.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    PendingToolApproval[] GetPendingToolApprovals(string chatId);

    /// <summary>
    /// Answers a pending tool call (see 'GetPendingToolApprovals') with one of the 'ToolApprovalDecisions':
    /// 'Deny' (the call is rejected and the tool source is hidden for this chat),
    /// 'AllowOnce' (only this call is executed) or
    /// 'AllowForChat' (this and all further calls of the same tool source within this chat are executed without asking).
    /// Unknown or already answered approval ids are ignored, unknown decisions are treated as 'Deny'.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void AnswerToolApproval(string chatId, string approvalId, string decision);

    /// <summary>
    /// Returns the audit log of all tool calls which were requested within the given chat (chronological).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ToolAuditEntry[] GetToolAuditLog(string chatId);

    //TODO: attachments

  }

  /// <summary>
  /// A tool call which is waiting for the confirmation of the user.
  /// </summary>
  public class PendingToolApproval {

    private string _ApprovalId;
    private string _ToolName;
    private string _SourceId;
    private string _SourceDisplayName;
    private string _ArgumentsJson;
    private DateTime _RequestedUtc;
    private DateTime _ExpiresUtc;
    private string _Kind = PendingInteractionKinds.ToolApproval;
    private string _Subject;

    /// <summary>
    /// Kind of the request (see 'PendingInteractionKinds'). Clients which do not know a kind can treat it
    /// like a tool approval: guard requests additionally accept the decision 'AllowPermanently'.
    /// </summary>
    public string Kind {
      get {
        return _Kind;
      }
      set {
        _Kind = value;
      }
    }

    /// <summary>
    /// Subject of a guard request (the path, the domain or the command line), null for tool approvals.
    /// </summary>
    public string Subject {
      get {
        return _Subject;
      }
      set {
        _Subject = value;
      }
    }

    /// <summary>
    /// Id of the request (to be passed to 'AnswerToolApproval').
    /// </summary>
    public string ApprovalId {
      get {
        return _ApprovalId;
      }
      set {
        _ApprovalId = value;
      }
    }

    /// <summary>
    /// Name of the tool which the model wants to call.
    /// </summary>
    public string ToolName {
      get {
        return _ToolName;
      }
      set {
        _ToolName = value;
      }
    }

    /// <summary>
    /// Id of the tool source of the tool.
    /// </summary>
    public string SourceId {
      get {
        return _SourceId;
      }
      set {
        _SourceId = value;
      }
    }

    /// <summary>
    /// Display name of the tool source of the tool.
    /// </summary>
    public string SourceDisplayName {
      get {
        return _SourceDisplayName;
      }
      set {
        _SourceDisplayName = value;
      }
    }

    /// <summary>
    /// The arguments of the call (JSON object, as requested by the model).
    /// </summary>
    public string ArgumentsJson {
      get {
        return _ArgumentsJson;
      }
      set {
        _ArgumentsJson = value;
      }
    }

    public DateTime RequestedUtc {
      get {
        return _RequestedUtc;
      }
      set {
        _RequestedUtc = value;
      }
    }

    /// <summary>
    /// After this point in time, the request is denied automatically.
    /// </summary>
    public DateTime ExpiresUtc {
      get {
        return _ExpiresUtc;
      }
      set {
        _ExpiresUtc = value;
      }
    }

  }

  public class ChatNameAndId {

    private string _Id;
    private string _Name;
    private DateTime _LastAction;

    public string Id {
      get {
        return _Id;
      }
      set {
        _Id = value;
      }
    }

    public string Name {
      get {
        return _Name;
      }
      set {
        _Name = value;
      }
    }

    public DateTime LastAction {
      get {
        return _LastAction;
      }
      set {
        _LastAction = value;
      }
    }

  }

  /// <summary>
  /// Details of a chat (as shown in the chat details dialog).
  /// </summary>
  public class ChatDetails {

    private string _Id;
    private string _Name;
    private string _ProviderName;
    private string _Model;
    private int _MessageCount;
    private bool _IsCompressed;
    private DateTime _LastAction;
    private ChatSourceState[] _SkillSources;
    private ChatSourceState[] _ToolSources;

    /// <summary>
    /// Skill sources and their effective level within this chat (empty if the backend has no sources).
    /// </summary>
    public ChatSourceState[] SkillSources {
      get {
        return _SkillSources;
      }
      set {
        _SkillSources = value;
      }
    }

    /// <summary>
    /// Tool sources and their effective level within this chat (empty if the backend has no sources).
    /// </summary>
    public ChatSourceState[] ToolSources {
      get {
        return _ToolSources;
      }
      set {
        _ToolSources = value;
      }
    }

    public string Id {
      get {
        return _Id;
      }
      set {
        _Id = value;
      }
    }

    public string Name {
      get {
        return _Name;
      }
      set {
        _Name = value;
      }
    }

    /// <summary>
    /// The effective LLM provider of this chat.
    /// </summary>
    public string ProviderName {
      get {
        return _ProviderName;
      }
      set {
        _ProviderName = value;
      }
    }

    /// <summary>
    /// The effective model of this chat.
    /// </summary>
    public string Model {
      get {
        return _Model;
      }
      set {
        _Model = value;
      }
    }

    public int MessageCount {
      get {
        return _MessageCount;
      }
      set {
        _MessageCount = value;
      }
    }

    /// <summary>
    /// True if the context of this chat was compressed into a baseline.
    /// </summary>
    public bool IsCompressed {
      get {
        return _IsCompressed;
      }
      set {
        _IsCompressed = value;
      }
    }

    public DateTime LastAction {
      get {
        return _LastAction;
      }
      set {
        _LastAction = value;
      }
    }

  }

  /// <summary>
  /// State of a skill source or tool source within a chat (as shown in the chat details dialog).
  /// </summary>
  public class ChatSourceState {

    private string _SourceId;
    private string _DisplayName;
    private string _DefaultLevel;
    private string _EffectiveLevel;
    private bool _IsFixed;
    private string[] _ItemNames;
    private bool _IsApprovedForChat;

    /// <summary>
    /// Only for tool sources: true if the user answered a confirmation with 'AllowForChat',
    /// so that the tools of this source are executed without asking within this chat.
    /// Setting the level of the source again via 'SetChatToolSourceLevel' revokes this approval.
    /// </summary>
    public bool IsApprovedForChat {
      get {
        return _IsApprovedForChat;
      }
      set {
        _IsApprovedForChat = value;
      }
    }

    public string SourceId {
      get {
        return _SourceId;
      }
      set {
        _SourceId = value;
      }
    }

    public string DisplayName {
      get {
        return _DisplayName;
      }
      set {
        _DisplayName = value;
      }
    }

    /// <summary>
    /// The global default level ('SkillSourceLevels' or 'ToolSourceLevels').
    /// </summary>
    public string DefaultLevel {
      get {
        return _DefaultLevel;
      }
      set {
        _DefaultLevel = value;
      }
    }

    /// <summary>
    /// The level which is effective within the chat (default or per-chat override).
    /// </summary>
    public string EffectiveLevel {
      get {
        return _EffectiveLevel;
      }
      set {
        _EffectiveLevel = value;
      }
    }

    /// <summary>
    /// True if the level cannot be changed (for example built-in skills).
    /// </summary>
    public bool IsFixed {
      get {
        return _IsFixed;
      }
      set {
        _IsFixed = value;
      }
    }

    /// <summary>
    /// Names of the skills/tools of this source.
    /// </summary>
    public string[] ItemNames {
      get {
        return _ItemNames;
      }
      set {
        _ItemNames = value;
      }
    }

  }

  public class ChatMessage {

    private string _Id;
    private string _Role;
    private string _Author;
    private string _Content;
    private DateTime _LastAction;
    private string _Kind;

    public string Id {
      get {
        return _Id;
      }
      set {
        _Id = value;
      }
    }

    /// <summary>
    /// 'user' or 'assistant' (see 'ChatRoles').
    /// </summary>
    public string Role {
      get {
        return _Role;
      }
      set {
        _Role = value;
      }
    }

    /// <summary>
    /// Display name of the author (for example the user name or the LLM provider/model).
    /// </summary>
    public string Author {
      get {
        return _Author;
      }
      set {
        _Author = value;
      }
    }

    public string Content {
      get {
        return _Content;
      }
      set {
        _Content = value;
      }
    }

    public DateTime LastAction {
      get {
        return _LastAction;
      }
      set {
        _LastAction = value;
      }
    }

    /// <summary>
    /// Optional kind of the message (see 'ChatMessageKinds'). Null = regular chat message.
    /// Clients which do not know the kind can simply ignore it (convention credo).
    /// </summary>
    public string Kind {
      get {
        return _Kind;
      }
      set {
        _Kind = value;
      }
    }

  }

  /// <summary>
  /// Well-known values of 'ChatMessage.Kind'.
  /// </summary>
  public static class ChatMessageKinds {

    /// <summary>
    /// A harness control message (for example a skill/tool switch notice or 'SKILL x loaded'),
    /// rendered as ghost bubble and delivered to the model in front of the next user message.
    /// </summary>
    public const string Control = "Control";

    /// <summary>
    /// A purely informative activity note of the harness (for example "tool 'x' called" or "SKILL 'x' loaded"),
    /// rendered as ghost bubble. It is NOT delivered to the model (the model already knows its own tool calls).
    /// </summary>
    public const string Activity = "Activity";

    /// <summary>
    /// Convention: progress status texts starting with this prefix announce an activity while the turn is running.
    /// UIs which know the convention render them as live ghost bubble (without the prefix), others simply show
    /// the status text.
    /// </summary>
    public const string ActivityProgressPrefix = "[activity] ";

  }

  /// <summary>
  /// Well-known role names
  /// </summary>
  public static class ChatRoles {

    public const string System = "system";

    public const string User = "user";

    public const string Assistant = "assistant";

  }

}
