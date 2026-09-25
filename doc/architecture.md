# Architektur

## Überblick

```text
┌─────────────────────────────────────────────────────────────┐
│ Anwendungen / Dienste (z. B. DemoWebService)                 │
└───────────────┬─────────────────────────────┬───────────────┘
                │ programmieren gegen          │ hosten per UJMW
┌───────────────▼──────────────┐  ┌────────────▼──────────────┐
│ SmartStandards.AiSupport      │  │ SmartStandards.AiSupport. │
│ (Verträge, net48/net8/net10)  │  │ AspNetCore (Hosting)      │
│  ICommonLLM, IPromptingUI-    │  └───────────────────────────┘
│  Backend, IPromptingSession-  │
│  Store, IPromtLibrary,        │
│  ICodeArtifactMap             │
└───────────────▲──────────────┘
                │ implementiert
┌───────────────┴──────────────────────────────────────────────┐
│ SmartStandards.AiSupport.CommonProviders (net8/net10)          │
│  OpenAiLLMConnector · dateibasierte Stores · CheapPrompting-   │
│  UIBackend · DynamicAiServiceFactory (UJMW → LLM-Pipeline)     │
└───────────────────────────────────────────────────────────────┘
```

## Bausteine

| Paket / Projekt | Inhalt |
|---|---|
| `SmartStandards.AiSupport` | Reine Verträge und DTOs (Namespaces `AI.SmartStandards.*`, z. B. `…LowLevelPrompting`, `…InteractivePrompting`, `…KnowledgeAccess`). Keine Abhängigkeiten zu Anbietern. |
| `SmartStandards.AiSupport.CommonProviders` | Standard-Implementierungen der Verträge und die `DynamicAiServiceFactory`. |
| `SmartStandards.AiSupport.AspNetCore` | Registrierung der Dienste und Endpunkte in ASP.NET Core (`AddAiSupport`). |
| `SmartStandards.AiSupport.DemoWebService` | Beispiel-Host: SmartStandards-Logging, UJMW-Auth-Header-Prüfung, Einbindung externer Knowledge-Repositories (u. a. Joplin-WebDAV-Endpunkt). |
| `test/SmartStandards.AiSupport.Tests` | MSTest-Projekt; Tests gegen echte Anbieter sind mit `[Ignore]` markiert. |

Der Quellcode liegt in **Shared Projects** (`*.shproj`/`*.projitems`). Die eigentlichen Projekte pro Zielplattform
(`*.net48`, `*.net8.0`, `*.net10.0`) binden diese ein. Plattformabhängige Teile werden über Compiler-Konstanten
unterschieden, z. B. `NET_FX` für die WCF-Attribute der Verträge.

## Wichtige Abläufe

### LLM-Aufruf über `ICommonLLM`

Der Aufrufer übergibt Prompt und optional Eingabedaten. Bei typisierten Aufrufen (`CallWebSearchApi<T>`) erzeugt der
Connector aus `T` eine JSON-Beispielstruktur als Formatvorgabe für das Modell und deserialisiert die Antwort
wieder nach `T`. `OpenAiLLMConnector` nutzt dafür die OpenAI Responses API mit Web Search sowie die Images API.

### Dynamische KI-Dienste (`DynamicAiServiceFactory`)

`DynamicAiServiceFactory.CreateInstance<TContract>()` erzeugt über den UJMW-`DynamicClientFactory` einen Proxy für ein
beliebiges Interface. Jeder Methodenaufruf wird mit Methodenname, XML-Doku-Kommentaren und Argumenten zu einem Prompt
zusammengesetzt, an das konfigurierte `ICommonLLM` geschickt, und das Ergebnis wird in den Rückgabetyp zurückgewandelt.

## Architekturentscheidungen

- **Remotefähigkeit vor Komfort:** Verträge bleiben synchron und DTO-basiert, damit UJMW und WCF sie transportieren können.
- **Verträge und Implementierungen sind getrennt** (eigene Pakete), damit Konsumenten nur die Verträge referenzieren müssen.
- **Wissensablagen ausgelagert:** `IKnowledgeRepository` und seine Provider liegen in `SmartStandards.KnowledgeManagement*`.
- Die geplante Erweiterung um einen Level-2 AI Harness samt der dafür bereits getroffenen Designentscheidungen ist in
  [ideas.md](ideas.md) beschrieben und wird erst bei Umsetzung hierher übernommen.
