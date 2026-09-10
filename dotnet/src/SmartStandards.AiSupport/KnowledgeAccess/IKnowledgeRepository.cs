using System.ComponentModel;
using System.Diagnostics.Contracts;
#if NET_FX
using System.ServiceModel;
using System.ServiceModel.Web;
#endif

namespace AI.SmartStandards.KnowledgeAccess {

  /// <summary>
  /// Describes how a logical knowledge area participates in textual content access.
  /// </summary>
  public enum ContentLevel {

    /// <summary>
    /// The area is located outside the content-accessible hierarchy.
    /// It may provide structural navigation and sub-areas, but textual content
    /// retrieval and textual content mutation operations are not applicable to
    /// the area itself.
    /// </summary>
    BeyondContent = 0,

    /// <summary>
    /// The area represents an aggregation scope for subordinate content.
    /// 
    /// A content aggregation area participates in content traversal and aggregated
    /// content retrieval but does not own direct textual content itself.
    /// Consequently, <see cref="IKnowledgeRepository.HasDirectContent(string)"/>
    /// MUST return false for such an area and
    /// <see cref="IKnowledgeRepository.GetDirectContent(string)"/> MUST return an
    /// empty string.
    /// 
    /// A content aggregation area may represent a physical provider concept such as
    /// a notebook, book, documentation collection or another grouping construct.
    /// It may also be purely virtual and synthesized by the provider, for example to
    /// expose a read-only cross-cutting projection that aggregates similarly named
    /// sections, conclusions, examples, code snippets or other content collected
    /// from multiple unrelated source areas.
    /// 
    /// Whether mutation operations are available for a concrete aggregation area is
    /// determined by its reported capabilities. A provider may expose fully mutable,
    /// partially mutable or completely read-only aggregation areas.
    /// </summary>
    ContentAggregation = 1,

    /// <summary>
    /// The area represents a concrete content-bearing unit.
    /// It may own direct textual content and may additionally contain subordinate
    /// content areas.
    /// 
    /// Direct content retrieval and direct content mutation operations are meaningful
    /// for this level, subject to the capabilities reported for the concrete area.
    /// 
    /// In a Markdown-oriented provider this typically corresponds to a concrete
    /// document or to a nested section represented by a heading. The exact physical
    /// representation is provider-specific and MUST NOT be assumed by consumers.
    /// </summary>
    ContentContainer = 2
  }

  /// <summary>
  /// Provides ordered hierarchical access to a logical knowledge repository.
  /// 
  /// A knowledge repository exposes knowledge through absolute logical area paths.
  /// Areas form an ordered tree. The public contract deliberately abstracts from
  /// physical persistence concepts such as files, directories, Git repositories,
  /// Markdown documents, headings, notebooks, pages or database records.
  /// 
  /// Providers MAY internally use any such concepts. Providers MAY also expose
  /// virtual areas that have no direct physical representation, provided that their
  /// behavior follows this contract.
  /// 
  /// Area paths are absolute and begin with "/". The repository root itself is "/".
  /// Area paths are logical addresses and MUST NOT expose provider-specific physical
  /// storage paths.
  /// 
  /// Areas participate in textual content according to <see cref="ContentLevel"/>:
  /// <see cref="ContentLevel.BeyondContent"/> for pure navigation,
  /// <see cref="ContentLevel.ContentAggregation"/> for content-access scopes that do
  /// not own direct content, and <see cref="ContentLevel.ContentContainer"/> for
  /// concrete areas that may own direct textual content.
  /// 
  /// Area ordering is semantically significant. Providers MUST preserve the natural
  /// order of sibling areas. Recursive enumeration MUST use pre-order traversal:
  /// parents are returned before descendants and siblings remain in their natural
  /// provider-defined order.
  /// 
  /// Mutating operations are atomic from the consumer's perspective. An operation
  /// either completes fully or leaves the repository in its previous externally
  /// observable state.
  /// </summary>
  public interface IKnowledgeRepository {

