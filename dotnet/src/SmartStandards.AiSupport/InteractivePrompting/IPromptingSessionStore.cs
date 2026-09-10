using System.ComponentModel;
using System;
using System.Diagnostics.Contracts;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.InteractivePrompting {

#if NET_FX 
  [ServiceContract()]
#endif
  public interface IPromptingSessionStore {

 
  }




}
