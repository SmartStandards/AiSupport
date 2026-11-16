using AI.SmartStandards.LowLevelPrompting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class CheapPromptingUIBackendTest {

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
    public void SendMessage_PersistsHistoryAndUsesProviderContext() {

      FakeLLM llm = new FakeLLM(true);
      CheapPromptingUIBackend backend = this.CreateBackend(llm);

      string chatId = backend.BeginNewChat(null);

      ChatMessage answer1 = backend.SendMessage(CancellationToken.None, chatId, "Hello");
      ChatMessage answer2 = backend.SendMessage(CancellationToken.None, chatId, "Second");

      Assert.AreEqual("echo: Hello", answer1.Content);
      Assert.AreEqual("echo: Second", answer2.Content);
      Assert.AreEqual(4, backend.GetChatMessageCount(chatId));

      //first call without context: full history (1 message), second call: only the new message
      Assert.AreEqual(1, llm.Calls[0].Length);
      Assert.IsNull(llm.ContextStoresReceived[0]);
      Assert.AreEqual(1, llm.Calls[1].Length);
      Assert.AreEqual("Second", llm.Calls[1][0].Content);
      Assert.AreEqual("ctx-1", llm.ContextStoresReceived[1]);

      //the chat was renamed automatically using the first message
      ChatNameAndId header = backend.GetRecentChats().Single();
      Assert.AreEqual("Hello", header.Name);

    }

    [TestMethod()]
    public void SendMessage_WithoutProviderContext_SendsFullHistory() {

      FakeLLM llm = new FakeLLM(false);
      CheapPromptingUIBackend backend = this.CreateBackend(llm);

      string chatId = backend.BeginNewChat("Test");

      backend.SendMessage(CancellationToken.None, chatId, "A");
      backend.SendMessage(CancellationToken.None, chatId, "B");

      //A, answer(A), B
      Assert.AreEqual(3, llm.Calls[1].Length);
      Assert.AreEqual(ChatRoles.Assistant, llm.Calls[1][1].Role);

    }

    [TestMethod()]
    public void SendMessage_InvalidContextStore_RetriesWithFullHistory() {

      FakeLLM llm = new FakeLLM(true);
      CheapPromptingUIBackend backend = this.CreateBackend(llm);

      string chatId = backend.BeginNewChat("Test");

      backend.SendMessage(CancellationToken.None, chatId, "A");

      llm.RejectNextContextStore = true;

      ChatMessage answer = backend.SendMessage(CancellationToken.None, chatId, "B");

      Assert.AreEqual("echo: B", answer.Content);

      //call 0: A / call 1: rejected (B only) / call 2: retry with A, answer(A), B
      Assert.AreEqual(3, llm.Calls.Count);
      Assert.AreEqual(3, llm.Calls[2].Length);
      Assert.IsNull(llm.ContextStoresReceived[2]);

    }

    [TestMethod()]
    public void CompressChatContext_CreatesBaselineUsedByNextTurn() {

      FakeLLM llm = new FakeLLM(false);
      CheapPromptingUIBackend backend = this.CreateBackend(llm);

      string chatId = backend.BeginNewChat("Test");

      backend.SendMessage(CancellationToken.None, chatId, "A");
      backend.SendMessage(CancellationToken.None, chatId, "B");

      backend.CompressChatContext(CancellationToken.None, chatId);
      backend.SendMessage(CancellationToken.None, chatId, "C");

      ChatTurn[] lastCall = llm.Calls.Last();

      //baseline (system) + C
      Assert.AreEqual(2, lastCall.Length);
      Assert.AreEqual(ChatRoles.System, lastCall[0].Role);
      Assert.AreEqual("C", lastCall[1].Content);

      //the full history is untouched
      Assert.AreEqual(6, backend.GetChatMessageCount(chatId));

    }

    [TestMethod()]
    public void ChatManagement_RenameDeleteAndRange() {

      FakeLLM llm = new FakeLLM(false);
      CheapPromptingUIBackend backend = this.CreateBackend(llm);

      string chatId = backend.BeginNewChat("Old");
      backend.RenameChat(chatId, "New");

      backend.SendMessage(CancellationToken.None, chatId, "A");

      Assert.AreEqual("New", backend.GetRecentChats().Single().Name);

      ChatMessage[] range = backend.GetChatMessages(chatId, 1, 1);
      Assert.AreEqual(1, range.Length);
      Assert.AreEqual(ChatRoles.Assistant, range[0].Role);

      Assert.IsTrue(backend.ChatIdExists(chatId));
      backend.DeleteChat(chatId);
      Assert.IsFalse(backend.ChatIdExists(chatId));

    }

    [TestMethod()]
    public void StupidMockProvider_IsSelectableWithoutApiKeyAndAnswers() {

      FileBasedPromptingSettingsService service = new FileBasedPromptingSettingsService(Path.Combine(_TempDirectory, "settings.json"));

      Assert.IsTrue(service.GetAvailableProviders().Any((p) => p.Name == StupidMockLLM.ProviderName));

      service.SaveSettings(StupidMockLLM.ProviderName, "", null);

      Assert.IsTrue(service.GetSettings().IsReady);

      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(
        new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats")),
        () => new StupidMockLLM(0), () => "Stupid", "Tester"
      );

      string chatId = backend.BeginNewChat(null);
      ChatMessage answer = backend.SendMessage(CancellationToken.None, chatId, "Whatever");

      Assert.IsFalse(string.IsNullOrWhiteSpace(answer.Content));
      Assert.AreEqual(2, backend.GetChatMessageCount(chatId));

    }

    [TestMethod()]
    public void GitHubCopilotCli_IsRegisteredAndBuildsPrompts() {

      FileBasedPromptingSettingsService service = new FileBasedPromptingSettingsService(Path.Combine(_TempDirectory, "settings.json"));

      service.SaveSettings(GitHubCopilotCliConnector.ProviderName, "", null);

      Assert.IsTrue(service.GetSettings().IsReady);
      Assert.IsInstanceOfType(service.CreateConfiguredLLM(), typeof(GitHubCopilotCliConnector));

      //a single user turn is passed unchanged
      ChatTurn[] single = new ChatTurn[] { new ChatTurn(ChatRoles.User, "Hi") };
      Assert.AreEqual("Hi", GitHubCopilotCliConnector.BuildPrompt(single));

      //a conversation is rendered as transcript
      ChatTurn[] conversation = new ChatTurn[] {
        new ChatTurn(ChatRoles.User, "A"),
        new ChatTurn(ChatRoles.Assistant, "B"),
        new ChatTurn(ChatRoles.User, "C")
      };
      string prompt = GitHubCopilotCliConnector.BuildPrompt(conversation);
      StringAssert.Contains(prompt, "[ASSISTANT]" + Environment.NewLine + "B");
      Assert.IsTrue(prompt.EndsWith("[USER]" + Environment.NewLine + "C"));

      //command line quoting (CommandLineToArgvW rules)
      Assert.AreEqual("\"a b\"", GitHubCopilotCliConnector.QuoteArgument("a b"));
      Assert.AreEqual("\"say \\\"hi\\\"\"", GitHubCopilotCliConnector.QuoteArgument("say \"hi\""));
      Assert.AreEqual("\"C:\\dir\\\\\"", GitHubCopilotCliConnector.QuoteArgument("C:\\dir\\"));

      //authentication errors are detected (to trigger the interactive login)
      Assert.IsTrue(GitHubCopilotCliConnector.IsAuthenticationError("Error: No authentication information found."));
      Assert.IsFalse(GitHubCopilotCliConnector.IsAuthenticationError("Error: model 'xyz' is not available"));

    }

    [TestMethod()]
    public void SettingsService_PersistsApiKeyWithoutExposingIt() {

      string settingsFile = Path.Combine(_TempDirectory, "settings.json");

      FileBasedPromptingSettingsService service = new FileBasedPromptingSettingsService(settingsFile);

      Assert.IsFalse(service.GetSettings().IsReady);
      Assert.ThrowsExactly<InvalidOperationException>(() => service.CreateConfiguredLLM());

      service.SaveSettings("OpenAI", "", "sk-test-1234567890");

      FileBasedPromptingSettingsService reloaded = new FileBasedPromptingSettingsService(settingsFile);
      PromptingSettingsInfo info = reloaded.GetSettings();

      Assert.IsTrue(info.IsReady);
      Assert.IsTrue(info.HasApiKey);
      Assert.AreEqual(OpenAiLLMConnector.DefaultChatModel, info.Model);
      Assert.AreEqual("sk-...7890", info.ApiKeyHint);
      Assert.IsInstanceOfType(reloaded.CreateConfiguredLLM(), typeof(OpenAiLLMConnector));

      //an empty key keeps the stored one
      reloaded.SaveSettings("OpenAI", "gpt-4.1", null);
      Assert.IsTrue(reloaded.GetSettings().HasApiKey);
      Assert.AreEqual("gpt-4.1", reloaded.GetSettings().Model);

      reloaded.DeleteApiKey("OpenAI");
      Assert.IsFalse(reloaded.GetSettings().HasApiKey);

    }

    private CheapPromptingUIBackend CreateBackend(FakeLLM llm) {

      FileBasedPromptingSessionStore store = new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats"));

      return new CheapPromptingUIBackend(store, () => llm, () => "Fake", "Tester");
    }

    /// <summary>
    /// Fake LLM which echoes the last message and optionally supports a provider-side context.
    /// </summary>
    private sealed class FakeLLM : ICommonLLM {

      private readonly bool _SupportsContextStore;
      private int _ContextCounter = 0;
      private bool _RejectNextContextStore = false;
      private readonly List<ChatTurn[]> _Calls = new List<ChatTurn[]>();
      private readonly List<string> _ContextStoresReceived = new List<string>();

      public FakeLLM(bool supportsContextStore) {
        _SupportsContextStore = supportsContextStore;
      }

      public bool RejectNextContextStore {
        get {
          return _RejectNextContextStore;
        }
        set {
          _RejectNextContextStore = value;
        }
      }

      public List<ChatTurn[]> Calls {
        get {
          return _Calls;
        }
      }

      public List<string> ContextStoresReceived {
        get {
          return _ContextStoresReceived;
        }
      }

      public string CallChatApi(ChatTurn[] newMessages, ref string contextStore) {

        _Calls.Add(newMessages);
        _ContextStoresReceived.Add(contextStore);

        if (contextStore != null && _RejectNextContextStore) {
          _RejectNextContextStore = false;
          throw new ContextStoreInvalidException("expired");
        }

        if (_SupportsContextStore) {
          _ContextCounter++;
          contextStore = "ctx-" + _ContextCounter.ToString();
        }
        else {
          contextStore = null;
        }

        return "echo: " + newMessages.Last().Content;
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
