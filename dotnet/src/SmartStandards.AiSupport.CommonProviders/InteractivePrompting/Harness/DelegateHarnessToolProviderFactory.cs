using System;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Simple 'IHarnessToolProviderFactory' which delegates the provider creation to a function
  /// (convenient for hosts which do not want to implement an own factory class).
  /// </summary>
  public class DelegateHarnessToolProviderFactory : IHarnessToolProviderFactory {

    private readonly string _SourceId;

    private readonly Func<string, IHarnessToolProvider> _ProviderCreator;

    /// <summary>
    /// Creates the factory.
    /// </summary>
    /// <param name="sourceId">Stable id of the tool source (equal to the 'ProviderId' of the created providers).</param>
    /// <param name="providerCreator">Creates the provider for the given profile id.</param>
    public DelegateHarnessToolProviderFactory(string sourceId, Func<string, IHarnessToolProvider> providerCreator) {

      if (string.IsNullOrWhiteSpace(sourceId)) {
        throw new ArgumentNullException(nameof(sourceId));
      }

      if (providerCreator == null) {
        throw new ArgumentNullException(nameof(providerCreator));
      }

      _SourceId = sourceId;
      _ProviderCreator = providerCreator;

    }

    public string SourceId {
      get {
        return _SourceId;
      }
    }

    /// <summary>
    /// Creates the provider for the given profile.
    /// </summary>
    public IHarnessToolProvider CreateProvider(string profileId) {

      return _ProviderCreator.Invoke(profileId);
    }

  }

}
