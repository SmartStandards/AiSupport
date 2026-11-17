# Epic: Prompting UI Iteration 2 (Skill and Tool Management, Chat Details, Approvals, Progress)

> Status: **concept, not yet an active requirement.** Nothing in this epic is implemented automatically. Accepted parts
> move into [requirements.md](../requirements.md) and
> Builds on [epic-interactive-prompting.md](epic-interactive-prompting.md) (middle layer, `contextStore`, PoC chat
> client) and [epic-skills-and-tools.md](epic-skills-and-tools.md) (source kinds, catalog, native vs. proxy path,
> text protocol, approval vocabulary). Where this epic and the skills/tools epic overlap, this epic describes the **UI
> and the user-facing management model**; the skills/tools epic stays authoritative for the harness mechanics.
>
> Implementation progress: approvals (3.3), audit log, tool provider model incl. MCP and UJMW contract sources (3.2),
> OOB tools with guards (3.4) and the pause/resume Variant A (3.5) are implemented and documented in
> [requirements.md](../requirements.md) (TLM/OOB/APR/GRD/AUD) and [architecture.md](../architecture.md).
> Also implemented: CHD-3 (tabbed details dialog), CHD-8 (per-source audit icon and filter), SKM-6 warning, immediate
> display of control messages (3.8), PRG-3 (tool progress), editable skill source labels and fixed allow-list editors
> for the built-in tools (UIC-2, instead of sandboxed detail tabs).
> Open: sandboxed settings detail tabs for foreign providers (3.2.1, UIC-1), Variant B for providers without
> `PausableToolCalls`. Follow-up: [epic-prompting-ui-iteration-3.md](epic-prompting-ui-iteration-3.md).

## 1. Abstract and motivation

Iteration 1 delivered a working chat client: chats, one global provider, provider capabilities, user instructions as a
mandatory skill and a first text-protocol tool loop (`HarnessToolbox`, `IHarnessTool`). Everything that makes a chat
model useful in a real environment is still hard-wired in `Program.cs`: which skills exist, which tools exist, what the
tools are allowed to do and which provider/model a chat uses.

Iteration 2 turns these hard-wired parts into **user-manageable building blocks**:

- Global settings get dedicated tabs to register and maintain **skill sources** and **tool sources**, each with a
	three-level visibility/protection switch.
- Four **out-of-the-box tools** (preference update, file system, internet, shell) come with their own allow-lists as a
	second line of defense behind the approval of the tool call itself.
- A **tool provider model** allows loading further tools from foreign DLLs later.
- Every chat gets a **chat details dialog** (`...` button): name, compression, provider, model, per-chat skill and tool
	switches, interactive approvals and a per-tool **audit log**.
- `ICommonLLM.CallChatApi` gets a **progress delegate**, so the UI can show what the provider is doing right now (for
	example the last output line of the GitHub Copilot CLI) and optional item/total counters.

### 1.1 Positive differentiation

| Aspect | Typical AI clients | This epic |
|---|---|---|
| Skill sources | One fixed folder per product | Files (drag and drop), URLs, folders, GitHub repositories; one catalog for all providers |
| Context load | Skills either always loaded or invisible | Per source: hidden / on demand / initialized at start, visibly marked as context-heavy |
| Tool security | One approval prompt, then full access | Approval (three levels) **plus** a second line of defense: allow-lists for paths (RO/RW), domains and shell commands |
| Approval scope | Often only "allow always" or "ask every time" | Deny (and hide for this chat), allow once, allow for this chat (persisted with the chat) |
| Provider choice | Global setting, or locked per chat without context transfer | Per chat, switchable at any time; the context is rebuilt from the persisted outer history |
| Traceability | Tool calls hidden in the transcript | Per-chat, per-tool audit log with a usage indicator |
| Progress | Spinner only | Provider-delivered status text plus optional item/total counter, also for tools with their own progress |
| Extensibility | Plugins bound to one vendor | Provider model for tools from foreign DLLs; UJMW contract interfaces become tools via URL + token |

## 2. Features as understood

The following list restates the requested features. Ids are working ids of this epic.

### 2.1 Global settings: tab "Skills"