    /// <summary>
    /// Returns logical area paths below the specified start area.
    /// 
    /// If <paramref name="recurse"/> is false, only direct children of
    /// <paramref name="startArea"/> are returned.
    /// 
    /// If <paramref name="recurse"/> is true, all descendants are returned using
    /// pre-order traversal. Each parent MUST appear before its descendants.
    /// Siblings MUST preserve their natural repository order.
    /// 
    /// Ordering is part of repository semantics. Providers MUST NOT arbitrarily
    /// reorder areas alphabetically, by creation time or by storage iteration order
    /// unless that order is explicitly defined as the provider's natural order for
    /// the affected structural level.
    /// 
    /// For Markdown-backed content structures, sibling order below the first
    /// content-bearing area MUST correspond to the order of the represented sections
    /// in the underlying content.
    /// 
    /// Virtual aggregation areas are returned in the same way as physical areas when
    /// they are part of the exposed logical tree. Their order MUST also be stable and
    /// deterministic.
    /// </summary>
    /// <param name="recurse">
    /// true to return the complete descendant tree; false to return direct children only.
    /// </param>
    /// <param name="startArea">
    /// The absolute logical area path at which enumeration starts. "/" represents
    /// the repository root.
    /// </param>
    /// <returns>
    /// The matching absolute logical area paths in stable natural hierarchical order.
    /// </returns>
    string[] GetAreas(bool recurse, string startArea = "/");

    /// <summary>
    /// Searches the logical repository for areas whose effective searchable representation
    /// matches the specified keyword.
    /// 
    /// The search starts at <paramref name="startArea"/> and includes all descendant areas
    /// below that point.
    /// 
    /// The exact searchable representation is provider-specific, but it SHOULD at minimum
    /// consider the logical area name and MAY additionally consider path segments, direct
    /// content, aggregated metadata, tags or other provider-defined searchable information.
    /// 
    /// The method returns logical area paths only. It does not return matching content
    /// fragments or provider-specific search metadata.
    /// 
    /// Returned areas MUST use the same canonical absolute area-path format as
    /// <see cref="GetAreas(bool, string)"/>.
    /// 
    /// Result ordering MUST be deterministic. Providers SHOULD preserve natural repository
    /// order whenever practical rather than applying arbitrary relevance-based sorting,
    /// unless the provider explicitly defines a stable search-ranking strategy.
    /// 
    /// The search MUST respect the logical visibility of the repository. Virtual areas,
    /// including virtual <see cref="ContentLevel.ContentAggregation"/> areas, MAY participate
    /// in keyword search when the provider defines them as searchable.
    /// </summary>
    /// <param name="keyword">
    /// The keyword or search expression used to identify matching logical areas.
    /// </param>
    /// <param name="startArea">
    /// The absolute logical area path that limits the search scope. "/" represents the
    /// complete repository.
    /// </param>
    /// <returns>
    /// The matching absolute logical area paths in deterministic order.
    /// </returns>
    string[] GetAreasByKeyword(string keyword, string startArea = "/");

