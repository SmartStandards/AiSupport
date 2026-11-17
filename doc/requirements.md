# Requirements

## Goal

**SmartStandards AI Support** provides provider-neutral abstractions for AI-enabled applications, together with
reusable standard implementations and ASP.NET Core hosting support. Applications program against stable contracts
instead of concrete LLM providers, chat front ends or storage.

## Target audiences

- .NET developers who integrate AI features (prompting, chat, knowledge access) into their own applications or services.
- Operators of services who want to expose these features over the network via **UJMW**, and therefore also to AI agents
  (e.g. through MCP adapters).

## Functional requirements

### Abstractions (package `SmartStandards.AiSupport`)

| Contract | Purpose |
|---|---|
| `ICommonLLM` | Low-level access to an LLM: web-search prompting (text or typed JSON result), image generation, image editing. Must be exposable via UJMW. |
| `IPromptingUIBackend` | Backend for chat front ends: chat history, create, rename and delete chats, send messages (file attachments planned). |
| `IPromptingSessionStore` | Persistence of prompting sessions; basis for an adapter between `IPromptingUIBackend` and `ICommonLLM`. |
| `IPromtLibrary` | Management of reusable prompts. |
| `IPromptingSettingsService` | Selection of the LLM provider/model, storage of API keys (never returned) and per-provider opt-outs of native provider capabilities. |
| `ICodeArtifactMap` | Semantic description of code artifacts and components per repository URL. |

Access to knowledge stores (`IKnowledgeRepository`) has moved to the separate `SmartStandards.KnowledgeManagement*`
packages and is only consumed here.

### Standard implementations (package `SmartStandards.AiSupport.CommonProviders`)

- `OpenAiLLMConnector` as an implementation of `ICommonLLM` against the OpenAI API.
- File-based implementations of the session store, prompt library and code artifact map, plus a simple prompting UI backend.
- `DynamicAiServiceFactory`: creates a runtime implementation for any .NET interface whose method calls are delegated
  to an `ICommonLLM` (pure AI communication; AI-generated code is prepared but not yet implemented).
- `GitHubCopilotCliConnector` as a chat provider on top of the locally installed GitHub Copilot CLI (see below).

### Provider capabilities and the Level-2 harness

The middle layer (`CheapPromptingUIBackend`, later the Level-2 harness) can **emulate** base features such as conversation
context, but it must not replace features which an underlying SDK/harness already provides natively.

| Id | Requirement |
|---|---|
| CAP-1 | If a connector can use an SDK/harness that supports a feature natively, the native feature is preferred. The Level-2 emulation is only a fallback. |
| CAP-2 | Every provider describes its native features via `LLMProviderInfo.Capabilities`. The list contains all known capability names (`LLMCapabilityNames`) with `IsAvailable`, so that clients can render every feature. |
| CAP-3 | Available capabilities can be opted out per provider (`GetDisabledCapabilities` / `SaveDisabledCapabilities`, persisted with the settings). Unknown or unavailable names are ignored. |
| CAP-4 | An opt-out switches the connector to the Level-2 emulation of the feature; the chat must keep working. |
| CAP-5 | The chat UI shows a switch for every available capability and an X for every unavailable one. |
| CAP-6 | Capability names: `ProviderSideContext` (the provider holds the conversation; only new messages are sent), `PersistentSession` (one long-running inner harness instance per chat), `SessionResume` (an ended session can be restored by its id). |

### Tool sources, approvals and guards (prompting UI)

