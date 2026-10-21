using Logging.SmartStandards.CopyForAI.SmartStandards;
using Microsoft.AspNetCore.Http;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AI.SmartStandards {

  // User-approved range for MCP diagnostic tracing: 75300-75399 (75.3xx).
  internal enum MyLogEventKind {
    McpRequestStarted = 75300,
    McpResponseStarting = 75301,
    McpRequestCompleted = 75302,
    McpRequestAborted = 75303,
    McpAuthenticationStarted = 75310,
    McpAuthenticationCompleted = 75311,
    McpAuthenticationBypassed = 75312,
    McpBrowserLoginStarted = 75313,
    McpBrowserLoginCompleted = 75314,
    McpRequestBodyReading = 75320,
    McpRequestBodyRead = 75321,
    McpProtocolDispatch = 75322,
    McpNotificationAccepted = 75323,
    McpProtocolFailure = 75324,
    McpResponseWriting = 75325,
    McpResponseWritten = 75326,
    McpToolResolved = 75330,
    McpToolPreparing = 75331,
    McpTargetAuthorizationStarted = 75332,
    McpTargetAuthorizationCompleted = 75333,
    McpTargetInvocationStarted = 75334,
    McpTargetInvocationCompleted = 75335,
    McpToolResultReading = 75336,
    McpToolCompleted = 75337,
    McpToolFailure = 75338
  }

  /// <summary>
  /// Emits opt-in, metadata-only diagnostics. Request data and exception messages must never be passed as details.
  /// </summary>
  internal static class McpDiagnosticTrace {

    private static readonly object _RequestStateKey = new object();

    /// <summary>
    /// Starts correlation and registers response lifecycle diagnostics only when tracing is enabled.
    /// </summary>
    internal static void BeginRequest(HttpContext context, bool enabled) {
      if (!enabled || context.Items.ContainsKey(_RequestStateKey)) {
        return;
      }

      RequestState state = new RequestState(context);
      context.Items[_RequestStateKey] = state;
      string method = "other";
      if (HttpMethods.IsPost(context.Request.Method)) {
        method = "POST";
      } else if (HttpMethods.IsGet(context.Request.Method)) {
        method = "GET";
      }
      Write(context, 2097980883285345896L, MyLogEventKind.McpRequestStarted,
        "method=" + method + "; authorizationPresent=" + context.Request.Headers.ContainsKey("Authorization"));
      context.Response.OnStarting(state.ResponseStarting);
      context.Response.OnCompleted(state.ResponseCompleted);
      state.RegisterAbort();
    }

    /// <summary>
    /// Writes a phase with the request correlation, elapsed milliseconds and current HTTP response status.
    /// Only server-owned metadata is allowed in details; never pass headers, bodies, query strings or exception messages.
    /// </summary>
    internal static void Write(HttpContext context, long sourceLineId, MyLogEventKind eventKind, string details = "") {
      object value;
      if (!context.Items.TryGetValue(_RequestStateKey, out value)) {
        return;
      }
      RequestState state = (RequestState)value;
      DevLogger.LogTrace(sourceLineId, (int)eventKind,
        "MCP request={RequestId}; stage={Stage}; elapsedMs={ElapsedMs}; httpStatus={HttpStatus}; responseStarted={ResponseStarted}; {Details}",
        state.RequestId, eventKind.ToString(), state.ElapsedMilliseconds, context.Response.StatusCode,
        context.Response.HasStarted, details);
    }

    private sealed class RequestState {

      private readonly HttpContext _Context;
      private readonly string _RequestId;
      private readonly Stopwatch _Stopwatch;
      private CancellationTokenRegistration _AbortRegistration;

      /// <summary>
      /// Creates a server-generated identifier instead of trusting client-supplied correlation values.
      /// </summary>
      internal RequestState(HttpContext context) {
        _Context = context;
        _RequestId = Guid.NewGuid().ToString("N");
        _Stopwatch = Stopwatch.StartNew();
      }

      internal string RequestId {
        get {
          return _RequestId;
        }
      }

      internal long ElapsedMilliseconds {
        get {
          return _Stopwatch.ElapsedMilliseconds;
        }
      }

      /// <summary>
      /// Observes a disconnected or canceled request without changing cancellation handling.
      /// </summary>
      internal void RegisterAbort() {
        _AbortRegistration = _Context.RequestAborted.Register(this.RequestAborted);
      }

      /// <summary>
      /// Records the final HTTP status when the response headers are about to be sent.
      /// </summary>
      internal Task ResponseStarting() {
        Write(_Context, 2097980883286000176L, MyLogEventKind.McpResponseStarting);
        return Task.CompletedTask;
      }

      /// <summary>
      /// Records transport completion and releases the cancellation registration.
      /// </summary>
      internal Task ResponseCompleted() {
        _AbortRegistration.Dispose();
        Write(_Context, 2097980883286790423L, MyLogEventKind.McpRequestCompleted);
        return Task.CompletedTask;
      }

      /// <summary>
      /// Records client disconnects independently from successful response completion.
      /// </summary>
      private void RequestAborted() {
        Write(_Context, 2097980883287112136L, MyLogEventKind.McpRequestAborted);
      }

    }

  }

}
