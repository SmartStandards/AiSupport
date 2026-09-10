# AI Skill: Implementing `IKnowledgeRepository`

## Abstract and Motivation

`IKnowledgeRepository` defines a provider-neutral abstraction for ordered, hierarchical knowledge stores.

Its purpose is to expose knowledge through one stable logical model while deliberately hiding the physical technology used to store, organize, derive, or synthesize that knowledge.

A provider may internally use:

- Git repositories,
- directory structures,
- Markdown documents,
- local files,
- database entities,
- cloud notebooks,
- pages,
- remote APIs,
- generated projections,
- or purely virtual aggregation nodes.

None of those physical concepts are part of the public contract.

The central abstraction is the **logical area**.

Areas form an ordered tree and are addressed by absolute logical paths. Every area describes how it participates in textual content through `ContentLevel`.

The contract intentionally does not model physical "documents" as a universal concept. Instead, it distinguishes three semantic content levels:

1. `BeyondContent`
2. `ContentAggregation`
3. `ContentContainer`

This distinction permits implementations that range from simple Markdown files to sophisticated virtual knowledge views.

A particularly important feature is that `ContentAggregation` nodes may be **virtual**. A provider can expose a read-only logical area that synthesizes content from multiple otherwise unrelated places.

For example, a provider could expose:

```text
/Views/All Examples
/Views/All Conclusions
/Views/All Code Snippets
```

even when those areas do not physically exist.

`/Views/All Code Snippets` could aggregate selected sections from dozens of source documents into one deterministic read-only content view.

This is not required for basic providers, but the contract intentionally permits it.

The design is also optimized for AI-agent access.

Its primary mutation primitive, `TryAppendContent`, is a **non-destructive Sparse Hierarchical Merge**. A caller can provide one sparse structured payload that targets multiple already-existing branches and creates missing branches in one atomic operation.

This reduces:

- network round trips,
- agent tool calls,
- token usage,
- redundant reads,
- intermediate repository states,
- and accidental destructive replacements.

---

# 1. Core Mental Model

A knowledge repository is an ordered tree:

```text
/
├── ProjectA
│   ├── Architecture
│   │   ├── Documentation
│   │   │   ├── [Hosting]
│   │   │   │   ├── ASP.NET Core
│   │   │   │   └── In-Memory
│   │   │   └── [Security]
│   │   └── ...
│   └── ...
└── ProjectB
```

The exposed path:

```text
/ProjectA/Architecture/Documentation/[Hosting]/ASP.NET Core
```

is a logical address.

The consumer does not need to know whether this maps to:

- a Markdown heading,
- a file,
- a notebook page,
- a database row,
- a computed result,
- or a virtual projection.

---

# 2. Content Levels

## 2.1 `BeyondContent`

A `BeyondContent` area is purely structural.

It may participate in navigation and may have child areas.

It does not participate in textual content access.

Typical examples:

- repository/source roots,
- category nodes,
- structural folders above the content model,
- provider-defined organizational nodes.

Typical semantics:

```text
HasDirectContent       not applicable
GetDirectContent       not applicable
GetAggregatedContent   not applicable
TryAppendContent       not applicable
TryTruncate            not applicable
TryReplace             not applicable
TryMoveContent         not applicable
```

Whether structural operations such as rename, delete or child creation are available depends on capabilities.

---

## 2.2 `ContentAggregation`

A `ContentAggregation` area is already part of the content-accessible hierarchy but **does not own direct textual content**.

This is a strict semantic invariant:

```text
HasDirectContent == false
GetDirectContent == string.Empty
```

A content aggregation acts as a scope over subordinate content.

Typical physical examples:

- notebook,
- book,
- documentation collection,
- section group,
- content catalog.

Typical virtual examples:

- all conclusions,
- all examples,
- all code snippets,
- all exercises,
- all sections tagged with a certain semantic role.

A virtual aggregation may have no physical storage artifact at all.

It may synthesize its children and aggregated content dynamically from multiple unrelated source branches.

Example:

