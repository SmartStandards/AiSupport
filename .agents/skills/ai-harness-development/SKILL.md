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

## Implementation map (verify flags before implementing; they change often)

| Implementation | Routing outside | Tool delegation | Policy delegation |
|---|---|---|---|
| `ClaudeCliProxy` | Bridge MCP server that blocks until `SubmitResponses`, **and** the Agent-SDK control protocol in .NET (`control_request` `can_use_tool` / SDK MCP tunneling) | `--mcp-config` | `--allowedTools`, `--disallowedTools`, `--permission-mode` |
| `CopilotCliProxy` | ACP client (`copilot --acp`), `session/request_permission`, bridge MCP server | ACP `session/new.mcpServers` | `--available-tools`/`--excluded-tools` (global per process), `--allow-tool`/`--deny-tool` |
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
- When design decisions change, update `doc/ideas.md` (or `doc/requirements.md`/`doc/architecture.md` once implemented) **and** the "Binding design decisions" section here in the same change.
- Mark unverified or reverse-engineered facts as *(unofficial)*. Never silently turn them into rules.
