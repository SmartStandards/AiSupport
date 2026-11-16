# Epic: Providing Skills and Tools to the Model (Native First, Layer-2 Proxy as Fallback)

> Status: **concept, not yet an active requirement.** Nothing in this epic is implemented automatically. Accepted parts
> move into [1-requirements.md](../1-requirements.md) and are planned as concrete steps before implementation.
> Builds on [epic-interactive-prompting.md](epic-interactive-prompting.md) (middle layer, `contextStore`) and on the
> provider capabilities (CAP-1..6) and the Copilot session mode (COP-1..8) in the requirements.

## 1. Abstract and motivation

A chat model becomes useful in a specific domain when it gets two kinds of additions:

- **Skills**: written knowledge and instructions, loaded on demand ("how we build X here").
- **Tools**: functions the model can call ("query the ticket system", "read the CalDAV calendar").

Both can reach the model in two ways:

1. **By reference (native).** The provider or the inner harness gets a public URL (skill URL, MCP server URL) and
	 resolves it itself. The Layer-2 harness only hands the reference in and then steps out.
2. **By proxy (Layer-2).** The Layer-2 harness resolves the source itself (embedded resource, local file, private URL,
	 Git repository, private MCP server, in-memory UJMW service). It then gives the content or the call results to the
	 model. The model never needs to reach the source.

The binding rule from the harness skill (decision 7: native first, emulation only as fallback) applies here too. If the
provider supports a reference natively (queryable via `LLMProviderInfo.Capabilities`) and the source is reachable from
the provider, the reference is preferred. Otherwise the Layer-2 harness acts as proxy. The user can opt out of each
native capability in the settings UI, which switches the provider to the proxy path.

The first half of this epic covers skills, the second half covers tools. Both share one design (source providers, a flat
catalog, activation, pinning into the context, recovery).

### 1.1 Positive differentiation

| Aspect | Typical AI clients | This epic |
|---|---|---|
| Skill sources | Fixed folder (`~/.claude/skills`, `.github/skills`) of one product | 4 pluggable source kinds, multiple instances each, one flat catalog for every provider |
| Provider switch | Skills/tools bound to one vendor | Same catalog for OpenAI, Copilot, local LLMs; native path where possible, proxy otherwise |
| Private sources | Model must reach them itself | Layer-2 harness proxies them; credentials never leave the harness |
| Recovery | Loaded skills/approvals are lost with the session | Loaded skills and approvals are persisted with the chat and replayed on rebuild |
| In-memory services | Need an extra MCP server process | UJMW service interfaces are exposed as tools directly from memory |

## 2. Standards to follow

The user asked to follow an existing industry norm for the textual harness communication if there is one. Findings
(state of the research in this repository; **verify the details before implementation, they change often**):