| Id | Requirement |
|---|---|
| TLM-1 | Tools are grouped into tool sources (`IHarnessToolProvider`). Every source has a global level (`Hidden`, `RequiresConfirmation`, `NoConfirmation`) and one JSON settings object, validated by the provider when saved. Secrets are stored separately (protected credentials file), never inside the settings object. |
| TLM-2 | MCP servers (Streamable HTTP, optional bearer token) can be registered as tool sources. |
| TLM-3 | Public interfaces of an uploaded .NET assembly can be registered as UJMW contract tool sources (base URL + token); calls go through the UJMW `DynamicClientFactory`. |
| OOB-1..4 | Built-in tool sources: preferences update, file system access, internet access (HTTP GET), shell access (cmd/PowerShell). They are registered via `RegisterOutOfTheBoxToolProviders()`. |
| APR-1 | Calls of sources with level `RequiresConfirmation` pause the turn until the user answers: `Deny` (rejection result, source hidden for this chat), `AllowOnce`, `AllowForChat` (persisted with the chat, revocable). Unanswered requests are denied after a timeout; cancelling the turn denies as well. |
| GRD-1 | The OOB tools enforce allow-lists independently of the approval: `allowedPaths` (RO/RW, recursive, normalized), `allowedDomains` (exact host or `*.domain`), `allowedCommands` (literal, wildcard or `regex:` over the full command line). A miss asks the user (`Deny`, `AllowOnce`, `AllowPermanently` = append to the global allow-list). |
| AUD-1 | Every requested tool call is written into the audit log of the chat (timestamp, source, tool, masked/truncated argument summary, decision, duration, success/error, result size; max. 500 entries). |
| PRG-3 | Tools can report their own progress (status text, current item, total items). It is forwarded through the progress callback of the running `SendMessage` turn. The shell tool reports its last non-empty output line; UJMW contract tools receive an injected `Action<string, int, int>` for such parameters (never visible to the model). Failures of the progress sink never fail the tool call. |
| SKM-6 | Selecting the skill source level `InitializeAtStart` immediately shows a warning (higher token usage in every context), globally and in the chat details. |
| SKM-9 | Skill sources have a freely selectable label (`SetSkillSourceDisplayName`); an empty value restores the derived default label. Built-in sources cannot be renamed. |
| CHD-3 | The chat details dialog is split into the tabs **General** (name, provider, model, context state, compress), **Security** (per-chat skill and tool source levels) and **Audit log**. |
| CHD-8 | Every tool source in the chat details shows a log icon (grey = unused in this chat, blue = used); a click opens the audit log filtered to this source. |
| CTL-1 | Control messages caused by changes in the chat details are visible immediately after saving (the chat is reloaded). |
| UIC-2 | The allow-lists of the built-in tools (`allowedPaths`, `allowedDomains`, `allowedCommands`) are maintained in fixed editors; a raw JSON editor stays available. Unknown properties of the settings object are preserved. |
| UPS-1 | User instructions changed by the model (`preferences_update`) are visible in the settings without restart; changes in the settings reach running chats with their next turn (the harness prompt fingerprint is re-evaluated per turn). |

### GitHub Copilot CLI session mode

| Id | Requirement |
|---|---|
| COP-1 | By default, every chat uses its own long-running `copilot --acp` process (Agent Client Protocol over stdio) with one native session. Only the new messages of a turn are sent. |
| COP-2 | The id of the native session is persisted as `contextStore` (`copilot-acp:<sessionId>`) with the chat, so it survives restarts of the host. |
| COP-3 | Processes which are idle longer than `SessionIdleTimeout` (default 15 minutes) are terminated; all processes are terminated when the host exits. |
| COP-4 | If the process of a chat has ended, the session is restored via `session/load` with the persisted id, provided that the CLI advertises `loadSession` and `SessionResume` is not opted out. |
| COP-5 | If the session cannot be continued (no resume support, unknown id, process died during the turn), the connector raises `ContextStoreInvalidException` and the middle layer rebuilds the context from the persisted outer chat history. No turn may be lost. |
| COP-6 | The CLI acts as pure chat model: it runs in an empty scratch directory, no file system/terminal capabilities are advertised and every permission request is refused. |
| COP-7 | Missing authentication opens the interactive login once (desktop processes only) and the turn is retried once. A GitHub token from the settings is passed as `COPILOT_GITHUB_TOKEN`. |
| COP-8 | If `ProviderSideContext` is opted out, the prompt mode (`copilot -p`, one process per turn, full self-maintained context) is used. If `PersistentSession` is opted out, the process ends after every turn (the session is resumed or rebuilt in the next turn). |

### Hosting (package `SmartStandards.AiSupport.AspNetCore`)

- Integration into ASP.NET Core via `AddAiSupport(...)`, so that the contracts can be provided as UJMW endpoints.
- An opt-in generic MCP facade for ASP.NET Core applications. It exposes only explicitly selected controller actions
  as one MCP tool per selected API operation, derives tool metadata from ASP.NET Core API metadata, maps route, query
  and JSON-body arguments, and executes target actions in process without loopback HTTP.

## Non-functional requirements

- **Target platforms:** .NET Framework 4.8, .NET 8.0 and .NET 10.0 for the contracts; .NET 8.0 and .NET 10.0 for the providers.
- **Transportability:** all contracts must remain remotable via UJMW (and additionally via WCF attributes on .NET Framework).
  This means simple DTOs, synchronous methods and no callbacks or events.
- **Provider neutrality:** provider specifics (endpoints, model names, formats) must not leak into the contracts.
- Delivered as NuGet packages. Versioning is done automatically by the build pipeline.

## Non-goals

- No chat user interface of its own; only the backend is provided.
- Knowledge stores (`IKnowledgeRepository` and its providers) are no longer part of this repository.
- The **Level-2 AI harness** (tool-calling handshake, CLI proxies, local LLM, MCP client work) is not an active
  requirement yet, except for the chat-only slice described above (provider capabilities, Copilot session mode).
  See [ideas.md](ideas.md).
- The full OAuth/CalDAV MCP demo scenario is not part of the first reusable facade slice yet.
