# Architecture

## Overview

```text
┌─────────────────────────────────────────────────────────────┐
│ Applications / services (e.g. DemoWebService)                │
└───────────────┬─────────────────────────────┬───────────────┘
                │ program against              │ host via UJMW
┌───────────────▼──────────────┐  ┌────────────▼──────────────┐
│ SmartStandards.AiSupport      │  │ SmartStandards.AiSupport. │
│ (contracts, net48/net8/net10) │  │ AspNetCore (hosting)      │
│  ICommonLLM, IPromptingUI-    │  └───────────────────────────┘
│  Backend, IPromptingSession-  │
│  Store, IPromtLibrary,        │
│  ICodeArtifactMap             │
└───────────────▲──────────────┘
                │ implement
┌───────────────┴──────────────────────────────────────────────┐
│ SmartStandards.AiSupport.CommonProviders (net8/net10)          │
│  OpenAiLLMConnector · file-based stores · CheapPrompting-      │
│  UIBackend · DynamicAiServiceFactory (UJMW → LLM pipeline)     │
└───────────────────────────────────────────────────────────────┘
```

## Building blocks

| Package / project | Content |
|---|---|
| `SmartStandards.AiSupport` | Pure contracts and DTOs (namespaces `AI.SmartStandards.*`, e.g. `…LowLevelPrompting`, `…InteractivePrompting`, `…KnowledgeAccess`). No dependencies on providers. |
| `SmartStandards.AiSupport.CommonProviders` | Standard implementations of the contracts and the `DynamicAiServiceFactory`. |
| `SmartStandards.AiSupport.AspNetCore` | Registration of services and endpoints in ASP.NET Core (`AddAiSupport`), including the opt-in generic MCP facade over selected controller actions. |
| `SmartStandards.AiSupport.DemoWebService` | Example host: SmartStandards logging, UJMW auth-header check, integration of external knowledge repositories (including a Joplin WebDAV endpoint). |
| `test/SmartStandards.AiSupport.Tests` | MSTest project; tests against real providers are marked `[Ignore]`. |

The source code lives in **shared projects** (`*.shproj`/`*.projitems`). The actual per-platform projects
(`*.net48`, `*.net8.0`, `*.net10.0`) include them. Platform-specific parts are distinguished via compiler constants,
e.g. `NET_FX` for the WCF attributes on the contracts.

## Key flows

### LLM call via `ICommonLLM`

The caller passes a prompt and optional input data. For typed calls (`CallWebSearchApi<T>`) the connector derives a
sample JSON structure from `T` as a format specification for the model and deserializes the answer back into `T`.
`OpenAiLLMConnector` uses the OpenAI Responses API with web search as well as the Images API.

### Dynamic AI services (`DynamicAiServiceFactory`)

`DynamicAiServiceFactory.CreateInstance<TContract>()` creates a proxy for any interface via UJMW's
`DynamicClientFactory`. Each method call is turned into a prompt (method name, XML doc comments and arguments), sent to
the configured `ICommonLLM`, and the result is converted back into the return type.

### Generic MCP facade

ASP.NET Core hosts can register the MCP facade through `AddAiSupport(...)` and map it through endpoint routing. The
facade indexes ASP.NET Core API metadata, but publishes only operations explicitly included by the host. Each included
operation becomes a deterministic MCP tool with its own JSON input schema. Tool calls are dispatched inside the process
with an isolated `HttpContext` and DI scope, so MVC model binding, validation, filters, result execution and target
authorization metadata remain part of the call path without using loopback HTTP.

Agent-facing quality rules of the facade:

- Tool descriptions and argument descriptions come from XML documentation; references to sibling C# methods are
  rewritten to the corresponding MCP tool names.
- Non-optional contract parameters (also when UJMW flattens them into one JSON body) are listed as `required`.
- Failures are returned as `isError` results with a single plain `message`; exception types, parameter names and
  stack traces are never sent to the agent (they are logged on the server instead).
- The facade is intentionally stateless: no `Mcp-Session-Id` is issued, every request is self-contained.

## Architecture decisions

- **Remotability over convenience:** contracts stay synchronous and DTO-based so that UJMW and WCF can transport them.
- **Contracts and implementations are separate packages**, so consumers only need to reference the contracts.
- **Knowledge stores moved out:** `IKnowledgeRepository` and its providers live in `SmartStandards.KnowledgeManagement*`.
- **MCP facade is opt-in:** discovering controller actions never publishes them automatically. Hosts explicitly include
  selected API operations and keep existing routes, authentication and public contracts unchanged.
- The planned extension by a Level-2 AI harness, including the design decisions already made for it, is described in
  [ideas.md](ideas.md). It will be moved here once implemented.
