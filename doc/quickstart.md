# Quickstart

## Ein Interface von einem LLM "implementieren" lassen

Voraussetzung: ein OpenAI-API-Key und eine Referenz auf `SmartStandards.AiSupport.CommonProviders`.

```csharp
using AI.SmartStandards.LowLevelPrompting;
using AI.SmartStandards.UjmwSupport;

public interface IMeinTool {
  /// <summary>Multipliziert die Differenz zweier Zahlen mit sich selbst.</summary>
  int MultipliziereDieDifferenzZweiterZahlenMitSichSelbst(int zahlA, int zahlB);
}

DynamicAiServiceFactory.AiOperationsProvider = new OpenAiLLMConnector("<OPENAI_API_KEY>");

IMeinTool tool = DynamicAiServiceFactory.CreateInstance<IMeinTool>();
int ergebnis = tool.MultipliziereDieDifferenzZweiterZahlenMitSichSelbst(3, 7); // erwartet: 16
```

Methodenname, XML-Doku-Kommentare und Argumente werden zum Prompt. Aussagekräftige Kommentare verbessern das Ergebnis.
Der Modus `ImplementationMode.AiGeneratedInmemoryCode` ist vorbereitet, aber noch nicht umgesetzt.

## Direkter Aufruf eines LLM

```csharp
ICommonLLM llm = new OpenAiLLMConnector("<OPENAI_API_KEY>");

string text = llm.CallWebSearchApi("Fasse die aktuellen Neuerungen von .NET 10 in 5 Punkten zusammen.");

MeinDto[] daten = llm.CallWebSearchApi<MeinDto[]>("Liefere 3 Beispiele …", inputData: new { Thema = "…" });

byte[] png = llm.CallImageGeneratorApi("Ein Leuchtturm im Morgennebel");
```

## Demo-Webservice starten

1. `dotnet/SmartStandards.AiSupport.sln` öffnen und `SmartStandards.AiSupport.DemoWebService` als Startprojekt wählen.
2. Die in `Program.cs` konfigurierten lokalen Pfade anpassen (z. B. `C:\Temp\_OneNoteExport`, `C:\Temp\Joplin`).
3. Starten. Die UJMW-Endpunkte sind über Swagger erreichbar. Die Demo verlangt einen beliebigen, nicht leeren `Authorization`-Header.
