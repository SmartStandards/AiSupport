using AI.SmartStandards.KnowledgeAccess;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AI.SmartStandards.KnowledgeAccess.Tests {

  /// <summary>
  /// Verifies provider-neutral semantics and safety invariants of
  /// <see cref="FileBasedKnowledgeRepository"/>.
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

        Assert.IsTrue(
          repository.TryMoveContent(
            documentArea,
            targetArea
          )
        );

        Assert.AreEqual(
          string.Empty,
          context.GetChildArea(
            repository,
            sourceArea,
            "Document"
          )
        );

        string movedArea = context.GetChildArea(
          repository,
          targetArea,
          "Document"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(movedArea)
        );

        Assert.Contains(
          "Persistent content",
          repository.GetAggregatedContent(movedArea),
          StringComparison.Ordinal
        );

        string[] deletedArtifacts = Directory.GetFiles(
          context.KnowledgeDirectory,
          "*.DELETED*.md",
          SearchOption.AllDirectories
        );

        CollectionAssert.AreEqual(
          new string[0],
          deletedArtifacts
        );
      }
    }

    /// <summary>
    /// Verifies that moving one Markdown heading reparents the addressed content scope
    /// without deleting either parent document.
    /// </summary>
    [TestMethod]
    public void TryMoveContent_HeadingBetweenDocuments_ReparentsOnlyHeadingScope() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "DocumentA",
            KnowledgeAreaKind.Content
          )
        );

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "DocumentB",
            KnowledgeAreaKind.Content
          )
        );

        string documentA = context.GetChildArea(
          repository,
          "/",
          "DocumentA"
        );

        string documentB = context.GetChildArea(
          repository,
          "/",
          "DocumentB"
        );

        Assert.IsTrue(
          repository.TryAppendContent(
            documentA,
            "# Section\nSection body"
          )
        );

        string section = context.GetChildArea(
          repository,
          documentA,
          "Section"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(section)
        );

        Assert.IsTrue(
          repository.TryMoveContent(
            section,
            documentB
          )
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(
            context.GetChildArea(
              repository,
              "/",
              "DocumentA"
            )
          )
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(
            context.GetChildArea(
              repository,
              "/",
              "DocumentB"
            )
          )
        );

        Assert.AreEqual(
          string.Empty,
          context.GetChildArea(
            repository,
            documentA,
            "Section"
          )
        );

        string movedSection = context.GetChildArea(
          repository,
          documentB,
          "Section"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(movedSection)
        );

        Assert.Contains(
          "Section body",
          repository.GetAggregatedContent(movedSection),
          StringComparison.Ordinal
        );
      }
    }

    /// <summary>
    /// Verifies that resource UIDs are positive repository-wide identities and are not
    /// reused for consecutive resources.
    /// </summary>
    [TestMethod]
    public void TryAddResource_AllocatesDistinctPositiveResourceUids() {
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

        long firstResourceUid;
        long secondResourceUid;

        Assert.IsTrue(
          repository.TryAddResource(
            documentArea,
            ".png",
            "image/png",
            new byte[] { 1, 2, 3 },
            out firstResourceUid
          )
        );

        Assert.IsTrue(
          repository.TryAddResource(
            documentArea,
            ".png",
            "image/png",
            new byte[] { 4, 5, 6 },
            out secondResourceUid
          )
        );

        Assert.IsTrue(
          firstResourceUid > 0
        );

        Assert.IsTrue(
          secondResourceUid > 0
        );

        Assert.AreNotEqual(
          firstResourceUid,
          secondResourceUid
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

        long resourceUid;

        Assert.IsTrue(
          repository.TryAddResource(
            documentArea,
            ".png",
            "image/png",
            new byte[] { 10, 20, 30 },
            out resourceUid
          )
        );

        string reference =
          "![Image](knowledge-resource:"
          + resourceUid.ToString()
          + ")";

        Assert.IsTrue(
          repository.TryAppendContent(
            documentArea,
            reference
          )
        );

        Assert.IsFalse(
          repository.TryDeleteResource(
            documentArea,
            resourceUid
          )
        );

        CollectionAssert.AreEqual(
          new byte[] { 10, 20, 30 },
          repository.GetResourceContent(
            documentArea,
            resourceUid
          )
        );
      }
    }

    /// <summary>
    /// Verifies that moving a heading between documents keeps every referenced resource
    /// resolvable with the same ResourceUid in the new resource scope.
    /// </summary>
    [TestMethod]
    public void TryMoveContent_HeadingWithResourceBetweenDocuments_PreservesResourceUid() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "DocumentA",
            KnowledgeAreaKind.Content
          )
        );

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "DocumentB",
            KnowledgeAreaKind.Content
          )
        );

        string documentA = context.GetChildArea(
          repository,
          "/",
          "DocumentA"
        );

        string documentB = context.GetChildArea(
          repository,
          "/",
          "DocumentB"
        );

        byte[] resourceContent =
          new byte[] { 11, 22, 33, 44 };

        long resourceUid;

        Assert.IsTrue(
          repository.TryAddResource(
            documentA,
            ".png",
            "image/png",
            resourceContent,
            out resourceUid
          )
        );

        string markdown =
          "# Section\n"
          + "![Image](knowledge-resource:"
          + resourceUid.ToString()
          + ")";

        Assert.IsTrue(
          repository.TryAppendContent(
            documentA,
            markdown
          )
        );

        string section = context.GetChildArea(
          repository,
          documentA,
          "Section"
        );

        Assert.IsTrue(
          repository.TryMoveContent(
            section,
            documentB
          )
        );

        string movedSection = context.GetChildArea(
          repository,
          documentB,
          "Section"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(movedSection)
        );

        Assert.Contains(
          "knowledge-resource:" + resourceUid.ToString(),
          repository.GetAggregatedContent(movedSection),
          StringComparison.Ordinal
        );

        CollectionAssert.AreEqual(
          resourceContent,
          repository.GetResourceContent(
            movedSection,
            resourceUid
          )
        );
      }
    }

    /// <summary>
    /// Verifies that replacing one multiply materialized ResourceUid updates every
    /// resource scope consistently.
    /// </summary>
    [TestMethod]
    public void TryReplaceResource_AfterCrossDocumentReference_UpdatesEveryMaterialization() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "DocumentA",
            KnowledgeAreaKind.Content
          )
        );

        Assert.IsTrue(
          repository.TryAddSubArea(
            "/",
            "DocumentB",
            KnowledgeAreaKind.Content
          )
        );

        string documentA = context.GetChildArea(
          repository,
          "/",
          "DocumentA"
        );

        string documentB = context.GetChildArea(
          repository,
          "/",
          "DocumentB"
        );

        long resourceUid;

        Assert.IsTrue(
          repository.TryAddResource(
            documentA,
            ".png",
            "image/png",
            new byte[] { 1, 1, 1 },
            out resourceUid
          )
        );

        string resourceReference =
          "knowledge-resource:"
          + resourceUid.ToString();

        Assert.IsTrue(
          repository.TryAppendContent(
            documentA,
            "Shared " + resourceReference + "\n\n# Section\nMoved " + resourceReference
          )
        );

        string section = context.GetChildArea(
          repository,
          documentA,
          "Section"
        );

        Assert.IsTrue(
          repository.TryMoveContent(
            section,
            documentB
          )
        );

        byte[] replacement =
          new byte[] { 9, 8, 7, 6 };

        Assert.IsTrue(
          repository.TryReplaceResource(
            documentA,
            resourceUid,
            ".png",
            "image/png",
            replacement
          )
        );

        CollectionAssert.AreEqual(
          replacement,
          repository.GetResourceContent(
            documentA,
            resourceUid
          )
        );

        CollectionAssert.AreEqual(
          replacement,
          repository.GetResourceContent(
            documentB,
            resourceUid
          )
        );
      }
    }

    /// <summary>
    /// Verifies that a soft-deleted document disappears from the logical area model while
    /// the physical Markdown content remains recoverable.
    /// </summary>
    [TestMethod]
    public void TryDelete_WithSoftDelete_RenamesDocumentAndOmitsLogicalArea() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateSoftDeleteRepository();

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

        Assert.IsTrue(
          repository.TryAppendContent(
            documentArea,
            "Recoverable content"
          )
        );

        Assert.IsTrue(
          repository.TryDelete(
            documentArea
          )
        );

        Assert.AreEqual(
          string.Empty,
          context.GetChildArea(
            repository,
            "/",
            "Document"
          )
        );

        string[] deletedFiles = Directory.GetFiles(
          context.KnowledgeDirectory,
          "Document.DELETED*.md",
          SearchOption.AllDirectories
        );

        Assert.AreEqual(
          1,
          deletedFiles.Length
        );

        Assert.Contains(
          "Recoverable content",
          File.ReadAllText(deletedFiles[0]),
          StringComparison.Ordinal
        );
      }
    }

    /// <summary>
    /// Verifies that renaming a document also renames its physical resource companion
    /// files while preserving the logical ResourceUid and resource content.
    /// </summary>
    [TestMethod]
    public void TryRename_DocumentWithResource_PreservesResourceUidAndRenamesCompanionFile() {
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

        byte[] resourceContent =
          new byte[] { 21, 22, 23, 24 };

        long resourceUid;

        Assert.IsTrue(
          repository.TryAddResource(
            originalArea,
            ".png",
            "image/png",
            resourceContent,
            out resourceUid
          )
        );

        Assert.IsTrue(
          repository.TryAppendContent(
            originalArea,
            "![Image](knowledge-resource:"
            + resourceUid.ToString()
            + ")"
          )
        );

        string oldCompanionFile = Path.Combine(
          context.KnowledgeDirectory,
          "OriginalDocument.Res"
          + resourceUid.ToString()
          + ".png"
        );

        Assert.IsTrue(
          File.Exists(oldCompanionFile)
        );

        Assert.IsTrue(
          repository.TryRename(
            originalArea,
            "RenamedDocument"
          )
        );

        Assert.AreEqual(
          string.Empty,
          context.GetChildArea(
            repository,
            "/",
            "OriginalDocument"
          )
        );

        string renamedArea = context.GetChildArea(
          repository,
          "/",
          "RenamedDocument"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(renamedArea)
        );

        string newCompanionFile = Path.Combine(
          context.KnowledgeDirectory,
          "RenamedDocument.Res"
          + resourceUid.ToString()
          + ".png"
        );

        Assert.IsFalse(
          File.Exists(oldCompanionFile)
        );

        Assert.IsTrue(
          File.Exists(newCompanionFile)
        );

        CollectionAssert.AreEqual(
          resourceContent,
          repository.GetResourceContent(
            renamedArea,
            resourceUid
          )
        );

        Assert.IsTrue(
          repository.GetAggregatedContent(
            renamedArea
          ).Contains(
            "knowledge-resource:" + resourceUid.ToString(),
            StringComparison.Ordinal
          )
        );
      }
    }

  }
}
