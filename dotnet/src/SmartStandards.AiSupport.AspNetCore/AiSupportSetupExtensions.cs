using Microsoft.Extensions.DependencyInjection;
using System;
using System.Web.UJMW;

namespace AI.SmartStandards {

  public static partial class AiSupportSetupExtensions {

    public static void AddAiSupport(
      this IServiceCollection services, Action<IAiSupportSetup> onRegisteringTargets
    ) {

      AiSupportSetup setup = new AiSupportSetup(services);

      if (onRegisteringTargets != null) {
        onRegisteringTargets.Invoke(setup);
      }

    }

  }


  public interface IAiSupportSetup {

    void AddMcpFacade(Action<McpFacadeOptions> configure);

  }

  internal sealed class AiSupportSetup : IAiSupportSetup {

    private readonly IServiceCollection _Services;

    public AiSupportSetup(IServiceCollection services) {
      _Services = services;
    }

    public void AddMcpFacade(Action<McpFacadeOptions> configure) {

      _Services.AddOptions<McpFacadeOptions>();

      if (configure != null) {
        _Services.Configure(configure);
      }

      _Services.AddSingleton<McpJsonSchemaGenerator>();
      _Services.AddSingleton<McpDocumentationProvider>();
      _Services.AddSingleton<McpToolCatalog>();
      _Services.AddSingleton<McpToolInvoker>();
      _Services.AddSingleton<McpProtocolHandler>();
      _Services.AddSingleton<McpIndexPageRenderer>();
    }

  }
  
}
