# Anforderungen

## Ziel

**SmartStandards AI Support** stellt provider-neutrale Abstraktionen für KI-gestützte Anwendungen bereit, dazu
wiederverwendbare Standard-Implementierungen und ASP.NET-Core-Hosting-Unterstützung. Anwendungen sollen gegen stabile
Verträge programmieren und nicht gegen konkrete LLM-Anbieter, Chat-Frontends oder Ablagen.

## Zielgruppen

- .NET-Entwickler, die KI-Funktionen (Prompting, Chat, Wissenszugriff) in eigene Anwendungen oder Dienste integrieren.
- Betreiber von Diensten, die diese Funktionen per **UJMW** (und damit auch für AI-Agenten, z. B. über MCP-Adapter)
  im Netz bereitstellen wollen.

## Funktionale Anforderungen

### Abstraktionen (Paket `SmartStandards.AiSupport`)

| Vertrag | Zweck |
|---|---|
| `ICommonLLM` | Low-Level-Zugriff auf ein LLM: Web-Search-Prompting (Text oder typisiertes JSON-Ergebnis), Bildgenerierung, Bildbearbeitung. Muss per UJMW exponierbar sein. |
| `IPromptingUIBackend` | Backend für Chat-Oberflächen: Chat-Historie, Chats anlegen, umbenennen und löschen, Nachrichten senden (Dateianhänge geplant). |
| `IPromptingSessionStore` | Persistenz von Prompting-Sessions; Grundlage für einen Adapter zwischen `IPromptingUIBackend` und `ICommonLLM`. |
| `IPromtLibrary` | Verwaltung wiederverwendbarer Prompts. |
| `ICodeArtifactMap` | Semantische Beschreibung von Code-Artefakten und Komponenten pro Repository-URL. |

Der Zugriff auf Wissensablagen (`IKnowledgeRepository`) ist in die separaten Pakete `SmartStandards.KnowledgeManagement*`
ausgelagert und wird hier nur konsumiert.

### Standard-Implementierungen (Paket `SmartStandards.AiSupport.CommonProviders`)

- `OpenAiLLMConnector` als Implementierung von `ICommonLLM` gegen die OpenAI-API.
- Dateibasierte Implementierungen für Session-Store, Prompt-Library und Code-Artifact-Map sowie ein einfaches Prompting-UI-Backend.
- `DynamicAiServiceFactory`: erzeugt zu einem beliebigen .NET-Interface eine Laufzeit-Implementierung, deren Methodenaufrufe an ein
  `ICommonLLM` delegiert werden (reine KI-Kommunikation; KI-generierter Code ist vorbereitet, aber noch nicht umgesetzt).

### Hosting (Paket `SmartStandards.AiSupport.AspNetCore`)

- Einbindung in ASP.NET Core über `AddAiSupport(...)`, damit die Verträge als UJMW-Endpunkte bereitgestellt werden können.

## Nicht-funktionale Anforderungen

- **Zielplattformen:** .NET Framework 4.8, .NET 8.0 und .NET 10.0 für die Verträge; die Provider für .NET 8.0 und .NET 10.0.
- **Transportfähigkeit:** Alle Verträge müssen über UJMW (unter .NET Framework zusätzlich per WCF-Attributen) remotefähig bleiben.
  Das bedeutet einfache DTOs, synchrone Methoden und keine Callbacks oder Events.
- **Provider-Neutralität:** Anbieterspezifika (Endpoints, Modellnamen, Formate) dürfen nicht in die Verträge durchschlagen.
- Auslieferung als NuGet-Pakete. Die Versionierung erfolgt automatisch durch die Build-Pipeline.

## Abgrenzung

- Keine eigene Chat-Oberfläche; bereitgestellt wird nur das Backend.
- Wissensablagen (`IKnowledgeRepository` und ihre Provider) gehören nicht mehr zu diesem Repository.
- Zukünftige Erweiterungen, insbesondere der **Level-2 AI Harness** (Tool-Calling-Handshake, CLI-Proxies, lokales LLM, MCP),
  sind noch **keine aktiven Anforderungen**. Siehe [ideas.md](ideas.md).
