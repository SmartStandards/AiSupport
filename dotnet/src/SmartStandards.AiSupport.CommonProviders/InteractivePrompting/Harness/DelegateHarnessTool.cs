using Newtonsoft.Json.Linq;
using System;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Simple tool implementation based on a delegate (used by the built-in providers).
  /// </summary>
  public class DelegateHarnessTool : IHarnessTool {

    private readonly string _Name;

    private readonly string _Description;

    private readonly string _ParametersJsonSchema;

    private readonly Func<JObject, string> _Invoker;

    public DelegateHarnessTool(string name, string description, string parametersJsonSchema, Func<JObject, string> invoker) {

      if (string.IsNullOrWhiteSpace(name)) {
        throw new ArgumentNullException(nameof(name));
      }

      if (invoker == null) {
        throw new ArgumentNullException(nameof(invoker));
      }

      _Name = name;
      _Description = description;
      _ParametersJsonSchema = parametersJsonSchema;
      _Invoker = invoker;

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

    public string Invoke(JObject arguments) {

      if (arguments == null) {
        arguments = new JObject();
      }

      return _Invoker.Invoke(arguments);
    }

    /// <summary>
    /// Returns a required string argument or throws an 'ArgumentException' (returned to the model as tool error).
    /// </summary>
    public static string GetRequiredString(JObject arguments, string name) {

      string value = GetOptionalString(arguments, name);

      if (string.IsNullOrEmpty(value)) {
        throw new ArgumentException($"The argument '{name}' is required.");
      }

      return value;
    }

    /// <summary>
    /// Returns an optional string argument or null.
    /// </summary>
    public static string GetOptionalString(JObject arguments, string name) {

      JToken token = arguments[name];

      if (token == null || token.Type == JTokenType.Null) {
        return null;
      }

      return token.ToString();
    }

  }

}
