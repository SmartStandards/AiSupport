using AI.SmartStandards.LowLevelPrompting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class PromptingUserLevelsTest {

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
    public void Resolve_WithoutGetterOrUnknownValue_IsAdmin() {

      Assert.AreEqual(PromptingUserLevels.Admin, PromptingUserLevels.Resolve(null));
      Assert.AreEqual(PromptingUserLevels.Admin, PromptingUserLevels.Resolve(() => 0));
      Assert.AreEqual(PromptingUserLevels.User, PromptingUserLevels.Resolve(() => 1));
      Assert.AreEqual(PromptingUserLevels.PowerUser, PromptingUserLevels.Resolve(() => 2));

    }

    [TestMethod()]
    public void SettingsService_DefaultsAreAdminOnly() {

      FileBasedPromptingSettingsService service = new FileBasedPromptingSettingsService(
        Path.Combine(_TempDirectory, "settings.json")
      );

      int level = PromptingUserLevels.PowerUser;
      service.UserLevelGetter = () => level;

      Assert.AreEqual(PromptingUserLevels.PowerUser, service.GetSettings().UserLevel);
      Assert.ThrowsException<UnauthorizedAccessException>(() => service.SaveSettings("OpenAI", "gpt-4o", "key"));
      Assert.ThrowsException<UnauthorizedAccessException>(() => service.DeleteApiKey("OpenAI"));

      //the own user instructions and the theme remain changeable (ROL-5)
      service.SaveUserInstructions("be brief");
      service.SaveUiTheme(UiThemeNames.Dark);

      level = PromptingUserLevels.Admin;
      service.SaveSettings("OpenAI", "gpt-4o", "key");

    }

    [TestMethod()]
    public void ExtensionService_SourceManagementAndBrowsingAreAdminOnly() {

      FileBasedPromptingExtensionService service = new FileBasedPromptingExtensionService(
        Path.Combine(_TempDirectory, "extensions.json"),
        Path.Combine(_TempDirectory, "user", "global-user-preferences", "SKILL.md"),
        Path.Combine(_TempDirectory, "public", "global-system-preferences", "SKILL.md")
      );
      service.SecretProtector = new KeyFileSecretProtector(Path.Combine(_TempDirectory, "local", "secrets.key"));
      service.UserLevelGetter = () => PromptingUserLevels.PowerUser;

      Assert.ThrowsException<UnauthorizedAccessException>(() => service.BrowseFileSystem(_TempDirectory, true));
      Assert.ThrowsException<UnauthorizedAccessException>(() => service.SaveToolProviderSettings("filesystem", "{}"));
      Assert.ThrowsException<UnauthorizedAccessException>(() => service.SetSkillSourceDisplayName("x", "y"));

    }

    [TestMethod()]
    public void Backend_UserCannotSwitchProvider() {

      FileBasedPromptingSessionStore store = new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats"));
      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(store, () => new StupidMockLLM(), () => "Fake", "Tester");
      backend.UserLevelGetter = () => PromptingUserLevels.User;

      //ROL-1/4: chats can still be started, used and deleted
      string chatId = backend.BeginNewChat("Test");
      backend.SendMessage(CancellationToken.None, chatId, "Hello");

      Assert.ThrowsException<UnauthorizedAccessException>(() => backend.SetChatProvider(chatId, "Any", null));

      backend.DeleteChat(chatId);
      Assert.IsFalse(backend.ChatIdExists(chatId));

    }

  }

}
