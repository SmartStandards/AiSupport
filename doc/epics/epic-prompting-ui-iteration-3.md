# Epic: Prompting UI Iteration 3 (Harness Profiles, Server-Side Tool Catalogs, Contextual Chat Applications)

> Status: **concept, not yet an active requirement.** Nothing in this epic is implemented automatically. Accepted parts
> move into [requirements.md](../requirements.md) and
> Builds on [epic-prompting-ui-iteration-2.md](epic-prompting-ui-iteration-2.md) (skill/tool sources, levels, guards,
> approvals, audit log, chat details) and [epic-skills-and-tools.md](epic-skills-and-tools.md) (harness mechanics).
>
> Order of work: **part A (harness profiles in the client) is implemented first.** Part B (server-side catalogs,
> multi-hosting, authorization) is discussed regarding feasibility afterwards; its sections are proposals for that
> discussion. Part C (tool DLLs referenced in place) is independent and can be done together with part A.

## 1. Abstract and motivation

After iteration 2, everything that makes a chat useful is configurable in the UI: providers and models, capability
opt-outs, skill sources with levels, tool sources with levels and allow-lists. But there is exactly **one** such
configuration per user. In practice, the same person needs clearly separated setups: a restrictive "customer project"
setup without shell access, a "private research" setup with internet access only, a "ops" setup with powerful tools and
narrow allow-lists. Switching between them by hand is error-prone and defeats the security model.

Iteration 3 introduces the **harness profile**: the complete set of everything that can be configured in the UI, as a
named, copyable and switchable unit.

On the server side, the same idea becomes the foundation for **contextually charged chat applications**: one web
service hosts the chat UI several times, each under its own URL with its own profile and pre-built tooling (for example
a chat over the logging data warehouse whose tools come from the service itself). Who may use which profile, and who
may configure it, is decided by the access token.

### 1.1 Positive differentiation

| Aspect | Typical AI clients | This epic |
|---|---|---|
| Configuration sets | One global configuration, or per-workspace files edited by hand | Named profiles, created by copying the current one, switchable in the UI |
| Start-up | Always the last state | Optional profile selection at start; command-line override for shortcuts/scripts |
| Security separation | Same tool rights everywhere | Tool levels and allow-lists are part of the profile: a "safe" profile really has no shell |
| Hosting | One chat application per deployment | One service hosts several chat applications (URL sub-route = profile) with pre-built tooling |
| Tool delivery | Plugins installed by the user | Tools/skills contributed by the hosting application via DI factory catalogs; MCP facade tools are reused in-process |
| Governance | All-or-nothing admin rights | Per profile: use vs. configure, decided by the access token |
| Foreign tool DLLs | Copied into a plugin folder, dependencies missing | Referenced in place; the original folder is probed for dependencies |

## 2. Features as understood

Ids are working ids of this epic.

### 2.1 Part A: harness profiles (client)

| Id | Feature |
|---|---|
| PRF-1 | A **harness profile** contains all settings that can be configured in the UI: default provider/model, API keys/tokens, capability opt-outs, skill sources with levels/labels/secure availability, tool sources with levels and settings objects (allow-lists), registered MCP servers and contract interfaces. **Not** part of a profile: the theme and the user instructions (decided, Q1/Q5). |
| PRF-2 | The settings dialog gets a new tab **"Profiles"** at the **last position**. |
| PRF-3 | **New profile:** creates a copy of the **current** profile under a new name, **including** API keys and source tokens (stored protected; decided, Q2). |
| PRF-4 | **Switch profile:** the list shows all profiles; the active one is marked. Switching applies the profile to the running client (see 3.3). |
| PRF-5 | **Rename / delete** profiles (the active profile and the last remaining profile cannot be deleted). |
| PRF-6 | Checkbox **"Ask for the profile at start"**: if enabled and more than one profile exists, the client shows a selection dialog (combo box) at start. |
| PRF-7 | The profile chosen **in the start dialog** is remembered and preselected next time (it becomes the active profile). |
| PRF-8 | A command-line argument (`--profile <name or id>`) selects the profile at start and **overrides** both the remembered profile and the start dialog (also when "ask at start" is enabled). It is not remembered. An unknown profile name is an error message, not a silent fallback. |
| PRF-9 | Existing installations are migrated without loss: the current configuration becomes the profile **"Default"**. |
| PRF-10 | The window title (and the settings header) shows the active profile, so the user always knows which rights are active. |
| PRF-11 | **Full chat isolation by profile and user:** the chat history is scoped by the pair **(profile, user)**. The chat list shows only the chats of the active profile **and** of the current user; there is no "all profiles" view (decided, Q3). In the local desktop client there is only one user, so effectively only the profile scope applies; in ASP.NET Core hosting a user must **never** see, open, continue, rename, delete or answer approvals of chats of another user (see AUTH-4). |
| PRF-12 | When a profile is started, a **provider combo box** is visible on the start/new-chat surface, preselected with the default provider of the profile. |