```text
/Views
└── All Code Snippets                 ContentAggregation
    ├── ProjectA / Hosting / Example
    ├── ProjectB / Parser / Example
    └── ProjectC / Serialization / Example
```

The implementation may expose these children as projected logical areas or may expose only aggregated textual output, depending on provider design.

The important contract rule is:

> A `ContentAggregation` area provides content scope, but never direct content ownership.

`GetAggregatedContent` is therefore meaningful.

`TryAppendContent` may also be meaningful, but only when the incoming payload contains subordinate structure that can be routed into child areas.

This is invalid:

```text
TryAppendContent(
    "/Notebook",
    "Free text"
)
```

because the aggregation itself cannot own that text.

This may be valid:

```markdown
# Chapter A

Additional content.

# Chapter B

Additional content.
```

because the payload contains explicit subordinate structure.

Mutation support is capability-driven.

A provider may expose:

- mutable aggregations,
- partially mutable aggregations,
- fully read-only aggregations,
- purely virtual read-only aggregations.

---

## 2.3 `ContentContainer`

A `ContentContainer` is a concrete content-bearing area.

It may own direct textual content.

It may also contain subordinate areas.

Typical mappings include:

- Markdown document,
- Markdown heading section,
- OneNote page,
- textual database entity,
- provider-specific document unit.

For this level:

```text
HasDirectContent
GetDirectContent
GetAggregatedContent
TryAppendContent
TryTruncate
TryReplace
TryMoveContent
```

are all semantically meaningful, subject to capabilities.

---

# 3. Area Paths

All area paths are absolute.

Root:

```text
/
```

Example:

```text
/ProjectA/Architecture/[Hosting]/ASP.NET Core
```

Paths are logical addresses.

A provider MUST NOT expose physical filesystem paths or remote provider IDs as the public area path.

A provider must use one canonical path format.

Direct siblings must have unambiguous logical path segments.

---

# 4. Ordered Tree Semantics

Area ordering is semantically significant.

The repository is not an unordered graph.

## 4.1 Direct enumeration

```csharp
GetAreas(false, area)
```

returns direct children only.

## 4.2 Recursive enumeration

```csharp
GetAreas(true, area)
```

uses pre-order traversal.

For:

```text
A
├── B
│   ├── C
│   └── D
├── E
└── F
```

the order is:

```text
A/B
A/B/C
A/B/D
A/E
A/F
```

## 4.3 Natural sibling order

Sibling order must be stable and meaningful.

For Markdown-backed content it must correspond to document section order.

Existing sibling order must never be changed incidentally by append, rename or unrelated mutations.

Virtual aggregation providers must also define stable deterministic ordering.

---

# 5. Direct Content

Every content-capable logical node can be modeled as:

```text
Area
├── DirectContent
└── Children[]
```

However:

```text
ContentAggregation.DirectContent
```

is always empty.

Only `ContentContainer` may own direct content.

Example Markdown:

```markdown
# Hosting

General hosting text.

## ASP.NET Core

ASP.NET-specific text.

## In-Memory

In-memory text.
```

Logical model:

```text
Hosting
├── DirectContent = "General hosting text."
├── ASP.NET Core
│   └── DirectContent = "ASP.NET-specific text."
└── In-Memory
    └── DirectContent = "In-memory text."
```

For `Hosting`, direct content excludes child sections.

---

# 6. Aggregated Content

`GetAggregatedContent(area)` returns the complete textual content exposed through the area.

For a `ContentContainer`, it includes:

```text
own direct content
+
subordinate content
```

For a `ContentAggregation`, it includes:

```text
subordinate content only
```

because the aggregation itself owns no direct content.

The provider is responsible for rendering a deterministic valid textual representation.

---

# 7. Virtual Aggregation

Virtual aggregation is an explicitly supported provider pattern.

A provider may expose logical content areas that do not map one-to-one to physical storage artifacts.

Example:

```text
/Virtual
├── All Conclusions
├── All Code Samples
└── All Exercises
```

`All Conclusions` may dynamically search source areas for content sections semantically or structurally identified as conclusions.

