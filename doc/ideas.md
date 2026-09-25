# Ideen und zukünftige Richtungen

> Inhalte dieses Dokuments sind **keine aktiven Anforderungen**. Eine Idee wird erst nach ausdrücklicher Freigabe
> umgesetzt. Sie wandert dann als Anforderung nach [requirements.md](requirements.md) und strukturell nach
> [architecture.md](architecture.md).
>
> Bereits getroffene **Designentscheidungen** innerhalb einer Idee (als "entschieden" markiert) gelten verbindlich,
> sobald die Idee umgesetzt wird.

## 1. Level-2 AI Harness

> Status: **Konzept / Diskussionsgrundlage** (2026-09-25)
> Betroffene Komponenten: `ICommonLLM`, `OpenAiLLMConnector`, `DynamicAiServiceFactory` (UJMW), neue Proxy-Connectoren

### 1.1 Motivation

Heutige Agenten-Werkzeuge wie **Claude Code CLI** oder **GitHub Copilot CLI** sind selbst schon ein *Harness*:
Sie reden mit einem LLM, bekommen von diesem Tool-Call-Anfragen ("bitte jetzt `Read` auf Datei X aufrufen"),
führen die Tools aus und schicken die Ergebnisse zurück an das LLM – so lange, bis das LLM eine finale Antwort liefert.

Wir wollen eine Ebene darüber einen **eigenen Harness ("Level 2")** bauen, der

- einen solchen inneren Harness (oder ein LLM direkt) **wie ein LLM** anspricht,
- ihm **eigene Tools** anbietet, die **auf dem lokalen PC oder im LAN** ausgeführt werden,
- die Tool-Call-Anfragen des inneren Harness **imperativ** entgegennimmt, ausführt und die Ergebnisse **zurückreicht**,
- mittelfristig Tools auch über einen **ausprogrammierten MCP-Client** bezieht (wir rufen MCP-Server selbst auf und leiten die Antworten weiter).

```text
┌──────────────────────────────────────────────────────────────────────┐
│ Level-2 Harness (unser Code)                                         │
│                                                                      │
│   Orchestrator (Agent-Loop) ──── ToolRegistry                        │
│        │   ▲                       ├─ lokale .NET-Tools              │
│        │   │ Tool-Calls (Polling)  ├─ LAN-Tools (UJMW/HTTP)          │
│        ▼   │ Tool-Results          └─ MCP-Client → MCP-Server  (später) │
│   ICommonLLM (+ Erweiterung für Tool-Handshake)                      │
└────────┬─────────────────────────────────────────────────────────────┘
         │ (in-process oder via UJMW-REST über das LAN)
   ┌─────┴───────────┬──────────────────────┬──────────────────────┐
   ▼                 ▼                      ▼                      ▼
OpenAiLLMConnector  ClaudeCliProxy        CopilotCliProxy        LocalMiniLlmConnector
(Cloud-API)         (lokaler Prozess,     (lokaler Prozess,      (Ollama / llama.cpp /
                     Level-1 Harness)      Level-1 Harness)       Foundry Local …)
```

### 1.2 Leitprinzip für Step 1: "Handshake so nah am Original wie möglich"

Der erste Schritt ist **nicht** MCP, sondern: Wir sprechen den inneren Harness (bzw. das LLM) **genauso** an,
wie dieser selbst mit seinem innenliegenden LLM spricht. Das heißt, der aus den Provider-APIs bekannte Tool-Use-Zyklus
wird 1:1 abgebildet:

1. **Request**: Nachrichtenverlauf + Tool-Deklarationen (Name, Beschreibung, JSON-Schema der Parameter).
2. **Response mit Stop-Grund "Tool-Use"**: Das Modell liefert einen oder mehrere Tool-Call-Requests
   (jeweils mit **Call-Id**, Tool-Name, Argument-JSON). Mehrere Calls können *parallel* angefordert werden.
3. **Ausführung** durch den Harness (bei uns: Level 2).
4. **Resubmit**: Tool-Ergebnisse werden – referenziert über die Call-Id – an den Verlauf angehängt
   (inkl. Fehlerkennzeichen `is_error`, ggf. Text-/Bild-Content-Blöcke).
5. Wiederholung ab 2., bis Stop-Grund "fertig" (`end_turn` / `completed`) oder ein Abbruchgrund
   (`max_tokens`, Refusal, Timeout, Cancel).

Vorbilder, an denen sich das neutrale Datenmodell orientieren soll (verlustfrei auf beide abbildbar):