### 2.2 Part B: server side (to be discussed)

| Id | Feature |
|---|---|
| SRV-1 | ASP.NET Core: **factory catalogs** for out-of-the-box **tool providers** and **skill sources** are resolved from the service container. The hosting web application contributes them as OOB sources (for example a later logging-DWH tool provider). |
| SRV-2 | `ai.AddMcpFacade(...)`: the controller actions **included in the MCP facade** are automatically registered as an OOB tool provider of the chat UI as well (in-process invocation, no loopback HTTP). |
| SRV-3 | When a chat UI (more precisely: a profile) is added in ASP.NET Core, the **own MCP endpoints of the host's MCP facade/proxy** are known to the profile **by default** (pre-registered as MCP server/tool source, removable or level-adjustable like any other source; decided, Q7). |
| SRV-4 | The demo service hosts the chat UI **several times**: a URL sub-route selects the profile (for example `/chat/{profile}/`). Using the UJMW contextualization hook, the profile is extracted from the route and stored **ambient** for the request. |
| SRV-5 | Sessions running in the server (turns continuing on background threads, approvals, polling) automatically use the profile of their chat, not the profile of an arbitrary later request. |
| SRV-6 | Target picture: **contextually charged chat applications with pre-built tooling**, hosted by any SmartStandards service. |
| SRV-7 | Server side, **user instructions belong to the user identity** (subject from the access token), not to a profile; every profile a user opens uses the same user instructions (decided, Q1). |

### 2.3 Part B: authorization (to be discussed)

| Id | Feature |
|---|---|
| AUTH-1 | The access token decides **which profiles a user may use** (see chats, send messages, answer approvals). |
| AUTH-2 | The access token decides **who may configure a profile** (sources, levels, allow-lists, provider, credentials). Users without this right see the settings read-only or not at all. |
| AUTH-3 | "Allow permanently" on guard requests (extends a global allow-list) is a configuration change and therefore requires AUTH-2. Users with use rights only get "Deny" / "Allow once". |
| AUTH-4 | **Chats are owned by a user.** Every chat stores its owner (subject from the access token) and its profile. Every chat operation (list, load, send, poll, approve/answer, cancel, rename, compress, delete, details) is checked server-side against owner **and** profile; a foreign chat id is treated like a non-existing one (no information leak). This is a hard prerequisite for multi-user hosting, not an optional extension. |

### 2.4 Part C: tool DLLs referenced in place

| Id | Feature |
|---|---|
| TDL-1 | Tool DLLs (UJMW contract interfaces, later foreign tool providers) are **not uploaded/copied** anymore, but **referenced at their original location** (like skill file references, SKM-2). |
| TDL-2 | The original folder is probed when dependencies are resolved: in the `AssemblyLoadContext.Resolving` event **and** in `AppDomain.CurrentDomain.AssemblyResolve`, so that sub-references of the tool DLL can be loaded. |
| TDL-3 | A missing or unreadable referenced DLL marks the source as broken (exclamation mark with reason), the registration is kept. |
| TDL-4 | Existing copies below `ToolAssemblies` stay valid (no forced migration). |

### 2.5 Chat list improvements

| Id | Feature |
|---|---|
| CLS-1 | **Context menu** on every chat in the chat list (left) with Delete, Rename, Compress and Details (the same actions as in the chat header). |
| CLS-2 | The chat list is **sorted by the time of the last activity** (newest first). |
| CLS-3 | Below the title, the last activity is shown small and gray: relative (`20 min ago`, `1 h ago`) for recent activity, the **time** for older activity of today, the **date** before today. Proposal for the thresholds: < 1 min `just now`, < 60 min `N min ago`, < 3 h `N h ago`, otherwise today `HH:mm`, otherwise the date. |
| CLS-4 | Chats that are **currently interacting with the AI** (running turn, including waiting for tool results) show an **animated busy indicator** (rotating blue donut) in the chat list. Waiting for a user approval/answer is not "active" in this sense and keeps the existing marker. |

## 3. Design (part A and C)

### 3.1 Profile storage

