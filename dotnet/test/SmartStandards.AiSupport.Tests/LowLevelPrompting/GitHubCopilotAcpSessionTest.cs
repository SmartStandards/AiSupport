using AI.SmartStandards.InteractivePrompting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;

namespace AI.SmartStandards.LowLevelPrompting {

  [TestClass()]
  public class GitHubCopilotAcpSessionTest {

    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(10);

    [TestMethod()]
    public void AcpSession_HandshakePromptAndPermissionRefusal() {

      using (FakeAgent agent = new FakeAgent(true)) {

        GitHubCopilotAcpSession session = agent.CreateClient();

        session.Initialize(_Timeout);
        Assert.IsTrue(session.SupportsLoadSession);

        string sessionId = session.CreateSession("C:\\temp", _Timeout);
        Assert.AreEqual("sess-1", sessionId);

        string answer = session.Prompt("Hello", _Timeout);
        Assert.AreEqual("Hello back", answer);

        //the agent asked for a permission and must have received a refusal
        JObject permissionResponse = agent.ReceivedMessages.Single((m) => (string)m["id"] == "perm-1");
        Assert.AreEqual("cancelled", (string)permissionResponse["result"]["outcome"]["outcome"]);

        session.Dispose();
        Assert.IsFalse(session.IsAlive);

      }

    }

    [TestMethod()]
    public void AcpSession_LoadSession_IgnoresReplayAndReportsUnknownIds() {

      using (FakeAgent agent = new FakeAgent(true)) {

        GitHubCopilotAcpSession session = agent.CreateClient();
        session.Initialize(_Timeout);

        Assert.IsFalse(session.TryLoadSession("unknown", "C:\\temp", _Timeout));
        Assert.IsTrue(session.TryLoadSession("sess-1", "C:\\temp", _Timeout));
        Assert.AreEqual("sess-1", session.SessionId);

        //the replayed history must not leak into the next answer
        Assert.AreEqual("Next back", session.Prompt("Next", _Timeout));

        session.Dispose();

      }

    }

    [TestMethod()]
    public void AcpSession_WithoutLoadSupport_DoesNotTryToLoad() {

      using (FakeAgent agent = new FakeAgent(false)) {

        GitHubCopilotAcpSession session = agent.CreateClient();
        session.Initialize(_Timeout);

        Assert.IsFalse(session.SupportsLoadSession);
        Assert.IsFalse(session.TryLoadSession("sess-1", "C:\\temp", _Timeout));
        Assert.IsFalse(agent.ReceivedMessages.Any((m) => (string)m["method"] == "session/load"));

        session.Dispose();

      }

    }

    [TestMethod()]
    public void AcpSession_AgentTerminates_PendingRequestFailsWithIOException() {

      using (FakeAgent agent = new FakeAgent(true)) {

        GitHubCopilotAcpSession session = agent.CreateClient();
        session.Initialize(_Timeout);
        session.CreateSession("C:\\temp", _Timeout);

        agent.DieOnNextPrompt = true;

        Assert.ThrowsException<IOException>(() => session.Prompt("Hello", _Timeout));
        Assert.IsFalse(session.IsAlive);

        session.Dispose();

      }

    }

    [TestMethod()]
    public void AcpSession_Prompt_ReportsProgressAndHonorsCancellation() {

      using (FakeAgent agent = new FakeAgent(true)) {

        GitHubCopilotAcpSession session = agent.CreateClient();
        session.Initialize(_Timeout);
        session.CreateSession("C:\\temp", _Timeout);

        List<string> progressTexts = new List<string>();

        string answer = session.Prompt(
          "Again", _Timeout, CancellationToken.None, (text, current, total) => progressTexts.Add(text)
        );

        Assert.AreEqual("Again back", answer);
        Assert.IsTrue(progressTexts.Count >= 2);
        Assert.AreEqual("waiting for GitHub Copilot", progressTexts[0]);
        Assert.AreEqual("Again", progressTexts[1]);

        using (CancellationTokenSource cancellation = new CancellationTokenSource()) {

          cancellation.Cancel();

          Assert.ThrowsException<OperationCanceledException>(
            () => session.Prompt("Cancelled", _Timeout, cancellation.Token, null)
          );

        }

        session.Dispose();

      }

    }