    /// <summary>
    /// Returns the effective capabilities of the specified logical area.
    /// 
    /// Capabilities describe what the current provider permits for this concrete area.
    /// They MAY depend on provider type, source configuration, permissions, physical
    /// storage restrictions, logical depth, virtual-area semantics or other
    /// provider-specific constraints.
    /// 
    /// <paramref name="contentLevel"/> describes how the area participates in content:
    /// 
    /// - <see cref="ContentLevel.BeyondContent"/> means the area is purely structural.
    /// - <see cref="ContentLevel.ContentAggregation"/> means aggregated content can be
    ///   obtained through the area, but the area owns no direct textual content.
    /// - <see cref="ContentLevel.ContentContainer"/> means the area may own direct
    ///   textual content and may additionally contain subordinate content.
    /// 
    /// <paramref name="supportsSubAreas"/> indicates whether the area can structurally
    /// contain direct child areas. This is intentionally independent from
    /// <paramref name="canAddSubAreas"/>. A read-only or preconfigured area may expose
    /// existing children while creation of additional children is forbidden.
    /// 
    /// <paramref name="canBeRenamed"/> indicates whether the addressed area itself may
    /// be renamed while preserving its direct content, descendants and sibling position.
    /// 
    /// <paramref name="canBeDeleted"/> indicates whether the addressed area itself and
    /// its complete descendant tree may be removed.
    /// 
    /// <paramref name="canAddSubAreas"/> indicates whether new direct child areas may be
    /// created below the area.
    /// 
    /// <paramref name="canAppendContent"/> indicates whether
    /// <see cref="TryAppendContent(string, string)"/> is generally supported.
    /// 
    /// For a <see cref="ContentLevel.ContentAggregation"/> area, direct unstructured
    /// text cannot be appended because the area owns no direct textual content.
    /// Appending may nevertheless be valid when the supplied content contains
    /// sufficient subordinate structure so that all content can be routed into
    /// subordinate content areas.
    /// 
    /// <paramref name="canTruncate"/> indicates whether
    /// <see cref="TryTruncate(string)"/> may clear the content tree below the area while
    /// preserving the addressed area itself. This may be true for
    /// <see cref="ContentLevel.ContentAggregation"/> even though that area has no direct
    /// content, because truncation can remove its subordinate content structure.
    /// 
    /// Composite operations derive their effective permission from these capabilities.
    /// <see cref="TryReplace(string, string)"/> requires both truncate and append
    /// capability. <see cref="TryMoveContent(string, string)"/> requires a truncatable
    /// source and an append-capable target, in addition to structural validation.
    /// 
    /// A purely virtual aggregation area may legitimately report all mutation
    /// capabilities as false while still supporting aggregated reads.
    /// </summary>
    /// <param name="area">The absolute logical area path.</param>
    /// <param name="contentLevel">The area's effective content level.</param>
    /// <param name="supportsSubAreas">Whether the area structurally supports child areas.</param>
    /// <param name="canBeRenamed">Whether the addressed area itself may be renamed.</param>
    /// <param name="canBeDeleted">Whether the addressed area and descendants may be deleted.</param>
    /// <param name="canAddSubAreas">Whether new direct child areas may be created.</param>
    /// <param name="canAppendContent">Whether hierarchical content append is generally supported.</param>
    /// <param name="canTruncate">Whether the area's complete subordinate content tree may be cleared.</param>
    void GetAreaCapabilities(
      string area,
      out ContentLevel contentLevel,
      out bool supportsSubAreas,
      out bool canBeRenamed,
      out bool canBeDeleted,
      out bool canAddSubAreas,
      out bool canAppendContent,
      out bool canTruncate
    );

    /// <summary>
    /// Determines whether the specified area currently owns non-empty direct textual
    /// content.
    /// 
    /// Direct content is content logically owned by the addressed area itself and
    /// explicitly excludes all content belonging to descendant areas.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>, this method MUST always return
    /// false because aggregation areas never own direct textual content.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>, the result reflects whether
    /// direct textual content currently exists.
    /// 
    /// For <see cref="ContentLevel.BeyondContent"/>, direct textual content is not
    /// semantically applicable and the provider should handle the request consistently
    /// with its general invalid-operation policy.
    /// </summary>
    /// <param name="area">The absolute logical area path.</param>
    /// <returns>true if direct textual content exists; otherwise false.</returns>
    bool HasDirectContent(string area);

    /// <summary>
    /// Returns only the direct textual content logically owned by the specified area.
    /// Descendant content MUST NOT be included.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>, this method MUST return
    /// <see cref="string.Empty"/> because aggregation areas do not own direct content.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>, the provider returns the direct
    /// content owned by the area.
    /// 
    /// In a Markdown-oriented provider, if a content container is represented by a
    /// heading, direct content corresponds to the textual block after that heading and
    /// before the first subordinate heading. The heading used to identify the area is
    /// structural representation and is not itself direct content.
    /// </summary>
    /// <param name="area">The absolute logical area path.</param>
    /// <returns>The direct textual content of the area, or an empty string for a content aggregation area.</returns>
    string GetDirectContent(string area);

