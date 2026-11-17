using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Contracts;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Persistence abstraction for chat sessions. A session always contains the FULL message history
  /// (independent of any provider-side context optimization).
  /// </summary>
#if NET_FX
  [ServiceContract()]
#endif
  public interface IPromptingSessionStore {

    /// <summary>
    /// Returns the header information (id, name, last action) of all stored sessions.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ChatNameAndId[] GetSessionHeaders();

    /// <summary>
    /// Returns true if a session with the given id exists.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    bool SessionExists(string sessionId);

    /// <summary>
    /// Loads the given session (or returns null if it does not exist).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    ChatSession LoadSession(string sessionId);

    /// <summary>
    /// Creates or overwrites the given session.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SaveSession(ChatSession session);

    /// <summary>
    /// Deletes the given session (does nothing if it does not exist).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void DeleteSession(string sessionId);

  }

  /// <summary>
  /// A persisted chat including its full history and the state required to build the LLM context.
  /// </summary>
  public class ChatSession {

    private string _Id;
    private string _Name;
    private DateTime _LastAction;
    private ChatMessage[] _Messages = new ChatMessage[0];
    private string _ContextStore;
    private int _ContextStoreCoveredMessageCount;
    private string _ContextBaseline;
    private int _ContextBaselineCoveredMessageCount;
    private string _HarnessPromptFingerprint;
    private string _ProviderName;
    private string _Model;
    private Dictionary<string, string> _SkillLevelOverrides = new Dictionary<string, string>();
    private Dictionary<string, string> _ToolLevelOverrides = new Dictionary<string, string>();
    private string[] _ChatApprovals = new string[0];
    private ToolAuditEntry[] _ToolAuditLog = new ToolAuditEntry[0];

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

    /// <summary>
    /// The full message history (as shown in the UI).
    /// </summary>
    public ChatMessage[] Messages {
      get {
        return _Messages;
      }
      set {
        _Messages = value;
      }
    }

    /// <summary>
    /// Opaque context value returned by the LLM provider (for example a server-side response id or a
    /// compressed snapshot). Null if the provider does not support it (then the context is self-maintained).
    /// </summary>
    public string ContextStore {
      get {
        return _ContextStore;
      }
      set {
        _ContextStore = value;
      }
    }

    /// <summary>
    /// Number of messages (from the beginning of 'Messages') which are already covered by 'ContextStore'.
    /// </summary>
    public int ContextStoreCoveredMessageCount {
      get {
        return _ContextStoreCoveredMessageCount;
      }
      set {
        _ContextStoreCoveredMessageCount = value;
      }
    }

    /// <summary>
    /// Compressed summary of the conversation (created via explicit compression). Null if not compressed yet.
    /// </summary>
    public string ContextBaseline {
      get {
        return _ContextBaseline;
      }
      set {
        _ContextBaseline = value;
      }
    }

    /// <summary>
    /// Number of messages (from the beginning of 'Messages') which are covered by 'ContextBaseline'.
    /// </summary>
    public int ContextBaselineCoveredMessageCount {
      get {
        return _ContextBaselineCoveredMessageCount;
      }
      set {
        _ContextBaselineCoveredMessageCount = value;
      }
    }

    /// <summary>
    /// Fingerprint of the hidden harness prompt (skills/tools) which is contained in the provider-side context
    /// ('ContextStore'). If the current harness prompt differs, it is sent again (null = not sent yet).
    /// </summary>
    public string HarnessPromptFingerprint {
      get {
        return _HarnessPromptFingerprint;
      }
      set {
        _HarnessPromptFingerprint = value;
      }
    }

    /// <summary>
    /// The LLM provider used by this chat (snapshot of the default provider at chat creation, changeable per chat).
    /// Null = use the globally configured default provider (for example chats created before Iteration 2).
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
    /// The concrete model used by this chat (snapshot of the default model at chat creation, changeable per chat).
    /// Null = use the model configured for the provider in the global settings.
    /// </summary>
    public string Model {
      get {
        return _Model;
      }
      set {
        _Model = value;
      }
    }

    /// <summary>
    /// Per-chat overrides of skill source levels (skill source id -> level, see 'SkillSourceLevels').
    /// Overrides can only raise a level (one-way), because loaded content cannot be removed from a context.
    /// </summary>
    public Dictionary<string, string> SkillLevelOverrides {
      get {
        return _SkillLevelOverrides;
      }
      set {
        _SkillLevelOverrides = value;
      }
    }

    /// <summary>
    /// Per-chat overrides of tool source levels (tool source id -> level, see 'ToolSourceLevels'),
    /// including 'Hidden' as result of a 'Deny' decision.
    /// </summary>
    public Dictionary<string, string> ToolLevelOverrides {
      get {
        return _ToolLevelOverrides;
      }
      set {
        _ToolLevelOverrides = value;
      }
    }

    /// <summary>
    /// Ids of the tool sources which were approved with 'Allow for this chat'.
    /// </summary>
    public string[] ChatApprovals {
      get {
        return _ChatApprovals;
      }
      set {
        _ChatApprovals = value;
      }
    }

    /// <summary>
    /// Audit log of all tool calls requested within this chat (chronological).
    /// </summary>
    public ToolAuditEntry[] ToolAuditLog {
      get {
        return _ToolAuditLog;
      }
      set {
        _ToolAuditLog = value;
      }
    }

    /// <summary>
    /// Replaces null values (for example from session files written before Iteration 2) with empty defaults.
    /// </summary>
    public void NormalizeAfterLoad() {

      if (_Messages == null) {
        _Messages = new ChatMessage[0];
      }

      if (_SkillLevelOverrides == null) {
        _SkillLevelOverrides = new Dictionary<string, string>();
      }

      if (_ToolLevelOverrides == null) {
        _ToolLevelOverrides = new Dictionary<string, string>();
      }

      if (_ChatApprovals == null) {
        _ChatApprovals = new string[0];
      }

      if (_ToolAuditLog == null) {
        _ToolAuditLog = new ToolAuditEntry[0];
      }

    }

  }

  /// <summary>
  /// One entry of the per-chat tool audit log.
  /// </summary>
  public class ToolAuditEntry {

    private DateTime _TimestampUtc;
    private string _ToolSourceId;
    private string _ToolName;
    private string _ArgumentSummary;
    private string _Decision;
    private long _DurationMs;
    private bool _Success;
    private string _ErrorMessage;
    private int _ResultSize;

    public DateTime TimestampUtc {
      get {
        return _TimestampUtc;
      }
      set {
        _TimestampUtc = value;
      }
    }

    public string ToolSourceId {
      get {
        return _ToolSourceId;
      }
      set {
        _ToolSourceId = value;
      }
    }

    public string ToolName {
      get {
        return _ToolName;
      }
      set {
        _ToolName = value;
      }
    }

    /// <summary>
    /// Shortened (secret-free) summary of the call arguments.
    /// </summary>
    public string ArgumentSummary {
      get {
        return _ArgumentSummary;
      }
      set {
        _ArgumentSummary = value;
      }
    }

    /// <summary>
    /// The approval decision which led to the execution or rejection (see 'ToolApprovalDecisions').
    /// </summary>
    public string Decision {
      get {
        return _Decision;
      }
      set {
        _Decision = value;
      }
    }

    public long DurationMs {
      get {
        return _DurationMs;
      }
      set {
        _DurationMs = value;
      }
    }

    public bool Success {
      get {
        return _Success;
      }
      set {
        _Success = value;
      }
    }

    public string ErrorMessage {
      get {
        return _ErrorMessage;
      }
      set {
        _ErrorMessage = value;
      }
    }

    /// <summary>
    /// Length (in characters) of the tool result which was returned to the model.
    /// </summary>
    public int ResultSize {
      get {
        return _ResultSize;
      }
      set {
        _ResultSize = value;
      }
    }

  }

  /// <summary>
  /// Well-known levels of a skill source (globally and as per-chat override).
  /// </summary>
  public static class SkillSourceLevels {

    public const string Hidden = "Hidden";

    public const string OnDemand = "OnDemand";

    public const string InitializeAtStart = "InitializeAtStart";

  }

  /// <summary>
  /// Well-known levels of a tool source (globally and as per-chat override).
  /// </summary>
  public static class ToolSourceLevels {

    public const string Hidden = "Hidden";

    public const string RequiresConfirmation = "RequiresConfirmation";

    public const string NoConfirmation = "NoConfirmation";

  }

  /// <summary>
  /// Well-known decisions of the interactive tool approval (also used within the audit log).
  /// </summary>
  public static class ToolApprovalDecisions {

    public const string Deny = "Deny";

    public const string AllowOnce = "AllowOnce";

    public const string AllowForChat = "AllowForChat";

    /// <summary>
    /// Only for guard requests: the path/domain/command is added to the (global) allow-list of the tool provider.
    /// </summary>
    public const string AllowPermanently = "AllowPermanently";

    /// <summary>
    /// No interaction was required (level 'NoConfirmation' or an existing 'AllowForChat' approval).
    /// </summary>
    public const string Preapproved = "Preapproved";

    /// <summary>
    /// The tool source is hidden (for example a hallucinated call) - the call was not executed.
    /// </summary>
    public const string NotAvailable = "NotAvailable";

  }

  /// <summary>
  /// Well-known kinds of pending interactions (see 'PendingToolApproval.Kind').
  /// </summary>
  public static class PendingInteractionKinds {

    /// <summary>
    /// A tool of a source with level 'RequiresConfirmation' wants to be executed.
    /// </summary>
    public const string ToolApproval = "ToolApproval";

    /// <summary>
    /// A tool wants to access a path which is not within the allow-list (second line of defense).
    /// </summary>
    public const string PathAccess = "PathAccess";

    /// <summary>
    /// A tool wants to access a domain which is not within the allow-list (second line of defense).
    /// </summary>
    public const string DomainAccess = "DomainAccess";

    /// <summary>
    /// A tool wants to execute a command line which is not within the allow-list (second line of defense).
    /// </summary>
    public const string ShellCommand = "ShellCommand";

  }

}
