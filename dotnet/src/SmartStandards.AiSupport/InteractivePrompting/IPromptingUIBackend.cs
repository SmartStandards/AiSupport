using System;
using System.ComponentModel;
using System.Diagnostics.Contracts;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.InteractivePrompting {

#if NET_FX 
  [ServiceContract()]
#endif
  public interface IPromptingUIBackend {

    ChatNameAndId[] GetRecentChats();

    int GetChatMessageCount(string chatId);
    ChatMessage[] GetChatMessages(string chatId, int startIndex, int endIndex);
    ChatMessage[] GetChatMessages(string chatId, DateTime from, DateTime to);

    void SendMessage(string chatId, string message);

    void DeleteChat(string chatId);
    bool ChatIdExists(string chatId);

    string BeginNewChat(string name);//returns a new chatid

    void RenameChat(string chatId, string newName);

    //TODO: attachments


  }


  public class ChatNameAndId {
    public string Id { get; set; }
    public string Name { get; set; }
    public DateTime LastAction { get; set; }
  }

  public class ChatMessage {
    public string Id { get; set; }
    public string Author { get; set; }
    public string Content { get; set; }
    public DateTime LastAction { get; set; }
  }


}