| Id | Feature |
|---|---|
| SKM-1 | The settings dialog becomes tabbed. A new tab shows and maintains (registers) skill sources. |
| SKM-2 | Single Markdown files (`SKILL.md` or any `*.md` with Agent Skills front matter) can be added via drag and drop (or by entering a path). They are **only referenced, never copied**; local and UNC paths are allowed. The `X` button only deregisters the reference (the file is never deleted). If a reference is broken (file missing or unreadable), the entry shows an exclamation mark with the reason. |
| SKM-3 | URLs can be registered. The harness acts as proxy (the model never needs to reach the URL itself), see skills/tools epic 4.2. |
| SKM-4 | A file system path (whole folder, scanned for `*/SKILL.md`) **or** a GitHub repository URL plus a GitHub PAT can be registered. The PAT is stored via `ICredentialStore` (SEC-1), never in the settings file. |
| SKM-5 | Two built-in skills exist which cannot be removed and are always active: `global-user-preferences` and `global-system-preferences`. `global-user-preferences` replaces the current `mustread-user-instructions` (SKL-11) and lives at `%USERPROFILE%\.agents\skills\global-user-preferences\SKILL.md`. For `global-system-preferences`, a URL or a UNC path can be configured; the fallback is `%PUBLIC%\.agents\skills\global-system-preferences\SKILL.md` (same folder structure as for the user). |
| SKM-6 | Every **source** has a three-level switch: **Hidden** / **Available (on demand)** / **Initialize at start**. The third level shows a warning ("loads into every context") and pushes all skills of the source into the session at chat start (pinned like a loaded skill). |
| SKM-7 | The two built-in skills are fixed to level 3 ("Initialize at start"). The switch is visible but disabled. Missing files are not an error: the skill is simply empty (and omitted from the prompt) until the first write. |
| SKM-8 | Every non-built-in skill source has an option **"Secure availability"**. It activates a local offline copy of the source, which is refreshed **once at every new chat start**. If the refresh fails (share/URL/repository unreachable), the last offline copy is used and the entry shows the exclamation mark with the reason ("offline copy from <timestamp> used"). Without the option, an unreachable source is skipped. |

### 2.2 Global settings: tab "Tools"

| Id | Feature |
|---|---|
| TLM-1 | A new tab shows and maintains (registers) tool sources. |
| TLM-2 | MCP server URLs can be registered (native reference or proxy, see skills/tools epic 5.1), optionally with a token. |
| TLM-3 | DLLs can be added via drag and drop. As in the UJMW Test Client, the contained contract interfaces are listed and can be selected. For each selected interface, a URL and a token are deposited. Every method of the interface becomes a tool, called through the UJMW dynamic client. |
| TLM-4 | Out-of-the-box (OOB) tools are listed as well. Each OOB tool has its own detail tab for its configuration (2.3). |
| TLM-5 | A clean **tool provider model** is introduced, so that tools (incl. their configuration) can later be loaded from foreign DLLs. The OOB tools are implemented as providers of this model themselves (dogfooding). |
| TLM-6 | Every **tool source** (MCP server, contract interface, OOB tool provider, foreign provider) has a three-level switch: **Hidden** / **Usable (with confirmation)** / **Usable (without confirmation)**. The level applies to all tools of the source; per-tool overrides are a later extension. |

### 2.3 Out-of-the-box tools

| Id | Tool | Feature |
|---|---|---|
| OOB-1 | Preferences update | Overwrites or appends the content of `global-user-preferences` / `global-system-preferences`. Default level: "Usable (with confirmation)". Reading is **not** done through this tool: consumption always happens via the built-in skills (SKM-5). Writing is allowed for both; if `global-system-preferences` points to a URL, writing is naturally impossible and the tool returns an actionable tool error. |
| OOB-2 | File system access | Detail tab maintains allowed (UNC) paths, each with RO or RW, always recursive. This list is the second line of defense behind the tool approval. If the model accesses a path outside the list, the user is asked via dialog (on-demand request, RO/RW selectable); the decision is added to the list. |
| OOB-3 | Internet access | Detail tab maintains allowed domains (second line of defense). Unlisted domains are requested on demand like OOB-2. |
| OOB-4 | Shell access | Detail tab maintains allowed cmd/PowerShell commands (second line of defense). When the model requests a command, the concrete command line is stored literally after approval. Entries can be edited later and generalized via wildcard or regex. |

### 2.4 New chat and chat details dialog