| Topic | Norm | Consequence |
|---|---|---|
| Skill format | **Agent Skills** open standard (agentskills.io, originated at Anthropic, adopted by Claude Code, GitHub Copilot, OpenAI Codex and others): one folder per skill with `SKILL.md` = YAML front matter (`name`, `description`, optional `license`, `metadata`, `allowed-tools`) + Markdown body, optional `scripts/`, `references/`, `assets/` | All four skill source kinds deliver exactly this shape. No custom format. |
| Skill discovery | **Progressive disclosure**: level 1 = only `name` + `description` of all skills in the system prompt. Level 2 = the `SKILL.md` body is loaded when relevant. Level 3 = referenced files are loaded on demand | The catalog contains `name` + `description` only. Bodies are loaded on activation. |
| Catalog rendering | The Agent Skills integration guide suggests an `<available_skills>` XML block (`<skill><name/><description/><location/></skill>`) in the system prompt | The Layer-2 harness renders the catalog in this form. |
| Skill activation | **Not standardized as text.** The spec leaves activation to the client: either the model reads the file with a file tool, or the client offers a dedicated tool (Claude Code: tool `Skill`). Slash commands (`/skill-name`) are *user*-side, not model-side. | Primary path: a tool call (`load_skill`). Text fallback see 4.3 and 4.6. |
| Text-based tool calls (models without native function calling) | De-facto norm of the open-weight ecosystem: **Hermes/ChatML tool format** (`<tools>` JSON schemas in the system prompt, `<tool_call>{"name":...,"arguments":{...}}</tool_call>` in the answer, `<tool_response>` as reply). Used by the chat templates of Qwen 2.5/3, Hermes and many Ollama models. | Canonical text format of the Layer-2 harness for skills and tools (4.6). |
| Tools | **Model Context Protocol** (MCP, spec 2025-11-25): `tools/list`, `tools/call`, tool annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`), Streamable HTTP transport, OAuth 2.1 authorization | All tool sources are normalized to MCP tool descriptors. Annotations drive the approval interceptor. |
| Remote MCP by reference | OpenAI Responses API: tool type `mcp` (`server_url`, `headers`, `require_approval`, `allowed_tools`). Anthropic Messages API: MCP connector (`mcp_servers`, beta). ACP (Copilot): `session/new.mcpServers` | Capability `RemoteMcpServers` per provider. |
| Approvals | ACP `session/request_permission` (options `allow_once`, `allow_always`, `reject_once`, `reject_always`), OpenAI `mcp_approval_request` / `mcp_approval_response` | The interceptor uses the same vocabulary (`AllowOnce`, `AllowAlways`, `RejectOnce`, `RejectAlways`). |

## 3. Overview

```
						+-------------------- Layer-2 harness (CheapPromptingUIBackend / later orchestrator) --------------------+
						|                                                                                                       |
 Skill      |  ISkillSource (n instances) --> SkillCatalog (flat, unique names) --+--> catalog in system prompt      |
 |    load_skill / <tool_call>    |
						|                                                                   +--> pinned skill messages (chat)  |
						|                                                                                                       |
 Tool       |  IToolSource (n instances) --> ToolCatalog (flat, unique names) --+--> native: MCP URL to provider    |
 sources -->|   RemoteMcpReference | McpProxy | UjmwInMemory                    +--> proxy: function tools ->       |
						|                                                                         ToolApprovalInterceptor -> call |
						+-------------------------------------------------------------------------------------------------------+
```

## 4. First half: skills

### 4.1 Skill sources

Four source kinds. Each can be registered any number of times (for example two Git repositories and one folder). All
sources deliver the same flat entries.

| Kind | Class (working title) | Input | Typical use |
|---|---|---|---|
| Embedded resource | `EmbeddedResourceSkillSource` | Assembly + resource name prefix | Skills shipped with an application |
| Local file/folder | `FileSystemSkillSource` | Folder with `*/SKILL.md` (or a single `SKILL.md`) | Developer machine, `~/.agents/skills`, repository `.agents/skills` |
| Online URL | `UrlSkillSource` | URL of a `SKILL.md` or of an index; optional credentials | Public or private web content, proxied by the harness |
| Git repository | `GitRepositorySkillSource` | Clone URL, branch/ref, sub path (default `.agents/skills`, `skills`, `.github/skills`), optional credentials | Shared skill repositories of a team |

Git access is done by a small out-of-the-box provider on top of the **command-line `git`** (no library):

- `git clone --depth 1 --filter=blob:none --sparse` into a cache folder below `%LOCALAPPDATA%\SmartStandards\AiSupport\SkillCache\<SourceId>`,
  `git sparse-checkout set <sub path>`, refresh via `git fetch --depth 1` + `git reset --hard origin/<ref>` (on demand
  and at most every N minutes).
- Non-interactive: `GIT_TERMINAL_PROMPT=0`, timeouts, process tree killed on cancel (same pattern as the Copilot connector).
- Credentials: if the source has stored credentials, they are passed per call through a credential helper of the
  harness (`-c credential.helper=` reset + our helper), never in the URL or the command line. Without stored
  credentials, the git configuration of the user applies (e.g. Git Credential Manager).
- Missing `git` executable: clear error message with an installation hint (same pattern as the Copilot installation).

### 4.1.1 Credential storage

First implementation (user-approved): **Windows API**. `ICredentialStore` with the default implementation
`WindowsCredentialStore`, which stores secrets per user in the Windows Credential Manager (target name
`SmartStandards.AiSupport/<SourceId>`). It is used for Git sources, URL sources and MCP proxy sources. The settings file
only contains a reference and a hint (e.g. `****abcd`), never the secret. The interface allows a cross-platform
implementation later (e.g. DPAPI-free encrypted file or AuthTokenHandling for OAuth 2.1 tokens).

```csharp
public interface ISkillSource {