The resulting content can be exposed as one read-only aggregation.

Possible use cases:

- collect all examples from many documents,
- collect all conclusions,
- collect all exercises,
- collect all warnings,
- collect all code blocks,
- create topic-specific synthesized views,
- expose cross-project reference collections.

Virtual aggregation nodes SHOULD normally be read-only unless the provider has a clear deterministic write-routing model.

A provider must not pretend that a virtual aggregation is writable when it cannot map mutations back to unambiguous source locations.

A virtual aggregation MUST provide:

- deterministic addressing,
- deterministic ordering,
- deterministic content projection,
- stable capability reporting,
- read consistency appropriate to the provider.

It MAY expose child areas that themselves point to projected source content.

---

# 8. Capability Semantics

Capabilities apply to a concrete area.

They may depend on:

- provider type,
- provider configuration,
- permissions,
- physical source state,
- virtual-area definition,
- logical depth,
- provider-specific constraints.

## `supportsSubAreas`

Means:

> The area can structurally contain child areas.

This does not imply that children may be created.

## `canAddSubAreas`

Means:

> The caller may create a new direct child.

## `canAppendContent`

Means:

> The area generally supports sparse hierarchical append.

For `ContentAggregation`, the incoming payload must not contain direct content at the aggregation level.

## `canTruncate`

Means:

> The represented content scope may be cleared while the addressed area remains.

For an aggregation this can remove all subordinate content even though direct content is empty.

Read-only virtual aggregations commonly report:

```text
canBeRenamed    = false
canBeDeleted    = false
canAddSubAreas  = false
canAppendContent = false
canTruncate     = false
```

while still supporting:

```text
GetAreas
GetAggregatedContent
```

---

# 9. Sparse Hierarchical Merge

`TryAppendContent` is the central additive mutation operation.

It is not a byte append.

It is a recursive hierarchical merge.

Conceptually:

```text
Merge(Target, Incoming):

    if Target is ContentContainer:
        Target.DirectContent += Incoming.DirectContent

    if Target is ContentAggregation:
        Incoming.DirectContent MUST be empty

    for each IncomingChild:

        if matching direct TargetChild exists:
            Merge(TargetChild, IncomingChild)

        else:
            append IncomingChild subtree
            after existing Target children
```

This is a **Sparse Hierarchical Merge**.

The input may contain only the branches that need modification.

It does not need to repeat unchanged parts of the repository.

---

# 10. Matching Rules

Matching occurs only among direct children of the current merge target.

Never perform recursive global name search.

Existing:

```text
A
├── B
│   └── X
└── C
    └── X
```

Incoming:

```text
X
```

at target `A` does not address either existing descendant.

It refers to direct child:

```text
A/X
```

To reach the existing nodes, the incoming structure must explicitly contain:

```text
B
└── X
```

or:

```text
C
└── X
```

This guarantees deterministic routing.

---

# 11. Free Text Append

For a `ContentContainer`, incoming free text before the first subordinate structure belongs to the target's direct content.

Existing:

```markdown
# A

Old text.

## B

B content.
```

Append:

```text
Additional A text.
```

Result:

```markdown
# A

Old text.

Additional A text.

## B

B content.
```

The physical text is inserted into the logical direct-content region.

It is not appended blindly to the physical end of a file.

For `ContentAggregation`, this payload is invalid because the aggregation cannot own direct text.

---

# 12. Mixed Free Text and Structured Append

Existing:

```text
A
├── DirectContent = "Old"
├── B
└── C
```

Incoming:

```text
DirectContent = "New"

D
└── E
```

Result:

```text
A
├── DirectContent = "Old" + "New"
├── B
├── C
└── D
    └── E
```

When projected into a linear textual format, the newly appended direct text may appear before existing child sections while newly created child sections appear after them.

This is correct.

Logical ownership is more important than preserving physical contiguity of the incoming payload.

---

# 13. Distributed Sparse Merge

Existing:

```text
Architecture
├── Hosting
│   ├── ASP.NET Core
│   └── In-Memory
├── Authentication
│   └── JWT
└── Logging
```