| Id | Feature |
|---|---|
| CHD-1 | Starting a new chat uses the default provider (and its default model) of the settings. Provider and model are stored **per chat** from then on. |
| CHD-2 | Every chat has a `...` button in the upper right corner which opens the chat details dialog. |
| CHD-3 | The dialog also contains the chat name (rename) and the compress button. |
| CHD-4 | A combo box switches the model provider of this chat (kept per chat). |
| CHD-5 | A combo box with free text input selects the concrete model (kept per chat). Its list comes from a new **optional** array of known models delivered by the provider (`LLMProviderInfo.KnownModels`). Without the array, the box is free text only. |
| CHD-6 | Slide switches **per skill source** allow activating sources which were hidden before, or raising their level. This is **one-way only** (on, never off), because loaded content cannot be removed from a context. The model is informed via a control message when a switch changes (3.8). |
| CHD-7 | Slide switches **per tool source** allow activating/deactivating tools or changing their protection level (with/without confirmation). The model is informed via a control message when a switch changes (3.8). |
| CHD-8 | Per tool source, a small log icon is shown. It is grey until a tool of the source was used at least once in this chat, then blue. A click opens the chat-specific audit log with all usages of the tools of this source. |

### 2.5 Interactive approval

| Id | Feature |
|---|---|
| APR-1 | If a tool with level "Usable (with confirmation)" is called, or an OOB tool hits its allow-list (OOB-2..4), the protection level is requested interactively. |
| APR-2 | While permissions are obtained, the turn waits. Afterwards the tool result is delivered (tool available) or a rejection result (tool denied). Whether the model notices the wait depends on the new provider capability `PausableToolCalls` (3.5). |
| APR-3 | The approval dialog has three buttons: **Deny** (and hide the tool source for this chat) / **Allow once** / **Allow for this chat**. "Session" always means the chat: decisions are persisted with it and survive restarts. |

### 2.6 Progress

| Id | Feature |
|---|---|
| PRG-1 | `ICommonLLM.CallChatApi` gets a progress delegate `Action<string, int, int> onProgress` (pattern of the UJMW Test Client / UJMW long-running callbacks) and, in the same change, a `CancellationToken`. |
| PRG-2 | The provider uses it to update the status text in the UI interactively (for example the last output line of the GitHub Copilot CLI, "thinking", "calling tool X"). |
| PRG-3 | Optional current item / total counters can be used for actions with a measurable progress (for example a later image generation) **or** when the model calls a tool which reports its own progress. |
| PRG-4 | The chat UI polls the UJMW long-running answers in a fixed 1 second interval (never earlier than `pollNbf`). |

## 3. Design

### 3.1 Settings model (skills)

```csharp
public enum SkillSourceLevel {
	Hidden = 0,
	OnDemand = 1,
	InitializeAtStart = 2
}

public class SkillSourceInfo {
	public string SourceId { get; set; }        // stable id, also used as credential key
	public string Kind { get; set; }            // "File" | "Url" | "Folder" | "GitHub" | "BuiltIn"
	public string DisplayName { get; set; }
	public string Location { get; set; }        // file path (local or UNC), URL, folder path or repository URL
	public bool HasCredentials { get; set; }    // PAT/token present (value is never returned)
	public string CredentialHint { get; set; }  // e.g. "****abcd"
	public int Level { get; set; }              // SkillSourceLevel
	public bool IsFixed { get; set; }           // built-in sources: not removable, level not changeable
	public string[] SkillNames { get; set; }    // resolved catalog entries (for display)
	public bool IsReferenceBroken { get; set; } // UI shows an exclamation mark
	public string LastError { get; set; }       // reason, e.g. file missing or URL unreachable; null if fine
	public bool SecureAvailability { get; set; }        // SKM-8: keep a local offline copy
	public string OfflineCopyTimestampUtc { get; set; } // ISO 8601 of the last successful refresh, null if none
}
```

