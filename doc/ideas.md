# Ideas and Future Directions

> The content of this document is **not an active requirement**. An idea is implemented only after explicit
> developer commitment. It then moves into [1-requirements.md](1-requirements.md) as a requirement and structurally into
> [2-architecture.md](2-architecture.md).
>
> **Design decisions** already made within an idea (marked as "decided") become binding as soon as the idea is implemented.

## 1. Level-2 AI Harness

> Status: **concept / basis for discussion** (2026-09-25)
> Affected components: `ICommonLLM`, `OpenAiLLMConnector`, `DynamicAiServiceFactory` (UJMW), new proxy connectors

### 1.1 Motivation

Today's agent tools such as **Claude Code CLI** or **GitHub Copilot CLI** are already a *harness* themselves:
they talk to an LLM, receive tool-call requests from it ("please call `Read` on file X now"), execute the tools and
send the results back to the LLM until the LLM delivers a final answer.

One level above, we want to build **our own harness ("Level 2")** that

- talks to such an inner harness (or directly to an LLM) **as if it were an LLM**,
- offers it **our own tools** that run **on the local PC or in the LAN**,
- **imperatively** accepts the inner harness's tool-call requests, executes them and **returns** the results,
- in the medium term also obtains tools via a **fully implemented MCP client** (we call MCP servers ourselves and forward their answers).

```text
┌──────────────────────────────────────────────────────────────────────┐
│ Level-2 harness (our code)                                           │
│                                                                      │
│   Orchestrator (agent loop) ──── ToolRegistry                        │
│        │   ▲                       ├─ local .NET tools               │
│        │   │ tool calls (polling)  ├─ LAN tools (UJMW/HTTP)          │
│        ▼   │ tool results          └─ MCP client → MCP servers (later) │
│   ICommonLLM (+ extension for the tool handshake)                    │
└────────┬─────────────────────────────────────────────────────────────┘
         │ (in-process or via UJMW REST across the LAN)
   ┌─────┴───────────┬──────────────────────┬──────────────────────┐
   ▼                 ▼                      ▼                      ▼
OpenAiLLMConnector  ClaudeCliProxy        CopilotCliProxy        LocalMiniLlmConnector
(cloud API)         (local process,       (local process,        (Ollama / llama.cpp /
                     Level-1 harness)      Level-1 harness)       Foundry Local …)
```

### 1.2 Guiding principle for step 1: "handshake as close to the original as possible"

The first step is **not** MCP. Instead, we talk to the inner harness (or the LLM) **exactly** the way it talks to its
own inner LLM. The tool-use cycle known from the provider APIs is reproduced 1:1:

1. **Request**: message history + tool declarations (name, description, JSON schema of the parameters).
2. **Response with stop reason "tool use"**: the model returns one or more tool-call requests
   (each with a **call id**, tool name and argument JSON). Several calls may be requested *in parallel*.
3. **Execution** by the harness (in our case: Level 2).
4. **Resubmit**: tool results are appended to the history, referenced by call id
   (including the error flag `is_error` and, if needed, text/image content blocks).
5. Repeat from step 2 until the stop reason is "done" (`end_turn` / `completed`) or an abort reason occurs
   (`max_tokens`, refusal, timeout, cancel).

Models the neutral data model should follow (mappable losslessly to all of them):

| Concept              | Anthropic Messages API                  | OpenAI Responses API                     | OpenAI Chat Completions (also Ollama etc.) |
|----------------------|-----------------------------------------|------------------------------------------|---------------------------------------------|
| Tool declaration     | `tools[] {name, description, input_schema}` | `tools[] {type:function, name, parameters}` | `tools[] {type:function, function:{…}}`  |
| Tool-call request    | content block `tool_use {id, name, input}` | output item `function_call {call_id, name, arguments}` | `message.tool_calls[] {id, function}` |
| Stop reason          | `stop_reason: tool_use`                 | output contains `function_call` items    | `finish_reason: tool_calls`                 |
| Tool result          | `tool_result {tool_use_id, content, is_error}` | `function_call_output {call_id, output}` | message `role: tool, tool_call_id`       |
| Continuation         | full history again                      | `previous_response_id` or history        | full history again                          |

**Important:** call ids, ordering, parallelism and error semantics are passed through and not "simplified".
Only then do the models and harnesses behave the way they were trained or built.