Incoming:

```text
Hosting
├── ASP.NET Core
│   └── + content
└── Containers

Authentication
└── OAuth

Deployment
```

Result:

```text
Architecture
├── Hosting
│   ├── ASP.NET Core
│   │   └── + appended content
│   ├── In-Memory
│   └── Containers
├── Authentication
│   ├── JWT
│   └── OAuth
├── Logging
└── Deployment
```

One call can update multiple distributed branches atomically.

---

# 14. AI-Agent Optimization

AI clients should prefer one append rooted at the nearest common ancestor of multiple intended additive mutations.

Instead of:

```text
Append /A/B/C
Append /A/B/D
Append /A/E/F
```

prefer:

```text
Append /A
```

with sparse structure:

```text
B
├── C
└── D

E
└── F
```

Benefits:

- fewer tool calls,
- fewer tokens,
- fewer round trips,
- fewer race conditions,
- fewer intermediate states,
- more coherent provider-level transactions,
- potentially one coherent version-control commit.

---

# 15. Relative Structure

Structured textual payloads are interpreted relative to the target area.

A Markdown provider must not treat incoming heading numbers as absolute document coordinates.

Incoming:

```markdown
# Authentication

## JWT
```

at a deeply nested target means:

```text
Target
└── Authentication
    └── JWT
```

The provider rebases the physical heading levels as required.

Logical hierarchy is authoritative.

---

# 16. Structural Normalization

A provider may normalize irregular incoming structural levels.

Example:

```markdown
# A
### B
##### C
```

may be interpreted logically as:

```text
A
└── B
    └── C
```

The provider may render canonical physical levels.

It must not alter logical parent-child relationships.

If the target physical format cannot represent the resulting hierarchy, the mutation must fail atomically.

---

# 17. Minimal Physical Diffs

Providers should avoid rewriting unaffected content.

This is particularly important for version-controlled implementations.

A small logical append should ideally result in a small physical diff.

Normalization of new input is acceptable.

Unnecessary reformatting of existing content is discouraged.

---

# 18. `TryAddSubArea`

Creates exactly one direct child.

It does not create a complex subtree.

New children are appended after existing siblings unless another stable provider-defined insertion rule exists.

Use `TryAppendContent` for complex structured additions.

---

# 19. `TryRename`

Rename changes only the addressed area's own name.

It preserves:

- direct content,
- descendants,
- descendant order,
- sibling position.

Virtual areas may be non-renamable.

---

# 20. `TryDelete`

Delete removes:

```text
addressed area
+
complete descendant tree
```

The area itself disappears.

This differs from truncate.

---

# 21. `TryTruncate`

Truncate preserves the addressed area.

For `ContentContainer` it removes:

```text
DirectContent
+
all descendants
```

For `ContentAggregation` it removes:

```text
all descendants
```

because direct content is always empty.

The aggregation or container itself remains.

Read-only virtual aggregations normally reject truncation.

---

# 22. `TryReplace`

Replace is:

```text
Atomic(
    Truncate(target)
    +
    Append(target, newContent)
)
```

For a `ContentContainer`, replacement content may contain direct text and children.

For a `ContentAggregation`, replacement content must contain subordinate structure only.

The target area itself remains.

---

# 23. `TryMoveContent`

MoveContent is:

```text
Atomic(
    Append(target, source content scope)
    +
    Truncate(source)
)
```

The source area itself remains.

Both `ContentAggregation` and `ContentContainer` may be source or target.

## Source aggregation

A source aggregation contributes only descendants.

## Target aggregation

A target aggregation cannot receive unstructured direct text.

Therefore the moved source content must be representable as subordinate structure.

## Invalid topology

These are invalid:

```text
MoveContent(A, A)
MoveContent(A, A/B)
MoveContent(A, A/B/C)
```

because the target cannot be the source or a descendant of the source.

This may be valid:

```text
MoveContent(A/B, A)
```

when all capabilities allow it.

---

# 24. Atomicity

All mutations must be atomic from the consumer's perspective:

- `TryDelete`
- `TryRename`
- `TryAddSubArea`
- `TryAppendContent`
- `TryTruncate`
- `TryReplace`
- `TryMoveContent`

A failed operation must leave no partial externally observable state.

Provider-specific implementation techniques may differ.

---

# 25. Failure Semantics

A `Try...` method returns false when the logical mutation cannot be completed.

Typical causes:

- area not found,
- capability unavailable,
- invalid name,
- ambiguous address,
- invalid payload,
- illegal free text on aggregation,
- illegal move topology,
- provider depth exceeded,
- physical persistence failure,
- inability to preserve atomicity.

The implementation must not leave partial state.

---

# 26. Requirements Table

| ID | Requirement | Mandatory |
|---|---|---|
| KR-001 | Expose knowledge through absolute logical area paths. | Yes |
| KR-002 | Represent repository root as `/`. | Yes |
| KR-003 | Treat area hierarchy as ordered. | Yes |
| KR-004 | Recursive enumeration uses pre-order traversal. | Yes |
| KR-005 | Preserve natural sibling order. | Yes |
| KR-006 | Distinguish `BeyondContent`, `ContentAggregation`, and `ContentContainer`. | Yes |
| KR-007 | `ContentAggregation` owns no direct content. | Yes |
| KR-008 | `HasDirectContent` returns false for `ContentAggregation`. | Yes |
| KR-009 | `GetDirectContent` returns empty string for `ContentAggregation`. | Yes |
| KR-010 | `GetAggregatedContent` is valid for aggregation and container levels. | Yes |
| KR-011 | Virtual aggregation nodes are permitted. | Yes |
| KR-012 | Virtual aggregations must expose deterministic order and content. | Yes |
| KR-013 | Virtual aggregations should be read-only unless write routing is unambiguous. | Strongly recommended |
| KR-014 | Direct content excludes descendant content. | Yes |
| KR-015 | `TryAppendContent` is non-destructive. | Yes |
| KR-016 | Append performs sparse hierarchical merge. | Yes |
| KR-017 | Append matches only direct children at each level. | Yes |
| KR-018 | Append may update multiple distributed branches in one call. | Yes |
| KR-019 | Existing sibling order is preserved. | Yes |
| KR-020 | Newly created siblings preserve incoming order. | Yes |
| KR-021 | Free text may be appended only to content containers. | Yes |
| KR-022 | Aggregation append payloads must contain subordinate structure only. | Yes |
| KR-023 | Structured input is interpreted relative to the target. | Yes |
| KR-024 | Providers preserve logical hierarchy when rebasing physical structure. | Yes |
| KR-025 | `TryTruncate` preserves the addressed area. | Yes |
| KR-026 | `TryDelete` removes the addressed area and descendants. | Yes |
| KR-027 | `TryRename` preserves subtree and sibling position. | Yes |
| KR-028 | `TryReplace` is atomic truncate plus append. | Yes |
| KR-029 | `TryMoveContent` is atomic append-to-target plus truncate-source. | Yes |
| KR-030 | Move supports aggregation and container levels. | Yes |
| KR-031 | Move target may not be inside source subtree. | Yes |
| KR-032 | All mutations are atomic. | Yes |
| KR-033 | Physical storage details do not leak into the interface. | Yes |
| KR-034 | Providers should minimize unnecessary rewrites. | Strongly recommended |

---

# 27. Top-Down Examples

## Read a complete notebook-like aggregation

```csharp
string content = repository.GetAggregatedContent(
  "/Knowledge/Engineering"
);
```

`Engineering` may be a `ContentAggregation`.

It has no direct content but may expose many subordinate containers.

## Read one concrete container

```csharp
string content = repository.GetDirectContent(
  "/Knowledge/Engineering/Architecture"
);
```

## Perform one sparse multi-branch update

```csharp
bool success = repository.TryAppendContent(
  "/Knowledge/Engineering",
  """
  # Architecture

  Additional architecture information.

  # Development

  Additional development information.
  """
);
```

