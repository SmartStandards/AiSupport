using System;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Manages harness profiles (PRF-1..12). A profile bundles everything that can be configured within the settings
  /// (default provider/model, API keys/tokens, capability opt-outs, skill and tool sources incl. their levels and
  /// settings). The theme and the user instructions are NOT part of a profile. Exactly one profile is active;
  /// the chat history is scoped by the profile.
  /// </summary>
#if NET_FX
  [ServiceContract()]
#endif
  public interface IPromptingProfileService {

    /// <summary>
    /// Returns all profiles (the active one is flagged via 'IsActive').
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    HarnessProfileInfo[] GetProfiles();

    /// <summary>
    /// Returns the id of the active profile.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string GetActiveProfileId();

    /// <summary>
    /// Creates a new profile as copy of the given profile (empty = the active profile), including the protected
    /// API keys and source tokens. Returns the id of the new profile.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    string CreateProfile(string copyFromProfileId, string displayName);

    /// <summary>
    /// Renames the given profile.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void RenameProfile(string profileId, string displayName);

    /// <summary>
    /// Deletes the given profile incl. its chats. The active profile and the default profile cannot be deleted.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void DeleteProfile(string profileId);

    /// <summary>
    /// Activates the given profile (it is applied to the running client and remembered for the next start).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void ActivateProfile(string profileId);

    /// <summary>
    /// Returns true if the client should ask for the profile at start (only relevant with more than one profile).
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    bool GetAskAtStartup();

    /// <summary>
    /// Enables or disables the profile selection at start.
    /// </summary>
#if NET_FX
    [OperationContract(), WebInvoke(Method = "POST")]
#endif
    void SetAskAtStartup(bool enabled);

  }

  /// <summary>
  /// Describes a harness profile.
  /// </summary>
  public class HarnessProfileInfo {

    private string _ProfileId;
    private string _DisplayName;
    private bool _IsActive;
    private bool _IsDefault;
    private string _CreatedUtc;

    /// <summary>
    /// Generated, stable id of the profile.
    /// </summary>
    public string ProfileId {
      get {
        return _ProfileId;
      }
      set {
        _ProfileId = value;
      }
    }

    /// <summary>
    /// Free text name of the profile.
    /// </summary>
    public string DisplayName {
      get {
        return _DisplayName;
      }
      set {
        _DisplayName = value;
      }
    }

    /// <summary>
    /// True for the currently active profile.
    /// </summary>
    public bool IsActive {
      get {
        return _IsActive;
      }
      set {
        _IsActive = value;
      }
    }

    /// <summary>
    /// True for the default profile (the configuration of installations without profiles, cannot be deleted).
    /// </summary>
    public bool IsDefault {
      get {
        return _IsDefault;
      }
      set {
        _IsDefault = value;
      }
    }

    /// <summary>
    /// Creation time (UTC, ISO 8601).
    /// </summary>
    public string CreatedUtc {
      get {
        return _CreatedUtc;
      }
      set {
        _CreatedUtc = value;
      }
    }

  }

}