### 1.3 Evolving `ICommonLLM`

#### 1.3.1 Starting point

Today `ICommonLLM` only offers one-shot operations (`CallWebSearchApi`, `CallWebSearchApi<T>`, `CallImageEditApi`,
`CallImageGeneratorApi`). It has neither conversations nor tool calls.

#### 1.3.2 Why polling?

`ICommonLLM` must remain exposable over the network via **UJMW** (and via WCF attributes on .NET Framework).
Callbacks, events or `IAsyncEnumerable` cannot be transported cleanly that way. The handshake is therefore modeled
as a **stateful session with (long) polling**:

- The caller starts a turn.
- It polls the turn state and receives new output **and pending tool-call requests**.
- It delivers tool results with a separate call.
- The implementation (e.g. a CLI proxy) keeps the inner harness "on hold" in the meantime.

Long polling (`maxWaitMs`) avoids busy waiting without leaving the transport model.

#### 1.3.3 Draft (sketch, names still open)

Recommendation: `ICommonLLM` keeps its convenient one-shot calls. The tool handshake goes into an
**additional interface** (working title `IConversationalLLM`) that connectors implement in addition.
This keeps the existing contract stable, and lean providers (e.g. pure image generators) don't have to support anything new.

```csharp
public interface IConversationalLLM {

  LlmCapabilities GetCapabilities();

  /// <returns>conversationId</returns>
  string BeginConversation(ConversationOptions options); // model, system prompt, tool declarations, limits

  /// <returns>turnId</returns>
  string SubmitUserMessage(string conversationId, ContentBlock[] content);

  /// Long polling: returns as soon as something has changed or maxWaitMs has elapsed.
  TurnState PollTurn(string conversationId, string turnId, int sinceSequenceNo, int maxWaitMs);

  /// Answers pending requests of any kind (tool results, approvals, questions), referenced by RequestId.
  void SubmitResponses(string conversationId, string turnId, HostResponse[] responses);

  void CancelTurn(string conversationId, string turnId);

  void EndConversation(string conversationId);

  // ---- optional, queryable delegation capabilities (see 1.3.4) ----

  /// Hands over tool sources (e.g. MCP servers) that the inner harness calls ITSELF. Only valid if
  /// GetCapabilities() lists the source kind as delegatable, otherwise NotSupportedException.
  void DelegateToolSources(string conversationId, ToolSourceDescriptor[] sources);

  /// Hands over rules by which the inner harness decides requests ITSELF (allow/deny rules, mode).
  /// Whatever the rules don't cover still arrives outside as a HostRequest.
  void DelegateRequestPolicy(string conversationId, RequestPolicy policy);

}

public class TurnState {
  public TurnStatus Status { get; set; }              // Running | AwaitingHost | Completed | Failed | Cancelled
  public string StopReason { get; set; }              // provider-neutral: EndTurn, MaxTokens, MaxTurns, Budget, Refusal, Cancelled, Error
  public int SequenceNo { get; set; }                 // for incremental polling
  public OutputItem[] NewOutputItems { get; set; }    // text deltas, thinking/status, info about internal or delegated tool calls
  public HostRequest[] PendingRequests { get; set; }  // everything the inner harness/LLM is currently waiting for
  public UsageInfo Usage { get; set; }
  public string ErrorMessage { get; set; }
}

public enum HostRequestKind { ToolCall, Approval, UserQuestion }

public class HostRequest {
  public string RequestId;           // = call id for ToolCall (tool_use_id / call_id), otherwise the protocol's request id
  public HostRequestKind Kind;
  public string ToolName;            // ToolCall: tool to call; Approval: tool the approval is requested for
  public string ArgumentsJson;       // ToolCall/Approval: inputs; UserQuestion: questions/options (elicitation schema)
  public string[] Options;           // Approval: e.g. AllowOnce, AllowAlways, Deny; UserQuestion: answer options
}

public class HostResponse {
  public string RequestId;
  public ContentBlock[] Content;     // tool-call result or answer text
  public string StructuredContentJson;
  public bool IsError;               // ToolCall: tool error, goes to the model
  public string SelectedOption;      // Approval/UserQuestion
}

public class ToolDescriptor { public string Name; public string Description; public string InputJsonSchema; public string OutputJsonSchema; public bool ReadOnlyHint; }
```