| Konzept              | Anthropic Messages API                  | OpenAI Responses API                     | OpenAI Chat Completions (auch Ollama & Co.) |
|----------------------|-----------------------------------------|------------------------------------------|---------------------------------------------|
| Tool-Deklaration     | `tools[] {name, description, input_schema}` | `tools[] {type:function, name, parameters}` | `tools[] {type:function, function:{…}}`  |
| Tool-Call-Request    | Content-Block `tool_use {id, name, input}` | Output-Item `function_call {call_id, name, arguments}` | `message.tool_calls[] {id, function}` |
| Stop-Grund           | `stop_reason: tool_use`                 | Output enthält `function_call`-Items     | `finish_reason: tool_calls`                 |
| Tool-Ergebnis        | `tool_result {tool_use_id, content, is_error}` | `function_call_output {call_id, output}` | Message `role: tool, tool_call_id`       |
| Fortsetzung          | kompletter Verlauf erneut               | `previous_response_id` oder Verlauf      | kompletter Verlauf erneut                   |

**Wichtig:** Call-Ids, Reihenfolge, Parallelität und Fehlersemantik werden durchgereicht und nicht "vereinfacht".
Nur so verhalten sich die Modelle/Harnesses so, wie sie trainiert bzw. gebaut wurden.

### 1.3 Weiterentwicklung von `ICommonLLM`

#### 1.3.1 Ausgangslage

`ICommonLLM` kennt heute nur One-Shot-Operationen (`CallWebSearchApi`, `CallWebSearchApi<T>`, `CallImageEditApi`,
`CallImageGeneratorApi`). Es gibt weder Konversationen noch Tool-Calls.

#### 1.3.2 Warum Polling?

`ICommonLLM` soll per **UJMW** (und unter .NET Framework per WCF-Attributen) über das Netz exponierbar bleiben.
Callbacks, Events oder `IAsyncEnumerable` lassen sich darüber nicht sauber transportieren. Daher wird der Handshake
als **zustandsbehaftete Session mit (Long-)Polling** modelliert:

- Der Aufrufer startet einen Turn.
- Er pollt den Turn-Status und bekommt dabei neue Ausgaben **und anstehende Tool-Call-Requests**.
- Er liefert Tool-Ergebnisse per separatem Call nach.
- Die Implementierung (z. B. ein CLI-Proxy) hält den inneren Harness solange "angehalten".

Long-Polling (`maxWaitMs`) vermeidet dabei Busy-Waiting, ohne das Transportmodell zu verlassen.

#### 1.3.3 Entwurf (Skizze, Namen noch verhandelbar)

Empfehlung: `ICommonLLM` bleibt für die bequemen One-Shot-Aufrufe erhalten; der Tool-Handshake kommt in ein
**zusätzliches Interface** (Arbeitstitel `IConversationalLLM`), das die Connectoren zusätzlich implementieren.
So bleibt der bestehende Vertrag stabil und schlanke Provider (z. B. reine Bildgeneratoren) müssen nichts Neues können.

```csharp
public interface IConversationalLLM {

  LlmCapabilities GetCapabilities();

  /// <returns>conversationId</returns>
  string BeginConversation(ConversationOptions options); // Modell, Systemprompt, Tool-Deklarationen, Limits

  /// <returns>turnId</returns>
  string SubmitUserMessage(string conversationId, ContentBlock[] content);

  /// Long-Polling: kehrt zurück, sobald sich etwas geändert hat oder maxWaitMs abgelaufen ist.
  TurnState PollTurn(string conversationId, string turnId, int sinceSequenceNo, int maxWaitMs);

  /// Beantwortet ausstehende Anfragen jeder Art (Tool-Ergebnisse, Freigaben, Rückfragen), referenziert über RequestId.
  void SubmitResponses(string conversationId, string turnId, HostResponse[] responses);

  void CancelTurn(string conversationId, string turnId);

  void EndConversation(string conversationId);

  // ---- optionale, abfragbare Delegations-Capabilities (siehe 1.3.4) ----

  /// Übergibt Tool-Quellen (z. B. MCP-Server), die der innere Harness SELBST aufruft. Nur gültig, wenn
  /// GetCapabilities().ToolDelegation die Quellart unterstützt, sonst NotSupportedException.
  void DelegateToolSources(string conversationId, ToolSourceDescriptor[] sources);

  /// Übergibt Regeln, nach denen der innere Harness Anfragen SELBST entscheidet (Allow/Deny-Regeln, Modus).
  /// Was die Regeln nicht abdecken, kommt weiterhin als HostRequest nach außen.
  void DelegateRequestPolicy(string conversationId, RequestPolicy policy);

}

public class TurnState {
  public TurnStatus Status { get; set; }              // Running | AwaitingHost | Completed | Failed | Cancelled
  public string StopReason { get; set; }              // provider-neutral: EndTurn, MaxTokens, MaxTurns, Budget, Refusal, Cancelled, Error
  public int SequenceNo { get; set; }                 // für inkrementelles Polling
  public OutputItem[] NewOutputItems { get; set; }    // Text-Deltas, Thinking/Status, Infos über interne bzw. delegierte Tool-Calls
  public HostRequest[] PendingRequests { get; set; }  // alles, worauf der innere Harness/das LLM gerade wartet
  public UsageInfo Usage { get; set; }
  public string ErrorMessage { get; set; }
}

public enum HostRequestKind { ToolCall, Approval, UserQuestion }

public class HostRequest {
  public string RequestId;           // = Call-Id bei ToolCall (tool_use_id / call_id), sonst Request-Id des Protokolls
  public HostRequestKind Kind;
  public string ToolName;            // ToolCall: aufzurufendes Tool; Approval: Tool, für das Freigabe erbeten wird
  public string ArgumentsJson;       // ToolCall/Approval: Eingaben; UserQuestion: Fragen/Optionen (Elicitation-Schema)
  public string[] Options;           // Approval: z. B. AllowOnce, AllowAlways, Deny; UserQuestion: Auswahloptionen
}

public class HostResponse {
  public string RequestId;
  public ContentBlock[] Content;     // ToolCall-Ergebnis bzw. Antworttext
  public string StructuredContentJson;
  public bool IsError;               // ToolCall: Tool-Fehler, geht an das Modell
  public string SelectedOption;      // Approval/UserQuestion
}

public class ToolDescriptor { public string Name; public string Description; public string InputJsonSchema; public string OutputJsonSchema; public bool ReadOnlyHint; }
```

