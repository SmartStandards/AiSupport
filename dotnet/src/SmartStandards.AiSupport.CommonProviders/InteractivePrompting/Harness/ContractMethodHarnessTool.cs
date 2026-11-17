using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Publishes a single method of a (UJMW-transportable) .NET contract interface as in-process harness tool.
  /// The arguments object contains one property per method parameter; the result is returned as JSON.
  /// Descriptions are taken from the XML documentation file next to the assembly of the contract (if available).
  /// </summary>
  public class ContractMethodHarnessTool : IHarnessTool {

    private static readonly ConcurrentDictionary<string, Dictionary<string, XElement>> _XmlDocsByFile = (
      new ConcurrentDictionary<string, Dictionary<string, XElement>>(StringComparer.OrdinalIgnoreCase)
    );

    private readonly MethodInfo _Method;

    private readonly Func<object> _InstanceGetter;

    private readonly string _Name;

    private readonly string _Description;

    private readonly string _ParametersJsonSchema;

    /// <summary>
    /// Creates a tool for the given contract method.
    /// </summary>
    /// <param name="method">A method of the contract interface.</param>
    /// <param name="instanceGetter">Returns the implementation of the contract (resolved on every call).</param>
    /// <param name="toolName">Unique tool name (letters, digits and underscores).</param>
    public ContractMethodHarnessTool(MethodInfo method, Func<object> instanceGetter, string toolName) {

      if (method == null) {
        throw new ArgumentNullException(nameof(method));
      }

      if (instanceGetter == null) {
        throw new ArgumentNullException(nameof(instanceGetter));
      }

      if (string.IsNullOrWhiteSpace(toolName)) {
        throw new ArgumentNullException(nameof(toolName));
      }

      _Method = method;
      _InstanceGetter = instanceGetter;
      _Name = toolName;
      _Description = BuildDescription(method);
      _ParametersJsonSchema = BuildParametersSchema(method).ToString(Formatting.None);

    }

    /// <summary>
    /// Creates one tool per public method of the given contract interface.
    /// The tool names are '{toolNamePrefix}{method_name_in_snake_case}'.
    /// </summary>
    /// <param name="instanceGetter">Returns the implementation of the contract (resolved on every call).</param>
    /// <param name="toolNamePrefix">Prefix of all tool names (for example 'calendar_').</param>
    /// <param name="methodFilter">Optional filter selecting the methods to publish.</param>
    public static ContractMethodHarnessTool[] CreateTools<TContract>(
      Func<TContract> instanceGetter, string toolNamePrefix, Func<MethodInfo, bool> methodFilter
    ) where TContract : class {

      if (instanceGetter == null) {
        throw new ArgumentNullException(nameof(instanceGetter));
      }

      Type contractType = typeof(TContract);

      if (!contractType.IsInterface) {
        throw new ArgumentException("The contract type must be an interface.", nameof(TContract));
      }

      if (toolNamePrefix == null) {
        toolNamePrefix = string.Empty;
      }

      List<ContractMethodHarnessTool> tools = new List<ContractMethodHarnessTool>();

      foreach (MethodInfo method in contractType.GetMethods().OrderBy((m) => m.MetadataToken)) {

        if (method.IsSpecialName) {
          continue;
        }

        if (methodFilter != null && !methodFilter.Invoke(method)) {
          continue;
        }

        string toolName = toolNamePrefix + ToSnakeCase(method.Name);
        tools.Add(new ContractMethodHarnessTool(method, () => instanceGetter.Invoke(), toolName));
      }

      return tools.ToArray();
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

    public string ParametersJsonSchema {
      get {
        return _ParametersJsonSchema;
      }
    }

    /// <summary>
    /// The contract method behind this tool.
    /// </summary>
    public MethodInfo Method {
      get {
        return _Method;
      }
    }

    /// <summary>
    /// Converts the arguments into the parameter values, invokes the method and returns the result as JSON.
    /// Exceptions of the implementation are rethrown unwrapped (the harness returns them as error result).
    /// </summary>
    public string Invoke(JObject arguments) {

      ParameterInfo[] parameters = _Method.GetParameters();
      object[] values = new object[parameters.Length];
      JsonSerializer serializer = JsonSerializer.CreateDefault();

      for (int i = 0; i < parameters.Length; i++) {
        values[i] = ConvertArgument(parameters[i], arguments, serializer);
      }

      object instance = _InstanceGetter.Invoke();

      if (instance == null) {
        throw new InvalidOperationException($"No implementation is available for the tool '{_Name}'.");
      }

      object result;

      try {
        result = _Method.Invoke(instance, values);
      }
      catch (TargetInvocationException ex) {

        if (ex.InnerException != null) {
          System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }

        throw;
      }

      if (_Method.ReturnType == typeof(void)) {
        return "OK";
      }

      return JsonConvert.SerializeObject(result, Formatting.None);
    }

    private static object ConvertArgument(ParameterInfo parameter, JObject arguments, JsonSerializer serializer) {

      JToken token = null;

      if (arguments != null) {

        JProperty property = arguments.Properties().FirstOrDefault(
          (p) => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
        );

        if (property != null) {
          token = property.Value;
        }

      }

      if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) {

        if (parameter.HasDefaultValue) {
          return parameter.DefaultValue;
        }

        throw new ArgumentException($"The argument '{parameter.Name}' is required.");
      }

      try {
        return token.ToObject(parameter.ParameterType, serializer);
      }
      catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidCastException || ex is ArgumentException) {
        throw new ArgumentException($"The argument '{parameter.Name}' has an invalid value: {ex.Message}");
      }

    }

    #region " Schema and documentation "

    private static string BuildDescription(MethodInfo method) {

      XElement member = FindMember(method);

      if (member == null) {
        return method.Name;
      }

      string summary = NormalizeDocText(member.Element("summary"));
      string returns = NormalizeDocText(member.Element("returns"));

      if (string.IsNullOrWhiteSpace(summary)) {
        summary = method.Name;
      }

      if (!string.IsNullOrWhiteSpace(returns)) {
        summary = summary + " Returns: " + returns;
      }

      return summary;
    }

    private static JObject BuildParametersSchema(MethodInfo method) {

      XElement member = FindMember(method);

      JObject schema = new JObject();
      schema["type"] = "object";

      JObject properties = new JObject();
      JArray required = new JArray();

      foreach (ParameterInfo parameter in method.GetParameters()) {

        JObject parameterSchema = GenerateSchema(parameter.ParameterType, new HashSet<Type>());

        if (member != null) {

          XElement parameterDoc = member.Elements("param").FirstOrDefault(
            (p) => string.Equals((string)p.Attribute("name"), parameter.Name, StringComparison.Ordinal)
          );

          string description = NormalizeDocText(parameterDoc);

          if (!string.IsNullOrWhiteSpace(description)) {
            parameterSchema["description"] = description;
          }

        }

        properties[parameter.Name] = parameterSchema;

        if (!parameter.HasDefaultValue) {
          required.Add(parameter.Name);
        }

      }

      schema["properties"] = properties;

      if (required.Count > 0) {
        schema["required"] = required;
      }

      return schema;
    }

    private static JObject GenerateSchema(Type type, HashSet<Type> visitedTypes) {

      Type effectiveType = Nullable.GetUnderlyingType(type);

      if (effectiveType == null) {
        effectiveType = type;
      }

      JObject schema = new JObject();

      if (effectiveType == typeof(string) || effectiveType == typeof(Guid)) {
        schema["type"] = "string";
        return schema;
      }

      if (effectiveType == typeof(DateTime) || effectiveType == typeof(DateTimeOffset)) {
        schema["type"] = "string";
        schema["format"] = "date-time";
        return schema;
      }

      if (effectiveType == typeof(bool)) {
        schema["type"] = "boolean";
        return schema;
      }

      if (
        effectiveType == typeof(byte) || effectiveType == typeof(short) || effectiveType == typeof(int) ||
        effectiveType == typeof(long) || effectiveType == typeof(sbyte) || effectiveType == typeof(ushort) ||
        effectiveType == typeof(uint) || effectiveType == typeof(ulong)
      ) {
        schema["type"] = "integer";
        return schema;
      }

      if (effectiveType == typeof(float) || effectiveType == typeof(double) || effectiveType == typeof(decimal)) {
        schema["type"] = "number";
        return schema;
      }

      if (effectiveType.IsEnum) {
        schema["type"] = "string";
        schema["enum"] = new JArray(Enum.GetNames(effectiveType));
        return schema;
      }

      Type itemType = GetEnumerableItemType(effectiveType);

      if (itemType != null) {
        schema["type"] = "array";
        schema["items"] = GenerateSchema(itemType, visitedTypes);
        return schema;
      }

      schema["type"] = "object";

      if (visitedTypes.Contains(effectiveType)) {
        return schema;
      }

      visitedTypes.Add(effectiveType);

      JObject properties = new JObject();

      foreach (PropertyInfo property in effectiveType.GetProperties(BindingFlags.Instance | BindingFlags.Public)) {

        if (!property.CanRead || property.GetIndexParameters().Length > 0) {
          continue;
        }

        JObject propertySchema = GenerateSchema(property.PropertyType, visitedTypes);
        string description = GetPropertySummary(property);

        if (!string.IsNullOrWhiteSpace(description)) {
          propertySchema["description"] = description;
        }

        properties[GetJsonPropertyName(property)] = propertySchema;
      }

      schema["properties"] = properties;
      visitedTypes.Remove(effectiveType);

      return schema;
    }

    private static Type GetEnumerableItemType(Type type) {

      if (type == typeof(string)) {
        return null;
      }

      if (type.IsArray) {
        return type.GetElementType();
      }

      if (!typeof(IEnumerable).IsAssignableFrom(type)) {
        return null;
      }

      if (type.IsGenericType && type.GetGenericArguments().Length == 1) {
        return type.GetGenericArguments()[0];
      }

      return typeof(object);
    }

    private static string GetJsonPropertyName(PropertyInfo property) {

      JsonPropertyAttribute attribute = property.GetCustomAttribute<JsonPropertyAttribute>();

      if (attribute != null && !string.IsNullOrWhiteSpace(attribute.PropertyName)) {
        return attribute.PropertyName;
      }

      return property.Name;
    }

    private static string GetPropertySummary(PropertyInfo property) {

      Type declaringType = property.DeclaringType;

      if (declaringType == null || declaringType.FullName == null) {
        return null;
      }

      Dictionary<string, XElement> members = GetXmlDocs(declaringType.Assembly);
      XElement member;

      if (members.TryGetValue("P:" + GetDocTypeName(declaringType) + "." + property.Name, out member)) {
        return NormalizeDocText(member.Element("summary"));
      }

      return null;
    }

    private static XElement FindMember(MethodInfo method) {

      Type declaringType = method.DeclaringType;

      if (declaringType == null || declaringType.FullName == null) {
        return null;
      }

      Dictionary<string, XElement> members = GetXmlDocs(declaringType.Assembly);
      string prefix = "M:" + GetDocTypeName(declaringType) + "." + method.Name;
      int parameterCount = method.GetParameters().Length;

      foreach (KeyValuePair<string, XElement> member in members) {

        if (parameterCount == 0 && string.Equals(member.Key, prefix, StringComparison.Ordinal)) {
          return member.Value;
        }

        if (parameterCount > 0 && member.Key.StartsWith(prefix + "(", StringComparison.Ordinal)) {

          if (CountDocParameters(member.Key) == parameterCount) {
            return member.Value;
          }

        }

      }

      return null;
    }

    private static int CountDocParameters(string memberKey) {

      int start = memberKey.IndexOf('(');
      int end = memberKey.LastIndexOf(')');

      if (start < 0 || end <= start + 1) {
        return 0;
      }

      int depth = 0;
      int count = 1;

      for (int i = start + 1; i < end; i++) {

        char c = memberKey[i];

        if (c == '{' || c == '(' || c == '[') {
          depth++;
        }
        else if (c == '}' || c == ')' || c == ']') {
          depth--;
        }
        else if (c == ',' && depth == 0) {
          count++;
        }

      }

      return count;
    }

    private static string GetDocTypeName(Type type) {

      return Regex.Replace(type.FullName.Replace('+', '.'), "`\\d+", string.Empty);
    }

    private static Dictionary<string, XElement> GetXmlDocs(Assembly assembly) {

      if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) {
        return new Dictionary<string, XElement>();
      }

      string xmlFileFullName = Path.ChangeExtension(assembly.Location, ".xml");

      return _XmlDocsByFile.GetOrAdd(xmlFileFullName, (fileFullName) => {

        Dictionary<string, XElement> result = new Dictionary<string, XElement>(StringComparer.Ordinal);

        if (!File.Exists(fileFullName)) {
          return result;
        }

        XDocument document = XDocument.Load(fileFullName);

        foreach (XElement member in document.Descendants("member")) {

          string name = (string)member.Attribute("name");

          if (!string.IsNullOrWhiteSpace(name)) {
            result[name] = member;
          }

        }

        return result;
      });

    }

    private static string NormalizeDocText(XElement element) {

      if (element == null) {
        return null;
      }

      StringBuilder text = new StringBuilder();

      foreach (XNode node in element.Nodes()) {

        XElement childElement = node as XElement;

        if (childElement == null) {
          text.Append(node.ToString());
          continue;
        }

        string reference = (string)childElement.Attribute("cref");

        if (reference == null) {
          reference = (string)childElement.Attribute("name");
        }

        if (reference != null) {
          int lastDot = reference.LastIndexOf('.');
          text.Append(reference.Substring(lastDot + 1));
        }
        else {
          text.Append(childElement.Value);
        }

      }

      return Regex.Replace(System.Net.WebUtility.HtmlDecode(text.ToString()), "\\s+", " ").Trim();
    }

    /// <summary>
    /// Converts 'GetEvents' into 'get_events'.
    /// </summary>
    public static string ToSnakeCase(string name) {

      if (string.IsNullOrEmpty(name)) {
        return name;
      }

      return Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
    }

    #endregion

  }

}