    [TestMethod()]
    public void AcpSession_EmptyTurnAfterRefusedNativeTool_ThrowsCleanStopException() {

      using (FakeAgent agent = new FakeAgent(true)) {

        GitHubCopilotAcpSession session = agent.CreateClient();
        session.Initialize(_Timeout);
        session.CreateSession("C:\\temp", _Timeout);

        agent.EmptyTurnOnNextPrompt = true;

        GitHubCopilotTurnStoppedException ex = Assert.ThrowsException<GitHubCopilotTurnStoppedException>(
          () => session.Prompt("Use a tool", _Timeout)
        );

        Assert.IsTrue(ex.Message.StartsWith(GitHubCopilotTurnStoppedException.MessagePrefix));
        Assert.AreEqual("end_turn", ex.StopReason);
        CollectionAssert.AreEqual(new string[] { "Run shell" }, ex.RefusedNativeTools);

        //the session stays usable
        Assert.AreEqual("Next back", session.Prompt("Next", _Timeout));

        session.Dispose();

      }

    }

    [TestMethod()]
    public void Copilot_AppendWrapperToolReminder_IsLastPartOfThePrompt() {

      string prompt = GitHubCopilotCliConnector.AppendWrapperToolReminder("history");

      Assert.IsTrue(prompt.StartsWith("history"));
      Assert.IsTrue(prompt.EndsWith(GitHubCopilotCliConnector.WrapperToolReminder.TrimEnd()));

    }

    [TestMethod()]
    public void AcpSession_GetLastNonEmptyLine() {

      Assert.AreEqual("second", GitHubCopilotAcpSession.GetLastNonEmptyLine("first\nsecond\n\n"));
      Assert.IsNull(GitHubCopilotAcpSession.GetLastNonEmptyLine("\n \n"));
      Assert.IsNull(GitHubCopilotAcpSession.GetLastNonEmptyLine(null));

    }

    [TestMethod()]
    public void ContextStore_SessionIdRoundTrip() {

      Assert.AreEqual("abc", GitHubCopilotCliConnector.GetSessionIdFromContextStore(GitHubCopilotCliConnector.SessionContextStorePrefix + "abc"));
      Assert.IsNull(GitHubCopilotCliConnector.GetSessionIdFromContextStore("resp_123"));
      Assert.IsNull(GitHubCopilotCliConnector.GetSessionIdFromContextStore(null));

    }

    [TestMethod()]
    public void Copilot_UnknownContextStore_RequestsOuterHistory() {

      GitHubCopilotCliConnector connector = new GitHubCopilotCliConnector();
      string contextStore = "resp_from_another_provider";

      ChatTurn[] turns = new ChatTurn[] { new ChatTurn(ChatRoles.User, "Hi") };

      Assert.ThrowsException<ContextStoreInvalidException>(
        () => connector.CallChatApi(CancellationToken.None, turns, ref contextStore, null)
      );

    }