Hinweise zum Entwurf:

- **Tool-Deklarationen** gehören in `ConversationOptions`; eine spätere Änderung während der Session ist optional
  (Capability-Flag), da nicht jeder Level-1-Harness das kann.
- **`GetCapabilities()`** liefert u. a.: unterstützt Tools / parallele Tool-Calls / Bilder im Tool-Result /
  eigene interne Tools (Level-1-Harness) / Web-Search / Streaming-Granularität.
- **Interne Tools des Level-1-Harness** (z. B. `Read`, `Edit`, `Bash` bei Claude Code) werden *nicht* als
  `ToolCall`-Request gemeldet (die führt der innere Harness selbst aus), sondern nur informativ als `OutputItem`.
  Braucht er dafür eine Freigabe, kommt diese als `Approval`-Request.
- Die bestehenden One-Shot-Methoden können intern auf den neuen Mechanismus umgestellt werden
  (Turn starten → pollen bis `Completed`), das ist aber kein Muss für Step 1.
- **Alle Anfragen werden gleich behandelt**: Tool-Calls, Freigaben und Rückfragen an den Benutzer (`AskUserQuestion`,
  MCP-Elicitation, ACP-`request_permission`, Codex-Approvals) sind gleichrangige `HostRequest`s. Für jeden gilt dasselbe
  Muster: Der Turn pausiert, `PollTurn` liefert `AwaitingHost`, und `SubmitResponses` setzt den Turn fort. Genauso gilt
  für jede Anfrageart die Wahl zwischen *außen routen* und *delegieren* (1.3.4).
- **API-Kontraktregeln** setzt der Orchestrator durch: Jeder Tool-Call bekommt genau ein Result (bei Abbruch ein synthetisches
  Fehler-Result), Tool-Namen bleiben auf `^[a-zA-Z0-9_-]{1,64}$` beschränkt, und die Tool-Liste ist deterministisch sortiert und während
  einer Conversation stabil (Prompt-Caching).
- `ReadOnlyHint` erlaubt parallele Ausführung. Zwischen Tool-Fehler (`IsError`, geht an das Modell) und Protokollfehler
  (geht an den Orchestrator) wird unterschieden.
- Optional zusätzlich eine **synchrone Komfort-Methode** im Orchestrator (nicht im Interface!), z. B.
  `RunToCompletion(conversationId, message, IToolDispatcher dispatcher)`, die den Loop kapselt.

#### 1.3.4 Zwei Betriebsarten: volles Routing außen vs. Delegation

**Grundsatz (entschieden):** Kann eine Implementierung eine Fähigkeit *intern* schon selbst, z. B. zusätzliche Tools
einhängen oder Freigaben nach Regeln entscheiden, wird das als **optionale, abfragbare Capability** am Interface gekapselt.
Der Level-2-Harness gibt dann die nötigen Tools bzw. Regeln einfach **hinein** und **zieht sich aus der Kommunikation heraus**.
Wird die Capability nicht unterstützt, übernimmt der Level-2-Harness das **volle Routing außen** über `PendingRequests`.

