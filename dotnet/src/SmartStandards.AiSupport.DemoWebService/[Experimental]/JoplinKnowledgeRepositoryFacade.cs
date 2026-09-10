using AI.SmartStandards.KnowledgeAccess;
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
using System;
using System.IO;

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
  /// </summary>
  [ApiController]
  [Route("api/knowledge/joplin")]
  [EndpointGroupName("Joplin-KnowledgeRepository")]
  public class JoplinKnowledgeRepositoryController : ControllerBase {

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
    /// Creates the Joplin WebDAV synchronization facade.
    /// </summary>
    /// <param name="knowledgeRepository">
    /// The provider-neutral knowledge repository exposed as Joplin notebooks and notes.
    /// </param>
    /// <param name="syncStateStore">
    /// Persistent storage for Joplin-specific synchronization artifacts that do not
    /// belong in the knowledge repository itself.
    /// </param>
    public JoplinKnowledgeRepositoryController(
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
    [AcceptVerbs("OPTIONS")]
    public IActionResult OptionsRoot() {
      return this.OptionsInternal();
    }

    /// <summary>
    /// Returns WebDAV capability information for any nested synchronization path.
    /// </summary>
    [AcceptVerbs("OPTIONS")]
    [Route("{**path}")]
    public IActionResult OptionsPath(string path) {
      return this.OptionsInternal();
    }

    /// <summary>
    /// Handles WebDAV PROPFIND for the synchronization root.
    /// </summary>
    [AcceptVerbs("PROPFIND")]
    public IActionResult PropFindRoot() {
      return this.PropFindInternal("/");
    }

    /// <summary>
    /// Handles WebDAV PROPFIND for a nested synchronization path.
    /// </summary>
    [AcceptVerbs("PROPFIND")]
    [Route("{**path}")]
    public IActionResult PropFindPath(string path) {
      return this.PropFindInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles GET for the synchronization root or a concrete WebDAV file.
    /// </summary>
    [HttpGet]
    public IActionResult GetRoot() {
      return this.GetInternal("/");
    }

    /// <summary>
    /// Handles GET for a concrete nested WebDAV file.
    /// </summary>
    [HttpGet("{**path}")]
    public IActionResult GetPath(string path) {
      return this.GetInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles HEAD for the synchronization root.
    /// </summary>
    [AcceptVerbs("HEAD")]
    public IActionResult HeadRoot() {
      return this.HeadInternal("/");
    }

    /// <summary>
    /// Handles HEAD for one nested WebDAV path.
    /// </summary>
    [AcceptVerbs("HEAD")]
    [Route("{**path}")]
    public IActionResult HeadPath(string path) {
      return this.HeadInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles PUT for one nested WebDAV file.
    /// </summary>
    [HttpPut("{**path}")]
    public IActionResult PutPath(string path) {
      return this.PutInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles DELETE for one nested WebDAV file or collection.
    /// </summary>
    [HttpDelete("{**path}")]
    public IActionResult DeletePath(string path) {
      return this.DeleteInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles WebDAV MKCOL for one nested collection.
    /// </summary>
    [AcceptVerbs("MKCOL")]
    [Route("{**path}")]
    public IActionResult MkColPath(string path) {
      return this.MkColInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Handles WebDAV MOVE for one nested file or collection.
    /// </summary>
    [AcceptVerbs("MOVE")]
    [Route("{**path}")]
    public IActionResult MovePath(string path) {
      return this.MoveInternal(this.NormalizeWebDavPath(path));
    }

    /// <summary>
    /// Returns the WebDAV methods implemented by this facade.
    /// </summary>
    private IActionResult OptionsInternal() {
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

        bool success;

        if (existingRecord == null) {
          success = this.CreateKnowledgeItemFromJoplin(
            item,
            projection
          );
        }
        else {
          success = this.UpdateKnowledgeItemFromJoplin(
            item,
            existingRecord,
            projection
          );
        }

        if (!success) {
          return this.Conflict(
            "The Joplin item could not be mapped atomically to the knowledge repository."
          );
        }

        this.SaveProjectionState(projection.State);

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
      lock (_SyncRoot) {
        JoplinProjection projection = this.BuildProjection();

        if (this.IsRootItemFile(path)) {
          string itemId = Path.GetFileNameWithoutExtension(path);
          JoplinProjectionRecord record = projection.FindRecordById(itemId);

          if (record != null) {
            bool deleted = _KnowledgeRepository.TryDelete(
              record.Area
            );

            if (!deleted) {
              return this.Conflict(
                "The corresponding knowledge area could not be deleted."
              );
            }

            projection.State.Records.Remove(record);
            this.SaveProjectionState(projection.State);
            return this.StatusCode(StatusCodes.Status204NoContent);
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
    /// Creates a new logical knowledge area from a Joplin note or notebook item.
    /// </summary>
    private bool CreateKnowledgeItemFromJoplin(
      JoplinSerializedItem item,
      JoplinProjection projection
    ) {
      string parentArea = "/";

      if (!string.IsNullOrWhiteSpace(item.ParentId)) {
        JoplinProjectionRecord parentRecord = projection.FindRecordById(
          item.ParentId
        );

        if (parentRecord == null) {
          return false;
        }

        parentArea = parentRecord.Area;
      }

      string[] beforeAreas = _KnowledgeRepository.GetAreas(
        false,
        parentArea
      );

      string childName = item.Title;

      if (item.Type == _JoplinNoteType) {
        childName = "[" + item.Title + "]";
      }

      bool added = _KnowledgeRepository.TryAddSubArea(
        parentArea,
        childName
      );

      if (!added && item.Type == _JoplinNoteType) {
        added = _KnowledgeRepository.TryAddSubArea(
          parentArea,
          item.Title
        );
      }

      if (!added) {
        return false;
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
        return false;
      }

      if (item.Type == _JoplinNoteType &&
          !string.IsNullOrEmpty(item.Body)) {
        bool appended = _KnowledgeRepository.TryAppendContent(
          newArea,
          item.Body
        );

        if (!appended) {
          _KnowledgeRepository.TryDelete(newArea);
          return false;
        }
      }

      DateTime now = DateTime.UtcNow;

      JoplinProjectionRecord record = new JoplinProjectionRecord();
      record.Id = item.Id;
      record.Area = newArea;
      record.Type = item.Type;
      record.CreatedUtc = now;
      record.ModifiedUtc = now;
      record.ParentIdOverride = item.ParentId;
      record.LastContentHash = string.Empty;

      projection.State.Records.Add(record);
      return true;
    }

    /// <summary>
    /// Applies a Joplin note or notebook update to an existing logical knowledge area.
    /// 
    /// Joplin parent changes are persisted as presentation-level parent overrides so
    /// notebook organization can change without requiring a provider-neutral
    /// TryMoveArea operation. Content identity and storage remain bound to the original
    /// knowledge area unless an explicit rename is performed.
    /// </summary>
    private bool UpdateKnowledgeItemFromJoplin(
      JoplinSerializedItem item,
      JoplinProjectionRecord record,
      JoplinProjection projection
    ) {
      string currentTitle = this.GetAreaDisplayName(record.Area);

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
          return false;
        }

        string renamedArea = this.FindRenamedArea(
          parentArea,
          oldArea,
          item.Title
        );

        if (string.IsNullOrEmpty(renamedArea)) {
          return false;
        }

        record.Area = renamedArea;
      }

      if (item.Type == _JoplinNoteType) {
        bool replaced = _KnowledgeRepository.TryReplace(
          record.Area,
          item.Body
        );

        if (!replaced) {
          return false;
        }
      }

      record.ParentIdOverride = item.ParentId;
      record.ModifiedUtc = DateTime.UtcNow;
      record.LastContentHash = string.Empty;

      return true;
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

          state.Records.Add(record);
        }

        currentlyProjectedAreas.Add(
          type.ToString(CultureInfo.InvariantCulture) + ":" + area
        );

        JoplinProjectedItem projectedItem = new JoplinProjectedItem();
        projectedItem.Record = record;
        projectedItem.Title = this.GetAreaDisplayName(area);
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

        string serialized = this.SerializeJoplinItem(
          projectedItem
        );

        string hash = this.ComputeHash(serialized);

        if (!string.Equals(
              record.LastContentHash,
              hash,
              StringComparison.Ordinal
            )) {
          record.LastContentHash = hash;
          record.ModifiedUtc = DateTime.UtcNow;
        }

        projectedItem.SerializedContent = serialized;
        projectedItem.ContentHash = hash;
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
    /// Serializes one projected knowledge item using Joplin's textual sync-item format.
    /// </summary>
    private string SerializeJoplinItem(JoplinProjectedItem item) {
      StringBuilder builder = new StringBuilder();

      builder.Append(item.Title.TrimEnd('\r', '\n'));
      builder.Append("\n\n");

      if (item.Record.Type == _JoplinNoteType &&
          !string.IsNullOrEmpty(item.Body)) {
        builder.Append(item.Body.TrimEnd('\r', '\n'));
        builder.Append("\n\n");
      }

      builder.Append("id: ");
      builder.Append(item.Record.Id);
      builder.Append('\n');
      builder.Append("parent_id: ");
      builder.Append(item.ParentId);
      builder.Append('\n');
      builder.Append("created_time: ");
      builder.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
      builder.Append('\n');
      builder.Append("updated_time: ");
      builder.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
      builder.Append('\n');

      if (item.Record.Type == _JoplinNoteType) {
        builder.Append("is_conflict: 0\n");
        builder.Append("latitude: 0.00000000\n");
        builder.Append("longitude: 0.00000000\n");
        builder.Append("altitude: 0.0000\n");
        builder.Append("author: \n");
        builder.Append("source_url: \n");
        builder.Append("is_todo: 0\n");
        builder.Append("todo_due: 0\n");
        builder.Append("todo_completed: 0\n");
        builder.Append("source: knowledge-repository\n");
        builder.Append("source_application: knowledge-repository\n");
        builder.Append("application_data: \n");
        builder.Append("order: 0\n");
        builder.Append("user_created_time: ");
        builder.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
        builder.Append('\n');
        builder.Append("user_updated_time: ");
        builder.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
        builder.Append('\n');
        builder.Append("encryption_cipher_text: \n");
        builder.Append("encryption_applied: 0\n");
        builder.Append("markup_language: 1\n");
        builder.Append("is_shared: 0\n");
        builder.Append("share_id: \n");
        builder.Append("conflict_original_id: \n");
        builder.Append("master_key_id: \n");
        builder.Append("user_data: \n");
        builder.Append("deleted_time: 0\n");
      }
      else {
        builder.Append("user_created_time: ");
        builder.Append(this.FormatJoplinTime(item.Record.CreatedUtc));
        builder.Append('\n');
        builder.Append("user_updated_time: ");
        builder.Append(this.FormatJoplinTime(item.Record.ModifiedUtc));
        builder.Append('\n');
        builder.Append("encryption_cipher_text: \n");
        builder.Append("encryption_applied: 0\n");
        builder.Append("is_shared: 0\n");
        builder.Append("share_id: \n");
        builder.Append("master_key_id: \n");
        builder.Append("user_data: \n");
        builder.Append("deleted_time: 0\n");
      }

      builder.Append("type_: ");
      builder.Append(item.Record.Type.ToString(CultureInfo.InvariantCulture));
      builder.Append('\n');

      return builder.ToString();
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
        root.LastModifiedUtc = DateTime.UtcNow;
        root.ETag = "root";
        root.SourceKind = JoplinWebDavSourceKind.Root;
        return root;
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

      JoplinSyncStateEntry stateEntry = _SyncStateStore.GetEntry(path);

      if (stateEntry == null) {
        return null;
      }

      JoplinWebDavEntry entry = new JoplinWebDavEntry();
      entry.Path = stateEntry.Path;
      entry.DisplayName = this.GetWebDavDisplayName(stateEntry.Path);
      entry.IsCollection = stateEntry.IsCollection;
      entry.Length = stateEntry.Length;
      entry.LastModifiedUtc = stateEntry.LastModifiedUtc;
      entry.SourceKind = JoplinWebDavSourceKind.StateStore;

      if (stateEntry.IsCollection) {
        entry.ETag = this.ComputeHash(
          stateEntry.Path
          + ":"
          + stateEntry.LastModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture)
        );
      }
      else {
        byte[] bytes = _SyncStateStore.ReadFile(path);
        entry.ETag = this.ComputeHash(bytes);
      }

      return entry;
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

        string childTitle = this.GetAreaDisplayName(child);

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
    /// Gets a human-readable title from one logical area path.
    /// </summary>
    private string GetAreaDisplayName(string area) {
      if (area == "/") {
        return "Knowledge";
      }

      int separator = area.LastIndexOf('/');
      string segment = area;

      if (separator >= 0 && separator < area.Length - 1) {
        segment = area.Substring(separator + 1);
      }

      if (segment.StartsWith("[", StringComparison.Ordinal) &&
          segment.EndsWith("]", StringComparison.Ordinal) &&
          segment.Length >= 2) {
        segment = segment.Substring(1, segment.Length - 2);
      }

      try {
        return Uri.UnescapeDataString(segment);
      }
      catch (UriFormatException ex) {
        DevLogger.LogError(ex);
        return segment;
      }
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


  /// <summary>
  /// Provides persistent opaque storage required by the Joplin WebDAV synchronization
  /// facade in addition to the provider-neutral <see cref="IKnowledgeRepository"/>.
  /// 
  /// Joplin synchronization requires files that are not knowledge content, including
  /// locks, temporary files, sync-target metadata and binary resources. These artifacts
  /// deliberately do not belong in <see cref="IKnowledgeRepository"/>.
  /// </summary>
  public interface IJoplinSyncStateStore {

    /// <summary>
    /// Determines whether an opaque WebDAV file exists.
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// Determines whether an opaque WebDAV collection exists.
    /// </summary>
    bool CollectionExists(string path);

    /// <summary>
    /// Reads an opaque WebDAV file.
    /// </summary>
    byte[] ReadFile(string path);

    /// <summary>
    /// Writes or replaces an opaque WebDAV file.
    /// </summary>
    void WriteFile(string path, byte[] content);

    /// <summary>
    /// Deletes an opaque WebDAV file or collection recursively.
    /// </summary>
    bool Delete(string path);

    /// <summary>
    /// Creates an opaque WebDAV collection.
    /// </summary>
    bool CreateCollection(string path);

    /// <summary>
    /// Moves an opaque WebDAV file or collection.
    /// </summary>
    bool Move(string sourcePath, string targetPath, bool overwrite);

    /// <summary>
    /// Returns direct opaque child entries of one collection.
    /// </summary>
    JoplinSyncStateEntry[] GetChildren(string collectionPath);

    /// <summary>
    /// Gets metadata for one opaque WebDAV file or collection.
    /// </summary>
    JoplinSyncStateEntry GetEntry(string path);

    /// <summary>
    /// Reads one provider-internal metadata document that is never exposed through
    /// WebDAV listing.
    /// </summary>
    string ReadInternalText(string name);

    /// <summary>
    /// Writes one provider-internal metadata document that is never exposed through
    /// WebDAV listing.
    /// </summary>
    void WriteInternalText(string name, string content);
  }

  /// <summary>
  /// Describes one opaque Joplin synchronization storage entry.
  /// </summary>
  public sealed class JoplinSyncStateEntry {

    private string _Path;
    private bool _IsCollection;
    private long _Length;
    private DateTime _LastModifiedUtc;

    /// <summary>
    /// Gets or sets the normalized WebDAV-relative path.
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
    /// Gets or sets whether the entry represents a collection.
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
    /// Gets or sets the content length for file entries.
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
    /// Gets or sets the UTC modification time.
    /// </summary>
    public DateTime LastModifiedUtc {
      get {
        return _LastModifiedUtc;
      }
      set {
        _LastModifiedUtc = value;
      }
    }
  }

  /// <summary>
  /// Implements <see cref="IJoplinSyncStateStore"/> in one isolated file-system
  /// directory.
  /// 
  /// This store contains Joplin-specific synchronization state only. Knowledge content
  /// remains in the injected <see cref="IKnowledgeRepository"/>.
  /// </summary>
  public sealed class FileBasedJoplinSyncStateStore : IJoplinSyncStateStore {

    private const string _InternalDirectoryName = ".internal";

    private readonly object _SyncRoot;
    private readonly string _RootDirectory;

    /// <summary>
    /// Creates a persistent Joplin synchronization state store.
    /// </summary>
    /// <param name="rootDirectory">
    /// The directory used exclusively for Joplin synchronization metadata, resources,
    /// locks and temporary files.
    /// </param>
    public FileBasedJoplinSyncStateStore(string rootDirectory) {
      if (string.IsNullOrWhiteSpace(rootDirectory)) {
        throw new ArgumentException("A Joplin synchronization state directory is required.", nameof(rootDirectory));
      }

      _SyncRoot = new object();
      _RootDirectory = Path.GetFullPath(rootDirectory);

      Directory.CreateDirectory(_RootDirectory);
      Directory.CreateDirectory(Path.Combine(_RootDirectory, _InternalDirectoryName));
    }

    /// <summary>
    /// Determines whether an opaque WebDAV file exists.
    /// </summary>
    public bool FileExists(string path) {
      lock (_SyncRoot) {
        return File.Exists(this.ResolvePath(path));
      }
    }

    /// <summary>
    /// Determines whether an opaque WebDAV collection exists.
    /// </summary>
    public bool CollectionExists(string path) {
      lock (_SyncRoot) {
        return Directory.Exists(this.ResolvePath(path));
      }
    }

    /// <summary>
    /// Reads an opaque WebDAV file.
    /// </summary>
    public byte[] ReadFile(string path) {
      lock (_SyncRoot) {
        return File.ReadAllBytes(this.ResolvePath(path));
      }
    }

    /// <summary>
    /// Writes or replaces an opaque WebDAV file.
    /// </summary>
    public void WriteFile(string path, byte[] content) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(path);
        string parentDirectory = Path.GetDirectoryName(physicalPath);

        if (string.IsNullOrEmpty(parentDirectory)) {
          throw new InvalidOperationException("Cannot resolve the parent directory.");
        }

        Directory.CreateDirectory(parentDirectory);
        File.WriteAllBytes(physicalPath, content);
      }
    }

    /// <summary>
    /// Deletes an opaque WebDAV file or collection recursively.
    /// </summary>
    public bool Delete(string path) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(path);

        if (File.Exists(physicalPath)) {
          File.Delete(physicalPath);
          return true;
        }

        if (Directory.Exists(physicalPath)) {
          Directory.Delete(physicalPath, true);
          return true;
        }

        return false;
      }
    }

    /// <summary>
    /// Creates an opaque WebDAV collection.
    /// </summary>
    public bool CreateCollection(string path) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(path);

        if (Directory.Exists(physicalPath)) {
          return false;
        }

        if (File.Exists(physicalPath)) {
          return false;
        }

        Directory.CreateDirectory(physicalPath);
        return true;
      }
    }

    /// <summary>
    /// Moves an opaque WebDAV file or collection.
    /// </summary>
    public bool Move(string sourcePath, string targetPath, bool overwrite) {
      lock (_SyncRoot) {
        string sourcePhysicalPath = this.ResolvePath(sourcePath);
        string targetPhysicalPath = this.ResolvePath(targetPath);

        if (File.Exists(sourcePhysicalPath)) {
          string targetParent = Path.GetDirectoryName(targetPhysicalPath);

          if (!string.IsNullOrEmpty(targetParent)) {
            Directory.CreateDirectory(targetParent);
          }

          if (File.Exists(targetPhysicalPath)) {
            if (!overwrite) {
              return false;
            }

            File.Delete(targetPhysicalPath);
          }

          File.Move(sourcePhysicalPath, targetPhysicalPath);
          return true;
        }

        if (Directory.Exists(sourcePhysicalPath)) {
          if (Directory.Exists(targetPhysicalPath)) {
            if (!overwrite) {
              return false;
            }

            Directory.Delete(targetPhysicalPath, true);
          }

          Directory.Move(sourcePhysicalPath, targetPhysicalPath);
          return true;
        }

        return false;
      }
    }

    /// <summary>
    /// Returns direct opaque child entries of one collection.
    /// </summary>
    public JoplinSyncStateEntry[] GetChildren(string collectionPath) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(collectionPath);

        if (!Directory.Exists(physicalPath)) {
          return Array.Empty<JoplinSyncStateEntry>();
        }

        string internalDirectory = Path.Combine(_RootDirectory, _InternalDirectoryName);

        string[] directories = Directory.GetDirectories(physicalPath);
        string[] files = Directory.GetFiles(physicalPath);

        JoplinSyncStateEntry[] entries = new JoplinSyncStateEntry[
          directories.Length + files.Length
        ];

        int index = 0;

        foreach (string directory in directories) {
          if (string.Equals(
                Path.GetFullPath(directory),
                Path.GetFullPath(internalDirectory),
                StringComparison.OrdinalIgnoreCase
              )) {
            continue;
          }

          DirectoryInfo info = new DirectoryInfo(directory);

          JoplinSyncStateEntry entry = new JoplinSyncStateEntry();
          entry.Path = this.ToLogicalPath(directory);
          entry.IsCollection = true;
          entry.Length = 0;
          entry.LastModifiedUtc = info.LastWriteTimeUtc;

          entries[index] = entry;
          index++;
        }

        foreach (string file in files) {
          FileInfo info = new FileInfo(file);

          JoplinSyncStateEntry entry = new JoplinSyncStateEntry();
          entry.Path = this.ToLogicalPath(file);
          entry.IsCollection = false;
          entry.Length = info.Length;
          entry.LastModifiedUtc = info.LastWriteTimeUtc;

          entries[index] = entry;
          index++;
        }

        if (index == entries.Length) {
          return entries;
        }

        JoplinSyncStateEntry[] compact = new JoplinSyncStateEntry[index];
        Array.Copy(entries, compact, index);
        return compact;
      }
    }

    /// <summary>
    /// Gets metadata for one opaque WebDAV file or collection.
    /// </summary>
    public JoplinSyncStateEntry GetEntry(string path) {
      lock (_SyncRoot) {
        string physicalPath = this.ResolvePath(path);

        if (File.Exists(physicalPath)) {
          FileInfo info = new FileInfo(physicalPath);

          JoplinSyncStateEntry entry = new JoplinSyncStateEntry();
          entry.Path = this.NormalizePath(path);
          entry.IsCollection = false;
          entry.Length = info.Length;
          entry.LastModifiedUtc = info.LastWriteTimeUtc;
          return entry;
        }

        if (Directory.Exists(physicalPath)) {
          DirectoryInfo info = new DirectoryInfo(physicalPath);

          JoplinSyncStateEntry entry = new JoplinSyncStateEntry();
          entry.Path = this.NormalizePath(path);
          entry.IsCollection = true;
          entry.Length = 0;
          entry.LastModifiedUtc = info.LastWriteTimeUtc;
          return entry;
        }

        return null;
      }
    }

    /// <summary>
    /// Reads one provider-internal metadata document.
    /// </summary>
    public string ReadInternalText(string name) {
      lock (_SyncRoot) {
        string path = this.ResolveInternalPath(name);

        if (!File.Exists(path)) {
          return string.Empty;
        }

        return File.ReadAllText(path);
      }
    }

    /// <summary>
    /// Writes one provider-internal metadata document.
    /// </summary>
    public void WriteInternalText(string name, string content) {
      lock (_SyncRoot) {
        string path = this.ResolveInternalPath(name);
        File.WriteAllText(path, content);
      }
    }

    /// <summary>
    /// Resolves one logical opaque-storage path without allowing traversal outside the
    /// configured synchronization-state root.
    /// </summary>
    private string ResolvePath(string path) {
      string normalized = this.NormalizePath(path);
      string relative = normalized.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

      string physical = Path.GetFullPath(
        Path.Combine(_RootDirectory, relative)
      );

      string rootPrefix = _RootDirectory
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

      if (!physical.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
          !string.Equals(physical, _RootDirectory, StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidOperationException("The Joplin synchronization path escapes the configured state root.");
      }

      return physical;
    }

    /// <summary>
    /// Resolves one internal metadata file name.
    /// </summary>
    private string ResolveInternalPath(string name) {
      string safeName = Path.GetFileName(name);

      if (!string.Equals(name, safeName, StringComparison.Ordinal)) {
        throw new InvalidOperationException("Invalid internal Joplin state file name.");
      }

      return Path.Combine(
        _RootDirectory,
        _InternalDirectoryName,
        safeName
      );
    }

    /// <summary>
    /// Converts one physical path to a normalized WebDAV-relative path.
    /// </summary>
    private string ToLogicalPath(string physicalPath) {
      string relative = Path.GetRelativePath(_RootDirectory, physicalPath)
        .Replace(Path.DirectorySeparatorChar, '/');

      return "/" + relative;
    }

    /// <summary>
    /// Normalizes one opaque WebDAV path.
    /// </summary>
    private string NormalizePath(string path) {
      if (string.IsNullOrWhiteSpace(path)) {
        return "/";
      }

      string normalized = path.Trim().Replace('\\', '/');

      if (!normalized.StartsWith("/", StringComparison.Ordinal)) {
        normalized = "/" + normalized;
      }

      while (normalized.Contains("//", StringComparison.Ordinal)) {
        normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
      }

      if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal)) {
        normalized = normalized.TrimEnd('/');
      }

      return normalized;
    }

  }

}