    /// <summary>
    /// Returns the complete textual representation accessible through the specified
    /// content-capable area, including subordinate content areas in natural hierarchical
    /// order.
    /// 
    /// This method is valid for both <see cref="ContentLevel.ContentAggregation"/> and
    /// <see cref="ContentLevel.ContentContainer"/>.
    /// 
    /// For a content aggregation area, the returned value is assembled entirely from
    /// subordinate content because the aggregation area itself owns no direct content.
    /// The aggregation may represent a physical parent such as a notebook or may be
    /// virtual and synthesized from content located in multiple unrelated physical or
    /// logical source areas.
    /// 
    /// For a content container, the result includes the area's own direct content plus
    /// all subordinate content.
    /// 
    /// Aggregation MUST preserve the logical order exposed by the provider. The provider
    /// is responsible for rendering subordinate structure in a valid textual form.
    /// 
    /// A virtual cross-cutting aggregation MAY intentionally project selected content
    /// from multiple source branches, for example all "Conclusion" sections or all code
    /// example sections. Such an aggregation MUST be deterministic, read-consistent and
    /// explicit through its area identity and reported capabilities.
    /// 
    /// This operation is read-only and MUST NOT mutate, normalize or rewrite the
    /// underlying source repository.
    /// </summary>
    /// <param name="area">The absolute logical content-capable area path.</param>
    /// <returns>The complete aggregated textual content exposed through the area.</returns>
    string GetAggregatedContent(string area);

    /// <summary>
    /// Atomically deletes the specified logical area together with its complete
    /// descendant tree.
    /// 
    /// This operation removes the addressed area itself and is therefore intentionally
    /// different from <see cref="TryTruncate(string)"/>, which preserves the addressed
    /// area and only clears its content scope.
    /// 
    /// Physical deletion semantics are provider-specific. A provider may delete a
    /// directory, document, page, virtual definition or another backing artifact.
    /// Consumers observe only the logical result.
    /// 
    /// Virtual or synthesized areas may legitimately be non-deletable.
    /// 
    /// Unaffected siblings MUST retain their content and relative order.
    /// </summary>
    /// <param name="area">The absolute logical area path to delete.</param>
    /// <returns>true if the complete deletion succeeded atomically; otherwise false.</returns>
    bool TryDelete(string area);

    /// <summary>
    /// Atomically renames the specified logical area while preserving its direct
    /// content, complete descendant tree and sibling position.
    /// 
    /// Only the addressed area's own logical name changes.
    /// Descendants remain descendants of the renamed area and preserve their order.
    /// 
    /// The provider MUST reject a rename that would create an ambiguous logical sibling
    /// address.
    /// 
    /// A provider may map this operation to a directory rename, document rename, heading
    /// rename, page rename or another provider-specific operation.
    /// 
    /// Virtual aggregation areas may legitimately report that rename is unsupported.
    /// </summary>
    /// <param name="area">The absolute logical area path to rename.</param>
    /// <param name="newName">The new direct logical name.</param>
    /// <returns>true if the rename succeeded atomically; otherwise false.</returns>
    bool TryRename(string area, string newName);

    /// <summary>
    /// Atomically creates one new direct child area below the specified parent area.
    /// 
    /// The new child is appended after existing direct children unless the provider's
    /// logical model explicitly defines another stable natural insertion rule.
    /// Existing siblings MUST NOT be reordered.
    /// 
    /// The new child's content level and physical representation are determined by the
    /// provider from the parent context, requested name and provider-specific rules.
    /// 
    /// The method creates only the requested direct child area. Complex subtrees and
    /// distributed additive changes should be performed through
    /// <see cref="TryAppendContent(string, string)"/>.
    /// 
    /// Read-only virtual aggregation areas commonly report that this operation is not
    /// supported.
    /// </summary>
    /// <param name="area">The absolute logical parent area path.</param>
    /// <param name="name">The logical name of the new direct child area.</param>
    /// <returns>true if the child area was created atomically; otherwise false.</returns>
    bool TryAddSubArea(string area, string name);