| | Volles Routing außen (Pflicht) | Delegation (optional, per Capability) |
|---|---|---|
| Tools | als `ToolDescriptor` in `ConversationOptions` deklariert → Aufruf kommt als `ToolCall`-Request → Level 2 führt aus | `DelegateToolSources(...)` → der innere Harness ruft die Quelle (z. B. MCP-Server) selbst auf |
| Freigaben/Rückfragen | kommen als `Approval`/`UserQuestion`-Request → Level 2 (Policy oder Mensch) entscheidet | `DelegateRequestPolicy(...)` → der innere Harness entscheidet nach Regeln. Nicht abgedeckte Fälle kommen weiterhin als Request |
| Level 2 sieht | jeden Call und jede Antwort | nur informativ `OutputItem`s (soweit die Implementierung sie liefert) |
| Vorteile | volle Kontrolle, Logging und Policy an einer Stelle; funktioniert mit Tools, die nur im Level-2-Prozess erreichbar sind | weniger Latenz und Roundtrips, kein Polling-Overhead |

Regeln:

1. **Volles Routing außen muss jede Implementierung können**, auch wenn es aufwendig oder bruchanfällig ist
   (z. B. Claude-Control-Protocol, siehe 1.5.2). Spätestens beim Self-Hosting (Mini-LLM, 1.5.4) gibt es keinen inneren
   Harness mehr, der etwas selbst erledigen könnte. Dort *ist* der Connector der Loop.
2. **Mischbetrieb ist der Normalfall:** Pro Tool-Quelle entscheidet der Orchestrator. Delegiert wird nur, wenn die Capability die
   Quellart unterstützt **und** die Quelle vom Rechner des inneren Harness aus erreichbar ist (Netz, Credentials). Alles andere,
   z. B. In-Process-.NET-Tools, wird außen geroutet.
3. Die Capability-Auskunft ist **fein genug für die Entscheidung**, z. B.:

   ```csharp
   public class LlmCapabilities {
     public bool SupportsTools;                        // Routing außen (für Conversation-fähige Implementierungen immer true)
     public bool SupportsParallelToolCalls;
     public HostRequestKind[] RoutableRequestKinds;    // welche Anfragearten nach außen gemeldet werden können
     public ToolSourceKind[] DelegatableToolSources;   // z. B. McpStdio, McpHttp (leer = keine Tool-Delegation)
     public bool SupportsRequestPolicyDelegation;      // Allow/Deny-Regeln, Permission-Mode
     public bool ReportsDelegatedCalls;                // liefert delegierte Calls als OutputItems (Observability)
   }
   ```
4. Delegation ist **vor dem ersten Turn** festzulegen (Prompt-Caching, und viele Harnesses laden Tools nur beim Start).

Zuordnung der geplanten Implementierungen (vorläufig, vor Umsetzung verifizieren):

| Implementierung | Routing außen über | Tool-Delegation über | Policy-Delegation über |
|---|---|---|---|
| `ClaudeCliProxy` | Bridge-MCP-Server bzw. Control-Protocol (`can_use_tool`, SDK-MCP) | `--mcp-config` (stdio/http) | `--allowedTools`, `--disallowedTools`, `--permission-mode` |
| `CopilotCliProxy` | ACP (`request_permission`) + Bridge-MCP | ACP `session/new.mcpServers` | `--available-tools`/`--excluded-tools` (global), `--allow-tool`/`--deny-tool` |
| `OpenAiLLMConnector` | Function Calling | ggf. Remote-MCP-Tool der API (nur öffentlich erreichbare Server, kein LAN) | – |
| Mini-LLM (Ollama) | Function Calling (`/api/chat`) | – | – |

### 1.4 Der Level-2 Harness selbst

| Baustein            | Aufgabe |
|---------------------|---------|
| **Orchestrator**    | Agent-Loop: Capabilities abfragen, Tool-Quellen auf Routing oder Delegation verteilen (1.3.4), Turn starten, pollen, `PendingRequests` dispatchen (Tool-Calls an die ToolRegistry, parallel wo erlaubt; Freigaben und Rückfragen an Policy oder Mensch), Antworten resubmitten, Limits überwachen (max. Iterationen, Timeouts, Kosten). |
| **ToolRegistry**    | Sammelt `ToolDescriptor`s aus mehreren `IToolProvider`n und routet Calls per Name. |
| **Lokale Tools**    | .NET-Interfaces/Methoden als Tools. Die JSON-Schemas + Beschreibungen können aus Signatur + XML-Kommentaren erzeugt werden (analog zu `XmlCommentAccessExtensions`/`DynamicAiServiceFactory`). |
| **LAN-Tools**       | Aufruf entfernter Dienste über UJMW-REST (Contract-Interface + `DynamicClientFactory`). |
| **MCP-Client** (später) | `tools/list` → `ToolDescriptor`, `tools/call` → `HostResponse`. Kandidat: offizielles C#-SDK (`ModelContextProtocol` NuGet). |
| **Policy/Approval** | Allow-/Deny-Listen, optional menschliche Freigabe pro Tool (Anbindung an `IPromptingUIBackend` denkbar). |
| **Logging/Trace**   | Vollständiges Protokoll aller Turns, Calls und Ergebnisse (SmartStandards.Logging) – wichtig für Debugging und Nachvollziehbarkeit. |

