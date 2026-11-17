using System;
using System.Collections.Generic;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Per-chat view on a harness toolbox: the per-chat level overrides of skill/tool sources and the
  /// (optional) approver for tools of sources with level 'RequiresConfirmation'.
  /// </summary>
  public class HarnessScope {

    private readonly IDictionary<string, string> _SkillLevelOverrides;

    private readonly IDictionary<string, string> _ToolLevelOverrides;

    private readonly Func<HarnessToolCall, HarnessSource, bool> _ToolApprover;

    private readonly Action<HarnessToolExecution> _OnToolExecuted;

    private Func<HarnessInteractionRequest, string> _InteractionRequester;

    /// <summary>
    /// Creates a scope.
    /// </summary>
    /// <param name="skillLevelOverrides">Skill source id -> level (can only raise the global default). Optional.</param>
    /// <param name="toolLevelOverrides">Tool source id -> level (replaces the global default). Optional.</param>
    /// <param name="toolApprover">
    /// Is asked before a tool of a source with level 'RequiresConfirmation' is executed. Optional:
    /// without approver, such calls are rejected.
    /// </param>
    public HarnessScope(
      IDictionary<string, string> skillLevelOverrides, IDictionary<string, string> toolLevelOverrides,
      Func<HarnessToolCall, HarnessSource, bool> toolApprover
    ) : this(skillLevelOverrides, toolLevelOverrides, toolApprover, null) {
    }

    /// <summary>
    /// Creates a scope with an observer, which is notified after every tool call (executed or rejected),
    /// for example to maintain an audit log.
    /// </summary>
    /// <param name="skillLevelOverrides">Skill source id -> level (can only raise the global default). Optional.</param>
    /// <param name="toolLevelOverrides">Tool source id -> level (replaces the global default). Optional.</param>
    /// <param name="toolApprover">Is asked for tools of sources with level 'RequiresConfirmation'. Optional.</param>
    /// <param name="onToolExecuted">Is notified after every tool call. Optional.</param>
    public HarnessScope(
      IDictionary<string, string> skillLevelOverrides, IDictionary<string, string> toolLevelOverrides,
      Func<HarnessToolCall, HarnessSource, bool> toolApprover, Action<HarnessToolExecution> onToolExecuted
    ) {

      if (skillLevelOverrides == null) {
        _SkillLevelOverrides = new Dictionary<string, string>();
      }
      else {
        _SkillLevelOverrides = skillLevelOverrides;
      }

      if (toolLevelOverrides == null) {
        _ToolLevelOverrides = new Dictionary<string, string>();
      }
      else {
        _ToolLevelOverrides = toolLevelOverrides;
      }

      _ToolApprover = toolApprover;
      _OnToolExecuted = onToolExecuted;

    }

    /// <summary>
    /// Returns a scope without overrides and without approver (global defaults only).
    /// </summary>
    public static HarnessScope CreateDefault() {

      return new HarnessScope(null, null, null);
    }

    public IDictionary<string, string> SkillLevelOverrides {
      get {
        return _SkillLevelOverrides;
      }
    }

    public IDictionary<string, string> ToolLevelOverrides {
      get {
        return _ToolLevelOverrides;
      }
    }

    public Func<HarnessToolCall, HarnessSource, bool> ToolApprover {
      get {
        return _ToolApprover;
      }
    }

    /// <summary>
    /// Optional observer, which is notified after every tool call (executed or rejected).
    /// </summary>
    public Action<HarnessToolExecution> OnToolExecuted {
      get {
        return _OnToolExecuted;
      }
    }

    /// <summary>
    /// Optional channel for interactive requests of running tools (second line of defense, for example a path
    /// which is not within the allow-list). Returns one of the 'ToolApprovalDecisions'. Without requester,
    /// such requests are denied.
    /// </summary>
    public Func<HarnessInteractionRequest, string> InteractionRequester {
      get {
        return _InteractionRequester;
      }
      set {
        _InteractionRequester = value;
      }
    }

    private Action<string, int, int> _ToolProgressReporter;

    /// <summary>
    /// Optional sink for progress reports of running tools (statusText, current, max), see
    /// 'HarnessToolContext.ReportProgress' (PRG-3). Without reporter, progress reports are ignored.
    /// </summary>
    public Action<string, int, int> ToolProgressReporter {
      get {
        return _ToolProgressReporter;
      }
      set {
        _ToolProgressReporter = value;
      }
    }

  }

  /// <summary>
  /// An interactive request of a running tool (see 'HarnessScope.InteractionRequester').
  /// </summary>
  public class HarnessInteractionRequest {

    private string _Kind;
    private string _Subject;
    private HarnessToolCall _Call;
    private HarnessSource _Source;

    /// <summary>
    /// See 'PendingInteractionKinds'.
    /// </summary>
    public string Kind {
      get {
        return _Kind;
      }
      set {
        _Kind = value;
      }
    }

    /// <summary>
    /// The path, domain or command line.
    /// </summary>
    public string Subject {
      get {
        return _Subject;
      }
      set {
        _Subject = value;
      }
    }

    /// <summary>
    /// The tool call which raised the request.
    /// </summary>
    public HarnessToolCall Call {
      get {
        return _Call;
      }
      set {
        _Call = value;
      }
    }

    /// <summary>
    /// The tool source of the tool (may be null).
    /// </summary>
    public HarnessSource Source {
      get {
        return _Source;
      }
      set {
        _Source = value;
      }
    }

  }

  /// <summary>
  /// Outcome of a single tool call (reported to 'HarnessScope.OnToolExecuted').
  /// </summary>
  public class HarnessToolExecution {

    private HarnessToolCall _Call;
    private string _SourceId;
    private bool _Executed;
    private bool _RequiredConfirmation;
    private bool _Success;
    private string _ErrorMessage;
    private long _DurationMs;
    private int _ResultSize;

    public HarnessToolCall Call {
      get {
        return _Call;
      }
      set {
        _Call = value;
      }
    }

    /// <summary>
    /// The tool source of the tool (null for unknown tools or tools without source).
    /// </summary>
    public string SourceId {
      get {
        return _SourceId;
      }
      set {
        _SourceId = value;
      }
    }

    /// <summary>
    /// True if the tool implementation was invoked.
    /// </summary>
    public bool Executed {
      get {
        return _Executed;
      }
      set {
        _Executed = value;
      }
    }

    /// <summary>
    /// True if the tool source required a confirmation (the approver was asked).
    /// </summary>
    public bool RequiredConfirmation {
      get {
        return _RequiredConfirmation;
      }
      set {
        _RequiredConfirmation = value;
      }
    }

    public bool Success {
      get {
        return _Success;
      }
      set {
        _Success = value;
      }
    }

    public string ErrorMessage {
      get {
        return _ErrorMessage;
      }
      set {
        _ErrorMessage = value;
      }
    }

    public long DurationMs {
      get {
        return _DurationMs;
      }
      set {
        _DurationMs = value;
      }
    }

    public int ResultSize {
      get {
        return _ResultSize;
      }
      set {
        _ResultSize = value;
      }
    }

  }

}
