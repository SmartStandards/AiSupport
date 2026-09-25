# How modern agent harnesses work inside (state 2026-09-25)

Condensed from official docs (Anthropic, OpenAI, GitHub) and marked third-party analyses. Use it to design our orchestrator
so it behaves like the originals.

## Terms

- **Turn** is ambiguous. Claude Agent SDK: one model round trip incl. tool execution. ACP/Codex: the whole processing of one user
  message (possibly dozens of model calls). Always define which one you mean in code and docs.
- **Client tool** (caller executes) vs. **server tool** (provider executes, e.g. web search) vs. **harness-internal tool**
  (`Read`/`Edit`/`Bash`, executed by the inner harness; for Level 2 these are "internal").

## The loop

```text
history = [system prompt, tool defs, project context, env context, user message]
loop:
  response = model(history)                 # streamed
  history += response                       # text, thinking/reasoning, tool calls
  if no tool calls: break
  for each call (parallel only if read-only):
     permission pipeline → execute → result or is_error
     history += tool_result(call.id, result)
  context management; check limits (turns, budget, tokens)
return final text + usage/cost + session id
```

- Stateless toward the API: Codex resends the full history every time (no `previous_response_id`, also for Zero Data Retention).
  JSON volume grows quadratically, but prefix caching keeps the cost from growing the same way.
- Termination only by an answer without tool calls, or by limits/cancel.
- Claude Code: one async generator `while(true)` is the only code path that talks to the model, runs tools, manages context and
  recovers from errors. It returns a typed terminal state. Its dependencies (model caller, compactor, id generator) are injected for tests. *(unofficial)*

## Prompt layout and caching

Order from stable to volatile: harness system prompt → tool definitions → project context (`CLAUDE.md`, `AGENTS.md`,
`copilot-instructions.md`, skill summaries only) → environment context (cwd, OS, date, git, permission mode) → history →
injected reminders. Things that break the cache: editing instruction files mid-session, changed or reordered tool lists
(e.g. an MCP `list_changed`), and a changed `tool_choice`.

## Tool execution

- Claude permission pipeline (official, fixed order): **Hooks (PreToolUse) → deny rules → ask rules → permission mode → allow rules
  → `canUseTool` callback**. Modes: `default`, `acceptEdits`, `plan`, `dontAsk`, `auto` (classifier), `bypassPermissions`.
- A denied call returns to the model **as a tool result** and the loop continues.
- Codex "smart approvals": guardian subagents apply policies to risky actions.
- Parallelism: read-only tools concurrently, mutating ones sequentially. Custom tools default to sequential unless `readOnlyHint`.
- Claude Code starts tools **while the model still streams**, as soon as a tool block is complete (`isConcurrencySafe`). *(unofficial)*
- Per-call pipeline: schema validation → input normalization → hooks → permission → sandbox → execute → truncate large results →
  PostToolUse hooks. *(unofficial)*
- Exceptions in tool handlers do not stop the loop. They become error results. Prefer composing an actionable `isError` message yourself.

## Context management

- Auto-compaction near the context limit summarizes older history (Claude: `system/compact_boundary`, `PreCompact` hook,
  `/compact`). Persistent rules belong in instruction files that are re-injected each request, not in the first prompt.
- Claude Code layers: tool-result budget → snip old messages → microcompact orphaned tool results → summarization. It triggers
  ~13k tokens before the window ends and has a circuit breaker after 3 failures. *(unofficial)*
- Tool search / deferred loading: the model sees only tool names and loads schemas on demand (default for MCP tools in Claude Code).
- Subagents get a fresh context and return only a summary (~1–2k tokens) as a tool result.
- Long-running work: an initializer agent creates a feature list, a progress file and a git repo. Later sessions make incremental
  progress and leave artifacts. Context is an attention budget ("context rot"), so load information just in time via references.

## Robustness

- Retries with backoff (Claude emits `system/api_retry` with an error category), model fallback on overload, output-limit
  escalation on `max_tokens`, reactive compaction on "prompt too long". All of these have hard caps against death spirals. *(unofficial)*
- On abort, synthesize tool results for requested but unexecuted calls so the history stays API-valid.
- Stop hooks can reject "done" and force continuation.
- Limits: `maxTurns`, `maxBudgetUsd` (Claude SDK), `max_turn_requests` (ACP).

## Sessions

Transcripts persist locally (Claude: `.jsonl` per session) and can be resumed or forked (`--resume`, Codex `thread/resume|fork`,
ACP `session/load`). The Claude SDK can mirror transcripts to a custom store (`sessionStore`).

## Host control pattern (key insight for Level 2)

All major harnesses expose a **bidirectional JSON protocol over stdio** (Claude control protocol, Codex app-server, ACP). The
harness sends its own requests to the host (tool execution, approvals, questions), and the turn **pauses until the host answers**.
Our `PollTurn` / `SubmitResponses` pair is the transport-neutral (UJMW-capable) projection of exactly this pattern.