Perspektivisch ist der Level-2 Harness selbst wieder als `IConversationalLLM` (oder als MCP-Server) exponierbar –
Harnesses werden damit **stapelbar**.

### 1.5 Geplante Connectoren

#### 1.5.1 `OpenAiLLMConnector` (bestehend, erweitern)

- Umsetzung des Handshakes über die **Responses API** (`function_call` / `function_call_output`,
  Verkettung über `previous_response_id`).
- Bei der Gelegenheit: Modellname, Endpoint und Limits konfigurierbar machen (heute hart `gpt-4o`),
  async/Timeouts sauber behandeln, Fehler nicht verschlucken (`catch { return null; }` bei Bild-APIs).
- **Konfigurierbare Base-URL + Chat-Completions-Modus**: Damit deckt derselbe Connector auch alle
  OpenAI-kompatiblen lokalen Server ab (siehe 1.5.4).

#### 1.5.2 `ClaudeCliProxy` (lokal laufender Proxy für Claude Code CLI)

Läuft auf dem Rechner, auf dem Claude Code installiert/angemeldet ist, und exponiert `IConversationalLLM`
(in-process oder per UJMW im LAN).

- **Steuerung des CLI**: Headless-Modus `claude -p` mit `--input-format stream-json --output-format stream-json`
  (ein langlebiger Prozess pro Conversation, Multi-Turn über stdin), Sessions über `--session-id`/`--resume`.
- **Eigene Tools einschleusen – "Bridge-MCP-Server"**: Der Proxy startet einen kleinen lokalen MCP-Server
  (stdio oder HTTP) und übergibt ihn per `--mcp-config`. Dieser Server meldet genau die im `BeginConversation`
  deklarierten Tools. Ruft Claude eines davon auf, **blockiert** der Bridge-Server den MCP-Request, legt einen
  `ToolCall`-Request in die Pending-Queue (→ `PollTurn` liefert `AwaitingHost`) und antwortet erst,
  wenn `SubmitResponses` eintrifft. Damit ist der Original-Handshake des inneren Harness vollständig erhalten.
- **Interne Tools begrenzen**: `--allowedTools` / `--disallowedTools` / `--permission-mode` aus der Policy ableiten.
- Zu beachten: MCP-Tool-Timeouts des CLI (per Umgebungsvariable konfigurierbar) müssen größer sein als die
  erwartete Tool-Laufzeit + Polling-Latenz.
- Für reproduzierbares Verhalten `--bare` nutzen, damit keine Hooks, Plugins oder MCP-Server aus der Umgebung geladen werden.
  Achtung: Dann ist nur `ANTHROPIC_API_KEY` möglich, kein Abo-Login. Für unbeaufsichtigte Läufe dient `--permission-prompts none`.
- Freigaben interner Tools lassen sich per `--permission-prompt-tool` an ein Tool des Bridge-MCP-Servers umleiten
  und landen dann als `Approval`-Request beim Level-2-Harness.
- **Control-Protocol des Agent SDK direkt in .NET** (`control_request`/`control_response` über stdin/stdout mit
  `can_use_tool`, `hook_callback` und In-Process-SDK-MCP-Servern) ist **ausdrücklich gewollt**, obwohl es offiziell nur über das
  TS/Python-SDK dokumentiert und damit bruchanfällig ist. Es ist der vollständigste Weg für das Routing außen (Tool-Calls,
  Freigaben und Rückfragen über einen Kanal, ohne Zusatzprozess). Absicherung: Feature-Detection über das
  `capabilities`-Array in `system/init`, eine festgelegte CLI-Version und Protokoll-Contract-Tests mit aufgezeichneten
  Fixtures (Details: Abschnitt 1.10 sowie Skill `ai-harness-development`).
- **Delegation** (1.3.4): Tool-Quellen, die Claude selbst erreicht, werden per `--mcp-config` direkt eingehängt, Freigaberegeln
  per `--allowedTools`/`--disallowedTools`/`--permission-mode`.

#### 1.5.3 `CopilotCliProxy` (lokal laufender Proxy für GitHub Copilot CLI)

- **Empfohlen: ACP-Server-Modus** `copilot --acp` (stdio) bzw. `--acp --port <n>` (TCP, localhost), Public Preview seit 2026-01.
  Der Proxy ist ACP-Client: `session/new` mit `cwd` und `mcpServers` (→ **derselbe Bridge-MCP-Server** wie in 1.5.2),
  `session/prompt`, `session/update`-Notifications (→ `OutputItem`s), `session/request_permission` (→ `Approval`-Request),
  `session/cancel`. Tool-Quellen, die Copilot selbst erreicht, werden über dieselben `mcpServers` **delegiert** (1.3.4).
