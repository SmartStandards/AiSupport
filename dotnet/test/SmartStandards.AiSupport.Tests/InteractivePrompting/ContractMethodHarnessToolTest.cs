using AI.SmartStandards.InteractivePrompting.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class ContractMethodHarnessToolTest {

    public interface ISampleContract {

      SampleItem[] GetItems(string filter, int maxCount);

      SampleItem CreateItem(SampleItem newItem);

      bool DeleteItem(string id, string eTag = null);

    }

    public class SampleItem {

      private string _Id;

      private DateTime _Start;

      public string Id {
        get {
          return _Id;
        }
        set {
          _Id = value;
        }
      }

      public DateTime Start {
        get {
          return _Start;
        }
        set {
          _Start = value;
        }
      }

    }

    private class SampleImplementation : ISampleContract {

      private string _LastDeletedETag = "<none>";

      public string LastDeletedETag {
        get {
          return _LastDeletedETag;
        }
      }

      public SampleItem[] GetItems(string filter, int maxCount) {

        return Enumerable.Range(1, maxCount).Select((i) => new SampleItem { Id = filter + i }).ToArray();
      }

      public SampleItem CreateItem(SampleItem newItem) {

        if (newItem.Start == DateTime.MinValue) {
          throw new ArgumentException("Start is required.");
        }

        newItem.Id = "new";
        return newItem;
      }

      public bool DeleteItem(string id, string eTag = null) {

        _LastDeletedETag = eTag;
        return true;
      }

    }

    [TestMethod()]
    public void CreateTools_ShouldPublishAllMethodsWithSnakeCaseNamesAndSchemas() {

      SampleImplementation implementation = new SampleImplementation();

      ContractMethodHarnessTool[] tools = ContractMethodHarnessTool.CreateTools<ISampleContract>(
        () => implementation, "sample_", null
      );

      CollectionAssert.AreEquivalent(
        new string[] { "sample_get_items", "sample_create_item", "sample_delete_item" },
        tools.Select((t) => t.Name).ToArray()
      );

      JObject deleteSchema = JObject.Parse(tools.Single((t) => t.Name == "sample_delete_item").ParametersJsonSchema);
      CollectionAssert.AreEqual(new string[] { "id" }, deleteSchema["required"].Values<string>().ToArray());

      JObject createSchema = JObject.Parse(tools.Single((t) => t.Name == "sample_create_item").ParametersJsonSchema);
      Assert.AreEqual("date-time", (string)createSchema["properties"]["newItem"]["properties"]["Start"]["format"]);

    }

    [TestMethod()]
    public void ExecuteToolCalls_ShouldInvokeContractAndReturnJsonOrError() {

      SampleImplementation implementation = new SampleImplementation();
      HarnessToolbox toolbox = new HarnessToolbox();

      foreach (ContractMethodHarnessTool tool in ContractMethodHarnessTool.CreateTools<ISampleContract>(() => implementation, "sample_", null)) {
        toolbox.AddTool(tool);
      }

      string response = toolbox.ExecuteToolCalls(HarnessToolbox.ParseToolCalls(
        "<tool_call>{\"name\":\"sample_get_items\",\"arguments\":{\"filter\":\"x\",\"maxCount\":2}}</tool_call>"
      ));
      StringAssert.Contains(response, "x2");

      response = toolbox.ExecuteToolCalls(HarnessToolbox.ParseToolCalls(
        "<tool_call>{\"name\":\"sample_delete_item\",\"arguments\":{\"id\":\"a\"}}</tool_call>"
      ));
      StringAssert.Contains(response, "true");
      Assert.IsNull(implementation.LastDeletedETag);

      response = toolbox.ExecuteToolCalls(HarnessToolbox.ParseToolCalls(
        "<tool_call>{\"name\":\"sample_create_item\",\"arguments\":{\"newItem\":{}}}</tool_call>"
      ));
      StringAssert.Contains(response, "\"is_error\":true");
      StringAssert.Contains(response, "Start is required.");

      response = toolbox.ExecuteToolCalls(HarnessToolbox.ParseToolCalls(
        "<tool_call>{\"name\":\"sample_get_items\",\"arguments\":{\"filter\":\"x\"}}</tool_call>"
      ));
      StringAssert.Contains(response, "The argument 'maxCount' is required.");

    }

  }

}
