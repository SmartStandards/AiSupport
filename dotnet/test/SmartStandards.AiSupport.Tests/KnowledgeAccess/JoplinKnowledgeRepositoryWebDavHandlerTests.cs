using AI.SmartStandards.KnowledgeAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AI.SmartStandards.KnowledgeAccess.Tests {

  /// <summary>
  /// Verifies Joplin synchronization state transitions against the provider-neutral
  /// knowledge repository contract.
  /// </summary>
  [TestClass]
  public sealed class JoplinKnowledgeRepositoryWebDavHandlerTests {

    private const string _FolderAId = "11111111111111111111111111111111";
    private const string _FolderBId = "22222222222222222222222222222222";
    private const string _NoteAId = "33333333333333333333333333333333";
    private const string _NoteBId = "44444444444444444444444444444444";
    private const string _ResourceId = "55555555555555555555555555555555";

    /// <summary>
    /// Verifies that a note arriving before its parent notebook is accepted as pending
    /// and materialized after the notebook arrives.
    /// </summary>
    [TestMethod]
    public void Put_NoteBeforeParentFolder_MaterializesAfterParentArrives() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        IActionResult noteResult = context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Pending Note",
            _FolderAId,
            "Pending body"
          )
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(noteResult)
        );

        Assert.AreEqual(
          string.Empty,
          context.GetChildArea(
            repository,
            "/",
            "Pending Note"
          )
        );

        IActionResult folderResult = context.PutText(
          handler,
          _FolderAId + ".md",
          context.CreateJoplinFolderItem(
            _FolderAId,
            "Folder A",
            string.Empty
          )
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(folderResult)
        );

        string folderArea = context.GetChildArea(
          repository,
          "/",
          "Folder A"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(folderArea)
        );

        string noteArea = context.GetChildArea(
          repository,
          folderArea,
          "Pending Note"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(noteArea)
        );

        Assert.Contains(
          "Pending body",
          repository.GetAggregatedContent(noteArea),
          StringComparison.Ordinal
        );
      }
    }

    /// <summary>
    /// Verifies that a Joplin parent_id change is mapped through the provider-neutral
    /// TryMoveContent operation and changes the note's logical parent.
    /// </summary>
    [TestMethod]
    public void Put_ExistingNoteWithChangedParent_MovesKnowledgeArea() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        context.PutText(
          handler,
          _FolderAId + ".md",
          context.CreateJoplinFolderItem(
            _FolderAId,
            "Folder A",
            string.Empty
          )
        );

        context.PutText(
          handler,
          _FolderBId + ".md",
          context.CreateJoplinFolderItem(
            _FolderBId,
            "Folder B",
            string.Empty
          )
        );

        context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Movable Note",
            _FolderAId,
            "Move me"
          )
        );

        string folderA = context.GetChildArea(
          repository,
          "/",
          "Folder A"
        );

        string folderB = context.GetChildArea(
          repository,
          "/",
          "Folder B"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(
            context.GetChildArea(
              repository,
              folderA,
              "Movable Note"
            )
          )
        );

        IActionResult moveResult = context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Movable Note",
            _FolderBId,
            "Move me"
          )
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(moveResult)
        );

        Assert.AreEqual(
          string.Empty,
          context.GetChildArea(
            repository,
            folderA,
            "Movable Note"
          )
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(
            context.GetChildArea(
              repository,
              folderB,
              "Movable Note"
            )
          )
        );
      }
    }

    /// <summary>
    /// Verifies that deleting a projected Joplin note suppresses the sync projection but
    /// never deletes the backing knowledge area.
    /// </summary>
    [TestMethod]
    public void Delete_ProjectedNote_DoesNotDeleteKnowledgeArea() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Protected Note",
            string.Empty,
            "Knowledge must survive"
          )
        );

        string noteArea = context.GetChildArea(
          repository,
          "/",
          "Protected Note"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(noteArea)
        );

        IActionResult deleteResult = context.Delete(
          handler,
          _NoteAId + ".md"
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(deleteResult)
        );

        string survivingArea = context.GetChildArea(
          repository,
          "/",
          "Protected Note"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(survivingArea)
        );

        Assert.Contains(
          "Knowledge must survive",
          repository.GetAggregatedContent(survivingArea),
          StringComparison.Ordinal
        );
      }
    }

    /// <summary>
    /// Verifies that an unchanged projected note is serialized identically on repeated
    /// GET operations.
    /// </summary>
    [TestMethod]
    public void Get_UnchangedProjectedNote_IsByteStableAcrossRepeatedReads() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Stable Note",
            string.Empty,
            "Stable body"
          )
        );

        IActionResult firstResult = context.Get(
          handler,
          _NoteAId + ".md"
        );

        IActionResult secondResult = context.Get(
          handler,
          _NoteAId + ".md"
        );

        FileContentResult firstFile =
          firstResult as FileContentResult;

        FileContentResult secondFile =
          secondResult as FileContentResult;

        Assert.IsNotNull(
          firstFile
        );

        Assert.IsNotNull(
          secondFile
        );

        CollectionAssert.AreEqual(
          firstFile.FileContents,
          secondFile.FileContents
        );
      }
    }

    /// <summary>
    /// Verifies that a Joplin resource referenced by a note is imported into the
    /// repository and translated to the canonical knowledge-resource URI.
    /// </summary>
    [TestMethod]
    public void Put_JoplinResourceAndReferencingNote_ImportsCanonicalKnowledgeResource() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        byte[] imageBytes =
          new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 };

        context.PutBytes(
          handler,
          ".resource/" + _ResourceId,
          imageBytes
        );

        context.PutText(
          handler,
          _ResourceId + ".md",
          context.CreateJoplinResourceItem(
            _ResourceId,
            "image.png",
            "image.png",
            "image/png",
            "png"
          )
        );

        IActionResult noteResult = context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Image Note",
            string.Empty,
            "![Image](:/" + _ResourceId + ")"
          )
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(noteResult)
        );

        string noteArea = context.GetChildArea(
          repository,
          "/",
          "Image Note"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(noteArea)
        );

        KnowledgeResourceInfo[] resources =
          repository.GetResources(
            noteArea
          );

        Assert.AreEqual(
          1,
          resources.Length
        );

        KnowledgeResourceInfo resource =
          resources[0];

        Assert.Contains(
          "knowledge-resource:" + resource.ResourceUid.ToString(),
          repository.GetAggregatedContent(noteArea),
          StringComparison.Ordinal
        );

        CollectionAssert.AreEqual(
          imageBytes,
          repository.GetResourceContent(
            noteArea,
            resource.ResourceUid
          )
        );
      }
    }

    /// <summary>
    /// Verifies that a Joplin internal link to another note is not mistaken for a binary
    /// resource reference merely because both use the :/ID syntax.
    /// </summary>
    [TestMethod]
    public void Put_JoplinNoteLink_DoesNotTranslateLinkAsResource() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Target Note",
            string.Empty,
            "Target body"
          )
        );

        string sourceBody =
          "[Open target](:/"
          + _NoteAId
          + ")";

        IActionResult sourceResult = context.PutText(
          handler,
          _NoteBId + ".md",
          context.CreateJoplinNoteItem(
            _NoteBId,
            "Source Note",
            string.Empty,
            sourceBody
          )
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(sourceResult)
        );

        string sourceArea = context.GetChildArea(
          repository,
          "/",
          "Source Note"
        );

        string storedContent =
          repository.GetAggregatedContent(
            sourceArea
          );

        Assert.Contains(
          ":/" + _NoteAId,
          storedContent,
          StringComparison.Ordinal
        );

        Assert.DoesNotContain(
          "knowledge-resource:",
          storedContent,
          StringComparison.Ordinal
        );
      }
    }

    /// <summary>
    /// Verifies that replacing an already mapped Joplin resource updates the same
    /// Knowledge ResourceUid instead of allocating a second logical resource.
    /// </summary>
    [TestMethod]
    public void Put_ExistingJoplinResourceBlob_ReplacesMappedKnowledgeResource() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        byte[] initialBytes =
          new byte[] { 1, 2, 3 };

        context.PutBytes(
          handler,
          ".resource/" + _ResourceId,
          initialBytes
        );

        context.PutText(
          handler,
          _ResourceId + ".md",
          context.CreateJoplinResourceItem(
            _ResourceId,
            "image.png",
            "image.png",
            "image/png",
            "png"
          )
        );

        context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Image Note",
            string.Empty,
            "![Image](:/" + _ResourceId + ")"
          )
        );

        string noteArea = context.GetChildArea(
          repository,
          "/",
          "Image Note"
        );

        KnowledgeResourceInfo[] originalResources =
          repository.GetResources(
            noteArea
          );

        Assert.AreEqual(
          1,
          originalResources.Length
        );

        KnowledgeResourceInfo originalResource =
          originalResources[0];

        long originalUid =
          originalResource.ResourceUid;

        byte[] replacementBytes =
          new byte[] { 9, 8, 7, 6 };

        IActionResult replaceResult = context.PutBytes(
          handler,
          ".resource/" + _ResourceId,
          replacementBytes
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(replaceResult)
        );

        KnowledgeResourceInfo[] updatedResources =
          repository.GetResources(
            noteArea
          );

        Assert.AreEqual(
          1,
          updatedResources.Length
        );

        KnowledgeResourceInfo updatedResource =
          updatedResources[0];

        Assert.AreEqual(
          originalUid,
          updatedResource.ResourceUid
        );

        CollectionAssert.AreEqual(
          replacementBytes,
          repository.GetResourceContent(
            noteArea,
            originalUid
          )
        );
      }
    }

    /// <summary>
    /// Verifies that deleting a Joplin resource projection never deletes the logical
    /// knowledge resource from the repository.
    /// </summary>
    [TestMethod]
    public void Delete_JoplinResource_DoesNotDeleteKnowledgeResource() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        byte[] imageBytes =
          new byte[] { 3, 4, 5 };

        context.PutBytes(
          handler,
          ".resource/" + _ResourceId,
          imageBytes
        );

        context.PutText(
          handler,
          _ResourceId + ".md",
          context.CreateJoplinResourceItem(
            _ResourceId,
            "image.png",
            "image.png",
            "image/png",
            "png"
          )
        );

        context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Image Note",
            string.Empty,
            "![Image](:/" + _ResourceId + ")"
          )
        );

        string noteArea = context.GetChildArea(
          repository,
          "/",
          "Image Note"
        );

        KnowledgeResourceInfo[] resources =
          repository.GetResources(
            noteArea
          );

        Assert.AreEqual(
          1,
          resources.Length
        );

        KnowledgeResourceInfo resource =
          resources[0];

        IActionResult deleteResult = context.Delete(
          handler,
          ".resource/" + _ResourceId
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(deleteResult)
        );

        CollectionAssert.AreEqual(
          imageBytes,
          repository.GetResourceContent(
            noteArea,
            resource.ResourceUid
          )
        );
      }
    }

    /// <summary>
    /// Verifies that changing the parent_id of an existing Joplin folder moves the
    /// corresponding structural knowledge area below the new parent.
    /// </summary>
    [TestMethod]
    public void Put_ExistingFolderWithChangedParent_MovesStructuralKnowledgeArea() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        string childFolderId =
          "66666666666666666666666666666666";

        context.PutText(
          handler,
          _FolderAId + ".md",
          context.CreateJoplinFolderItem(
            _FolderAId,
            "Folder A",
            string.Empty
          )
        );

        context.PutText(
          handler,
          _FolderBId + ".md",
          context.CreateJoplinFolderItem(
            _FolderBId,
            "Folder B",
            string.Empty
          )
        );

        context.PutText(
          handler,
          childFolderId + ".md",
          context.CreateJoplinFolderItem(
            childFolderId,
            "Child Folder",
            _FolderAId
          )
        );

        string folderA = context.GetChildArea(
          repository,
          "/",
          "Folder A"
        );

        string folderB = context.GetChildArea(
          repository,
          "/",
          "Folder B"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(
            context.GetChildArea(
              repository,
              folderA,
              "Child Folder"
            )
          )
        );

        IActionResult moveResult = context.PutText(
          handler,
          childFolderId + ".md",
          context.CreateJoplinFolderItem(
            childFolderId,
            "Child Folder",
            _FolderBId
          )
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(moveResult)
        );

        Assert.AreEqual(
          string.Empty,
          context.GetChildArea(
            repository,
            folderA,
            "Child Folder"
          )
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(
            context.GetChildArea(
              repository,
              folderB,
              "Child Folder"
            )
          )
        );
      }
    }

    /// <summary>
    /// Verifies that renaming a Joplin note containing a mapped resource preserves the
    /// logical ResourceUid and keeps the resource resolvable from the renamed area.
    /// </summary>
    [TestMethod]
    public void Put_ExistingNoteWithChangedTitle_PreservesMappedResourceUid() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        byte[] imageBytes =
          new byte[] { 31, 32, 33, 34 };

        context.PutBytes(
          handler,
          ".resource/" + _ResourceId,
          imageBytes
        );

        context.PutText(
          handler,
          _ResourceId + ".md",
          context.CreateJoplinResourceItem(
            _ResourceId,
            "image.png",
            "image.png",
            "image/png",
            "png"
          )
        );

        context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Original Note",
            string.Empty,
            "![Image](:/" + _ResourceId + ")"
          )
        );

        string originalArea = context.GetChildArea(
          repository,
          "/",
          "Original Note"
        );

        KnowledgeResourceInfo[] originalResources =
          repository.GetResources(
            originalArea
          );

        Assert.AreEqual(
          1,
          originalResources.Length
        );

        long originalResourceUid =
          originalResources[0].ResourceUid;

        IActionResult renameResult = context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Renamed Note",
            string.Empty,
            "![Image](:/" + _ResourceId + ")"
          )
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(renameResult)
        );

        Assert.AreEqual(
          string.Empty,
          context.GetChildArea(
            repository,
            "/",
            "Original Note"
          )
        );

        string renamedArea = context.GetChildArea(
          repository,
          "/",
          "Renamed Note"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(renamedArea)
        );

        KnowledgeResourceInfo[] renamedResources =
          repository.GetResources(
            renamedArea
          );

        Assert.AreEqual(
          1,
          renamedResources.Length
        );

        Assert.AreEqual(
          originalResourceUid,
          renamedResources[0].ResourceUid
        );

        CollectionAssert.AreEqual(
          imageBytes,
          repository.GetResourceContent(
            renamedArea,
            originalResourceUid
          )
        );
      }
    }

    /// <summary>
    /// Verifies that a newly created Joplin identity can rebind to an existing knowledge
    /// area after the previous identity was suppressed, without creating a duplicate area.
    /// </summary>
    [TestMethod]
    public void Put_NewIdentityForSuppressedLogicalNote_RebindsExistingKnowledgeArea() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        context.PutText(
          handler,
          _NoteAId + ".md",
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Conflict Note",
            string.Empty,
            "Original body"
          )
        );

        string originalArea = context.GetChildArea(
          repository,
          "/",
          "Conflict Note"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(originalArea)
        );

        IActionResult deleteResult = context.Delete(
          handler,
          _NoteAId + ".md"
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(deleteResult)
        );

        string survivingArea = context.GetChildArea(
          repository,
          "/",
          "Conflict Note"
        );

        Assert.IsFalse(
          string.IsNullOrEmpty(survivingArea)
        );

        IActionResult rebindResult = context.PutText(
          handler,
          _NoteBId + ".md",
          context.CreateJoplinNoteItem(
            _NoteBId,
            "Conflict Note",
            string.Empty,
            "Rebound body"
          )
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(rebindResult)
        );

        string[] rootAreas =
          repository.GetAreas(
            false,
            "/"
          );

        int matchingAreaCount = 0;

        foreach (string area in rootAreas) {
          if (string.Equals(
                repository.GetAreaName(area),
                "Conflict Note",
                StringComparison.Ordinal
              )) {
            matchingAreaCount++;
          }
        }

        Assert.AreEqual(
          1,
          matchingAreaCount
        );

        string reboundArea = context.GetChildArea(
          repository,
          "/",
          "Conflict Note"
        );

        Assert.IsTrue(
          repository.GetAggregatedContent(
            reboundArea
          ).Contains(
            "Rebound body",
            StringComparison.Ordinal
          )
        );

        Assert.IsFalse(
          repository.GetAggregatedContent(
            reboundArea
          ).Contains(
            "Original body",
            StringComparison.Ordinal
          )
        );
      }
    }

    /// <summary>
    /// Verifies that submitting an identical Joplin note repeatedly is idempotent from
    /// the knowledge repository perspective and never duplicates the logical area or body.
    /// </summary>
    [TestMethod]
    public void Put_IdenticalNoteRepeatedly_IsIdempotentForKnowledgeRepository() {
      using (KnowledgeRepositoryTestContext context =
        new KnowledgeRepositoryTestContext()) {

        FileBasedKnowledgeRepository repository =
          context.CreateRepository();

        JoplinKnowledgeRepositoryWebDavHandler handler =
          context.CreateJoplinHandler(repository);

        string serializedNote =
          context.CreateJoplinNoteItem(
            _NoteAId,
            "Idempotent Note",
            string.Empty,
            "One body instance"
          );

        IActionResult firstResult = context.PutText(
          handler,
          _NoteAId + ".md",
          serializedNote
        );

        IActionResult secondResult = context.PutText(
          handler,
          _NoteAId + ".md",
          serializedNote
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(firstResult)
        );

        Assert.AreEqual(
          StatusCodes.Status204NoContent,
          context.GetStatusCode(secondResult)
        );

        string[] rootAreas =
          repository.GetAreas(
            false,
            "/"
          );

        int matchingAreaCount = 0;

        foreach (string area in rootAreas) {
          if (string.Equals(
                repository.GetAreaName(area),
                "Idempotent Note",
                StringComparison.Ordinal
              )) {
            matchingAreaCount++;
          }
        }

        Assert.AreEqual(
          1,
          matchingAreaCount
        );

        string noteArea = context.GetChildArea(
          repository,
          "/",
          "Idempotent Note"
        );

        string content =
          repository.GetAggregatedContent(
            noteArea
          );

        int firstIndex = content.IndexOf(
          "One body instance",
          StringComparison.Ordinal
        );

        int lastIndex = content.LastIndexOf(
          "One body instance",
          StringComparison.Ordinal
        );

        Assert.IsTrue(
          firstIndex >= 0
        );

        Assert.AreEqual(
          firstIndex,
          lastIndex
        );
      }
    }

  }
}