- Interne Tools begrenzen per `--available-tools` / `--excluded-tools`, allerdings gilt das global pro Serverprozess.
- Fallback: Prompt-Modus `copilot -p … --output-format json` (JSONL) mit `--allow-tool` / `--deny-tool`.
- ACP ist herstellerübergreifend (Adapter für Claude Code u. a. existieren) und damit Kandidat für einen **gemeinsamen**
  Proxy-Unterbau für mehrere CLI-Harnesses. Analog bietet OpenAI Codex ein eigenes JSON-RPC-Protokoll (`codex app-server`) mit
  client-seitig ausgeführten "Dynamic Tools".

> Die genauen CLI-Flags beider Tools ändern sich häufig und sind vor der Implementierung gegen die aktuelle
> Dokumentation zu verifizieren.

#### 1.5.4 Lokales Mini-LLM (exemplarisch selbst gehostet)

Ziel: vollständig offline, auf normaler Entwickler-Hardware lauffähig, **zuverlässiges Tool-Calling**.

Hier gibt es keinen inneren Harness: Der Connector **ist** der Loop, und es gibt keine Delegation. Alle Anfragen werden außen
geroutet. Das ist der Referenzfall für das volle Routing (1.3.4, Regel 1).

**Runtime** – Empfehlung: **Ollama** (einfache Windows-Installation, OpenAI-kompatibler Endpoint
`/v1/chat/completions` inkl. `tools`, Modellverwaltung per `ollama pull`). Damit reicht im Idealfall der um
Base-URL/Chat-Completions erweiterte `OpenAiLLMConnector` – ein eigener Connector ist nur für Extras nötig.
Alternativen:

| Runtime            | Pro | Contra |
|--------------------|-----|--------|
| **Ollama**         | sehr einfach, OpenAI-kompatibel, Tool-Calling für viele Modelle | weniger Feintuning-Optionen |
| llama.cpp `llama-server` | maximal schlank/kontrollierbar, Tool-Calling via `--jinja` | mehr Handarbeit |
| LM Studio          | komfortable GUI, OpenAI-kompatibler Server | nicht Open Source |
| Microsoft Foundry Local | Windows-/.NET-nah, OpenAI-kompatibel, ONNX/NPU | kleinere Modellauswahl |

**Modellkandidaten** (Fokus Tool-Calling, grob nach Hardwarebedarf):

| Modell                 | Größe | Einschätzung |
|------------------------|-------|--------------|
| **Qwen3-4B-Instruct-2507** (bzw. Qwen3 8B) | ~3–6 GB (Q4) | **Empfehlung für den Start**: führend unter 7B im BFCL v4, stark bei parallelen Calls |
| Gemma 4 E4B            | ~3–4 GB | native Function-Call-Tokens, Apache 2.0 |
| Phi-4-mini (3.8B)      | ~3 GB | schnellste Inferenz, gute Mehrschritt-Planung |
| gpt-oss-20b            | ~14 GB | deutlich stärker (Reasoning + Tools), braucht ~16 GB (V)RAM |

Hinweis: Für Tool-Calling mit Streaming den nativen Ollama-Endpoint `/api/chat` nutzen. Über `/v1/chat/completions`
gehen beim Streaming laut Community-Berichten Tool-Call-Daten verloren. Alternativ ohne Streaming arbeiten.

Vorgehen: mit **Qwen3-4B-Instruct-2507** bzw. Qwen3 8B über Ollama starten; einen kleinen
**Tool-Calling-Benchmark** (dieselben Szenarien wie in den Tests unter 1.7) gegen alle Kandidaten laufen lassen
und dann festlegen. Modelllandschaft vor Umsetzung nochmals prüfen – sie ändert sich schnell.

### 1.6 Roadmap

