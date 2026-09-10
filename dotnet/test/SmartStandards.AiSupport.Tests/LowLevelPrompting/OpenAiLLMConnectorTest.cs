using AI.SmartStandards.UjmwSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.ComponentModel;
using System.Reflection;

namespace AI.SmartStandards.LowLevelPrompting {

  [TestClass()]
  public class OpenAiLLMConnectorTest {

    [TestMethod(),Ignore()]
    public void OpenAiProviderTest1() {

      DynamicAiServiceFactory.AiOperationsProvider = new OpenAiLLMConnector("???");

      IMeinTool tool = DynamicAiServiceFactory.CreateInstance<IMeinTool>(
        ImplementationMode.AiGeneratedInmemoryCode
      );

      int erg = tool.MultipliziereDieDifferenzZweiterZahlenMitSichSelbst(3, 7);

      Assert.AreEqual(16, erg);

    }

  }

  public interface IMeinTool {

    int MultipliziereDieDifferenzZweiterZahlenMitSichSelbst(int ZahlA, int ZahlB);

  }

}
