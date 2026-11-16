using System;

namespace AI.SmartStandards.LowLevelPrompting {

  /// <summary>
  /// Raised by 'ICommonLLM.CallChatApi' if the passed 'contextStore' is no longer valid (for example an expired
  /// server-side id). The caller should reset its 'contextStore' to null and retry once with the self-maintained context.
  /// </summary>
  public class ContextStoreInvalidException : Exception {

    public ContextStoreInvalidException(string message) : base(message) {
    }

    public ContextStoreInvalidException(string message, Exception innerException) : base(message, innerException) {
    }

  }

}
