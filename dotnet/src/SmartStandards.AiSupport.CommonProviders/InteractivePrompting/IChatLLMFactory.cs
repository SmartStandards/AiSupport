using AI.SmartStandards.LowLevelPrompting;
using System;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Creates the 'ICommonLLM' instances for the chats of a 'CheapPromptingUIBackend' (per provider and model,
  /// because every chat keeps its own provider/model selection).
  /// </summary>
  public interface IChatLLMFactory {

    /// <summary>
    /// Returns the name of the default provider (used as snapshot for new chats) or null if there is none.
    /// </summary>
    string GetDefaultProviderName();

    /// <summary>
    /// Returns the model configured for the given provider (or its default model) or null if unknown.
    /// </summary>
    string GetDefaultModel(string providerName);

    /// <summary>
    /// Returns true if the given provider can be created by this factory.
    /// </summary>
    bool IsKnownProvider(string providerName);

    /// <summary>
    /// Creates an LLM for the given provider and model. A null/empty provider selects the default provider,
    /// a null/empty model selects the model configured for the provider.
    /// </summary>
    ICommonLLM CreateLLM(string providerName, string model);

  }

  /// <summary>
  /// Adapter for the legacy 'Func&lt;ICommonLLM&gt;' factory (no per-chat provider/model selection).
  /// </summary>
  internal sealed class DelegateChatLLMFactory : IChatLLMFactory {

    private readonly Func<ICommonLLM> _Factory;

    public DelegateChatLLMFactory(Func<ICommonLLM> factory) {

      if (factory == null) {
        throw new ArgumentNullException(nameof(factory));
      }

      _Factory = factory;

    }

    /// <summary>
    /// The legacy factory has no provider names.
    /// </summary>
    public string GetDefaultProviderName() {
      return null;
    }

    /// <summary>
    /// The legacy factory has no model names.
    /// </summary>
    public string GetDefaultModel(string providerName) {
      return null;
    }

    /// <summary>
    /// The legacy factory cannot select providers.
    /// </summary>
    public bool IsKnownProvider(string providerName) {
      return false;
    }

    /// <summary>
    /// Ignores provider and model and invokes the legacy factory.
    /// </summary>
    public ICommonLLM CreateLLM(string providerName, string model) {
      return _Factory.Invoke();
    }

  }

}
