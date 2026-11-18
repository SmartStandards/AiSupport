using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class FileBasedPromptingProfileServiceTest {

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
    public void GetProfiles_WithoutProfilesFile_ReturnsActiveDefaultProfileAtRootDirectory() {

      FileBasedPromptingProfileService service = new FileBasedPromptingProfileService(_TempDirectory);

      HarnessProfileInfo[] profiles = service.GetProfiles();

      Assert.AreEqual(1, profiles.Length);
      Assert.AreEqual(FileBasedPromptingProfileService.DefaultProfileId, profiles[0].ProfileId);
      Assert.IsTrue(profiles[0].IsActive);
      Assert.IsTrue(profiles[0].IsDefault);
      Assert.AreEqual(FileBasedPromptingProfileService.DefaultProfileId, service.GetActiveProfileId());
      Assert.AreEqual(_TempDirectory, service.GetProfileDirectory(FileBasedPromptingProfileService.DefaultProfileId));

    }

    [TestMethod()]
    public void CreateProfile_CopiesConfigurationFilesButNotChats() {

      Directory.CreateDirectory(Path.Combine(_TempDirectory, "Chats"));
      Directory.CreateDirectory(Path.Combine(_TempDirectory, "ToolAssemblies", "a1"));
      File.WriteAllText(Path.Combine(_TempDirectory, "settings.json"), "{\"ProviderName\":\"X\"}");
      File.WriteAllText(Path.Combine(_TempDirectory, "extensions.credentials.json"), "protected");
      File.WriteAllText(Path.Combine(_TempDirectory, "ToolAssemblies", "a1", "tool.dll"), "dll");
      File.WriteAllText(Path.Combine(_TempDirectory, "Chats", "c1.json"), "{}");

      FileBasedPromptingProfileService service = new FileBasedPromptingProfileService(_TempDirectory);

      string profileId = service.CreateProfile(null, "Work");
      string profileDirectory = service.GetProfileDirectory(profileId);

      Assert.AreNotEqual(_TempDirectory, profileDirectory);
      Assert.AreEqual("{\"ProviderName\":\"X\"}", File.ReadAllText(Path.Combine(profileDirectory, "settings.json")));
      Assert.AreEqual("protected", File.ReadAllText(Path.Combine(profileDirectory, "extensions.credentials.json")));
      Assert.IsTrue(File.Exists(Path.Combine(profileDirectory, "ToolAssemblies", "a1", "tool.dll")));
      Assert.IsFalse(Directory.Exists(Path.Combine(profileDirectory, "Chats")));

      HarnessProfileInfo created = service.GetProfiles().Single((p) => p.ProfileId == profileId);
      Assert.AreEqual("Work", created.DisplayName);
      Assert.IsFalse(created.IsActive);

    }

    [TestMethod()]
    public void ActivateRenameDelete_EnforceConstraints() {

      FileBasedPromptingProfileService service = new FileBasedPromptingProfileService(_TempDirectory);
      string profileId = service.CreateProfile(null, "Work");

      Assert.ThrowsException<ArgumentException>(() => service.CreateProfile(null, "work"));
      Assert.ThrowsException<InvalidOperationException>(() => service.DeleteProfile(FileBasedPromptingProfileService.DefaultProfileId));

      service.ActivateProfile(profileId);

      FileBasedPromptingProfileService reloaded = new FileBasedPromptingProfileService(_TempDirectory);
      Assert.AreEqual(profileId, reloaded.GetActiveProfileId());
      Assert.ThrowsException<InvalidOperationException>(() => reloaded.DeleteProfile(profileId));

      reloaded.RenameProfile(profileId, "Customer A");
      Assert.IsNotNull(reloaded.FindProfile("customer a"));

      reloaded.ActivateProfile(FileBasedPromptingProfileService.DefaultProfileId);
      reloaded.DeleteProfile(profileId);

      Assert.AreEqual(1, reloaded.GetProfiles().Length);
      Assert.IsFalse(Directory.Exists(reloaded.GetProfileDirectory(profileId)));

    }

    [TestMethod()]
    public void ModifyingOperations_RequireAdmin() {

      FileBasedPromptingProfileService service = new FileBasedPromptingProfileService(_TempDirectory);
      service.UserLevelGetter = () => PromptingUserLevels.PowerUser;

      Assert.ThrowsException<UnauthorizedAccessException>(() => service.CreateProfile(null, "Work"));
      Assert.ThrowsException<UnauthorizedAccessException>(() => service.SetAskAtStartup(true));
      Assert.AreEqual(1, service.GetProfiles().Length);

    }

  }

}
