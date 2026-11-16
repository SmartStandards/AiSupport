using System;
using System.ComponentModel;
using System.Diagnostics.Contracts;

namespace AI.SmartStandards.LowLevelPrompting {

  /// <summary>
  /// One message within a chat context which is sent to the LLM (role + content).
  /// </summary>
  public class ChatTurn {

    private string _Role;
    private string _Content;

    public ChatTurn() {
    }

    public ChatTurn(string role, string content) {
      _Role = role;
      _Content = content;
    }

    /// <summary>
    /// 'system', 'user' or 'assistant' (see 'AI.SmartStandards.InteractivePrompting.ChatRoles').
    /// </summary>
    public string Role {
      get {
        return _Role;
      }
      set {
        _Role = value;
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

  }

}
