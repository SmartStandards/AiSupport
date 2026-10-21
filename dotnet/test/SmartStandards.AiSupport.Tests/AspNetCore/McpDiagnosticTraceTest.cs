using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AI.SmartStandards {

  [TestClass()]
  [DoNotParallelize]
  public class McpDiagnosticTraceTest {

    private const string _Secret = "sensitive-value-must-not-appear";
    private StringWriter _Output;
    private TextWriterTraceListener _Listener;

    /// <summary>
    /// Captures the existing DevLogger trace transport without adding a logging dependency.
    /// </summary>
    [TestInitialize()]
    public void Initialize() {
      _Output = new StringWriter();
      _Listener = new TextWriterTraceListener(_Output, "McpDiagnosticTraceTest");
      Trace.Listeners.Add(_Listener);
    }

    /// <summary>
    /// Releases only the trace listener owned by this test.
    /// </summary>
    [TestCleanup()]
    public void Cleanup() {
      Trace.Listeners.Remove(_Listener);
      _Listener.Dispose();
      _Output.Dispose();
    }

    /// <summary>
    /// Diagnostic tracing must remain opt-in.
    /// </summary>
    [TestMethod()]
    public void DiagnosticTracingIsDisabledByDefault() {
      Assert.IsFalse(new McpFacadeOptions().EnableDiagnosticTracing);
    }

    /// <summary>
    /// Disabled tracing must not allocate request state, register callbacks or emit events.
    /// </summary>
    [TestMethod()]
    public void DisabledTracingDoesNotRegisterOrEmit() {
      TestResponseFeature response = new TestResponseFeature();
      DefaultHttpContext context = CreateContext(response);
      using (CancellationTokenSource cancellation = new CancellationTokenSource()) {
        context.RequestAborted = cancellation.Token;
        BeginRequest(context, false);
        cancellation.Cancel();
        response.Start();
        response.Complete();
        Assert.AreEqual(0, context.Items.Count);
        Assert.AreEqual(0, response.CallbackCount);
        Assert.AreEqual(string.Empty, _Output.ToString());
      }
    }

    /// <summary>
    /// Every phase of one request uses the same server-generated correlation and no client secrets.
    /// </summary>
    [TestMethod()]
    public void LifecycleIsCorrelatedAndDoesNotLogRequestData() {
      TestResponseFeature response = new TestResponseFeature();
      DefaultHttpContext context = CreateContext(response);
      context.TraceIdentifier = _Secret;
      context.Request.Headers.Authorization = "Bearer " + _Secret;
      context.Request.Headers.Cookie = "session=" + _Secret;
      context.Request.QueryString = new QueryString("?code=" + _Secret);
      context.Request.Path = "/" + _Secret;
      context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(_Secret));
      BeginRequest(context, true);
      BeginRequest(context, true);
      response.StatusCode = StatusCodes.Status401Unauthorized;
      response.Start();
      response.Complete();

      string output = _Output.ToString();
      StringAssert.Contains(output, "McpRequestStarted");
      StringAssert.Contains(output, "McpResponseStarting");
      StringAssert.Contains(output, "McpRequestCompleted");
      StringAssert.Contains(output, "httpStatus=401");
      StringAssert.Contains(output, "elapsedMs=");
      Assert.IsFalse(output.Contains(_Secret, StringComparison.Ordinal));
      MatchCollection correlations = Regex.Matches(output, "request=([a-f0-9]{32})");
      Assert.AreEqual(3, correlations.Count);
      foreach (Match correlation in correlations) {
        Assert.AreEqual(correlations[0].Groups[1].Value, correlation.Groups[1].Value);
      }
    }

    /// <summary>
    /// Independent requests must never share diagnostic correlation.
    /// </summary>
    [TestMethod()]
    public void SeparateRequestsHaveSeparateCorrelation() {
      BeginRequest(CreateContext(new TestResponseFeature()), true);
      BeginRequest(CreateContext(new TestResponseFeature()), true);
      MatchCollection correlations = Regex.Matches(_Output.ToString(), "request=([a-f0-9]{32})");
      Assert.AreEqual(2, correlations.Count);
      Assert.AreNotEqual(correlations[0].Groups[1].Value, correlations[1].Groups[1].Value);
    }

    /// <summary>
    /// Client cancellation is observable independently from response completion.
    /// </summary>
    [TestMethod()]
    public void AbortedRequestIsRecorded() {
      TestResponseFeature response = new TestResponseFeature();
      DefaultHttpContext context = CreateContext(response);
      using (CancellationTokenSource cancellation = new CancellationTokenSource()) {
        context.RequestAborted = cancellation.Token;
        BeginRequest(context, true);
        cancellation.Cancel();
        StringAssert.Contains(_Output.ToString(), "McpRequestAborted");
        response.Complete();
      }
    }

    /// <summary>
    /// Completed requests release the cancellation observer.
    /// </summary>
    [TestMethod()]
    public void CompletionReleasesAbortRegistration() {
      TestResponseFeature response = new TestResponseFeature();
      DefaultHttpContext context = CreateContext(response);
      using (CancellationTokenSource cancellation = new CancellationTokenSource()) {
        context.RequestAborted = cancellation.Token;
        BeginRequest(context, true);
        response.Complete();
        cancellation.Cancel();
        Assert.IsFalse(_Output.ToString().Contains("McpRequestAborted", StringComparison.Ordinal));
      }
    }

    /// <summary>
    /// Rejections remain HTTP 401 and expose challenge presence rather than challenge content.
    /// </summary>
    [TestMethod()]
    public void AuthenticationRejectionIsTracedWithoutChallengeContents() {
      DefaultHttpContext context = CreateContext(new TestResponseFeature());
      McpFacadeOptions options = new McpFacadeOptions();
      options.RequestAuthenticator = (request) => {
        request.Response.Headers.WWWAuthenticate = "Bearer " + _Secret;
        return false;
      };
      BeginRequest(context, true);
      bool authenticated = Authenticate(context, options);

      Assert.IsFalse(authenticated);
      Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
      StringAssert.Contains(_Output.ToString(), "McpAuthenticationStarted");
      StringAssert.Contains(_Output.ToString(), "McpAuthenticationCompleted");
      StringAssert.Contains(_Output.ToString(), "challengePresent=True");
      Assert.IsFalse(_Output.ToString().Contains(_Secret, StringComparison.Ordinal));
    }

    /// <summary>
    /// Existing localhost bypass behavior remains visible and unchanged.
    /// </summary>
    [TestMethod()]
    public void LocalhostBypassIsExplicitlyTraced() {
      DefaultHttpContext context = CreateContext(new TestResponseFeature());
      context.Connection.RemoteIpAddress = IPAddress.Loopback;
      McpFacadeOptions options = new McpFacadeOptions();
      bool invoked = false;
      options.RequestAuthenticator = (request) => {
        invoked = true;
        return false;
      };
      options.SkipRequestAuthenticatorForLocalhost = true;
      BeginRequest(context, true);

      Assert.IsTrue(Authenticate(context, options));
      Assert.IsFalse(invoked);
      StringAssert.Contains(_Output.ToString(), "McpAuthenticationBypassed");
    }

    /// <summary>
    /// A real protocol request is traced through response writing without leaking its JSON-RPC id or body.
    /// </summary>
    [TestMethod()]
    public void InitializeIsTracedThroughResponseWriting() {
      DefaultHttpContext context = CreateContext(new TestResponseFeature());
      context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
        "{\"jsonrpc\":\"2.0\",\"id\":\"" + _Secret + "\",\"method\":\"initialize\",\"params\":{}}"));
      BeginRequest(context, true);
      HandleProtocol(context);

      StringAssert.Contains(_Output.ToString(), "McpRequestBodyRead");
      StringAssert.Contains(_Output.ToString(), "method=initialize");
      StringAssert.Contains(_Output.ToString(), "McpResponseWritten");
      Assert.IsFalse(_Output.ToString().Contains(_Secret, StringComparison.Ordinal));
      context.Response.Body.Position = 0;
      using (StreamReader reader = new StreamReader(context.Response.Body, Encoding.UTF8, true, 1024, true)) {
        JObject reply = JObject.Parse(reader.ReadToEnd());
        Assert.AreEqual(_Secret, reply.Value<string>("id"));
      }
    }

    /// <summary>
    /// Unknown method names are untrusted and must not become diagnostic content.
    /// </summary>
    [TestMethod()]
    public void UnsupportedMethodIsClassifiedWithoutLoggingItsValue() {
      DefaultHttpContext context = CreateContext(new TestResponseFeature());
      context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + _Secret + "\"}"));
      BeginRequest(context, true);
      HandleProtocol(context);
      StringAssert.Contains(_Output.ToString(), "method=unsupported");
      Assert.IsFalse(_Output.ToString().Contains(_Secret, StringComparison.Ordinal));
    }

    /// <summary>
    /// Parse failures record the category, not exception messages containing payload fragments.
    /// </summary>
    [TestMethod()]
    public void MalformedRequestIsTracedWithoutPayloadFragments() {
      DefaultHttpContext context = CreateContext(new TestResponseFeature());
      context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"" + _Secret + "\": invalid}"));
      BeginRequest(context, true);
      HandleProtocol(context);
      Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
      StringAssert.Contains(_Output.ToString(), "category=parse");
      StringAssert.Contains(_Output.ToString(), "McpResponseWritten");
      Assert.IsFalse(_Output.ToString().Contains(_Secret, StringComparison.Ordinal));
    }

    /// <summary>
    /// Tests internal tracing without widening the production library's public API.
    /// </summary>
    private static void BeginRequest(HttpContext context, bool enabled) {
      Type traceType = typeof(McpFacadeOptions).Assembly.GetType("AI.SmartStandards.McpDiagnosticTrace", true);
      MethodInfo begin = traceType.GetMethod("BeginRequest", BindingFlags.Static | BindingFlags.NonPublic);
      begin.Invoke(null, new object[] { context, enabled });
    }

    /// <summary>
    /// Exercises the existing authenticator boundary without starting a web server.
    /// </summary>
    private static bool Authenticate(HttpContext context, McpFacadeOptions options) {
      MethodInfo authenticate = typeof(McpEndpointRouteBuilderExtensions).GetMethod("Authenticate", BindingFlags.Static | BindingFlags.NonPublic);
      return (bool)authenticate.Invoke(null, new object[] { context, options });
    }

    /// <summary>
    /// Exercises protocol handling directly; initialize and unsupported methods require no catalog or tool invoker.
    /// </summary>
    private static void HandleProtocol(HttpContext context) {
      Type handlerType = typeof(McpFacadeOptions).Assembly.GetType("AI.SmartStandards.McpProtocolHandler", true);
      object handler = Activator.CreateInstance(handlerType, new object[] { Options.Create(new McpFacadeOptions()), null, null });
      MethodInfo handle = handlerType.GetMethod("HandleAsync");
      Task operation = (Task)handle.Invoke(handler, new object[] { context });
      operation.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Creates an in-memory HTTP context with controllable transport lifecycle callbacks.
    /// </summary>
    private static DefaultHttpContext CreateContext(TestResponseFeature response) {
      DefaultHttpContext context = new DefaultHttpContext();
      context.Features.Set<IHttpResponseFeature>(response);
      context.Request.Method = "POST";
      context.Response.Body = new MemoryStream();
      return context;
    }

    private sealed class TestResponseFeature : HttpResponseFeature {

      private readonly List<KeyValuePair<Func<object, Task>, object>> _Starting = new List<KeyValuePair<Func<object, Task>, object>>();
      private readonly List<KeyValuePair<Func<object, Task>, object>> _Completed = new List<KeyValuePair<Func<object, Task>, object>>();

      internal int CallbackCount {
        get {
          return _Starting.Count + _Completed.Count;
        }
      }

      /// <summary>
      /// Retains header-start callbacks for explicit test invocation.
      /// </summary>
      public override void OnStarting(Func<object, Task> callback, object state) {
        _Starting.Add(new KeyValuePair<Func<object, Task>, object>(callback, state));
      }

      /// <summary>
      /// Retains completion callbacks for explicit test invocation.
      /// </summary>
      public override void OnCompleted(Func<object, Task> callback, object state) {
        _Completed.Add(new KeyValuePair<Func<object, Task>, object>(callback, state));
      }

      /// <summary>
      /// Simulates header transmission without running an ASP.NET Core host.
      /// </summary>
      internal void Start() {
        foreach (KeyValuePair<Func<object, Task>, object> callback in _Starting) {
          callback.Key.Invoke(callback.Value).GetAwaiter().GetResult();
        }
      }

      /// <summary>
      /// Simulates completion without running an ASP.NET Core host.
      /// </summary>
      internal void Complete() {
        foreach (KeyValuePair<Func<object, Task>, object> callback in _Completed) {
          callback.Key.Invoke(callback.Value).GetAwaiter().GetResult();
        }
      }

    }

  }

}
