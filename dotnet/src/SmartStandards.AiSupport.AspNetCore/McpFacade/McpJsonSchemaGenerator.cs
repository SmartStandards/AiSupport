using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

namespace AI.SmartStandards {

  public sealed class McpJsonSchemaGenerator {

    private readonly McpDocumentationProvider _DocumentationProvider;

    public McpJsonSchemaGenerator() {
      _DocumentationProvider = null;
    }

    /// <summary>
    /// Creates a generator that also emits the XML documentation of DTO properties as schema descriptions.
    /// </summary>
    public McpJsonSchemaGenerator(McpDocumentationProvider documentationProvider) {
      _DocumentationProvider = documentationProvider;
    }

    public JObject GenerateInputSchema(ApiDescription apiDescription) {
      ApiParameterDescription flattenedBodyParameter;
      McpOperationDocumentation emptyDocumentation = new McpOperationDocumentation(null, new Dictionary<string, string>());
      return this.GenerateInputSchema(apiDescription, emptyDocumentation, out flattenedBodyParameter);
    }

    /// <summary>
    /// Generates the tool input schema of an API operation. If the operation has exactly one parameter and it is a
    /// complex JSON body (e.g. a UJMW request DTO), the body properties become the tool arguments directly
    /// ('flattenedBodyParameter' is set in this case); otherwise each route/query/body parameter becomes an argument.
    /// </summary>
    public JObject GenerateInputSchema(ApiDescription apiDescription, McpOperationDocumentation documentation, out ApiParameterDescription flattenedBodyParameter) {
      flattenedBodyParameter = null;

      List<ApiParameterDescription> relevantParameters = new List<ApiParameterDescription>();
      foreach (ApiParameterDescription parameter in apiDescription.ParameterDescriptions) {
        if (this.ShouldSkipParameter(parameter)) {
          continue;
        }
        this.ThrowIfUnsupportedParameter(parameter);
        relevantParameters.Add(parameter);
      }

      if (relevantParameters.Count == 1 && this.IsBodyParameter(relevantParameters[0]) && relevantParameters[0].Type != null) {
        JObject bodySchema = this.GenerateSchema(relevantParameters[0].Type, new HashSet<Type>());
        JObject bodyProperties = bodySchema["properties"] as JObject;
        if (string.Equals(bodySchema.Value<string>("type"), "object", StringComparison.Ordinal) && bodyProperties != null) {
          JArray bodyRequired = bodySchema["required"] as JArray;
          if (bodyRequired == null) {
            bodyRequired = new JArray();
          }
          foreach (JProperty bodyProperty in bodyProperties.Properties()) {
            string description = documentation.GetParameterDescription(bodyProperty.Name);
            if (!string.IsNullOrWhiteSpace(description)) {
              ((JObject)bodyProperty.Value)["description"] = description;
            }
            bool alreadyRequired = bodyRequired.Any((t) => string.Equals(t.ToString(), bodyProperty.Name, StringComparison.Ordinal));
            if (!alreadyRequired && documentation.IsRequiredContractParameter(bodyProperty.Name)) {
              bodyRequired.Add(bodyProperty.Name);
            }
          }
          if (bodyRequired.Count > 0) {
            bodySchema["required"] = bodyRequired;
          }
          flattenedBodyParameter = relevantParameters[0];
          return bodySchema;
        }
      }

      JObject schema = new JObject();
      schema["type"] = "object";

      JObject properties = new JObject();
      JArray required = new JArray();

      foreach (ApiParameterDescription parameter in relevantParameters) {
        string parameterName = this.GetParameterName(parameter);
        Type parameterType = parameter.Type;
        if (parameterType == null) {
          parameterType = typeof(string);
        }

        JObject parameterSchema = this.GenerateSchema(parameterType, new HashSet<Type>());
        string description = documentation.GetParameterDescription(parameterName);
        if (!string.IsNullOrWhiteSpace(description)) {
          parameterSchema["description"] = description;
        }
        properties[parameterName] = parameterSchema;
        if (parameter.IsRequired || this.IsPathParameter(parameter) || documentation.IsRequiredContractParameter(parameterName)) {
          required.Add(parameterName);
        }
      }

      schema["properties"] = properties;
      if (required.Count > 0) {
        schema["required"] = required;
      }
      schema["additionalProperties"] = false;
      return schema;
    }

    public JObject GenerateSchema(Type type) {
      return this.GenerateSchema(type, new HashSet<Type>());
    }