- Offline copies (SKM-8) live in `%LOCALAPPDATA%\SmartStandards\AiSupport\SkillCache\<SourceId>\` with the same folder
	layout as the source (`<skill>/SKILL.md` plus referenced files). The refresh runs once per new chat (not per turn),
	replaces the copy atomically (write to a temp folder, then swap) and never blocks the chat longer than a short timeout;
	on failure the previous copy stays in place. GitHub sources use the existing sparse checkout as their copy.

- `File` sources are pure references (SKM-2). The UI offers drag and drop plus a path text field (also for UNC paths).
	A browser `drop` event never reveals the full path, therefore the EXE uses the WebView2 host channel
	(`chrome.webview.postMessageWithAdditionalObjects`, `CoreWebView2File.Path`) to obtain it. In the web host (`/chat/`),
	drag and drop is not offered for files, because paths there must be valid on the server; the text field remains.
- Broken references are detected on every catalog refresh (and on chat start). They never block a chat: the source is
	skipped and flagged; skills already pinned in a chat keep their stored content (SKL-7).
- Built-in sources `global-user-preferences` and `global-system-preferences` are always present with `IsFixed = true` and
	`Level = InitializeAtStart`.
- `global-user-preferences` lives at `%USERPROFILE%\.agents\skills\global-user-preferences\SKILL.md` (successor of the
	current `UserInstructions` setting, migrated into the file on first start). `global-system-preferences` is read from the
	configured URL/UNC path or from `%PUBLIC%\.agents\skills\global-system-preferences\SKILL.md`. Both use the Agent Skills
	folder layout, so other agent products that index `.agents/skills` can consume them as well.
- Level semantics per chat:
	- `Hidden`: not in the catalog.
	- `OnDemand`: name + description in `<available_skills>`, body via `load_skill`.
	- `InitializeAtStart`: bodies are pinned at chat start (SKL-7) and therefore count against the context of every chat.
- The level is a **global default**. Chats can only raise it (CHD-6).

### 3.2 Settings model (tools) and tool provider model

```csharp
public enum ToolLevel {
	Hidden = 0,
	RequiresConfirmation = 1,
	NoConfirmation = 2
}

/// <summary>
/// Factory for tools. OOB tools and tools from foreign DLLs implement this.
/// </summary>
public interface IHarnessToolProvider {

	string ProviderId { get; }

	string DisplayName { get; }

	/// <summary>Default level of this tool source if the user did not set one.</summary>
	int DefaultLevel { get; }

	/// <summary>Default settings object (JSON) for a new instance.</summary>
	string GetDefaultSettingsJson();

	/// <summary>Self-contained HTML page (inline JS/CSS) of the detail tab, or null (see 3.2.1).</summary>
	string GetSettingsUiHtml();

	/// <summary>Credential slots rendered by the host outside the detail tab (secrets never enter the settings JSON).</summary>
	ToolCredentialSlot[] GetCredentialSlots();

