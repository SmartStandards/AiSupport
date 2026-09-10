using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;



namespace AI.SmartStandards.KnowledgeAccess {

  /// <summary>
  /// Aggregates an arbitrary number of <see cref="IKnowledgeRepository"/> implementations
  /// into one ordered logical knowledge repository.
  /// 
  /// Every repository is added at an absolute logical mount point. Repositories may be
  /// mounted side by side, below synthetic parent paths, or directly on top of already
  /// mounted repositories.
  /// 
  /// The aggregated repository exposes the union of all mounted area trees. When several
  /// repositories expose the same resulting global area path, that path is represented
  /// exactly once and behaves as an overlay for read operations.
  /// 
  /// Read operations combine all matching mounted providers in mount-registration order.
  /// Existing area ordering from every mounted repository is preserved as far as possible:
  /// the first appearance of a logical child fixes its position, while later overlays of
  /// the same child merge into that existing position.
  /// 
  /// Synthetic mount ancestors are created automatically. For example, mounting a
  /// repository at "/Company/Engineering" implicitly exposes "/Company" even when no
  /// provider directly owns that area.
  /// 
  /// Mutation operations deliberately use conservative routing. A mutation is forwarded
  /// only when exactly one mounted repository unambiguously owns the addressed target and
  /// reports the required capability. The implementation never guesses which provider
  /// should be modified when several repositories overlap the same logical area.
  /// 
  /// <see cref="TryMoveContent(string, string)"/> is supported only when source and target
  /// resolve uniquely to the same concrete repository instance. Cross-repository moves are
  /// intentionally rejected because the <see cref="IKnowledgeRepository"/> contract
  /// requires atomic mutations and arbitrary repository implementations cannot provide a
  /// shared distributed transaction.
  /// </summary>
  public class AggregatedKnowledgeRepository : IKnowledgeRepository {

    private const string _RootArea = "/";

    private readonly object _SyncRoot;
    private readonly List<MountedRepository> _Repositories;

    /// <summary>
    /// Creates an empty aggregated knowledge repository.
    /// </summary>
    public AggregatedKnowledgeRepository() {
      _SyncRoot = new object();
      _Repositories = new List<MountedRepository>();
    }

    /// <summary>
    /// Adds a knowledge repository at the specified logical mount point.
    /// 
    /// The mount point is an absolute logical area path. "/" mounts the repository
    /// directly into the aggregated root. A deeper path such as "/Projects/Foo" exposes
    /// the mounted repository below that path and creates missing ancestor areas
    /// virtually.
    /// 
    /// Multiple repositories may use the same mount point. Their visible area trees then
    /// overlap and are merged for read operations.
    /// 
    /// Registration order is significant. It contributes to deterministic area ordering
    /// and to deterministic read aggregation when several providers expose the same
    /// logical area.
    /// 
    /// Adding a repository does not transfer lifetime ownership. This class does not
    /// dispose mounted repositories.
    /// </summary>
    /// <param name="repository">The knowledge repository to mount.</param>
    /// <param name="mountPoint">
    /// The absolute logical mount point. "/" mounts directly into the aggregated root.
    /// </param>
    public void Add(IKnowledgeRepository repository, string mountPoint = "/") {
      if (repository == null) {
        throw new ArgumentNullException(nameof(repository));
      }

      string normalizedMountPoint = this.NormalizeAreaPath(mountPoint);

      lock (_SyncRoot) {
        MountedRepository mountedRepository = new MountedRepository(
          repository,
          normalizedMountPoint,
          _Repositories.Count
        );

        _Repositories.Add(mountedRepository);
      }
    }

    /// <summary>
    /// Returns the visible logical areas below the specified global start area.
    /// 
    /// The result is the ordered union of all mounted repositories plus automatically
    /// synthesized mount ancestors.
    /// 
    /// If several providers expose the same global area, that area appears only once.
    /// Its position is determined by the first registered provider or synthetic mount
    /// structure that exposes it. Later providers overlay content and capabilities onto
    /// the same logical path without moving it.
    /// 
    /// Recursive enumeration uses pre-order traversal.
    /// </summary>
    /// <param name="recurse">
    /// true to include all descendants recursively; false to return direct children only.
    /// </param>
    /// <param name="startArea">
    /// The absolute aggregated area path at which enumeration starts.
    /// </param>
    /// <returns>The ordered global logical area paths.</returns>
    public string[] GetAreas(bool recurse, string startArea = "/") {
      lock (_SyncRoot) {
        AggregatedTree tree = this.BuildTree();
        string normalizedStartArea = this.NormalizeAreaPath(startArea);

        AggregatedNode startNode = tree.Find(normalizedStartArea);

        if (startNode == null) {
          return Array.Empty<string>();
        }

        List<string> result = new List<string>();

        foreach (AggregatedNode child in startNode.Children) {
          result.Add(child.Path);

          if (recurse) {
            this.CollectDescendants(child, result);
          }
        }

        return result.ToArray();
      }
    }

