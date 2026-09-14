using Logging.SmartStandards;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace AI.SmartStandards.KnowledgeAccess {
  /// <summary>
  /// Exposes one <see cref="IKnowledgeRepository"/> as a Joplin-compatible WebDAV
  /// synchronization target.
  /// 
  /// The facade deliberately separates provider-neutral knowledge from Joplin-specific
  /// synchronization artifacts:
  /// 
  /// - Knowledge areas are projected to Joplin notebook and note sync items.
  /// - Joplin locks, temporary files, resources, sync metadata and unsupported item types
  ///   are persisted through <see cref="IJoplinSyncStateStore"/>.
  /// 
  /// Structural and aggregation areas are projected as Joplin notebooks. The first
  /// <see cref="ContentLevel.ContentContainer"/> below a non-container area is projected
  /// as one Joplin note whose body is the complete aggregated Markdown content of that
  /// container. Nested content containers remain headings inside that note rather than
  /// being duplicated as additional Joplin notes.
  /// 
  /// This projection allows a normal Joplin client to consume and edit repository
  /// knowledge without requiring a custom Joplin client or plugin.
  /// 
  /// This type is deliberately not an MVC controller and has no route or HTTP-method
  /// attributes. <see cref="JoplinKnowledgeRepositoryWebDavMiddleware"/> dispatches raw
  /// HTTP methods directly from <see cref="HttpRequest.Method"/>. MVC routing, ApiExplorer,
  /// Swagger and formatter negotiation therefore never interpret WebDAV methods such as
  /// PROPFIND, MKCOL or MOVE.
  /// </summary>
  public class JoplinKnowledgeRepositoryWebDavHandler : ControllerBase {

    private const string _ProjectionStateFileName = "projection.json";
    private const string _InfoFilePath = "/info.json";
    private const string _LocksCollection = "/locks";
    private const string _TempCollection = "/temp";
    private const string _ResourceCollection = "/.resource";
    private const string _LegacySyncCollection = "/.sync";
    private const string _LegacyLockCollection = "/.lock";
    private const string _MarkdownExtension = ".md";
    private const int _JoplinSyncVersion = 3;
    private const int _JoplinNoteType = 1;
    private const int _JoplinFolderType = 2;

    private readonly object _SyncRoot;
    private readonly IKnowledgeRepository _KnowledgeRepository;
    private readonly IJoplinSyncStateStore _SyncStateStore;

    /// <summary>
    /// Creates the Joplin WebDAV protocol handler.
    /// </summary>
    /// <param name="knowledgeRepository">
    /// The provider-neutral knowledge repository exposed as Joplin notebooks and notes.
    /// </param>
    /// <param name="syncStateStore">
    /// Persistent storage for Joplin-specific synchronization artifacts that do not
    /// belong in the knowledge repository itself.
    /// </param>
    public JoplinKnowledgeRepositoryWebDavHandler(
      IKnowledgeRepository knowledgeRepository,
      IJoplinSyncStateStore syncStateStore
    ) {
      if (knowledgeRepository == null) {
        throw new ArgumentNullException(nameof(knowledgeRepository));
      }

      if (syncStateStore == null) {
        throw new ArgumentNullException(nameof(syncStateStore));
      }

      _SyncRoot = new object();
      _KnowledgeRepository = knowledgeRepository;
      _SyncStateStore = syncStateStore;

      this.EnsureJoplinInfrastructure();
    }

    /// <summary>
    /// Returns WebDAV capability information for the synchronization root.
    /// </summary>
    public IActionResult OptionsRoot() {
      return this.OptionsInternal();
    }

    /// <summary>
    /// Returns WebDAV capability information for any nested synchronization path.
    /// </summary>
    public IActionResult OptionsPath(string path) {
      return this.OptionsInternal();
    }

    /// <summary>
    /// Handles WebDAV PROPFIND for the synchronization root.
    /// </summary>
    public IActionResult PropFindRoot() {
      return this.PropFindInternal("/");
    }

    /// <summary>
    /// Handles WebDAV PROPFIND for a nested synchronization path.
    /// </summary>
    public IActionResult PropFindPath(string path) {
      return this.PropFindInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles GET for the synchronization root or a concrete WebDAV file.
    /// </summary>
    public IActionResult GetRoot() {
      return this.GetInternal("/");
    }

    /// <summary>
    /// Handles GET for a concrete nested WebDAV file.
    /// </summary>
    public IActionResult GetPath(string path) {
      return this.GetInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles HEAD for the synchronization root.
    /// </summary>
    public IActionResult HeadRoot() {
      return this.HeadInternal("/");
    }

    /// <summary>
    /// Handles HEAD for one nested WebDAV path.
    /// </summary>
    public IActionResult HeadPath(string path) {
      return this.HeadInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles PUT for one nested WebDAV file.
    /// </summary>
    public IActionResult PutPath(string path) {
      return this.PutInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles DELETE for one nested WebDAV file or collection.
    /// </summary>
    public IActionResult DeletePath(string path) {
      return this.DeleteInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles WebDAV MKCOL for one nested collection.
    /// </summary>
    public IActionResult MkColPath(string path) {
      return this.MkColInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles WebDAV MOVE for one nested file or collection.
    /// </summary>
    public IActionResult MovePath(string path) {
      return this.MoveInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Returns the WebDAV methods implemented by this facade.
    /// </summary>
    private IActionResult OptionsInternal() {
      this.TraceWebDavRequest();

      this.Response.Headers["DAV"] = "1";
      this.Response.Headers["Allow"] =
        "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL, MOVE";

      return this.Ok();
    }

    /// <summary>
    /// Builds a standards-oriented WebDAV multi-status response for one resource and,
    /// when Depth is not zero, its direct children.
    /// </summary>
    private IActionResult PropFindInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        JoplinProjection projection = this.BuildProjection();

        JoplinWebDavEntry entry = this.ResolveWebDavEntry(
          path,
          projection
        );

        if (entry == null) {
          return this.NotFound();
        }

        List<JoplinWebDavEntry> entries = new List<JoplinWebDavEntry>();
        entries.Add(entry);

        string depth = this.Request.Headers["Depth"].ToString();

        if (!string.Equals(depth, "0", StringComparison.Ordinal)) {
          JoplinWebDavEntry[] children = this.GetWebDavChildren(
            path,
            projection
          );

          entries.AddRange(children);
        }

        XNamespace dav = "DAV:";
        XElement multiStatus = new XElement(dav + "multistatus");

        foreach (JoplinWebDavEntry currentEntry in entries) {
          XElement properties = new XElement(
            dav + "prop",
            new XElement(
              dav + "displayname",
              currentEntry.DisplayName
            ),
            new XElement(
              dav + "getlastmodified",
              currentEntry.LastModifiedUtc.ToString(
                "R",
                CultureInfo.InvariantCulture
              )
            ),
            new XElement(
              dav + "getcontentlength",
              this.GetWebDavContentLength(currentEntry)
            ),
            new XElement(
              dav + "getetag",
              "\"" + currentEntry.ETag + "\""
            )
          );

          if (currentEntry.IsCollection) {
            properties.Add(
              new XElement(
                dav + "resourcetype",
                new XElement(dav + "collection")
              )
            );
          }
          else {
            properties.Add(
              new XElement(dav + "resourcetype")
            );
          }

          XElement response = new XElement(
            dav + "response",
            new XElement(
              dav + "href",
              this.BuildWebDavHref(currentEntry.Path)
            ),
            new XElement(
              dav + "propstat",
              properties,
              new XElement(
                dav + "status",
                "HTTP/1.1 200 OK"
              )
            )
          );

          multiStatus.Add(response);
        }

        XDocument document = new XDocument(
          new XDeclaration("1.0", "utf-8", "yes"),
          multiStatus
        );

        this.Response.StatusCode = StatusCodes.Status207MultiStatus;

        return this.Content(
          document.ToString(SaveOptions.DisableFormatting),
          "application/xml; charset=utf-8",
          Encoding.UTF8
        );
      }
    }

    /// <summary>
    /// Returns a projected Joplin item or one opaque synchronization-state file.
    /// Collections are not returned as file content.
    /// </summary>
    private IActionResult GetInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        JoplinProjection projection = this.BuildProjection();
        JoplinWebDavEntry entry = this.ResolveWebDavEntry(path, projection);

        if (entry == null) {
          return this.NotFound();
        }

        if (entry.IsCollection) {
          return this.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        byte[] content = this.GetWebDavFileContent(
          entry,
          projection
        );

        this.ApplyFileHeaders(entry);

        return this.File(
          content,
          "application/octet-stream"
        );
      }
    }

    /// <summary>
    /// Returns only metadata headers for one WebDAV resource.
    /// </summary>
    private IActionResult HeadInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        JoplinProjection projection = this.BuildProjection();
        JoplinWebDavEntry entry = this.ResolveWebDavEntry(path, projection);

        if (entry == null) {
          return this.NotFound();
        }

        this.ApplyFileHeaders(entry);
        return this.Ok();
      }
    }

    /// <summary>
    /// Accepts one Joplin synchronization file.
    /// 
    /// Joplin note and folder item files are translated back into logical
    /// <see cref="IKnowledgeRepository"/> mutations. Unsupported Joplin item types and
    /// auxiliary synchronization files are persisted opaquely in the state store.
    /// </summary>
    private IActionResult PutInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        byte[] content;

        try {
          using MemoryStream buffer = new MemoryStream();
          this.Request.Body.CopyTo(buffer);
          content = buffer.ToArray();
        }
        catch (IOException ex) {
          DevLogger.LogError(ex);
          return this.BadRequest();
        }

        if (string.Equals(path, _InfoFilePath, StringComparison.Ordinal)) {
          _SyncStateStore.WriteFile(path, content);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        if (!this.IsRootItemFile(path)) {
          _SyncStateStore.WriteFile(path, content);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        string text = Encoding.UTF8.GetString(content);
        JoplinSerializedItem item = this.ParseJoplinItem(text);

        if (item == null) {
          _SyncStateStore.WriteFile(path, content);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        if (item.Type != _JoplinNoteType &&
            item.Type != _JoplinFolderType) {
          _SyncStateStore.WriteFile(path, content);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        // A real Joplin sync target must accept an item independently of whether its
        // semantic parent has already arrived. Persist the raw item first. It remains
        // authoritative for WebDAV reads until it can be materialized successfully into
        // the knowledge repository.
        _SyncStateStore.WriteFile(path, content);

        string itemId = Path.GetFileNameWithoutExtension(path);

        if (!string.Equals(
              item.Id,
              itemId,
              StringComparison.OrdinalIgnoreCase
            )) {
          return this.BadRequest(
            "The Joplin item ID does not match the WebDAV file name."
          );
        }

        JoplinProjection projection = this.BuildProjection();
        JoplinProjectionRecord existingRecord = projection.FindRecordById(itemId);

        MaterializationResult materializationResult;

        if (existingRecord == null) {
          materializationResult = this.CreateKnowledgeItemFromJoplin(
            item,
            projection
          );
        }
        else {
          materializationResult = this.UpdateKnowledgeItemFromJoplin(
            item,
            existingRecord,
            projection
          );
        }

        if (materializationResult == MaterializationResult.PendingDependency) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin item '"
            + item.Id
            + "' accepted as pending because a referenced parent item is not available yet."
          );

          this.SaveProjectionState(projection.State);
          return this.StatusCode(StatusCodes.Status204NoContent);
        }

        if (materializationResult == MaterializationResult.TemporarilyUnavailable) {
          _SyncStateStore.Delete(
            path
          );

          this.Response.Headers["Retry-After"] = "1";

          DevLogger.LogTrace(
            0,
            99999,
            "Joplin PUT could not be committed because the knowledge storage is temporarily unavailable: id="
            + item.Id
            + "."
          );

          return this.StatusCode(
            StatusCodes.Status503ServiceUnavailable
          );
        }

        if (materializationResult == MaterializationResult.Failed) {
          return this.Conflict(
            "The Joplin item was accepted by the sync target but could not be mapped to the knowledge repository."
          );
        }

        // The knowledge repository now represents the item. Remove the temporary raw
        // sync copy so subsequent reads use the dynamic knowledge projection.
        _SyncStateStore.Delete(path);

        this.SaveProjectionState(projection.State);

        // A newly materialized notebook may unlock notes that arrived before it.
        this.MaterializePendingKnowledgeItems();

        return this.StatusCode(StatusCodes.Status204NoContent);
      }
    }

    /// <summary>
    /// Deletes one projected Joplin note/notebook or one opaque WebDAV artifact.
    /// 
    /// Projected knowledge items map to <see cref="IKnowledgeRepository.TryDelete(string)"/>
    /// because Joplin DELETE removes the synchronization item itself rather than merely
    /// clearing its content.
    /// </summary>
    private IActionResult DeleteInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        JoplinProjection projection = this.BuildProjection();

        if (this.IsRootItemFile(path)) {
          string itemId = Path.GetFileNameWithoutExtension(path);
          JoplinProjectionRecord record = projection.FindRecordById(itemId);

          if (record != null) {
            // A WebDAV DELETE is a synchronization-protocol operation. It must not be
            // translated blindly into a destructive knowledge-repository delete because
            // Joplin may remove/reconcile remote sync items for reasons that do not mean
            // "physically destroy the source knowledge document".
            //
            // Suppress the item from the Joplin projection instead. The knowledge source
            // remains untouched and can therefore never be lost merely because of a
            // synchronization reconciliation cycle.
            record.IsSuppressed = true;
            record.ModifiedUtc = DateTime.UtcNow;

            this.SaveProjectionState(
              projection.State
            );

            DevLogger.LogTrace(
              0,
              99999,
              "Joplin DELETE suppressed projected item without deleting knowledge: id="
              + record.Id
              + " area='"
              + record.Area
              + "'."
            );

            return this.StatusCode(
              StatusCodes.Status204NoContent
            );
          }
        }

        bool stateDeleted = _SyncStateStore.Delete(path);

        if (!stateDeleted) {
          return this.NotFound();
        }

        return this.StatusCode(StatusCodes.Status204NoContent);
      }
    }

    /// <summary>
    /// Creates one opaque WebDAV collection used by the Joplin synchronizer.
    /// 
    /// Collections are synchronization infrastructure and are intentionally not mapped
    /// to knowledge areas.
    /// </summary>
    private IActionResult MkColInternal(string path) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        if (_SyncStateStore.CollectionExists(path)) {
          return this.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        bool created = _SyncStateStore.CreateCollection(path);

        if (!created) {
          return this.Conflict();
        }

        return this.StatusCode(StatusCodes.Status201Created);
      }
    }

    /// <summary>
    /// Moves one opaque WebDAV synchronization artifact.
    /// 
    /// Joplin commonly uses MOVE for temporary upload workflows. Projected knowledge
    /// item files themselves are not moved through WebDAV because their identity is
    /// represented by their Joplin item ID.
    /// </summary>
    private IActionResult MoveInternal(string sourcePath) {
      this.TraceWebDavRequest();

      lock (_SyncRoot) {
        if (this.IsRootItemFile(sourcePath)) {
          return this.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        string destinationHeader = this.Request.Headers["Destination"].ToString();

        if (string.IsNullOrWhiteSpace(destinationHeader)) {
          return this.BadRequest("The WebDAV Destination header is required.");
        }

        string destinationPath = this.ExtractDestinationPath(destinationHeader);

        bool overwrite = !string.Equals(
          this.Request.Headers["Overwrite"].ToString(),
          "F",
          StringComparison.OrdinalIgnoreCase
        );

        bool moved = _SyncStateStore.Move(
          sourcePath,
          destinationPath,
          overwrite
        );

        if (!moved) {
          return this.Conflict();
        }

        return this.StatusCode(StatusCodes.Status201Created);
      }
    }

    /// <summary>
    /// Finds an existing direct knowledge area that represents the supplied Joplin title
    /// and semantic item type without relying on provider-specific path syntax.
    /// </summary>
    private string FindExistingDirectArea(
      string parentArea,
      string title,
      int itemType
    ) {
      string[] children = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      foreach (string child in children) {
        string childTitle = _KnowledgeRepository.GetAreaName(
          child
        );

        if (!string.Equals(
              childTitle,
              title,
              StringComparison.Ordinal
            )) {
          continue;
        }

        ContentLevel contentLevel;
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;

        _KnowledgeRepository.GetAreaCapabilities(
          child,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate
        );

        if (itemType == _JoplinNoteType &&
            contentLevel == ContentLevel.ContentContainer) {
          return child;
        }

        if (itemType == _JoplinFolderType &&
            contentLevel != ContentLevel.ContentContainer) {
          return child;
        }
      }

      return string.Empty;
    }

    /// <summary>
    /// Finds a suppressed projection record bound to one existing logical area.
    /// </summary>
    private JoplinProjectionRecord FindSuppressedRecordByArea(
      JoplinProjectionState state,
      string area
    ) {
      foreach (JoplinProjectionRecord record in state.Records) {
        if (!record.IsSuppressed) {
          continue;
        }

        if (string.Equals(
              record.Area,
              area,
              StringComparison.Ordinal
            )) {
          return record;
        }
      }

      return null;
    }

    /// <summary>
    /// Rebinds a newly created Joplin sync identity to an existing knowledge area whose
    /// previous Joplin identity was suppressed during conflict reconciliation.
    /// </summary>
    private MaterializationResult RebindSuppressedArea(
      JoplinSerializedItem item,
      JoplinProjection projection,
      JoplinProjectionRecord suppressedRecord,
      string existingArea
    ) {
      if (item.Type == _JoplinNoteType) {
        bool replaced = _KnowledgeRepository.TryReplace(
          existingArea,
          item.Body
        );

        if (!replaced) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin conflict rebind temporarily blocked while replacing knowledge area '"
            + existingArea
            + "'."
          );

          return MaterializationResult.TemporarilyUnavailable;
        }
      }

      string oldId = suppressedRecord.Id;

      projection.State.Records.Remove(
        suppressedRecord
      );

      JoplinProjectionRecord record = new JoplinProjectionRecord();
      record.Id = item.Id;
      record.Area = existingArea;
      record.Type = item.Type;
      record.CreatedUtc = DateTime.MinValue;
      record.ModifiedUtc = DateTime.MinValue;
      record.ParentIdOverride = item.ParentId;
      record.LastContentHash = string.Empty;
      record.IsSuppressed = false;

      this.SynchronizeProjectionRecordAfterJoplinWrite(
        record,
        item
      );

      projection.State.Records.Add(
        record
      );

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin conflict rebind: oldId="
        + oldId
        + " newId="
        + item.Id
        + " area='"
        + existingArea
        + "'."
      );

      return MaterializationResult.Success;
    }

    /// <summary>
    /// Creates a new logical knowledge area from a Joplin note or notebook item.
    /// </summary>
    private MaterializationResult CreateKnowledgeItemFromJoplin(
      JoplinSerializedItem item,
      JoplinProjection projection
    ) {
      string parentArea = "/";

      if (!string.IsNullOrWhiteSpace(item.ParentId)) {
        JoplinProjectionRecord parentRecord = projection.FindRecordById(
          item.ParentId
        );

        if (parentRecord == null) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin create mapping deferred because parent_id '"
            + item.ParentId
            + "' is not known in the current projection."
          );

          return MaterializationResult.PendingDependency;
        }

        parentArea = parentRecord.Area;
      }

      string[] beforeAreas = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      string childName = item.Title;
      KnowledgeAreaKind childKind = KnowledgeAreaKind.Content;

      if (item.Type == _JoplinFolderType) {
        childKind = KnowledgeAreaKind.Structural;
      }

      string existingArea = this.FindExistingDirectArea(
        parentArea,
        item.Title,
        item.Type
      );

      if (!string.IsNullOrEmpty(existingArea)) {
        JoplinProjectionRecord suppressedRecord =
          this.FindSuppressedRecordByArea(
            projection.State,
            existingArea
          );

        if (suppressedRecord != null) {
          MaterializationResult rebindResult = this.RebindSuppressedArea(
            item,
            projection,
            suppressedRecord,
            existingArea
          );

          if (rebindResult != MaterializationResult.Failed) {
            return rebindResult;
          }
        }
      }

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin create mapping: id="
        + item.Id
        + " type="
        + item.Type.ToString(CultureInfo.InvariantCulture)
        + " title='"
        + item.Title
        + "' parentArea='"
        + parentArea
        + "' childName='"
        + childName
        + "'"
      );

      bool added = _KnowledgeRepository.TryAddSubArea(
        parentArea,
        childName,
        childKind
      );

      if (!added) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin create mapping failed at TryAddSubArea: parentArea='"
          + parentArea
          + "' childName='"
          + childName
          + "'"
        );

        return MaterializationResult.Failed;
      }

      string[] afterAreas = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      string newArea = this.FindAddedArea(
        beforeAreas,
        afterAreas
      );

      if (string.IsNullOrEmpty(newArea)) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin create mapping failed because the newly added area could not be identified uniquely below '"
          + parentArea
          + "'."
        );

        return MaterializationResult.Failed;
      }

      if (item.Type == _JoplinNoteType &&
          !string.IsNullOrEmpty(item.Body)) {
        bool appended = _KnowledgeRepository.TryAppendContent(
          newArea,
          item.Body
        );

        if (!appended) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin create mapping failed while appending note content to '"
            + newArea
            + "'. The newly created area will be rolled back."
          );

          _KnowledgeRepository.TryDelete(newArea);
          return MaterializationResult.Failed;
        }
      }

      JoplinProjectionRecord record = new JoplinProjectionRecord();
      record.Id = item.Id;
      record.Area = newArea;
      record.Type = item.Type;
      record.CreatedUtc = DateTime.MinValue;
      record.ModifiedUtc = DateTime.MinValue;
      record.ParentIdOverride = item.ParentId;
      record.LastContentHash = string.Empty;
      record.IsSuppressed = false;

      this.SynchronizeProjectionRecordAfterJoplinWrite(
        record,
        item
      );

      projection.State.Records.Add(record);
      return MaterializationResult.Success;
    }

    /// <summary>
    /// Applies a Joplin parent change through the provider-neutral repository move
    /// contract.
    ///
    /// This adapter deliberately has no knowledge of files, folders, Markdown rewrite
    /// mechanics or any other concrete repository representation. It resolves only the
    /// logical new parent and delegates the complete structural move to
    /// <see cref="IKnowledgeRepository.TryMoveContent(string, string)"/>.
    /// </summary>
    private MaterializationResult MoveExistingItemToUpdatedParent(
      JoplinSerializedItem item,
      JoplinProjectionRecord record,
      JoplinProjection projection
    ) {
      string newParentArea = "/";

      if (!string.IsNullOrWhiteSpace(item.ParentId)) {
        JoplinProjectionRecord newParentRecord =
          projection.FindRecordById(
            item.ParentId
          );

        if (newParentRecord == null ||
            newParentRecord.IsSuppressed) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin move deferred because parent_id '"
            + item.ParentId
            + "' is not available in the current logical projection."
          );

          return MaterializationResult.PendingDependency;
        }

        newParentArea = newParentRecord.Area;
      }

      string currentParentArea = this.GetParentArea(
        record.Area
      );

      if (string.Equals(
            currentParentArea,
            newParentArea,
            StringComparison.Ordinal
          )) {
        record.ParentIdOverride = item.ParentId;
        return MaterializationResult.Success;
      }

      string oldArea = record.Area;
      string logicalName = _KnowledgeRepository.GetAreaName(
        oldArea
      );

      bool moved = _KnowledgeRepository.TryMoveContent(
        oldArea,
        newParentArea
      );

      if (!moved) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin logical move temporarily unavailable: id="
          + item.Id
          + " contentAreaToMove='"
          + oldArea
          + "' newParentArea='"
          + newParentArea
          + "'."
        );

        return MaterializationResult.TemporarilyUnavailable;
      }

      string movedArea = this.FindExistingDirectArea(
        newParentArea,
        logicalName,
        item.Type
      );

      if (string.IsNullOrEmpty(movedArea)) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin logical move completed but the moved area could not be resolved below its new parent: id="
          + item.Id
          + " oldArea='"
          + oldArea
          + "' newParentArea='"
          + newParentArea
          + "'."
        );

        return MaterializationResult.Failed;
      }

      record.Area = movedArea;
      record.ParentIdOverride = item.ParentId;

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin logical move completed: id="
        + item.Id
        + " oldArea='"
        + oldArea
        + "' newArea='"
        + movedArea
        + "'."
      );

      return MaterializationResult.Success;
    }

    /// <summary>
    /// Applies a Joplin note or notebook update to an existing logical knowledge area.
    /// Parent changes are delegated exclusively through the provider-neutral
    /// <see cref="IKnowledgeRepository.TryMoveContent(string, string)"/> operation.
    /// </summary>
    private MaterializationResult UpdateKnowledgeItemFromJoplin(
      JoplinSerializedItem item,
      JoplinProjectionRecord record,
      JoplinProjection projection
    ) {
      MaterializationResult parentMoveResult =
        this.MoveExistingItemToUpdatedParent(
          item,
          record,
          projection
        );

      if (parentMoveResult != MaterializationResult.Success) {
        return parentMoveResult;
      }

      string currentTitle = _KnowledgeRepository.GetAreaName(record.Area);

      if (!string.Equals(
            currentTitle,
            item.Title,
            StringComparison.Ordinal
          )) {
        string oldArea = record.Area;
        string parentArea = this.GetParentArea(oldArea);

        bool renamed = _KnowledgeRepository.TryRename(
          oldArea,
          item.Title
        );

        if (!renamed) {
          return MaterializationResult.Failed;
        }

        string renamedArea = this.FindRenamedArea(
          parentArea,
          oldArea,
          item.Title
        );

        if (string.IsNullOrEmpty(renamedArea)) {
          return MaterializationResult.Failed;
        }

        record.Area = renamedArea;
      }

      if (item.Type == _JoplinNoteType) {
        DevLogger.LogTrace(
          0,
          99999,
          "Joplin note update: id="
          + item.Id
          + " area='"
          + record.Area
          + "' bodyLength="
          + item.Body.Length.ToString(CultureInfo.InvariantCulture)
        );

        bool replaced = _KnowledgeRepository.TryReplace(
          record.Area,
          item.Body
        );

        if (!replaced) {
          DevLogger.LogTrace(
            0,
            99999,
            "Joplin note update failed at TryReplace: id="
            + item.Id
            + " area='"
            + record.Area
            + "'"
          );

          return MaterializationResult.TemporarilyUnavailable;
        }

        string repositoryContent = _KnowledgeRepository.GetAggregatedContent(
          record.Area
        );

        string uploadedHash = this.ComputeHash(
          this.NormalizeContentForComparison(item.Body)
        );

        string repositoryHash = this.ComputeHash(
          this.NormalizeContentForComparison(repositoryContent)
        );

        DevLogger.LogTrace(
          0,
          99999,
          "Joplin note update persisted: id="
          + item.Id
          + " area='"
          + record.Area
          + "' uploadedHash="
          + uploadedHash
          + " repositoryHash="
          + repositoryHash
          + " equal="
          + string.Equals(
              uploadedHash,
              repositoryHash,
              StringComparison.Ordinal
            ).ToString()
        );
      }

      this.SynchronizeProjectionRecordAfterJoplinWrite(
        record,
        item
      );

      return MaterializationResult.Success;
    }

    /// <summary>
    /// Attempts to materialize accepted Joplin note and notebook items that previously
    /// could not be projected because their parent item had not yet arrived.
    /// 
    /// The method repeatedly scans root sync-item files until no additional item can be
    /// materialized. This handles arbitrary parent-before-child upload ordering without
    /// requiring Joplin to retry the original PUT.
    /// </summary>
    private void MaterializePendingKnowledgeItems() {
      bool progress = true;

      while (progress) {
        progress = false;

        JoplinSyncStateEntry[] rootEntries = _SyncStateStore.GetChildren("/");
        JoplinProjection projection = this.BuildProjection();

        foreach (JoplinSyncStateEntry entry in rootEntries) {
          if (entry.IsCollection) {
            continue;
          }

          if (!this.IsRootItemFile(entry.Path)) {
            continue;
          }

          byte[] content = _SyncStateStore.ReadFile(entry.Path);
          JoplinSerializedItem item = this.ParseJoplinItem(
            Encoding.UTF8.GetString(content)
          );

          if (item == null) {
            continue;
          }

          if (item.Type != _JoplinNoteType &&
              item.Type != _JoplinFolderType) {
            continue;
          }

          JoplinProjectionRecord existingRecord =
            projection.FindRecordById(item.Id);

          MaterializationResult result;

          if (existingRecord == null) {
            result = this.CreateKnowledgeItemFromJoplin(
              item,
              projection
            );
          }
          else {
            result = this.UpdateKnowledgeItemFromJoplin(
              item,
              existingRecord,
              projection
            );
          }

          if (result == MaterializationResult.PendingDependency) {
            continue;
          }

          if (result == MaterializationResult.Failed) {
            DevLogger.LogTrace(
              0,
              99999,
              "Pending Joplin item '"
              + item.Id
              + "' still cannot be materialized."
            );

            continue;
          }

          _SyncStateStore.Delete(entry.Path);
          this.SaveProjectionState(projection.State);

          DevLogger.LogTrace(
            0,
            99999,
            "Pending Joplin item '"
            + item.Id
            + "' was materialized successfully."
          );

          progress = true;
          break;
        }
      }
    }

    /// <summary>
    /// Builds the current deterministic projection from logical knowledge areas to flat
    /// Joplin WebDAV sync-item files.
    /// </summary>
    private JoplinProjection BuildProjection() {
      JoplinProjectionState state = this.LoadProjectionState();

      string[] areas = _KnowledgeRepository.GetAreas(
        true,
        "/"
      );

      HashSet<string> currentlyProjectedAreas =
        new HashSet<string>(StringComparer.Ordinal);

      List<JoplinProjectedItem> items = new List<JoplinProjectedItem>();

      foreach (string area in areas) {
        ContentLevel contentLevel;
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;

        _KnowledgeRepository.GetAreaCapabilities(
          area,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate
        );

        bool projectAsFolder =
          contentLevel == ContentLevel.BeyondContent ||
          contentLevel == ContentLevel.ContentAggregation;

        bool projectAsNote = false;

        if (contentLevel == ContentLevel.ContentContainer) {
          string parentArea = this.GetParentArea(area);
          ContentLevel parentContentLevel = ContentLevel.BeyondContent;

          if (parentArea != "/") {
            bool parentResolved = this.TryGetContentLevel(
              parentArea,
              out parentContentLevel
            );

            if (!parentResolved) {
              parentContentLevel = ContentLevel.BeyondContent;
            }
          }

          if (parentContentLevel != ContentLevel.ContentContainer) {
            projectAsNote = true;
          }
        }

        if (!projectAsFolder && !projectAsNote) {
          continue;
        }

        int type = _JoplinFolderType;

        if (projectAsNote) {
          type = _JoplinNoteType;
        }

        JoplinProjectionRecord record = state.Records
          .FirstOrDefault((JoplinProjectionRecord candidate) =>
            string.Equals(
              candidate.Area,
              area,
              StringComparison.Ordinal
            ) &&
            candidate.Type == type
          );

        if (record == null) {
          record = new JoplinProjectionRecord();
          record.Id = this.CreateDeterministicItemId(
            type.ToString(CultureInfo.InvariantCulture) + ":" + area
          );
          record.Area = area;
          record.Type = type;
          record.CreatedUtc = DateTime.UtcNow;
          record.ModifiedUtc = record.CreatedUtc;
          record.ParentIdOverride = string.Empty;
          record.LastContentHash = string.Empty;
          record.IsSuppressed = false;

          state.Records.Add(record);
        }

        currentlyProjectedAreas.Add(
          type.ToString(CultureInfo.InvariantCulture) + ":" + area
        );

        if (record.IsSuppressed) {
          continue;
        }

        JoplinProjectedItem projectedItem = new JoplinProjectedItem();
        projectedItem.Record = record;
        projectedItem.Title = _KnowledgeRepository.GetAreaName(area);
        projectedItem.ParentId = this.ResolveProjectedParentId(
          area,
          record,
          state,
          areas
        );

        if (type == _JoplinNoteType) {
          projectedItem.Body = _KnowledgeRepository.GetAggregatedContent(
            area
          );
        }
        else {
          projectedItem.Body = string.Empty;
        }

        string semanticHash = this.ComputeProjectedSemanticHash(
          projectedItem
        );

        if (!string.Equals(
              record.LastContentHash,
              semanticHash,
              StringComparison.Ordinal
            )) {
          record.LastContentHash = semanticHash;
          record.ModifiedUtc = DateTime.UtcNow;
        }

        // Serialize only after the semantic modification timestamp has reached its final
        // value for this projection pass. Otherwise updated_time becomes part of the
        // previous hash decision and causes every subsequent projection to modify itself.
        string serialized = this.SerializeJoplinItem(
          projectedItem
        );

        string transportHash = this.ComputeHash(serialized);

        projectedItem.SerializedContent = serialized;
        projectedItem.ContentHash = transportHash;
        items.Add(projectedItem);
      }

      JoplinProjectionRecord[] staleRecords = state.Records
        .Where((JoplinProjectionRecord record) =>
          (record.Type == _JoplinNoteType ||
           record.Type == _JoplinFolderType) &&
          !currentlyProjectedAreas.Contains(
            record.Type.ToString(CultureInfo.InvariantCulture)
            + ":"
            + record.Area
          ))
        .ToArray();

      foreach (JoplinProjectionRecord staleRecord in staleRecords) {
        state.Records.Remove(staleRecord);
      }

      this.SaveProjectionState(state);

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin projection state: records="
        + state.Records.Count.ToString(CultureInfo.InvariantCulture)
        + " items="
        + items.Count.ToString(CultureInfo.InvariantCulture)
      );

      return new JoplinProjection(
        state,
        items.ToArray()
      );
    }

    /// <summary>
    /// Resolves the Joplin parent notebook ID for one projected area.
    /// </summary>
    private string ResolveProjectedParentId(
      string area,
      JoplinProjectionRecord record,
      JoplinProjectionState state,
      string[] allAreas
    ) {
      if (!string.IsNullOrWhiteSpace(record.ParentIdOverride)) {
        JoplinProjectionRecord overriddenParent = state.Records
          .FirstOrDefault((JoplinProjectionRecord candidate) =>
            string.Equals(
              candidate.Id,
              record.ParentIdOverride,
              StringComparison.OrdinalIgnoreCase
            ) &&
            candidate.Type == _JoplinFolderType
          );

        if (overriddenParent != null) {
          return overriddenParent.Id;
        }
      }

      string parentArea = this.GetParentArea(area);

      while (parentArea != "/") {
        JoplinProjectionRecord parentRecord = state.Records
          .FirstOrDefault((JoplinProjectionRecord candidate) =>
            string.Equals(
              candidate.Area,
              parentArea,
              StringComparison.Ordinal
            ) &&
            candidate.Type == _JoplinFolderType
          );

        if (parentRecord != null) {
          return parentRecord.Id;
        }

        parentArea = this.GetParentArea(parentArea);
      }

      return string.Empty;
    }

    /// <summary>
    /// Normalizes textual content for diagnostic write-through comparison.
    /// </summary>
    private string NormalizeContentForComparison(
      string content
    ) {
      if (content == null) {
        return string.Empty;
      }

      return content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .TrimEnd('\n');
    }

    /// <summary>
    /// Synchronizes the persistent projection record with a successfully materialized
    /// Joplin item so the immediately following projection pass is byte-stable and does
    /// not manufacture another remote modification.
    /// </summary>
    private void SynchronizeProjectionRecordAfterJoplinWrite(
      JoplinProjectionRecord record,
      JoplinSerializedItem item
    ) {
      if (item.CreatedUtc != DateTime.MinValue) {
        record.CreatedUtc = item.CreatedUtc;
      }
      else if (record.CreatedUtc == DateTime.MinValue) {
        record.CreatedUtc = DateTime.UtcNow;
      }

      if (item.ModifiedUtc != DateTime.MinValue) {
        record.ModifiedUtc = item.ModifiedUtc;
      }
      else {
        record.ModifiedUtc = DateTime.UtcNow;
      }

      record.ParentIdOverride = item.ParentId;

      JoplinProjectedItem projectedItem = new JoplinProjectedItem();
      projectedItem.Record = record;
      projectedItem.Title = _KnowledgeRepository.GetAreaName(
        record.Area
      );
      projectedItem.ParentId = item.ParentId;

      if (record.Type == _JoplinNoteType) {
        projectedItem.Body = _KnowledgeRepository.GetAggregatedContent(
          record.Area
        );
      }
      else {
        projectedItem.Body = string.Empty;
      }

      record.LastContentHash = this.ComputeProjectedSemanticHash(
        projectedItem
      );
    }

    /// <summary>
    /// Computes a stable semantic fingerprint for one projected Joplin item.
    ///
    /// Transport metadata such as <c>updated_time</c> is deliberately excluded. Including
    /// the modification timestamp in the change detector would make the projection
    /// self-modifying: changing the timestamp would change the hash, which would change
    /// the timestamp again during the next projection pass.
    /// </summary>
    private string ComputeProjectedSemanticHash(
      JoplinProjectedItem item
    ) {
      StringBuilder builder = new StringBuilder();

      builder.Append(
        item.Record.Type.ToString(
          CultureInfo.InvariantCulture
        )
      );
      builder.Append('\n');
      builder.Append(item.Record.Area);
      builder.Append('\n');
      builder.Append(item.Title);
      builder.Append('\n');
      builder.Append(item.ParentId);
      builder.Append('\n');
      builder.Append(item.Body);

      return this.ComputeHash(
        builder.ToString()
      );
    }

    private string SerializeJoplinItem(JoplinProjectedItem item) {
      List<string> blocks = new List<string>();

      blocks.Add(
        item.Title.TrimEnd('\r', '\n')
      );

      if (item.Record.Type == _JoplinNoteType &&
          !string.IsNullOrEmpty(item.Body)) {
        blocks.Add(
          item.Body.TrimEnd('\r', '\n')
        );
      }

      StringBuilder properties = new StringBuilder();

      properties.Append("id: ");
      properties.Append(item.Record.Id);
      properties.Append('\n');
      properties.Append("parent_id: ");
      properties.Append(item.ParentId);
      properties.Append('\n');
      properties.Append("created_time: ");
      properties.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
      properties.Append('\n');
      properties.Append("updated_time: ");
      properties.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
      properties.Append('\n');

      if (item.Record.Type == _JoplinNoteType) {
        properties.Append("is_conflict: 0\n");
        properties.Append("latitude: 0.00000000\n");
        properties.Append("longitude: 0.00000000\n");
        properties.Append("altitude: 0.0000\n");
        properties.Append("author: \n");
        properties.Append("source_url: \n");
        properties.Append("is_todo: 0\n");
        properties.Append("todo_due: 0\n");
        properties.Append("todo_completed: 0\n");
        properties.Append("source: knowledge-repository\n");
        properties.Append("source_application: knowledge-repository\n");
        properties.Append("application_data: \n");
        properties.Append("order: 0\n");
        properties.Append("user_created_time: ");
        properties.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
        properties.Append('\n');
        properties.Append("user_updated_time: ");
        properties.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
        properties.Append('\n');
        properties.Append("encryption_cipher_text: \n");
        properties.Append("encryption_applied: 0\n");
        properties.Append("markup_language: 1\n");
        properties.Append("is_shared: 0\n");
        properties.Append("share_id: \n");
        properties.Append("conflict_original_id: \n");
        properties.Append("master_key_id: \n");
        properties.Append("user_data: \n");
        properties.Append("deleted_time: 0\n");
      }
      else {
        properties.Append("user_created_time: ");
        properties.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
        properties.Append('\n');
        properties.Append("user_updated_time: ");
        properties.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
        properties.Append('\n');
        properties.Append("encryption_cipher_text: \n");
        properties.Append("encryption_applied: 0\n");
        properties.Append("is_shared: 0\n");
        properties.Append("share_id: \n");
        properties.Append("master_key_id: \n");
        properties.Append("user_data: \n");
        properties.Append("deleted_time: 0\n");
      }

      properties.Append("type_: ");
      properties.Append(
        item.Record.Type.ToString(
          CultureInfo.InvariantCulture
        )
      );

      blocks.Add(
        properties.ToString()
      );

      // Joplin serializes title, optional note body and the property block by joining
      // them with exactly one empty line. There must be no trailing line break after
      // the final property. BaseItem.unserialize() scans from the end and would treat
      // such a trailing empty line as the body/property separator before reading any
      // metadata, causing "Missing required property: type_".
      return string.Join(
        "\n\n",
        blocks.ToArray()
      );
    }

    /// <summary>
    /// Parses one UTC timestamp from Joplin sync-item metadata.
    /// </summary>
    private DateTime ParseJoplinTime(
      Dictionary<string, string> metadata,
      string key
    ) {
      if (!metadata.TryGetValue(
            key,
            out string value
          )) {
        return DateTime.MinValue;
      }

      DateTime parsed;

      if (!DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal,
            out parsed
          )) {
        return DateTime.MinValue;
      }

      return parsed;
    }

    /// <summary>
    /// Parses the subset of Joplin sync-item metadata required to map notes and notebooks
    /// back to the provider-neutral knowledge repository.
    /// </summary>
    private JoplinSerializedItem ParseJoplinItem(string content) {
      string normalized = content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

      string[] lines = normalized.Split('\n');

      Dictionary<string, string> metadata =
        new Dictionary<string, string>(StringComparer.Ordinal);

      int metadataStartIndex = lines.Length;

      for (int index = lines.Length - 1; index >= 0; index--) {
        string line = lines[index];

        if (line.Length == 0) {
          if (metadata.Count > 0) {
            metadataStartIndex = index + 1;
            break;
          }

          continue;
        }

        int separator = line.IndexOf(": ", StringComparison.Ordinal);

        if (separator <= 0) {
          if (metadata.Count > 0) {
            metadataStartIndex = index + 1;
            break;
          }

          return null;
        }

        string key = line.Substring(0, separator);
        string value = line.Substring(separator + 2);

        metadata[key] = value;
        metadataStartIndex = index;
      }

      if (!metadata.ContainsKey("id") ||
          !metadata.ContainsKey("type_")) {
        return null;
      }

      int type;

      if (!int.TryParse(
            metadata["type_"],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out type
          )) {
        return null;
      }

      JoplinSerializedItem item = new JoplinSerializedItem();
      item.Id = metadata["id"];
      item.Type = type;

      item.CreatedUtc = this.ParseJoplinTime(
        metadata,
        "created_time"
      );

      item.ModifiedUtc = this.ParseJoplinTime(
        metadata,
        "updated_time"
      );

      if (metadata.TryGetValue("parent_id", out string parentId)) {
        item.ParentId = parentId;
      }
      else {
        item.ParentId = string.Empty;
      }

      int contentEndIndex = metadataStartIndex - 1;

      while (contentEndIndex >= 0 &&
             lines[contentEndIndex].Length == 0) {
        contentEndIndex--;
      }

      if (contentEndIndex < 0) {
        return null;
      }

      item.Title = lines[0].Trim();

      if (string.IsNullOrWhiteSpace(item.Title)) {
        return null;
      }

      if (type == _JoplinNoteType) {
        int bodyStartIndex = 1;

        while (bodyStartIndex <= contentEndIndex &&
               lines[bodyStartIndex].Length == 0) {
          bodyStartIndex++;
        }

        if (bodyStartIndex <= contentEndIndex) {
          item.Body = string.Join(
            "\n",
            lines,
            bodyStartIndex,
            contentEndIndex - bodyStartIndex + 1
          );
        }
        else {
          item.Body = string.Empty;
        }
      }
      else {
        item.Body = string.Empty;
      }

      return item;
    }

    /// <summary>
    /// Resolves one WebDAV resource from the dynamic knowledge projection or opaque
    /// synchronization-state store.
    /// </summary>
    private JoplinWebDavEntry ResolveWebDavEntry(
      string path,
      JoplinProjection projection
    ) {
      if (path == "/") {
        JoplinWebDavEntry root = new JoplinWebDavEntry();
        root.Path = "/";
        root.DisplayName = "Joplin";
        root.IsCollection = true;
        root.Length = 0;
        root.LastModifiedUtc = DateTime.UnixEpoch;
        root.ETag = "root";
        root.SourceKind = JoplinWebDavSourceKind.Root;
        return root;
      }

      // A raw root item in the state store represents an accepted Joplin item that has
      // not yet been materialized successfully. It must take precedence over the dynamic
      // projection so Joplin can immediately read back exactly what it uploaded.
      JoplinSyncStateEntry stateEntry = _SyncStateStore.GetEntry(path);

      if (stateEntry != null) {
        JoplinWebDavEntry stateBackedEntry = new JoplinWebDavEntry();
        stateBackedEntry.Path = stateEntry.Path;
        stateBackedEntry.DisplayName = this.GetWebDavDisplayName(stateEntry.Path);
        stateBackedEntry.IsCollection = stateEntry.IsCollection;
        stateBackedEntry.Length = stateEntry.Length;
        stateBackedEntry.LastModifiedUtc = stateEntry.LastModifiedUtc;
        stateBackedEntry.SourceKind = JoplinWebDavSourceKind.StateStore;

        if (stateEntry.IsCollection) {
          stateBackedEntry.ETag = this.ComputeHash(
            stateEntry.Path
            + ":"
            + stateEntry.LastModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture)
          );
        }
        else {
          byte[] stateBytes = _SyncStateStore.ReadFile(path);
          stateBackedEntry.ETag = this.ComputeHash(stateBytes);
        }

        return stateBackedEntry;
      }

      if (this.IsRootItemFile(path)) {
        string itemId = Path.GetFileNameWithoutExtension(path);
        JoplinProjectedItem item = projection.FindItemById(itemId);

        if (item != null) {
          byte[] bytes = Encoding.UTF8.GetBytes(item.SerializedContent);

          JoplinWebDavEntry projectedEntry = new JoplinWebDavEntry();
          projectedEntry.Path = path;
          projectedEntry.DisplayName = Path.GetFileName(path);
          projectedEntry.IsCollection = false;
          projectedEntry.Length = bytes.LongLength;
          projectedEntry.LastModifiedUtc = item.Record.ModifiedUtc;
          projectedEntry.ETag = item.ContentHash;
          projectedEntry.SourceKind = JoplinWebDavSourceKind.ProjectedKnowledgeItem;
          projectedEntry.ProjectedItem = item;
          return projectedEntry;
        }
      }


      return null;
    }

    /// <summary>
    /// Returns direct WebDAV children for a collection.
    /// </summary>
    private JoplinWebDavEntry[] GetWebDavChildren(
      string path,
      JoplinProjection projection
    ) {
      List<JoplinWebDavEntry> children = new List<JoplinWebDavEntry>();

      if (path == "/") {
        JoplinSyncStateEntry[] stateChildren = _SyncStateStore.GetChildren("/");

        foreach (JoplinSyncStateEntry stateChild in stateChildren) {
          JoplinWebDavEntry entry = this.ResolveWebDavEntry(
            stateChild.Path,
            projection
          );

          if (entry != null) {
            children.Add(entry);
          }
        }

        foreach (JoplinProjectedItem item in projection.Items) {
          string itemPath = "/" + item.Record.Id + _MarkdownExtension;

          if (children.Any((JoplinWebDavEntry existing) =>
                string.Equals(
                  existing.Path,
                  itemPath,
                  StringComparison.OrdinalIgnoreCase
                ))) {
            continue;
          }

          JoplinWebDavEntry entry = this.ResolveWebDavEntry(
            itemPath,
            projection
          );

          if (entry != null) {
            children.Add(entry);
          }
        }

        return children.ToArray();
      }

      JoplinSyncStateEntry[] nestedChildren = _SyncStateStore.GetChildren(
        path
      );

      foreach (JoplinSyncStateEntry nestedChild in nestedChildren) {
        JoplinWebDavEntry entry = this.ResolveWebDavEntry(
          nestedChild.Path,
          projection
        );

        if (entry != null) {
          children.Add(entry);
        }
      }

      return children.ToArray();
    }

    /// <summary>
    /// Returns bytes for a projected or opaque WebDAV file.
    /// </summary>
    private byte[] GetWebDavFileContent(
      JoplinWebDavEntry entry,
      JoplinProjection projection
    ) {
      if (entry.SourceKind == JoplinWebDavSourceKind.ProjectedKnowledgeItem) {
        return Encoding.UTF8.GetBytes(
          entry.ProjectedItem.SerializedContent
        );
      }

      return _SyncStateStore.ReadFile(
        entry.Path
      );
    }

    /// <summary>
    /// Returns the WebDAV content length value used in PROPFIND responses.
    /// </summary>
    private string GetWebDavContentLength(JoplinWebDavEntry entry) {
      if (entry.IsCollection) {
        return "0";
      }

      return entry.Length.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Applies HTTP metadata expected by WebDAV clients.
    /// </summary>
    private void ApplyFileHeaders(JoplinWebDavEntry entry) {
      this.Response.Headers["ETag"] = "\"" + entry.ETag + "\"";
      this.Response.Headers["Last-Modified"] = entry.LastModifiedUtc.ToString(
        "R",
        CultureInfo.InvariantCulture
      );

      if (!entry.IsCollection) {
        this.Response.ContentLength = entry.Length;
      }
    }

    /// <summary>
    /// Ensures the persistent auxiliary directories and sync-target metadata expected by
    /// Joplin are present.
    /// </summary>
    private void EnsureJoplinInfrastructure() {
      lock (_SyncRoot) {
        this.EnsureCollection(_LocksCollection);
        this.EnsureCollection(_TempCollection);
        this.EnsureCollection(_ResourceCollection);
        this.EnsureCollection(_LegacySyncCollection);
        this.EnsureCollection(_LegacyLockCollection);

        if (!_SyncStateStore.FileExists(_InfoFilePath)) {
          JoplinSyncTargetInfo info = new JoplinSyncTargetInfo();
          info.Version = _JoplinSyncVersion;

          string json = JsonConvert.SerializeObject(
            info,
            Formatting.Indented
          );

          _SyncStateStore.WriteFile(
            _InfoFilePath,
            Encoding.UTF8.GetBytes(json)
          );
        }
      }
    }

    /// <summary>
    /// Creates one auxiliary collection when it does not already exist.
    /// </summary>
    private void EnsureCollection(string path) {
      if (!_SyncStateStore.CollectionExists(path)) {
        _SyncStateStore.CreateCollection(path);
      }
    }

    /// <summary>
    /// Loads persisted stable item mappings and modification metadata.
    /// </summary>
    private JoplinProjectionState LoadProjectionState() {
      string json = _SyncStateStore.ReadInternalText(
        _ProjectionStateFileName
      );

      if (string.IsNullOrWhiteSpace(json)) {
        return new JoplinProjectionState();
      }

      try {
        JoplinProjectionState state =
          JsonConvert.DeserializeObject<JoplinProjectionState>(json);

        if (state == null) {
          return new JoplinProjectionState();
        }

        if (state.Records == null) {
          state.Records = new List<JoplinProjectionRecord>();
        }

        return state;
      }
      catch (JsonException ex) {
        DevLogger.LogError(ex);
        return new JoplinProjectionState();
      }
    }

    /// <summary>
    /// Persists stable Joplin item mappings and projection timestamps.
    /// </summary>
    private void SaveProjectionState(JoplinProjectionState state) {
      string json = JsonConvert.SerializeObject(
        state,
        Formatting.Indented
      );

      _SyncStateStore.WriteInternalText(
        _ProjectionStateFileName,
        json
      );
    }

    /// <summary>
    /// Attempts to resolve the content level of one logical area.
    /// </summary>
    private bool TryGetContentLevel(
      string area,
      out ContentLevel contentLevel
    ) {
      contentLevel = ContentLevel.BeyondContent;

      try {
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;

        _KnowledgeRepository.GetAreaCapabilities(
          area,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate
        );

        return true;
      }
      catch (InvalidOperationException) {
        return false;
      }
    }

    /// <summary>
    /// Finds the direct child added by comparing repository state before and after
    /// TryAddSubArea.
    /// </summary>
    private string FindAddedArea(
      string[] beforeAreas,
      string[] afterAreas
    ) {
      HashSet<string> before = new HashSet<string>(
        beforeAreas,
        StringComparer.Ordinal
      );

      string[] added = afterAreas
        .Where((string area) => !before.Contains(area))
        .ToArray();

      if (added.Length != 1) {
        return string.Empty;
      }

      return added[0];
    }

    /// <summary>
    /// Finds the renamed direct child after a successful provider-level rename.
    /// </summary>
    private string FindRenamedArea(
      string parentArea,
      string oldArea,
      string newTitle
    ) {
      string[] children = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      string normalizedTitle = this.NormalizeDisplayName(newTitle);

      foreach (string child in children) {
        if (string.Equals(
              child,
              oldArea,
              StringComparison.Ordinal
            )) {
          continue;
        }

        string childTitle = _KnowledgeRepository.GetAreaName(child);

        if (string.Equals(
              this.NormalizeDisplayName(childTitle),
              normalizedTitle,
              StringComparison.OrdinalIgnoreCase
            )) {
          return child;
        }
      }

      return string.Empty;
    }


    /// <summary>
    /// Normalizes a display title for post-rename matching.
    /// </summary>
    private string NormalizeDisplayName(string value) {
      return value.Trim();
    }

    /// <summary>
    /// Gets the logical parent area.
    /// </summary>
    private string GetParentArea(string area) {
      if (string.IsNullOrWhiteSpace(area) || area == "/") {
        return "/";
      }

      string normalized = area.TrimEnd('/');
      int separator = normalized.LastIndexOf('/');

      if (separator <= 0) {
        return "/";
      }

      return normalized.Substring(0, separator);
    }

    /// <summary>
    /// Creates a deterministic Joplin-compatible 32-character hexadecimal item ID.
    /// Persisted projection records may later retain client-generated Joplin IDs.
    /// </summary>
    private string CreateDeterministicItemId(string value) {
      byte[] input = Encoding.UTF8.GetBytes(value);
      byte[] hash = SHA256.HashData(input);
      StringBuilder builder = new StringBuilder();

      for (int index = 0; index < 16; index++) {
        builder.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
      }

      return builder.ToString();
    }

    /// <summary>
    /// Computes a hexadecimal SHA-256 content hash.
    /// </summary>
    private string ComputeHash(string value) {
      return this.ComputeHash(
        Encoding.UTF8.GetBytes(value)
      );
    }

    /// <summary>
    /// Computes a hexadecimal SHA-256 content hash.
    /// </summary>
    private string ComputeHash(byte[] value) {
      byte[] hash = SHA256.HashData(value);
      StringBuilder builder = new StringBuilder();

      foreach (byte current in hash) {
        builder.Append(
          current.ToString("x2", CultureInfo.InvariantCulture)
        );
      }

      return builder.ToString();
    }

    /// <summary>
    /// Formats one UTC timestamp using the timestamp format used by Joplin item exports.
    /// </summary>
    private string FormatJoplinTime(DateTime value) {
      return value
        .ToUniversalTime()
        .ToString(
          "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
          CultureInfo.InvariantCulture
        );
    }

    /// <summary>
    /// Generates a fallback title for a note whose incoming sync item does not provide
    /// one explicitly.
    /// </summary>
    private string GetFallbackNoteTitle(string body) {
      using StringReader reader = new StringReader(body);

      string firstLine = reader.ReadLine();

      if (string.IsNullOrWhiteSpace(firstLine)) {
        return "Untitled";
      }

      string title = firstLine.Trim().TrimStart('#').Trim();

      if (string.IsNullOrWhiteSpace(title)) {
        return "Untitled";
      }

      if (title.Length > 100) {
        return title.Substring(0, 100);
      }

      return title;
    }

    /// <summary>
    /// Determines whether one WebDAV root file name has Joplin sync-item shape.
    /// </summary>
    private bool IsRootItemFile(string path) {
      if (string.IsNullOrWhiteSpace(path)) {
        return false;
      }

      if (path.Count((char value) => value == '/') != 1) {
        return false;
      }

      if (!path.EndsWith(_MarkdownExtension, StringComparison.OrdinalIgnoreCase)) {
        return false;
      }

      string id = Path.GetFileNameWithoutExtension(path);

      if (id.Length != 32) {
        return false;
      }

      return id.All((char value) =>
        (value >= '0' && value <= '9') ||
        (value >= 'a' && value <= 'f') ||
        (value >= 'A' && value <= 'F'));
    }

    /// <summary>
    /// Builds the WebDAV href expected in PROPFIND responses.
    /// </summary>
    private string BuildWebDavHref(string path) {
      StringBuilder builder = new StringBuilder();

      builder.Append(this.Request.PathBase.Value);
      builder.Append("/api/knowledge/joplin");

      if (path != "/") {
        string[] segments = path
          .Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments) {
          builder.Append('/');
          builder.Append(Uri.EscapeDataString(segment));
        }
      }
      else {
        builder.Append('/');
      }

      return builder.ToString();
    }

    /// <summary>
    /// Extracts the facade-relative path from an absolute or relative WebDAV Destination
    /// header.
    /// </summary>
    private string ExtractDestinationPath(string destination) {
      string value = destination;

      if (Uri.TryCreate(destination, UriKind.Absolute, out Uri absoluteUri)) {
        value = absoluteUri.AbsolutePath;
      }

      string basePath = this.Request.PathBase.Value
        + "/api/knowledge/joplin";

      int index = value.IndexOf(
        basePath,
        StringComparison.OrdinalIgnoreCase
      );

      if (index >= 0) {
        value = value.Substring(index + basePath.Length);
      }

      return this.NormalizeWebDavPath(value);
    }

    /// <summary>
    /// Normalizes one controller catch-all value into a WebDAV-relative absolute path.
    /// </summary>
    private string NormalizeWebDavPath(string path) {
      if (string.IsNullOrWhiteSpace(path)) {
        return "/";
      }

      string normalized = path
        .Replace('\\', '/')
        .Trim();

      if (!normalized.StartsWith("/", StringComparison.Ordinal)) {
        normalized = "/" + normalized;
      }

      while (normalized.Contains("//", StringComparison.Ordinal)) {
        normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
      }

      if (normalized.Length > 1 &&
          normalized.EndsWith("/", StringComparison.Ordinal)) {
        normalized = normalized.TrimEnd('/');
      }

      return normalized;
    }

    /// <summary>
    /// Gets the display name of one WebDAV path.
    /// </summary>
    private string GetWebDavDisplayName(string path) {
      if (path == "/") {
        return "Joplin";
      }

      int separator = path.LastIndexOf('/');

      if (separator >= 0 && separator < path.Length - 1) {
        return path.Substring(separator + 1);
      }

      return path;
    }

    /// <summary>
    /// Writes one lightweight trace entry for every incoming WebDAV request.
    /// 
    /// The trace is useful during Joplin compatibility testing because the Joplin UI
    /// often reports only the resulting HTTP status code.
    /// </summary>
    private void TraceWebDavRequest() {
      string contentType = this.Request.ContentType;

      if (string.IsNullOrWhiteSpace(contentType)) {
        contentType = "<none>";
      }

      DevLogger.LogTrace(
        0,
        99999,
        "Joplin WebDAV request: "
        + this.Request.Method
        + " "
        + this.Request.Path.Value
        + " Content-Type="
        + contentType
      );
    }

    /// <summary>
    /// Represents persisted Joplin sync-target metadata.
    /// </summary>
    private sealed class JoplinSyncTargetInfo {

      private int _Version;

      /// <summary>
      /// Gets or sets the Joplin synchronization-target version.
      /// </summary>
      [JsonProperty("version")]
      public int Version {
        get {
          return _Version;
        }
        set {
          _Version = value;
        }
      }
    }

    /// <summary>
    /// Contains persistent projection state.
    /// </summary>
    private sealed class JoplinProjectionState {

      private List<JoplinProjectionRecord> _Records;

      /// <summary>
      /// Creates empty projection state.
      /// </summary>
      public JoplinProjectionState() {
        _Records = new List<JoplinProjectionRecord>();
      }

      /// <summary>
      /// Gets or sets stable item mappings.
      /// </summary>
      public List<JoplinProjectionRecord> Records {
        get {
          return _Records;
        }
        set {
          _Records = value;
        }
      }
    }

    /// <summary>
    /// Stores one stable Joplin item identity and its corresponding knowledge area.
    /// </summary>
    private sealed class JoplinProjectionRecord {

      private string _Id;
      private string _Area;
      private int _Type;
      private DateTime _CreatedUtc;
      private DateTime _ModifiedUtc;
      private string _ParentIdOverride;
      private string _LastContentHash;
      private bool _IsSuppressed;

      /// <summary>
      /// Gets or sets the stable Joplin item ID.
      /// </summary>
      public string Id {
        get {
          return _Id;
        }
        set {
          _Id = value;
        }
      }

      /// <summary>
      /// Gets or sets the backing logical knowledge area.
      /// </summary>
      public string Area {
        get {
          return _Area;
        }
        set {
          _Area = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin item type.
      /// </summary>
      public int Type {
        get {
          return _Type;
        }
        set {
          _Type = value;
        }
      }

      /// <summary>
      /// Gets or sets the first projection time.
      /// </summary>
      public DateTime CreatedUtc {
        get {
          return _CreatedUtc;
        }
        set {
          _CreatedUtc = value;
        }
      }

      /// <summary>
      /// Gets or sets the most recent projected-content modification time.
      /// </summary>
      public DateTime ModifiedUtc {
        get {
          return _ModifiedUtc;
        }
        set {
          _ModifiedUtc = value;
        }
      }

      /// <summary>
      /// Gets or sets an optional Joplin-only parent override.
      /// </summary>
      public string ParentIdOverride {
        get {
          return _ParentIdOverride;
        }
        set {
          _ParentIdOverride = value;
        }
      }

      /// <summary>
      /// Gets or sets the most recent serialized-content hash.
      /// </summary>
      public string LastContentHash {
        get {
          return _LastContentHash;
        }
        set {
          _LastContentHash = value;
        }
      }

      /// <summary>
      /// Gets or sets whether this knowledge-backed item is intentionally hidden from the
      /// Joplin projection after a WebDAV DELETE.
      ///
      /// Suppression is persistent synchronization state only. It never deletes or
      /// modifies the backing knowledge area.
      /// </summary>
      public bool IsSuppressed {
        get {
          return _IsSuppressed;
        }
        set {
          _IsSuppressed = value;
        }
      }
    }

    /// <summary>
    /// Represents the complete current dynamic Joplin projection.
    /// </summary>
    private sealed class JoplinProjection {

      private readonly JoplinProjectionState _State;
      private readonly JoplinProjectedItem[] _Items;

      /// <summary>
      /// Creates one projection snapshot.
      /// </summary>
      public JoplinProjection(
        JoplinProjectionState state,
        JoplinProjectedItem[] items
      ) {
        _State = state;
        _Items = items;
      }

      /// <summary>
      /// Gets persistent projection state.
      /// </summary>
      public JoplinProjectionState State {
        get {
          return _State;
        }
      }

      /// <summary>
      /// Gets all projected Joplin items.
      /// </summary>
      public JoplinProjectedItem[] Items {
        get {
          return _Items;
        }
      }

      /// <summary>
      /// Finds one projected item by Joplin ID.
      /// </summary>
      public JoplinProjectedItem FindItemById(string id) {
        return _Items.FirstOrDefault((JoplinProjectedItem item) =>
          string.Equals(
            item.Record.Id,
            id,
            StringComparison.OrdinalIgnoreCase
          ));
      }

      /// <summary>
      /// Finds one persistent mapping record by Joplin ID.
      /// </summary>
      public JoplinProjectionRecord FindRecordById(string id) {
        return _State.Records.FirstOrDefault((JoplinProjectionRecord record) =>
          string.Equals(
            record.Id,
            id,
            StringComparison.OrdinalIgnoreCase
          ));
      }
    }

    /// <summary>
    /// Represents one projected Joplin notebook or note.
    /// </summary>
    private sealed class JoplinProjectedItem {

      private JoplinProjectionRecord _Record;
      private string _Title;
      private string _ParentId;
      private string _Body;
      private string _SerializedContent;
      private string _ContentHash;

      /// <summary>
      /// Gets or sets the persistent projection record.
      /// </summary>
      public JoplinProjectionRecord Record {
        get {
          return _Record;
        }
        set {
          _Record = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin title.
      /// </summary>
      public string Title {
        get {
          return _Title;
        }
        set {
          _Title = value;
        }
      }

      /// <summary>
      /// Gets or sets the parent notebook ID.
      /// </summary>
      public string ParentId {
        get {
          return _ParentId;
        }
        set {
          _ParentId = value;
        }
      }

      /// <summary>
      /// Gets or sets note Markdown content.
      /// </summary>
      public string Body {
        get {
          return _Body;
        }
        set {
          _Body = value;
        }
      }

      /// <summary>
      /// Gets or sets the complete serialized sync-item content.
      /// </summary>
      public string SerializedContent {
        get {
          return _SerializedContent;
        }
        set {
          _SerializedContent = value;
        }
      }

      /// <summary>
      /// Gets or sets the content ETag hash.
      /// </summary>
      public string ContentHash {
        get {
          return _ContentHash;
        }
        set {
          _ContentHash = value;
        }
      }
    }

    /// <summary>
    /// Represents one parsed Joplin sync item uploaded by a client.
    /// </summary>
    private sealed class JoplinSerializedItem {

      private string _Id;
      private int _Type;
      private string _ParentId;
      private string _Title;
      private string _Body;
      private DateTime _CreatedUtc;
      private DateTime _ModifiedUtc;

      /// <summary>
      /// Gets or sets the Joplin item ID.
      /// </summary>
      public string Id {
        get {
          return _Id;
        }
        set {
          _Id = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin item type.
      /// </summary>
      public int Type {
        get {
          return _Type;
        }
        set {
          _Type = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin parent notebook ID.
      /// </summary>
      public string ParentId {
        get {
          return _ParentId;
        }
        set {
          _ParentId = value;
        }
      }

      /// <summary>
      /// Gets or sets the item title.
      /// </summary>
      public string Title {
        get {
          return _Title;
        }
        set {
          _Title = value;
        }
      }

      /// <summary>
      /// Gets or sets the note body.
      /// </summary>
      public string Body {
        get {
          return _Body;
        }
        set {
          _Body = value;
        }
      }


      /// <summary>
      /// Gets or sets the Joplin creation timestamp.
      /// </summary>
      public DateTime CreatedUtc {
        get {
          return _CreatedUtc;
        }
        set {
          _CreatedUtc = value;
        }
      }

      /// <summary>
      /// Gets or sets the Joplin modification timestamp.
      /// </summary>
      public DateTime ModifiedUtc {
        get {
          return _ModifiedUtc;
        }
        set {
          _ModifiedUtc = value;
        }
      }
    }

    /// <summary>
    /// Describes the result of translating an accepted Joplin sync item into the
    /// provider-neutral knowledge repository.
    /// </summary>
    private enum MaterializationResult {
      Success = 0,
      PendingDependency = 1,
      Failed = 2,
      TemporarilyUnavailable = 3
    }

    /// <summary>
    /// Identifies how one WebDAV resource is backed.
    /// </summary>
    private enum JoplinWebDavSourceKind {
      Root = 0,
      ProjectedKnowledgeItem = 1,
      StateStore = 2
    }

    /// <summary>
    /// Represents one WebDAV resource exposed to Joplin.
    /// </summary>
    private sealed class JoplinWebDavEntry {

      private string _Path;
      private string _DisplayName;
      private bool _IsCollection;
      private long _Length;
      private DateTime _LastModifiedUtc;
      private string _ETag;
      private JoplinWebDavSourceKind _SourceKind;
      private JoplinProjectedItem _ProjectedItem;

      /// <summary>
      /// Gets or sets the WebDAV-relative path.
      /// </summary>
      public string Path {
        get {
          return _Path;
        }
        set {
          _Path = value;
        }
      }

      /// <summary>
      /// Gets or sets the display name.
      /// </summary>
      public string DisplayName {
        get {
          return _DisplayName;
        }
        set {
          _DisplayName = value;
        }
      }

      /// <summary>
      /// Gets or sets whether the resource is a collection.
      /// </summary>
      public bool IsCollection {
        get {
          return _IsCollection;
        }
        set {
          _IsCollection = value;
        }
      }

      /// <summary>
      /// Gets or sets the resource length.
      /// </summary>
      public long Length {
        get {
          return _Length;
        }
        set {
          _Length = value;
        }
      }

      /// <summary>
      /// Gets or sets the resource modification time.
      /// </summary>
      public DateTime LastModifiedUtc {
        get {
          return _LastModifiedUtc;
        }
        set {
          _LastModifiedUtc = value;
        }
      }

      /// <summary>
      /// Gets or sets the ETag.
      /// </summary>
      public string ETag {
        get {
          return _ETag;
        }
        set {
          _ETag = value;
        }
      }

      /// <summary>
      /// Gets or sets the backing source kind.
      /// </summary>
      public JoplinWebDavSourceKind SourceKind {
        get {
          return _SourceKind;
        }
        set {
          _SourceKind = value;
        }
      }

      /// <summary>
      /// Gets or sets the projected knowledge item when applicable.
      /// </summary>
      public JoplinProjectedItem ProjectedItem {
        get {
          return _ProjectedItem;
        }
        set {
          _ProjectedItem = value;
        }
      }
    }
  }
}
