using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System;

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

		/// <summary>
		/// Adds the MCP facade. Authentication is NOT implemented here: by default, the MCP endpoint is protected
		/// according to the authentication requirements of the published operations, which are resolved and
		/// validated via AuthTokenHandling (see 'McpFacadeOptions.RequestAuthenticator').
		/// </summary>
		void AddMcpFacade(Action<McpFacadeOptions> configure);

	}

	internal sealed class AiSupportSetup : IAiSupportSetup {

		private readonly IServiceCollection _Services;

		public AiSupportSetup(IServiceCollection services) {
			_Services = services;
		}

		/// <summary>
		/// Registers all services of the MCP facade.
		/// </summary>
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
			_Services.AddSingleton<McpAuthenticationGate>();
			//required by the browser login (protected client binding cookie)
			_Services.AddDataProtection();
			_Services.AddSingleton<McpBrowserLoginHandler>();
			_Services.AddSingleton<McpIndexPageRenderer>();
		}

	}

}
