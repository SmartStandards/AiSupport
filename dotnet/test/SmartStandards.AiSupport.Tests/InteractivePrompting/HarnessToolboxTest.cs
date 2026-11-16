using AI.SmartStandards.InteractivePrompting.Harness;
using AI.SmartStandards.LowLevelPrompting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class HarnessToolboxTest {

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
    public void SettingsService_PersistsUserInstructionsAndTheme() {

      string settingsFile = Path.Combine(_TempDirectory, "settings.json");
      FileBasedPromptingSettingsService service = new FileBasedPromptingSettingsService(settingsFile);

      Assert.AreEqual(string.Empty, service.GetUserInstructions());
      Assert.AreEqual(UiThemeNames.Light, service.GetUiTheme());

      service.SaveUserInstructions("  Always answer in German.  ");
      service.SaveUiTheme("DARK");

      FileBasedPromptingSettingsService reloaded = new FileBasedPromptingSettingsService(settingsFile);
      Assert.AreEqual("Always answer in German.", reloaded.GetUserInstructions());
      Assert.AreEqual(UiThemeNames.Dark, reloaded.GetUiTheme());

      reloaded.SaveUserInstructions("");
      reloaded.SaveUiTheme("unknown");
      Assert.AreEqual(string.Empty, reloaded.GetUserInstructions());
      Assert.AreEqual(UiThemeNames.Light, reloaded.GetUiTheme());

    }

    [TestMethod()]
    public void BuildHarnessPrompt_ContainsMandatorySkillAndTools() {

      string instructions = "Always answer in German.";
      HarnessToolbox toolbox = new HarnessToolbox();

      Assert.IsNull(toolbox.BuildHarnessPrompt());

      toolbox.AddSkill(HarnessSkill.CreateUserInstructionsSkill(() => instructions));

      string prompt = toolbox.BuildHarnessPrompt();
      StringAssert.Contains(prompt, "<skill name=\"mustread-user-instructions\">");
      StringAssert.Contains(prompt, "Always answer in German.");
      Assert.IsFalse(prompt.Contains("<tools>"));

      //an empty mandatory skill is omitted completely
      instructions = "";
      Assert.IsNull(toolbox.BuildHarnessPrompt());

      toolbox.AddTool(new SetUiThemeTool(this.CreateSettingsService()));
      prompt = toolbox.BuildHarnessPrompt();
      StringAssert.Contains(prompt, "<tools>");
      StringAssert.Contains(prompt, "\"ui_set_theme\"");
      Assert.IsFalse(prompt.Contains(HarnessToolbox.LoadSkillToolName));

    }

    [TestMethod()]
    public void ParseToolCalls_SupportsCanonicalFormatAndAliases() {

      HarnessToolCall[] calls = HarnessToolbox.ParseToolCalls(
        "Sure.\n<tool_call>\n{\"name\": \"ui_set_theme\", \"arguments\": {\"theme\": \"dark\"}}\n</tool_call>"
      );
      Assert.AreEqual(1, calls.Length);
      Assert.AreEqual("ui_set_theme", calls[0].Name);
      Assert.AreEqual("dark", calls[0].Arguments["theme"].Value<string>());

      calls = HarnessToolbox.ParseToolCalls("/USETOOL \"ui_set_theme\" {\"theme\":\"light\"}");
      Assert.AreEqual(1, calls.Length);
      Assert.AreEqual("light", calls[0].Arguments["theme"].Value<string>());

      calls = HarnessToolbox.ParseToolCalls("/REQUESTSKILL \"some-skill\"");
      Assert.AreEqual(1, calls.Length);
      Assert.AreEqual(HarnessToolbox.LoadSkillToolName, calls[0].Name);

      calls = HarnessToolbox.ParseToolCalls("<tool_call>{broken</tool_call>");
      Assert.AreEqual(1, calls.Length);
      Assert.IsNotNull(calls[0].ParseError);

      Assert.AreEqual(0, HarnessToolbox.ParseToolCalls("Just a normal answer.").Length);

    }

    [TestMethod()]
    public void ExecuteToolCalls_ReturnsErrorsAsToolResponse() {

      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddTool(new SetUiThemeTool(this.CreateSettingsService()));

      string response = toolbox.ExecuteToolCalls(
        HarnessToolbox.ParseToolCalls("<tool_call>{\"name\":\"ui_set_theme\",\"arguments\":{\"theme\":\"pink\"}}</tool_call>")
      );
      StringAssert.Contains(response, "\"is_error\":true");

      response = toolbox.ExecuteToolCalls(
        HarnessToolbox.ParseToolCalls("<tool_call>{\"name\":\"unknown_tool\",\"arguments\":{}}</tool_call>")
      );
      StringAssert.Contains(response, "Unknown tool");

    }

    [TestMethod()]
    public void LoadSkill_ReturnsBodyOfCatalogSkill() {

      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddSkill(new HarnessSkill("demo-skill", "A demo skill.", () => "Demo body", false));

      string prompt = toolbox.BuildHarnessPrompt();
      StringAssert.Contains(prompt, "- demo-skill: A demo skill.");
      Assert.IsFalse(prompt.Contains("Demo body"));

      string response = toolbox.ExecuteToolCalls(HarnessToolbox.ParseToolCalls("/REQUESTSKILL \"demo-skill\""));
      StringAssert.Contains(response, "Demo body");

    }

    [TestMethod()]
    public void SendMessage_StupidMockSwitchesThemeViaToolLoop() {

      FileBasedPromptingSettingsService settings = this.CreateSettingsService();
      settings.SaveUserInstructions("Be nice.");

      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddSkill(HarnessSkill.CreateUserInstructionsSkill(() => settings.GetUserInstructions()));
      toolbox.AddTool(new SetUiThemeTool(settings));

      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(
        new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats")),
        () => new StupidMockLLM(0), toolbox, () => "Stupid", "Tester"
      );

      string chatId = backend.BeginNewChat(null);
      ChatMessage answer = backend.SendMessage(CancellationToken.None, chatId, "Please switch to dark mode");

      Assert.AreEqual(UiThemeNames.Dark, settings.GetUiTheme());
      Assert.IsFalse(answer.Content.Contains("<tool_call>"));

      //the tool exchange is not part of the visible history
      Assert.AreEqual(2, backend.GetChatMessageCount(chatId));

      backend.SendMessage(CancellationToken.None, chatId, "and back to light mode");
      Assert.AreEqual(UiThemeNames.Light, settings.GetUiTheme());

    }

    [TestMethod()]
    public void SendMessage_ResendsHarnessPromptIntoProviderContextOnlyWhenChanged() {

      string instructions = "Rule A";
      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddSkill(HarnessSkill.CreateUserInstructionsSkill(() => instructions));

      RecordingLLM llm = new RecordingLLM();
      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(
        new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats")),
        () => llm, toolbox, () => "Rec", "Tester"
      );

      string chatId = backend.BeginNewChat("Test");

      backend.SendMessage(CancellationToken.None, chatId, "1");
      backend.SendMessage(CancellationToken.None, chatId, "2");
      instructions = "Rule B";
      backend.SendMessage(CancellationToken.None, chatId, "3");

      //call 1: fresh context -> harness prompt + message
      Assert.AreEqual(ChatRoles.System, llm.Calls[0][0].Role);
      StringAssert.Contains(llm.Calls[0][0].Content, "Rule A");

      //call 2: provider context unchanged -> only the new message
      Assert.AreEqual(1, llm.Calls[1].Length);

      //call 3: changed user instructions -> harness prompt is sent again
      Assert.AreEqual(2, llm.Calls[2].Length);
      StringAssert.Contains(llm.Calls[2][0].Content, "Rule B");

    }

    private FileBasedPromptingSettingsService CreateSettingsService() {

      return new FileBasedPromptingSettingsService(Path.Combine(_TempDirectory, "settings.json"));
    }

    /// <summary>
    /// Fake LLM with provider-side context, which records all calls.
    /// </summary>
    private sealed class RecordingLLM : ICommonLLM {

      private readonly List<ChatTurn[]> _Calls = new List<ChatTurn[]>();

      public List<ChatTurn[]> Calls {
        get {
          return _Calls;
        }
      }

      public string CallChatApi(ChatTurn[] newMessages, ref string contextStore) {

        _Calls.Add(newMessages);
        contextStore = "ctx-" + _Calls.Count.ToString();

        return "ok: " + newMessages.Last().Content;
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