    [TestMethod()]
    public void SettingsService_ExposesCapabilitiesAndPersistsOptOuts() {

      string directory = Path.Combine(Path.GetTempPath(), "AiSupportTests", Guid.NewGuid().ToString("N"));

      try {

        FileBasedPromptingSettingsService service = new FileBasedPromptingSettingsService(Path.Combine(directory, "settings.json"));

        LLMProviderInfo copilot = service.GetAvailableProviders().Single((p) => p.Name == GitHubCopilotCliConnector.ProviderName);
        LLMProviderInfo mock = service.GetAvailableProviders().Single((p) => p.Name == StupidMockLLM.ProviderName);

        Assert.IsTrue(copilot.Capabilities.All((c) => c.IsAvailable));
        Assert.IsTrue(mock.Capabilities.All((c) => !c.IsAvailable || c.Name == LLMCapabilityNames.PausableToolCalls));
        Assert.IsTrue(mock.Capabilities.Single((c) => c.Name == LLMCapabilityNames.PausableToolCalls).IsAvailable);
        Assert.AreEqual(LLMCapabilityNames.GetAll().Length, mock.Capabilities.Length);
        Assert.IsTrue(copilot.KnownModels.Contains(GitHubCopilotCliConnector.DefaultModelPlaceholder));

        //defaults: everything which is available is used
        service.SaveSettings(GitHubCopilotCliConnector.ProviderName, "", null);
        GitHubCopilotCliConnector connector = (GitHubCopilotCliConnector)service.CreateConfiguredLLM();
        Assert.IsTrue(connector.UseSessionMode);
        Assert.IsTrue(connector.KeepSessionAlive);
        Assert.IsTrue(connector.AllowSessionResume);

        service.SaveDisabledCapabilities(GitHubCopilotCliConnector.ProviderName, new string[] { LLMCapabilityNames.SessionResume });

        CollectionAssert.AreEqual(
          new string[] { LLMCapabilityNames.SessionResume },
          service.GetDisabledCapabilities(GitHubCopilotCliConnector.ProviderName)
        );

        connector = (GitHubCopilotCliConnector)service.CreateConfiguredLLM();
        Assert.IsTrue(connector.UseSessionMode);
        Assert.IsTrue(connector.KeepSessionAlive);
        Assert.IsFalse(connector.AllowSessionResume);

      }
      finally {

        if (Directory.Exists(directory)) {
          Directory.Delete(directory, true);
        }

      }

    }

    /// <summary>
    /// Minimal in-process ACP agent connected via anonymous pipes.
    /// </summary>
    private sealed class FakeAgent : IDisposable {

      private readonly bool _SupportsLoad;

      private readonly AnonymousPipeServerStream _ToClient = new AnonymousPipeServerStream(PipeDirection.Out);

      private readonly AnonymousPipeServerStream _FromClient = new AnonymousPipeServerStream(PipeDirection.In);

      private readonly AnonymousPipeClientStream _ClientInput;

      private readonly AnonymousPipeClientStream _ClientOutput;

      private readonly StreamWriter _Writer;

      private readonly Thread _Thread;

      private readonly List<JObject> _ReceivedMessages = new List<JObject>();

      private volatile bool _DieOnNextPrompt = false;

      private volatile bool _EmptyTurnOnNextPrompt = false;

      public bool EmptyTurnOnNextPrompt {
        get {
          return _EmptyTurnOnNextPrompt;
        }
        set {
          _EmptyTurnOnNextPrompt = value;
        }
      }

      public FakeAgent(bool supportsLoad) {

        _SupportsLoad = supportsLoad;

        _ClientInput = new AnonymousPipeClientStream(PipeDirection.In, _ToClient.ClientSafePipeHandle);
        _ClientOutput = new AnonymousPipeClientStream(PipeDirection.Out, _FromClient.ClientSafePipeHandle);

        _Writer = new StreamWriter(_ToClient, new UTF8Encoding(false));
        _Writer.AutoFlush = true;

        _Thread = new Thread(this.Run);
        _Thread.IsBackground = true;
        _Thread.Start();

      }

      public bool DieOnNextPrompt {
        get {
          return _DieOnNextPrompt;
        }
        set {
          _DieOnNextPrompt = value;
        }
      }

      public JObject[] ReceivedMessages {
        get {
          lock (_ReceivedMessages) {
            return _ReceivedMessages.ToArray();
          }
        }
      }

      public GitHubCopilotAcpSession CreateClient() {

        StreamReader reader = new StreamReader(_ClientInput, Encoding.UTF8);
        StreamWriter writer = new StreamWriter(_ClientOutput, new UTF8Encoding(false));

        return new GitHubCopilotAcpSession(reader, writer, null, null);
      }

