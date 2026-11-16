---
name: most-important-details-for-smartstandards-aisupport
description: Project-specific implementation details, constraints and pitfalls for the SmartStandards AiSupport repository (shared projects, multi-targeting net48/net8/net10, UJMW-exposable contracts, generated versioning artifacts, documentation layout). Use before changing code, project files or docs in this repository.
metadata:
  owner: SmartStandards/AiSupport
---

# Most important details for SmartStandards AiSupport

## Documentation layout

- Human-readable docs follow the ai-cowork-process structure: `readme.md`, `doc/1-requirements.md`, `doc/2-architecture.md`,
  `doc/3-quickstart.md`, `doc/ideas.md`. Links must stay relative. README and all `doc/` files are written in English.
- `doc/changelog.md` and `doc/versioninfo.json` are maintained by the build/versioning pipeline. **Never edit them.**
- Future concepts go into epics: `doc/epics/epic-<title>.md` (ai-cowork-process). `doc/ideas.md` is the legacy
  container (Level-2 AI harness as section 1, MCP facade epic as section 3) until it is migrated into epics. Binding
  harness decisions are mirrored in the skill `ai-harness-development`.
- Chat middle layer, `contextStore` convention and PoC chat client: [epic-interactive-prompting.md](../../../doc/epics/epic-interactive-prompting.md)
  (concept, not yet an active requirement).

## Code structure

- Source lives in **shared projects** (`*.shproj` + `*.projitems`). **Every new `.cs` file must be added to the matching
  `.projitems`** (`<Compile Include="$(MSBuildThisFileDirectory)…" />`), otherwise no target project compiles it.
- Target projects per framework: contracts `SmartStandards.AiSupport.net48|net8.0|net10.0`, providers
  `…CommonProviders.net8.0|net10.0`, hosting `…AspNetCore.net10.0`. Three `.nuspec` files in `dotnet/` define the NuGet packages.
- Compiler constants: `NET_FX` (net48, enables `[ServiceContract]`/`[OperationContract]` on contracts), `NET_CORE`, `NET_8_`, `NET_10_`.
- Namespaces are `AI.SmartStandards.<Area>` (`LowLevelPrompting`, `InteractivePrompting`, `KnowledgeAccess`, `UjmwSupport`).

## Contract rules

- Contracts must stay **UJMW/WCF-transportable**: synchronous methods, simple DTOs with public properties, no callbacks,
  events, delegates or `IAsyncEnumerable`. Long-running interactions use session ids and (long-)polling.
- `IKnowledgeRepository` was moved out to the `SmartStandards.KnowledgeManagement*` packages. Do not re-add it here.

## Known state and pitfalls

- `OpenAiLLMConnector` hard-codes the model (`gpt-4o`), blocks on `.Result`, and its image methods swallow exceptions
  (`return null`). Keep this in mind when relying on its error behavior. Improving it is part of the harness idea.
- `DynamicAiServiceFactory`: only `ImplementationMode.PureAiCommunication` works. `AiGeneratedInmemoryCode` throws `NotImplementedException`.
- Tests against real providers are marked `[Ignore]` and need API keys.
- MCP diagnostics are opt-in via `McpFacadeOptions.EnableDiagnosticTracing` (demo: `Mcp:EnableDiagnosticTracing`).
  `McpDiagnosticTrace` uses DevLogger Trace events in the user-approved `MyLogEventKind` range **75300-75399**.
  Never log tokens, cookies, payloads, query strings, arbitrary client method names/IDs or exception messages there.
  Correlation IDs are server-generated; elapsed times are cumulative. The logging sink must accept Trace (0).
  See `doc/3-quickstart.md` for activation and phase interpretation. OAuth endpoints belong to AuthTokenHandling.
- The UJMW side channel (`_`) must stay fully transparent in the MCP facade (flat string-to-string dictionary, keys may
  use dot notation, values are always strings; passthrough in both
  directions, no filtering). Values select a scope; authorization is decided server-side against the token. Field
  semantics are only added via `McpFacadeOptions.AddSideChannelFieldDescription`.
- `.github/copilot-instructions.md` in `dotnet/` only contains generic Azure rules, not project rules.

## Related skills

- `ai-harness-development`: binding design rules and protocol knowledge for LLM connectors, the tool-call handshake and CLI proxies.
- `include-additional-mandatory-skills`: additional knowledge sources.
- `asp-engine-webview-coupling` (AI-SKILLS-SMART): procedure for WebView2 desktop utilities with an in-memory ASP.NET Core
  engine, embedded UI resources, dual hosting and UJMW long-running operations (used by the PoC chat client).
