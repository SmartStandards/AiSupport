---
name: most-important-details-for-smartstandards-aisupport
description: Project-specific implementation details, constraints and pitfalls for the SmartStandards AiSupport repository (shared projects, multi-targeting net48/net8/net10, UJMW-exposable contracts, generated versioning artifacts, documentation layout). Use before changing code, project files or docs in this repository.
metadata:
  owner: SmartStandards/AiSupport
---

# Most important details for SmartStandards AiSupport

## Documentation layout

- Human-readable docs follow the ai-cowork-process structure: `readme.md`, `doc/requirements.md`, `doc/architecture.md`,
  `doc/quickstart.md`, `doc/ideas.md`. Links must stay relative.
- `doc/changelog.md` and `doc/versioninfo.json` are maintained by the build/versioning pipeline. **Never edit them.**
- Future concepts go into `doc/ideas.md`, never into extra ad-hoc files under `doc/`. The Level-2 AI harness concept lives
  there as section 1. Its binding decisions are mirrored in the skill `ai-harness-development`.

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
- `.github/copilot-instructions.md` in `dotnet/` only contains generic Azure rules, not project rules.

## Related skills

- `ai-harness-development`: binding design rules and protocol knowledge for LLM connectors, the tool-call handshake and CLI proxies.
- `include-additional-mandatory-skills`: additional knowledge sources.