      private void Run() {

        try {

          using (StreamReader reader = new StreamReader(_FromClient, Encoding.UTF8)) {

            while (true) {

              string line = reader.ReadLine();

              if (line == null) {
                return;
              }

              JObject message = JObject.Parse(line);

              lock (_ReceivedMessages) {
                _ReceivedMessages.Add(message);
              }

              this.Handle(message);
            }

          }

        }
        catch (IOException) {
          // the client went away
        }
        catch (ObjectDisposedException) {
          // the agent was disposed
        }

      }

      private void Handle(JObject message) {

        string method = (string)message["method"];
        JToken id = message["id"];

        if (method == null || id == null) {
          return;
        }

        if (method == "initialize") {
          this.Respond(id, new JObject(new JProperty("protocolVersion", 1), new JProperty("agentCapabilities", new JObject(new JProperty("loadSession", _SupportsLoad)))));
        }
        else if (method == "session/new") {
          this.Respond(id, new JObject(new JProperty("sessionId", "sess-1")));
        }
        else if (method == "session/load") {

          if ((string)message["params"]["sessionId"] != "sess-1") {
            this.RespondError(id, -32602, "Unknown session");
            return;
          }

          //replay of the history (must be ignored by the client)
          this.SendChunk("sess-1", "OLD HISTORY ");
          this.Respond(id, new JObject());
        }
        else if (method == "session/prompt") {

          if (_DieOnNextPrompt) {
            _ToClient.Dispose();
            return;
          }

          string text = (string)message["params"]["prompt"][0]["text"];

          if (_EmptyTurnOnNextPrompt) {

            _EmptyTurnOnNextPrompt = false;

            JObject toolCall = new JObject(new JProperty("toolCallId", "tc-1"), new JProperty("title", "Run shell"), new JProperty("kind", "execute"));

            this.Send(new JObject(
              new JProperty("jsonrpc", "2.0"), new JProperty("id", "perm-2"), new JProperty("method", "session/request_permission"),
              new JProperty("params", new JObject(new JProperty("sessionId", "sess-1"), new JProperty("toolCall", toolCall)))
            ));

            //give the client the chance to answer the permission request before the turn ends
            Thread.Sleep(200);

            this.Respond(id, new JObject(new JProperty("stopReason", "end_turn")));
            return;
          }

          this.Send(new JObject(
            new JProperty("jsonrpc", "2.0"), new JProperty("id", "perm-1"), new JProperty("method", "session/request_permission"),
            new JProperty("params", new JObject(new JProperty("sessionId", "sess-1")))
          ));

          this.SendChunk("sess-1", text);
          this.SendChunk("sess-1", " back");
          this.Respond(id, new JObject(new JProperty("stopReason", "end_turn")));
        }

      }

      private void SendChunk(string sessionId, string text) {

        JObject content = new JObject(new JProperty("type", "text"), new JProperty("text", text));
        JObject update = new JObject(new JProperty("sessionUpdate", "agent_message_chunk"), new JProperty("content", content));

        this.Send(new JObject(
          new JProperty("jsonrpc", "2.0"), new JProperty("method", "session/update"),
          new JProperty("params", new JObject(new JProperty("sessionId", sessionId), new JProperty("update", update)))
        ));

      }

      private void Respond(JToken id, JObject result) {
        this.Send(new JObject(new JProperty("jsonrpc", "2.0"), new JProperty("id", id), new JProperty("result", result)));
      }

      private void RespondError(JToken id, int code, string text) {
        JObject error = new JObject(new JProperty("code", code), new JProperty("message", text));
        this.Send(new JObject(new JProperty("jsonrpc", "2.0"), new JProperty("id", id), new JProperty("error", error)));
      }

      private void Send(JObject message) {
        _Writer.WriteLine(message.ToString(Newtonsoft.Json.Formatting.None));
      }

      public void Dispose() {

        try {
          _ToClient.Dispose();
        }
        catch (IOException) {
          // pipe already broken
        }

        _FromClient.Dispose();
        _ClientInput.Dispose();
        _ClientOutput.Dispose();

      }

    }

  }

}