| Phase | Inhalt | Ergebnis |
|-------|--------|----------|
| 0 | Diese Idee, Review, Festlegung der offenen Punkte | abgestimmter Interface-Entwurf |
| 1 | `IConversationalLLM` + DTOs (inkl. `HostRequest`/`HostResponse`, Capabilities), Orchestrator mit Routing außen, ToolRegistry mit lokalen .NET-Tools, **Fake-LLM** (skriptbarer Mock für alle Anfragearten, optional mit simulierter Delegation) | Agent-Loop testbar ohne Kosten |
| 2 | `OpenAiLLMConnector` auf Function Calling erweitern (Responses API; Base-URL + Chat-Completions-Modus) | erster echter End-to-End-Durchlauf |
| 3 | Lokales Mini-LLM via Ollama anbinden, Modellauswahl per Benchmark | Offline-Betrieb, Referenz für volles Routing |
| 4 | `ClaudeCliProxy`: Bridge-MCP-Server, danach das Control-Protocol in .NET; Delegation über `--mcp-config`; UJMW-Hosting im LAN | Level-1-Harness als "LLM" nutzbar |
| 5 | `CopilotCliProxy` als ACP-Client (Bridge-MCP wiederverwenden; Delegation über `mcpServers`) | zweiter Level-1-Harness |
| 6 | MCP-Client als `IToolProvider` (fremde MCP-Server als Tool-Quelle); der Orchestrator entscheidet pro Quelle zwischen Routing und Delegation | Tools aus dem MCP-Ökosystem |
| 7 | Optional: Level-2 Harness selbst als `IConversationalLLM`/MCP-Server exponieren | stapelbare Harnesses |

### 1.7 Tests / Qualitätssicherung

- **Handshake-Konformitätstests** gegen jeden Connector (gleiche Szenarien):
  kein Tool nötig · genau ein Tool-Call · mehrere parallele Calls · Folge-Calls über mehrere Runden ·
  Tool liefert Fehler (`IsError`) · Tool-Timeout · Cancel mitten im Turn · Limit `max_tokens` ·
  Freigabe erteilt/verweigert · Rückfrage beantwortet · Mischbetrieb (ein Tool delegiert, eines außen geroutet) ·
  Delegation angefordert, aber nicht unterstützt (`NotSupportedException`).
- Fake-LLM für schnelle, deterministische Unit-Tests; echte Provider als `[Ignore]`/Kategorie-Tests (wie bisher).
- Aufzeichnung von Roh-Requests/-Responses je Provider als Fixtures, um Mapping-Regressions zu erkennen.

### 1.8 Sicherheit

- Tools auf dem PC/im LAN sind mächtig: **Allow-Listen**, optionale **Freigabe durch einen Menschen**,
  Argument-Validierung gegen das JSON-Schema.
- Level-1-Harnesses bringen eigene gefährliche Tools mit (Shell, Dateizugriff) → restriktive Default-Policy,
  Arbeitsverzeichnis festlegen.
- UJMW-Endpunkte der Proxies nur authentifiziert und nicht ungeschützt im LAN exponieren
  (die Proxies laufen mit dem Login/Abo des jeweiligen Benutzers).
- Tool-Ergebnisse können Prompt-Injection enthalten → im Log kennzeichnen, bei sensiblen Tools Freigabe erzwingen.

### 1.9 Offene Punkte

1. Eigenes Interface (`IConversationalLLM`) vs. direkte Erweiterung von `ICommonLLM`? (Empfehlung: eigenes Interface)
2. Wo lebt der Session-State bei UJMW-Hosting (In-Memory mit Ablaufzeit vs. `IPromptingSessionStore`)?
3. Zielplattformen für die neuen Teile: auch `net48` oder nur `net8.0`/`net10.0`? (CLI-Proxies und MCP-SDK eher nur modernes .NET)
4. Wie viel der internen Level-1-Aktivität (Thinking, interne Tool-Calls) soll über `OutputItem`s sichtbar werden?
5. ~~Brückenansatz für die CLIs~~ → **entschieden** (1.3.4): Delegation als optionale Capability, volles Routing außen als
   Pflicht. Das Claude-Control-Protocol wird trotz Bruchrisiko umgesetzt. Offen ist nur die Reihenfolge Bridge-MCP → Control-Protocol.
6. Konkrete Hardware für das Mini-LLM (GPU/VRAM) → bestimmt die Modellgröße.

### 1.10 Hintergrund: Wie moderne Agent-Harnesses intern funktionieren (Recherche 2026-09)

Die ausführliche technische Zusammenfassung (Protokolle, Loop-Interna, Flags) pflegt der AI-Skill
[`ai-harness-development`](../.agents/skills/ai-harness-development/SKILL.md) mit seinen Referenzdateien. Die für
Menschen wichtigsten Erkenntnisse:

- **Alle großen Harnesses arbeiten nach demselben Loop:** Modell aufrufen, angeforderte Tools ausführen, Ergebnisse
  zurückgeben, wiederholen, bis das Modell ohne Tool-Call antwortet. Der Verlauf wird bei jedem Aufruf komplett
  mitgeschickt, und Prompt-Caching hält die Kosten im Rahmen.
- **Steuerbarkeit von außen:** Claude Code (Agent-SDK-Steuerprotokoll), OpenAI Codex (`codex app-server`) und
  GitHub Copilot CLI (`copilot --acp`, Agent Client Protocol) bieten ein bidirektionales JSON-Protokoll über stdio.
  Der Harness stellt darin eigene Anfragen an den Host (Tool-Ausführung, Freigaben, Rückfragen), und der Turn pausiert
  bis zur Antwort. Genau dieses Muster bildet `PollTurn`/`SubmitResponses` UJMW-tauglich ab.
