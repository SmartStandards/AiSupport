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

  }

}
