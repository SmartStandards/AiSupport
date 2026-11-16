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

      JObject recent = this.Post(ChatUiSetupExtensions.BackendRouteSegment + "/GetRecentChats", new JObject());
      Assert.AreEqual("Hello", (string)recent["return"].First()["name"], recent.ToString());

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

      public string CallChatApi(ChatTurn[] newMessages, ref string contextStore) {
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
