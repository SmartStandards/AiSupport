using System;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Ambient context of the tool call which is currently executed on this thread (set by 'HarnessToolbox'
  /// around 'IHarnessTool.Invoke'). Tools use it to raise interactive requests of the second line of defense
  /// (guards) without changing the 'IHarnessTool' signature.
  /// </summary>
  public class HarnessToolContext {

    [ThreadStatic]
    private static HarnessToolContext _Current;

    private readonly HarnessToolCall _Call;

    private readonly HarnessSource _Source;

    private readonly HarnessScope _Scope;

    public HarnessToolContext(HarnessToolCall call, HarnessSource source, HarnessScope scope) {
      _Call = call;
      _Source = source;
      _Scope = scope;
    }

    /// <summary>
    /// The context of the tool call which is currently executed on this thread (null outside of a tool call).
    /// </summary>
    public static HarnessToolContext Current {
      get {
        return _Current;
      }
    }

    public HarnessToolCall Call {
      get {
        return _Call;
      }
    }

    public HarnessSource Source {
      get {
        return _Source;
      }
    }

    public HarnessScope Scope {
      get {
        return _Scope;
      }
    }

    /// <summary>
    /// Asks the user (via the interaction requester of the scope) and returns one of the 'ToolApprovalDecisions'.
    /// Outside of a tool call or without requester, 'Deny' is returned.
    /// </summary>
    public static string RequestInteraction(string kind, string subject) {

      HarnessToolContext context = _Current;

      if (context == null || context.Scope == null || context.Scope.InteractionRequester == null) {
        return ToolApprovalDecisions.Deny;
      }

      HarnessInteractionRequest request = new HarnessInteractionRequest();
      request.Kind = kind;
      request.Subject = subject;
      request.Call = context.Call;
      request.Source = context.Source;

      string decision = context.Scope.InteractionRequester.Invoke(request);

      if (string.IsNullOrWhiteSpace(decision)) {
        return ToolApprovalDecisions.Deny;
      }

      return decision;
    }

    /// <summary>
    /// Sets the ambient context for the current thread and returns the previous one (to be restored afterwards).
    /// </summary>
    internal static HarnessToolContext Enter(HarnessToolContext context) {

      HarnessToolContext previous = _Current;
      _Current = context;

      return previous;
    }

    internal static void Restore(HarnessToolContext previous) {
      _Current = previous;
    }

  }

}