Notes on the draft:

- **Tool declarations** belong in `ConversationOptions`. Changing them later during the session is optional
  (capability flag), because not every Level-1 harness supports it.
- **`GetCapabilities()`** reports, among other things: supports tools / parallel tool calls / images in tool results /
  own internal tools (Level-1 harness) / web search / streaming granularity.
- **Internal tools of the Level-1 harness** (e.g. `Read`, `Edit`, `Bash` in Claude Code) are *not* reported as
  `ToolCall` requests (the inner harness executes them itself). They appear only as informational `OutputItem`s.
  If the harness needs approval for one, that arrives as an `Approval` request.
- The existing one-shot methods can be switched internally to the new mechanism
  (start turn → poll until `Completed`), but this is not required for step 1.
- **All requests are treated equally**: tool calls, approvals and questions to the user (`AskUserQuestion`,
  MCP elicitation, ACP `request_permission`, Codex approvals) are equal-ranking `HostRequest`s. The same pattern applies
  to each of them: the turn pauses, `PollTurn` returns `AwaitingHost`, and `SubmitResponses` resumes the turn. Likewise,
  each kind of request can either be *routed outside* or *delegated* (1.3.4).
- The orchestrator enforces the **API contract rules**: every tool call gets exactly one result (on cancel a synthetic
  error result), tool names stay within `^[a-zA-Z0-9_-]{1,64}$`, and the tool list is sorted deterministically and kept
  stable during a conversation (prompt caching).
- `ReadOnlyHint` allows parallel execution. Tool errors (`IsError`, sent to the model) are distinguished from protocol
  errors (sent to the orchestrator).
- Optionally, a **synchronous convenience method** in the orchestrator (not in the interface!), e.g.
  `RunToCompletion(conversationId, message, IToolDispatcher dispatcher)`, that encapsulates the loop.

#### 1.3.4 Two operating modes: full routing outside vs. delegation

**Principle (decided):** if an implementation can already do something *internally*, e.g. hook in additional tools or
decide approvals by rules, this is exposed as an **optional, queryable capability** on the interface. The Level-2
harness then simply **hands in** the required tools or rules and **steps out of the communication**.
If the capability is not supported, the Level-2 harness takes over **full routing outside** via `PendingRequests`.

| | Full routing outside (mandatory) | Delegation (optional, per capability) |
|---|---|---|
| Tools | declared as `ToolDescriptor` in `ConversationOptions` → call arrives as `ToolCall` request → Level 2 executes | `DelegateToolSources(...)` → the inner harness calls the source (e.g. an MCP server) itself |
| Approvals/questions | arrive as `Approval`/`UserQuestion` requests → Level 2 (policy or human) decides | `DelegateRequestPolicy(...)` → the inner harness decides by rules. Uncovered cases still arrive as requests |
| Level 2 sees | every call and every answer | only informational `OutputItem`s (as far as the implementation provides them) |
| Benefits | full control, logging and policy in one place; works with tools that are only reachable inside the Level-2 process | less latency and fewer round trips, no polling overhead |

Rules:

1. **Every implementation must support full routing outside**, even when it is laborious or brittle
   (e.g. the Claude control protocol, see 1.5.2). At the latest with self-hosting (mini LLM, 1.5.4) there is no inner
   harness left that could do anything itself. There the connector *is* the loop.
2. **Mixed mode is the normal case:** the orchestrator decides per tool source. A source is delegated only if the
   capability supports its kind **and** it is reachable from the machine running the inner harness (network,
   credentials). Everything else, e.g. in-process .NET tools, is routed outside.
3. The capability information is **fine-grained enough for this decision**, e.g.:

   ```csharp
   public class LlmCapabilities {
     public bool SupportsTools;                        // routing outside (always true for conversation-capable implementations)
     public bool SupportsParallelToolCalls;
     public HostRequestKind[] RoutableRequestKinds;    // which kinds of requests can be reported outside
     public ToolSourceKind[] DelegatableToolSources;   // e.g. McpStdio, McpHttp (empty = no tool delegation)
     public bool SupportsRequestPolicyDelegation;      // allow/deny rules, permission mode
     public bool ReportsDelegatedCalls;                // reports delegated calls as OutputItems (observability)
   }
   ```
