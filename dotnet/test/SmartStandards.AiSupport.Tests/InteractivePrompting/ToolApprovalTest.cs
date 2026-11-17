using AI.SmartStandards.InteractivePrompting.Harness;
using AI.SmartStandards.LowLevelPrompting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class ToolApprovalTest {

    private string _TempDirectory;

    [TestInitialize()]
    public void Setup() {

      _TempDirectory = Path.Combine(Path.GetTempPath(), "AiSupportTests", Guid.NewGuid().ToString("N"));

    }

    [TestCleanup()]
    public void Cleanup() {

      if (Directory.Exists(_TempDirectory)) {
        Directory.Delete(_TempDirectory, true);
      }

    }

    [TestMethod()]
    public void SendMessage_WaitsForApprovalAndExecutesApprovedTool() {

      CountingTool tool = new CountingTool();
      ScriptedLLM llm = new ScriptedLLM();
      CheapPromptingUIBackend backend = this.CreateBackend(tool, llm);
      string chatId = backend.BeginNewChat("Test");

      Task<ChatMessage> sending = Task.Run(() => backend.SendMessage(CancellationToken.None, chatId, "go"));

      PendingToolApproval approval = WaitForApproval(backend, chatId);
      Assert.AreEqual("count_up", approval.ToolName);
      Assert.AreEqual("confirm", approval.SourceId);
      StringAssert.Contains(approval.ArgumentsJson, "\"step\": 2");
      Assert.AreEqual(0, tool.InvocationCount);

      //an approval id cannot be answered via another chat
      backend.AnswerToolApproval("other-chat", approval.ApprovalId, ToolApprovalDecisions.AllowOnce);
      Assert.AreEqual(1, backend.GetPendingToolApprovals(chatId).Length);

      backend.AnswerToolApproval(chatId, approval.ApprovalId, ToolApprovalDecisions.AllowOnce);

      Assert.IsTrue(sending.Wait(TimeSpan.FromSeconds(10)));
      Assert.AreEqual(1, tool.InvocationCount);
      StringAssert.Contains(llm.LastToolResponse, "counted 2");
      Assert.AreEqual(0, backend.GetPendingToolApprovals(chatId).Length);

      ToolAuditEntry[] log = backend.GetToolAuditLog(chatId);
      Assert.AreEqual(1, log.Length);
      Assert.AreEqual("count_up", log[0].ToolName);
      Assert.AreEqual("confirm", log[0].ToolSourceId);
      Assert.AreEqual(ToolApprovalDecisions.AllowOnce, log[0].Decision);
      Assert.IsTrue(log[0].Success);
      Assert.AreEqual("counted 2".Length, log[0].ResultSize);

      //'AllowOnce' is not remembered
      Task<ChatMessage> secondSending = Task.Run(() => backend.SendMessage(CancellationToken.None, chatId, "again"));
      PendingToolApproval secondApproval = WaitForApproval(backend, chatId);
      backend.AnswerToolApproval(chatId, secondApproval.ApprovalId, ToolApprovalDecisions.AllowOnce);
      Assert.IsTrue(secondSending.Wait(TimeSpan.FromSeconds(10)));
      Assert.AreEqual(2, tool.InvocationCount);

    }

    [TestMethod()]
    public void SendMessage_AllowForChatIsPersistedAndCanBeRevoked() {

      CountingTool tool = new CountingTool();
      ScriptedLLM llm = new ScriptedLLM();
      CheapPromptingUIBackend backend = this.CreateBackend(tool, llm);
      string chatId = backend.BeginNewChat("Test");

      Task<ChatMessage> sending = Task.Run(() => backend.SendMessage(CancellationToken.None, chatId, "go"));
      PendingToolApproval approval = WaitForApproval(backend, chatId);
      backend.AnswerToolApproval(chatId, approval.ApprovalId, ToolApprovalDecisions.AllowForChat);
      Assert.IsTrue(sending.Wait(TimeSpan.FromSeconds(10)));
      Assert.AreEqual(1, tool.InvocationCount);

      ChatSourceState state = backend.GetChatDetails(chatId).ToolSources.Single();
      Assert.IsTrue(state.IsApprovedForChat);

      //a new backend instance (restart) still knows the approval: no confirmation required
      CheapPromptingUIBackend restartedBackend = this.CreateBackend(tool, llm);
      restartedBackend.ToolApprovalTimeout = TimeSpan.FromMilliseconds(200);
      restartedBackend.SendMessage(CancellationToken.None, chatId, "again");
      Assert.AreEqual(2, tool.InvocationCount);

      ToolAuditEntry[] log = restartedBackend.GetToolAuditLog(chatId);
      Assert.AreEqual(2, log.Length);
      Assert.AreEqual(ToolApprovalDecisions.AllowForChat, log[0].Decision);
      Assert.AreEqual(ToolApprovalDecisions.Preapproved, log[1].Decision);

      //setting the level explicitly revokes the approval
      restartedBackend.SetChatToolSourceLevel(chatId, "confirm", ToolSourceLevels.RequiresConfirmation);
      Assert.IsFalse(restartedBackend.GetChatDetails(chatId).ToolSources.Single().IsApprovedForChat);

      restartedBackend.SendMessage(CancellationToken.None, chatId, "third");
      Assert.AreEqual(2, tool.InvocationCount);

    }

    [TestMethod()]
    public void BuildArgumentSummary_MasksSensitiveValues() {

      JObject arguments = JObject.Parse("{\"user\":\"bob\",\"Password\":\"p@ss\",\"nested\":{\"apiKey\":\"k\"}}");
      string summary = CheapPromptingUIBackend.BuildArgumentSummary(new HarnessToolCall("x", arguments, null));

      StringAssert.Contains(summary, "bob");
      Assert.IsFalse(summary.Contains("p@ss"));
      Assert.IsFalse(summary.Contains("\"k\""));

    }

    [TestMethod()]
    public void SendMessage_DeniedToolIsNotExecuted() {

      CountingTool tool = new CountingTool();
      ScriptedLLM llm = new ScriptedLLM();
      CheapPromptingUIBackend backend = this.CreateBackend(tool, llm);
      string chatId = backend.BeginNewChat("Test");

      Task<ChatMessage> sending = Task.Run(() => backend.SendMessage(CancellationToken.None, chatId, "go"));

      PendingToolApproval approval = WaitForApproval(backend, chatId);
      backend.AnswerToolApproval(chatId, approval.ApprovalId, ToolApprovalDecisions.Deny);

      Assert.IsTrue(sending.Wait(TimeSpan.FromSeconds(10)));
      Assert.AreEqual(0, tool.InvocationCount);
      StringAssert.Contains(llm.LastToolResponse, "denied by the user");

      //'Deny' hides the source for this chat and informs the model via a control message
      ChatDetails details = backend.GetChatDetails(chatId);
      Assert.AreEqual(ToolSourceLevels.Hidden, details.ToolSources.Single().EffectiveLevel);
      Assert.IsTrue(
        backend.GetChatMessages(chatId, 0, details.MessageCount - 1).Any((m) => m.Kind == ChatMessageKinds.Control)
      );

      ToolAuditEntry[] log = backend.GetToolAuditLog(chatId);
      Assert.AreEqual(1, log.Length);
      Assert.AreEqual(ToolApprovalDecisions.Deny, log[0].Decision);
      Assert.IsFalse(log[0].Success);

    }

    [TestMethod()]
    public void SendMessage_UnansweredApprovalIsDeniedAfterTimeout() {

      CountingTool tool = new CountingTool();
      ScriptedLLM llm = new ScriptedLLM();
      CheapPromptingUIBackend backend = this.CreateBackend(tool, llm);
      backend.ToolApprovalTimeout = TimeSpan.FromMilliseconds(200);
      string chatId = backend.BeginNewChat("Test");

      backend.SendMessage(CancellationToken.None, chatId, "go");

      Assert.AreEqual(0, tool.InvocationCount);
      StringAssert.Contains(llm.LastToolResponse, "denied by the user");
      Assert.AreEqual(0, backend.GetPendingToolApprovals(chatId).Length);

      //a timeout does not hide the source
      Assert.AreEqual(
        ToolSourceLevels.RequiresConfirmation, backend.GetChatDetails(chatId).ToolSources.Single().EffectiveLevel
      );

    }

    private CheapPromptingUIBackend CreateBackend(CountingTool tool, ScriptedLLM llm) {

      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddSource(new HarnessSource("confirm", true, "Confirmed tools", ToolSourceLevels.RequiresConfirmation, false));
      toolbox.AddTool(tool, "confirm");

      return new CheapPromptingUIBackend(
        new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats")),
        () => llm, toolbox, () => "Scripted", "Tester"
      );
    }

    private static PendingToolApproval WaitForApproval(CheapPromptingUIBackend backend, string chatId) {

      DateTime timeout = DateTime.UtcNow.AddSeconds(10);

      while (DateTime.UtcNow < timeout) {

        PendingToolApproval[] approvals = backend.GetPendingToolApprovals(chatId);

        if (approvals.Length > 0) {
          return approvals[0];
        }

        Thread.Sleep(20);
      }

      Assert.Fail("No pending tool approval appeared.");
      return null;
    }

    private sealed class CountingTool : IHarnessTool {

      private int _InvocationCount;

      public int InvocationCount {
        get {
          return _InvocationCount;
        }
      }

      public string Name {
        get {
          return "count_up";
        }
      }

      public string Description {
        get {
          return "Counts up.";
        }
      }

      public string ParametersJsonSchema {
        get {
          return "{\"type\":\"object\",\"properties\":{\"step\":{\"type\":\"integer\"}}}";
        }
      }

      public string Invoke(JObject arguments) {

        Interlocked.Increment(ref _InvocationCount);
        return "counted " + arguments["step"].ToString();
      }

    }

    /// <summary>
    /// Fake LLM: requests the tool 'count_up' for every user message, answers plain text after a tool response.
    /// </summary>
    private sealed class ScriptedLLM : ICommonLLM {

      private int _CallCount;

      private string _LastToolResponse;

      public string LastToolResponse {
        get {
          return _LastToolResponse;
        }
      }

      public string CallChatApi(
        CancellationToken cancellationToken, ChatTurn[] newMessages, ref string contextStore,
        Action<string, int, int> onProgress
      ) {

        _CallCount++;
        contextStore = "ctx-" + _CallCount.ToString();

        string lastContent = newMessages.Last().Content;

        if (!lastContent.Contains("<tool_response>")) {
          return "<tool_call>{\"name\":\"count_up\",\"arguments\":{\"step\":2}}</tool_call>";
        }

        _LastToolResponse = lastContent;
        return "done";
      }

      public string CallWebSearchApi(string prompt, object inputData = null) {
        throw new NotSupportedException();
      }

      public T CallWebSearchApi<T>(string prompt, object inputData = null) where T : class {
        throw new NotSupportedException();
      }

      public byte[] CallImageEditApi(byte[] inputImageBytes, string prompt, byte[] maskImageBytes = null) {
        throw new NotSupportedException();
      }

      public byte[] CallImageGeneratorApi(string prompt) {
        throw new NotSupportedException();
      }

    }

  }

}
