# Protocol reference (state 2026-09-25)

Wire-level facts needed to implement connectors and proxies. *(unofficial)* = reverse-engineered or third-party. Verify before use.
Source links: `doc/ideas.md`, section 1.10 "Quellen".

## Stop reasons across protocols

| Meaning | Anthropic | OpenAI Responses | Chat Completions / Ollama | ACP | Claude Agent SDK result |
|---|---|---|---|---|---|
| done | `end_turn` | no `function_call` items | `stop` | `end_turn` | `success` |
| wants tool | `tool_use` | `function_call` items | `tool_calls` | (internal) | (internal) |
| token limit | `max_tokens` | `incomplete` | `length` | `max_tokens` | – |
| server tool paused | `pause_turn` | – | – | – | – |
| refusal | `refusal` | refusal content | – | `refusal` | `stop_reason: refusal` |
| loop limit | – | – | – | `max_turn_requests` | `error_max_turns`, `error_max_budget_usd` |
| cancel/crash | – | – | – | `cancelled` | `error_during_execution` |

## Anthropic Messages API

- Tool: `name` (`^[a-zA-Z0-9_-]{1,128}$`), `description` (the most important quality factor: 3–4+ sentences), `input_schema`,
  optional `input_examples`, `strict`, `cache_control`, `defer_loading`.
- Response: `stop_reason: tool_use` plus `tool_use {id, name, input}` blocks (often preceded by text).
- Reply: user message with `tool_result {tool_use_id, content (string | text/image/document/search_result blocks), is_error}`.
  - Must **immediately** follow the assistant tool-use message. `tool_result` blocks come **first**, text after. All results of a round go in one message.
  - If the assistant turn also has an unresolved `server_tool_use`, the user message must contain only `tool_result` blocks.
- `tool_choice`: `auto` | `any` | `tool` | `none`. Newest models (Opus 5.5, Fable 5.1) reject `any`/`tool` (use `strict: true`).
- Invalid calls: return `is_error` with an explanation. The model typically retries 2–3 times.

## OpenAI Responses API

- Tool: `{type:"function", name, description, parameters, strict}`.
- Output item `function_call {id, call_id, name, arguments(JSON string)}` → input item `function_call_output {call_id, output}`.
- `parallel_tool_calls`, `tool_choice`: `auto` | `required` | `{type:function,name}` | `allowed_tools`.
- Reasoning items must be passed back with the outputs. Continuation via `previous_response_id` or full history (stateless, as Codex does).
- Streaming: `response.output_item.added`, `response.function_call_arguments.delta|done`.

## Chat Completions / Ollama

- `message.tool_calls[] {id, function{name, arguments}}` → `role:"tool"` message (`tool_call_id`, Ollama native: `tool_name`).
- Ollama: parallel and streamed tool calls supported. Accumulate `thinking`/`content`/`tool_calls` across chunks.
  Use native `/api/chat`. *(unofficial)* `/v1` streaming drops tool calls.

## Claude Code CLI

- Headless: `claude -p`, `--output-format text|json|stream-json` (+ `--verbose`, `--include-partial-messages`),
  `--input-format stream-json` for a long-lived multi-turn process, `--resume <id>`, `--continue`.
- `--bare`: skip ambient hooks, skills, plugins, MCP, CLAUDE.md. Auth only via `ANTHROPIC_API_KEY`/`apiKeyHelper`, no subscription login.
- Tools/permissions: `--allowedTools "Read,Bash(git diff *)"`, `--disallowedTools`,
  `--permission-mode default|acceptEdits|plan|dontAsk|auto|bypassPermissions`, `--permission-prompts none`,
  `--permission-prompt-tool <mcp tool>` (route permission prompts to an MCP tool → our `Approval` requests).
- `--mcp-config <file|json>`. In `-p` mode Claude waits up to `MCP_TIMEOUT` (default 30 s) for servers. Tools are named `mcp__<server>__<tool>`.
- Stream message types: `system` (`init`, `compact_boundary`, `api_retry`, `permission_denied`, …), `assistant` (one content block each),
  `user` (tool results), `stream_event`, `result` (`subtype`, `result`, `total_cost_usd`, `usage`, `session_id`, `stop_reason`).
  `system/init` carries tools, `mcp_servers`, `mcp_server_errors` and a `capabilities` array for feature detection.
  Subagent messages carry `parent_tool_use_id`.