    /// <summary>
    /// Atomically performs a non-destructive sparse hierarchical merge of the supplied
    /// textual content into the specified content-capable target area.
    /// 
    /// This operation is intentionally stronger than physical end-of-file appending.
    /// The supplied content is interpreted as a relative logical content tree rooted at
    /// the target area.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>:
    /// 
    /// - Unstructured content appearing before the first structural child in the input
    ///   is appended to the target area's existing direct-content region.
    /// - Structured child blocks are merged recursively into matching direct children
    ///   or appended as newly created children when no match exists.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>:
    /// 
    /// - The incoming payload MUST NOT contain direct unstructured content for the
    ///   aggregation area itself because aggregation areas own no direct content.
    /// - The payload MAY contain subordinate structure.
    /// - That subordinate structure is merged recursively into existing descendants or
    ///   used to create new descendants when permitted.
    /// 
    /// For every incoming direct child at every recursion level:
    /// 
    /// 1. If a matching direct child already exists, the operation recursively merges
    ///    into that child.
    /// 2. If no matching direct child exists, the incoming child subtree is appended as
    ///    a new child after existing siblings.
    /// 3. Existing areas are never deleted, replaced, moved or reordered.
    /// 4. Existing sibling order is preserved exactly.
    /// 5. Newly created siblings preserve their incoming relative order.
    /// 
    /// Matching is always performed against direct children of the current merge target.
    /// It MUST NOT perform an implicit recursive global name search.
    /// 
    /// The incoming payload may therefore be sparse and may address multiple existing
    /// branches in one call. A single append can update several distributed descendant
    /// branches while also creating missing branches.
    /// 
    /// In providers that interpret structured text such as Markdown, structural levels
    /// in the payload MUST be interpreted relative to the target area's current logical
    /// depth. Physical levels may need rebasing. The logical parent-child relationships
    /// are authoritative.
    /// 
    /// Providers SHOULD avoid unnecessary rewrites of unaffected existing content.
    /// This is especially important for version-controlled providers where small logical
    /// mutations should ideally create small physical diffs.
    /// 
    /// If any part of the incoming payload is invalid, cannot be routed, violates naming
    /// constraints, exceeds provider-specific structural limits or cannot be persisted
    /// atomically, the complete operation MUST fail and leave the repository unchanged.
    /// 
    /// This method is intended as the primary efficient additive mutation primitive,
    /// especially for AI agents. A caller can address the nearest common ancestor of
    /// multiple intended changes and provide one sparse structured payload rather than
    /// performing many separate round trips.
    /// </summary>
    /// <param name="area">The absolute logical content-capable target area path.</param>
    /// <param name="content">The direct and/or structured textual content to merge.</param>
    /// <returns>true if the complete hierarchical append succeeded atomically; otherwise false.</returns>
    bool TryAppendContent(string area, string content);

    /// <summary>
    /// Atomically clears the complete content scope represented by the specified
    /// content-capable area while preserving the addressed area itself.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>, truncation removes:
    /// 
    /// - all direct textual content owned by the area, and
    /// - all descendant areas together with their content.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>, the area owns no direct content,
    /// so truncation removes its complete subordinate content structure while preserving
    /// the aggregation area itself.
    /// 
    /// The addressed area retains its own identity, name, parent relationship and sibling
    /// position.
    /// 
    /// Read-only or purely virtual aggregations may report truncation as unsupported.
    /// 
    /// The operation MUST be atomic. If any part of the represented content scope cannot
    /// be removed, no externally observable partial mutation may remain.
    /// </summary>
    /// <param name="area">The absolute logical content-capable area path.</param>
    /// <returns>true if truncation completed atomically; otherwise false.</returns>
    bool TryTruncate(string area);

