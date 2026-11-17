using AI.SmartStandards.InteractivePrompting.Harness;
using AI.SmartStandards.LowLevelPrompting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class ToolGuardTest {

    private string _TempDirectory;

    [TestInitialize()]
    public void Setup() {

      _TempDirectory = Path.Combine(Path.GetTempPath(), "AiSupportTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(_TempDirectory);

    }

    [TestCleanup()]
    public void Cleanup() {

      if (Directory.Exists(_TempDirectory)) {
        Directory.Delete(_TempDirectory, true);
      }

    }

    [TestMethod()]
    public void PathGuard_MatchesRecursiveAndRespectsAccess() {

      JArray allowed = JArray.Parse("[{\"path\":\"C:\\\\Data\",\"access\":\"RO\"},{\"path\":\"C:\\\\Work\",\"access\":\"RW\"}]");

      Assert.IsTrue(HarnessGuards.IsPathAllowed(allowed, @"C:\Data\sub\file.txt", false));
      Assert.IsFalse(HarnessGuards.IsPathAllowed(allowed, @"C:\Data\sub\file.txt", true));
      Assert.IsTrue(HarnessGuards.IsPathAllowed(allowed, @"c:\work\x.txt", true));
      Assert.IsFalse(HarnessGuards.IsPathAllowed(allowed, @"C:\DataOther\file.txt", false));

    }

    [TestMethod()]
    public void DomainAndShellGuard_MatchPatterns() {

      JArray domains = JArray.Parse("[\"example.com\",\"*.github.com\"]");

      Assert.IsTrue(HarnessGuards.IsDomainAllowed(domains, "example.com"));
      Assert.IsTrue(HarnessGuards.IsDomainAllowed(domains, "api.github.com"));
      Assert.IsFalse(HarnessGuards.IsDomainAllowed(domains, "evil-example.com"));

      Assert.IsTrue(HarnessGuards.MatchesCommandPattern("git status", "git status"));
      Assert.IsFalse(HarnessGuards.MatchesCommandPattern("git status", "git status && del *"));
      Assert.IsTrue(HarnessGuards.MatchesCommandPattern("dir *", "dir C:\\temp"));
      Assert.IsTrue(HarnessGuards.MatchesCommandPattern("regex:git (status|log)", "git log"));
      Assert.IsFalse(HarnessGuards.MatchesCommandPattern("regex:git (status|log)", "git push"));

    }

    [TestMethod()]
    public void SendMessage_GuardRequestAllowPermanentlyExtendsAllowList() {

      string fileFullName = Path.Combine(_TempDirectory, "data.txt");
      File.WriteAllText(fileFullName, "secret content");

      string settingsJson = "{\"allowedPaths\":[]}";
      ScriptedLLM llm = new ScriptedLLM(fileFullName);
      CheapPromptingUIBackend backend = this.CreateBackend(llm, () => settingsJson, (json) => { settingsJson = json; });
      string chatId = backend.BeginNewChat("Test");

      Task<ChatMessage> sending = Task.Run(() => backend.SendMessage(CancellationToken.None, chatId, "go"));

      PendingToolApproval request = WaitForRequest(backend, chatId);
      Assert.AreEqual(PendingInteractionKinds.PathAccess, request.Kind);
      StringAssert.Contains(request.Subject, fileFullName);

      backend.AnswerToolApproval(chatId, request.ApprovalId, ToolApprovalDecisions.AllowPermanently);

      Assert.IsTrue(sending.Wait(TimeSpan.FromSeconds(10)));
      StringAssert.Contains(llm.LastToolResponse, "secret content");
      StringAssert.Contains(settingsJson, "data.txt");

    }

    [TestMethod()]
    public void SendMessage_GuardRequestDenyBlocksTool() {

      string fileFullName = Path.Combine(_TempDirectory, "data.txt");
      File.WriteAllText(fileFullName, "secret content");

      string settingsJson = "{\"allowedPaths\":[]}";
      ScriptedLLM llm = new ScriptedLLM(fileFullName);
      CheapPromptingUIBackend backend = this.CreateBackend(llm, () => settingsJson, (json) => { settingsJson = json; });
      string chatId = backend.BeginNewChat("Test");

      Task<ChatMessage> sending = Task.Run(() => backend.SendMessage(CancellationToken.None, chatId, "go"));

      PendingToolApproval request = WaitForRequest(backend, chatId);
      backend.AnswerToolApproval(chatId, request.ApprovalId, ToolApprovalDecisions.Deny);

      Assert.IsTrue(sending.Wait(TimeSpan.FromSeconds(10)));
      Assert.IsFalse(llm.LastToolResponse.Contains("secret content"));
      StringAssert.Contains(llm.LastToolResponse, "denied");
      Assert.IsFalse(settingsJson.Contains("data.txt"));

    }

    [TestMethod()]
    public void ExtensionService_PersistsToolSourceLevelAndSettings() {

      FileBasedPromptingExtensionService service = new FileBasedPromptingExtensionService(
        Path.Combine(_TempDirectory, "extensions.json"),
        Path.Combine(_TempDirectory, "user", "SKILL.md"),
        Path.Combine(_TempDirectory, "system", "SKILL.md")
      );

      service.RegisterOutOfTheBoxToolProviders();
      HarnessToolbox toolbox = new HarnessToolbox();
      service.AttachToolbox(toolbox);

      ToolSourceInfo fileSystem = service.GetToolSources().Single((s) => s.SourceId == FileSystemToolProvider.Id);
      Assert.AreEqual(ToolSourceLevels.RequiresConfirmation, fileSystem.Level);
      CollectionAssert.Contains(fileSystem.ToolNames, "fs_read_file", "Broken: " + fileSystem.BrokenReason + " Tools: " + string.Join(",", fileSystem.ToolNames));

      service.SetToolSourceLevel(FileSystemToolProvider.Id, ToolSourceLevels.NoConfirmation);
      service.SaveToolProviderSettings(FileSystemToolProvider.Id, "{\"allowedPaths\":[{\"path\":\"C:\\\\Data\",\"access\":\"RO\"}]}");

      Assert.ThrowsException<ArgumentException>(() => {
        service.SaveToolProviderSettings(FileSystemToolProvider.Id, "{\"allowedPaths\":[{\"path\":\"C:\\\\Data\",\"access\":\"XX\"}]}");
      });

      FileBasedPromptingExtensionService reloaded = new FileBasedPromptingExtensionService(
        Path.Combine(_TempDirectory, "extensions.json"),
        Path.Combine(_TempDirectory, "user", "SKILL.md"),
        Path.Combine(_TempDirectory, "system", "SKILL.md")
      );

      reloaded.RegisterOutOfTheBoxToolProviders();

      ToolSourceInfo reloadedInfo = reloaded.GetToolSources().Single((s) => s.SourceId == FileSystemToolProvider.Id);
      Assert.AreEqual(ToolSourceLevels.NoConfirmation, reloadedInfo.Level);
      StringAssert.Contains(reloaded.GetToolProviderSettings(FileSystemToolProvider.Id), "C:\\\\Data");

      Assert.ThrowsException<InvalidOperationException>(() => reloaded.RemoveToolSource(FileSystemToolProvider.Id));

    }

    private CheapPromptingUIBackend CreateBackend(ScriptedLLM llm, Func<string> settingsGetter, Action<string> settingsSaver) {

      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddSource(new HarnessSource("fs", true, "File system", ToolSourceLevels.NoConfirmation, false));

      ToolProviderContext context = new ToolProviderContext("fs", null, settingsGetter, () => null, settingsSaver);

      foreach (IHarnessTool tool in new FileSystemToolProvider().CreateTools(context)) {
        toolbox.AddTool(tool, "fs");
      }

      return new CheapPromptingUIBackend(
        new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats")),
        () => llm, toolbox, () => "Scripted", "Tester"
      );
    }

    private static PendingToolApproval WaitForRequest(CheapPromptingUIBackend backend, string chatId) {

      DateTime timeout = DateTime.UtcNow.AddSeconds(10);

      while (DateTime.UtcNow < timeout) {

        PendingToolApproval[] requests = backend.GetPendingToolApprovals(chatId);

        if (requests.Length > 0) {
          return requests[0];
        }

        Thread.Sleep(20);
      }

      Assert.Fail("No pending guard request appeared.");
      return null;
    }

    /// <summary>
    /// Fake LLM: requests 'fs_read_file' for the first call, answers plain text afterwards.
    /// </summary>
    private sealed class ScriptedLLM : ICommonLLM {

      private readonly string _FileFullName;

      private int _CallCount;

      private string _LastToolResponse;

      public ScriptedLLM(string fileFullName) {
        _FileFullName = fileFullName;
      }

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

        if (_CallCount == 1) {

          JObject call = new JObject();
          call["name"] = "fs_read_file";
          call["arguments"] = new JObject(new JProperty("path", _FileFullName));

          return "<tool_call>" + call.ToString(Newtonsoft.Json.Formatting.None) + "</tool_call>";
        }

        _LastToolResponse = newMessages.Last().Content;
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
