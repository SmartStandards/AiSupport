using AI.SmartStandards.LowLevelPrompting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Verifies the complete server-side wiring of the chat UI (embedded files + UJMW endpoints)
  /// exactly as used by the desktop chat client - but with a fake LLM.
  /// </summary>
  [TestClass()]
  public class ChatUiEndToEndTest {

    private string _TempDirectory;

    private WebApplication _App;

    private HttpClient _Client;

    [TestInitialize()]
    public void Setup() {

      _TempDirectory = Path.Combine(Path.GetTempPath(), "AiSupportTests", Guid.NewGuid().ToString("N"));

      FileBasedPromptingSettingsService settingsService = new FileBasedPromptingSettingsService(
        Path.Combine(_TempDirectory, "settings.json")
      );

      FileBasedPromptingSessionStore sessionStore = new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats"));

      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(
        sessionStore, () => new EchoLLM(), () => "Echo", "Tester"
      );

      WebApplicationBuilder builder = WebApplication.CreateBuilder();
      builder.WebHost.UseTestServer();

      builder.Services.AddSingleton<IPromptingSettingsService>(settingsService);
      builder.Services.AddSingleton<IPromptingUIBackend>(backend);
      builder.Services.AddControllers().AddNewtonsoftJson();
      builder.Services.AddChatUi("/");

      _App = builder.Build();
      _App.UseRouting();
      _App.MapControllers();
      _App.MapChatUi("/");
      _App.Start();

      _Client = _App.GetTestClient();

    }

    [TestCleanup()]
    public void Cleanup() {

      _Client.Dispose();
      _App.StopAsync().Wait();
      _App.DisposeAsync().AsTask().Wait();

      if (Directory.Exists(_TempDirectory)) {
        Directory.Delete(_TempDirectory, true);
      }

    }

    [TestMethod()]
    public void EmbeddedFiles_AreServed() {

      HttpResponseMessage index = _Client.GetAsync("/").Result;
      Assert.AreEqual(HttpStatusCode.OK, index.StatusCode);
      StringAssert.Contains(index.Content.ReadAsStringAsync().Result, "chat.js");

      HttpResponseMessage script = _Client.GetAsync("/chat.js").Result;
      Assert.AreEqual(HttpStatusCode.OK, script.StatusCode);
      StringAssert.StartsWith(script.Content.Headers.ContentType.MediaType, "text/javascript");

    }

    [TestMethod()]
    public void SettingsAndChat_WorkViaUjmw() {

      JObject settings = this.Post(ChatUiSetupExtensions.SettingsRouteSegment + "/GetSettings", new JObject());
      Assert.IsFalse((bool)settings["return"]["isReady"], settings.ToString());

      JObject saveArgs = new JObject();
      saveArgs["providerName"] = "OpenAI";
      saveArgs["model"] = "";
      saveArgs["apiKey"] = "sk-test-1234567890";
      this.Post(ChatUiSetupExtensions.SettingsRouteSegment + "/SaveSettings", saveArgs);

      settings = this.Post(ChatUiSetupExtensions.SettingsRouteSegment + "/GetSettings", new JObject());
      Assert.IsTrue((bool)settings["return"]["isReady"], settings.ToString());
      Assert.IsNull(settings["return"]["apiKey"]);

      JObject beginArgs = new JObject();
      beginArgs["name"] = "";
      string chatId = (string)this.Post(ChatUiSetupExtensions.BackendRouteSegment + "/BeginNewChat", beginArgs)["return"];
      Assert.IsFalse(string.IsNullOrWhiteSpace(chatId));

      JObject sendArgs = new JObject();
      sendArgs["chatId"] = chatId;
      sendArgs["message"] = "Hello";
      JObject sendResult = this.Post(ChatUiSetupExtensions.BackendRouteSegment + "/SendMessage?wait=10", sendArgs);

      Assert.AreEqual("echo: Hello", (string)sendResult["return"]["content"], sendResult.ToString());

      //the progress callback is delivered to UJMW clients (convention: '<callbackName>Invokes')
      JArray progressInvokes = sendResult["onProgressInvokes"] as JArray;
      Assert.IsNotNull(progressInvokes, sendResult.ToString());
      Assert.IsTrue(progressInvokes.Count > 0, sendResult.ToString());

      JObject recent = this.Post(ChatUiSetupExtensions.BackendRouteSegment + "/GetRecentChats", new JObject());
      Assert.AreEqual("Hello", (string)recent["return"].First()["name"], recent.ToString());

    }

    [TestMethod()]
    public void ChatDetailsDialog_IsServed() {

      string index = _Client.GetAsync("/").Result.Content.ReadAsStringAsync().Result;
      StringAssert.Contains(index, "detailsDialog");

      string script = _Client.GetAsync("/chat.js").Result.Content.ReadAsStringAsync().Result;
      StringAssert.Contains(script, "SetChatProvider");
      StringAssert.Contains(script, "Invokes");

    }

    [TestMethod()]
    public void SourceManagementUi_IsServed() {

      string index = _Client.GetAsync("/").Result.Content.ReadAsStringAsync().Result;
      StringAssert.Contains(index, "settingsTabSkills");
      StringAssert.Contains(index, "settingsTabTools");
      StringAssert.Contains(index, "toolSourceList");
      StringAssert.Contains(index, "btnUserInstructions");
      StringAssert.Contains(index, "detailsSkillSources");
      StringAssert.Contains(index, "detailsToolSources");

      string script = _Client.GetAsync("/chat.js").Result.Content.ReadAsStringAsync().Result;
      StringAssert.Contains(script, "chatApi/extensions/");
      StringAssert.Contains(script, "GetSkillSources");
      StringAssert.Contains(script, "GetToolSources");
      StringAssert.Contains(script, "AddMcpServer");
      StringAssert.Contains(script, "UploadToolAssembly");
      StringAssert.Contains(script, "[activity] ");
      StringAssert.Contains(script, "RaiseChatSkillSourceLevel");
      StringAssert.Contains(script, "SetChatToolSourceLevel");

    }

    [TestMethod()]
    public void ToolApprovalAndAuditUi_IsServedAndAuditLogIsReachable() {

      string index = _Client.GetAsync("/").Result.Content.ReadAsStringAsync().Result;
      StringAssert.Contains(index, "detailsAuditLog");

      string script = _Client.GetAsync("/chat.js").Result.Content.ReadAsStringAsync().Result;
      StringAssert.Contains(script, "AllowForChat");
      StringAssert.Contains(script, "AllowOnce");
      StringAssert.Contains(script, "GetToolAuditLog");

    }

    [TestMethod()]
    public void SendMessage_DeliversProgressWhileRunning() {

      EchoLLM.Delay = TimeSpan.FromSeconds(4);

      try {

        JObject beginArgs = new JObject();
        beginArgs["name"] = "";
        string chatId = (string)this.Post(ChatUiSetupExtensions.BackendRouteSegment + "/BeginNewChat", beginArgs)["return"];

        JObject sendArgs = new JObject();
        sendArgs["chatId"] = chatId;
        sendArgs["message"] = "Hello";
        JObject initial = this.Post(ChatUiSetupExtensions.BackendRouteSegment + "/SendMessage?wait=1", sendArgs);

        Assert.IsNotNull(initial["async"], initial.ToString());

        JArray progressInvokes = initial["onProgressInvokes"] as JArray;
        Assert.IsNotNull(progressInvokes, "No progress within the initial (still running) response: " + initial.ToString());
        Assert.IsTrue(progressInvokes.Any((p) => ((string)p["arg1"]).Contains("echo-progress")), initial.ToString());

        System.Threading.Thread.Sleep(5000);

        string asyncId = (string)initial["async"]["asyncId"];
        string secret = (string)initial["async"]["pollingSecret"];
        string salt = "abcdef";
        string hash;

        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create()) {
          byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(secret + salt));
          hash = Convert.ToBase64String(hashBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        string pollUrl = "/" + ChatUiSetupExtensions.BackendRouteSegment + "/SendMessage?asyncId=" + Uri.EscapeDataString(asyncId) +
          "&pollingSalt=" + salt + "&pollingHash=" + Uri.EscapeDataString(hash);
        string polledText = _Client.GetAsync(pollUrl).Result.Content.ReadAsStringAsync().Result;
        JObject polled = JObject.Parse(polledText);

        //later progress is delivered with the poll (not again the already delivered entries)
        JArray polledInvokes = polled["onProgressInvokes"] as JArray;
        Assert.IsNotNull(polledInvokes, polledText);
        Assert.IsTrue(polledInvokes.Any((p) => (string)p["arg1"] == "echo-progress-2"), polledText);
        Assert.IsFalse(polledInvokes.Any((p) => (string)p["arg1"] == "echo-progress"), polledText);
        Assert.AreEqual("echo: Hello", (string)polled["return"]["content"], polledText);

      }
      finally {
        EchoLLM.Delay = TimeSpan.Zero;
      }

    }

    private JObject Post(string relativeUrl, JObject body) {

      StringContent content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
      HttpResponseMessage response = _Client.PostAsync("/" + relativeUrl, content).Result;
      string responseText = response.Content.ReadAsStringAsync().Result;

      Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, relativeUrl + ": " + responseText);

      if (string.IsNullOrWhiteSpace(responseText)) {
        return new JObject();
      }

      return JObject.Parse(responseText);
    }

    private sealed class EchoLLM : ICommonLLM {

      public static TimeSpan Delay = TimeSpan.Zero;

      public string CallChatApi(
        System.Threading.CancellationToken cancellationToken, ChatTurn[] newMessages, ref string contextStore,
        Action<string, int, int> onProgress
      ) {

        if (Delay > TimeSpan.Zero) {

          if (onProgress != null) {
            onProgress.Invoke("echo-progress", 0, 0);
          }

          System.Threading.Thread.Sleep(Delay);

          if (onProgress != null) {
            onProgress.Invoke("echo-progress-2", 0, 0);
          }
        }

        contextStore = null;
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
