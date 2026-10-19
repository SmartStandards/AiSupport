using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

namespace AI.SmartStandards {

  [TestClass()]
  public class McpFacadeTest {

    [TestMethod()]
    public void IncludeOperationNormalizesMethodAndRelativePath() {
      McpFacadeOptions options = new McpFacadeOptions();

      options.IncludeOperation("sample_tool", "get", "/api/sample", "Sample operation");

      McpOperationRegistration[] registrations = options.GetIncludedOperations();
      Assert.AreEqual(1, registrations.Length);
      Assert.AreEqual("sample_tool", registrations[0].ToolName);
      Assert.AreEqual("GET", registrations[0].HttpMethod);
      Assert.AreEqual("api/sample", registrations[0].RelativePath);
      Assert.AreEqual("Sample operation", registrations[0].Description);
    }

    [TestMethod()]
    public void GenerateSchemaUsesJsonPropertyNames() {
      McpJsonSchemaGenerator generator = new McpJsonSchemaGenerator();

      JObject schema = generator.GenerateSchema(typeof(SampleToolInput));

      JObject properties = (JObject)schema["properties"];
      Assert.IsTrue(properties.ContainsKey("calendar_id"));
      Assert.IsTrue(properties.ContainsKey("Limit"));
      Assert.AreEqual("string", properties["calendar_id"]["type"].Value<string>());
      Assert.AreEqual("integer", properties["Limit"]["type"].Value<string>());
      Assert.AreEqual(false, schema["additionalProperties"].Value<bool>());
    }

    private sealed class SampleToolInput {

      private string _CalendarId;
      private int _Limit;
      private DateTime _From;

      [JsonProperty("calendar_id")]
      public string CalendarId {
        get {
          return _CalendarId;
        }
        set {
          _CalendarId = value;
        }
      }

      public int Limit {
        get {
          return _Limit;
        }
        set {
          _Limit = value;
        }
      }

      public DateTime From {
        get {
          return _From;
        }
        set {
          _From = value;
        }
      }

    }

  }

}
