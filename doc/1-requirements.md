# Requirements

## Goal

**SmartStandards AI Support** provides provider-neutral abstractions for AI-enabled applications, together with
reusable standard implementations and ASP.NET Core hosting support. Applications program against stable contracts
instead of concrete LLM providers, chat front ends or storage.

## Target audiences

- .NET developers who integrate AI features (prompting, chat, knowledge access) into their own applications or services.
- Operators of services who want to expose these features over the network via **UJMW**, and therefore also to AI agents
  (e.g. through MCP adapters).

## Functional requirements

### Abstractions (package `SmartStandards.AiSupport`)

| Contract | Purpose |
|---|---|
| `ICommonLLM` | Low-level access to an LLM: web-search prompting (text or typed JSON result), image generation, image editing. Must be exposable via UJMW. |
| `IPromptingUIBackend` | Backend for chat front ends: chat history, create, rename and delete chats, send messages (file attachments planned). |
| `IPromptingSessionStore` | Persistence of prompting sessions; basis for an adapter between `IPromptingUIBackend` and `ICommonLLM`. |
| `IPromtLibrary` | Management of reusable prompts. |
| `ICodeArtifactMap` | Semantic description of code artifacts and components per repository URL. |

Access to knowledge stores (`IKnowledgeRepository`) has moved to the separate `SmartStandards.KnowledgeManagement*`
packages and is only consumed here.

### Standard implementations (package `SmartStandards.AiSupport.CommonProviders`)

- `OpenAiLLMConnector` as an implementation of `ICommonLLM` against the OpenAI API.
- File-based implementations of the session store, prompt library and code artifact map, plus a simple prompting UI backend.
- `DynamicAiServiceFactory`: creates a runtime implementation for any .NET interface whose method calls are delegated
  to an `ICommonLLM` (pure AI communication; AI-generated code is prepared but not yet implemented).

### Hosting (package `SmartStandards.AiSupport.AspNetCore`)

- Integration into ASP.NET Core via `AddAiSupport(...)`, so that the contracts can be provided as UJMW endpoints.
- An opt-in generic MCP facade for ASP.NET Core applications. It exposes only explicitly selected controller actions
  as one MCP tool per selected API operation, derives tool metadata from ASP.NET Core API metadata, maps route, query
  and JSON-body arguments, and executes target actions in process without loopback HTTP.

## Non-functional requirements

- **Target platforms:** .NET Framework 4.8, .NET 8.0 and .NET 10.0 for the contracts; .NET 8.0 and .NET 10.0 for the providers.
- **Transportability:** all contracts must remain remotable via UJMW (and additionally via WCF attributes on .NET Framework).
  This means simple DTOs, synchronous methods and no callbacks or events.
- **Provider neutrality:** provider specifics (endpoints, model names, formats) must not leak into the contracts.
- Delivered as NuGet packages. Versioning is done automatically by the build pipeline.

## Non-goals

- No chat user interface of its own; only the backend is provided.
- Knowledge stores (`IKnowledgeRepository` and its providers) are no longer part of this repository.
- The **Level-2 AI harness** (tool-calling handshake, CLI proxies, local LLM, MCP client work) is not an active
  requirement yet. See [ideas.md](ideas.md).
- The full OAuth/CalDAV MCP demo scenario is not part of the first reusable facade slice yet.