	/// <summary>Creates the tools for the given (persisted) configuration.</summary>
	IHarnessTool[] CreateTools(IToolProviderContext context);

}
```

- `IToolProviderContext` gives access to the provider's persisted configuration (JSON, Newtonsoft), the credential
	store, the progress reporter (3.6) and the **guard** of the second line of defense (3.4).
- Provider discovery from foreign DLLs: a DLL is loaded into a dedicated `AssemblyLoadContext` and scanned for public
	non-abstract `IHarnessToolProvider` implementations with a parameterless constructor (or via the SmartStandards
	ComponentDiscovery convention, to be decided).
- Built-in providers: `PreferencesToolProvider` (OOB-1), `FileSystemToolProvider` (OOB-2), `InternetToolProvider`
	(OOB-3), `ShellToolProvider` (OOB-4), `McpToolProvider` (TLM-2, one instance per registered server),
	`UjmwContractToolProvider` (TLM-3, one instance per selected interface).
- `IHarnessTool` gets optional metadata (MCP-shaped annotations `readOnlyHint`, `destructiveHint`) and access to the
	progress reporter. The existing `Invoke(JObject)` stays as is for compatibility.

#### 3.2.1 Detail tabs (settings UI contract)

Every provider (OOB providers included, dogfooding) may deliver its own detail tab as **HTML + JS**, strictly limited
to exactly **one JSON settings object** that goes in and comes out. The binding norm is documented in the skill
`ai-harness-development` (section "Tool provider settings UI contract"). Summary:

- The host renders the page in a sandboxed `iframe` (`sandbox="allow-scripts"`, no `allow-same-origin`, CSP without any
	network access). The page cannot call services, read cookies or reach the host DOM.
- The only channel is `postMessage` with three message types: `settings.load` (host to page, the current object),
	`settings.changed` (page to host, the complete new object), `settings.ready` (page to host, optional).
- The object is validated server-side by the provider when it is saved; invalid objects are rejected with a message.
- Secrets are never part of the object; they use credential slots rendered by the host (`ICredentialStore`).
- Providers without HTML (`GetSettingsUiHtml() == null`) get a plain JSON editor for the same object.

#### 3.2.2 UJMW contract interfaces as tools (TLM-3)

- Upload: the UI sends the DLL bytes; the backend stores it in a managed folder, loads it into an
	`AssemblyLoadContext` and returns the list of public interfaces (like the UJMW Test Client).
- Per selected interface: base URL + token (credential store). Tool names: `<interface>__<method>` (rules of
	skills/tools epic 5.1). JSON schemas are generated from the method parameters.
- Calls go through the UJMW `DynamicClientFactory`; long-running operations of the target service are polled by the
	dynamic client and their progress callbacks are forwarded to the progress reporter (PRG-3).

### 3.3 Approval levels and decisions

Effective level of a tool source in a chat = per-chat override (if any), otherwise the global level.

| Global/chat level | Call behavior |
|---|---|
| Hidden | Tool is not declared to the model. Calls (e.g. hallucinated) get a "tool not available" result. |
| Usable (with confirmation) | Approval dialog (APR-3) unless an "allow for this chat" decision exists for the source. |
| Usable (without confirmation) | Executed directly. The second line of defense (3.4) still applies. |

Dialog decisions and their effect:

| Button | Effect |
|---|---|
| Deny | Tool result "The user rejected the call". The tool source is set to **Hidden for this chat** (chat override); the model learns it from the rejection result itself (no extra control message, 3.8). |
| Allow once | Only this call is executed. |
| Allow for this chat | Chat override "approved" for the tool source; persisted with the chat (survives restarts). |

This replaces the decision table of skills/tools epic 5.3 for the UI. "Always allow" is expressed by the global level
"Usable (without confirmation)".

### 3.4 Second line of defense (guards)

The guards are enforced **inside** the OOB tools, independently of the approval. An approved call can still be blocked.

| Guard | Entry | Matching | On miss |
|---|---|---|---|
| `PathGuard` | Path (local or UNC), `RO` or `RW` | Normalized full path, case-insensitive, recursive; `..` and symlinks resolved before matching | Request dialog: allow path (RO/RW, recursive) or deny |
| `DomainGuard` | Domain | Exact host or `*.domain` | Request dialog: allow domain or deny |
| `ShellGuard` | Command pattern + shell (`cmd` / `powershell`) | Literal (default), wildcard (`*`, `?`) or regex (prefix `regex:`) over the full command line | Request dialog: allow this exact command line (stored literally) or deny |

Guard requests use the same pause mechanics as approvals (3.5). Their decisions are stored in the **global** settings
(the allow-lists), not per chat.

### 3.5 Pause/resume during a turn (approvals and guard requests)

`SendMessage` is a UJMW long-running operation. An approval needs a round trip to the UI while that operation is still
running. Proposal:

1. The harness hits an approval point inside the tool loop and creates a pending request (`RequestId`, chat id, tool,
	 argument summary, kind `ToolApproval|PathAccess|DomainAccess|ShellCommand`).
2. The request is published through a callback of `SendMessage` (`onInteractionRequired`) and the progress status is set
	 to "Waiting for permission ...".
3. The UI shows the dialog and answers via a separate short call `AnswerInteraction(chatId, requestId, decision)`.
4. The tool loop continues with the decision. Cancelling the turn (UJMW `DELETE`) yields a rejection result.

What the model "sees" while waiting (APR-2) is selected by the new provider capability `PausableToolCalls` ("the
provider/inner harness can keep a pending tool call open for an arbitrary time"):

- **Capability available - Variant A (silent pause):** the tool response is simply delayed. The model sees nothing but
	the final result (granted result or rejection). This is the default for every provider whose tool loop is driven by
	the harness (text protocol, function calling).
- **Capability unavailable or opted out - Variant B (explicit notice):** the harness immediately answers with a
	`<tool_response>` "permission is being requested, wait", ends the turn and later continues with a control message
	"permission granted/denied, result: ...". Used for providers whose inner harness times out on open tool calls.
- Like every capability, it is opt-out-able in the settings (CAP-3); the opt-out switches to Variant B.

### 3.6 Progress delegate

```csharp
#if NET_FX
		string CallChatApi(ChatTurn[] newMessages, ref string contextStore);