Today, the client configuration lives in `%APPDATA%\SmartStandards\AiSupport\`: `settings.json`
(`FileBasedPromptingSettingsService`), `extensions.json`, `extensions.credentials.json`, `tools.json` and
`ToolAssemblies\` (`FileBasedPromptingExtensionService`).

Proposal:

```text
%APPDATA%\SmartStandards\AiSupport\
	profiles.json                      // index: profiles, active profile, ask-at-start flag
	settings.json, extensions.json ... // profile "default" (unchanged location = zero-cost migration, PRF-9)
	profiles\
		<profileId>\
			settings.json
			extensions.json
			extensions.credentials.json    // protected, copied with the profile (Q2)
			tools.json
```

```json
{
	"activeProfileId": "default",
	"askAtStartup": false,
	"profiles": [
		{ "profileId": "default", "displayName": "Default", "createdUtc": "2025-01-01T00:00:00Z" },
		{ "profileId": "p-7f3a", "displayName": "Customer project (no shell)", "createdUtc": "..." }
	]
}
```

- Profile ids are generated and stable; display names are free text (same principle as skill source labels).
- **Not** part of a profile (decided, Q1/Q5): the user preferences skill `global-user-preferences` and the system
	preferences skill (they describe the person/the machine, not the setup), and the theme. The chat history is scoped
	by **(profile, user)** (PRF-11, AUTH-4).
- Chats store the `profileId` they were created with (`ChatSession.ProfileId`, null = default for old chats) and their
	owner (`ChatSession.OwnerId`, the token subject; in the desktop client a fixed local user, null = local user for old
	chats). The session store filters by both values; the filter is applied in the store/service, never only in the UI.
- Copying a profile (PRF-3) copies the profile folder; the copy gets a new id and is never linked to the original.

### 3.2 Contract

A new remotable contract (UJMW/WCF rules: synchronous, DTOs, no callbacks):

```csharp
public interface IPromptingProfileService {

	HarnessProfileInfo[] GetProfiles();          // incl. IsActive

	string GetActiveProfileId();

	string CreateProfile(string copyFromProfileId, string displayName); // returns the new id

	void RenameProfile(string profileId, string displayName);

	void DeleteProfile(string profileId);        // not the active one, not the last one

	void ActivateProfile(string profileId);

	bool GetAskAtStartup();

	void SetAskAtStartup(bool enabled);

}

public class HarnessProfileInfo {
	public string ProfileId { get; set; }
	public string DisplayName { get; set; }
	public bool IsActive { get; set; }
	public string CreatedUtc { get; set; }
}
```

The UI calls it at `chatApi/profiles/`. Old servers without this endpoint simply hide the "Profiles" tab (convention
credo).

### 3.3 Switching at runtime

All profile-dependent services (`FileBasedPromptingSettingsService`, `FileBasedPromptingExtensionService`, the
`HarnessToolbox`) are bundled into a `HarnessProfileRuntime`. A `HarnessProfileManager` owns the active runtime;
`CheapPromptingUIBackend` resolves the runtime **per chat** (by `ChatSession.ProfileId`), which is also the basis for
part B (SRV-5).

Switching the active profile (PRF-4):

1. Running turns keep their runtime until they finish (they captured it at turn start).
2. The new runtime is created (sources resolved, tools created) and becomes active.
3. The UI reloads settings, providers and the chat list (filtered to the new profile, PRF-11).

A restart of the client process with `--profile <id>` is **only an emergency fallback** (decided, Q4), for example if
the runtime switch fails; it is not the regular path.

### 3.4 Start-up (desktop client)

`SmartStandards.AiSupport.ChatClient` gets `Main(string[] args)`:

1. `--profile <name|id>` given: use it (PRF-8), skip the dialog, do not remember it.
2. Otherwise, `askAtStartup` and more than one profile: show a small WinForms dialog with a combo box (preselected: the
	 remembered profile) before the in-memory ASP.NET Core engine is started. The choice is remembered (PRF-7).
3. Otherwise: the remembered active profile.

The chosen profile is passed to the service registration before the engine starts, so no runtime switch is needed at
start.

### 3.5 Tool DLLs referenced in place (part C)

Today, `UploadToolAssembly` copies the DLL to `ToolAssemblies\<AssemblyId>\` and `ContractInterfaceToolProvider` loads
it into a non-collectible `AssemblyLoadContext` with a `Resolving` handler that probes that directory.
`AppDomain.AssemblyResolve` is not handled.

Proposal:

- New registration operation `AddToolAssemblyReference(string fullPath)` (local or UNC path), persisted as path; the
	upload operation stays for remote UIs (server scenario) but is no longer used by the desktop client.
- A process-wide `ToolAssemblyResolver` keeps the set of **original directories** of all referenced tool DLLs. It is
	attached to `AssemblyLoadContext.Resolving` of each tool load context and once to
	`AppDomain.CurrentDomain.AssemblyResolve` (TDL-2). Probing order: directory of the requesting assembly, then all
	other registered directories; first match by simple name and compatible version wins.
- Tool DLLs are **loaded from their path** (decided, Q6). The file lock while the client runs is accepted; loading from
	bytes is not used, so `Assembly.Location` stays valid (risk R4).

## 4. Design proposals for the discussion (part B)

### 4.1 DI factory catalogs (SRV-1, SRV-2)

```csharp
public interface IHarnessToolProviderFactory {
	string SourceId { get; }
	IHarnessToolProvider CreateProvider(IServiceProvider services, string profileId);
}