	/// <summary>Stable id of this source instance (used for persistence and for name conflicts).</summary>
	string SourceId { get; }

	/// <summary>Level 1: name + description of every skill (cheap, cached).</summary>
	SkillDescriptor[] GetSkills();

	/// <summary>Level 2: the complete SKILL.md (front matter + body).</summary>
	string GetSkillContent(string skillName);

	/// <summary>Level 3: a file referenced by the skill (relative path inside the skill folder).</summary>
	string GetSkillResource(string skillName, string relativePath);

}

public class SkillDescriptor {
	public string Name { get; set; }          // from the front matter, unique within the catalog
	public string Description { get; set; }   // from the front matter, shown to the model
	public string SourceId { get; set; }
	public string PublicUrl { get; set; }     // only set if the skill is publicly reachable (native path)
	public string ContentHash { get; set; }   // detects changes between load and recovery
}
```

`SkillCatalog` merges all sources into one flat list. Name conflicts are resolved deterministically: the first
registered source wins, and later duplicates get the suffix `@<SourceId>`. The catalog is sorted and stable during a
chat (prompt caching).

### 4.2 Native path vs. proxy path for skills

| Situation | Path |
|---|---|
| Provider/inner harness can load skills from a URL **and** the skill has a `PublicUrl` **and** capability `NativeSkills` is not opted out | Hand in the URL/reference; the provider loads and activates the skill itself |
| Inner harness has its own skill folder mechanism (e.g. Copilot CLI / Claude Code reading `SKILL.md` folders) **and** the source is a local folder | Point the inner harness to the folder (ACP working directory or configuration) |
| All other cases | Proxy: the catalog goes into the system prompt and the content is delivered by the harness on activation |

### 4.3 Activation

1. **Native tool call (primary).** If the provider supports function calling, the harness declares the tools
	 `load_skill(name)` and `read_skill_resource(name, path)`. The call result is the skill content.
2. **Text protocol (fallback for providers without function calling, e.g. prompt-mode CLIs).** The hidden harness prompt
	 (4.6) tells the model to emit `<tool_call>{"name":"load_skill","arguments":{"name":"<name>"}}</tool_call>`.
	 The short form `/REQUESTSKILL "<name>"` on a line of its own is accepted as an alias.
3. **User-side.** The user can also load a skill explicitly (UI: skill picker, or typing `/skill <name>`).

Rules for the text protocol:

- Recognized only outside of code blocks. `<tool_call>` blocks must contain valid JSON; slash aliases must be the only
	content of their line.
- The recognized block/line is removed from the visible answer. The harness posts the result as the next message
	(`<tool_response>`) and lets the model continue the same turn.
- Unknown names produce a short harness message with the list of valid names. No error is raised.
- At most N activations/tool calls per turn (default 5) to prevent loops.

### 4.4 Pinning and recovery

A loaded skill must always be part of the context from then on:

- The chat stores a **pinned skill entry** (`name`, `SourceId`, `ContentHash`, the content at load time).
- `BuildSelfMaintainedContext` always places the pinned skills directly after the system prompt, also when a
	compression baseline exists. Compression never removes pinned skills.
- If the provider holds the context (`ProviderSideContext`), the skill content is sent only once. When the
	`contextStore` becomes invalid, the rebuild includes the pinned skills again.
- If the content of a source changed after loading (`ContentHash` differs), the chat keeps the stored version. The UI
	can offer "reload skill".

### 4.5 UI

- Skill activation produces no chat bubble. Instead the UI shows a thin grey line: `SKILL 'xxxx' loaded`.
- New message kind in `ChatMessage` (for example `Kind = "SkillLoaded"`). Old clients that don't know the kind can
	ignore it, following the convention credo: conventions may only enrich behavior.
- Settings dialog: list of skill sources (add/remove, kind, URL/path, credentials state), similar to the provider
	capabilities.

### 4.6 Hidden harness prompt and how the big harnesses do it

The model needs an invisible initial prompt that tells it which skills and tools exist and how to request them.
How the major harnesses solve this (state of knowledge, **verify against current sources before implementation**):

| Harness | Catalog placement | Skill activation | Loaded content | UI |
|---|---|---|---|---|
| Claude Code / Claude Agent SDK | Skill names + descriptions are listed inside the description of a dedicated tool `Skill` (`<available_skills>`) | Native tool call `Skill` with the skill name | Injected as a hidden (meta) user message: base directory + `SKILL.md` body; stays in the history | Short status line ("Loaded skill"), no bubble |
| OpenAI Codex CLI | Developer instructions contain a skills section (name, description, path of `SKILL.md`) | The model reads the file itself with its shell/read tool; users can mention `$skill-name` | Normal tool result | Tool activity line |
| GitHub Copilot (CLI / VS Code) | Skills section in the system prompt (name, description, location) | Reads `SKILL.md` via its file tool (CLI: dedicated skill tool) | Normal tool result | Tool activity line |
| Agent Skills integration guide | `<available_skills><skill><name/><description/><location/></skill></available_skills>` in the system prompt | Filesystem-based (read file) or tool-based (client tool) | – | – |
| Open-weight models (Hermes/Qwen templates, Ollama) | `<tools>` block with JSON schemas in the system prompt | `<tool_call>{json}</tool_call>` in the answer text | `<tool_response>` message | – |

Common pattern, which we adopt 1:1:

- Instructions ("AGENTS.md"-like rules) and the **catalog with names and descriptions only** live in the stable part of
	the system prompt. Bodies are never pre-loaded.
- Activation is a **tool call**, never a slash command: slash commands are user-side in every harness.
- Loaded content is a hidden message that stays in the history (= our pinning), the UI shows a status line only.

Layout of the hidden harness prompt (rendered by the Layer-2 harness, never shown in the UI, rebuilt identically on
recovery and sorted deterministically for prompt caching):

```text
<harness_instructions>
You can load skills (instructions for specific tasks) and use tools.
Load a skill before you work on a task it describes. Never guess the content of a skill.
[native function calling]  Use the tools 'load_skill' and the tools listed in the tool declarations.
[text protocol]            To call a tool, answer with exactly one block per call and nothing after it:
													 <tool_call>{"name": "<tool>", "arguments": { ... }}</tool_call>
													 Wait for the <tool_response> before you continue.
