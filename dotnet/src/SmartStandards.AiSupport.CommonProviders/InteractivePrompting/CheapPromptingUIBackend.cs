using System.ComponentModel;
using System.Diagnostics.Contracts;

namespace AI.SmartStandards.InteractivePrompting {

  public class CheapPromptingUIBackend : IPromptingUIBackend {
    public string BeginNewChat(string name) {
      throw new NotImplementedException();
    }

    public bool ChatIdExists(string chatId) {
      throw new NotImplementedException();
    }

    public void DeleteChat(string chatId) {
      throw new NotImplementedException();
    }

    public int GetChatMessageCount(string chatId) {
      throw new NotImplementedException();
    }

    public ChatMessage[] GetChatMessages(string chatId, int startIndex, int endIndex) {
      throw new NotImplementedException();
    }

    public ChatMessage[] GetChatMessages(string chatId, DateTime from, DateTime to) {
      throw new NotImplementedException();
    }

    public ChatNameAndId[] GetRecentChats() {
      throw new NotImplementedException();
    }

    public void RenameChat(string chatId, string newName) {
      throw new NotImplementedException();
    }

    public void SendMessage(string chatId, string message) {
      throw new NotImplementedException();
    }
  }

}