public interface IHarnessSkillSourceFactory {
	string SourceId { get; }
	SkillSourceInfo Describe(string profileId);
	// delivers the skill files (name, description, body) of this source
}

services.AddHarnessToolProvider<LoggingDwhToolProviderFactory>();
services.AddHarnessSkillSource<LoggingDwhSkillSourceFactory>();
```

- `FileBasedPromptingExtensionService` (or its profile runtime) enumerates all registered factories and registers the
	created providers as built-in sources (fixed, not removable, level and settings still configurable per profile).
- `AddMcpFacade` registers one `McpFacadeToolProviderFactory`: every included operation becomes a harness tool, invoked
	through the same in-process invoker as the MCP facade (no loopback HTTP, same authorization).
- Feasibility: high. The provider model (`IHarnessToolProvider`) already exists; only the DI enumeration and the
	in-process invoker adapter are new.

### 4.2 Multi-hosting by URL sub-route (SRV-4, SRV-5)

```csharp
app.MapChatUi("/chat/{profile}/");   // one UI, many profiles
// or explicitly:
app.MapChatUi("/chat/dwh/", "logging-dwh");
app.MapChatUi("/chat/support/", "support");
```

- A UJMW contextualization hook reads the `{profile}` route value and stores it as ambient field
	(`harnessProfile`) for the request. All chat services resolve the profile runtime from that ambient value.
- Chats persist their `ProfileId` (3.1); background work of a turn uses the runtime captured at turn start, not the
	ambient value of a later request (SRV-5). A request with a profile that does not match the chat is rejected.
- Feasibility: medium. To be verified: availability of the contextualization hook for route values in the UJMW
	version used, flow of ambient fields into long-running operations (polling requests vs. the worker thread), and the
	embedded UI's base path handling (`chatApi/` relative URLs already work below any mount path).
- Server profiles are defined by the host (code/config), not created by end users; the "Profiles" tab is hidden or
	read-only there.

### 4.3 Authorization via access token (AUTH-1..3)

- Proposal: two permission kinds per profile, derived from token claims/scopes, for example
	`harness-profile:<id>:use` and `harness-profile:<id>:configure` (wildcards for admins).
- Enforcement in the services, not in the UI: write operations of `IPromptingSettingsService`,
	`IPromptingExtensionService` and `IPromptingProfileService` require `configure`; chat operations require `use`.
- Chats are bound to the **user** (subject of the token) **and** the profile (AUTH-4); today the session store is
	single-user. A multi-user session store (owner-aware storage and checks in every chat operation) is a mandatory
	prerequisite of multi-hosting and possibly larger than the profile authorization itself.
- User instructions are resolved per **user identity** (token subject), independent of the profile (SRV-7).
- Feasibility: medium to high effort; depends on AuthTokenHandling claim conventions (to be clarified).

## 5. Examples

### 5.1 Getting started: two desktop profiles

1. Settings - Profiles - "New profile" - name "Customer project". The current configuration is copied.
2. With "Customer project" active: Settings - Tools - set "Shell access" to Hidden, restrict file system access to
	 `C:\Customer\` (RO).
3. Enable "Ask for the profile at start". From now on, the client asks at every start; the last choice is preselected.

### 5.2 Advanced: shortcut per profile and a hosted DWH chat

- Desktop: a Windows shortcut `SmartStandards.AiSupport.ChatClient.exe --profile "Customer project"` opens the
	restricted setup directly, even if "ask at start" is enabled - useful for screen sharing with customers.
- Server (part B): the logging service registers `AddHarnessToolProvider<LoggingDwhToolProviderFactory>()`, uses
	`AddMcpFacade` for its query API and maps `MapChatUi("/chat/dwh/", "logging-dwh")`. Support engineers open
	`/chat/dwh/`, get a chat with exactly the DWH tools; only operators (configure right) may change allow-lists.

## 6. Requirements (candidates)

AUTH-1..4, TDL-1..4, CLS-1..4) plus:

| Id | Requirement |
|---|---|
| CMP-3 | Clients without profile support keep working against servers with profiles (default profile); the UI hides the "Profiles" tab if the service is missing. |
| SEC-5 | Switching or copying profiles never leaks credentials into profile files in plain text (copied credentials are stored in the protected credentials file of the new profile). |
| SEC-6 | A running turn never changes its tool rights because another request switched the profile (runtime captured per turn). |
| LOG-2 | Profile operations are logged via DevLogger in the sub-range 75340-75359 (chat middle layer) or 75360-75379 (sources); no profile content is logged. |

## 7. Risks

- R1: Runtime profile switching touches every service lifetime; subtle bugs (old toolbox still referenced) are likely.
	Mitigation: per-chat runtime resolution and tests; restart only as emergency fallback (Q4).
- R2: Copying a profile with credentials duplicates secrets (accepted, Q2; the user can remove them in the copy).
- R3: Multi-hosting requires a multi-user session store (AUTH-4); the current file-based store is single-user. A missing
	owner check in any single chat operation would leak foreign chats.
- R4: Loading tool DLLs from the original location may lock files of a build output folder; loading from bytes breaks
	code that relies on `Assembly.Location`.
- R5: `AppDomain.AssemblyResolve` is process-wide; probing foreign folders can resolve unexpected versions for unrelated
	code. Mitigation: only answer requests whose requesting assembly is a tool assembly, or names not loadable otherwise.
- R6: Tools contributed by the host (SRV-1/2) run with the rights of the service; authorization must be enforced by the
	invoker exactly as for MCP calls.

## 8. Questions and decisions

| # | Question | Decision / status |
|---|---|---|
| Q1 | Are user instructions (`global-user-preferences`) part of the profile? | **Decided:** no, shared by all profiles. Server side they belong to the user identity from the token (SRV-7). |
| Q2 | Are API keys and source tokens copied with a new profile? | **Decided:** yes, copied (protected). |
| Q3 | Is the chat list filtered by profile? | **Decided:** full isolation by **profile and user**: only chats of the active profile and the current user (PRF-11, AUTH-4). |
| Q4 | Runtime switch vs. restart on profile switch? | **Decided:** runtime switch; restart only as emergency fallback. |
| Q5 | Is the theme part of the profile? | **Decided:** no. |
| Q6 | Tool DLLs: load from path (file lock) or from bytes (no lock, no `Location`)? | **Decided:** from path. |
| Q7 | What does "`AddChatUi` gives the MCP endpoints for free" mean? | **Decided:** the host's own MCP facade/proxy endpoints are known to the profile by default (SRV-3). |
| Q8 | Claim convention for AUTH-1/2 (scope names, wildcard syntax) in AuthTokenHandling. | Open, to be clarified in the part B discussion. |

## 9. Work packages

| # | Package |
|---|---|
| 1 | Profile index (`profiles.json`), profile folders, migration of the existing configuration to "Default" |
| 2 | `IPromptingProfileService` contract + file-based implementation (create by copy, rename, delete, activate, ask-at-start) |
| 3 | `HarnessProfileRuntime` / `HarnessProfileManager`, `ChatSession.ProfileId` and `ChatSession.OwnerId`, (profile, user) filtering in the session store, per-chat runtime resolution in `CheapPromptingUIBackend` |
| 4 | UI: settings tab "Profiles" (last position), active profile in title, reload after switch, provider combo box on profile start (PRF-12) |
| 4a | UI chat list: context menu (CLS-1), sort by last activity and relative/time/date subtitle (CLS-2/3), animated busy indicator for active chats (CLS-4) |
| 5 | Desktop client: `Main(string[] args)`, `--profile`, start dialog, remembering the choice |
| 6 | Tool DLL references in place (`AddToolAssemblyReference`, broken-reference detection, `ToolAssemblyResolver` for `Resolving` and `AppDomain.AssemblyResolve`) |
| 7 | MSTest coverage: copy/activate/delete rules, command-line precedence, migration, per-chat runtime capture, resolver probing |
| 8 | Documentation (requirements, architecture, quickstart) and the project skill |
| 9 | Part B discussion: feasibility check of the UJMW contextualization hook and ambient flow, DI factory catalogs, MCP facade tool provider, authorization model, multi-user session store |
