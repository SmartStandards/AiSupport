# SmartStandards - AI Support

## About

Provider-neutral .NET contracts for AI-enabled applications, together with standard implementations and ASP.NET Core
hosting. All contracts are remotable via **UJMW**, so they can be used across the network and by AI agents.

- `ICommonLLM`: low-level access to an LLM (web-search prompting with typed answers, image generation, image editing)
- `IPromptingUIBackend`: backend for chat front ends (chat history, file attachments later)
- `IPromptingSessionStore`: persistence of prompting sessions (basis for an adapter between the UI backend and `ICommonLLM`)
- `IPromtLibrary`: reusable prompts
- `ICodeArtifactMap`: semantics and components per repository URL

## Motivation

Applications should program against stable contracts instead of individual LLM providers. This makes providers
interchangeable (cloud APIs, local models, agent CLIs), and the same features are available to remote services and
agents via UJMW.

## Examples

```csharp
DynamicAiServiceFactory.AiOperationsProvider = new OpenAiLLMConnector("<OPENAI_API_KEY>");
IMyTool tool = DynamicAiServiceFactory.CreateInstance<IMyTool>(); // an LLM "implements" the interface
int result = tool.SquareTheDifference(3, 7);
```

## Differentiation

- No chat user interface and no model of its own. This repository provides contracts, adapters and hosting.
- Knowledge stores (`IKnowledgeRepository`) live in the separate `SmartStandards.KnowledgeManagement*` packages.

## Documentation

- [Requirements](doc/1-requirements.md)
- [Architecture](doc/2-architecture.md)
- [Quickstart](doc/3-quickstart.md)
- [Changelog](doc/changelog.md)
- [Ideas and future directions](doc/ideas.md) (including the Level-2 AI harness)