4. Delegation must be set **before the first turn** (prompt caching; many harnesses load tools only at startup).

Mapping of the planned implementations (preliminary, verify before implementing):

| Implementation | Routing outside via | Tool delegation via | Policy delegation via |
|---|---|---|---|
| `ClaudeCliProxy` | bridge MCP server or control protocol (`can_use_tool`, SDK MCP) | `--mcp-config` (stdio/http) | `--allowedTools`, `--disallowedTools`, `--permission-mode` |
| `CopilotCliProxy` | ACP (`request_permission`) + bridge MCP | ACP `session/new.mcpServers` | `--available-tools`/`--excluded-tools` (global), `--allow-tool`/`--deny-tool` |
| `OpenAiLLMConnector` | function calling | possibly the API's remote MCP tool (publicly reachable servers only, no LAN) | – |
| Mini LLM (Ollama) | function calling (`/api/chat`) | – | – |

### 1.4 The Level-2 harness itself

| Building block      | Responsibility |
|---------------------|----------------|
| **Orchestrator**    | Agent loop: query capabilities, assign tool sources to routing or delegation (1.3.4), start the turn, poll, dispatch `PendingRequests` (tool calls to the ToolRegistry, in parallel where allowed; approvals and questions to policy or a human), resubmit answers, enforce limits (max. iterations, timeouts, cost). |
| **ToolRegistry**    | Collects `ToolDescriptor`s from several `IToolProvider`s and routes calls by name. |
| **Local tools**     | .NET interfaces/methods as tools. JSON schemas and descriptions can be generated from signatures and XML comments (like `XmlCommentAccessExtensions`/`DynamicAiServiceFactory`). |
| **LAN tools**       | Calls to remote services via UJMW REST (contract interface + `DynamicClientFactory`). |
| **MCP client** (later) | `tools/list` → `ToolDescriptor`, `tools/call` → `HostResponse`. Candidate: the official C# SDK (`ModelContextProtocol` NuGet). |
| **Policy/approval** | Allow/deny lists, optional human approval per tool (could connect to `IPromptingUIBackend`). |
| **Logging/trace**   | Complete log of all turns, calls and results (SmartStandards.Logging), important for debugging and traceability. |

Eventually the Level-2 harness itself can be exposed as `IConversationalLLM` (or as an MCP server),
which makes harnesses **stackable**.

### 1.5 Planned connectors

#### 1.5.1 `OpenAiLLMConnector` (existing, to be extended)

- Implement the handshake via the **Responses API** (`function_call` / `function_call_output`,
  chaining via `previous_response_id`).
- Along the way: make model name, endpoint and limits configurable (currently hard-coded `gpt-4o`),
  handle async and timeouts properly, and stop swallowing errors (`catch { return null; }` in the image APIs).
- **Configurable base URL + chat-completions mode**: the same connector then also covers all
  OpenAI-compatible local servers (see 1.5.4).

#### 1.5.2 `ClaudeCliProxy` (locally running proxy for Claude Code CLI)

Runs on the machine where Claude Code is installed and logged in, and exposes `IConversationalLLM`
(in-process or via UJMW in the LAN).

- **Driving the CLI**: headless mode `claude -p` with `--input-format stream-json --output-format stream-json`
  (one long-lived process per conversation, multi-turn via stdin), sessions via `--session-id`/`--resume`.
- **Injecting our own tools, the "bridge MCP server"**: the proxy starts a small local MCP server (stdio or HTTP) and
  passes it via `--mcp-config`. This server announces exactly the tools declared in `BeginConversation`. When Claude
  calls one of them, the bridge server **blocks** the MCP request, puts a `ToolCall` request into the pending queue
  (→ `PollTurn` returns `AwaitingHost`) and only answers once `SubmitResponses` arrives. This fully preserves the inner
  harness's original handshake.
- **Restricting internal tools**: derive `--allowedTools` / `--disallowedTools` / `--permission-mode` from the policy.
- Note: the CLI's MCP tool timeouts (configurable via environment variable) must exceed the expected tool runtime plus
  polling latency.
- Use `--bare` for reproducible behavior, so that no hooks, plugins or MCP servers are loaded from the environment.
  Caveat: then only `ANTHROPIC_API_KEY` works, not the subscription login. For unattended runs use `--permission-prompts none`.