- **Eigene Tools einhängen** können alle über MCP: Claude per `--mcp-config`, Copilot per `mcpServers` in ACP-`session/new`,
  Codex per Konfiguration oder "Dynamic Tools". Das ist die Grundlage der Delegations-Capability (1.3.4).
- **Interne Mechanik, die wir übernehmen:**
  - Nur lesende Tools laufen parallel.
  - Jeder Tool-Call bekommt genau ein Ergebnis, auch bei Abbruch.
  - Tool-Fehler gehen als Ergebnis ans Modell, damit es sich selbst korrigieren kann.
  - Die Tool-Liste bleibt stabil (Prompt-Caching).
  - Lange Verläufe werden automatisch zusammengefasst.
  - Freigaben laufen über eine feste Prüfkette (Hooks → Regeln → Modus → Rückfrage).
- **Lokale Modelle** mit gutem Tool-Calling (BFCL v4, Stand 2026-09): Qwen3-4B-Instruct-2507, Gemma 4 E4B, Phi-4-mini;
  Runtime Ollama mit nativem `/api/chat`.

#### Quellen

**Offiziell – Anthropic**
- [Run Claude Code programmatically (headless)](https://code.claude.com/docs/en/headless)
- [How the agent loop works (Agent SDK)](https://code.claude.com/docs/en/agent-sdk/agent-loop)
- [Give Claude custom tools (Agent SDK)](https://code.claude.com/docs/en/agent-sdk/custom-tools)
- [Configure permissions (Agent SDK)](https://code.claude.com/docs/en/agent-sdk/permissions)
- [Define tools](https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools)
- [Handle tool calls](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls)
- [Effective context engineering for AI agents](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)
- [Effective harnesses for long-running agents](https://anthropic.com/engineering/effective-harnesses-for-long-running-agents)

**Offiziell – OpenAI**
- [Unrolling the Codex agent loop](https://openai.com/index/unrolling-the-codex-agent-loop/)
- [Unlocking the Codex harness: how we built the App Server](https://openai.com/index/unlocking-the-codex-harness/)
- [Codex app-server README](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [Function calling guide](https://developers.openai.com/api/docs/guides/function-calling)

**Offiziell – GitHub**
- [Copilot CLI programmatic reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference)
- [Copilot CLI ACP server](https://docs.github.com/en/copilot/reference/copilot-cli-reference/acp-server)
- [ACP support in Copilot CLI – Changelog 2026-01-28](https://github.blog/changelog/2026-01-28-acp-support-in-copilot-cli-is-now-in-public-preview/)
- [Adding MCP servers for Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers)

**Spezifikationen**
- [MCP Spec 2025-11-25 – Tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)
- [Agent Client Protocol – Overview](https://agentclientprotocol.com/protocol/overview) · [Prompt Turn](https://agentclientprotocol.com/protocol/prompt-turn) · [Session Setup](https://agentclientprotocol.com/protocol/session-setup)

**Lokale Modelle**
- [Ollama – Tool calling](https://docs.ollama.com/capabilities/tool-calling) · [Ollama – OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility)
- [Berkeley Function Calling Leaderboard V4](https://gorilla.cs.berkeley.edu/leaderboard.html)
- [Phi-4-Mini vs Gemma 4 vs Qwen3-4B: Tool Calling (Ertas AI)](https://www.ertas.ai/blog/on-device-tool-calling-2026-qwen3-gemma4-phi4)

**Inoffiziell / Drittquellen (mit Vorsicht)**
- [Claude Code from Source – Ch. 5 The Agent Loop](https://claude-code-from-source.com/ch05-agent-loop/)
- [Inside the Claude Agent SDK: stdin/stdout communication](https://buildwithaws.substack.com/p/inside-the-claude-agent-sdk-from)
- [Codex Knowledge Base – Agent loop deep dive](https://codex.danielvaughan.com/2026/03/28/codex-agent-loop-deep-dive/)
- [Building on codex app-server (Gist)](https://gist.github.com/oneryalcin/ee2c27e2d8aa040da8fbe7eebcc2ecea)
- [openclaw PR: native Ollama /api/chat for streaming + tool calling](https://github.com/openclaw/openclaw/pull/11853)

## 2. Weitere Kandidaten für Abstraktionen (extern, "auch interessant")

- `ILogEntryRepository` (SmartStandards.Logging.Centralized)
- `IUniversalWorkItemRepository`
- `IAfsRepository`
- `IServerCommands` (+ separater MCP-Adapter, der die Commands selbst besser exponiert)
