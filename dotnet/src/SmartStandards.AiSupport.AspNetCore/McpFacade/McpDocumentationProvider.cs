using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AI.SmartStandards {

  /// <summary>
  /// Reads XML documentation (summary, param) for controller actions.
  /// Lookup order: the action method itself, then same-named methods of interfaces implemented by the controller,
  /// then same-named methods of interfaces used as generic arguments of the controller's base types
  /// (covers generated controllers like UJMW's without depending on them).
  /// </summary>
  public class McpDocumentationProvider {

    private readonly ConcurrentDictionary<string, Dictionary<string, XElement>> _XmlDocsByAssemblyPath;

    public McpDocumentationProvider() {
      _XmlDocsByAssemblyPath = new ConcurrentDictionary<string, Dictionary<string, XElement>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the documentation of the given action, or an empty documentation object if none is available.
    /// </summary>
    public virtual McpOperationDocumentation GetDocumentation(ActionDescriptor actionDescriptor) {
      ControllerActionDescriptor controllerAction = actionDescriptor as ControllerActionDescriptor;
      if (controllerAction == null) {
        return new McpOperationDocumentation(null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
      }

      MethodInfo[] candidates = this.GetCandidateMethods(controllerAction).ToArray();

      // The contract method (interface method behind a generated controller) defines the logical parameters
      MethodInfo contractMethod = candidates.FirstOrDefault((m) => m.DeclaringType.IsInterface);
      if (contractMethod == null) {
        contractMethod = controllerAction.MethodInfo;
      }

      foreach (MethodInfo candidate in candidates) {
        XElement member = this.FindMember(candidate);
        if (member == null) {
          continue;
        }

        string summary = Normalize(member.Element("summary"));
        Dictionary<string, string> parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement param in member.Elements("param")) {
          string name = (string)param.Attribute("name");
          if (!string.IsNullOrWhiteSpace(name)) {
            parameters[name] = Normalize(param);
          }
        }
        string returns = Normalize(member.Element("returns"));
        if (!string.IsNullOrWhiteSpace(returns)) {
          if (string.IsNullOrWhiteSpace(summary)) {
            summary = "Returns: " + returns;
          }
          else {
            summary = summary + " Returns: " + returns;
          }
        }
        return new McpOperationDocumentation(summary, parameters, contractMethod);
      }

      return new McpOperationDocumentation(null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), contractMethod);
    }

    /// <summary>
    /// Returns the XML documentation summary of a property, or null if none is available.
    /// </summary>
    public virtual string GetPropertySummary(PropertyInfo property) {
      Type declaringType = property.DeclaringType;
      if (declaringType == null || declaringType.FullName == null) {
        return null;
      }
      Dictionary<string, XElement> members = this.GetXmlDocs(declaringType.Assembly);
      string typeName = Regex.Replace(declaringType.FullName.Replace('+', '.'), "`\\d+", string.Empty);
      XElement member;
      if (members.TryGetValue("P:" + typeName + "." + property.Name, out member)) {
        return Normalize(member.Element("summary"));
      }
      return null;
    }

    /// <summary>
    /// Enumerates the methods whose XML documentation may describe the given action.
    /// </summary>
    private IEnumerable<MethodInfo> GetCandidateMethods(ControllerActionDescriptor controllerAction) {
      MethodInfo actionMethod = controllerAction.MethodInfo;
      yield return actionMethod;

      Type controllerType = controllerAction.ControllerTypeInfo.AsType();
      string[] names = new string[] { actionMethod.Name, controllerAction.ActionName };

      List<Type> interfaces = new List<Type>(controllerType.GetInterfaces());
      Type baseType = controllerType.BaseType;
      while (baseType != null) {
        if (baseType.IsGenericType) {
          foreach (Type genericArgument in baseType.GetGenericArguments()) {
            if (genericArgument.IsInterface) {
              interfaces.Add(genericArgument);
            }
          }
        }
        baseType = baseType.BaseType;
      }

      foreach (Type contractType in interfaces.Distinct()) {
        foreach (MethodInfo method in contractType.GetMethods()) {
          if (names.Contains(method.Name, StringComparer.OrdinalIgnoreCase)) {
            yield return method;
          }
        }
      }
    }

    /// <summary>
    /// Finds the XML documentation member of a method (matched by name and parameter count).
    /// </summary>
    private XElement FindMember(MethodInfo method) {
      Dictionary<string, XElement> members = this.GetXmlDocs(method.DeclaringType.Assembly);
      if (members.Count == 0) {
        return null;
      }

      string typeName = method.DeclaringType.FullName;
      if (typeName == null) {
        return null;
      }
      typeName = Regex.Replace(typeName.Replace('+', '.'), "`\\d+", string.Empty);
      string prefix = "M:" + typeName + "." + method.Name;
      int parameterCount = method.GetParameters().Length;

      foreach (KeyValuePair<string, XElement> member in members) {
        if (parameterCount == 0 && string.Equals(member.Key, prefix, StringComparison.Ordinal)) {
          return member.Value;
        }
        if (member.Key.StartsWith(prefix + "(", StringComparison.Ordinal) && CountParameters(member.Key) == parameterCount) {
          return member.Value;
        }
      }
      return null;
    }

    /// <summary>
    /// Loads (and caches) the XML documentation file located next to the given assembly.
    /// </summary>
    private Dictionary<string, XElement> GetXmlDocs(Assembly assembly) {
      if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) {
        return new Dictionary<string, XElement>();
      }

      string xmlPath = Path.ChangeExtension(assembly.Location, ".xml");
      return _XmlDocsByAssemblyPath.GetOrAdd(xmlPath, (path) => {
        Dictionary<string, XElement> result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        if (!File.Exists(path)) {
          return result;
        }
        XDocument document = XDocument.Load(path);
        foreach (XElement member in document.Descendants("member")) {
          string name = (string)member.Attribute("name");
          if (!string.IsNullOrEmpty(name)) {
            result[name] = member;
          }
        }
        return result;
      });
    }

    /// <summary>
    /// Counts the top-level parameters of an XML documentation member id.
    /// </summary>
    private static int CountParameters(string memberId) {
      int start = memberId.IndexOf('(');
      int end = memberId.LastIndexOf(')');
      if (start < 0 || end <= start + 1) {
        return 0;
      }
      int depth = 0;
      int count = 1;
      for (int i = start + 1; i < end; i++) {
        char current = memberId[i];
        if (current == '{' || current == '[') {
          depth++;
        }
        else if (current == '}' || current == ']') {
          depth--;
        }
        else if (current == ',' && depth == 0) {
          count++;
        }
      }
      return count;
    }

    /// <summary>
    /// Flattens an XML documentation element into a single-line text (cref names are kept).
    /// </summary>
    private static string Normalize(XElement element) {
      if (element == null) {
        return null;
      }
      foreach (XElement reference in element.Descendants().ToArray()) {
        XAttribute cref = reference.Attribute("cref");
        if (cref == null) {
          cref = reference.Attribute("name");
        }
        if (cref != null && reference.IsEmpty) {
          string text = cref.Value;
          int colonIndex = text.IndexOf(':');
          if (colonIndex >= 0) {
            text = text.Substring(colonIndex + 1);
          }
          int parameterListIndex = text.IndexOf('(');
          if (parameterListIndex >= 0) {
            text = text.Substring(0, parameterListIndex);
          }
          reference.ReplaceWith(new XText(text.Split('.').Last()));
        }
      }
      string value = Regex.Replace(element.Value, "\\s+", " ").Trim();
      if (value.Length == 0) {
        return null;
      }
      return value;
    }

  }

  /// <summary>
  /// Documentation of a single API operation.
  /// </summary>
  public sealed class McpOperationDocumentation {

    private readonly string _Summary;
    private readonly Dictionary<string, string> _ParameterDescriptions;
    private readonly MethodInfo _ContractMethod;

    public McpOperationDocumentation(string summary, Dictionary<string, string> parameterDescriptions) : this(summary, parameterDescriptions, null) {
    }

    public McpOperationDocumentation(string summary, Dictionary<string, string> parameterDescriptions, MethodInfo contractMethod) {
      _Summary = summary;
      _ParameterDescriptions = parameterDescriptions;
      _ContractMethod = contractMethod;
    }

    /// <summary>
    /// The method that logically defines the operation (the interface method behind a generated controller,
    /// otherwise the action method itself). May be null.
    /// </summary>
    public MethodInfo ContractMethod {
      get {
        return _ContractMethod;
      }
    }

    /// <summary>
    /// Returns true if the contract method declares a parameter with the given name (case-insensitive) that has
    /// no default value and is not a Nullable value type.
    /// </summary>
    public bool IsRequiredContractParameter(string parameterName) {
      if (_ContractMethod == null || parameterName == null) {
        return false;
      }
      foreach (ParameterInfo parameter in _ContractMethod.GetParameters()) {
        if (!string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase)) {
          continue;
        }
        if (parameter.IsOut || parameter.HasDefaultValue || parameter.IsOptional) {
          return false;
        }
        return Nullable.GetUnderlyingType(parameter.ParameterType) == null;
      }
      return false;
    }

    public string Summary {
      get {
        return _Summary;
      }
    }

    /// <summary>
    /// Returns the description of the given parameter (case-insensitive), or null.
    /// </summary>
    public string GetParameterDescription(string parameterName) {
      string description;
      if (parameterName != null && _ParameterDescriptions.TryGetValue(parameterName, out description)) {
        return description;
      }
      return null;
    }

  }

}
