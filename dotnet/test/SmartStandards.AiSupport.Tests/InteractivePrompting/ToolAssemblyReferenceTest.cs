using AI.SmartStandards.InteractivePrompting.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AI.SmartStandards.InteractivePrompting {

  [TestClass()]
  public class ToolAssemblyReferenceTest {

    private string _TempDirectory;

    [TestInitialize()]
    public void Setup() {

      _TempDirectory = Path.Combine(Path.GetTempPath(), "AiSupportTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(_TempDirectory);

    }

    /// <summary>
    /// The referenced DLL stays locked while the test process runs (TDL-1, risk R4), so the folder is not deleted.
    /// </summary>
    private string CreateReferencedToolAssembly() {

      string sourceFile = typeof(IPromptingExtensionService).Assembly.Location;
      string toolDirectory = Path.Combine(_TempDirectory, "tools");
      Directory.CreateDirectory(toolDirectory);

      string targetFile = Path.Combine(toolDirectory, Path.GetFileName(sourceFile));
      File.Copy(sourceFile, targetFile);

      return targetFile;
    }

    private FileBasedPromptingExtensionService CreateService() {

      FileBasedPromptingExtensionService service = new FileBasedPromptingExtensionService(
        Path.Combine(_TempDirectory, "extensions.json"),
        Path.Combine(_TempDirectory, "user", "global-user-preferences", "SKILL.md"),
        Path.Combine(_TempDirectory, "public", "global-system-preferences", "SKILL.md")
      );
      service.SecretProtector = new KeyFileSecretProtector(Path.Combine(_TempDirectory, "local", "secrets.key"));

      return service;
    }

    [TestMethod()]
    public void AddToolAssemblyReference_ReferencesInPlaceWithoutCopy() {

      string toolFile = this.CreateReferencedToolAssembly();
      FileBasedPromptingExtensionService service = this.CreateService();
      service.AttachToolbox(new HarnessToolbox());

      ContractInterfaceInfo[] interfaces = service.AddToolAssemblyReference(toolFile);

      ContractInterfaceInfo contract = interfaces.Single((i) => i.InterfaceName == typeof(IPromptingExtensionService).FullName);
      Assert.AreEqual(Path.GetFullPath(toolFile), contract.AssemblyId);
      Assert.IsFalse(Directory.Exists(service.ToolAssembliesDirectory), "Referenced DLLs are never copied.");

      string sourceId = service.RegisterContractInterface(contract.AssemblyId, contract.InterfaceName, "http://localhost:1/api/", null);

      ToolSourceInfo source = service.GetToolSources().Single((s) => s.SourceId == sourceId);
      Assert.IsFalse(source.IsReferenceBroken, source.BrokenReason);
      Assert.IsTrue(source.ToolNames.Length > 0);

      CollectionAssert.Contains(ToolAssemblyResolver.GetDirectories(), Path.GetDirectoryName(Path.GetFullPath(toolFile)));

    }

    [TestMethod()]
    public void AddToolAssemblyReference_RejectsInvalidPaths() {

      FileBasedPromptingExtensionService service = this.CreateService();

      Assert.ThrowsException<ArgumentException>(() => service.AddToolAssemblyReference("relative\\tool.dll"));
      Assert.ThrowsException<ArgumentException>(() => service.AddToolAssemblyReference(Path.Combine(_TempDirectory, "tool.txt")));
      Assert.ThrowsException<FileNotFoundException>(() => service.AddToolAssemblyReference(Path.Combine(_TempDirectory, "missing.dll")));

    }

    [TestMethod()]
    public void MissingReferencedAssembly_MarksSourceAsBroken() {

      ContractInterfaceToolProvider provider = new ContractInterfaceToolProvider(
        Path.Combine(_TempDirectory, "missing", "tool.dll"), "Some.IContract", "contract-x"
      );

      ToolProviderContext context = new ToolProviderContext("contract-x", "http://localhost:1/api/", () => "{}", () => null, (json) => { });

      FileNotFoundException ex = Assert.ThrowsException<FileNotFoundException>(() => provider.CreateTools(context));
      StringAssert.Contains(ex.Message, "tool.dll");

    }

    [TestMethod()]
    public void Resolver_ProbesRegisteredDirectoriesWithCompatibleVersion() {

      string toolFile = this.CreateReferencedToolAssembly();
      string directory = Path.GetDirectoryName(Path.GetFullPath(toolFile));
      ToolAssemblyResolver.RegisterDirectory(directory);

      AssemblyName name = AssemblyName.GetAssemblyName(toolFile);

      Assert.AreEqual(toolFile, ToolAssemblyResolver.FindCandidate(new AssemblyName(name.Name), null), true);
      Assert.AreEqual(toolFile, ToolAssemblyResolver.FindCandidate(name, null), true);

      AssemblyName newer = new AssemblyName(name.Name);
      newer.Version = new Version(name.Version.Major + 1, 0);
      Assert.IsNull(ToolAssemblyResolver.FindCandidate(newer, directory));

      Assert.IsNull(ToolAssemblyResolver.FindCandidate(new AssemblyName("Does.Not.Exist"), directory));

    }

  }

}
