using AI.SmartStandards.InteractivePrompting.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class PromptingExtensionServiceTest {

    private string _TempDirectory;

    private string _ExtensionsFile;

    private string _UserPreferencesFile;

    private string _SystemPreferencesFile;

    [TestInitialize()]
    public void Setup() {

      _TempDirectory = Path.Combine(Path.GetTempPath(), "AiSupportTests", Guid.NewGuid().ToString("N"));
      _ExtensionsFile = Path.Combine(_TempDirectory, "extensions.json");
      _UserPreferencesFile = Path.Combine(_TempDirectory, "user", "global-user-preferences", "SKILL.md");
      _SystemPreferencesFile = Path.Combine(_TempDirectory, "public", "global-system-preferences", "SKILL.md");

    }

    [TestCleanup()]
    public void Cleanup() {

      if (Directory.Exists(_TempDirectory)) {
        Directory.Delete(_TempDirectory, true);
      }

    }

    private FileBasedPromptingExtensionService CreateService() {

      FileBasedPromptingExtensionService service = new FileBasedPromptingExtensionService(
        _ExtensionsFile, _UserPreferencesFile, _SystemPreferencesFile
      );
      service.SecretProtector = new KeyFileSecretProtector(Path.Combine(_TempDirectory, "local", "secrets.key"));

      return service;
    }

    [TestMethod()]
    public void KeyFileSecretProtector_RoundTripsAndDetectsManipulation() {

      string keyFile = Path.Combine(_TempDirectory, "local", "secrets.key");
      KeyFileSecretProtector protector = new KeyFileSecretProtector(keyFile);

      string protectedValue = protector.Protect("ghp_secret-token-äöü");

      Assert.IsTrue(protector.IsProtected(protectedValue));
      Assert.IsFalse(protector.IsProtected("ghp_secret-token"));
      Assert.IsFalse(protectedValue.Contains("secret"));
      Assert.AreNotEqual(protectedValue, protector.Protect("ghp_secret-token-äöü"));
      Assert.IsTrue(File.Exists(keyFile));

      //a new instance with the same key file can unprotect
      Assert.AreEqual("ghp_secret-token-äöü", new KeyFileSecretProtector(keyFile).Unprotect(protectedValue));

      //another key fails
      KeyFileSecretProtector otherProtector = new KeyFileSecretProtector(Path.Combine(_TempDirectory, "other.key"));
      Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(() => otherProtector.Unprotect(protectedValue));

      //manipulation fails
      char[] chars = protectedValue.ToCharArray();
      int index = chars.Length / 2;
      if (chars[index] == 'A') {
        chars[index] = 'B';
      }
      else {
        chars[index] = 'A';
      }
      Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(new string(chars)));

    }

    [TestMethod()]
    public void Credentials_AreEncryptedAtRestAndLegacyPlainTextIsAccepted() {

      string receivedCredential = null;

      FileBasedPromptingExtensionService service = this.CreateService();
      service.RemoteContentLoader = (url, credential) => {
        receivedCredential = credential;
        return "Remote body";
      };

      string sourceId = service.AddSkillSource(SkillSourceKinds.Url, "https://example.org/SKILL.md", "secret-token-12345");

      string credentialsJson = File.ReadAllText(service.CredentialsFileFullName);
      Assert.IsFalse(credentialsJson.Contains("secret-token-12345"));
      Assert.IsTrue(credentialsJson.Contains("enc1:"));

      //a new service instance (same key) decrypts the credential for the refresh
      FileBasedPromptingExtensionService reloaded = this.CreateService();
      reloaded.RemoteContentLoader = service.RemoteContentLoader;
      receivedCredential = null;
      reloaded.RefreshOfflineCopies();
      Assert.AreEqual("secret-token-12345", receivedCredential);

      //legacy plain text (written by older versions) is accepted
      File.WriteAllText(service.CredentialsFileFullName, "{ \"" + sourceId + "\": \"legacy-plain-token\" }");
      receivedCredential = null;
      reloaded.RefreshOfflineCopies();
      Assert.AreEqual("legacy-plain-token", receivedCredential);

      //... and is protected on the next write
      reloaded.SetSkillSourceCredential(sourceId, "legacy-plain-token");
      Assert.IsFalse(File.ReadAllText(service.CredentialsFileFullName).Contains("legacy-plain-token"));

      //an unreadable value (other key) is treated as missing
      FileBasedPromptingExtensionService otherKey = new FileBasedPromptingExtensionService(
        _ExtensionsFile, _UserPreferencesFile, _SystemPreferencesFile
      );
      otherKey.SecretProtector = new KeyFileSecretProtector(Path.Combine(_TempDirectory, "other.key"));
      Assert.IsFalse(otherKey.GetSkillSources().Single((s) => s.SourceId == sourceId).HasCredentials);

    }

    private string WriteSkill(string relativePath, string name, string description, string body) {

      string fullName = Path.Combine(_TempDirectory, relativePath);
      SkillFile skillFile = new SkillFile();
      skillFile.Name = name;
      skillFile.Description = description;
      skillFile.Body = body;
      skillFile.Save(fullName);

      return fullName;
    }

    [TestMethod()]
    public void SkillFile_ParsesFrontMatterAndFallsBackToFolderName() {

      SkillFile parsed = SkillFile.Parse("---\r\nname: my-skill\r\ndescription: \"Does things\"\r\n---\r\n\r\nBody line\r\n");

      Assert.AreEqual("my-skill", parsed.Name);
      Assert.AreEqual("Does things", parsed.Description);
      Assert.AreEqual("Body line", parsed.Body);

      SkillFile plain = SkillFile.Parse("Only a body");
      Assert.AreEqual(string.Empty, plain.Name);
      Assert.AreEqual("Only a body", plain.Body);

      string file = Path.Combine(_TempDirectory, "folder-name", "SKILL.md");
      Directory.CreateDirectory(Path.GetDirectoryName(file));
      File.WriteAllText(file, "No front matter");

      Assert.AreEqual("folder-name", SkillFile.Load(file).Name);

    }

    [TestMethod()]
    public void BuiltInSources_AreFixedAndPresentWithoutFiles() {

      FileBasedPromptingExtensionService service = this.CreateService();

      SkillSourceInfo[] sources = service.GetSkillSources();

      Assert.AreEqual(2, sources.Length);
      Assert.AreEqual(FileBasedPromptingExtensionService.UserPreferencesSourceId, sources[0].SourceId);
      Assert.AreEqual(FileBasedPromptingExtensionService.SystemPreferencesSourceId, sources[1].SourceId);
      Assert.IsTrue(sources.All((s) => s.IsFixed && s.Level == SkillSourceLevels.InitializeAtStart));
      Assert.IsTrue(sources.All((s) => !s.IsReferenceBroken && s.SkillNames.Length == 0));

      Assert.ThrowsException<InvalidOperationException>(
        () => service.RemoveSkillSource(FileBasedPromptingExtensionService.UserPreferencesSourceId)
      );
      Assert.ThrowsException<InvalidOperationException>(
        () => service.SetSkillSourceLevel(FileBasedPromptingExtensionService.SystemPreferencesSourceId, SkillSourceLevels.Hidden)
      );

    }

    [TestMethod()]
    public void FileAndFolderReferences_ArePersistedAndNotCopied() {

      string singleFile = this.WriteSkill(Path.Combine("refs", "coding.md"), "coding-rules", "Coding rules", "Use explicit types.");
      this.WriteSkill(Path.Combine("lib", "alpha", "SKILL.md"), "alpha", "Alpha skill", "Alpha body");
      this.WriteSkill(Path.Combine("lib", "beta", "SKILL.md"), "beta", "Beta skill", "Beta body");

      FileBasedPromptingExtensionService service = this.CreateService();

      string fileSourceId = service.AddSkillFileReference(singleFile);
      string folderSourceId = service.AddSkillSource("folder", Path.Combine(_TempDirectory, "lib"), null);

      Assert.AreEqual(fileSourceId, service.AddSkillFileReference(singleFile), "Re-adding returns the existing id.");
      Assert.AreNotEqual(fileSourceId, folderSourceId);

      service.SetSkillSourceLevel(folderSourceId, SkillSourceLevels.InitializeAtStart);

      FileBasedPromptingExtensionService reloaded = this.CreateService();
      SkillSourceInfo[] sources = reloaded.GetSkillSources();

      SkillSourceInfo fileSource = sources.Single((s) => s.SourceId == fileSourceId);
      Assert.AreEqual(SkillSourceKinds.File, fileSource.Kind);
      Assert.AreEqual(singleFile, fileSource.Location);
      Assert.AreEqual(SkillSourceLevels.OnDemand, fileSource.Level);
      CollectionAssert.AreEqual(new string[] { "coding-rules" }, fileSource.SkillNames);

      SkillSourceInfo folderSource = sources.Single((s) => s.SourceId == folderSourceId);
      Assert.AreEqual(SkillSourceLevels.InitializeAtStart, folderSource.Level);
      CollectionAssert.AreEqual(new string[] { "alpha", "beta" }, folderSource.SkillNames);

      //pure references: nothing was copied next to the extensions file
      Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(_ExtensionsFile)).Length);

      reloaded.RemoveSkillSource(fileSourceId);
      Assert.IsFalse(this.CreateService().GetSkillSources().Any((s) => s.SourceId == fileSourceId));
      Assert.IsTrue(File.Exists(singleFile), "Removing a reference never deletes the referenced file.");

    }

    [TestMethod()]
    public void BrokenReferences_AreFlaggedButKept() {

      string singleFile = this.WriteSkill(Path.Combine("refs", "temp.md"), "temp-skill", "Temp", "Temp body");

      FileBasedPromptingExtensionService service = this.CreateService();
      string sourceId = service.AddSkillFileReference(singleFile);

      File.Delete(singleFile);
      service.SetSystemPreferencesLocation(Path.Combine(_TempDirectory, "missing", "SKILL.md"));

      SkillSourceInfo[] sources = service.GetSkillSources();

      SkillSourceInfo broken = sources.Single((s) => s.SourceId == sourceId);
      Assert.IsTrue(broken.IsReferenceBroken);
      Assert.IsFalse(string.IsNullOrWhiteSpace(broken.LastError));

      SkillSourceInfo system = sources.Single((s) => s.SourceId == FileBasedPromptingExtensionService.SystemPreferencesSourceId);
      Assert.IsTrue(system.IsReferenceBroken);

      Assert.ThrowsException<NotSupportedException>(() => service.AddSkillSource(SkillSourceKinds.BuiltIn, "anything", null));
      Assert.ThrowsException<NotSupportedException>(() => service.AddSkillSource(SkillSourceKinds.Folder, _TempDirectory, "token"));

    }

    [TestMethod()]
    public void AttachedToolbox_ReceivesSourcesAndReadsBodiesOnDemand() {

      string singleFile = this.WriteSkill(Path.Combine("refs", "team.md"), "team-rules", "Team rules", "Version 1");
      this.WriteSkill(Path.Combine("public", "global-system-preferences", "SKILL.md"), "ignored-name", "System", "Company policy");

      FileBasedPromptingExtensionService service = this.CreateService();
      HarnessToolbox toolbox = new HarnessToolbox();
      service.AttachToolbox(toolbox);

      string sourceId = service.AddSkillFileReference(singleFile);

      string prompt = toolbox.BuildHarnessPrompt(HarnessScope.CreateDefault());
      StringAssert.Contains(prompt, "Company policy", "Built-in system preferences are pinned.");
      StringAssert.Contains(prompt, "team-rules", "On-demand skill appears in the catalog.");
      Assert.IsFalse(prompt.Contains("Version 1"), "On-demand body is not pinned.");

      File.WriteAllText(singleFile, "---\nname: team-rules\ndescription: Team rules\n---\nVersion 2\n");
      service.SetSkillSourceLevel(sourceId, SkillSourceLevels.InitializeAtStart);

      prompt = toolbox.BuildHarnessPrompt(HarnessScope.CreateDefault());
      StringAssert.Contains(prompt, "Version 2", "The referenced file is read on demand (never copied).");

      service.SetSkillSourceLevel(sourceId, SkillSourceLevels.Hidden);
      prompt = toolbox.BuildHarnessPrompt(HarnessScope.CreateDefault());
      Assert.IsFalse(prompt.Contains("team-rules"));

      service.RemoveSkillSource(sourceId);
      Assert.IsNull(toolbox.GetSource(sourceId));
      Assert.IsFalse(toolbox.GetSkills().Any((s) => s.Name == "team-rules"));

    }

    [TestMethod()]
    public void UserInstructions_AreMigratedIntoUserPreferencesSkill() {

      string settingsFile = Path.Combine(_TempDirectory, "settings.json");

      FileBasedPromptingSettingsService legacy = new FileBasedPromptingSettingsService(settingsFile);
      legacy.SaveUserInstructions("Always answer in German.");

      FileBasedPromptingSettingsService settings = new FileBasedPromptingSettingsService(settingsFile);
      settings.UseUserInstructionsFile(_UserPreferencesFile, FileBasedPromptingExtensionService.UserPreferencesSourceId, "User prefs");

      Assert.IsTrue(File.Exists(_UserPreferencesFile));
      Assert.AreEqual("Always answer in German.", settings.GetUserInstructions());
      Assert.IsFalse(File.ReadAllText(settingsFile).Contains("Always answer in German."), "Migrated out of settings.json.");

      FileBasedPromptingExtensionService service = this.CreateService();
      HarnessToolbox toolbox = new HarnessToolbox();
      service.AttachToolbox(toolbox);

      StringAssert.Contains(toolbox.BuildHarnessPrompt(HarnessScope.CreateDefault()), "Always answer in German.");

      settings.SaveUserInstructions("Prefer explicit types.");
      SkillFile stored = SkillFile.Load(_UserPreferencesFile);
      Assert.AreEqual(FileBasedPromptingExtensionService.UserPreferencesSourceId, stored.Name);
      Assert.AreEqual("Prefer explicit types.", stored.Body);
      StringAssert.Contains(toolbox.BuildHarnessPrompt(HarnessScope.CreateDefault()), "Prefer explicit types.");

    }

    [TestMethod()]
    public void SecureAvailability_KeepsOfflineCopyRefreshedOnChatStartAndUsedAsFallback() {

      this.WriteSkill(Path.Combine("lib", "alpha", "SKILL.md"), "alpha", "Alpha skill", "Alpha version 1");
      string folder = Path.Combine(_TempDirectory, "lib");

      FileBasedPromptingExtensionService service = this.CreateService();
      HarnessToolbox toolbox = new HarnessToolbox();
      service.AttachToolbox(toolbox);

      string sourceId = service.AddSkillSource(SkillSourceKinds.Folder, folder, null);
      service.SetSkillSourceLevel(sourceId, SkillSourceLevels.InitializeAtStart);

      Assert.IsTrue(service.GetSkillSources().Single((s) => s.SourceId == sourceId).SupportsSecureAvailability);
      Assert.IsFalse(service.GetSkillSources().Single((s) => s.SourceId == FileBasedPromptingExtensionService.UserPreferencesSourceId).SupportsSecureAvailability);
      Assert.ThrowsException<InvalidOperationException>(
        () => service.SetSkillSourceSecureAvailability(FileBasedPromptingExtensionService.UserPreferencesSourceId, true)
      );

      service.SetSkillSourceSecureAvailability(sourceId, true);

      SkillSourceInfo info = service.GetSkillSources().Single((s) => s.SourceId == sourceId);
      Assert.IsTrue(info.SecureAvailability);
      Assert.IsFalse(string.IsNullOrWhiteSpace(info.OfflineCopyTimestampUtc));
      Assert.IsTrue(Directory.Exists(service.OfflineCopiesDirectory));

      //the original changes: the offline copy is refreshed on the next chat start
      this.WriteSkill(Path.Combine("lib", "alpha", "SKILL.md"), "alpha", "Alpha skill", "Alpha version 2");
      service.RefreshOfflineCopies();

      //the original becomes unreachable: the offline copy is used (and the reference is flagged)
      Directory.Delete(folder, true);

      FileBasedPromptingExtensionService reloaded = this.CreateService();
      HarnessToolbox reloadedToolbox = new HarnessToolbox();
      reloaded.AttachToolbox(reloadedToolbox);

      info = reloaded.GetSkillSources().Single((s) => s.SourceId == sourceId);
      Assert.IsTrue(info.IsReferenceBroken);
      StringAssert.Contains(info.LastError, "offline copy");
      CollectionAssert.AreEqual(new string[] { "alpha" }, info.SkillNames);
      StringAssert.Contains(reloadedToolbox.BuildHarnessPrompt(HarnessScope.CreateDefault()), "Alpha version 2");

      //an unreachable original never destroys the existing offline copy
      reloaded.RefreshOfflineCopies();
      StringAssert.Contains(reloadedToolbox.BuildHarnessPrompt(HarnessScope.CreateDefault()), "Alpha version 2");

      //disabling deletes the offline copy
      reloaded.SetSkillSourceSecureAvailability(sourceId, false);
      info = reloaded.GetSkillSources().Single((s) => s.SourceId == sourceId);
      Assert.IsFalse(info.SecureAvailability);
      Assert.AreEqual(0, info.SkillNames.Length);
      string promptAfterDisabling = reloadedToolbox.BuildHarnessPrompt(HarnessScope.CreateDefault());
      Assert.IsTrue(promptAfterDisabling == null || !promptAfterDisabling.Contains("Alpha version 2"));
      Assert.IsFalse(Directory.EnumerateFileSystemEntries(reloaded.OfflineCopiesDirectory).Any());

    }

    private static string ToGitHubFileJson(string content) {

      string base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));

      return "{\"type\":\"file\",\"encoding\":\"base64\",\"content\":\"" + base64 + "\"}";
    }

    [TestMethod()]
    public void UrlSource_IsLoadedIntoCacheWithSeparateMaskedCredential() {

      string remoteContent = "---\nname: remote-rules\ndescription: Remote rules\n---\n\nRemote body version 1";
      string receivedCredential = null;
      bool reachable = true;

      FileBasedPromptingExtensionService service = this.CreateService();
      service.RemoteContentLoader = (url, credential) => {

        receivedCredential = credential;

        if (!reachable) {
          throw new System.Net.Http.HttpRequestException("Host not reachable");
        }

        Assert.AreEqual("https://example.org/skills/SKILL.md", url);

        return remoteContent;
      };

      string sourceId = service.AddSkillSource("url", "https://example.org/skills/SKILL.md", "secret-token-12345");

      Assert.AreEqual("secret-token-12345", receivedCredential);

      SkillSourceInfo info = service.GetSkillSources().Single((s) => s.SourceId == sourceId);
      Assert.AreEqual(SkillSourceKinds.Url, info.Kind);
      Assert.IsFalse(info.IsReferenceBroken, info.LastError);
      CollectionAssert.AreEqual(new string[] { "remote-rules" }, info.SkillNames);
      Assert.IsTrue(info.HasCredentials);
      Assert.AreNotEqual("secret-token-12345", info.CredentialHint);

      //the secret never lands within the (non-secret) extensions file
      Assert.IsFalse(File.ReadAllText(_ExtensionsFile).Contains("secret-token-12345"));
      Assert.IsTrue(File.Exists(service.CredentialsFileFullName));

      HarnessToolbox toolbox = new HarnessToolbox();
      service.AttachToolbox(toolbox);
      service.SetSkillSourceLevel(sourceId, SkillSourceLevels.InitializeAtStart);
      Assert.IsTrue(toolbox.BuildHarnessPrompt(HarnessScope.CreateDefault()).Contains("Remote body version 1"));

      //refresh on chat start: unreachable -> cache is kept, but the source is flagged
      reachable = false;
      service.RefreshOfflineCopies();

      info = service.GetSkillSources().Single((s) => s.SourceId == sourceId);
      Assert.IsTrue(info.IsReferenceBroken);
      StringAssert.Contains(info.LastError, "Host not reachable");
      CollectionAssert.AreEqual(new string[] { "remote-rules" }, info.SkillNames);
      Assert.IsTrue(toolbox.BuildHarnessPrompt(HarnessScope.CreateDefault()).Contains("Remote body version 1"));

      //reachable again -> new content
      reachable = true;
      remoteContent = remoteContent.Replace("version 1", "version 2");
      service.RefreshOfflineCopies();

      info = service.GetSkillSources().Single((s) => s.SourceId == sourceId);
      Assert.IsFalse(info.IsReferenceBroken, info.LastError);
      Assert.IsTrue(toolbox.BuildHarnessPrompt(HarnessScope.CreateDefault()).Contains("Remote body version 2"));

      //removing the credential
      service.SetSkillSourceCredential(sourceId, "");
      Assert.IsNull(receivedCredential);
      Assert.IsFalse(service.GetSkillSources().Single((s) => s.SourceId == sourceId).HasCredentials);

      //removing the source also removes its cache
      service.RemoveSkillSource(sourceId);
      Assert.IsFalse(service.GetSkillSources().Any((s) => s.SourceId == sourceId));
      Assert.IsFalse(Directory.EnumerateFileSystemEntries(service.OfflineCopiesDirectory).Any());

    }

    [TestMethod()]
    public void GitHubSource_LoadsContainerOfSkillFoldersViaContentsApi() {

      string apiBase = "https://api.github.com/repos/my-org/my-skills/contents/";

      FileBasedPromptingExtensionService service = this.CreateService();
      service.RemoteContentLoader = (url, credential) => {

        if (url == apiBase + "skills?ref=main") {
          return "[{\"type\":\"dir\",\"name\":\"beta\"},{\"type\":\"dir\",\"name\":\"alpha\"},{\"type\":\"file\",\"name\":\"README.md\"}]";
        }

        if (url == apiBase + "skills/alpha/SKILL.md?ref=main") {
          return ToGitHubFileJson("---\nname: alpha\ndescription: Alpha skill\n---\n\nAlpha body");
        }

        if (url == apiBase + "skills/beta/SKILL.md?ref=main") {
          return ToGitHubFileJson("Beta body without front matter");
        }

        return null;
      };

      string sourceId = service.AddSkillSource("GitHub", "https://github.com/my-org/my-skills/tree/main/skills", null);

      SkillSourceInfo info = service.GetSkillSources().Single((s) => s.SourceId == sourceId);
      Assert.AreEqual(SkillSourceKinds.GitHub, info.Kind);
      Assert.AreEqual("https://github.com/my-org/my-skills/tree/main/skills", info.Location);
      Assert.IsFalse(info.IsReferenceBroken, info.LastError);
      Assert.IsFalse(info.HasCredentials);
      CollectionAssert.AreEqual(new string[] { "alpha", "beta" }, info.SkillNames);

      Assert.ThrowsException<ArgumentException>(
        () => service.AddSkillSource("GitHub", "https://example.org/not-github", null)
      );

    }

    [TestMethod()]
    public void Backend_InvokesNewChatHookAndToleratesFailures() {

      FileBasedPromptingSessionStore store = new FileBasedPromptingSessionStore(Path.Combine(_TempDirectory, "Chats"));
      CheapPromptingUIBackend backend = new CheapPromptingUIBackend(store, () => null, () => "Test", "Tester");

      int calls = 0;
      backend.OnNewChatStarting = () => {
        calls++;
        throw new IOException("Share not reachable");
      };

      string chatId = backend.BeginNewChat("");

      Assert.AreEqual(1, calls);
      Assert.IsTrue(backend.ChatIdExists(chatId));

    }

  }

}
