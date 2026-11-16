using Newtonsoft.Json.Linq;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// A tool which is executed by the Level-2 harness itself (in-process), when the model requests it.
  /// Exceptions thrown by 'Invoke' do not stop the loop: they are returned to the model as error result.
  /// </summary>
  public interface IHarnessTool {

    /// <summary>
    /// Unique tool name (letters, digits and underscores).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Description for the model (what the tool does and when to use it).
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON schema of the arguments object.
    /// </summary>
    string ParametersJsonSchema { get; }

    /// <summary>
    /// Executes the tool and returns the result text for the model.
    /// </summary>
    string Invoke(JObject arguments);

  }

}