    /// <summary>
    /// Atomically replaces the complete content scope represented by the specified area
    /// with the supplied new content.
    /// 
    /// The operation is semantically equivalent to a successful
    /// <see cref="TryTruncate(string)"/> followed by a successful
    /// <see cref="TryAppendContent(string, string)"/>, but MUST be implemented as one
    /// externally atomic mutation.
    /// 
    /// For <see cref="ContentLevel.ContentContainer"/>, the replacement payload may
    /// contain direct content and subordinate structure.
    /// 
    /// For <see cref="ContentLevel.ContentAggregation"/>, the replacement payload MUST
    /// not contain direct unstructured content for the aggregation area itself. It may
    /// contain subordinate structure that reconstructs the aggregation's content scope.
    /// 
    /// The addressed area itself is preserved.
    /// 
    /// Both truncate and append capabilities are required. The provider SHOULD validate
    /// the complete replacement payload before publishing any destructive changes.
    /// If any part cannot be completed, the previous repository state MUST remain
    /// externally unchanged.
    /// </summary>
    /// <param name="area">The absolute logical content-capable target area path.</param>
    /// <param name="newContent">The complete replacement content scope.</param>
    /// <returns>true if replacement completed atomically; otherwise false.</returns>
    bool TryReplace(string area, string newContent);

    /// <summary>
    /// Atomically moves the complete content scope of one existing content-capable area
    /// into another existing content-capable area.
    /// 
    /// Both <see cref="ContentLevel.ContentAggregation"/> and
    /// <see cref="ContentLevel.ContentContainer"/> may participate as source or target,
    /// provided that their capabilities allow the operation.
    /// 
    /// The source area itself is NOT moved, renamed or deleted.
    /// Instead:
    /// 
    /// 1. The complete source content scope is merged into the target using exactly the
    ///    same sparse hierarchical merge semantics as
    ///    <see cref="TryAppendContent(string, string)"/>.
    /// 2. After the target merge succeeds, the source is truncated using exactly the
    ///    semantics of <see cref="TryTruncate(string)"/>.
    /// 3. The complete operation is atomic. If either phase cannot be completed, neither
    ///    source nor target may remain changed.
    /// 
    /// When the source is a <see cref="ContentLevel.ContentAggregation"/>, it contributes
    /// only its subordinate content structure because it has no direct content.
    /// 
    /// When the target is a <see cref="ContentLevel.ContentAggregation"/>, the moved
    /// content must be representable entirely as subordinate structure. Direct
    /// unstructured content cannot be attached to the aggregation area itself.
    /// 
    /// Existing target content is preserved. Matching target branches receive additive
    /// recursive merges, missing branches are appended, and existing target siblings are
    /// not reordered.
    /// 
    /// The source and target MUST be different areas.
    /// The target MUST NOT be located inside the source area's descendant subtree,
    /// because moving a content scope into itself would be structurally recursive.
    /// 
    /// Moving content from a descendant into one of its ancestors is valid when all
    /// capability and provider-specific constraints are satisfied.
    /// 
    /// Providers may move content across physical storage boundaries, documents, pages
    /// or other provider-specific containers. Such physical details MUST remain hidden
    /// through this interface.
    /// 
    /// Purely virtual read-only aggregation areas will normally not support this method.
    /// </summary>
    /// <param name="sourceArea">
    /// The absolute logical content-capable source area. The source area itself remains
    /// present and empty after success.
    /// </param>
    /// <param name="targetArea">
    /// The absolute logical content-capable target area receiving the moved content
    /// through sparse hierarchical merge semantics.
    /// </param>
    /// <returns>true if the complete move succeeded atomically; otherwise false.</returns>
    bool TryMoveContent(string sourceArea, string targetArea);

  }

}