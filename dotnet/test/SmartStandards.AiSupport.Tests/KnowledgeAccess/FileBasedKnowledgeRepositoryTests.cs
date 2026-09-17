using AI.SmartStandards.KnowledgeAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace AI.SmartStandards.KnowledgeAccess.Tests {

  /// <summary>
  /// Verifies provider-neutral semantics and FileBased resource mapping invariants.
  /// </summary>
  [TestClass]
  public sealed class FileBasedKnowledgeRepositoryTests {

    /// <summary>
    /// Verifies that moving a document changes only its parent and does not invoke
    /// soft-delete semantics.
    /// </summary>
    [TestMethod]
    public void TryMoveContent_DocumentToNewParent_MovesPhysicalDocumentWithoutSoftDeleteArtifact() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateSoftDeleteRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "Source",
            KnowledgeAreaKind.Structural
          )
        );

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "Target",
            KnowledgeAreaKind.Structural
          )
        );

        string sourceArea = context.GetChildArea(
          repository,
          "/",
          "Source"
        );

        string targetArea = context.GetChildArea(
          repository,
          "/",
          "Target"
        );

        Assert.IsTrue(
          repository.TryAddSubArea(
            sourceArea,
            "Document",
            KnowledgeAreaKind.Content
          )
        );

        string documentArea = context.GetChildArea(
          repository,
          sourceArea,
          "Document"
        );

        Assert.IsTrue(
          repository.TryAppendContent(
            documentArea,
            "Persistent content"
          )
        );

        KnowledgeResourceIdChange[] resourceIdChanges;

        Assert.IsTrue(
          repository.TryMoveContent(
            documentArea,
            targetArea,
            out resourceIdChanges
          )
        );

        Assert.AreEqual(
          0,
          resourceIdChanges.Length
        );

        string movedArea = context.GetChildArea(
          repository,
          targetArea,
          "Document"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(movedArea)
        );

        string[] deletedArtifacts = Directory.GetFiles(
          context.KnowledgeDirectory,
          "*.DELETED*.md",
          SearchOption.AllDirectories
        );

        Assert.AreEqual(
          0,
          deletedArtifacts.Length
        );
      }
    }

    /// <summary>
    /// Verifies that ordinary physical Markdown image references are exposed through the
    /// repository as opaque knowledge-resource references without modifying the file.
    /// </summary>
    [TestMethod]
    public void GetAggregatedContent_PhysicalImageReference_ReturnsOpaqueKnowledgeResourceReference() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        string markdownPath = Path.Combine(
          context.KnowledgeDirectory,
          "Article.md"
        );

        string imagePath = Path.Combine(
          context.KnowledgeDirectory,
          "diagram.png"
        );

        File.WriteAllText(
          markdownPath,
          "![Architecture](diagram.png)"
        );

        File.WriteAllBytes(
          imagePath,
          new byte[] { 1, 2, 3, 4 }
        );

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        string articleArea = context.GetChildArea(
          repository,
          "/",
          "Article"
        );

        string content = repository.GetAggregatedContent(
          articleArea
        );

        Assert.IsTrue(
          content.Contains(
            "knowledge-resource:",
            StringComparison.Ordinal
          )
        );

        Assert.IsFalse(
          content.Contains(
            "diagram.png",
            StringComparison.Ordinal
          )
        );

        Assert.AreEqual(
          "![Architecture](diagram.png)",
          File.ReadAllText(markdownPath)
        );

        KnowledgeResourceInfo[] resources =
          repository.GetResources(
            articleArea
          );

        Assert.AreEqual(
          1,
          resources.Length
        );

        Assert.AreEqual(
          "diagram.png",
          resources[0].FileName
        );
      }
    }

    /// <summary>
    /// Verifies that canonical knowledge-resource references are serialized back to normal
    /// relative physical Markdown paths.
    /// </summary>
    [TestMethod]
    public void TryAppendContent_KnowledgeResourceReference_WritesNormalPhysicalMarkdownPath() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "Article",
            KnowledgeAreaKind.Content
          )
        );

        string articleArea = context.GetChildArea(
          repository,
          "/",
          "Article"
        );

        string resourceId;

        Assert.IsTrue(
          repository.TryAddResource(
            articleArea,
            "diagram.png",
            "image/png",
            new byte[] { 5, 6, 7 },
            out resourceId
          )
        );

        Assert.IsTrue(
          repository.TryAppendContent(
            articleArea,
            "![Architecture](knowledge-resource:"
            + resourceId
            + ")"
          )
        );

        string markdownPath = Path.Combine(
          context.KnowledgeDirectory,
          "Article.md"
        );

        string physicalContent = File.ReadAllText(
          markdownPath
        );

        Assert.AreEqual(
          "![Architecture](diagram.png)",
          physicalContent.Trim()
        );

        Assert.IsFalse(
          physicalContent.Contains(
            "knowledge-resource:",
            StringComparison.Ordinal
          )
        );
      }
    }

    /// <summary>
    /// Verifies that resources created without a usable preferred filename receive the
    /// provider-owned document fallback naming convention.
    /// </summary>
    [TestMethod]
    public void TryAddResource_WithoutPreferredName_UsesOwnedSnowflakeFallbackFileName() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "Article",
            KnowledgeAreaKind.Content
          )
        );

        string articleArea = context.GetChildArea(
          repository,
          "/",
          "Article"
        );

        string resourceId;

        Assert.IsTrue(
          repository.TryAddResource(
            articleArea,
            string.Empty,
            "image/png",
            new byte[] { 8, 9, 10 },
            out resourceId
          )
        );

        Assert.IsFalse(
          string.IsNullOrWhiteSpace(resourceId)
        );

        string[] files = Directory.GetFiles(
          context.KnowledgeDirectory,
          "Article.Res*.png",
          SearchOption.TopDirectoryOnly
        );

        Assert.AreEqual(
          1,
          files.Length
        );

        CollectionAssert.AreEqual(
          new byte[] { 8, 9, 10 },
          repository.GetResourceContent(
            resourceId
          )
        );
      }
    }

    /// <summary>
    /// Verifies that a referenced resource cannot be deleted.
    /// </summary>
    [TestMethod]
    public void TryDeleteResource_ReferencedResource_IsRejected() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "Document",
            KnowledgeAreaKind.Content
          )
        );

        string documentArea = context.GetChildArea(
          repository,
          "/",
          "Document"
        );

        string resourceId;

        Assert.IsTrue(
          repository.TryAddResource(
            documentArea,
            "diagram.png",
            "image/png",
            new byte[] { 10, 20, 30 },
            out resourceId
          )
        );

        Assert.IsTrue(
          repository.TryAppendContent(
            documentArea,
            "![Image](knowledge-resource:"
            + resourceId
            + ")"
          )
        );

        Assert.IsFalse(
          repository.TryDeleteResource(
            resourceId
          )
        );
      }
    }

    /// <summary>
    /// Verifies that a document-owned generated resource moves with its document, keeps
    /// the physical Markdown reference simple, and reports the resulting ResourceId change.
    /// </summary>
    [TestMethod]
    public void TryMoveContent_DocumentWithOwnedResource_MovesResourceAndReportsResourceIdChange() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "Source",
            KnowledgeAreaKind.Structural
          )
        );

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "Target",
            KnowledgeAreaKind.Structural
          )
        );

        string sourceArea = context.GetChildArea(
          repository,
          "/",
          "Source"
        );

        string targetArea = context.GetChildArea(
          repository,
          "/",
          "Target"
        );

        Assert.IsTrue(
          repository.TryAddSubArea(
            sourceArea,
            "Article",
            KnowledgeAreaKind.Content
          )
        );

        string articleArea = context.GetChildArea(
          repository,
          sourceArea,
          "Article"
        );

        string originalResourceId;

        Assert.IsTrue(
          repository.TryAddResource(
            articleArea,
            string.Empty,
            "image/png",
            new byte[] { 11, 12, 13 },
            out originalResourceId
          )
        );

        Assert.IsTrue(
          repository.TryAppendContent(
            articleArea,
            "![Image](knowledge-resource:"
            + originalResourceId
            + ")"
          )
        );

        KnowledgeResourceIdChange[] resourceIdChanges;

        Assert.IsTrue(
          repository.TryMoveContent(
            articleArea,
            targetArea,
            out resourceIdChanges
          )
        );

        Assert.AreEqual(
          1,
          resourceIdChanges.Length
        );

        Assert.AreEqual(
          originalResourceId,
          resourceIdChanges[0].PreviousResourceId
        );

        Assert.AreNotEqual(
          originalResourceId,
          resourceIdChanges[0].CurrentResourceId
        );

        string movedMarkdownPath = Path.Combine(
          context.KnowledgeDirectory,
          "Target",
          "Article.md"
        );

        string physicalContent = File.ReadAllText(
          movedMarkdownPath
        );

        Assert.IsTrue(
          physicalContent.Contains(
            "Article.Res",
            StringComparison.Ordinal
          )
        );

        Assert.IsFalse(
          physicalContent.Contains(
            "knowledge-resource:",
            StringComparison.Ordinal
          )
        );

        CollectionAssert.AreEqual(
          new byte[] { 11, 12, 13 },
          repository.GetResourceContent(
            resourceIdChanges[0].CurrentResourceId
          )
        );
      }
    }

    /// <summary>
    /// Verifies that a free resource remains at its native location when a referencing
    /// document moves and that the physical Markdown path is repaired automatically.
    /// </summary>
    [TestMethod]
    public void TryMoveContent_DocumentWithFreeResource_RepairsPhysicalRelativePathWithoutChangingResourceId() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        Directory.CreateDirectory(
          Path.Combine(
            context.KnowledgeDirectory,
            "Source"
          )
        );

        Directory.CreateDirectory(
          Path.Combine(
            context.KnowledgeDirectory,
            "Target"
          )
        );

        string sourceMarkdown = Path.Combine(
          context.KnowledgeDirectory,
          "Source",
          "Article.md"
        );

        string sharedImage = Path.Combine(
          context.KnowledgeDirectory,
          "Source",
          "CompanyLogo.png"
        );

        File.WriteAllText(
          sourceMarkdown,
          "![Logo](CompanyLogo.png)"
        );

        File.WriteAllBytes(
          sharedImage,
          new byte[] { 20, 21, 22 }
        );

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        string sourceArea = context.GetChildArea(
          repository,
          "/",
          "Source"
        );

        string targetArea = context.GetChildArea(
          repository,
          "/",
          "Target"
        );

        string articleArea = context.GetChildArea(
          repository,
          sourceArea,
          "Article"
        );

        KnowledgeResourceInfo originalResource =
          repository.GetResources(articleArea)[0];

        KnowledgeResourceIdChange[] resourceIdChanges;

        Assert.IsTrue(
          repository.TryMoveContent(
            articleArea,
            targetArea,
            out resourceIdChanges
          )
        );

        Assert.AreEqual(
          0,
          resourceIdChanges.Length
        );

        Assert.IsTrue(
          File.Exists(sharedImage)
        );

        string movedMarkdown = File.ReadAllText(
          Path.Combine(
            context.KnowledgeDirectory,
            "Target",
            "Article.md"
          )
        );

        Assert.AreEqual(
          "![Logo](../Source/CompanyLogo.png)",
          movedMarkdown.Trim()
        );

        string movedArea = context.GetChildArea(
          repository,
          targetArea,
          "Article"
        );

        KnowledgeResourceInfo movedResource =
          repository.GetResources(movedArea)[0];

        Assert.AreEqual(
          originalResource.ResourceId,
          movedResource.ResourceId
        );
      }
    }

    /// <summary>
    /// Verifies that renaming a document also renames an owned resource and reports the
    /// provider-native ResourceId transition.
    /// </summary>
    [TestMethod]
    public void TryRename_DocumentWithOwnedResource_RenamesResourceAndReportsResourceIdChange() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "OriginalDocument",
            KnowledgeAreaKind.Content
          )
        );

        string originalArea = context.GetChildArea(
          repository,
          "/",
          "OriginalDocument"
        );

        string originalResourceId;

        Assert.IsTrue(
          repository.TryAddResource(
            originalArea,
            string.Empty,
            "image/png",
            new byte[] { 31, 32, 33 },
            out originalResourceId
          )
        );

        Assert.IsTrue(
          repository.TryAppendContent(
            originalArea,
            "![Image](knowledge-resource:"
            + originalResourceId
            + ")"
          )
        );

        KnowledgeResourceIdChange[] resourceIdChanges;

        Assert.IsTrue(
          repository.TryRename(
            originalArea,
            "RenamedDocument",
            out resourceIdChanges
          )
        );

        Assert.AreEqual(
          1,
          resourceIdChanges.Length
        );

        Assert.AreEqual(
          originalResourceId,
          resourceIdChanges[0].PreviousResourceId
        );

        string[] renamedResources = Directory.GetFiles(
          context.KnowledgeDirectory,
          "RenamedDocument.Res*.png",
          SearchOption.TopDirectoryOnly
        );

        Assert.AreEqual(
          1,
          renamedResources.Length
        );

        string markdown = File.ReadAllText(
          Path.Combine(
            context.KnowledgeDirectory,
            "RenamedDocument.md"
          )
        );

        Assert.IsTrue(
          markdown.Contains(
            "RenamedDocument.Res",
            StringComparison.Ordinal
          )
        );
      }
    }

    /// <summary>
    /// Verifies that the legacy numeric knowledge-resource representation remains readable
    /// and is converted to the current opaque identifier at the repository boundary.
    /// </summary>
    [TestMethod]
    public void GetAggregatedContent_LegacyNumericResourceReference_IsMappedToOpaqueResourceId() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        File.WriteAllText(
          Path.Combine(
            context.KnowledgeDirectory,
            "Legacy.md"
          ),
          "![Image](knowledge-resource:123456)"
        );

        File.WriteAllBytes(
          Path.Combine(
            context.KnowledgeDirectory,
            "Legacy.Res123456.png"
          ),
          new byte[] { 40, 41, 42 }
        );

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        string legacyArea = context.GetChildArea(
          repository,
          "/",
          "Legacy"
        );

        string content = repository.GetAggregatedContent(
          legacyArea
        );

        Assert.IsTrue(
          content.Contains(
            "knowledge-resource:1.",
            StringComparison.Ordinal
          )
        );

        Assert.IsFalse(
          content.Contains(
            "knowledge-resource:123456",
            StringComparison.Ordinal
          )
        );
      }
    }
  }
}