#else
		/// <summary>
		/// ... 'onProgress' (optional, may be null) reports (statusText, currentItem, totalItems);
		/// counters are 0 if not applicable.
		/// </summary>
		string CallChatApi(
			CancellationToken cancellationToken, ChatTurn[] newMessages, ref string contextStore, Action<string, int, int> onProgress
		);
#endif
```

- Same `#if NET_FX` split as `IPromptingUIBackend.SendMessage`, because delegates are not WCF-transportable.
- The `CancellationToken` is passed through from `SendMessage` (UJMW `DELETE`). Connectors abort HTTP requests or kill
	the CLI process tree; the middle layer no longer has to abandon a still-running provider call.
- Convention credo: `onProgress` may be `null`, callers and UIs ignore unknown/missing values. A status text of `null`
	keeps the previous text; `totalItems <= 0` hides the progress bar.
- Providers: OpenAI reports phases ("sending", "waiting for response", "receiving"); Copilot CLI reports the last
	non-empty output/ACP update line (throttled, e.g. at most 4 per second); the mock reports a fake sequence.
- The harness reports tool phases ("calling tool X") and forwards progress of tools (PRG-3).
- `IPromptingUIBackend.SendMessage` (NET_CORE) gets the same `Action<string, int, int> onProgress` plus
	`Action<string, string, string, string> onInteractionRequired` (requestId, kind, toolName, detailsJson). UJMW replays
	them as `onProgressInvokes` / `onInteractionRequiredInvokes`; the UI only applies the **latest** progress entry.
- `chat.js`: fixed 1 s poll interval (PRG-4) instead of the adaptive 500 ms..5 s, still respecting `pollNbf`.

### 3.7 Per-chat state

`ChatSession` (session store) gets:

| Field | Content |
|---|---|
| `ProviderName`, `Model` | Snapshot of the defaults at chat creation, changeable in the details dialog |
| `SkillLevelOverrides` | skill source id → raised level (one-way) |
| `ToolLevelOverrides` | tool source id → level (incl. "hidden by deny") |
| `ChatApprovals` | tool source ids with "allow for this chat" |
| `ToolAuditLog` | entries: timestamp (UTC), tool source, tool, argument summary, decision, duration, success/error, result size |

Control messages (3.8) are regular entries of `Messages` (with `Kind = "Control"`), so they need no own field.

Provider switch: the middle layer resets `ContextStore` to `null` (the old provider's opaque value is meaningless for
the new one) and rebuilds the context from the persisted outer history, pinned skills and the hidden harness prompt
(same path as COP-5). `CheapPromptingUIBackend` therefore needs an LLM factory per provider/model instead of the current
`Func<ICommonLLM>`.

### 3.8 Control messages on switch changes

The model must know which skills and tools it may use. Whether a separate control message is needed depends on the
situation:

| Situation | Behavior |
|---|---|
| The change is the answer to a running request (approval dialog, guard request, "Deny") | **No** control message. The model learns the outcome from the tool result itself (granted result or rejection). |
| Free switching in the chat details dialog while the chat is **idle** | The control message is pushed into the chat **immediately**, for example `<harness_notice>Tool source 'shell' is now available (requires confirmation).</harness_notice>`. |
| Free switching while a turn is running | The change becomes effective after the turn; the control message is pushed then. |

Presentation and transport:

- Control messages are persisted as `ChatMessage` with `Kind = "Control"` at their chronological position. Old clients
	ignore the kind (convention credo).
- The UI renders them **not** as a blue chat bubble, but as a translucent "glass" ghost bubble (also used for
	`SKILL 'x' loaded`).
- Pushing does not trigger an LLM answer of its own. The control messages reach the provider in front of the next user
	message (as part of the same turn), because the model can only act on them within a turn anyway. Several changes in a
	row are therefore delivered together and in order.

### 3.9 Contract additions (sketch)

A new contract keeps `IPromptingSettingsService` small (name to be decided):

