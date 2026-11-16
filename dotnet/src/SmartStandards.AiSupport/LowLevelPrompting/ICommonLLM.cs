using System.ComponentModel;
using System.Diagnostics.Contracts;
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

    /// <summary>
    /// Sends chat messages to the LLM and returns the answer.
    /// 'contextStore' is an opaque value owned by the provider: if it holds a value (returned by a previous call),
    /// 'newMessages' must contain ONLY the messages which were added since that call. If it is null, 'newMessages'
    /// must contain the complete (self-maintained) context. After the call, 'contextStore' contains the value which
    /// must be passed in the next call (or null, if the provider does not support provider-side context).
    /// </summary>
#if NET_FX
  [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string CallChatApi(ChatTurn[] newMessages, ref string contextStore);

  }

}
