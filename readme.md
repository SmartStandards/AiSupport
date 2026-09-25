# SmartStandards - AI Support

## Was ist das?

Provider-neutrale .NET-Verträge für KI-gestützte Anwendungen, dazu Standard-Implementierungen und ASP.NET-Core-Hosting.
Alle Verträge sind per **UJMW** remotefähig, also auch über das Netz und für AI-Agenten nutzbar.

- `ICommonLLM`: Low-Level-Zugriff auf ein LLM (Web-Search-Prompting mit typisierten Antworten, Bildgenerierung, Bildbearbeitung)
- `IPromptingUIBackend`: Backend für Chat-Oberflächen (Chat-Historie, später Dateianhänge)
- `IPromptingSessionStore`: Persistenz von Prompting-Sessions (Basis für einen Adapter zwischen UI-Backend und `ICommonLLM`)
- `IPromtLibrary`: wiederverwendbare Prompts
- `ICodeArtifactMap`: Semantik und Komponenten pro Repository-URL

## Motivation

Anwendungen sollen gegen stabile Verträge programmieren statt gegen einzelne LLM-Anbieter. So lassen sich Anbieter tauschen
(Cloud-API, lokale Modelle, Agent-CLIs), und dieselben Funktionen stehen per UJMW auch entfernten Diensten und Agenten zur Verfügung.

## Kurzbeispiel

```csharp
DynamicAiServiceFactory.AiOperationsProvider = new OpenAiLLMConnector("<OPENAI_API_KEY>");
IMeinTool tool = DynamicAiServiceFactory.CreateInstance<IMeinTool>(); // ein LLM "implementiert" das Interface
int ergebnis = tool.MultipliziereDieDifferenzZweiterZahlenMitSichSelbst(3, 7);
```

## Abgrenzung

- Keine Chat-Oberfläche und kein eigenes Modell. Das Repository liefert Verträge, Adapter und Hosting.
- Wissensablagen (`IKnowledgeRepository`) liegen in den separaten Paketen `SmartStandards.KnowledgeManagement*`.

## Dokumentation

- [Anforderungen](doc/requirements.md)
- [Architektur](doc/architecture.md)
- [Quickstart](doc/quickstart.md)
- [Ideen und zukünftige Richtungen](doc/ideas.md) (u. a. Level-2 AI Harness)
- [Changelog](doc/changelog.md)
