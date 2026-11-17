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

      //the harness tools must be clearly announced as second (text-only) tool layer
      StringAssert.Contains(prompt, "SECOND tool layer");
      StringAssert.Contains(prompt, "NOT part of your native/built-in tool set");

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

      //the raw tool exchange is not part of the visible history, only a presentation-only activity note
      Assert.AreEqual(3, backend.GetChatMessageCount(chatId));
      ChatMessage[] messages = backend.GetChatMessages(chatId, 0, 2);
      Assert.AreEqual(ChatMessageKinds.Activity, messages[1].Kind);
      Assert.IsFalse(messages[1].Content.Contains("<tool_call>"));

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

    [TestMethod()]
    public void SourceLevels_FilterSkillsAndToolsPerScope() {

      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddSource(new HarnessSource("team", false, "Team guidelines", SkillSourceLevels.Hidden, false));
      toolbox.AddSource(new HarnessSource("ui", true, "UI tools", ToolSourceLevels.Hidden, false));
      toolbox.AddSkill(new HarnessSkill("team-style", "Team style.", () => "Team body", false), "team");
      toolbox.AddTool(new SetUiThemeTool(this.CreateSettingsService()), "ui");

      //everything hidden by default
      Assert.IsNull(toolbox.BuildHarnessPrompt());

      Dictionary<string, string> skillOverrides = new Dictionary<string, string>();
      Dictionary<string, string> toolOverrides = new Dictionary<string, string>();
      HarnessScope scope = new HarnessScope(skillOverrides, toolOverrides, null);

      skillOverrides["team"] = SkillSourceLevels.OnDemand;
      string prompt = toolbox.BuildHarnessPrompt(scope);
      StringAssert.Contains(prompt, "- team-style: Team style.");
      Assert.IsFalse(prompt.Contains("Team body"));

      skillOverrides["team"] = SkillSourceLevels.InitializeAtStart;
      prompt = toolbox.BuildHarnessPrompt(scope);
      StringAssert.Contains(prompt, "Team body");

      //tool hidden -> unknown, requires confirmation without approver -> denied, with approver -> executed
      string call = "<tool_call>{\"name\":\"ui_set_theme\",\"arguments\":{\"theme\":\"dark\"}}</tool_call>";
      StringAssert.Contains(toolbox.ExecuteToolCalls(HarnessToolbox.ParseToolCalls(call), scope), "Unknown tool");

      toolOverrides["ui"] = ToolSourceLevels.RequiresConfirmation;
      StringAssert.Contains(toolbox.ExecuteToolCalls(HarnessToolbox.ParseToolCalls(call), scope), "denied");

      HarnessScope approvingScope = new HarnessScope(skillOverrides, toolOverrides, (c, s) => true);
      string response = toolbox.ExecuteToolCalls(HarnessToolbox.ParseToolCalls(call), approvingScope);
      Assert.IsFalse(response.Contains("is_error"), response);

    }

    [TestMethod()]
    public void SkillSourceOverride_IsOneWayAndIgnoredForFixedSources() {

      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddSource(new HarnessSource("ondemand", false, null, SkillSourceLevels.OnDemand, false));
      toolbox.AddSource(new HarnessSource("builtin", false, null, SkillSourceLevels.InitializeAtStart, true));

      Dictionary<string, string> overrides = new Dictionary<string, string>();
      overrides["ondemand"] = SkillSourceLevels.Hidden;
      overrides["builtin"] = SkillSourceLevels.Hidden;
      HarnessScope scope = new HarnessScope(overrides, null, null);

      Assert.AreEqual(SkillSourceLevels.OnDemand, toolbox.GetEffectiveSkillSourceLevel("ondemand", scope));
      Assert.AreEqual(SkillSourceLevels.InitializeAtStart, toolbox.GetEffectiveSkillSourceLevel("builtin", scope));

      Assert.ThrowsExactly<ArgumentException>(
        () => toolbox.AddTool(new SetUiThemeTool(this.CreateSettingsService()), "ondemand")
      );

    }

    [TestMethod()]
    public void Backend_ChatSourceLevels_PushControlMessagesAndReachModel() {

      HarnessToolbox toolbox = new HarnessToolbox();
      toolbox.AddSource(new HarnessSource("team", false, "Team guidelines", SkillSourceLevels.Hidden, false));
      toolbox.AddSource(new HarnessSource("ui", true, "UI tools", ToolSourceLevels.Hidden, false));
      toolbox.AddSkill(new HarnessSkill("team-style", "Team style.", () => "Team body", false), "team");
      toolbox.AddTool(new SetUiThemeTool(this.CreateSettingsService()), "ui");

      RecordingLLM llm = new RecordingLLM();
      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(
        new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats")),
        () => llm, toolbox, () => "Rec", "Tester"
      );

      string chatId = backend.BeginNewChat("Test");
      backend.SendMessage(CancellationToken.None, chatId, "1");
      Assert.AreEqual(1, llm.Calls[0].Length);

      backend.RaiseChatSkillSourceLevel(chatId, "team", SkillSourceLevels.InitializeAtStart);
      backend.SetChatToolSourceLevel(chatId, "ui", ToolSourceLevels.NoConfirmation);

      //one-way: lowering is ignored (no further control message)
      backend.RaiseChatSkillSourceLevel(chatId, "team", SkillSourceLevels.OnDemand);

      ChatMessage[] messages = backend.GetChatMessages(chatId, 0, 100);
      Assert.AreEqual(4, messages.Length);
      Assert.AreEqual(ChatMessageKinds.Control, messages[2].Kind);
      Assert.AreEqual(ChatMessageKinds.Control, messages[3].Kind);

      ChatDetails details = backend.GetChatDetails(chatId);
      Assert.AreEqual(SkillSourceLevels.InitializeAtStart, details.SkillSources.Single().EffectiveLevel);
      Assert.AreEqual(ToolSourceLevels.NoConfirmation, details.ToolSources.Single().EffectiveLevel);
      CollectionAssert.AreEqual(new string[] { "ui_set_theme" }, details.ToolSources.Single().ItemNames);

      backend.SendMessage(CancellationToken.None, chatId, "2");

      //harness prompt changed -> resent, followed by both control messages and the user message
      ChatTurn[] lastCall = llm.Calls.Last();
      Assert.AreEqual(4, lastCall.Length);
      StringAssert.Contains(lastCall[0].Content, "Team body");
      StringAssert.Contains(lastCall[0].Content, "ui_set_theme");
      StringAssert.Contains(lastCall[1].Content, "<harness_notice>");
      Assert.AreEqual("2", lastCall[3].Content);

      Assert.ThrowsExactly<KeyNotFoundException>(
        () => backend.SetChatToolSourceLevel(chatId, "team", ToolSourceLevels.Hidden)
      );

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

      public string CallChatApi(
        System.Threading.CancellationToken cancellationToken, ChatTurn[] newMessages, ref string contextStore,
        Action<string, int, int> onProgress
      ) {

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
