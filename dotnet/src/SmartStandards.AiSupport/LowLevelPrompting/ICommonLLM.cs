using System;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.Threading;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.LowLevelPrompting {

#if NET_FX 
  [ServiceContract()]
#endif
  public interface ICommonLLM {

#if NET_FX
  [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string CallWebSearchApi(string prompt, object inputData = null);

#if NET_FX
  [OperationContract(), WebInvoke(Method = "POST")]
#endif
    T CallWebSearchApi<T>(string prompt, object inputData = null) where T : class;

#if NET_FX
  [OperationContract(), WebInvoke(Method = "POST")]
#endif
    byte[] CallImageEditApi(byte[] inputImageBytes, string prompt, byte[] maskImageBytes = null);

#if NET_FX
  [OperationContract(), WebInvoke(Method = "POST")]
#endif
    byte[] CallImageGeneratorApi(string prompt);

    #if NET_FX
    /// <summary>
    /// Sends chat messages to the LLM and returns the answer.
    /// 'contextStore' is an opaque value owned by the provider: if it holds a value (returned by a previous call),
    /// 'newMessages' must contain ONLY the messages which were added since that call. If it is null, 'newMessages'
    /// must contain the complete (self-maintained) context. After the call, 'contextStore' contains the value which
    /// must be passed in the next call (or null, if the provider does not support provider-side context).
    /// </summary>
    [OperationContract(), WebInvoke(Method = "POST")]
    string CallChatApi(ChatTurn[] newMessages, ref string contextStore);
#else
    /// <summary>
    /// Sends chat messages to the LLM and returns the answer.
    /// 'contextStore' is an opaque value owned by the provider: if it holds a value (returned by a previous call),
    /// 'newMessages' must contain ONLY the messages which were added since that call. If it is null, 'newMessages'
    /// must contain the complete (self-maintained) context. After the call, 'contextStore' contains the value which
    /// must be passed in the next call (or null, if the provider does not support provider-side context).
    /// 'cancellationToken' aborts the provider call (HTTP request or CLI turn) as fast as possible.
    /// 'onProgress' (optional, may be null) reports (statusText, currentItem, totalItems): a status text of null keeps
    /// the previous text, counters are 0 if not applicable.
    /// </summary>
    string CallChatApi(
      CancellationToken cancellationToken, ChatTurn[] newMessages, ref string contextStore, Action<string, int, int> onProgress
    );
#endif

  }

}
