using AI.SmartStandards.DemoWebService.Sample.Calendar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.UJMW;

namespace AI.SmartStandards {

  [TestClass()]
  public class UjmwSideChannelSchemaTest {

    /// <summary>
    /// Verifies against the real UJMW controller factory that the request side channel ('_') of a dynamic
    /// controller is emitted as property and therefore offered transparently in the MCP tool input schema.
    /// </summary>
    [TestMethod()]
    public void UjmwRequestSideChannelIsOfferedInToolSchema() {
      DynamicUjmwControllerOptions controllerOptions = new DynamicUjmwControllerOptions();
      controllerOptions.ControllerRoute = "calendar";
      controllerOptions.EnableRequestSidechannel = true;
      controllerOptions.EnableResponseSidechannel = true;
      Type controllerType = DynamicUjmwControllerFactory.BuildDynamicControllerType(typeof(ICalendarAccess), controllerOptions, "SideChannelSchemaTest");

      McpFacadeOptions facadeOptions = new McpFacadeOptions();
      facadeOptions.AddSideChannelFieldDescription("Tenant.Id", "Selects the tenant scope.");
      McpJsonSchemaGenerator generator = new McpJsonSchemaGenerator(null, Options.Create(facadeOptions));

      MethodInfo action = controllerType.GetMethod(nameof(ICalendarAccess.GetCalendars), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
      Assert.IsNotNull(action);
      Type requestType = action.GetParameters()[0].ParameterType;
      Assert.IsNotNull(requestType.GetProperty("_"), "UJMW no longer emits the request side channel as property.");
      Assert.IsNotNull(action.ReturnType.GetProperty("_"), "UJMW no longer emits the response side channel as property.");

      JObject schema = generator.GenerateSchema(requestType);

      JObject sideChannel = (JObject)schema["properties"]["_"];
      Assert.AreEqual("object", sideChannel["type"].Value<string>());
      Assert.AreEqual("Selects the tenant scope.", sideChannel["properties"]["Tenant.Id"]["description"].Value<string>());
      Assert.AreEqual("string", sideChannel["additionalProperties"]["type"].Value<string>());
    }

    /// <summary>
    /// Verifies that the DI container creates the schema generator with the facade options, so that the registered
    /// side channel field descriptions reach the published tool schemas.
    /// </summary>
    [TestMethod()]
    public void DependencyInjectionProvidesSideChannelDescriptionsToSchemaGenerator() {
      ServiceCollection services = new ServiceCollection();
      services.AddAiSupport((ai) => {
        ai.AddMcpFacade((mcp) => {
          mcp.AddSideChannelFieldDescription("Tenant", "Selects the tenant scope.");
        });
      });

      using (ServiceProvider serviceProvider = services.BuildServiceProvider()) {
        McpJsonSchemaGenerator generator = serviceProvider.GetRequiredService<McpJsonSchemaGenerator>();
        JObject schema = generator.GenerateSchema(typeof(SampleEnvelope));
        Assert.AreEqual("Selects the tenant scope.", schema["properties"]["_"]["properties"]["Tenant"]["description"].Value<string>());
      }
    }

    private sealed class SampleEnvelope {

      private Dictionary<string, string> _SideChannel;

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

  }

}
