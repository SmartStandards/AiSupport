# Epic: Interactive Prompting (Chat Middle Layer and PoC Chat Client)

> Status: **concept, not yet an active requirement.** Nothing in this epic is implemented automatically. Accepted parts
> move into [requirements.md](../requirements.md) and

## 1. Abstract and motivation

All popular AI client applications offer the same UI pattern: a list of recent chats, a chat history, an input box and a
streamed answer. This repository already has a first draft of a provider-neutral backend contract for such UIs
(`IPromptingUIBackend`) and a low-level provider contract (`ICommonLLM`). It does not have the layer in between yet.

This epic describes:

- a thin **middle layer** between `IPromptingUIBackend` (upwards) and `ICommonLLM` (downwards) that owns the chat
	histories,
- a **provider-agnostic context convention** (`contextStore`) so that providers with server-side conversation state and
	providers without it can be used the same way,
- a **PoC chat client**: a WinForms/WebView2 EXE with an in-memory ASP.NET Core engine. Its JavaScript UI talks real UJMW
	and can also be hosted unchanged in a regular web host,
- the path towards a later **UShell module** with a chat UI.

## 2. Layers

| Layer | Artifact | Responsibility |
| --- | --- | --- |
| UI contract | `IPromptingUIBackend` (`SmartStandards.AiSupport`) | Chats (list, create, rename, delete), messages (count, range, by time), send. UJMW-transportable. |
| Middle layer | `CheapPromptingUIBackend` (`SmartStandards.AiSupport.CommonProviders`) | Implements the UI contract. Builds the LLM context per turn, handles `contextStore` and compression. |
| Persistence | `IPromptingSessionStore`, first implementation `FileBasedPromptingSessionStore` | Full chat history (user and AI messages) plus the context state per chat. |
| Provider | `ICommonLLM`, e.g. `OpenAiLLMConnector` | Executes one chat turn and optionally returns a `contextStore` value. |
| Client | PoC chat client (WinForms + WebView2 + in-memory ASP.NET Core) and web host | Vanilla JS UI served from embedded resources, calling the UI contract via UJMW. |

## 3. Contract changes

### 3.1 `IPromptingUIBackend`

- Rename the overload `GetChatMessages(string chatId, DateTime from, DateTime to)` to `GetChatMessagesByTime`. UJMW and
	WCF address operations by name, so overloads are not transportable.
- `SendMessage` becomes a UJMW **long-running operation** to keep UIs responsive:
	`ChatMessage SendMessage(CancellationToken cancellationToken, string chatId, string message, Action<string> onPartialAnswer)`.
	It returns the final AI message. Partial answers arrive via the callback, and cancellation maps to UJMW `DELETE`.
- `ChatMessage` gets a role (`system` / `user` / `assistant`) in addition to `Author`.
- Explicit compression: `void CompressChatContext(string chatId)` (see 4.3).
- Long-running operations are an ASP.NET Core feature of UJMW. Under `NET_FX` (WCF) the method remains a plain synchronous
	call.

### 3.2 `IPromptingSessionStore`

CRUD for chats and messages, plus the context state per chat:

- `ContextStore` - last opaque value returned by the provider, or `null`,
- `ContextBaseline` - compression text plus the index of the last message it covers, or `null`.

The first implementation (`FileBasedPromptingSessionStore`) stores one JSON file per chat (Newtonsoft.Json).

### 3.3 `ICommonLLM`: chat turn with `contextStore`

```csharp
string CallChatApi(
	CancellationToken cancellationToken,
	ChatTurn[] messages,
	ref string contextStore,
	Action<string> onPartialAnswer
);

public class ChatTurn {
	public string Role { get; set; }     // "system" | "user" | "assistant"
	public string Content { get; set; }
}
```

The existing one-shot methods of `ICommonLLM` stay unchanged. `CallChatApi` is the light chat level below the larger
`IConversationalLLM` concept (tool handshake) described in [ideas.md](../ideas.md), section 1.

## 4. Context convention

### 4.1 Background

LLM APIs are stateless by default: the relevant context has to be sent with every call. Some providers offer
server-side conversation state (e.g. `previous_response_id` of the OpenAI Responses API). Others can return a compressed
snapshot. Callers must not depend on either.

