using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AI.SmartStandards {

	/// <summary>
	/// Builds (once, lazily) the list of MCP tools from the explicitly included operations and API groups.
	/// Operations of included API groups that cannot be represented (e.g. form/file parameters) are not published
	/// but reported via GetRejectedOperations.
	/// </summary>
	internal sealed class McpToolCatalog {

		private static readonly Regex _ToolNamePattern = new Regex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);

		private readonly IOptions<McpFacadeOptions> _Options;
		private readonly IApiDescriptionGroupCollectionProvider _ApiDescriptions;
		private readonly McpJsonSchemaGenerator _SchemaGenerator;
		private readonly McpDocumentationProvider _DocumentationProvider;
		private readonly object _SyncRoot = new object();

		private McpToolDefinition[] _Tools;
		private McpRejectedOperation[] _RejectedOperations;

		public McpToolCatalog(
			IOptions<McpFacadeOptions> options,
			IApiDescriptionGroupCollectionProvider apiDescriptions,
			McpJsonSchemaGenerator schemaGenerator,
			McpDocumentationProvider documentationProvider
		) {
			_Options = options;
			_ApiDescriptions = apiDescriptions;
			_SchemaGenerator = schemaGenerator;
			_DocumentationProvider = documentationProvider;
		}

		/// <summary>
		/// Returns all published tools, sorted by name.
		/// </summary>
		public McpToolDefinition[] GetTools() {
			this.EnsureBuilt();
			return _Tools;
		}

		/// <summary>
		/// Returns the operations of included API groups that were not published, including the reason.
		/// </summary>
		public McpRejectedOperation[] GetRejectedOperations() {
			this.EnsureBuilt();
			return _RejectedOperations;
		}

		/// <summary>
		/// Returns the tool with the given name, or null.
		/// </summary>
		public McpToolDefinition GetTool(string toolName) {
			foreach (McpToolDefinition tool in this.GetTools()) {
				if (string.Equals(tool.Name, toolName, StringComparison.Ordinal)) {
					return tool;
				}
			}
			return null;
		}

		/// <summary>
		/// Builds the catalog on first use (API descriptions are stable after application startup).
		/// </summary>
		private void EnsureBuilt() {
			if (_Tools != null) {
				return;
			}
			lock (_SyncRoot) {
				if (_Tools != null) {
					return;
				}

				List<McpToolDefinition> tools = new List<McpToolDefinition>();
				List<McpRejectedOperation> rejected = new List<McpRejectedOperation>();
				Dictionary<string, McpToolDefinition> toolsByName = new Dictionary<string, McpToolDefinition>(StringComparer.OrdinalIgnoreCase);
				HashSet<ApiDescription> publishedDescriptions = new HashSet<ApiDescription>();

				foreach (McpOperationRegistration registration in _Options.Value.GetIncludedOperations()) {
					ApiDescription apiDescription = this.FindApiDescription(registration);
					McpToolDefinition tool = this.CreateTool(registration, apiDescription);
					this.AddTool(tool, toolsByName, tools);
					publishedDescriptions.Add(apiDescription);
				}

				foreach (string apiGroupName in _Options.Value.GetIncludedApiGroups()) {
					ApiDescription[] groupDescriptions = this.GetGroupDescriptions(apiGroupName);
					if (groupDescriptions.Length == 0) {
						throw new InvalidOperationException("The API group '" + apiGroupName + "' included into the MCP facade does not contain any operation.");
					}

					foreach (ApiDescription apiDescription in groupDescriptions) {
						if (publishedDescriptions.Contains(apiDescription) || this.IsMcpEndpoint(apiDescription)) {
							continue;
						}

						McpOperationRegistration registration = new McpOperationRegistration(
							CreateToolName(apiDescription), apiDescription.HttpMethod, NormalizeRelativePath(apiDescription.RelativePath), null
						);

						McpToolDefinition tool;
						try {
							tool = this.CreateTool(registration, apiDescription);
						}
						catch (NotSupportedException ex) {
							rejected.Add(new McpRejectedOperation(apiDescription.HttpMethod, registration.RelativePath, ex.Message));
							continue;
						}

						this.AddTool(tool, toolsByName, tools);
						publishedDescriptions.Add(apiDescription);
					}
				}

				_RejectedOperations = rejected.ToArray();
				_Tools = this.ReplaceMethodNamesWithToolNames(tools).OrderBy((t) => t.Name, StringComparer.Ordinal).ToArray();
			}
		}

		/// <summary>
		/// Descriptions are taken from XML docs and therefore reference C# method names (e.g. "as returned by GetCalendars").
		/// An agent only knows the tool names, so every whole-word occurrence of the method name of another published
		/// operation of the same controller is replaced by its tool name (in the tool description and all schema descriptions).
		/// </summary>
		private List<McpToolDefinition> ReplaceMethodNamesWithToolNames(List<McpToolDefinition> tools) {
			List<McpToolDefinition> result = new List<McpToolDefinition>();
			foreach (McpToolDefinition tool in tools) {
				Dictionary<string, string> replacements = new Dictionary<string, string>(StringComparer.Ordinal);
				foreach (McpToolDefinition sibling in tools) {
					if (!IsSameController(tool, sibling)) {
						continue;
					}
					foreach (string methodName in GetMethodNames(sibling)) {
						if (!string.Equals(methodName, sibling.Name, StringComparison.Ordinal)) {
							replacements[methodName] = sibling.Name;
						}
					}
				}

				if (replacements.Count == 0) {
					result.Add(tool);
					continue;
				}

				string description = ReplaceWords(tool.Description, replacements);
				JObject inputSchema = (JObject)tool.InputSchema.DeepClone();
				foreach (JProperty descriptionProperty in inputSchema.Descendants().OfType<JProperty>().Where((p) => p.Name == "description" && p.Value.Type == JTokenType.String).ToArray()) {
					descriptionProperty.Value = ReplaceWords(descriptionProperty.Value.ToString(), replacements);
				}

				result.Add(new McpToolDefinition(tool.Name, description, inputSchema, tool.ApiDescription, tool.Registration, tool.FlattenedBodyParameter));
			}
			return result;
		}

		private static bool IsSameController(McpToolDefinition first, McpToolDefinition second) {
			ControllerActionDescriptor firstAction = first.ApiDescription.ActionDescriptor as ControllerActionDescriptor;
			ControllerActionDescriptor secondAction = second.ApiDescription.ActionDescriptor as ControllerActionDescriptor;
			if (firstAction == null || secondAction == null) {
				return false;
			}
			return firstAction.ControllerTypeInfo == secondAction.ControllerTypeInfo;
		}

		private static string[] GetMethodNames(McpToolDefinition tool) {
			ControllerActionDescriptor action = tool.ApiDescription.ActionDescriptor as ControllerActionDescriptor;
			if (action == null) {
				return new string[0];
			}
			return new string[] { action.ActionName, action.MethodInfo.Name }.Distinct(StringComparer.Ordinal).ToArray();
		}

		private static string ReplaceWords(string text, Dictionary<string, string> replacements) {
			if (string.IsNullOrEmpty(text)) {
				return text;
			}
			string result = text;
			foreach (KeyValuePair<string, string> replacement in replacements) {
				result = Regex.Replace(result, "\\b" + Regex.Escape(replacement.Key) + "\\b", replacement.Value);
			}
			return result;
		}

		/// <summary>
		/// Validates the name of a tool and adds it (collisions are configuration errors).
		/// </summary>
		private void AddTool(McpToolDefinition tool, Dictionary<string, McpToolDefinition> toolsByName, List<McpToolDefinition> tools) {
			if (!_ToolNamePattern.IsMatch(tool.Name)) {
				throw new InvalidOperationException("The MCP tool name '" + tool.Name + "' does not match ^[a-zA-Z0-9_-]{1,64}$.");
			}
			McpToolDefinition existing;
			if (toolsByName.TryGetValue(tool.Name, out existing)) {
				throw new InvalidOperationException(
					"The MCP tool name '" + tool.Name + "' is used by '" + existing.Registration.HttpMethod + " " + existing.Registration.RelativePath +
					"' and '" + tool.Registration.HttpMethod + " " + tool.Registration.RelativePath + "'. Register one of them explicitly with a different tool name."
				);
			}
			toolsByName.Add(tool.Name, tool);
			tools.Add(tool);
		}

		/// <summary>
		/// Creates the tool definition (schema + description) for an API operation.
		/// </summary>
		private McpToolDefinition CreateTool(McpOperationRegistration registration, ApiDescription apiDescription) {
			McpOperationDocumentation documentation = _DocumentationProvider.GetDocumentation(apiDescription.ActionDescriptor);

			ApiParameterDescription flattenedBodyParameter;
			JObject inputSchema = _SchemaGenerator.GenerateInputSchema(apiDescription, documentation, out flattenedBodyParameter);

			string description = registration.Description;
			if (string.IsNullOrWhiteSpace(description)) {
				description = documentation.Summary;
			}
			if (string.IsNullOrWhiteSpace(description)) {
				description = apiDescription.HttpMethod + " " + registration.RelativePath;
			}

			return new McpToolDefinition(registration.ToolName, description, inputSchema, apiDescription, registration, flattenedBodyParameter);
		}

		/// <summary>
		/// Returns all API descriptions of the given ApiExplorer group.
		/// </summary>
		private ApiDescription[] GetGroupDescriptions(string apiGroupName) {
			List<ApiDescription> result = new List<ApiDescription>();
			foreach (ApiDescriptionGroup group in _ApiDescriptions.ApiDescriptionGroups.Items) {
				foreach (ApiDescription apiDescription in group.Items) {
					if (string.Equals(apiDescription.GroupName, apiGroupName, StringComparison.OrdinalIgnoreCase)) {
						result.Add(apiDescription);
					}
				}
			}
			return result.ToArray();
		}

		/// <summary>
		/// Returns true if the description addresses the MCP endpoint itself (never published as tool).
		/// </summary>
		private bool IsMcpEndpoint(ApiDescription apiDescription) {
			string mcpPath = NormalizeRelativePath(_Options.Value.EndpointPattern);
			return string.Equals(NormalizeRelativePath(apiDescription.RelativePath), mcpPath, StringComparison.OrdinalIgnoreCase);
		}

		private ApiDescription FindApiDescription(McpOperationRegistration registration) {
			List<ApiDescription> matches = new List<ApiDescription>();
			foreach (ApiDescriptionGroup group in _ApiDescriptions.ApiDescriptionGroups.Items) {
				foreach (ApiDescription apiDescription in group.Items) {
					if (this.Matches(apiDescription, registration)) {
						matches.Add(apiDescription);
					}
				}
			}

			if (matches.Count == 0) {
				throw new InvalidOperationException("No ASP.NET Core API action matches MCP operation '" + registration.ToolName + "' (" + registration.HttpMethod + " " + registration.RelativePath + ").");
			}
			if (matches.Count > 1) {
				throw new InvalidOperationException("More than one ASP.NET Core API action matches MCP operation '" + registration.ToolName + "' (" + registration.HttpMethod + " " + registration.RelativePath + ").");
			}
			return matches[0];
		}

		private bool Matches(ApiDescription apiDescription, McpOperationRegistration registration) {
			if (!string.Equals(apiDescription.HttpMethod, registration.HttpMethod, StringComparison.OrdinalIgnoreCase)) {
				return false;
			}
			return string.Equals(NormalizeRelativePath(apiDescription.RelativePath), registration.RelativePath, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Derives a deterministic tool name from the relative path, e.g. "calendar/GetEvents" -> "calendar_GetEvents".
		/// Non-POST operations get the HTTP method as prefix (e.g. "get_items_id") to keep same-path operations apart.
		/// </summary>
		internal static string CreateToolName(ApiDescription apiDescription) {
			string path = NormalizeRelativePath(apiDescription.RelativePath);
			StringBuilder name = new StringBuilder();

			if (!string.Equals(apiDescription.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(apiDescription.HttpMethod)) {
				name.Append(apiDescription.HttpMethod.ToLowerInvariant());
				name.Append('_');
			}

			bool lastWasSeparator = true;
			foreach (char current in path) {
				if ((current >= 'a' && current <= 'z') || (current >= 'A' && current <= 'Z') || (current >= '0' && current <= '9')) {
					name.Append(current);
					lastWasSeparator = false;
				}
				else if (!lastWasSeparator) {
					name.Append('_');
					lastWasSeparator = true;
				}
			}

			string result = name.ToString().TrimEnd('_');
			if (result.Length > 64) {
				result = result.Substring(result.Length - 64).TrimStart('_');
			}
			return result;
		}

		internal static string NormalizeRelativePath(string path) {
			if (string.IsNullOrWhiteSpace(path)) {
				return string.Empty;
			}
			string normalized = path.Trim();
			int queryIndex = normalized.IndexOf('?');
			if (queryIndex >= 0) {
				normalized = normalized.Substring(0, queryIndex);
			}
			return normalized.Trim('/');
		}

	}

	internal sealed class McpToolDefinition {

		private readonly string _Name;
		private readonly string _Description;
		private readonly JObject _InputSchema;
		private readonly ApiDescription _ApiDescription;
		private readonly McpOperationRegistration _Registration;
		private readonly ApiParameterDescription _FlattenedBodyParameter;

		public McpToolDefinition(
			string name,
			string description,
			JObject inputSchema,
			ApiDescription apiDescription,
			McpOperationRegistration registration,
			ApiParameterDescription flattenedBodyParameter
		) {
			_Name = name;
			_Description = description;
			_InputSchema = inputSchema;
			_ApiDescription = apiDescription;
			_Registration = registration;
			_FlattenedBodyParameter = flattenedBodyParameter;
		}

		public string Name {
			get {
				return _Name;
			}
		}

		public string Description {
			get {
				return _Description;
			}
		}

		public JObject InputSchema {
			get {
				return _InputSchema;
			}
		}

		public ApiDescription ApiDescription {
			get {
				return _ApiDescription;
			}
		}

		public McpOperationRegistration Registration {
			get {
				return _Registration;
			}
		}

		/// <summary>
		/// If set, the tool arguments form the JSON body of this parameter (see McpJsonSchemaGenerator).
		/// </summary>
		public ApiParameterDescription FlattenedBodyParameter {
			get {
				return _FlattenedBodyParameter;
			}
		}

	}

	internal sealed class McpRejectedOperation {

		private readonly string _HttpMethod;
		private readonly string _RelativePath;
		private readonly string _Reason;

		public McpRejectedOperation(string httpMethod, string relativePath, string reason) {
			_HttpMethod = httpMethod;
			_RelativePath = relativePath;
			_Reason = reason;
		}

		public string HttpMethod {
			get {
				return _HttpMethod;
			}
		}

		public string RelativePath {
			get {
				return _RelativePath;
			}
		}

		public string Reason {
			get {
				return _Reason;
			}
		}

	}

}
