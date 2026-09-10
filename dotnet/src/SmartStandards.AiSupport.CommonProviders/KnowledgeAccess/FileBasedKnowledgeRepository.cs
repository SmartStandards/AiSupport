using Logging.SmartStandards;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Logging.SmartStandards.CopyForAI.SmartStandards;

namespace AI.SmartStandards.KnowledgeAccess {

  /// <summary>
  /// Implements <see cref="IKnowledgeRepository"/> on top of a file-system directory
  /// containing an arbitrary hierarchy of directories and Markdown files.
  /// 
  /// The configured directory itself represents the logical repository root "/".
  /// Directories are exposed as <see cref="ContentLevel.ContentAggregation"/> areas.
  /// Markdown files are exposed as <see cref="ContentLevel.ContentContainer"/> areas
  /// whose logical names are enclosed in square brackets. Markdown headings below a
  /// document are exposed as additional <see cref="ContentLevel.ContentContainer"/>
  /// areas.
  /// 
  /// Files other than Markdown files are not exposed as knowledge areas. They are
  /// therefore ignored by content enumeration and content reads. Directory rename
  /// operations naturally move such files together with their containing directory,
  /// because the directory itself is the provider's physical representation of the
  /// logical aggregation area.
  /// 
  /// The implementation uses atomic mutation scopes. Before a mutating file-system
  /// operation is published, a provider-level snapshot is created. If the mutation
  /// cannot be completed, the previous state is restored.
  /// </summary>
  public class FileBasedKnowledgeRepository : IKnowledgeRepository {

    private const string _MarkdownExtension = ".md";
    private const string _RootArea = "/";
    private const int _MaximumMarkdownHeadingLevel = 6;

    private static readonly Regex _AtxHeadingRegex = new Regex(
      @"^( {0,3})(#{1,6})(?:[ \t]+|$)(.*?)(?:[ \t]+#+[ \t]*)?(?:\r\n|\n|\r)?$",
      RegexOptions.Compiled
    );

    protected readonly object _SyncRoot = new object();

    private string _RootDirectory;
    private readonly bool _ReadOnly;
    private bool _Initialized;

    /// <summary>
    /// Creates a file-based knowledge repository rooted at the specified directory.
    /// 
    /// The directory is created when it does not yet exist and the repository is not
    /// read-only. A read-only repository requires the directory to exist.
    /// 
    /// No global process configuration is modified. All operations are scoped to the
    /// supplied directory.
    /// </summary>
    /// <param name="rootDirectory">
    /// The physical directory that represents the logical knowledge repository root.
    /// </param>
    /// <param name="readOnly">
    /// true to disable all mutating operations; false to allow mutations according to
    /// the concrete area's capabilities.
    /// </param>
    public FileBasedKnowledgeRepository(string rootDirectory, bool readOnly) {
      _RootDirectory = string.Empty;
      _ReadOnly = readOnly;
      _Initialized = false;

      this.InitializeRootDirectory(rootDirectory);
    }

    /// <summary>
    /// Initializes the base repository without assigning a physical root immediately.
    /// 
    /// This constructor exists for derived providers that must prepare their storage
    /// before the file-based projection can be attached to it.
    /// </summary>
    /// <param name="readOnly">Whether the derived repository is read-only.</param>
    protected FileBasedKnowledgeRepository(bool readOnly) {
      _RootDirectory = string.Empty;
      _ReadOnly = readOnly;
      _Initialized = false;
    }

    /// <summary>
    /// Gets whether this repository instance is read-only.
    /// </summary>
    protected bool IsReadOnly {
      get {
        return _ReadOnly;
      }
    }

    /// <summary>
    /// Gets the physical root directory used by this provider.
    /// </summary>
    protected string RootDirectory {
      get {
        this.EnsureInitialized();
        return _RootDirectory;
      }
    }

    /// <summary>
    /// Initializes the physical root directory used by the file-based provider.
    /// 
    /// Derived providers may call this once after preparing their storage.
    /// </summary>
    /// <param name="rootDirectory">The physical knowledge root directory.</param>
    protected void InitializeRootDirectory(string rootDirectory) {
      if (_Initialized) {
        throw new InvalidOperationException("The knowledge repository root directory has already been initialized.");
      }

      if (string.IsNullOrWhiteSpace(rootDirectory)) {
        throw new ArgumentException("A knowledge repository root directory is required.", nameof(rootDirectory));
      }

      string fullPath = Path.GetFullPath(rootDirectory);

      if (!Directory.Exists(fullPath)) {
        if (_ReadOnly) {
          throw new DirectoryNotFoundException("The read-only knowledge repository directory does not exist: " + fullPath);
        }

        Directory.CreateDirectory(fullPath);
      }

      _RootDirectory = fullPath;
      _Initialized = true;
    }

    /// <summary>
    /// Returns logical area paths below the specified start area.
    /// 
    /// Directories are returned in deterministic ordinal name order. Markdown files are
    /// represented as bracketed document areas and participate in the same deterministic
    /// directory ordering. Headings inside a Markdown document preserve their exact
    /// document order.
    /// 
    /// Recursive enumeration uses pre-order traversal. Each parent is returned before
    /// its descendants.
    /// </summary>
    /// <param name="recurse">
    /// true to return the complete descendant tree; false to return direct children only.
    /// </param>
    /// <param name="startArea">
    /// The absolute logical start area. "/" represents the repository root.
    /// </param>
    /// <returns>The matching logical area paths in deterministic hierarchical order.</returns>
    public string[] GetAreas(bool recurse, string startArea = "/") {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor startDescriptor = this.ResolveArea(startArea, null);
        List<string> result = new List<string>();

        this.CollectChildAreas(startDescriptor, recurse, result, null);

        return result.ToArray();
      }
    }