### 4.2 Rules

| Party | Rule |
| --- | --- |
| Provider | May return **any opaque string** in `contextStore`: a server-side id, a compressed context snapshot, or `null` if it supports neither. The provider alone decides. |
| Caller | Treats `contextStore` as opaque and persists it. If it holds a value, the caller sends it again together with **only the new messages**. If it is `null`, the caller sends its own context (see 4.3). |
| Middle layer | Always keeps the **full history** (including AI answers), because the UI shows it, independent of `contextStore`. |

### 4.3 Self-maintained context and compression

If `contextStore` is `null`, the middle layer builds the context itself:

1. If a `ContextBaseline` exists, send the baseline text as the starting point, followed by all messages after the
	 covered index.
2. Otherwise send the full history.

`CompressChatContext(chatId)` is the explicit compression method:

- It sends the current self-maintained context with an explicit request for compression to the LLM.
- The answer becomes the new `ContextBaseline`. From then on, only the baseline and the subsequent messages are sent.
- It is meant for chats with `contextStore == null`.

### 4.4 Invalid `contextStore` (proposal)

Server-side ids can expire. Proposed convention:

- The provider raises a recognizable fault.
- The middle layer resets `ContextStore` to `null` and retries the turn **once** with the self-maintained context. This
	is always possible because the full history is persisted.

## 5. PoC chat client and dual hosting

The general procedure is described in the skill `asp-engine-webview-coupling` (AI-SKILLS-SMART). Applied here:

- New WinForms project (`net10.0-windows`) with WebView2. `WebViewAspNetCoreAdapter` is embedded **via T4** from
	`SmartStandards/AspNetCoreTemplate` (`dotnet/src/WebView2Template`). The base address is the custom scheme `app://local`.
- The UI files (vanilla HTML/CSS/JS, no build step) are **embedded resources**: no `wwwroot`, no loose files. They are
	served via `EmbeddedFileProvider`, following the UniversalBFF pattern.
- The UI contract is exposed as a UJMW controller. The JS client implements the UJMW long-running protocol (initiate
	with `wait`, poll with `pollingHash` respecting `pollNbf`, cancel via `DELETE`, replay callbacks in `#` order). The
	reference implementation is the UJMW Test Client.
- **Dual hosting:** a UI library offers `AddChatUi(services, config)` / `UseChatUi(app, mountPath)`. The EXE mounts the
	UI at `/`, and the existing AspNetCore host additionally mounts it at `/chat/`. The UI uses relative URLs only.

### 5.1

The PoC client is **not** a UShell module. It is a lighter predecessor. Contracts, services and wire protocol already
match what a UShell module needs, so only the UI shell changes later (UniversalBFF hosting, UShell frontend stack).

## 6. Open points

1. Compression trigger: explicit only (UI button / `CompressChatContext`), or additionally automatic above a threshold?
2. Fault convention for an invalid `contextStore` (4.4): exact fault shape.
3. Naming of the UI library and the EXE project.
4. Attachments (`//TODO` in `IPromptingUIBackend`) - out of scope for the first iteration.
5. Logging: `MyLogEventKind` range for the new components (to be approved).
6. Unit tests (MSTest) for the store and the middle layer, using a fake `ICommonLLM`.

## 7. Work packages

| # | Package |
| --- | --- |
| 1 | Contract changes (`IPromptingUIBackend`, `ChatMessage`, `IPromptingSessionStore`, `ICommonLLM.CallChatApi`, `ChatTurn`) |
| 2 | `FileBasedPromptingSessionStore` |
| 3 | `CheapPromptingUIBackend` incl. context building and compression |
| 4 | `OpenAiLLMConnector.CallChatApi` (Responses API, `previous_response_id` as `contextStore`) |
| 5 | UI library: embedded vanilla UI, JS UJMW long-running client, `AddChatUi` / `UseChatUi` |
| 6 | WinForms/WebView2 EXE with T4-embedded adapter |
| 7 | Additional mount in the existing AspNetCore host |
| 8 | Documentation (requirements, architecture, quickstart) and the project skill |