    /// <summary>
    /// Searches the aggregated logical repository for areas matching the specified
    /// keyword.
    /// 
    /// Provider-native keyword searches are executed within every mounted repository and
    /// translated into global paths. Synthetic mount areas are additionally matched by
    /// their logical path and direct display name.
    /// 
    /// Duplicate global paths are collapsed. Final results follow the natural global
    /// area order rather than provider-specific search ranking.
    /// </summary>
    /// <param name="keyword">The keyword to search for.</param>
    /// <param name="startArea">The absolute global search scope.</param>
    /// <returns>Matching global area paths in deterministic repository order.</returns>
    public string[] GetAreasByKeyword(string keyword, string startArea = "/") {
      if (string.IsNullOrWhiteSpace(keyword)) {
        return Array.Empty<string>();
      }

      lock (_SyncRoot) {
        string normalizedStartArea = this.NormalizeAreaPath(startArea);
        AggregatedTree tree = this.BuildTree();
        AggregatedNode startNode = tree.Find(normalizedStartArea);

        if (startNode == null) {
          return Array.Empty<string>();
        }

        HashSet<string> matches = new HashSet<string>(StringComparer.Ordinal);

        string[] visibleAreas = this.GetAreas(true, normalizedStartArea);

        foreach (string visibleArea in visibleAreas) {
          AggregatedNode node = tree.Find(visibleArea);

          if (node == null) {
            continue;
          }

          if (visibleArea.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
            matches.Add(visibleArea);
            continue;
          }

          if (node.DisplayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
            matches.Add(visibleArea);
          }
        }

        foreach (MountedRepository mountedRepository in _Repositories) {
          string localSearchStart;

          if (!this.TryTranslateGlobalScopeToLocal(
                mountedRepository,
                normalizedStartArea,
                out localSearchStart
              )) {
            continue;
          }

          string[] localMatches = mountedRepository.Repository.GetAreasByKeyword(
            keyword,
            localSearchStart
          );

          foreach (string localMatch in localMatches) {
            string globalMatch = this.ToGlobalPath(
              mountedRepository,
              localMatch
            );

            if (this.IsSameOrDescendant(globalMatch, normalizedStartArea)) {
              matches.Add(globalMatch);
            }
          }
        }

        List<string> orderedResult = new List<string>();

        foreach (string visibleArea in visibleAreas) {
          if (matches.Contains(visibleArea)) {
            orderedResult.Add(visibleArea);
          }
        }

        if (normalizedStartArea != _RootArea &&
            matches.Contains(normalizedStartArea)) {
          orderedResult.Insert(0, normalizedStartArea);
        }

        return orderedResult.ToArray();
      }
    }

    /// <summary>
    /// Returns the effective capabilities of one global aggregated area.
    /// 
    /// Content level is combined for read semantics:
    /// 
    /// - If any concrete contributor is a <see cref="ContentLevel.ContentContainer"/>,
    ///   the global area is exposed as <see cref="ContentLevel.ContentContainer"/>.
    /// - Otherwise, if any contributor is a <see cref="ContentLevel.ContentAggregation"/>
    ///   or the global area is a synthetic mount node with content descendants, it is
    ///   exposed as <see cref="ContentLevel.ContentAggregation"/>.
    /// - Otherwise it is <see cref="ContentLevel.BeyondContent"/>.
    /// 
    /// Structural child support is the union of all contributors and synthetic children.
    /// 
    /// Mutation capabilities are intentionally stricter. A mutation capability is true
    /// only when exactly one concrete mounted repository unambiguously represents the
    /// global area and that repository reports the corresponding capability. Overlay
    /// areas owned by several repositories are therefore read-mergeable but not directly
    /// mutable through this aggregator.
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
        string normalizedArea = this.NormalizeAreaPath(area);
        AggregatedTree tree = this.BuildTree();
        AggregatedNode node = tree.Find(normalizedArea);

        if (node == null) {
          throw new InvalidOperationException(
            "The aggregated knowledge area does not exist: " + normalizedArea
          );
        }

        contentLevel = this.ResolveCombinedContentLevel(node);
        supportsSubAreas = node.Children.Count > 0;

        foreach (AreaContribution contribution in node.Contributions) {
          ContentLevel providerContentLevel;
          bool providerSupportsSubAreas;
          bool providerCanBeRenamed;
          bool providerCanBeDeleted;
          bool providerCanAddSubAreas;
          bool providerCanAppendContent;
          bool providerCanTruncate;

          contribution.MountedRepository.Repository.GetAreaCapabilities(
            contribution.LocalArea,
            out providerContentLevel,
            out providerSupportsSubAreas,
            out providerCanBeRenamed,
            out providerCanBeDeleted,
            out providerCanAddSubAreas,
            out providerCanAppendContent,
            out providerCanTruncate
          );

          if (providerSupportsSubAreas) {
            supportsSubAreas = true;
          }
        }

        canBeRenamed = false;
        canBeDeleted = false;
        canAddSubAreas = false;
        canAppendContent = false;
        canTruncate = false;

        if (node.Contributions.Count != 1) {
          return;
        }

        AreaContribution uniqueContribution = node.Contributions[0];

