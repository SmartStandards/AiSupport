---
name: ai-harness-development
description: Binding design rules and condensed protocol knowledge for building the SmartStandards "Level-2 AI harness" (IConversationalLLM / ICommonLLM connectors, tool-call handshake via polling, routing vs. delegation of tools/approvals/questions, proxies for Claude Code CLI, GitHub Copilot CLI, OpenAI and self-hosted local LLMs like Ollama, MCP/ACP integration). Use when designing, implementing, reviewing or extending LLM connectors, the agent loop/orchestrator, tool registries, CLI proxies, MCP clients/bridge servers, or when answering questions about how modern agent harnesses work internally.
metadata:
  owner: SmartStandards/AiSupport
  knowledge-date: "2026-09-25"
---

# AI Harness Development

## Purpose

Preserve and apply the project knowledge for building a **Level-2 harness**: our own agent harness that drives an
LLM *or another harness* (Claude Code, Copilot CLI, …) exactly like that harness drives its own LLM, and injects
our own tools that run on the local PC, in the LAN or (later) behind MCP servers.

Human-readable sources (authoritative for intent, keep in sync with this skill):

- `doc/ideas.md`, section 1 "Level-2 AI Harness": concept, interface draft, routing/delegation, roadmap, open points, research summary and all source links

Detail references of this skill (load on demand):

- [references/protocols.md](references/protocols.md) – wire-level facts per API/CLI/protocol
- [references/harness-internals.md](references/harness-internals.md) – how modern harnesses work inside (loop, permissions, context, robustness)

## Binding design decisions

1. **Handshake fidelity.** Reproduce the original tool-use cycle 1:1 (declare → model requests call(s) with call-id →
   execute → resubmit result by call-id → repeat until end). Never "simplify" call-ids, ordering, parallelism or error semantics.
2. **Separate interface.** `ICommonLLM` keeps its one-shot methods. The conversational/tool handshake lives in an
   additional interface (working title `IConversationalLLM`) that connectors implement in addition.
3. **Polling, not callbacks.** The interface must stay exposable via UJMW (and WCF attributes on net48). Therefore the loop is a
   stateful session with long-polling: `BeginConversation` → `SubmitUserMessage` → `PollTurn(sinceSequenceNo, maxWaitMs)` →
   `SubmitResponses` → … → `EndConversation`, plus `CancelTurn`.