```csharp
public interface IPromptingExtensionService {

	SkillSourceInfo[] GetSkillSources();
	string AddSkillFileReference(string filePath);                         // SKM-2, local or UNC path, returns SourceId
	string AddSkillSource(string kind, string location, string credential); // SKM-3/4
	void RemoveSkillSource(string sourceId);
	void SetSkillSourceLevel(string sourceId, int level);
	void SetSkillSourceSecureAvailability(string sourceId, bool enabled);  // SKM-8
	void SetSystemPreferencesLocation(string urlOrUncPath);                // SKM-5

	ToolSourceInfo[] GetToolSources();                                     // providers + their tools + levels
	string AddMcpServer(string url, string token);                         // TLM-2
	ContractInterfaceInfo[] UploadToolAssembly(string fileName, byte[] content); // TLM-3
	string RegisterContractInterface(string assemblyId, string interfaceName, string url, string token);
	void RemoveToolSource(string sourceId);
	void SetToolSourceLevel(string sourceId, int level);
	string GetToolProviderSettingsUi(string sourceId);                     // HTML of the detail tab or null (3.2.1)
	string GetToolProviderSettings(string sourceId);                       // the one JSON settings object
	void SaveToolProviderSettings(string sourceId, string settingsJson);   // validated by the provider

}
```

`IPromptingUIBackend` additions:

```csharp
ChatDetails GetChatDetails(string chatId);                     // name, provider, model, source states, used flags
void SetChatProvider(string chatId, string providerName, string model);
void RaiseChatSkillSourceLevel(string chatId, string sourceId, int level);
void SetChatToolSourceLevel(string chatId, string sourceId, int level);
void AnswerInteraction(string chatId, string requestId, string decision);
ToolAuditEntry[] GetToolAuditLog(string chatId, string sourceId);
```

`LLMProviderInfo` addition: `string[] KnownModels` (optional, empty = free text only).

`LLMCapabilityNames` addition: `PausableToolCalls` (3.5).

## 4. Examples

### 4.1 Getting started

1. Settings → tab "Skills": drop `\\server\skills\csharp-style\SKILL.md` into the list (it is referenced, not copied),
	 level "Available (on demand)". If the share becomes unreachable, the entry shows an exclamation mark.
2. Settings → tab "Tools" → "File system": add `\\server\share\specs` as RO. Level "Usable (with confirmation)".
3. New chat (default provider OpenAI). Ask: "Summarize the specs in \\server\share\specs".
4. The model calls `fs_read`. The approval dialog appears; "Allow for this chat". The log icon of the file system
	 source turns blue. The status line shows "calling tool fs_read (3/12)" while files are read.

### 4.2 Advanced: switching provider and escalating rights in a running chat

- A chat started with OpenAI is switched to GitHub Copilot in the details dialog; the model combo offers the
	provider's `KnownModels`, a custom model name is typed in. The next turn rebuilds the context from the outer history.
- The user enables the hidden skill source "team-guidelines" for this chat only (one-way); a control message appears
	as ghost bubble and reaches the model with the next turn.
- The model wants to run `dotnet build` via `shell_run`. The command is not in the shell allow-list → request dialog →
	allowed; the literal line is stored. Later the user edits it to `regex:^dotnet (build|test)\b.*$`.
- The Copilot CLI's last output line is shown as live status text while it works.

Use case: one chat can evolve from "pure Q&A" to "agent with controlled rights" without restarting and without
granting global rights.

## 5. Requirements (candidates)

All ids of section 2 (SKM-1..7, TLM-1..6, OOB-1..4, CHD-1..8, APR-1..3, PRG-1..4) plus:

| Id | Requirement |
|---|---|
| SEC-2 | Guards (3.4) are enforced inside the tools and cannot be bypassed by an approval. |
| SEC-3 | Tokens/PATs of skill and tool sources are stored via `ICredentialStore`; settings and UI only see hints. |
| SEC-4 | The audit log never contains secrets; argument summaries are truncated. |
| CMP-1 | Old clients ignore new callbacks, message kinds and fields (convention credo). |
| CMP-2 | `onProgress` may be `null` in every implementation of `ICommonLLM`. |
| CAP-7 | New capability `PausableToolCalls` selects silent pause (available) or explicit wait notice (unavailable/opted out) during approvals. |
| UIC-1 | Tool provider detail tabs follow the settings UI contract (sandboxed iframe, `postMessage`, exactly one JSON settings object, no secrets). |
| LOG-1 | All DevLogger event kinds of the whole AiSupport project use the approved `MyLogEventKind` range **75300-75399** (sub-ranges see 7). |

## 6. Risks