- SIGTERM leaves an unfinished turn (exit 143). Stop with SIGINT / `interrupt`.
- **Agent SDK control protocol** *(unofficial in detail)*: JSON lines over stdin/stdout, multiplexed by `request_id`:
  ```json
  {"type":"control_request","request_id":"req_1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"}}}
  {"type":"control_response","request_id":"req_1","response":{"subtype":"success","response":{"behavior":"allow"}}}
  ```
  Known subtypes: `initialize` (registers hooks and **SDK MCP servers**), `can_use_tool`, `hook_callback`, `interrupt`,
  set permission mode, and MCP message tunneling. SDK MCP tools run **in the host process**: the CLI sends a control request
  and the host executes it. This is the target handshake for full routing without a bridge process.
- SDK tool result shape: `{content:[text|image|audio|resource|resource_link], structuredContent?, isError?}`. Annotations:
  `readOnlyHint` (enables parallelism), `destructiveHint`, `idempotentHint`, `openWorldHint`.

## GitHub Copilot CLI

- Prompt mode: `copilot -p`, `--output-format json` (JSONL), `--model`, `--agent`, `--allow-tool`, `--deny-tool`, `--allow-all-tools`.
  Filters: `shell(git:*)`, `write(path)`, `url(domain)`, `<MCP-SERVER>(tool)`. Workspace MCP servers load in `-p` only in trusted
  dirs (`GITHUB_COPILOT_PROMPT_MODE_WORKSPACE_MCP=true`). Env: `COPILOT_ALLOW_ALL`, `COPILOT_MODEL`, `COPILOT_GITHUB_TOKEN`.
- **ACP server** (public preview since 2026-01-28): `copilot --acp` (stdio, default) or `--acp --port <n>` (TCP on 127.0.0.1,
  multiple clients), NDJSON. `--available-tools`, `--excluded-tools` (global per process), `--effort`. `session/new` accepts
  `mcpServers`. Permissions via request_permission (refuse: `{outcome:{outcome:"cancelled"}}`). BYOK providers work without
  GitHub login. Interactive slash commands (`/diff`, `/resume`, `/login`, …) are unavailable.

## OpenAI Codex app-server

- `codex app-server`: JSON-RPC 2.0 without the `"jsonrpc"` member, JSONL over stdio (WebSocket experimental).
- `initialize` → `initialized`, then `thread/start|resume|fork`, `turn/start|interrupt|steer`. Notifications: `item/started`,
  `item/agentMessage/delta`, `item/completed`, `turn/completed`. Items: `agentMessage`, `reasoning`, `commandExecution`,
  `fileChange`, `mcpToolCall`, `dynamicToolCall`.
- Approvals are server→client requests. The turn pauses until `accept`/`decline`/`cancel`.
- `dynamicTools` on `thread/start` (needs `capabilities.experimentalApi`) = client-executed tools. The exact invocation method name is still unverified.

## Agent Client Protocol (ACP)

- JSON-RPC 2.0, stdio mandatory. `initialize` → (`authenticate`) → `session/new` | `session/load` → `session/prompt` … `session/cancel`.
- `session/new`: `cwd`, `mcpServers` (stdio: `name, command, args, env`; `http`/`sse` if the agent advertises `mcpCapabilities`).
- `session/update` kinds: `agent_message_chunk`, `agent_thought_chunk`, `user_message_chunk`, `tool_call` (`toolCallId`, `title`,
  `kind`, `status: pending`), `tool_call_update` (`in_progress` → `completed`), `plan`, `usage_update`,
  `available_commands_update`, `current_mode_update`, `config_option_update`, `session_info_update`.
- Agent → client: `session/request_permission`, optional `fs/read_text_file`, `fs/write_text_file`, `terminal/*`, `elicitation/create`.
- Cancel: the agent may still send updates but must do so before answering `session/prompt` with `cancelled`.

## Model Context Protocol (spec 2025-11-25)

- `tools/list` (paginated; `name`, `title`, `description`, `inputSchema`, `outputSchema`, `annotations`, `execution.taskSupport`),
  `tools/call` → `{content[], structuredContent?, isError}`, `notifications/tools/list_changed` (capability `tools.listChanged`).
- Names: 1–128 chars, `A-Z a-z 0-9 _ - .`, case-sensitive.
- Errors: JSON-RPC protocol errors (e.g. `-32602` unknown tool) vs. execution errors (`isError: true`, give them to the model).
- Annotations are untrusted unless the server is trusted. Clients should confirm sensitive calls, show inputs, set timeouts and log calls.
- No-parameter tools: `{"type":"object","additionalProperties":false}`.
- C# SDK: NuGet `ModelContextProtocol`.
