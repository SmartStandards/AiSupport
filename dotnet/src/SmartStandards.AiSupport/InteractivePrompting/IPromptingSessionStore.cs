using System;
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

  }

}