- Approvals for internal tools can be redirected to a tool of the bridge MCP server via `--permission-prompt-tool`
  and then arrive at the Level-2 harness as `Approval` requests.
- Speaking the **Agent SDK control protocol directly in .NET** (`control_request`/`control_response` over stdin/stdout
  with `can_use_tool`, `hook_callback` and in-process SDK MCP servers) is **explicitly wanted**, even though it is only
  officially documented through the TS/Python SDK and therefore brittle. It is the most complete way to route
  everything outside (tool calls, approvals and questions over one channel, without an extra process). Safeguards:
  feature detection via the `capabilities` array in `system/init`, a pinned CLI version and protocol contract tests
  with recorded fixtures (details: section 1.10 and the skill `ai-harness-development`).
- **Delegation** (1.3.4): tool sources that Claude can reach itself are hooked in directly via `--mcp-config`,
  approval rules via `--allowedTools`/`--disallowedTools`/`--permission-mode`.

#### 1.5.3 `CopilotCliProxy` (locally running proxy for GitHub Copilot CLI)

- **Recommended: ACP server mode** `copilot --acp` (stdio) or `--acp --port <n>` (TCP, localhost), public preview since 2026-01.
  The proxy is an ACP client: `session/new` with `cwd` and `mcpServers` (→ **the same bridge MCP server** as in 1.5.2),
  `session/prompt`, `session/update` notifications (→ `OutputItem`s), `session/request_permission` (→ `Approval` request),
  `session/cancel`. Tool sources that Copilot can reach itself are **delegated** through the same `mcpServers` (1.3.4).
- Restrict internal tools via `--available-tools` / `--excluded-tools`; note that this applies globally per server process.
- Fallback: prompt mode `copilot -p … --output-format json` (JSONL) with `--allow-tool` / `--deny-tool`.
- ACP is vendor-neutral (adapters for Claude Code and others exist) and therefore a candidate for a **shared** proxy
  foundation for several CLI harnesses. Similarly, OpenAI Codex offers its own JSON-RPC protocol (`codex app-server`)
  with client-executed "dynamic tools".

> The exact CLI flags of both tools change frequently and must be verified against the current documentation before
> implementation.

#### 1.5.4 Local mini LLM (self-hosted example)

Goal: fully offline, runs on normal developer hardware, **reliable tool calling**.

There is no inner harness here: the connector **is** the loop, and there is no delegation. All requests are routed
outside. This is the reference case for full routing (1.3.4, rule 1).

**Runtime** – recommendation: **Ollama** (easy Windows installation, OpenAI-compatible endpoint `/v1/chat/completions`
including `tools`, model management via `ollama pull`). Ideally the `OpenAiLLMConnector`, extended by base URL and
chat-completions mode, is sufficient; a dedicated connector is only needed for extras.
Alternatives:

| Runtime            | Pro | Contra |
|--------------------|-----|--------|
| **Ollama**         | very easy, OpenAI-compatible, tool calling for many models | fewer tuning options |
| llama.cpp `llama-server` | maximally lean and controllable, tool calling via `--jinja` | more manual work |
| LM Studio          | convenient GUI, OpenAI-compatible server | not open source |
| Microsoft Foundry Local | close to Windows/.NET, OpenAI-compatible, ONNX/NPU | smaller model selection |

**Model candidates** (focus on tool calling, roughly by hardware requirements):

| Model                  | Size  | Assessment |
|------------------------|-------|------------|
| **Qwen3-4B-Instruct-2507** (or Qwen3 8B) | ~3–6 GB (Q4) | **Recommended start**: leading below 7B in BFCL v4, strong with parallel calls |
| Gemma 4 E4B            | ~3–4 GB | native function-call tokens, Apache 2.0 |
| Phi-4-mini (3.8B)      | ~3 GB | fastest inference, good multi-step planning |
| gpt-oss-20b            | ~14 GB | much stronger (reasoning + tools), needs ~16 GB (V)RAM |

Note: for tool calling with streaming, use Ollama's native endpoint `/api/chat`. According to community reports,
`/v1/chat/completions` loses tool-call data while streaming. Alternatively, don't stream.

