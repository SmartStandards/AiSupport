namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// SRV-1: catalog entry of a host-contributed tool source. The host registers its factories once (for example
  /// at the 'HarnessProfileManager'); every profile runtime asks each factory for its OWN provider instance, which
  /// is registered as built-in (fixed, not removable) tool source. Level and settings stay configurable per profile.
  /// </summary>
  public interface IHarnessToolProviderFactory {

    /// <summary>
    /// Stable id of the tool source. Must be equal to the 'ProviderId' of the created providers.
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Creates the provider for the given profile (called once per profile runtime).
    /// </summary>
    /// <param name="profileId">Id of the profile whose runtime is being created.</param>
    IHarnessToolProvider CreateProvider(string profileId);

  }

}
