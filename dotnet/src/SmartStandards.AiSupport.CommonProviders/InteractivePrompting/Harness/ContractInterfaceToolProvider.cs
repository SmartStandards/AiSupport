using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Web.UJMW;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// TLM-3: publishes the methods of a UJMW contract interface (from an uploaded DLL) as harness tools.
  /// Calls are executed via the UJMW 'DynamicClientFactory' against the configured base URL.
  /// The optional token of the source is sent as 'Authorization' header (prefixed with 'Bearer ' if it has no scheme).
  /// </summary>
  public class ContractInterfaceToolProvider : IHarnessToolProvider {

    private static readonly ConcurrentDictionary<string, Assembly> _LoadedAssemblies = (
      new ConcurrentDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
    );

    private readonly string _AssemblyFileFullName;

    private readonly string _InterfaceName;

    private readonly string _ToolNamePrefix;

    public ContractInterfaceToolProvider(string assemblyFileFullName, string interfaceName, string toolNamePrefix) {

      if (string.IsNullOrWhiteSpace(assemblyFileFullName)) {
        throw new ArgumentNullException(nameof(assemblyFileFullName));
      }

      if (string.IsNullOrWhiteSpace(interfaceName)) {
        throw new ArgumentNullException(nameof(interfaceName));
      }

      _AssemblyFileFullName = assemblyFileFullName;
      _InterfaceName = interfaceName;
      _ToolNamePrefix = McpToolProvider.SanitizeName(toolNamePrefix);

    }

    public string ProviderId {
      get {
        return "contract";
      }
    }

    public string DisplayName {
      get {
        return "UJMW contract interface";
      }
    }

    public string DefaultLevel {
      get {
        return ToolSourceLevels.RequiresConfirmation;
      }
    }

    public bool SupportsCredential {
      get {
        return true;
      }
    }

    public string GetDefaultSettingsJson() {
      return "{}";
    }

    public string GetSettingsUiHtml() {
      return null;
    }

    public string ValidateSettingsJson(string settingsJson) {

      if (string.IsNullOrWhiteSpace(settingsJson)) {
        return "{}";
      }

      try {
        return JObject.Parse(settingsJson).ToString(Formatting.Indented);
      }
      catch (JsonException ex) {
        throw new ArgumentException("The settings are not a valid JSON object: " + ex.Message);
      }

    }

    public IHarnessTool[] CreateTools(IToolProviderContext context) {

      if (string.IsNullOrWhiteSpace(context.Location)) {
        throw new InvalidOperationException("The base URL of the service is missing.");
      }

      Type contractType = LoadAssembly(_AssemblyFileFullName).GetType(_InterfaceName, false);

      if (contractType == null || !contractType.IsInterface) {
        throw new InvalidOperationException($"The interface '{_InterfaceName}' was not found within '{_AssemblyFileFullName}'.");
      }

      object clientLock = new object();
      object client = null;

      Func<object> instanceGetter = () => {

        lock (clientLock) {

          if (client == null) {

            Func<string> urlGetter = () => context.Location;
            Func<string> authHeaderGetter = () => BuildAuthHeader(context.Credential);

            client = DynamicClientFactory.CreateInstance(contractType, urlGetter, authHeaderGetter, new string[0]);
          }

          return client;
        }

      };

      List<IHarnessTool> tools = new List<IHarnessTool>();

      foreach (MethodInfo method in contractType.GetMethods().OrderBy((m) => m.MetadataToken)) {

        if (method.IsSpecialName) {
          continue;
        }

        string toolName = _ToolNamePrefix + "__" + ContractMethodHarnessTool.ToSnakeCase(method.Name);
        tools.Add(new ContractMethodHarnessTool(method, instanceGetter, toolName));
      }

      return tools.ToArray();
    }

    private static string BuildAuthHeader(string credential) {

      if (string.IsNullOrWhiteSpace(credential)) {
        return null;
      }

      string trimmed = credential.Trim();

      if (trimmed.IndexOf(' ') > 0) {
        return trimmed;
      }

      return "Bearer " + trimmed;
    }

    /// <summary>
    /// Loads the assembly into a dedicated (collectible-free) load context (cached per file).
    /// </summary>
    public static Assembly LoadAssembly(string assemblyFileFullName) {

      return _LoadedAssemblies.GetOrAdd(assemblyFileFullName, (fileFullName) => {

        AssemblyLoadContext loadContext = new AssemblyLoadContext("ToolAssembly:" + Path.GetFileName(fileFullName), false);
        string directory = Path.GetDirectoryName(fileFullName);

        loadContext.Resolving += (alc, assemblyName) => {

          string candidate = Path.Combine(directory, assemblyName.Name + ".dll");

          if (File.Exists(candidate)) {
            return alc.LoadFromAssemblyPath(candidate);
          }

          return null;
        };

        return loadContext.LoadFromAssemblyPath(fileFullName);
      });

    }

    /// <summary>
    /// Returns the public interfaces of the given assembly with their method names.
    /// </summary>
    public static ContractInterfaceInfo[] GetContractInterfaces(string assemblyId, string assemblyFileFullName) {

      Assembly assembly = LoadAssembly(assemblyFileFullName);
      Type[] types;

      try {
        types = assembly.GetExportedTypes();
      }
      catch (ReflectionTypeLoadException ex) {
        types = ex.Types.Where((t) => t != null && t.IsPublic).ToArray();
      }

      List<ContractInterfaceInfo> result = new List<ContractInterfaceInfo>();

      foreach (Type type in types.Where((t) => t.IsInterface && !t.IsGenericTypeDefinition).OrderBy((t) => t.FullName)) {

        ContractInterfaceInfo info = new ContractInterfaceInfo();
        info.AssemblyId = assemblyId;
        info.InterfaceName = type.FullName;
        info.MethodNames = type.GetMethods().Where((m) => !m.IsSpecialName).Select((m) => m.Name).ToArray();

        result.Add(info);
      }

      return result.ToArray();
    }

  }

}