Approach: start with **Qwen3-4B-Instruct-2507** or Qwen3 8B on Ollama, run a small **tool-calling benchmark**
(the same scenarios as the tests in 1.7) against all candidates, then decide. Re-check the model landscape before
implementing; it changes quickly.

### 1.6 Roadmap

| Phase | Content | Result |
|-------|---------|--------|
| 0 | This idea, review, settle the open points | agreed interface draft |
| 1 | `IConversationalLLM` + DTOs (incl. `HostRequest`/`HostResponse`, capabilities), orchestrator with routing outside, ToolRegistry with local .NET tools, **fake LLM** (scriptable mock for all request kinds, optionally with simulated delegation) | agent loop testable at no cost |
| 2 | Extend `OpenAiLLMConnector` with function calling (Responses API; base URL + chat-completions mode) | first real end-to-end run |
| 3 | Connect the local mini LLM via Ollama, choose the model by benchmark | offline operation, reference for full routing |
| 4 | `ClaudeCliProxy`: bridge MCP server, then the control protocol in .NET; delegation via `--mcp-config`; UJMW hosting in the LAN | Level-1 harness usable as an "LLM" |
| 5 | `CopilotCliProxy` as ACP client (reuse bridge MCP; delegation via `mcpServers`) | second Level-1 harness |
| 6 | MCP client as `IToolProvider` (third-party MCP servers as tool sources); the orchestrator decides per source between routing and delegation | tools from the MCP ecosystem |
| 7 | Optional: expose the Level-2 harness itself as `IConversationalLLM`/MCP server | stackable harnesses |

### 1.7 Tests / quality assurance

- **Handshake conformance tests** against every connector (same scenarios):
  no tool needed · exactly one tool call · several parallel calls · follow-up calls over several rounds ·
  tool returns an error (`IsError`) · tool timeout · cancel in the middle of a turn · `max_tokens` limit ·
  approval granted/denied · question answered · mixed mode (one tool delegated, one routed outside) ·
  delegation requested but not supported (`NotSupportedException`).
- Fake LLM for fast, deterministic unit tests; real providers as `[Ignore]`/category tests (as before).
- Record raw requests/responses per provider as fixtures to detect mapping regressions.

### 1.8 Security

- Tools on the PC or in the LAN are powerful: **allow lists**, optional **human approval**,
  argument validation against the JSON schema.
- Level-1 harnesses bring their own dangerous tools (shell, file access) → restrictive default policy,
  fixed working directory.