- Loading foreign DLLs executes foreign code in the host process. Only explicitly dropped DLLs are loaded; isolation is
	limited to an `AssemblyLoadContext` (no sandbox).
- "Initialize at start" for large sources can exceed context windows; the UI warns, the harness should report the size.
- Provider switches lose provider-side state (e.g. Copilot session); the rebuild costs tokens once.
- Pausing a turn for user input keeps provider connections open; inner harnesses may time out (therefore
	`PausableToolCalls`, 3.5).
- The shell tool is the most dangerous tool: regex patterns that are too broad effectively disable the guard.
- Referenced skill files (SKM-2) can change or vanish at any time; pinned content protects running chats only.
- The HTML detail tabs of foreign providers are foreign code in the UI; the sandbox (3.2.1) is mandatory, not optional.

## 7. Decisions (answered by the user)

| # | Topic | Decision |
|---|---|---|
| D1 | Progress delegate | `Action<string, int, int> onProgress` (statusText, currentItem, totalItems). |
| D2 | Waiting for approvals | Silent pause or explicit wait notice, selected per provider by the new capability `PausableToolCalls` (3.5). |
| D3 | Meaning of "session" | The chat (persisted, survives restarts). |
| D4 | Granularity of tool switches | Per tool source for now (per-tool overrides later). |
| D5 | Location of the preferences skills | `%PUBLIC%\.agents\skills\global-system-preferences\SKILL.md` and the equivalent `%USERPROFILE%\.agents\skills\global-user-preferences\SKILL.md`. |
| D6 | Control messages | No message when the change answers a running request; otherwise pushed immediately into the chat (after the turn if one is running), rendered as glass ghost bubble (3.8). |
| D7 | Writing preferences | Allowed for both skills; naturally impossible for URL locations (tool error). |
| D8 | Detail tabs | HTML + JS, strictly limited to one JSON settings object in/out; norm documented in the skill `ai-harness-development` (3.2.1). |
| D9 | Per-chat skill switches | Per source. |
| D10 | GitHub sources | Command-line `git` (SKL-10). |
| D11 | Cancellation | `CallChatApi` gets a `CancellationToken` in the same change. |
| D12 | Logging | `MyLogEventKind` range **75300-75399** for the whole AiSupport project. Proposed sub-ranges: 75300-75339 MCP facade (existing), 75340-75359 chat middle layer and session store, 75360-75379 skill/tool sources, guards and approvals, 75380-75399 LLM connectors. |
| D13 | Skill files via drag and drop | Referenced, never copied; UNC paths allowed; `X` only deregisters; exclamation mark for broken references (SKM-2). |

Still open:

- Confirmation of the proposed logging sub-ranges (D12).
- Discovery of foreign providers: plain type scan vs. SmartStandards ComponentDiscovery (3.2).

## 8. Work packages

| # | Package |
|---|---|
| 1 | `ICommonLLM.CallChatApi` progress delegate and `CancellationToken` (contract, OpenAI, Copilot, mock, test fakes) |
| 2 | `SendMessage` callbacks (`onProgress`, `onInteractionRequired`), `AnswerInteraction`, UI status line/progress bar, fixed 1 s polling |
| 3 | Per-chat provider/model (`ChatSession`, LLM factory per provider/model, context rebuild on switch, `KnownModels`) |
| 4 | Chat details dialog (`...` button, name, compress, provider, model) |
| 5 | Skill source management (settings model, file references incl. broken-reference detection, URL, folder, GitHub + PAT, built-in `global-*-preferences` skills, levels, migration of `UserInstructions`) |
| 6 | Tool provider model (`IHarnessToolProvider`, context, discovery, sandboxed HTML detail tab with JSON settings contract, JSON editor fallback) |
| 7 | OOB tools: preferences update, file system + `PathGuard`, internet + `DomainGuard`, shell + `ShellGuard` |
| 8 | MCP server sources and UJMW contract interface sources (DLL upload, interface selection, URL + token) |
| 9 | Approval flow (source levels, dialog with three buttons, per-chat overrides, guard requests, capability `PausableToolCalls`) |
| 10 | Per-chat skill/tool source switches with control messages (ghost bubbles) |
| 11 | Audit log (store, log icon state, viewer) |
| 12 | MSTest coverage for guards, level resolution, notices, provider switch and progress forwarding |
| 13 | Documentation (requirements, architecture, quickstart) and the project skill |
