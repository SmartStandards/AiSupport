using System;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// A registered skill source or tool source of the harness toolbox. The level is the global default
  /// (see 'SkillSourceLevels' / 'ToolSourceLevels'), which can be overridden per chat.
  /// </summary>
  public class HarnessSource {

    private readonly string _SourceId;

    private readonly bool _IsToolSource;

    private readonly string _DisplayName;

    private readonly string _DefaultLevel;

    private readonly bool _IsFixed;

    /// <summary>
    /// Creates a source descriptor.
    /// </summary>
    /// <param name="sourceId">Stable id of the source (also used as key for per-chat overrides).</param>
    /// <param name="isToolSource">True for a tool source ('ToolSourceLevels'), false for a skill source ('SkillSourceLevels').</param>
    /// <param name="displayName">Name shown in the UI and within control messages.</param>
    /// <param name="defaultLevel">Global default level ('SkillSourceLevels' or 'ToolSourceLevels').</param>
    /// <param name="isFixed">True if the level cannot be changed (for example built-in skills).</param>
    public HarnessSource(string sourceId, bool isToolSource, string displayName, string defaultLevel, bool isFixed) {

      if (string.IsNullOrWhiteSpace(sourceId)) {
        throw new ArgumentNullException(nameof(sourceId));
      }

      if (string.IsNullOrWhiteSpace(defaultLevel)) {
        throw new ArgumentNullException(nameof(defaultLevel));
      }

      _SourceId = sourceId.Trim();
      _IsToolSource = isToolSource;
      _DefaultLevel = defaultLevel;
      _IsFixed = isFixed;

      if (string.IsNullOrWhiteSpace(displayName)) {
        _DisplayName = _SourceId;
      }
      else {
        _DisplayName = displayName.Trim();
      }

    }

    public string SourceId {
      get {
        return _SourceId;
      }
    }

    public bool IsToolSource {
      get {
        return _IsToolSource;
      }
    }

    public string DisplayName {
      get {
        return _DisplayName;
      }
    }

    public string DefaultLevel {
      get {
        return _DefaultLevel;
      }
    }

    public bool IsFixed {
      get {
        return _IsFixed;
      }
    }

  }

}