        ContentLevel uniqueContentLevel;
        bool uniqueSupportsSubAreas;
        bool uniqueCanBeRenamed;
        bool uniqueCanBeDeleted;
        bool uniqueCanAddSubAreas;
        bool uniqueCanAppendContent;
        bool uniqueCanTruncate;

        uniqueContribution.MountedRepository.Repository.GetAreaCapabilities(
          uniqueContribution.LocalArea,
          out uniqueContentLevel,
          out uniqueSupportsSubAreas,
          out uniqueCanBeRenamed,
          out uniqueCanBeDeleted,
          out uniqueCanAddSubAreas,
          out uniqueCanAppendContent,
          out uniqueCanTruncate
        );

        canBeRenamed = uniqueCanBeRenamed;
        canBeDeleted = uniqueCanBeDeleted;
        canAddSubAreas = uniqueCanAddSubAreas;
        canAppendContent = uniqueCanAppendContent;
        canTruncate = uniqueCanTruncate;
      }
    }

    /// <summary>
    /// Determines whether any concrete contributor to the global area owns non-empty
    /// direct textual content.
    /// 
    /// Direct content from several overlaid repositories is allowed for read purposes.
    /// The global result is true when at least one contributing provider reports direct
    /// content.
    /// </summary>
    public bool HasDirectContent(string area) {
      lock (_SyncRoot) {
        AggregatedNode node = this.RequireNode(area);

        foreach (AreaContribution contribution in node.Contributions) {
          if (contribution.MountedRepository.Repository.HasDirectContent(
                contribution.LocalArea
              )) {
            return true;
          }
        }

        return false;
      }
    }

    /// <summary>
    /// Returns direct textual content from all concrete contributors to the global area
    /// in mount-registration order.
    /// 
    /// Empty contributor content is omitted. When several repositories provide non-empty
    /// direct content at the same overlay path, their direct content blocks are separated
    /// by one empty line.
    /// 
    /// Synthetic areas and pure aggregation contributors add no direct content.
    /// </summary>
    public string GetDirectContent(string area) {
      lock (_SyncRoot) {
        AggregatedNode node = this.RequireNode(area);
        List<string> blocks = new List<string>();

        foreach (AreaContribution contribution in node.Contributions) {
          string content = contribution.MountedRepository.Repository.GetDirectContent(
            contribution.LocalArea
          );

          if (!string.IsNullOrWhiteSpace(content)) {
            blocks.Add(content.Trim('\r', '\n'));
          }
        }

        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
      }
    }

    /// <summary>
    /// Returns the complete aggregated textual view rooted at the specified global area.
    /// 
    /// The method renders the global overlay tree rather than blindly concatenating whole
    /// provider documents. This prevents duplicate structural branches when several
    /// repositories overlap the same logical area.
    /// 
    /// For every global node:
    /// 
    /// - direct content from all concrete content contributors is concatenated in
    ///   registration order;
    /// - visible child areas are rendered once in global natural order;
    /// - synthetic mount areas participate like content aggregations;
    /// - a content aggregation contributor that exposes no child areas but still returns
    ///   provider-native aggregated content is treated as an opaque aggregate leaf and
    ///   its aggregated content is included.
    /// 
    /// The returned representation uses Markdown-style headings as the neutral textual
    /// projection of the aggregated area tree.
    /// </summary>
    public string GetAggregatedContent(string area) {
      lock (_SyncRoot) {
        string normalizedArea = this.NormalizeAreaPath(area);
        AggregatedTree tree = this.BuildTree();
        AggregatedNode node = tree.Find(normalizedArea);

        if (node == null) {
          return string.Empty;
        }

        ContentLevel contentLevel = this.ResolveCombinedContentLevel(node);

        if (contentLevel == ContentLevel.BeyondContent) {
          return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        this.RenderAggregatedNodeContent(node, builder, 1);

        return builder.ToString().TrimEnd('\r', '\n');
      }
    }

    /// <summary>
    /// Deletes a global area only when exactly one mounted repository owns that concrete
    /// path and reports deletion capability.
    /// 
    /// Synthetic mount nodes and overlay paths contributed by multiple repositories are
    /// not deleted because there is no unambiguous single mutation target.
    /// </summary>
    public bool TryDelete(string area) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.Delete,
              out contribution
            )) {
          return false;
        }

        return contribution.MountedRepository.Repository.TryDelete(
          contribution.LocalArea
        );
      }
    }

    /// <summary>
    /// Renames a global area only when it resolves to exactly one concrete mounted
    /// repository and that provider reports rename capability.
    /// 
    /// The physical effect remains entirely provider-specific. The underlying repository
    /// may rename a heading, document, directory, notebook page or another provider
    /// artifact.
    /// 
    /// Synthetic and multiply overlaid areas are not renameable through this aggregator.
    /// </summary>
    public bool TryRename(string area, string newName) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.Rename,
              out contribution
            )) {
          return false;
        }

        return contribution.MountedRepository.Repository.TryRename(
          contribution.LocalArea,
          newName
        );
      }
    }

    /// <summary>
    /// Adds a direct sub-area only when the global parent resolves to exactly one concrete
    /// repository and that provider allows child creation.
    /// 
    /// The aggregator does not infer which mounted repository should receive a new child
    /// when several providers overlap the same parent.
    /// </summary>
    public bool TryAddSubArea(string area, string name) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.AddSubArea,
              out contribution
            )) {
          return false;
        }

        return contribution.MountedRepository.Repository.TryAddSubArea(
          contribution.LocalArea,
          name
        );
      }
    }

    /// <summary>
    /// Performs sparse hierarchical append only when the global target resolves to one
    /// unambiguous concrete repository and that provider allows append.
    /// 
    /// The aggregator deliberately does not split one sparse append payload across
    /// several mounted repositories because doing so would violate the atomic mutation
    /// guarantee of <see cref="IKnowledgeRepository"/>.
    /// </summary>
    public bool TryAppendContent(string area, string content) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.AppendContent,
              out contribution
            )) {
          return false;
        }

        return contribution.MountedRepository.Repository.TryAppendContent(
          contribution.LocalArea,
          content
        );
      }
    }

    /// <summary>
    /// Truncates a global area only when it resolves uniquely to one concrete mounted
    /// repository and that provider reports truncate capability.
    /// </summary>
    public bool TryTruncate(string area) {
      lock (_SyncRoot) {
        AreaContribution contribution;

        if (!this.TryGetUniqueContributionForCapability(
              area,
              CapabilityKind.Truncate,
              out contribution
            )) {
          return false;
        }

        return contribution.MountedRepository.Repository.TryTruncate(
          contribution.LocalArea
        );
      }
    }

    /// <summary>
    /// Replaces a global content scope only when exactly one provider owns the global
    /// target and that provider supports both truncate and append semantics.
    /// 
    /// The replacement is delegated as one operation to the underlying repository so its
    /// provider-specific atomicity guarantee remains intact.
    /// </summary>
    public bool TryReplace(string area, string newContent) {
      lock (_SyncRoot) {
        string normalizedArea = this.NormalizeAreaPath(area);
        AggregatedNode node = this.RequireNode(normalizedArea);

        if (node.Contributions.Count != 1) {
          return false;
        }

        AreaContribution contribution = node.Contributions[0];

        ContentLevel contentLevel;
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;

        contribution.MountedRepository.Repository.GetAreaCapabilities(
          contribution.LocalArea,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate
        );

        if (!canAppendContent || !canTruncate) {
          return false;
        }

        return contribution.MountedRepository.Repository.TryReplace(
          contribution.LocalArea,
          newContent
        );
      }
    }

    /// <summary>
    /// Moves content only when source and target each resolve uniquely to the same mounted
    /// repository instance.
    /// 
    /// Cross-repository movement is intentionally rejected. Although it could be emulated
    /// through read/append/truncate calls, arbitrary <see cref="IKnowledgeRepository"/>
    /// implementations do not share a distributed transaction and therefore such an
    /// emulation could not satisfy the atomicity contract.
    /// </summary>
    public bool TryMoveContent(string sourceArea, string targetArea) {
      lock (_SyncRoot) {
        AggregatedNode sourceNode = this.RequireNode(sourceArea);
        AggregatedNode targetNode = this.RequireNode(targetArea);

        if (sourceNode.Contributions.Count != 1 ||
            targetNode.Contributions.Count != 1) {
          return false;
        }

        AreaContribution sourceContribution = sourceNode.Contributions[0];
        AreaContribution targetContribution = targetNode.Contributions[0];

        if (!object.ReferenceEquals(
              sourceContribution.MountedRepository.Repository,
              targetContribution.MountedRepository.Repository
            )) {
          return false;
        }

        if (!object.ReferenceEquals(
              sourceContribution.MountedRepository,
              targetContribution.MountedRepository
            )) {
          return false;
        }

        ContentLevel sourceContentLevel;
        bool sourceSupportsSubAreas;
        bool sourceCanBeRenamed;
        bool sourceCanBeDeleted;
        bool sourceCanAddSubAreas;
        bool sourceCanAppendContent;
        bool sourceCanTruncate;

        sourceContribution.MountedRepository.Repository.GetAreaCapabilities(
          sourceContribution.LocalArea,
          out sourceContentLevel,
          out sourceSupportsSubAreas,
          out sourceCanBeRenamed,
          out sourceCanBeDeleted,
          out sourceCanAddSubAreas,
          out sourceCanAppendContent,
          out sourceCanTruncate
        );

        ContentLevel targetContentLevel;
        bool targetSupportsSubAreas;
        bool targetCanBeRenamed;
        bool targetCanBeDeleted;
        bool targetCanAddSubAreas;
        bool targetCanAppendContent;
        bool targetCanTruncate;

        targetContribution.MountedRepository.Repository.GetAreaCapabilities(
          targetContribution.LocalArea,
          out targetContentLevel,
          out targetSupportsSubAreas,
          out targetCanBeRenamed,
          out targetCanBeDeleted,
          out targetCanAddSubAreas,
          out targetCanAppendContent,
          out targetCanTruncate
        );

        if (sourceContentLevel == ContentLevel.BeyondContent) {
          return false;
        }

        if (targetContentLevel == ContentLevel.BeyondContent) {
          return false;
        }

        if (!sourceCanTruncate || !targetCanAppendContent) {
          return false;
        }

        return sourceContribution.MountedRepository.Repository.TryMoveContent(
          sourceContribution.LocalArea,
          targetContribution.LocalArea
        );
      }
    }

    /// <summary>
    /// Builds the complete current global overlay tree from all mounted repositories.
    /// 
    /// No long-lived cache is used intentionally. Mounted repositories may represent
    /// remote or virtual knowledge that changes independently, so every public read sees
    /// a fresh logical projection of the current provider states.
    /// </summary>
    private AggregatedTree BuildTree() {
      AggregatedTree tree = new AggregatedTree();

      foreach (MountedRepository mountedRepository in _Repositories) {
        this.EnsureMountPath(tree, mountedRepository);

        AggregatedNode mountNode = tree.GetOrCreate(
          mountedRepository.MountPoint,
          this.GetLastAreaSegment(mountedRepository.MountPoint)
        );

        mountNode.AddContribution(
          new AreaContribution(
            mountedRepository,
            _RootArea
          )
        );

        string[] localAreas = mountedRepository.Repository.GetAreas(
          true,
          _RootArea
        );

        foreach (string localArea in localAreas) {
          string globalArea = this.ToGlobalPath(
            mountedRepository,
            localArea
          );

          string displayName = this.GetLastAreaSegment(globalArea);

          AggregatedNode globalNode = tree.GetOrCreate(
            globalArea,
            displayName
          );

          globalNode.AddContribution(
            new AreaContribution(
              mountedRepository,
              localArea
            )
          );
        }
      }

      return tree;
    }

    /// <summary>
    /// Creates synthetic mount ancestors so a deep mount point is navigable even when no
    /// concrete repository owns the intermediate areas.
    /// </summary>
    private void EnsureMountPath(
      AggregatedTree tree,
      MountedRepository mountedRepository
    ) {
      if (mountedRepository.MountPoint == _RootArea) {
        return;
      }

      string[] segments = mountedRepository.MountPoint
        .Split('/', StringSplitOptions.RemoveEmptyEntries);

      string currentPath = _RootArea;

      foreach (string segment in segments) {
        currentPath = this.CombineAreaPath(currentPath, segment);

        tree.GetOrCreate(
          currentPath,
          segment
        );
      }
    }

    /// <summary>
    /// Recursively collects descendants using the already ordered global child list.
    /// </summary>
    private void CollectDescendants(
      AggregatedNode node,
      List<string> result
    ) {
      foreach (AggregatedNode child in node.Children) {
        result.Add(child.Path);
        this.CollectDescendants(child, result);
      }
    }

    /// <summary>
    /// Resolves the effective read-oriented content level of a merged global area.
    /// </summary>
    private ContentLevel ResolveCombinedContentLevel(AggregatedNode node) {
      bool hasAggregation = false;

      foreach (AreaContribution contribution in node.Contributions) {
        ContentLevel contentLevel;
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;

        contribution.MountedRepository.Repository.GetAreaCapabilities(
          contribution.LocalArea,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate
        );

        if (contentLevel == ContentLevel.ContentContainer) {
          return ContentLevel.ContentContainer;
        }

        if (contentLevel == ContentLevel.ContentAggregation) {
          hasAggregation = true;
        }
      }

      if (hasAggregation) {
        return ContentLevel.ContentAggregation;
      }

      if (node.Children.Count > 0 && this.HasContentDescendant(node)) {
        return ContentLevel.ContentAggregation;
      }

      return ContentLevel.BeyondContent;
    }

    /// <summary>
    /// Determines whether a synthetic or structural area has any content-capable
    /// descendant.
    /// </summary>
    private bool HasContentDescendant(AggregatedNode node) {
      foreach (AggregatedNode child in node.Children) {
        foreach (AreaContribution contribution in child.Contributions) {
          ContentLevel contentLevel;
          bool supportsSubAreas;
          bool canBeRenamed;
          bool canBeDeleted;
          bool canAddSubAreas;
          bool canAppendContent;
          bool canTruncate;

          contribution.MountedRepository.Repository.GetAreaCapabilities(
            contribution.LocalArea,
            out contentLevel,
            out supportsSubAreas,
            out canBeRenamed,
            out canBeDeleted,
            out canAddSubAreas,
            out canAppendContent,
            out canTruncate
          );

          if (contentLevel != ContentLevel.BeyondContent) {
            return true;
          }
        }

        if (this.HasContentDescendant(child)) {
          return true;
        }
      }

      return false;
    }

    /// <summary>
    /// Renders the global overlay tree as a Markdown-like neutral aggregate projection.
    /// </summary>
    private void RenderAggregatedNodeContent(
      AggregatedNode node,
      StringBuilder builder,
      int childHeadingLevel
    ) {
      string directContent = this.GetDirectContent(node.Path);

      if (!string.IsNullOrWhiteSpace(directContent)) {
        builder.Append(directContent.Trim('\r', '\n'));
        builder.Append(Environment.NewLine);
        builder.Append(Environment.NewLine);
      }

      this.RenderOpaqueAggregationLeafContent(node, builder);

      foreach (AggregatedNode child in node.Children) {
        ContentLevel childContentLevel = this.ResolveCombinedContentLevel(child);

        if (childContentLevel == ContentLevel.BeyondContent &&
            !this.HasContentDescendant(child)) {
          continue;
        }

        int effectiveHeadingLevel = childHeadingLevel;

        if (effectiveHeadingLevel > 6) {
          effectiveHeadingLevel = 6;
        }

        builder.Append(new string('#', effectiveHeadingLevel));
        builder.Append(' ');
        builder.Append(child.DisplayName);
        builder.Append(Environment.NewLine);
        builder.Append(Environment.NewLine);

        this.RenderAggregatedNodeContent(
          child,
          builder,
          childHeadingLevel + 1
        );

        if (builder.Length > 0 &&
            !builder.ToString().EndsWith(
              Environment.NewLine + Environment.NewLine,
              StringComparison.Ordinal
            )) {
          builder.Append(Environment.NewLine);
        }
      }
    }

    /// <summary>
    /// Preserves provider-native aggregate content for leaf aggregation areas whose
    /// content cannot be reconstructed from visible child areas.
    /// 
    /// This is particularly important for virtual cross-cutting aggregation providers.
    /// </summary>
    private void RenderOpaqueAggregationLeafContent(
      AggregatedNode node,
      StringBuilder builder
    ) {
      foreach (AreaContribution contribution in node.Contributions) {
        ContentLevel contentLevel;
        bool supportsSubAreas;
        bool canBeRenamed;
        bool canBeDeleted;
        bool canAddSubAreas;
        bool canAppendContent;
        bool canTruncate;

        contribution.MountedRepository.Repository.GetAreaCapabilities(
          contribution.LocalArea,
          out contentLevel,
          out supportsSubAreas,
          out canBeRenamed,
          out canBeDeleted,
          out canAddSubAreas,
          out canAppendContent,
          out canTruncate
        );

        if (contentLevel != ContentLevel.ContentAggregation) {
          continue;
        }

        string[] providerChildren = contribution.MountedRepository.Repository.GetAreas(
          false,
          contribution.LocalArea
        );

        if (providerChildren.Length > 0) {
          continue;
        }

        string aggregatedContent =
          contribution.MountedRepository.Repository.GetAggregatedContent(
            contribution.LocalArea
          );

        if (string.IsNullOrWhiteSpace(aggregatedContent)) {
          continue;
        }

        builder.Append(aggregatedContent.Trim('\r', '\n'));
        builder.Append(Environment.NewLine);
        builder.Append(Environment.NewLine);
      }
    }

    /// <summary>
    /// Resolves one mutation target and verifies that exactly one concrete provider owns
    /// the global area and supports the requested capability.
    /// </summary>
    private bool TryGetUniqueContributionForCapability(
      string area,
      CapabilityKind capabilityKind,
      out AreaContribution contribution
    ) {
      contribution = null;

      AggregatedNode node = this.RequireNode(area);

      if (node.Contributions.Count != 1) {
        return false;
      }

      AreaContribution candidate = node.Contributions[0];

      ContentLevel contentLevel;
      bool supportsSubAreas;
      bool canBeRenamed;
      bool canBeDeleted;
      bool canAddSubAreas;
      bool canAppendContent;
      bool canTruncate;

      candidate.MountedRepository.Repository.GetAreaCapabilities(
        candidate.LocalArea,
        out contentLevel,
        out supportsSubAreas,
        out canBeRenamed,
        out canBeDeleted,
        out canAddSubAreas,
        out canAppendContent,
        out canTruncate
      );

      bool capabilityAvailable = false;

      if (capabilityKind == CapabilityKind.Rename) {
        capabilityAvailable = canBeRenamed;
      }
      else if (capabilityKind == CapabilityKind.Delete) {
        capabilityAvailable = canBeDeleted;
      }
      else if (capabilityKind == CapabilityKind.AddSubArea) {
        capabilityAvailable = canAddSubAreas;
      }
      else if (capabilityKind == CapabilityKind.AppendContent) {
        capabilityAvailable = canAppendContent;
      }
      else if (capabilityKind == CapabilityKind.Truncate) {
        capabilityAvailable = canTruncate;
      }

      if (!capabilityAvailable) {
        return false;
      }

      contribution = candidate;
      return true;
    }

    /// <summary>
    /// Returns a required global node or throws when the area does not exist.
    /// </summary>
    private AggregatedNode RequireNode(string area) {
      string normalizedArea = this.NormalizeAreaPath(area);
      AggregatedTree tree = this.BuildTree();
      AggregatedNode node = tree.Find(normalizedArea);

      if (node == null) {
        throw new InvalidOperationException(
          "The aggregated knowledge area does not exist: " + normalizedArea
        );
      }

      return node;
    }

    /// <summary>
    /// Translates a local mounted-repository area path into its global aggregated path.
    /// </summary>
    private string ToGlobalPath(
      MountedRepository mountedRepository,
      string localArea
    ) {
      string normalizedLocalArea = this.NormalizeAreaPath(localArea);

      if (normalizedLocalArea == _RootArea) {
        return mountedRepository.MountPoint;
      }

      if (mountedRepository.MountPoint == _RootArea) {
        return normalizedLocalArea;
      }

      return mountedRepository.MountPoint + normalizedLocalArea;
    }

    /// <summary>
    /// Determines whether a requested global search scope intersects a mounted repository
    /// and maps that scope to the corresponding local provider path.
    /// </summary>
    private bool TryTranslateGlobalScopeToLocal(
      MountedRepository mountedRepository,
      string globalStartArea,
      out string localStartArea
    ) {
      localStartArea = _RootArea;

      if (globalStartArea == _RootArea) {
        return true;
      }

      if (this.IsSameOrDescendant(
            mountedRepository.MountPoint,
            globalStartArea
          )) {
        localStartArea = _RootArea;
        return true;
      }

      if (!this.IsSameOrDescendant(
            globalStartArea,
            mountedRepository.MountPoint
          )) {
        return false;
      }

      if (mountedRepository.MountPoint == _RootArea) {
        localStartArea = globalStartArea;
        return true;
      }

      string suffix = globalStartArea.Substring(
        mountedRepository.MountPoint.Length
      );

      if (string.IsNullOrEmpty(suffix)) {
        localStartArea = _RootArea;
      }
      else {
        localStartArea = this.NormalizeAreaPath(suffix);
      }

      return true;
    }

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is equal to or located below
    /// <paramref name="ancestor"/>.
    /// </summary>
    private bool IsSameOrDescendant(
      string candidate,
      string ancestor
    ) {
      string normalizedCandidate = this.NormalizeAreaPath(candidate);
      string normalizedAncestor = this.NormalizeAreaPath(ancestor);

      if (normalizedAncestor == _RootArea) {
        return true;
      }

      if (string.Equals(
            normalizedCandidate,
            normalizedAncestor,
            StringComparison.Ordinal
          )) {
        return true;
      }

      return normalizedCandidate.StartsWith(
        normalizedAncestor + "/",
        StringComparison.Ordinal
      );
    }

    /// <summary>
    /// Normalizes one absolute logical aggregated area path.
    /// </summary>
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

      if (normalized.Length > 1 &&
          normalized.EndsWith("/", StringComparison.Ordinal)) {
        normalized = normalized.TrimEnd('/');
      }

      return normalized;
    }

    /// <summary>
    /// Combines one logical parent path with one already encoded logical child segment.
    /// </summary>
    private string CombineAreaPath(
      string parent,
      string childSegment
    ) {
      if (parent == _RootArea) {
        return _RootArea + childSegment;
      }

      return parent + "/" + childSegment;
    }

    /// <summary>
    /// Returns the final logical path segment for display in the aggregated projection.
    /// </summary>
    private string GetLastAreaSegment(string area) {
      string normalizedArea = this.NormalizeAreaPath(area);

      if (normalizedArea == _RootArea) {
        return _RootArea;
      }

      int separatorIndex = normalizedArea.LastIndexOf('/');

      if (separatorIndex < 0 ||
          separatorIndex >= normalizedArea.Length - 1) {
        return normalizedArea;
      }

      return normalizedArea.Substring(separatorIndex + 1);
    }

    /// <summary>
    /// Identifies a mutation capability used by unique-provider routing.
    /// </summary>
    private enum CapabilityKind {
      Rename = 0,
      Delete = 1,
      AddSubArea = 2,
      AppendContent = 3,
      Truncate = 4
    }

    /// <summary>
    /// Represents one mounted repository registration.
    /// </summary>
    private sealed class MountedRepository {

      private readonly IKnowledgeRepository _Repository;
      private readonly string _MountPoint;
      private readonly int _RegistrationOrder;

      /// <summary>
      /// Creates one repository mount registration.
      /// </summary>
      public MountedRepository(
        IKnowledgeRepository repository,
        string mountPoint,
        int registrationOrder
      ) {
        _Repository = repository;
        _MountPoint = mountPoint;
        _RegistrationOrder = registrationOrder;
      }

      /// <summary>
      /// Gets the mounted repository.
      /// </summary>
      public IKnowledgeRepository Repository {
        get {
          return _Repository;
        }
      }

      /// <summary>
      /// Gets the absolute global mount point.
      /// </summary>
      public string MountPoint {
        get {
          return _MountPoint;
        }
      }

      /// <summary>
      /// Gets the stable registration order.
      /// </summary>
      public int RegistrationOrder {
        get {
          return _RegistrationOrder;
        }
      }
    }

    /// <summary>
    /// Represents one provider's contribution to one global overlay area.
    /// </summary>
    private sealed class AreaContribution {

      private readonly MountedRepository _MountedRepository;
      private readonly string _LocalArea;

      /// <summary>
      /// Creates one concrete area contribution.
      /// </summary>
      public AreaContribution(
        MountedRepository mountedRepository,
        string localArea
      ) {
        _MountedRepository = mountedRepository;
        _LocalArea = localArea;
      }

      /// <summary>
      /// Gets the contributing mounted repository.
      /// </summary>
      public MountedRepository MountedRepository {
        get {
          return _MountedRepository;
        }
      }

      /// <summary>
      /// Gets the local area path within the mounted repository.
      /// </summary>
      public string LocalArea {
        get {
          return _LocalArea;
        }
      }
    }

    /// <summary>
    /// Represents one global overlay-tree node.
    /// </summary>
    private sealed class AggregatedNode {

      private readonly string _Path;
      private readonly string _DisplayName;
      private AggregatedNode _Parent;
      private readonly List<AggregatedNode> _Children;
      private readonly List<AreaContribution> _Contributions;

      /// <summary>
      /// Creates one global tree node.
      /// </summary>
      public AggregatedNode(
        string path,
        string displayName
      ) {
        _Path = path;
        _DisplayName = displayName;
        _Children = new List<AggregatedNode>();
        _Contributions = new List<AreaContribution>();
      }

      /// <summary>
      /// Gets the absolute global area path.
      /// </summary>
      public string Path {
        get {
          return _Path;
        }
      }

      /// <summary>
      /// Gets the direct display name.
      /// </summary>
      public string DisplayName {
        get {
          return _DisplayName;
        }
      }

      /// <summary>
      /// Gets or sets the global parent node.
      /// </summary>
      public AggregatedNode Parent {
        get {
          return _Parent;
        }
        set {
          _Parent = value;
        }
      }

      /// <summary>
      /// Gets the ordered global child nodes.
      /// </summary>
      public List<AggregatedNode> Children {
        get {
          return _Children;
        }
      }

      /// <summary>
      /// Gets all concrete mounted-provider contributions to this global area.
      /// </summary>
      public List<AreaContribution> Contributions {
        get {
          return _Contributions;
        }
      }

      /// <summary>
      /// Adds one provider contribution when the same mount registration and local area
      /// have not already been registered.
      /// </summary>
      public void AddContribution(AreaContribution contribution) {
        bool exists = _Contributions.Any((AreaContribution candidate) =>
          object.ReferenceEquals(
            candidate.MountedRepository,
            contribution.MountedRepository
          ) &&
          string.Equals(
            candidate.LocalArea,
            contribution.LocalArea,
            StringComparison.Ordinal
          ));

        if (!exists) {
          _Contributions.Add(contribution);
        }
      }
    }

    /// <summary>
    /// Represents the complete current global overlay tree.
    /// </summary>
    private sealed class AggregatedTree {

      private readonly Dictionary<string, AggregatedNode> _Nodes;
      private readonly AggregatedNode _Root;

      /// <summary>
      /// Creates an empty aggregated tree containing only the global root.
      /// </summary>
      public AggregatedTree() {
        _Nodes = new Dictionary<string, AggregatedNode>(StringComparer.Ordinal);
        _Root = new AggregatedNode("/", "/");
        _Nodes.Add("/", _Root);
      }

      /// <summary>
      /// Finds a global node by canonical absolute path.
      /// </summary>
      public AggregatedNode Find(string path) {
        if (_Nodes.TryGetValue(path, out AggregatedNode node)) {
          return node;
        }

        return null;
      }

      /// <summary>
      /// Gets an existing node or creates it together with any required parent relation.
      /// 
      /// Child ordering follows first appearance. Once a global child exists, later
      /// provider overlays do not change its position.
      /// </summary>
      public AggregatedNode GetOrCreate(
        string path,
        string displayName
      ) {
        if (_Nodes.TryGetValue(path, out AggregatedNode existing)) {
          return existing;
        }

        string parentPath = this.GetParentPath(path);
        AggregatedNode parent = this.GetOrCreate(
          parentPath,
          this.GetDisplayName(parentPath)
        );

        AggregatedNode node = new AggregatedNode(
          path,
          displayName
        );

        node.Parent = parent;
        parent.Children.Add(node);
        _Nodes.Add(path, node);

        return node;
      }

      /// <summary>
      /// Gets the parent path of one absolute logical path.
      /// </summary>
      private string GetParentPath(string path) {
        if (path == "/") {
          return "/";
        }

        int separatorIndex = path.LastIndexOf('/');

        if (separatorIndex <= 0) {
          return "/";
        }

        return path.Substring(0, separatorIndex);
      }

      /// <summary>
      /// Gets the display segment of one absolute logical path.
      /// </summary>
      private string GetDisplayName(string path) {
        if (path == "/") {
          return "/";
        }

        int separatorIndex = path.LastIndexOf('/');

        if (separatorIndex < 0 ||
            separatorIndex >= path.Length - 1) {
          return path;
        }

        return path.Substring(separatorIndex + 1);
      }
    }
  }


}
