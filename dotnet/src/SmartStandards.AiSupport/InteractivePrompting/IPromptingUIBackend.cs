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
    /// </summary>
    ChatMessage SendMessage(CancellationToken cancellationToken, string chatId, string message);
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

    //TODO: attachments

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

  public class ChatMessage {

    private string _Id;
    private string _Role;
    private string _Author;
    private string _Content;
    private DateTime _LastAction;

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

  }

  /// <summary>
  /// Well-known role names used within 'ChatMessage.Role' and 'ChatTurn.Role'.
  /// </summary>
  public static class ChatRoles {

    public const string System = "system";

    public const string User = "user";

    public const string Assistant = "assistant";

  }

}