    private JObject GenerateSchema(Type type, HashSet<Type> visitedTypes) {
      Type effectiveType = Nullable.GetUnderlyingType(type);
      if (effectiveType == null) {
        effectiveType = type;
      }

      if (effectiveType == typeof(string) || effectiveType == typeof(Guid) || effectiveType == typeof(DateTime) || effectiveType == typeof(DateTimeOffset)) {
        JObject stringSchema = new JObject();
        stringSchema["type"] = "string";
        if (effectiveType == typeof(DateTime) || effectiveType == typeof(DateTimeOffset)) {
          stringSchema["format"] = "date-time";
        }
        return stringSchema;
      }

      if (effectiveType == typeof(bool)) {
        JObject booleanSchema = new JObject();
        booleanSchema["type"] = "boolean";
        return booleanSchema;
      }

      if (this.IsIntegerType(effectiveType)) {
        JObject integerSchema = new JObject();
        integerSchema["type"] = "integer";
        return integerSchema;
      }

      if (this.IsNumberType(effectiveType)) {
        JObject numberSchema = new JObject();
        numberSchema["type"] = "number";
        return numberSchema;
      }

      if (effectiveType.IsEnum) {
        JObject enumSchema = new JObject();
        enumSchema["type"] = "string";
        JArray enumValues = new JArray();
        string[] names = Enum.GetNames(effectiveType);
        foreach (string name in names) {
          enumValues.Add(name);
        }
        enumSchema["enum"] = enumValues;
        return enumSchema;
      }

      Type itemType = this.TryGetEnumerableItemType(effectiveType);
      if (itemType != null) {
        JObject arraySchema = new JObject();
        arraySchema["type"] = "array";
        arraySchema["items"] = this.GenerateSchema(itemType, visitedTypes);
        return arraySchema;
      }

      if (visitedTypes.Contains(effectiveType)) {
        JObject recursiveSchema = new JObject();
        recursiveSchema["type"] = "object";
        return recursiveSchema;
      }

      visitedTypes.Add(effectiveType);

      JObject objectSchema = new JObject();
      objectSchema["type"] = "object";
      JObject properties = new JObject();
      JArray requiredProperties = new JArray();

      PropertyInfo[] publicProperties = effectiveType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
      foreach (PropertyInfo property in publicProperties) {
        if (!property.CanRead || property.GetIndexParameters().Length > 0) {
          continue;
        }

        string propertyName = this.GetJsonPropertyName(property);
        JObject propertySchema = this.GenerateSchema(property.PropertyType, visitedTypes);
        if (_DocumentationProvider != null) {
          string propertyDescription = _DocumentationProvider.GetPropertySummary(property);
          if (!string.IsNullOrWhiteSpace(propertyDescription)) {
            propertySchema["description"] = propertyDescription;
          }
        }
        properties[propertyName] = propertySchema;
        if (property.GetCustomAttribute<RequiredAttribute>() != null) {
          requiredProperties.Add(propertyName);
        }
      }

      objectSchema["properties"] = properties;
      if (requiredProperties.Count > 0) {
        objectSchema["required"] = requiredProperties;
      }
      objectSchema["additionalProperties"] = false;
      visitedTypes.Remove(effectiveType);
      return objectSchema;
    }

    private bool ShouldSkipParameter(ApiParameterDescription parameter) {
      if (parameter.Source == null) {
        return false;
      }
      if (parameter.Source.CanAcceptDataFrom(BindingSource.Services)) {
        return true;
      }
      if (parameter.Source.CanAcceptDataFrom(BindingSource.Special)) {
        return true;
      }
      return false;
    }

    private void ThrowIfUnsupportedParameter(ApiParameterDescription parameter) {
      if (parameter.Source == null) {
        return;
      }
      if (parameter.Source.CanAcceptDataFrom(BindingSource.Form) || parameter.Source.CanAcceptDataFrom(BindingSource.FormFile)) {
        throw new NotSupportedException("MCP facade tool generation currently supports JSON, route and query parameters only.");
      }
    }

    private bool IsPathParameter(ApiParameterDescription parameter) {
      if (parameter.Source == null) {
        return false;
      }
      return parameter.Source.CanAcceptDataFrom(BindingSource.Path);
    }

    private bool IsBodyParameter(ApiParameterDescription parameter) {
      if (parameter.Source == null) {
        return false;
      }
      return parameter.Source.CanAcceptDataFrom(BindingSource.Body);
    }

    private string GetParameterName(ApiParameterDescription parameter) {
      if (!string.IsNullOrWhiteSpace(parameter.Name)) {
        return parameter.Name;
      }
      return "value";
    }

    private string GetJsonPropertyName(PropertyInfo property) {
      JsonPropertyAttribute newtonsoftAttribute = property.GetCustomAttribute<JsonPropertyAttribute>();
      if (newtonsoftAttribute != null && !string.IsNullOrWhiteSpace(newtonsoftAttribute.PropertyName)) {
        return newtonsoftAttribute.PropertyName;
      }

      JsonPropertyNameAttribute systemTextJsonAttribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
      if (systemTextJsonAttribute != null && !string.IsNullOrWhiteSpace(systemTextJsonAttribute.Name)) {
        return systemTextJsonAttribute.Name;
      }

      return property.Name;
    }

    private bool IsIntegerType(Type type) {
      return type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) || type == typeof(sbyte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong);
    }

    private bool IsNumberType(Type type) {
      return type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    private Type TryGetEnumerableItemType(Type type) {
      if (type == typeof(string)) {
        return null;
      }

      if (type.IsArray) {
        return type.GetElementType();
      }

      Type enumerableType = type.GetInterfaces().Concat(new Type[] { type }).FirstOrDefault((candidate) => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
      if (enumerableType == null) {
        return null;
      }
      return enumerableType.GetGenericArguments()[0];
    }

  }

}