4. **All requests are equal.** Tool calls, approvals (permission for the inner harness's *own* tools) and user questions
   (AskUserQuestion, MCP elicitation) are all `HostRequest`s (`Kind` = ToolCall | Approval | UserQuestion). Each one pauses the turn
   (`Status = AwaitingHost`) and is answered with a `HostResponse` referencing its `RequestId`.
5. **Routing vs. delegation.**
   - **Full routing outside is mandatory** for every implementation, even when it is hard or brittle
     (e.g. the Claude Agent SDK control protocol). The self-hosted local LLM has no inner harness, so full routing is the only option there.
   - If an implementation can do something internally (hook in extra tool sources, decide approvals by rules), expose it as an
     **optional, queryable capability** (`GetCapabilities()` + `DelegateToolSources(...)`, `DelegateRequestPolicy(...)`). The Level-2
     harness then hands the tools or rules in and steps out of the communication. Calling an unsupported delegation method throws
     `NotSupportedException`.
   - **Mixed mode is the normal case.** Decide per tool source: delegate only if the capability supports the source kind **and**
     the source is reachable (network, credentials) from the machine running the inner harness. Otherwise route outside.
   - Fix the delegation **before the first turn** (prompt caching; many harnesses load tools at startup only).
   - Policy delegation never swallows cases: whatever the rules don't cover still arrives as a `HostRequest`.
6. **The Level-2 harness is stackable.** It may itself be exposed as `IConversationalLLM` or as an MCP server later.
7. **Native first, emulation only as fallback.** The name "Level-2 harness" is deliberate: it *can* emulate base features
   (context/history, sessions, tool loop, approvals) when a provider lacks them, but it must **not** replace features that an
   underlying SDK/harness already provides. If a connector can go through an SDK/harness that supports a feature natively
   (e.g. Copilot ACP sessions, OpenAI `previous_response_id`), the connector **must** use the native feature.
   - Every provider describes its native features as **capabilities** (`LLMProviderInfo.Capabilities`, each with
     `IsAvailable`). The list always contains all known capability names, so a UI can render every feature.
   - Available capabilities are **opt-out** per provider (persisted, e.g. `IPromptingSettingsService.SaveDisabledCapabilities`).
     UI convention: a switch for available features, an X for unavailable ones. An opt-out switches the connector to the
     Level-2 emulation of that feature; it never breaks the chat.
   - Current capability names: `ProviderSideContext` (provider holds the conversation), `PersistentSession` (one long-running
     inner harness instance per chat), `SessionResume` (an ended session can be restored by its id). Planned:
     `PausableToolCalls` (a pending tool call may stay open while the user is asked; otherwise an explicit wait notice is used).
8. **Sessions are disposable, the outer history is the truth.** One inner-harness instance per chat; idle instances may be
   terminated at any time. Recovery order: (1) running instance, (2) native resume with the session id persisted in the
   `contextStore` (ACP `session/load`), (3) `ContextStoreInvalidException` → the Level-2 harness rebuilds the context from the
   persisted outer chat history (optionally a compressed baseline). Never lose a turn because an inner session died.

## Invariants every connector/orchestrator must enforce

- Every tool call gets **exactly one** result, including on cancel/abort (synthesize an error result). Anthropic rejects histories
  with an unanswered `tool_use`.
- Results of one parallel round are sent **together**. On Anthropic, `tool_result` blocks come **first** in the user message, text after.
- OpenAI reasoning models: pass **reasoning items back** together with `function_call_output`.
- Tool names: restrict to `^[a-zA-Z0-9_-]{1,64}$` (Anthropic forbids `.`, which MCP allows; keep headroom for prefixes like `mcp__<server>__`).
- Tool list: **deterministically sorted and stable** during a conversation, otherwise prompt caching breaks.
- Distinguish **tool errors** (`IsError = true`, content goes to the model so it can self-correct; write actionable messages) from
  **protocol errors** (unknown tool, malformed request → orchestrator/log).
- `ReadOnlyHint` = may run in parallel with other read-only calls; mutating tools run sequentially.
- Truncate or summarize oversized tool results before resubmitting.
- Treat tool results as **untrusted** (indirect prompt injection); keep them in tool-result slots, never in system prompts.
- Normalize stop reasons to: `EndTurn`, `MaxTokens`, `MaxTurns`, `Budget`, `Refusal`, `Cancelled`, `Error`.
- Timeouts: bridge/MCP tool timeouts of the inner harness must exceed the expected tool runtime plus polling latency.

## Tool provider settings UI contract (norm, planned)

Source: [epic-prompting-ui-iteration-2.md](../../../doc/epics/epic-prompting-ui-iteration-2.md), 3.2.1. Binding for every
`IHarnessToolProvider` that delivers its own detail tab (OOB providers included).

- The provider returns **one self-contained HTML page** (`GetSettingsUiHtml()`, inline JS/CSS, no external resources) or `null`
  (the host then shows a plain JSON editor).
- The page exchanges **exactly one JSON settings object** with the host. Nothing else goes in or out.
- Host rendering: `iframe` with `sandbox="allow-scripts"` (never `allow-same-origin`), `srcdoc`, CSP `default-src 'none';
  script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:`. No network, no cookies, no host DOM access.
- Channel: `window.postMessage` only. Messages (JSON objects with a `type` field):

| `type` | Direction | Payload | Meaning |
|---|---|---|---|
| `settings.ready` | page → host | – | optional; the page is ready to receive the object |
| `settings.load` | host → page | `settings` (object) | the current settings object; may be sent again (e.g. after reset) |
| `settings.changed` | page → host | `settings` (object) | the **complete** new object (no patches), sent on every change |

- The host saves via `SaveToolProviderSettings(sourceId, settingsJson)`; the **provider validates server-side** and rejects
  invalid objects with a readable message. The page is never trusted.
- **No secrets** in the object. Tokens/PATs use credential slots (`GetCredentialSlots()`) rendered by the host outside the frame
  and stored via `ICredentialStore`.
- Convention credo: unknown message types and unknown fields are ignored on both sides; a page that never sends
  `settings.ready` still receives `settings.load` after the frame's `load` event.

## Implementation map (verify flags before implementing; they change often)

| Implementation | Routing outside | Tool delegation | Policy delegation |
|---|---|---|---|
| `ClaudeCliProxy` | Bridge MCP server that blocks until `SubmitResponses`, **and** the Agent-SDK control protocol in .NET (`control_request` `can_use_tool` / SDK MCP tunneling) | `--mcp-config` | `--allowedTools`, `--disallowedTools`, `--permission-mode` |
| `CopilotCliProxy` | ACP client (`copilot --acp`), `session/request_permission`, bridge MCP server | ACP `session/new.mcpServers` | `--available-tools`/`--excluded-tools` (global per process), `--allow-tool`/`--deny-tool` |

Implemented chat-only slice: `GitHubCopilotCliConnector` (session mode, one `copilot --acp` process per chat, pool with idle
reaper, `contextStore = "copilot-acp:<sessionId>"`, resume via `session/load` if `agentCapabilities.loadSession`, permission
requests are refused) on top of `GitHubCopilotAcpSession` (transport-agnostic ACP client, testable with pipes). Prompt mode
(`copilot -p`) remains as fallback when `ProviderSideContext` is opted out.
| `OpenAiLLMConnector` | Responses API function calling | remote MCP tool of the API (public servers only, no LAN) | – |
| Local mini LLM | Ollama native `/api/chat` tool calling | – | – |

Brittle paths (Claude control protocol, Codex dynamic tools) are acceptable. Protect them with feature detection
(`system/init.capabilities`), a pinned CLI version and contract tests against recorded fixtures.

## Local model selection (state 2026-09)

Runtime: **Ollama**. Use native `/api/chat` for streaming tool calls, because `/v1/chat/completions` reportedly loses tool calls
when streaming. Candidates: **Qwen3-4B-Instruct-2507** (start; best sub-7B BFCL v4), Gemma 4 E4B (native function tokens,
Apache 2.0), Phi-4-mini (fastest), gpt-oss-20b (stronger, ~16 GB). Decide with our own handshake benchmark (see tests).

## Testing checklist (every connector)

No tool · one call · parallel calls · multi-round calls · tool error (`IsError`) · tool timeout · cancel mid-turn · `max_tokens` ·
approval granted/denied · user question answered · mixed mode (one delegated + one routed source) · delegation requested but
unsupported → `NotSupportedException`. Use a scriptable **fake LLM** for deterministic unit tests. Real providers go into
ignored/category tests. Record raw provider traffic as fixtures.

## Maintaining this skill

- When new facts are verified (flags, protocol messages, model results), update the matching `references/*.md` file with the
  date and version, and adjust `doc/ideas.md` section 1.10 if human-relevant.
- When design decisions change, update `doc/ideas.md` (or `doc/1-requirements.md`/`doc/2-architecture.md` once implemented) **and** the "Binding design decisions" section here in the same change.
- Mark unverified or reverse-engineered facts as *(unofficial)*. Never silently turn them into rules.
