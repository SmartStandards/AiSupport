using AI.SmartStandards.InteractivePrompting.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class HarnessProfileManagerTest {

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
    public void StartupProfile_OverridesWithoutBeingRemembered() {

      FileBasedPromptingProfileService store = new FileBasedPromptingProfileService(_TempDirectory);
      string profileId = store.CreateProfile(null, "Work");

      HarnessProfileManager manager = new HarnessProfileManager(store, "work", null);

      Assert.AreEqual(profileId, manager.GetActiveProfileId());
      Assert.AreEqual(FileBasedPromptingProfileService.DefaultProfileId, store.GetActiveProfileId());
      Assert.IsTrue(manager.GetProfiles().Single((p) => p.ProfileId == profileId).IsActive);

    }

    [TestMethod()]
    public void UnknownStartupProfile_Throws() {

      FileBasedPromptingProfileService store = new FileBasedPromptingProfileService(_TempDirectory);

      Assert.ThrowsException<ArgumentException>(() => new HarnessProfileManager(store, "unknown", null));

    }

    [TestMethod()]
    public void ActivateProfile_SwitchesRuntimeAndIsolatesChats() {

      FileBasedPromptingProfileService store = new FileBasedPromptingProfileService(_TempDirectory);
      string profileId = store.CreateProfile(null, "Work");

      int configuredRuntimes = 0;
      HarnessProfileManager manager = new HarnessProfileManager(store, null, (runtime) => configuredRuntimes++);
      ActiveProfileUIBackend backend = new ActiveProfileUIBackend(manager);

      string defaultChatId = backend.BeginNewChat("Default chat");

      bool changed = false;
      manager.ActiveProfileChanged += (sender, e) => changed = true;
      manager.ActivateProfile(profileId);

      Assert.IsTrue(changed);
      Assert.AreEqual(2, configuredRuntimes);
      Assert.AreEqual(profileId, store.GetActiveProfileId());
      Assert.AreEqual("Work", manager.ActiveProfileDisplayName);
      Assert.IsFalse(backend.GetRecentChats().Any((c) => c.Id == defaultChatId));

      //chats of the previous profile remain reachable for running turns (retired runtime)
      Assert.AreEqual(0, backend.GetChatMessageCount(defaultChatId));

      Assert.ThrowsException<InvalidOperationException>(() => manager.DeleteProfile(profileId));

    }

    [TestMethod()]
    public void ToolProviderFactories_CreateOwnProviderPerProfile() {

      FileBasedPromptingProfileService store = new FileBasedPromptingProfileService(_TempDirectory);
      string profileId = store.CreateProfile(null, "Work");

      List<string> requestedProfileIds = new List<string>();

      IHarnessToolProviderFactory factory = new DelegateHarnessToolProviderFactory(
        "host-echo",
        (requestedProfileId) => {
          requestedProfileIds.Add(requestedProfileId);
          return new EchoToolProvider();
        }
      );

      HarnessProfileManager manager = new HarnessProfileManager(store, null, new IHarnessToolProviderFactory[] { factory }, null);
      HarnessProfileRuntime defaultRuntime = manager.ActiveRuntime;

      manager.ActivateProfile(profileId);
      HarnessProfileRuntime workRuntime = manager.ActiveRuntime;

      CollectionAssert.AreEqual(new string[] { FileBasedPromptingProfileService.DefaultProfileId, profileId }, requestedProfileIds);
      Assert.AreEqual("host-echo", defaultRuntime.Toolbox.GetToolSourceId("host_echo"));
      Assert.AreEqual("host-echo", workRuntime.Toolbox.GetToolSourceId("host_echo"));

      ToolSourceInfo source = workRuntime.ExtensionService.GetToolSources().Single((s) => s.SourceId == "host-echo");
      Assert.AreEqual(ToolSourceKinds.BuiltIn, source.Kind);

    }

    [TestMethod()]
    public void ToolProviderFactory_WithMismatchingProviderId_Throws() {

      FileBasedPromptingProfileService store = new FileBasedPromptingProfileService(_TempDirectory);

      IHarnessToolProviderFactory factory = new DelegateHarnessToolProviderFactory(
        "other-id", (requestedProfileId) => new EchoToolProvider()
      );

      Assert.ThrowsException<InvalidOperationException>(
        () => new HarnessProfileManager(store, null, new IHarnessToolProviderFactory[] { factory }, null)
      );

    }

    private sealed class EchoToolProvider : BuiltInToolProviderBase {

      public override string ProviderId {
        get {
          return "host-echo";
        }
      }

      public override string DisplayName {
        get {
          return "Host echo";
        }
      }

      public override IHarnessTool[] CreateTools(IToolProviderContext context) {

        IHarnessTool echo = new DelegateHarnessTool(
          "host_echo", "Returns the given text.", "{\"type\":\"object\"}", (arguments) => arguments.ToString()
        );

        return new IHarnessTool[] { echo };
      }

    }

  }

}
