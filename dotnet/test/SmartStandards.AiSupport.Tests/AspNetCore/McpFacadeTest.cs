using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

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

    [TestMethod()]
    public void GenerateSchemaDescribesSideChannelAndKeepsItTransparent() {
      McpFacadeOptions options = new McpFacadeOptions();
      options.AddSideChannelFieldDescription("Tenant.Id", "Selects the tenant scope.");
      McpJsonSchemaGenerator generator = new McpJsonSchemaGenerator(null, Options.Create(options));

      JObject schema = generator.GenerateSchema(typeof(SampleEnvelopeInput));

      JObject sideChannel = (JObject)schema["properties"]["_"];
      Assert.AreEqual("object", sideChannel["type"].Value<string>());
      Assert.IsFalse(string.IsNullOrWhiteSpace(sideChannel["description"].Value<string>()));
      Assert.AreEqual("string", sideChannel["properties"]["Tenant.Id"]["type"].Value<string>());
      Assert.AreEqual("Selects the tenant scope.", sideChannel["properties"]["Tenant.Id"]["description"].Value<string>());
      Assert.AreEqual("string", sideChannel["additionalProperties"]["type"].Value<string>());
      Assert.AreEqual("string", sideChannel["propertyNames"]["type"].Value<string>());
      StringAssert.Contains(sideChannel["description"].Value<string>(), "dictionary (string -> string)");
    }

    [TestMethod()]
    public void GenerateSchemaOffersSideChannelWithoutRegisteredFields() {
      McpJsonSchemaGenerator generator = new McpJsonSchemaGenerator();

      JObject schema = generator.GenerateSchema(typeof(SampleEnvelopeInput));

      JObject sideChannel = (JObject)schema["properties"]["_"];
      Assert.AreEqual("object", sideChannel["type"].Value<string>());
      Assert.AreEqual(0, ((JObject)sideChannel["properties"]).Count);
      Assert.AreEqual("string", sideChannel["additionalProperties"]["type"].Value<string>());
    }

    [TestMethod()]
    public void AddSideChannelFieldDescriptionReplacesExistingDescription() {
      McpFacadeOptions options = new McpFacadeOptions();

      options.AddSideChannelFieldDescription("Foo.Bar", "first");
      options.AddSideChannelFieldDescription("Foo.Bar", "second");

      KeyValuePair<string, string>[] descriptions = options.GetSideChannelFieldDescriptions();
      Assert.AreEqual(1, descriptions.Length);
      Assert.AreEqual("second", descriptions[0].Value);
    }

    private sealed class SampleEnvelopeInput {

      private string _Subject;
      private Dictionary<string, string> _SideChannel;

      public string Subject {
        get {
          return _Subject;
        }
        set {
          _Subject = value;
        }
      }

      [JsonProperty("_")]
      public Dictionary<string, string> SideChannel {
        get {
          return _SideChannel;
        }
        set {
          _SideChannel = value;
        }
      }

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