</harness_instructions>

<available_skills>
	<skill><name>csharp-style</name><description>...</description></skill>
	...
</available_skills>

<tools>                     (text protocol only; native providers get the declarations via the API)
	{"name": "tickets__search", "description": "...", "parameters": { JSON schema }}
	...
</tools>
```

Short aliases (user suggestion, accepted as tolerant input only, convention credo):

| Alias (own line) | Canonical form |
|---|---|
| `/REQUESTSKILL "<name>"` | `<tool_call>{"name":"load_skill","arguments":{"name":"<name>"}}</tool_call>` |
| `/USETOOL "<tool>" {json arguments}` | `<tool_call>{"name":"<tool>","arguments":{json arguments}}</tool_call>` |

The aliases are not advertised in the prompt by default (an option can enable it for weak models that fail at JSON).
Results always come back in the canonical `<tool_response>` form.

## 5. Second half: tools

The same principles apply as for skills: n source instances, one flat catalog with descriptions, native first, proxy as
fallback, persistence for recovery.

### 5.1 Tool sources

| Kind | Class (working title) | Native/proxy | Description |
|---|---|---|---|
| Remote MCP reference | `RemoteMcpToolSource` | native | Public MCP server URL. It is handed to the provider if the provider reports capability `RemoteMcpServers` and it is not opted out. Otherwise the same source is used via `McpProxyToolSource`. |
| MCP proxy | `McpProxyToolSource` | proxy | The harness is the MCP client (Streamable HTTP, later stdio). Target credentials (API key, OAuth tokens) are kept in the harness and never sent to the model or the provider. |
| UJMW in memory | `UjmwServiceToolSource` | proxy | A service interface (e.g. `IPromptLibrary`, a CalDAV service) registered in memory. Methods become tools; JSON schemas come from the contract (shared with the MCP facade of ideas.md, section 3). |

```csharp
public interface IToolSource {
	string SourceId { get; }
	ToolDescriptor[] GetTools();                                   // MCP-shaped: name, description, input schema, annotations
	ToolCallResult CallTool(string toolName, string argumentsJson, CancellationToken cancellationToken);
	RemoteToolReference GetNativeReference();                      // null = proxy only
}
```

Catalog rules are the same as in [ideas.md](../ideas.md), 1.3.3: names match `^[a-zA-Z0-9_-]{1,64}$`, prefixed per
source if needed (`<source>__<tool>`), sorted deterministically and stable during a chat.

### 5.2 Native path vs. proxy path for tools

| Provider | Native reference | Proxy path |
|---|---|---|
| OpenAI (Responses API) | tool type `mcp` with `server_url` (public servers only) | function calling (`function_call` / `function_call_output`) |
| GitHub Copilot (ACP) | `session/new.mcpServers` (HTTP or stdio servers) | expose our proxy tools as a local bridge MCP endpoint and hand it in via `session/new.mcpServers` (delegation of our own proxy) |
| Local LLM (Ollama) | – | native `/api/chat` tool calling |
| Mock | – | – (capabilities show X) |

**Dependency:** the proxy path needs the tool handshake. `ICommonLLM.CallChatApi` has none today. Either this epic
introduces the minimal version of `IConversationalLLM` (ideas.md, 1.3.3), or the first iteration is limited to
providers that can use the bridge MCP endpoint (Copilot) plus the native references. This is decided in work package 7.
Providers without native function calling use the text protocol of 4.6 (`<tool_call>` / `<tool_response>`, alias
`/USETOOL`), which works on top of `CallChatApi` without any contract change.

### 5.3 Protective interceptor (approvals)

Every proxied tool call passes `ToolApprovalInterceptor`. For native references, the provider's approval mechanism is
mapped to the same decision (OpenAI `require_approval` → `mcp_approval_request`, ACP `session/request_permission`).

| Decision | Scope | Persisted in |
|---|---|---|
| Allow once | this single call | – |
| Allow for this chat | all calls of this tool in this chat | chat (session store), so it survives recovery and restarts |
| Always allow | all chats | settings (per tool source + tool, optionally per whole source) |
| Reject once / reject for this chat | as above | as above |

Rules:

- Default: every tool needs confirmation once per chat. `readOnlyHint = true` tools can be pre-approved by a setting.
	`destructiveHint = true` tools can never be "always allowed" without an explicit second confirmation.
- While waiting for the user, the turn pauses (UJMW long-running operation stays open, or the polling state of
	`IConversationalLLM` is `AwaitingHost`). Cancelling the turn yields a synthetic error result for the open call.
- A rejected call yields a tool error result for the model ("The user rejected the call"), not a protocol error.
- Tool results are untrusted content: they stay in tool-result slots and are truncated/summarized if they are too large.

### 5.4 Persistence and recovery

The chat stores, next to `ContextStore` and `ContextBaseline`:

- pinned skills (4.4),
- the enabled tool sources of the chat (ids),
- the per-chat approvals (5.3).

When the provider context is rebuilt (invalid `contextStore`, ended Copilot session without resume), the harness
restores all three before the next turn. Global "always allow" approvals and the source definitions live in the
settings.

## 6. Provider capabilities (additions)

New `LLMCapabilityNames`, each opt-out-able in the settings UI (switch if available, X otherwise):

| Capability | Meaning |
|---|---|
| `ToolCalling` | Provider supports function calling, so the harness can proxy tools and use `load_skill` |
| `RemoteMcpServers` | Provider calls public MCP servers itself when given their URL |
| `NativeSkills` | Provider/inner harness loads Agent Skills itself (by URL or skill folder) |
| `NativeToolApproval` | Provider asks for approvals itself (mapped to the interceptor UI) |

## 7. Examples

### 7.1 Getting started: one folder with skills

```csharp
SkillCatalog skills = new SkillCatalog();
skills.AddSource(new FileSystemSkillSource("team-skills", @"C:\GIT\TeamSkills\.agents\skills"));