- Expose the proxies' UJMW endpoints only with authentication and never unprotected in the LAN
  (the proxies run with the respective user's login/subscription).
- Tool results may contain prompt injection → mark them in the log, enforce approval for sensitive tools.

### 1.9 Open points

1. Separate interface (`IConversationalLLM`) vs. extending `ICommonLLM` directly? (Recommendation: separate interface)
2. Where does the session state live when hosted via UJMW (in memory with expiry vs. `IPromptingSessionStore`)?
3. Target platforms for the new parts: also `net48` or only `net8.0`/`net10.0`? (CLI proxies and the MCP SDK favor modern .NET)
4. How much of the internal Level-1 activity (thinking, internal tool calls) should be visible as `OutputItem`s?
5. ~~Bridging approach for the CLIs~~ → **decided** (1.3.4): delegation as an optional capability, full routing outside
   as mandatory. The Claude control protocol will be implemented despite the risk of breakage. Only the order
   bridge MCP → control protocol remains open.
6. Concrete hardware for the mini LLM (GPU/VRAM) → determines the model size.

### 1.10 Background: how modern agent harnesses work internally (research 2026-09)

The detailed technical summary (protocols, loop internals, flags) is maintained in the AI skill
[`ai-harness-development`](../.agents/skills/ai-harness-development/SKILL.md) and its reference files. The most
important findings for humans:

- **All major harnesses run the same loop:** call the model, execute the requested tools, return the results, repeat
  until the model answers without a tool call. The full history is sent with every call, and prompt caching keeps the
  cost in check.
- **Control from outside:** Claude Code (Agent SDK control protocol), OpenAI Codex (`codex app-server`) and
  GitHub Copilot CLI (`copilot --acp`, Agent Client Protocol) offer a bidirectional JSON protocol over stdio.
  The harness sends its own requests to the host (tool execution, approvals, questions), and the turn pauses until
  the answer arrives. `PollTurn`/`SubmitResponses` maps exactly this pattern in a UJMW-compatible way.
- **All of them can hook in extra tools via MCP:** Claude via `--mcp-config`, Copilot via `mcpServers` in ACP `session/new`,
  Codex via configuration or "dynamic tools". This is the basis of the delegation capability (1.3.4).
- **Internal mechanics we adopt:**
  - Only read-only tools run in parallel.
  - Every tool call gets exactly one result, even on cancel.
  - Tool errors go back to the model as results, so it can correct itself.
  - The tool list stays stable (prompt caching).
  - Long histories are summarized automatically.
  - Approvals pass through a fixed chain of checks (hooks → rules → mode → ask).
- **Local models** with good tool calling (BFCL v4, as of 2026-09): Qwen3-4B-Instruct-2507, Gemma 4 E4B, Phi-4-mini;
  runtime Ollama with its native `/api/chat`.

#### Sources

**Official – Anthropic**
- [Run Claude Code programmatically (headless)](https://code.claude.com/docs/en/headless)
- [How the agent loop works (Agent SDK)](https://code.claude.com/docs/en/agent-sdk/agent-loop)
- [Give Claude custom tools (Agent SDK)](https://code.claude.com/docs/en/agent-sdk/custom-tools)
- [Configure permissions (Agent SDK)](https://code.claude.com/docs/en/agent-sdk/permissions)
- [Define tools](https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools)
- [Handle tool calls](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls)
- [Effective context engineering for AI agents](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)
- [Effective harnesses for long-running agents](https://anthropic.com/engineering/effective-harnesses-for-long-running-agents)

**Official – OpenAI**
- [Unrolling the Codex agent loop](https://openai.com/index/unrolling-the-codex-agent-loop/)
- [Unlocking the Codex harness: how we built the App Server](https://openai.com/index/unlocking-the-codex-harness/)
- [Codex app-server README](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [Function calling guide](https://developers.openai.com/api/docs/guides/function-calling)

**Official – GitHub**
- [Copilot CLI programmatic reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference)
- [Copilot CLI ACP server](https://docs.github.com/en/copilot/reference/copilot-cli-reference/acp-server)
- [ACP support in Copilot CLI – changelog 2026-01-28](https://github.blog/changelog/2026-01-28-acp-support-in-copilot-cli-is-now-in-public-preview/)
- [Adding MCP servers for Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers)

**Specifications**
- [MCP spec 2025-11-25 – Tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)
- [Agent Client Protocol – Overview](https://agentclientprotocol.com/protocol/overview) · [Prompt Turn](https://agentclientprotocol.com/protocol/prompt-turn) · [Session Setup](https://agentclientprotocol.com/protocol/session-setup)

**Local models**
- [Ollama – Tool calling](https://docs.ollama.com/capabilities/tool-calling) · [Ollama – OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility)
- [Berkeley Function Calling Leaderboard V4](https://gorilla.cs.berkeley.edu/leaderboard.html)
- [Phi-4-Mini vs Gemma 4 vs Qwen3-4B: Tool Calling (Ertas AI)](https://www.ertas.ai/blog/on-device-tool-calling-2026-qwen3-gemma4-phi4)

**Unofficial / third-party (use with care)**
- [Claude Code from Source – Ch. 5 The Agent Loop](https://claude-code-from-source.com/ch05-agent-loop/)
- [Inside the Claude Agent SDK: stdin/stdout communication](https://buildwithaws.substack.com/p/inside-the-claude-agent-sdk-from)
- [Codex Knowledge Base – Agent loop deep dive](https://codex.danielvaughan.com/2026/03/28/codex-agent-loop-deep-dive/)
- [Building on codex app-server (Gist)](https://gist.github.com/oneryalcin/ee2c27e2d8aa040da8fbe7eebcc2ecea)
- [openclaw PR: native Ollama /api/chat for streaming + tool calling](https://github.com/openclaw/openclaw/pull/11853)

## 2. Further candidate abstractions (external, "also interesting")

- `ILogEntryRepository` (SmartStandards.Logging.Centralized)
- `IUniversalWorkItemRepository`
- `IAfsRepository`
- `IServerCommands` (+ a separate MCP adapter that exposes the commands themselves more effectively)