    /// <summary>
    /// Searches all descendant areas below <paramref name="startArea"/> for the supplied
    /// keyword.
    /// 
    /// The implementation searches each area's logical path and display name. For
    /// content-container areas it additionally searches direct textual content.
    /// 
    /// Matching is case-insensitive and ordinal. Results preserve the natural repository
    /// order returned by recursive area enumeration.
    /// </summary>
    /// <param name="keyword">The keyword to search for.</param>
    /// <param name="startArea">The absolute logical search scope.</param>
    /// <returns>Matching absolute logical area paths in deterministic order.</returns>
    public string[] GetAreasByKeyword(string keyword, string startArea = "/") {
      if (string.IsNullOrWhiteSpace(keyword)) {
        return Array.Empty<string>();
      }

      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor startDescriptor = this.ResolveArea(startArea, null);
        List<string> allAreas = new List<string>();
        List<string> result = new List<string>();

        this.CollectChildAreas(startDescriptor, true, allAreas, null);

        foreach (string areaPath in allAreas) {
          AreaDescriptor descriptor = this.ResolveArea(areaPath, null);

          if (areaPath.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
            result.Add(areaPath);
            continue;
          }

          if (descriptor.DisplayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
            result.Add(areaPath);
            continue;
          }

          if (descriptor.ContentLevel == ContentLevel.ContentContainer) {
            string directContent = this.GetDirectContentCore(descriptor);

            if (directContent.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
              result.Add(areaPath);
            }
          }
        }

        return result.ToArray();
      }
    }

    /// <summary>
    /// Returns the effective capabilities of a logical area.
    /// 
    /// Directories are exposed as content aggregations. They support child areas and,
    /// when writable, may create directories or bracketed Markdown document children,
    /// accept structured sparse content merges, and truncate their exposed subordinate
    /// area tree.
    /// 
    /// Markdown documents and headings are content containers. They may own direct
    /// content, may receive appended content, and may contain subordinate headings.
    /// 
    /// A Markdown heading at physical level 6 cannot create additional nested headings
    /// because standard Markdown does not provide a deeper ATX heading level.
    /// 
    /// In read-only mode every mutation capability is false regardless of physical
    /// file-system permissions.
    /// </summary>
    public void GetAreaCapabilities(
      string area,
      out ContentLevel contentLevel,
      out bool supportsSubAreas,
      out bool canBeRenamed,
      out bool canBeDeleted,
      out bool canAddSubAreas,
      out bool canAppendContent,
      out bool canTruncate
    ) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(area, null);

        contentLevel = descriptor.ContentLevel;
        supportsSubAreas = this.GetSupportsSubAreas(descriptor);

        if (_ReadOnly) {
          canBeRenamed = false;
          canBeDeleted = false;
          canAddSubAreas = false;
          canAppendContent = false;
          canTruncate = false;
          return;
        }

        canBeRenamed = descriptor.Kind != AreaKind.Root;
        canBeDeleted = descriptor.Kind != AreaKind.Root;
        canAddSubAreas = this.GetCanAddSubAreas(descriptor);
        canAppendContent = descriptor.ContentLevel != ContentLevel.BeyondContent;
        canTruncate = descriptor.ContentLevel != ContentLevel.BeyondContent;
      }
    }

    /// <summary>
    /// Determines whether the specified area owns non-empty direct textual content.
    /// 
    /// Content aggregation areas never own direct content and therefore always return
    /// false. Markdown document and heading containers return true only when their own
    /// direct content block contains non-whitespace text.
    /// </summary>
    public bool HasDirectContent(string area) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(area, null);

        if (descriptor.ContentLevel != ContentLevel.ContentContainer) {
          return false;
        }

        string content = this.GetDirectContentCore(descriptor);
        return !string.IsNullOrWhiteSpace(content);
      }
    }

    /// <summary>
    /// Returns only the direct textual content owned by the specified area.
    /// 
    /// Directories are content aggregations and therefore return an empty string.
    /// A Markdown document returns its preamble before the first heading. A Markdown
    /// heading returns the text belonging to that heading before its first child heading.
    /// </summary>
    public string GetDirectContent(string area) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(area, null);
        return this.GetDirectContentCore(descriptor);
      }
    }

    /// <summary>
    /// Returns the complete textual content exposed through the specified area.
    /// 
    /// For a Markdown document or heading, the result consists of its direct content plus
    /// its complete subordinate heading tree. The addressed heading itself is not emitted
    /// as framing; descendants are rebased relative to the requested area.
    /// 
    /// For a directory aggregation, the result is a deterministic Markdown projection of
    /// all exposed descendant directories and documents. Directory children are rendered
    /// as headings using their logical name. Markdown document children are rendered as
    /// headings whose names are enclosed in square brackets. This projection allows a
    /// structured aggregate to be understood without leaking physical paths.
    /// </summary>
    public string GetAggregatedContent(string area) {
      lock (_SyncRoot) {
        this.PrepareForRead();

        AreaDescriptor descriptor = this.ResolveArea(area, null);

        if (descriptor.ContentLevel == ContentLevel.BeyondContent) {
          return string.Empty;
        }

        if (descriptor.Kind == AreaKind.Root || descriptor.Kind == AreaKind.Directory) {
          StringBuilder aggregationBuilder = new StringBuilder();
          this.RenderAggregationChildren(descriptor, aggregationBuilder, 1, null);
          return aggregationBuilder.ToString();
        }

        MarkdownDocumentModel document = descriptor.Document;
        MarkdownNode contentNode = descriptor.Node;

        if (descriptor.Kind == AreaKind.Document) {
          contentNode = document.Root;
        }

        return this.RenderContentSubtree(contentNode, document.NewLine);
      }
    }

    /// <summary>
    /// Atomically deletes the specified area and its complete descendant tree.
    /// 
    /// Deleting a directory removes the represented directory recursively. Deleting a
    /// Markdown document removes the represented `.md` file. Deleting a heading removes
    /// that heading, its direct content, and every subordinate heading in the same
    /// document.
    /// 
    /// Deleting the logical root is not allowed.
    /// </summary>
    public bool TryDelete(string area) {
      return this.ExecuteMutation(
        "Delete knowledge area '" + area + "'",
        (MutationContext context) => this.TryDeleteCore(area, context)
      );
    }

    /// <summary>
    /// Atomically renames the specified logical area while preserving its complete
    /// logical subtree and sibling position.
    /// 
    /// The physical effect intentionally depends on the addressed area:
    /// 
    /// - Renaming a directory renames the physical directory. Every Markdown document
    ///   and nested directory below it therefore moves together with the directory.
    /// - Renaming a Markdown document renames the physical `.md` file while preserving
    ///   its content.
    /// - Renaming a heading changes the Markdown heading text while preserving its direct
    ///   content and subordinate headings.
    /// 
    /// This provider-specific physical behavior is intentionally hidden behind the
    /// logical rename operation.
    /// </summary>
    public bool TryRename(string area, string newName) {
      return this.ExecuteMutation(
        "Rename knowledge area '" + area + "' to '" + newName + "'",
        (MutationContext context) => this.TryRenameCore(area, newName, context)
      );
    }

    /// <summary>
    /// Atomically creates one direct child area.
    /// 
    /// Below a directory aggregation, an unbracketed name creates a subdirectory and a
    /// name enclosed in square brackets creates a Markdown document.
    /// 
    /// Below a Markdown document or heading container, the name creates a new subordinate
    /// Markdown heading appended after all existing direct child headings.
    /// </summary>
    public bool TryAddSubArea(string area, string name) {
      return this.ExecuteMutation(
        "Add knowledge sub-area '" + name + "' below '" + area + "'",
        (MutationContext context) => this.TryAddSubAreaCore(area, name, context)
      );
    }

    /// <summary>
    /// Atomically performs a non-destructive sparse hierarchical merge of the supplied
    /// Markdown-shaped content into the target area.
    /// 
    /// On a content container, free text before the first incoming heading is appended
    /// to the target's direct content block. Existing child headings are matched only
    /// among direct children and merged recursively. Missing headings are appended after
    /// existing siblings.
    /// 
    /// On a directory content aggregation, free root text is invalid because aggregation
    /// areas own no direct content. Incoming heading structure is interpreted as logical
    /// subordinate areas. Unbracketed headings represent directory aggregation areas;
    /// bracketed headings represent Markdown document containers. Once a document
    /// container is reached, subordinate headings represent content-container sections
    /// inside that document.
    /// 
    /// Existing siblings are never reordered. Existing content is never replaced or
    /// removed by append.
    /// </summary>
    public bool TryAppendContent(string area, string content) {
      return this.ExecuteMutation(
        "Append content to knowledge area '" + area + "'",
        (MutationContext context) => this.TryAppendContentCore(area, content, context)
      );
    }

    /// <summary>
    /// Atomically clears the complete content scope of the addressed area while
    /// preserving the area itself.
    /// 
    /// For a Markdown document, its preamble and all headings are removed while the file
    /// remains present.
    /// 
    /// For a heading, its direct content and subordinate headings are removed while the
    /// addressed heading itself remains present.
    /// 
    /// For a directory aggregation, all exposed direct sub-areas are removed. Direct
    /// non-Markdown files that are not themselves exposed as knowledge areas remain
    /// untouched.
    /// </summary>
    public bool TryTruncate(string area) {
      return this.ExecuteMutation(
        "Truncate knowledge area '" + area + "'",
        (MutationContext context) => this.TryTruncateCore(area, context)
      );
    }

    /// <summary>
    /// Atomically replaces the complete content scope of the addressed area.
    /// 
    /// The operation is semantically equivalent to truncating the area and then applying
    /// the supplied content through sparse hierarchical append, but both phases are
    /// executed within one mutation transaction.
    /// </summary>
    public bool TryReplace(string area, string newContent) {
      return this.ExecuteMutation(
        "Replace content of knowledge area '" + area + "'",
        (MutationContext context) => {
          if (!this.TryTruncateCore(area, context)) {
            return false;
          }

          return this.TryAppendContentCore(area, newContent, context);
        }
      );
    }

    /// <summary>
    /// Atomically moves the complete content scope of one content-capable area into
    /// another content-capable area.
    /// 
    /// The source area itself is preserved and becomes empty after success. Its content
    /// is merged into the target using the same sparse hierarchical merge rules used by
    /// <see cref="TryAppendContent(string, string)"/>.
    /// 
    /// The source and target may belong to different Markdown files or different
    /// directory aggregation branches. The provider therefore may physically transfer
    /// content between files while exposing only one logical content-move operation.
    /// 
    /// The target may not equal the source and may not be located inside the source
    /// subtree.
    /// </summary>
    public bool TryMoveContent(string sourceArea, string targetArea) {
      return this.ExecuteMutation(
        "Move content from knowledge area '" + sourceArea + "' to '" + targetArea + "'",
        (MutationContext context) => this.TryMoveContentCore(sourceArea, targetArea, context)
      );
    }

    /// <summary>
    /// Allows derived providers to refresh or synchronize their backing storage before
    /// a read operation is resolved.
    /// </summary>
    protected virtual void PrepareForRead() {
    }

    /// <summary>
    /// Executes one logical mutation atomically.
    /// 
    /// The file-based implementation snapshots the configured knowledge root before
    /// applying the mutation. Derived providers may override this method to provide a
    /// more efficient transaction mechanism while retaining the same external atomicity
    /// guarantee.
    /// </summary>
    protected virtual bool ExecuteMutation(
      string operationDescription,
      Func<MutationContext, bool> mutation
    ) {
      if (_ReadOnly) {
        return false;
      }

      lock (_SyncRoot) {
        this.EnsureInitialized();

        string snapshotDirectory = string.Empty;

        try {
          snapshotDirectory = this.CreateMutationSnapshot();

          bool success = this.ApplyMutation(mutation);

          if (!success) {
            this.RestoreMutationSnapshot(snapshotDirectory);
            return false;
          }

          this.DeleteMutationSnapshot(snapshotDirectory);
          return true;
        }
        catch (IOException ex) {
          DevLogger.LogError(ex);
          this.TryRestoreMutationSnapshot(snapshotDirectory);
          return false;
        }
        catch (UnauthorizedAccessException ex) {
          DevLogger.LogError(ex);
          this.TryRestoreMutationSnapshot(snapshotDirectory);
          return false;
        }
      }
    }

    /// <summary>
    /// Applies one logical mutation to the currently prepared backing store and persists
    /// every changed Markdown document.
    /// 
    /// Derived providers can reuse this method inside their own transaction mechanism.
    /// </summary>
    protected bool ApplyMutation(Func<MutationContext, bool> mutation) {
      MutationContext context = new MutationContext(this);

      bool success = mutation(context);

      if (!success) {
        return false;
      }

      context.SaveChanges();
      return true;
    }

    /// <summary>
    /// Resolves a logical area path to its provider-specific representation.
    /// </summary>
    protected AreaDescriptor ResolveArea(string area, MutationContext context) {
      this.EnsureInitialized();

      string normalizedArea = this.NormalizeAreaPath(area);

      if (normalizedArea == _RootArea) {
        return AreaDescriptor.CreateRoot(_RootDirectory);
      }

      string[] segments = normalizedArea
        .Split('/', StringSplitOptions.RemoveEmptyEntries);

      AreaDescriptor current = AreaDescriptor.CreateRoot(_RootDirectory);

      foreach (string segment in segments) {
        if (current.Kind == AreaKind.Root || current.Kind == AreaKind.Directory) {
          if (this.IsDocumentSegment(segment)) {
            string documentName = this.DecodeAreaSegment(segment.Substring(1, segment.Length - 2));
            string documentPath = this.GetSafePhysicalChildPath(
              current.PhysicalPath,
              documentName,
              _MarkdownExtension
            );

            if (!File.Exists(documentPath)) {
              throw new InvalidOperationException("The knowledge area does not exist: " + normalizedArea);
            }

            MarkdownDocumentModel document = this.LoadDocument(documentPath, context);
            current = AreaDescriptor.CreateDocument(
              this.CombineAreaPath(current.AreaPath, this.CreateDocumentSegment(documentName)),
              documentName,
              documentPath,
              document
            );
          }
          else {
            string directoryName = this.DecodeAreaSegment(segment);
            string directoryPath = this.GetSafePhysicalChildPath(
              current.PhysicalPath,
              directoryName,
              string.Empty
            );

            if (!Directory.Exists(directoryPath)) {
              throw new InvalidOperationException("The knowledge area does not exist: " + normalizedArea);
            }

            current = AreaDescriptor.CreateDirectory(
              this.CombineAreaPath(current.AreaPath, this.EncodeAreaSegment(directoryName)),
              directoryName,
              directoryPath
            );
          }

          continue;
        }

        MarkdownNode parentNode = current.Node;

        if (current.Kind == AreaKind.Document) {
          parentNode = current.Document.Root;
        }

        MarkdownNode childNode = parentNode.Children
          .FirstOrDefault((MarkdownNode candidate) =>
            string.Equals(candidate.LogicalSegment, segment, StringComparison.Ordinal));

        if (childNode == null) {
          throw new InvalidOperationException("The knowledge area does not exist: " + normalizedArea);
        }

        current = AreaDescriptor.CreateHeading(
          this.CombineAreaPath(current.AreaPath, childNode.LogicalSegment),
          childNode.Title,
          current.PhysicalPath,
          current.Document,
          childNode
        );
      }

      return current;
    }

    /// <summary>
    /// Gets the repository-relative physical root used by this instance.
    /// </summary>
    protected string GetPhysicalRootDirectory() {
      return this.RootDirectory;
    }

    private bool TryDeleteCore(string area, MutationContext context) {
      AreaDescriptor descriptor = this.ResolveArea(area, context);

      if (descriptor.Kind == AreaKind.Root) {
        return false;
      }

      if (descriptor.Kind == AreaKind.Directory) {
        context.ForgetDocumentsBelow(descriptor.PhysicalPath);
        Directory.Delete(descriptor.PhysicalPath, true);
        return true;
      }

      if (descriptor.Kind == AreaKind.Document) {
        context.ForgetDocument(descriptor.PhysicalPath);
        File.Delete(descriptor.PhysicalPath);
        return true;
      }

      MarkdownNode parent = descriptor.Node.Parent;

      if (parent == null) {
        return false;
      }

      bool removed = parent.Children.Remove(descriptor.Node);

      if (!removed) {
        return false;
      }

      descriptor.Document.MarkChanged();
      return true;
    }

    private bool TryRenameCore(string area, string newName, MutationContext context) {
      if (string.IsNullOrWhiteSpace(newName)) {
        return false;
      }

      AreaDescriptor descriptor = this.ResolveArea(area, context);

      if (descriptor.Kind == AreaKind.Root) {
        return false;
      }

      if (descriptor.Kind == AreaKind.Directory) {
        if (this.IsDocumentSegment(newName)) {
          return false;
        }

        string parentDirectory = Path.GetDirectoryName(descriptor.PhysicalPath);

        if (string.IsNullOrEmpty(parentDirectory)) {
          return false;
        }

        string normalizedNewName = newName.Trim();

        if (!this.IsValidPhysicalName(normalizedNewName)) {
          return false;
        }

        string targetPath = this.GetSafePhysicalChildPath(
          parentDirectory,
          normalizedNewName,
          string.Empty
        );

        if (Directory.Exists(targetPath) || File.Exists(targetPath)) {
          return false;
        }

        context.ForgetDocumentsBelow(descriptor.PhysicalPath);
        Directory.Move(descriptor.PhysicalPath, targetPath);
        return true;
      }

      if (descriptor.Kind == AreaKind.Document) {
        string documentName = newName;

        if (this.IsDocumentSegment(newName)) {
          documentName = newName.Substring(1, newName.Length - 2);
        }

        documentName = documentName.Trim();

        if (string.IsNullOrWhiteSpace(documentName)) {
          return false;
        }

        if (!this.IsValidPhysicalName(documentName)) {
          return false;
        }

        string parentDirectory = Path.GetDirectoryName(descriptor.PhysicalPath);

        if (string.IsNullOrEmpty(parentDirectory)) {
          return false;
        }

        string targetPath = this.GetSafePhysicalChildPath(
          parentDirectory,
          documentName,
          _MarkdownExtension
        );

        if (File.Exists(targetPath) || Directory.Exists(targetPath)) {
          return false;
        }

        context.ForgetDocument(descriptor.PhysicalPath);
        File.Move(descriptor.PhysicalPath, targetPath);
        return true;
      }

      string headingName = newName.Trim();

      if (string.IsNullOrWhiteSpace(headingName)) {
        return false;
      }

      MarkdownNode parent = descriptor.Node.Parent;

      if (parent == null) {
        return false;
      }

      bool duplicate = parent.Children.Any((MarkdownNode sibling) =>
        sibling != descriptor.Node &&
        string.Equals(sibling.Title, headingName, StringComparison.Ordinal));

      if (duplicate) {
        return false;
      }

      descriptor.Node.Title = headingName;
      descriptor.Node.HeadingLineModified = true;
      this.RebuildLogicalSegments(parent);
      descriptor.Document.MarkChanged();

      return true;
    }

    private bool TryAddSubAreaCore(string area, string name, MutationContext context) {
      if (string.IsNullOrWhiteSpace(name)) {
        return false;
      }

      AreaDescriptor descriptor = this.ResolveArea(area, context);

      if (descriptor.Kind == AreaKind.Root || descriptor.Kind == AreaKind.Directory) {
        if (this.IsDocumentSegment(name)) {
          string documentName = name.Substring(1, name.Length - 2).Trim();

          if (string.IsNullOrWhiteSpace(documentName)) {
            return false;
          }

          if (!this.IsValidPhysicalName(documentName)) {
            return false;
          }

          string documentPath = this.GetSafePhysicalChildPath(
            descriptor.PhysicalPath,
            documentName,
            _MarkdownExtension
          );

          if (File.Exists(documentPath) || Directory.Exists(documentPath)) {
            return false;
          }

          File.WriteAllText(documentPath, string.Empty, new UTF8Encoding(false));
          context.ForgetDocument(documentPath);
          return true;
        }

        string directoryName = name.Trim();

        if (!this.IsValidPhysicalName(directoryName)) {
          return false;
        }

        string directoryPath = this.GetSafePhysicalChildPath(
          descriptor.PhysicalPath,
          directoryName,
          string.Empty
        );

        if (Directory.Exists(directoryPath) || File.Exists(directoryPath)) {
          return false;
        }

        Directory.CreateDirectory(directoryPath);
        return true;
      }

      MarkdownNode parentNode = descriptor.Node;
      int newHeadingLevel = descriptor.Node.HeadingLevel + 1;

      if (descriptor.Kind == AreaKind.Document) {
        parentNode = descriptor.Document.Root;
        newHeadingLevel = 1;
      }

      if (newHeadingLevel > _MaximumMarkdownHeadingLevel) {
        return false;
      }

      string title = name.Trim();

      bool duplicate = parentNode.Children.Any((MarkdownNode child) =>
        string.Equals(child.Title, title, StringComparison.Ordinal));

      if (duplicate) {
        return false;
      }

      MarkdownNode newNode = MarkdownNode.CreateNewHeading(
        title,
        newHeadingLevel,
        descriptor.Document.NewLine
      );

      newNode.Parent = parentNode;
      parentNode.Children.Add(newNode);
      this.RebuildLogicalSegments(parentNode);
      descriptor.Document.MarkChanged();

      return true;
    }

    private bool TryAppendContentCore(string area, string content, MutationContext context) {
      if (content == null) {
        return false;
      }

      AreaDescriptor target = this.ResolveArea(area, context);

      if (target.ContentLevel == ContentLevel.BeyondContent) {
        return false;
      }

      ParsedIncomingContent incoming = this.ParseIncomingContent(content);

      if (target.ContentLevel == ContentLevel.ContentAggregation) {
        if (!string.IsNullOrWhiteSpace(incoming.Root.DirectContent)) {
          return false;
        }

        return this.MergeIntoAggregation(target, incoming.Root, context);
      }

      MarkdownNode targetNode = target.Node;
      int targetHeadingLevel = target.Node.HeadingLevel;

      if (target.Kind == AreaKind.Document) {
        targetNode = target.Document.Root;
        targetHeadingLevel = 0;
      }

      bool merged = this.MergeIntoContentContainer(
        target.Document,
        targetNode,
        targetHeadingLevel,
        incoming.Root
      );

      if (merged) {
        target.Document.MarkChanged();
      }

      return merged;
    }

    private bool TryTruncateCore(string area, MutationContext context) {
      AreaDescriptor descriptor = this.ResolveArea(area, context);

      if (descriptor.ContentLevel == ContentLevel.BeyondContent) {
        return false;
      }

      if (descriptor.Kind == AreaKind.Root || descriptor.Kind == AreaKind.Directory) {
        string[] directDirectories = Directory.GetDirectories(descriptor.PhysicalPath);

        foreach (string directory in directDirectories) {
          context.ForgetDocumentsBelow(directory);
          Directory.Delete(directory, true);
        }

        string[] markdownFiles = Directory.GetFiles(
          descriptor.PhysicalPath,
          "*" + _MarkdownExtension,
          SearchOption.TopDirectoryOnly
        );

        foreach (string markdownFile in markdownFiles) {
          context.ForgetDocument(markdownFile);
          File.Delete(markdownFile);
        }

        return true;
      }

      MarkdownNode targetNode = descriptor.Node;

      if (descriptor.Kind == AreaKind.Document) {
        targetNode = descriptor.Document.Root;
      }

      targetNode.DirectContent = string.Empty;
      targetNode.Children.Clear();
      descriptor.Document.MarkChanged();

      return true;
    }

    private bool TryMoveContentCore(
      string sourceArea,
      string targetArea,
      MutationContext context
    ) {
      string normalizedSource = this.NormalizeAreaPath(sourceArea);
      string normalizedTarget = this.NormalizeAreaPath(targetArea);

      if (string.Equals(normalizedSource, normalizedTarget, StringComparison.Ordinal)) {
        return false;
      }

      string sourcePrefix = normalizedSource;

      if (!sourcePrefix.EndsWith("/", StringComparison.Ordinal)) {
        sourcePrefix += "/";
      }

      if (normalizedTarget.StartsWith(sourcePrefix, StringComparison.Ordinal)) {
        return false;
      }

      AreaDescriptor source = this.ResolveArea(normalizedSource, context);
      AreaDescriptor target = this.ResolveArea(normalizedTarget, context);

      if (source.ContentLevel == ContentLevel.BeyondContent) {
        return false;
      }

      if (target.ContentLevel == ContentLevel.BeyondContent) {
        return false;
      }

      ParsedIncomingContent sourceContent = this.CreateIncomingContentFromArea(source);

      if (target.ContentLevel == ContentLevel.ContentAggregation &&
          !string.IsNullOrWhiteSpace(sourceContent.Root.DirectContent)) {
        return false;
      }

      bool merged;

      if (target.ContentLevel == ContentLevel.ContentAggregation) {
        merged = this.MergeIntoAggregation(target, sourceContent.Root, context);
      }
      else {
        MarkdownNode targetNode = target.Node;
        int targetHeadingLevel = target.Node.HeadingLevel;

        if (target.Kind == AreaKind.Document) {
          targetNode = target.Document.Root;
          targetHeadingLevel = 0;
        }

        merged = this.MergeIntoContentContainer(
          target.Document,
          targetNode,
          targetHeadingLevel,
          sourceContent.Root
        );

        if (merged) {
          target.Document.MarkChanged();
        }
      }

      if (!merged) {
        return false;
      }

      return this.TryTruncateCore(normalizedSource, context);
    }

    private bool MergeIntoAggregation(
      AreaDescriptor target,
      MarkdownNode incomingRoot,
      MutationContext context
    ) {
      if (!string.IsNullOrWhiteSpace(incomingRoot.DirectContent)) {
        return false;
      }

      foreach (MarkdownNode incomingChild in incomingRoot.Children) {
        bool documentChild = this.IsDocumentDisplayName(incomingChild.Title);

        if (documentChild) {
          string documentName = incomingChild.Title.Substring(1, incomingChild.Title.Length - 2).Trim();

          if (string.IsNullOrWhiteSpace(documentName)) {
            return false;
          }

          if (!this.IsValidPhysicalName(documentName)) {
            return false;
          }

          string documentPath = this.GetSafePhysicalChildPath(
            target.PhysicalPath,
            documentName,
            _MarkdownExtension
          );

          if (!File.Exists(documentPath)) {
            if (Directory.Exists(documentPath)) {
              return false;
            }

            File.WriteAllText(documentPath, string.Empty, new UTF8Encoding(false));
          }

          MarkdownDocumentModel document = this.LoadDocument(documentPath, context);

          MarkdownNode virtualIncomingRoot = new MarkdownNode();
          virtualIncomingRoot.DirectContent = incomingChild.DirectContent;

          foreach (MarkdownNode child in incomingChild.Children) {
            virtualIncomingRoot.Children.Add(child);
          }

          bool merged2 = this.MergeIntoContentContainer(
            document,
            document.Root,
            0,
            virtualIncomingRoot
          );

          if (!merged2) {
            return false;
          }

          document.MarkChanged();
          continue;
        }

        string directoryName = incomingChild.Title.Trim();

        if (string.IsNullOrWhiteSpace(directoryName)) {
          return false;
        }

        if (!this.IsValidPhysicalName(directoryName)) {
          return false;
        }

        string directoryPath = this.GetSafePhysicalChildPath(
          target.PhysicalPath,
          directoryName,
          string.Empty
        );

        if (!Directory.Exists(directoryPath)) {
          if (File.Exists(directoryPath)) {
            return false;
          }

          Directory.CreateDirectory(directoryPath);
        }

        if (!string.IsNullOrWhiteSpace(incomingChild.DirectContent)) {
          return false;
        }

        AreaDescriptor directoryDescriptor = AreaDescriptor.CreateDirectory(
          this.CombineAreaPath(target.AreaPath, this.EncodeAreaSegment(directoryName)),
          directoryName,
          directoryPath
        );

        bool merged = this.MergeIntoAggregation(directoryDescriptor, incomingChild, context);

        if (!merged) {
          return false;
        }
      }

      return true;
    }

    private bool MergeIntoContentContainer(
      MarkdownDocumentModel document,
      MarkdownNode targetNode,
      int targetHeadingLevel,
      MarkdownNode incomingRoot
    ) {
      if (!string.IsNullOrWhiteSpace(incomingRoot.DirectContent)) {
        targetNode.DirectContent = this.AppendDirectContent(
          targetNode.DirectContent,
          incomingRoot.DirectContent,
          document.NewLine
        );
      }

      foreach (MarkdownNode incomingChild in incomingRoot.Children) {
        MarkdownNode[] matchingChildren = targetNode.Children
          .Where((MarkdownNode child) =>
            string.Equals(child.Title, incomingChild.Title, StringComparison.Ordinal))
          .ToArray();

        if (matchingChildren.Length > 1) {
          return false;
        }

        if (matchingChildren.Length == 1) {
          MarkdownNode existingChild = matchingChildren[0];

          bool merged = this.MergeIntoContentContainer(
            document,
            existingChild,
            existingChild.HeadingLevel,
            incomingChild
          );

          if (!merged) {
            return false;
          }

          continue;
        }

        int childHeadingLevel = targetHeadingLevel + 1;

        if (childHeadingLevel > _MaximumMarkdownHeadingLevel) {
          return false;
        }

        MarkdownNode newChild = this.CloneIncomingAsDocumentNode(
          incomingChild,
          childHeadingLevel,
          document.NewLine
        );

        if (newChild == null) {
          return false;
        }

        newChild.Parent = targetNode;
        targetNode.Children.Add(newChild);
      }

      this.RebuildLogicalSegments(targetNode);
      return true;
    }

    private MarkdownNode CloneIncomingAsDocumentNode(
      MarkdownNode incoming,
      int headingLevel,
      string newLine
    ) {
      if (headingLevel > _MaximumMarkdownHeadingLevel) {
        return null;
      }

      MarkdownNode clone = MarkdownNode.CreateNewHeading(
        incoming.Title,
        headingLevel,
        newLine
      );

      clone.DirectContent = incoming.DirectContent;

      foreach (MarkdownNode incomingChild in incoming.Children) {
        MarkdownNode childClone = this.CloneIncomingAsDocumentNode(
          incomingChild,
          headingLevel + 1,
          newLine
        );

        if (childClone == null) {
          return null;
        }

        childClone.Parent = clone;
        clone.Children.Add(childClone);
      }

      this.RebuildLogicalSegments(clone);
      return clone;
    }

    private ParsedIncomingContent CreateIncomingContentFromArea(AreaDescriptor source) {
      MarkdownNode root = new MarkdownNode();

      if (source.Kind == AreaKind.Root || source.Kind == AreaKind.Directory) {
        this.PopulateIncomingFromAggregation(source, root);
        return new ParsedIncomingContent(root);
      }

      MarkdownNode sourceNode = source.Node;

      if (source.Kind == AreaKind.Document) {
        sourceNode = source.Document.Root;
      }

      root.DirectContent = sourceNode.DirectContent;

      foreach (MarkdownNode child in sourceNode.Children) {
        MarkdownNode cloned = this.CloneIncomingNode(child);
        cloned.Parent = root;
        root.Children.Add(cloned);
      }

      return new ParsedIncomingContent(root);
    }

    private void PopulateIncomingFromAggregation(
      AreaDescriptor aggregation,
      MarkdownNode root
    ) {
      AreaDescriptor[] children = this.GetDirectoryChildren(aggregation);

      foreach (AreaDescriptor child in children) {
        MarkdownNode incomingChild = new MarkdownNode();

        if (child.Kind == AreaKind.Document) {
          incomingChild.Title = "[" + child.DisplayName + "]";
          incomingChild.DirectContent = child.Document.Root.DirectContent;

          foreach (MarkdownNode documentChild in child.Document.Root.Children) {
            MarkdownNode clonedDocumentChild = this.CloneIncomingNode(documentChild);
            clonedDocumentChild.Parent = incomingChild;
            incomingChild.Children.Add(clonedDocumentChild);
          }
        }
        else {
          incomingChild.Title = child.DisplayName;
          this.PopulateIncomingFromAggregation(child, incomingChild);
        }

        incomingChild.Parent = root;
        root.Children.Add(incomingChild);
      }
    }

    private MarkdownNode CloneIncomingNode(MarkdownNode source) {
      MarkdownNode clone = new MarkdownNode();
      clone.Title = source.Title;
      clone.DirectContent = source.DirectContent;

      foreach (MarkdownNode sourceChild in source.Children) {
        MarkdownNode childClone = this.CloneIncomingNode(sourceChild);
        childClone.Parent = clone;
        clone.Children.Add(childClone);
      }

      return clone;
    }

    private string GetDirectContentCore(AreaDescriptor descriptor) {
      if (descriptor.ContentLevel != ContentLevel.ContentContainer) {
        return string.Empty;
      }

      if (descriptor.Kind == AreaKind.Document) {
        return this.NormalizeContentForRead(descriptor.Document.Root.DirectContent);
      }

      return this.NormalizeContentForRead(descriptor.Node.DirectContent);
    }

    private string RenderContentSubtree(MarkdownNode node, string newLine) {
      StringBuilder builder = new StringBuilder();

      if (!string.IsNullOrEmpty(node.DirectContent)) {
        builder.Append(this.NormalizeContentForRead(node.DirectContent));

        if (node.Children.Count > 0) {
          builder.Append(newLine);
          builder.Append(newLine);
        }
      }

      foreach (MarkdownNode child in node.Children) {
        this.RenderRebasedNode(child, builder, 1, newLine);
      }

      return builder.ToString();
    }

    private void RenderRebasedNode(
      MarkdownNode node,
      StringBuilder builder,
      int headingLevel,
      string newLine
    ) {
      int effectiveHeadingLevel = headingLevel;

      if (effectiveHeadingLevel > _MaximumMarkdownHeadingLevel) {
        effectiveHeadingLevel = _MaximumMarkdownHeadingLevel;
      }

      builder.Append(new string('#', effectiveHeadingLevel));
      builder.Append(' ');
      builder.Append(node.Title);
      builder.Append(newLine);

      string directContent = this.NormalizeContentForRead(node.DirectContent);

      if (!string.IsNullOrEmpty(directContent)) {
        builder.Append(newLine);
        builder.Append(directContent);
        builder.Append(newLine);
      }

      if (node.Children.Count > 0) {
        builder.Append(newLine);
      }

      foreach (MarkdownNode child in node.Children) {
        this.RenderRebasedNode(child, builder, headingLevel + 1, newLine);
      }
    }

    private void RenderAggregationChildren(
      AreaDescriptor aggregation,
      StringBuilder builder,
      int headingLevel,
      MutationContext context
    ) {
      AreaDescriptor[] children = this.GetDirectoryChildren(aggregation);

      foreach (AreaDescriptor child in children) {
        int effectiveHeadingLevel = headingLevel;

        if (effectiveHeadingLevel > _MaximumMarkdownHeadingLevel) {
          effectiveHeadingLevel = _MaximumMarkdownHeadingLevel;
        }

        builder.Append(new string('#', effectiveHeadingLevel));
        builder.Append(' ');

        if (child.Kind == AreaKind.Document) {
          builder.Append('[');
          builder.Append(child.DisplayName);
          builder.Append(']');
        }
        else {
          builder.Append(child.DisplayName);
        }

        builder.Append(Environment.NewLine);
        builder.Append(Environment.NewLine);

        if (child.Kind == AreaKind.Document) {
          string directContent = this.NormalizeContentForRead(
            child.Document.Root.DirectContent
          );

          if (!string.IsNullOrEmpty(directContent)) {
            builder.Append(directContent);
            builder.Append(Environment.NewLine);
            builder.Append(Environment.NewLine);
          }

          foreach (MarkdownNode documentChild in child.Document.Root.Children) {
            this.RenderRebasedNode(
              documentChild,
              builder,
              headingLevel + 1,
              child.Document.NewLine
            );
          }
        }
        else {
          this.RenderAggregationChildren(child, builder, headingLevel + 1, context);
        }
      }
    }

    private void CollectChildAreas(
      AreaDescriptor parent,
      bool recurse,
      List<string> result,
      MutationContext context
    ) {
      if (parent.Kind == AreaKind.Root || parent.Kind == AreaKind.Directory) {
        AreaDescriptor[] children = this.GetDirectoryChildren(parent);

        foreach (AreaDescriptor child in children) {
          result.Add(child.AreaPath);

          if (recurse) {
            this.CollectChildAreas(child, true, result, context);
          }
        }

        return;
      }

      MarkdownNode parentNode = parent.Node;

      if (parent.Kind == AreaKind.Document) {
        parentNode = parent.Document.Root;
      }

      foreach (MarkdownNode childNode in parentNode.Children) {
        string childAreaPath = this.CombineAreaPath(parent.AreaPath, childNode.LogicalSegment);
        result.Add(childAreaPath);

        if (recurse) {
          AreaDescriptor childDescriptor = AreaDescriptor.CreateHeading(
            childAreaPath,
            childNode.Title,
            parent.PhysicalPath,
            parent.Document,
            childNode
          );

          this.CollectChildAreas(childDescriptor, true, result, context);
        }
      }
    }

    private AreaDescriptor[] GetDirectoryChildren(AreaDescriptor directoryDescriptor) {
      List<AreaDescriptor> children = new List<AreaDescriptor>();

      string[] directories = Directory.GetDirectories(directoryDescriptor.PhysicalPath);

      foreach (string directory in directories) {
        DirectoryInfo directoryInfo = new DirectoryInfo(directory);

        if (this.IsProviderInternalDirectory(directoryInfo.Name)) {
          continue;
        }

        if (this.IsReparsePoint(directoryInfo.FullName)) {
          continue;
        }

        string areaPath = this.CombineAreaPath(
          directoryDescriptor.AreaPath,
          this.EncodeAreaSegment(directoryInfo.Name)
        );

        children.Add(
          AreaDescriptor.CreateDirectory(
            areaPath,
            directoryInfo.Name,
            directoryInfo.FullName
          )
        );
      }

      string[] markdownFiles = Directory.GetFiles(
        directoryDescriptor.PhysicalPath,
        "*" + _MarkdownExtension,
        SearchOption.TopDirectoryOnly
      );

      foreach (string markdownFile in markdownFiles) {
        if (this.IsReparsePoint(markdownFile)) {
          continue;
        }

        string documentName = Path.GetFileNameWithoutExtension(markdownFile);
        string areaPath = this.CombineAreaPath(
          directoryDescriptor.AreaPath,
          this.CreateDocumentSegment(documentName)
        );

        MarkdownDocumentModel document = this.LoadDocument(markdownFile, null);

        children.Add(
          AreaDescriptor.CreateDocument(
            areaPath,
            documentName,
            markdownFile,
            document
          )
        );
      }

      return children
        .OrderBy((AreaDescriptor descriptor) => descriptor.AreaPath, StringComparer.Ordinal)
        .ToArray();
    }

    private bool GetSupportsSubAreas(AreaDescriptor descriptor) {
      if (descriptor.Kind == AreaKind.Root || descriptor.Kind == AreaKind.Directory) {
        return true;
      }

      if (descriptor.Kind == AreaKind.Document) {
        return true;
      }

      return descriptor.Node.HeadingLevel < _MaximumMarkdownHeadingLevel;
    }

    private bool GetCanAddSubAreas(AreaDescriptor descriptor) {
      if (_ReadOnly) {
        return false;
      }

      return this.GetSupportsSubAreas(descriptor);
    }

    private MarkdownDocumentModel LoadDocument(
      string documentPath,
      MutationContext context
    ) {
      if (context != null) {
        return context.GetDocument(documentPath);
      }

      string content = File.ReadAllText(documentPath, Encoding.UTF8);
      return this.ParseMarkdownDocument(documentPath, content);
    }

    private MarkdownDocumentModel ParseMarkdownDocument(
      string documentPath,
      string content
    ) {
      string newLine = this.DetectNewLine(content);
      MarkdownDocumentModel document = new MarkdownDocumentModel(documentPath, newLine);
      MarkdownNode root = document.Root;

      MatchCollection lines = Regex.Matches(content, @".*?(?:\r\n|\n|\r|$)", RegexOptions.Singleline);
      Stack<MarkdownNode> stack = new Stack<MarkdownNode>();
      stack.Push(root);

      char fenceCharacter = '\0';
      int fenceLength = 0;

      foreach (Match lineMatch in lines) {
        string line = lineMatch.Value;

        if (line.Length == 0) {
          continue;
        }

        string lineWithoutEnding = line.TrimEnd('\r', '\n');
        string trimmedStart = lineWithoutEnding.TrimStart();

        if (this.TryUpdateFenceState(trimmedStart, ref fenceCharacter, ref fenceLength)) {
          stack.Peek().DirectContent += line;
          continue;
        }

        if (fenceCharacter != '\0') {
          stack.Peek().DirectContent += line;
          continue;
        }

        Match headingMatch = _AtxHeadingRegex.Match(line);

        if (!headingMatch.Success) {
          stack.Peek().DirectContent += line;
          continue;
        }

        string title = headingMatch.Groups[3].Value.Trim();

        if (string.IsNullOrWhiteSpace(title)) {
          stack.Peek().DirectContent += line;
          continue;
        }

        int headingLevel = headingMatch.Groups[2].Value.Length;

        while (stack.Count > 1 && stack.Peek().HeadingLevel >= headingLevel) {
          stack.Pop();
        }

        MarkdownNode parent = stack.Peek();
        MarkdownNode node = new MarkdownNode();
        node.Title = title;
        node.HeadingLevel = headingLevel;
        node.OriginalHeadingLine = line;
        node.HeadingLineModified = false;
        node.Parent = parent;

        parent.Children.Add(node);
        stack.Push(node);
      }

      this.RebuildLogicalSegments(root);
      return document;
    }

    private ParsedIncomingContent ParseIncomingContent(string content) {
      MarkdownDocumentModel parsed = this.ParseMarkdownDocument(string.Empty, content);
      MarkdownNode normalizedRoot = new MarkdownNode();
      normalizedRoot.DirectContent = parsed.Root.DirectContent;

      this.NormalizeIncomingHierarchy(parsed.Root, normalizedRoot);

      return new ParsedIncomingContent(normalizedRoot);
    }

    private void NormalizeIncomingHierarchy(
      MarkdownNode sourceRoot,
      MarkdownNode targetRoot
    ) {
      foreach (MarkdownNode sourceChild in sourceRoot.Children) {
        MarkdownNode cloned = this.CloneIncomingNode(sourceChild);
        cloned.Parent = targetRoot;
        targetRoot.Children.Add(cloned);
      }
    }

    private bool TryUpdateFenceState(
      string trimmedLine,
      ref char fenceCharacter,
      ref int fenceLength
    ) {
      if (string.IsNullOrEmpty(trimmedLine)) {
        return false;
      }

      char candidate = trimmedLine[0];

      if (candidate != '`' && candidate != '~') {
        return false;
      }

      int count = 0;

      while (count < trimmedLine.Length && trimmedLine[count] == candidate) {
        count++;
      }

      if (count < 3) {
        return false;
      }

      if (fenceCharacter == '\0') {
        fenceCharacter = candidate;
        fenceLength = count;
        return true;
      }

      if (fenceCharacter == candidate && count >= fenceLength) {
        fenceCharacter = '\0';
        fenceLength = 0;
        return true;
      }

      return false;
    }

    private void RebuildLogicalSegments(MarkdownNode parent) {
      Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

      foreach (MarkdownNode child in parent.Children) {
        string baseSegment = this.EncodeAreaSegment(child.Title);
        int occurrence = 1;

        if (counts.TryGetValue(baseSegment, out int existingCount)) {
          occurrence = existingCount + 1;
        }

        counts[baseSegment] = occurrence;

        if (occurrence == 1) {
          child.LogicalSegment = baseSegment;
        }
        else {
          child.LogicalSegment = baseSegment + "~" + occurrence.ToString();
        }

        this.RebuildLogicalSegments(child);
      }
    }

    private string AppendDirectContent(
      string existing,
      string incoming,
      string newLine
    ) {
      string incomingTrimmed = incoming.Trim('\r', '\n');

      if (string.IsNullOrWhiteSpace(incomingTrimmed)) {
        return existing;
      }

      if (string.IsNullOrEmpty(existing)) {
        return incomingTrimmed + newLine;
      }

      string existingWithoutTrailingBreaks = existing.TrimEnd('\r', '\n');

      return existingWithoutTrailingBreaks
        + newLine
        + newLine
        + incomingTrimmed
        + newLine;
    }

    private string NormalizeContentForRead(string content) {
      return content.Trim('\r', '\n');
    }

    private string RenderDocument(MarkdownDocumentModel document) {
      StringBuilder builder = new StringBuilder();
      builder.Append(document.Root.DirectContent);

      foreach (MarkdownNode child in document.Root.Children) {
        this.RenderPhysicalNode(child, builder, document.NewLine);
      }

      return builder.ToString();
    }

    private void RenderPhysicalNode(
      MarkdownNode node,
      StringBuilder builder,
      string newLine
    ) {
      if (node.HeadingLineModified || string.IsNullOrEmpty(node.OriginalHeadingLine)) {
        builder.Append(new string('#', node.HeadingLevel));
        builder.Append(' ');
        builder.Append(node.Title);
        builder.Append(newLine);
      }
      else {
        builder.Append(node.OriginalHeadingLine);
      }

      builder.Append(node.DirectContent);

      foreach (MarkdownNode child in node.Children) {
        this.RenderPhysicalNode(child, builder, newLine);
      }
    }

    private string CreateMutationSnapshot() {
      string snapshotRoot = Path.Combine(
        Path.GetTempPath(),
        ".knowledge-repository-transactions",
        Guid.NewGuid().ToString("N")
      );

      Directory.CreateDirectory(snapshotRoot);

      string snapshotDirectory = Path.Combine(snapshotRoot, "snapshot");
      Directory.CreateDirectory(snapshotDirectory);

      this.CopyDirectory(_RootDirectory, snapshotDirectory);

      return snapshotRoot;
    }

    private void RestoreMutationSnapshot(string snapshotRoot) {
      if (string.IsNullOrEmpty(snapshotRoot) || !Directory.Exists(snapshotRoot)) {
        return;
      }

      string snapshotDirectory = Path.Combine(snapshotRoot, "snapshot");

      if (!Directory.Exists(snapshotDirectory)) {
        return;
      }

      this.DeleteDirectoryContents(_RootDirectory);
      this.CopyDirectory(snapshotDirectory, _RootDirectory);
      this.DeleteMutationSnapshot(snapshotRoot);
    }

    private void TryRestoreMutationSnapshot(string snapshotRoot) {
      try {
        this.RestoreMutationSnapshot(snapshotRoot);
      }
      catch (IOException ex) {
        DevLogger.LogError(ex);
      }
      catch (UnauthorizedAccessException ex) {
        DevLogger.LogError(ex);
      }
    }

    private void DeleteMutationSnapshot(string snapshotRoot) {
      if (string.IsNullOrEmpty(snapshotRoot)) {
        return;
      }

      if (Directory.Exists(snapshotRoot)) {
        Directory.Delete(snapshotRoot, true);
      }
    }

    private void CopyDirectory(string sourceDirectory, string targetDirectory) {
      Directory.CreateDirectory(targetDirectory);

      string[] files = Directory.GetFiles(sourceDirectory);

      foreach (string file in files) {
        if (this.IsReparsePoint(file)) {
          continue;
        }

        string targetFile = Path.Combine(targetDirectory, Path.GetFileName(file));
        File.Copy(file, targetFile, true);
      }

      string[] directories = Directory.GetDirectories(sourceDirectory);

      foreach (string directory in directories) {
        string directoryName = Path.GetFileName(directory);

        if (this.IsProviderInternalDirectory(directoryName)) {
          continue;
        }

        if (this.IsReparsePoint(directory)) {
          continue;
        }

        string targetChild = Path.Combine(targetDirectory, directoryName);
        this.CopyDirectory(directory, targetChild);
      }
    }

    private void DeleteDirectoryContents(string directory) {
      string[] files = Directory.GetFiles(directory);

      foreach (string file in files) {
        File.Delete(file);
      }

      string[] directories = Directory.GetDirectories(directory);

      foreach (string childDirectory in directories) {
        Directory.Delete(childDirectory, true);
      }
    }

    private string NormalizeAreaPath(string area) {
      if (string.IsNullOrWhiteSpace(area)) {
        return _RootArea;
      }

      string normalized = area.Trim().Replace('\\', '/');

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

    private string CombineAreaPath(string parent, string segment) {
      if (parent == _RootArea) {
        return _RootArea + segment;
      }

      return parent + "/" + segment;
    }

    private string CreateDocumentSegment(string documentName) {
      return "[" + this.EncodeAreaSegment(documentName) + "]";
    }

    private bool IsDocumentSegment(string segment) {
      return segment.Length >= 2
        && segment.StartsWith("[", StringComparison.Ordinal)
        && segment.EndsWith("]", StringComparison.Ordinal);
    }

    private bool IsDocumentDisplayName(string name) {
      return this.IsDocumentSegment(name.Trim());
    }

    private string EncodeAreaSegment(string value) {
      return value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("/", "%2F", StringComparison.Ordinal)
        .Replace("\\", "%5C", StringComparison.Ordinal)
        .Replace("[", "%5B", StringComparison.Ordinal)
        .Replace("]", "%5D", StringComparison.Ordinal);
    }

    private string DecodeAreaSegment(string value) {
      return value
        .Replace("%5D", "]", StringComparison.OrdinalIgnoreCase)
        .Replace("%5B", "[", StringComparison.OrdinalIgnoreCase)
        .Replace("%5C", "\\", StringComparison.OrdinalIgnoreCase)
        .Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)
        .Replace("%25", "%", StringComparison.OrdinalIgnoreCase);
    }

    private string DetectNewLine(string content) {
      if (content.Contains("\r\n", StringComparison.Ordinal)) {
        return "\r\n";
      }

      if (content.Contains("\n", StringComparison.Ordinal)) {
        return "\n";
      }

      if (content.Contains("\r", StringComparison.Ordinal)) {
        return "\r";
      }

      return Environment.NewLine;
    }

    private bool IsProviderInternalDirectory(string directoryName) {
      return directoryName.StartsWith(".knowledge-repository-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether a provider-visible physical directory or Markdown base name
    /// is safe to use as exactly one physical child path segment.
    /// </summary>
    private bool IsValidPhysicalName(string name) {
      if (string.IsNullOrWhiteSpace(name)) {
        return false;
      }

      if (name == "." || name == "..") {
        return false;
      }

      if (name.IndexOf(Path.DirectorySeparatorChar) >= 0) {
        return false;
      }

      if (name.IndexOf(Path.AltDirectorySeparatorChar) >= 0) {
        return false;
      }

      char[] invalidCharacters = Path.GetInvalidFileNameChars();

      if (name.IndexOfAny(invalidCharacters) >= 0) {
        return false;
      }

      return true;
    }

    /// <summary>
    /// Builds one physical child path and verifies that the resulting path remains
    /// strictly below the supplied parent directory.
    /// </summary>
    private string GetSafePhysicalChildPath(
      string parentDirectory,
      string childName,
      string extension
    ) {
      if (!this.IsValidPhysicalName(childName)) {
        throw new InvalidOperationException(
          "The logical area name cannot be mapped to a safe physical path segment: " + childName
        );
      }

      string normalizedParent = Path.GetFullPath(parentDirectory)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

      string childPath = Path.GetFullPath(
        Path.Combine(normalizedParent, childName + extension)
      );

      string expectedPrefix = normalizedParent + Path.DirectorySeparatorChar;

      if (!childPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) {
        throw new InvalidOperationException(
          "The resolved physical path escapes the configured knowledge repository."
        );
      }

      return childPath;
    }

    /// <summary>
    /// Determines whether a path is a symbolic link or another file-system reparse point.
    /// Such entries are not traversed as knowledge areas because they could escape the
    /// configured knowledge root or create recursive directory graphs.
    /// </summary>
    private bool IsReparsePoint(string path) {
      FileAttributes attributes = File.GetAttributes(path);
      return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private void EnsureInitialized() {
      if (!_Initialized) {
        throw new InvalidOperationException("The knowledge repository has not been initialized.");
      }
    }

    /// <summary>
    /// Describes one resolved logical area.
    /// </summary>
    protected sealed class AreaDescriptor {

      private AreaKind _Kind;
      private string _AreaPath;
      private string _DisplayName;
      private string _PhysicalPath;
      private ContentLevel _ContentLevel;
      private MarkdownDocumentModel _Document;
      private MarkdownNode _Node;

      private AreaDescriptor() {
        _AreaPath = string.Empty;
        _DisplayName = string.Empty;
        _PhysicalPath = string.Empty;
      }

      /// <summary>
      /// Gets the provider-specific area kind.
      /// </summary>
      public AreaKind Kind {
        get {
          return _Kind;
        }
        private set {
          _Kind = value;
        }
      }

      /// <summary>
      /// Gets the absolute logical area path.
      /// </summary>
      public string AreaPath {
        get {
          return _AreaPath;
        }
        private set {
          _AreaPath = value;
        }
      }

      /// <summary>
      /// Gets the human-readable direct area name.
      /// </summary>
      public string DisplayName {
        get {
          return _DisplayName;
        }
        private set {
          _DisplayName = value;
        }
      }

      /// <summary>
      /// Gets the physical file-system path represented by the area.
      /// </summary>
      public string PhysicalPath {
        get {
          return _PhysicalPath;
        }
        private set {
          _PhysicalPath = value;
        }
      }

      /// <summary>
      /// Gets the effective content level.
      /// </summary>
      public ContentLevel ContentLevel {
        get {
          return _ContentLevel;
        }
        private set {
          _ContentLevel = value;
        }
      }

      /// <summary>
      /// Gets the loaded Markdown document for document and heading areas.
      /// </summary>
      public MarkdownDocumentModel Document {
        get {
          return _Document;
        }
        private set {
          _Document = value;
        }
      }

      /// <summary>
      /// Gets the concrete heading node for heading areas.
      /// </summary>
      public MarkdownNode Node {
        get {
          return _Node;
        }
        private set {
          _Node = value;
        }
      }

      /// <summary>
      /// Creates the logical root descriptor.
      /// </summary>
      public static AreaDescriptor CreateRoot(string physicalPath) {
        AreaDescriptor descriptor = new AreaDescriptor();
        descriptor.Kind = AreaKind.Root;
        descriptor.AreaPath = "/";
        descriptor.DisplayName = "/";
        descriptor.PhysicalPath = physicalPath;
        descriptor.ContentLevel = ContentLevel.ContentAggregation;
        return descriptor;
      }

      /// <summary>
      /// Creates a directory aggregation descriptor.
      /// </summary>
      public static AreaDescriptor CreateDirectory(
        string areaPath,
        string displayName,
        string physicalPath
      ) {
        AreaDescriptor descriptor = new AreaDescriptor();
        descriptor.Kind = AreaKind.Directory;
        descriptor.AreaPath = areaPath;
        descriptor.DisplayName = displayName;
        descriptor.PhysicalPath = physicalPath;
        descriptor.ContentLevel = ContentLevel.ContentAggregation;
        return descriptor;
      }

      /// <summary>
      /// Creates a Markdown document descriptor.
      /// </summary>
      public static AreaDescriptor CreateDocument(
        string areaPath,
        string displayName,
        string physicalPath,
        MarkdownDocumentModel document
      ) {
        AreaDescriptor descriptor = new AreaDescriptor();
        descriptor.Kind = AreaKind.Document;
        descriptor.AreaPath = areaPath;
        descriptor.DisplayName = displayName;
        descriptor.PhysicalPath = physicalPath;
        descriptor.ContentLevel = ContentLevel.ContentContainer;
        descriptor.Document = document;
        return descriptor;
      }

      /// <summary>
      /// Creates a Markdown heading descriptor.
      /// </summary>
      public static AreaDescriptor CreateHeading(
        string areaPath,
        string displayName,
        string physicalPath,
        MarkdownDocumentModel document,
        MarkdownNode node
      ) {
        AreaDescriptor descriptor = new AreaDescriptor();
        descriptor.Kind = AreaKind.Heading;
        descriptor.AreaPath = areaPath;
        descriptor.DisplayName = displayName;
        descriptor.PhysicalPath = physicalPath;
        descriptor.ContentLevel = ContentLevel.ContentContainer;
        descriptor.Document = document;
        descriptor.Node = node;
        return descriptor;
      }
    }

    /// <summary>
    /// Identifies the provider-specific physical representation of an area.
    /// </summary>
    protected enum AreaKind {
      Root = 0,
      Directory = 1,
      Document = 2,
      Heading = 3
    }

    /// <summary>
    /// Represents one parsed Markdown document.
    /// </summary>
    protected sealed class MarkdownDocumentModel {

      private readonly string _FilePath;
      private readonly string _NewLine;
      private readonly MarkdownNode _Root;
      private bool _Changed;

      /// <summary>
      /// Creates a parsed document model.
      /// </summary>
      public MarkdownDocumentModel(string filePath, string newLine) {
        _FilePath = filePath;
        _NewLine = newLine;
        _Root = new MarkdownNode();
        _Changed = false;
      }

      /// <summary>
      /// Gets the physical Markdown file path.
      /// </summary>
      public string FilePath {
        get {
          return _FilePath;
        }
      }

      /// <summary>
      /// Gets the preferred document line ending.
      /// </summary>
      public string NewLine {
        get {
          return _NewLine;
        }
      }

      /// <summary>
      /// Gets the virtual document root node.
      /// </summary>
      public MarkdownNode Root {
        get {
          return _Root;
        }
      }

      /// <summary>
      /// Gets whether the document has been mutated.
      /// </summary>
      public bool Changed {
        get {
          return _Changed;
        }
      }

      /// <summary>
      /// Marks the document as mutated.
      /// </summary>
      public void MarkChanged() {
        _Changed = true;
      }
    }

    /// <summary>
    /// Represents one logical Markdown content node.
    /// </summary>
    protected sealed class MarkdownNode {

      private string _Title;
      private string _LogicalSegment;
      private int _HeadingLevel;
      private string _DirectContent;
      private string _OriginalHeadingLine;
      private bool _HeadingLineModified;
      private MarkdownNode _Parent;
      private readonly List<MarkdownNode> _Children;

      /// <summary>
      /// Creates an empty Markdown node.
      /// </summary>
      public MarkdownNode() {
        _Title = string.Empty;
        _LogicalSegment = string.Empty;
        _HeadingLevel = 0;
        _DirectContent = string.Empty;
        _OriginalHeadingLine = string.Empty;
        _HeadingLineModified = false;
        _Children = new List<MarkdownNode>();
      }

      /// <summary>
      /// Gets or sets the display title.
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
      /// Gets or sets the canonical logical path segment.
      /// </summary>
      public string LogicalSegment {
        get {
          return _LogicalSegment;
        }
        set {
          _LogicalSegment = value;
        }
      }

      /// <summary>
      /// Gets or sets the physical Markdown heading level.
      /// </summary>
      public int HeadingLevel {
        get {
          return _HeadingLevel;
        }
        set {
          _HeadingLevel = value;
        }
      }

      /// <summary>
      /// Gets or sets direct textual content owned by this node.
      /// </summary>
      public string DirectContent {
        get {
          return _DirectContent;
        }
        set {
          _DirectContent = value;
        }
      }

      /// <summary>
      /// Gets or sets the original heading line for minimally invasive rendering.
      /// </summary>
      public string OriginalHeadingLine {
        get {
          return _OriginalHeadingLine;
        }
        set {
          _OriginalHeadingLine = value;
        }
      }

      /// <summary>
      /// Gets or sets whether the heading line must be regenerated.
      /// </summary>
      public bool HeadingLineModified {
        get {
          return _HeadingLineModified;
        }
        set {
          _HeadingLineModified = value;
        }
      }

      /// <summary>
      /// Gets or sets the logical parent node.
      /// </summary>
      public MarkdownNode Parent {
        get {
          return _Parent;
        }
        set {
          _Parent = value;
        }
      }

      /// <summary>
      /// Gets the ordered child-node collection.
      /// </summary>
      public List<MarkdownNode> Children {
        get {
          return _Children;
        }
      }

      /// <summary>
      /// Creates a new physical ATX heading node.
      /// </summary>
      public static MarkdownNode CreateNewHeading(
        string title,
        int headingLevel,
        string newLine
      ) {
        MarkdownNode node = new MarkdownNode();
        node.Title = title;
        node.HeadingLevel = headingLevel;
        node.OriginalHeadingLine = string.Empty;
        node.HeadingLineModified = true;
        node.DirectContent = string.Empty;
        return node;
      }
    }

    /// <summary>
    /// Encapsulates the parsed root of one incoming structured content payload.
    /// </summary>
    protected sealed class ParsedIncomingContent {

      private readonly MarkdownNode _Root;

      /// <summary>
      /// Creates the parsed incoming content wrapper.
      /// </summary>
      public ParsedIncomingContent(MarkdownNode root) {
        _Root = root;
      }

      /// <summary>
      /// Gets the virtual incoming root.
      /// </summary>
      public MarkdownNode Root {
        get {
          return _Root;
        }
      }
    }

    /// <summary>
    /// Caches parsed Markdown documents during one logical mutation so cross-document
    /// operations modify one coherent in-memory view before persistence.
    /// </summary>
    protected sealed class MutationContext {

      private readonly FileBasedKnowledgeRepository _Owner;
      private readonly Dictionary<string, MarkdownDocumentModel> _Documents;

      /// <summary>
      /// Creates a mutation context.
      /// </summary>
      public MutationContext(FileBasedKnowledgeRepository owner) {
        _Owner = owner;
        _Documents = new Dictionary<string, MarkdownDocumentModel>(StringComparer.OrdinalIgnoreCase);
      }

      /// <summary>
      /// Gets or parses one Markdown document.
      /// </summary>
      public MarkdownDocumentModel GetDocument(string filePath) {
        string fullPath = Path.GetFullPath(filePath);

        if (_Documents.TryGetValue(fullPath, out MarkdownDocumentModel document)) {
          return document;
        }

        string content = File.ReadAllText(fullPath, Encoding.UTF8);
        document = _Owner.ParseMarkdownDocument(fullPath, content);
        _Documents.Add(fullPath, document);
        return document;
      }

      /// <summary>
      /// Removes a document from the mutation cache.
      /// </summary>
      public void ForgetDocument(string filePath) {
        string fullPath = Path.GetFullPath(filePath);
        _Documents.Remove(fullPath);
      }

      /// <summary>
      /// Removes all cached documents located below a physical directory.
      /// </summary>
      public void ForgetDocumentsBelow(string directoryPath) {
        string normalizedDirectory = Path.GetFullPath(directoryPath)
          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
          + Path.DirectorySeparatorChar;

        string[] keys = _Documents.Keys
          .Where((string key) =>
            key.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
          .ToArray();

        foreach (string key in keys) {
          _Documents.Remove(key);
        }
      }

      /// <summary>
      /// Persists every changed Markdown document using an atomic file replacement.
      /// </summary>
      public void SaveChanges() {
        foreach (MarkdownDocumentModel document in _Documents.Values) {
          if (!document.Changed) {
            continue;
          }

          string content = _Owner.RenderDocument(document);
          _Owner.WriteAllTextAtomically(document.FilePath, content);
        }
      }
    }

    private void WriteAllTextAtomically(string filePath, string content) {
      string directory = Path.GetDirectoryName(filePath);

      if (string.IsNullOrEmpty(directory)) {
        throw new InvalidOperationException("Cannot determine the document directory.");
      }

      string temporaryFile = Path.Combine(
        directory,
        ".knowledge-repository-write-" + Guid.NewGuid().ToString("N") + ".tmp"
      );

      File.WriteAllText(temporaryFile, content, new UTF8Encoding(false));

      try {
        File.Move(temporaryFile, filePath, true);
      }
      finally {
        if (File.Exists(temporaryFile)) {
          File.Delete(temporaryFile);
        }
      }
    }
  }


}