This is valid if `Engineering` is a writable `ContentAggregation` and the payload can be routed entirely into subordinate areas.

## Invalid aggregation append

```csharp
bool success = repository.TryAppendContent(
  "/Knowledge/Engineering",
  "This text has no subordinate target."
);
```

This must fail because a content aggregation cannot own direct text.

---

# 28. Advanced Virtual Aggregation Example

A provider may expose:

```text
/Virtual Views
└── All Examples
```

with:

```text
ContentLevel = ContentAggregation
canAppendContent = false
canTruncate = false
canAddSubAreas = false
canBeRenamed = false
canBeDeleted = false
```

`GetAggregatedContent("/Virtual Views/All Examples")` may dynamically collect:

```text
/ProjectA/[Guide]/Example
/ProjectB/[Tutorial]/Example
/ProjectC/[Reference]/Examples/Advanced
```

into one deterministic textual result.

The provider may preserve source order, project-defined order, explicit ranking, or another stable deterministic ordering strategy.

The provider should document that strategy.

The virtual area does not need a backing document.

This pattern enables powerful cross-cutting knowledge views without changing the source material.

---

# 29. Bottom-Up Implementation Guidance

## `IKnowledgeRepository`

Owns the provider-neutral logical behavior.

It should not expose:

- file paths,
- Git commits,
- notebook IDs,
- Markdown heading numbers,
- database primary keys,
- remote API IDs.

## `ContentLevel`

Describes semantic content participation, not storage type.

## Internal logical representation

Providers should internally resolve physical state into something conceptually equivalent to:

```text
ResolvedArea
├── Path
├── ContentLevel
├── DirectContent
├── Children[]
└── Capabilities
```

For aggregation nodes:

```text
DirectContent = empty
```

always.

## Parser

Text-oriented providers parse source content into the ordered logical tree.

## Renderer

Renderers project logical mutations back into the physical provider format.

## Virtual projection engine

Providers supporting virtual aggregations may implement a projection layer that:

1. queries source areas,
2. selects matching content,
3. determines stable order,
4. synthesizes logical children or aggregated textual content,
5. reports read-only capabilities unless reverse write routing is unambiguous.

## Mutation engine

Providers should reuse one hierarchical merge implementation.

Conceptually:

```text
Append = Merge
Replace = Atomic(Truncate + Merge)
MoveContent = Atomic(Merge(target, source-scope) + Truncate(source))
```

---

# 30. Implementation Checklist

Before considering a provider complete, verify:

- logical paths are canonical,
- direct siblings are unambiguous,
- enumeration order is deterministic,
- recursive enumeration is pre-order,
- sibling order is preserved,
- `BeyondContent` never exposes textual content,
- `ContentAggregation` never owns direct content,
- `ContentContainer` may own direct content,
- aggregation reads work correctly,
- virtual aggregations are deterministic,
- virtual aggregations report truthful capabilities,
- append rejects unstructured direct text on aggregations,
- append merges into existing direct child branches,
- append creates missing branches,
- append supports multiple distributed branches,
- append preserves existing ordering,
- relative hierarchy is rebased correctly,
- truncate preserves the addressed area,
- delete removes the addressed area,
- rename preserves subtree and sibling position,
- replace is atomic,
- move is atomic,
- move supports aggregation and container areas correctly,
- invalid recursive move topology is rejected,
- provider storage details remain hidden,
- failed mutations leave no partial state,
- version-controlled providers minimize unnecessary diffs.

---

# 31. Design Principle Summary

Always reason in this order:

```text
Physical or virtual provider state
        ↓
Resolve
        ↓
Ordered logical area tree
        ↓
Determine ContentLevel
        ↓
Apply IKnowledgeRepository semantics
        ↓
Validate complete resulting state
        ↓
Persist or project atomically
        ↓
Expose stable logical result
```

The authoritative abstraction is the ordered logical area tree.

Physical files, Markdown headings, notebook pages, remote objects and virtual cross-cutting views are merely provider-specific projections.

The key implementation principle is:

> **Think in logical ordered areas first. Map to physical or virtual provider mechanics second.**