CheapPromptingUIBackend backend = new CheapPromptingUIBackend(store, settingsService);
backend.Skills = skills;
```

The model sees the catalog. When it calls `load_skill("csharp-style")`, the UI shows
`SKILL 'csharp-style' loaded` and the skill stays in the context for the rest of the chat.

### 7.2 Advanced: mixed sources with native and proxy paths

```csharp
skills.AddSource(new EmbeddedResourceSkillSource("app", typeof(Program).Assembly, "MyApp.Skills."));
skills.AddSource(new GitRepositorySkillSource("ushell-docs", "https://github.com/.../UShell.Docs.git", "main", ".agents/skills"));

ToolCatalog tools = new ToolCatalog();
tools.AddSource(new RemoteMcpToolSource("docs-search", "https://mcp.example.com/mcp"));        // public: native if possible
tools.AddSource(new McpProxyToolSource("tickets", "https://intranet/mcp", credentialsRef));   // private: always proxy
tools.AddSource(new UjmwServiceToolSource("caldav", calDavService, typeof(ICalDavService)));  // in memory: always proxy
```

Use case: an OpenAI chat calls the public `docs-search` server itself (no round trip through the harness). The
intranet ticket system is unreachable for OpenAI, so the harness proxies it and keeps the credentials. The CalDAV
service runs in the same process. With Copilot, all three end up in `session/new.mcpServers` (the public server
directly, the other two via the local bridge endpoint).

## 8. Requirements (candidates)

| Id | Requirement |
|---|---|
| SKL-1 | Skills follow the Agent Skills format (`SKILL.md` with `name` and `description` front matter). |
| SKL-2 | Four skill source kinds (embedded resource, local file/folder, URL proxy, Git repository), each can be registered multiple times. |
| SKL-3 | All sources form one flat, deterministically sorted catalog with unique names. |
| SKL-4 | Only name + description are sent up front (progressive disclosure). Content is loaded on activation, referenced files on demand. |
| SKL-5 | Native skill references are preferred when the provider supports them (`NativeSkills`) and the skill is public. |
| SKL-6 | Activation via native tool call (`load_skill`); providers without function calling use the Hermes-style text protocol (`<tool_call>`), `/REQUESTSKILL "<name>"` is accepted as alias. |
| SKL-9 | A hidden harness prompt (instructions, `<available_skills>`, for the text protocol also `<tools>`) is rendered deterministically, never shown in the UI and rebuilt identically on recovery. |
| SKL-10 | Git skill sources use the command-line `git` (shallow, sparse, cached, non-interactive). |
| SEC-1 | Secrets of sources are stored in the Windows Credential Manager via `ICredentialStore` (first implementation); the settings file holds references and hints only. |
| SKL-7 | A loaded skill is pinned: persisted with the chat and always part of the rebuilt context, also after compression and recovery. |
| SKL-8 | The UI shows skill activation as a grey line instead of a chat bubble. |
| TL-1 | Three tool source kinds (remote MCP reference, MCP proxy, UJMW in memory), each can be registered multiple times, normalized to MCP tool descriptors. |
| TL-2 | Remote MCP URLs are handed to the provider only if it reports `RemoteMcpServers` (not opted out) and the server is public; otherwise they are proxied. |
| TL-3 | Credentials of proxied sources are stored in the harness only and never reach the model or the provider. |
| TL-4 | Every tool call passes the approval interceptor: once per chat (persisted for recovery) or "always allow" in the settings. |
| TL-5 | Tool names, ordering and the result contract follow the harness invariants (exactly one result per call, stable sorted list, untrusted results). |
| TL-6 | Providers without native function calling get tools via the text protocol (`<tools>`, `<tool_call>`, `<tool_response>`); `/USETOOL` is accepted as alias. |
| GEN-1 | Native first, proxy as fallback. Every native path can be opted out per provider. |
| GEN-2 | Unknown keywords, kinds or names are ignored or answered with a hint, never treated as fatal errors. |
| SKL-11 | The settings contain a "User instructions" dialog. The text is persisted globally and provided as mandatory (pinned) skill `mustread-user-instructions` in the hidden harness prompt of every context. An empty text omits the skill. *(implemented)* |
| SKL-12 | If the hidden harness prompt changes (e.g. edited user instructions), it is sent again into an existing provider-side context (fingerprint per chat). *(implemented)* |
| TL-7 | For testing the tool handshake end-to-end, the harness offers the internal tool `ui_set_theme` (`{"theme":"light"\|"dark"}`), which switches the chat UI between light and dark mode. The theme is also selectable in the settings. *(implemented)* |
| TL-8 | The text-protocol tool loop is bounded (`MaxToolRoundTrips` = 5); the intermediate tool exchange is not shown as chat bubble; tool errors become `is_error` results. *(implemented)* |

## 9. Artifacts (bottom-up)

| Artifact | Package | Purpose |
|---|---|---|
| `SkillDescriptor`, `ToolDescriptor`, `ToolCallResult` | `SmartStandards.AiSupport` | Transportable DTOs |
| `ISkillSource`, `IToolSource` | `SmartStandards.AiSupport` | Source contracts |
| `EmbeddedResourceSkillSource`, `FileSystemSkillSource`, `UrlSkillSource`, `GitRepositorySkillSource` | `CommonProviders` | Skill sources |
| `RemoteMcpToolSource`, `McpProxyToolSource`, `UjmwServiceToolSource` | `CommonProviders` | Tool sources |
| `SkillCatalog`, `ToolCatalog` | `CommonProviders` | Flat catalogs, naming, rendering (`<available_skills>`) |
| `ToolApprovalInterceptor` | `CommonProviders` | Approval decisions and persistence |
| `HarnessPromptRenderer`, `TextToolCallParser` | `CommonProviders` | Hidden harness prompt, text protocol (canonical form + aliases) |
| `GitCommandLineClient` | `CommonProviders` | Out-of-the-box Git access via command-line `git` |
| `ICredentialStore`, `WindowsCredentialStore` | `SmartStandards.AiSupport` / `CommonProviders` | Secret storage (Windows Credential Manager first) |
| Session store additions | `CommonProviders` | Pinned skills, enabled tool sources, per-chat approvals |
| Settings additions | `SmartStandards.AiSupport` + `CommonProviders` | Source definitions, credentials, global approvals, new capabilities |
| Chat UI | `AspNetCore` | Grey skill lines, approval prompt, source management |

## 10. Open points

1. ~~Text keyword~~ decided: canonical Hermes-style `<tool_call>`, aliases `/REQUESTSKILL` and `/USETOOL` (4.6).
2. ~~Credential storage~~ decided for the first iteration: Windows Credential Manager behind `ICredentialStore` (4.1.1).
	 Windows Credential Manager access needs P/Invoke (`advapi32 CredWrite/CredRead`), approved by the user for this
	 purpose; alternative without interop: `ProtectedData` (DPAPI, NuGet `System.Security.Cryptography.ProtectedData`).
3. ~~Git access~~ decided: own small provider on top of command-line `git` (4.1). Open: cache refresh interval.
4. Proxy tools for OpenAI need the tool handshake: introduce a minimal `IConversationalLLM` here, or postpone OpenAI proxy tools?
5. Skill `scripts/`: executing scripts is out of scope (content only) unless a later epic adds a sandbox.
6. Verify the current state of native skill support per provider (Copilot CLI skill folders, Anthropic/OpenAI skill APIs)
	 before implementing `NativeSkills`.
7. Logging: `MyLogEventKind` range for the new components (to be approved).

## 11. Work packages

### 11.1 Implemented first slice (internal tools and user instructions)

| Artifact | Location | State |
|---|---|---|
| `GetUserInstructions`, `SaveUserInstructions`, `GetUiTheme`, `SaveUiTheme`, `UiThemeNames` | `IPromptingSettingsService` (contracts) | Synchronous, UJMW/WCF transportable |
| Persistence of `UserInstructions` and `UiTheme` | `FileBasedPromptingSettingsService` (`settings.json`) | Implemented |
| `HarnessSkill` (incl. `CreateUserInstructionsSkill`), `IHarnessTool`, `SetUiThemeTool` | `CommonProviders/InteractivePrompting/Harness` | Implemented |
| `HarnessToolbox` (hidden prompt rendering, `load_skill`, Hermes text protocol, `/USETOOL` and `/REQUESTSKILL` aliases) | `CommonProviders/InteractivePrompting/Harness` | Implemented (precursor of `HarnessPromptRenderer` / `TextToolCallParser`) |
| Hidden prompt injection, fingerprint re-send, bounded tool loop | `CheapPromptingUIBackend`, `ChatSession.HarnessPromptFingerprint` | Implemented |
| Theme selection, dark mode CSS, User instructions dialog, theme refresh after each answer | Chat UI (`index.html`, `chat.js`, `chat.css`) | Implemented |
| Offline test path: `StupidMockLLM` calls `ui_set_theme` on "dark mode" / "light mode" if the tool is announced | `StupidMockLLM` | Implemented |

Native function calling (OpenAI tools, Copilot ACP client tools) is not used yet; all providers currently receive the
text protocol. Switching to the native path is part of work package 7.

### 11.2 Remaining packages

| # | Package |
|---|---|
| 1 | Contracts: descriptors, `ISkillSource`, `IToolSource`, new capability names |
| 2 | Skill sources (embedded, file) + `SkillCatalog` + catalog rendering |
| 3 | Hidden harness prompt, text protocol parser (canonical + aliases), skill activation (`load_skill`), pinning in the session store and context building |
| 4 | UI: grey skill lines, skill source settings |
| 5 | Skill sources URL and Git (`GitCommandLineClient`, caching) + `ICredentialStore` / `WindowsCredentialStore` |
| 6 | Tool sources: remote MCP reference, MCP proxy client, UJMW in memory + `ToolCatalog` |
| 7 | Tool handshake decision (minimal `IConversationalLLM` vs. bridge MCP only) and provider integration (OpenAI, Copilot ACP) |
| 8 | `ToolApprovalInterceptor` incl. per-chat and global persistence and UI prompt |
| 9 | MSTests (fake sources, fake MCP server, recovery with pinned skills and approvals) |
| 10 | Documentation (requirements, architecture), harness skill update |
