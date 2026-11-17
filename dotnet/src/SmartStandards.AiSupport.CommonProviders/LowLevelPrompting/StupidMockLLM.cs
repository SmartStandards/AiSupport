using System;
using System.Linq;
using System.Threading;

namespace AI.SmartStandards.LowLevelPrompting {

  /// <summary>
  /// A "Stupid Mock LLM" which ignores any input and always answers with one of a few generic phrases.
  /// It requires no API key and is intended to test chat UIs without costs or network access.
  /// </summary>
  public class StupidMockLLM : ICommonLLM {

    public const string ProviderName = "StupidMock";

    /// <summary>
    /// 30 verschiedene Phrasen, die den neutzer in den Wahnsinn treiben ;-)
    /// </summary>
    private static readonly string[] _Phrases = new string[] {
      "Aha, interessant - fahre fort.",
      "Kannst du mir mehr darüber berichten?",
      "Was wäre denn deine Idee dazu?",
      "Hmm, verstehe. Und was bedeutet das für dich?",
      "Das klingt spannend - erzähl weiter.",
      "Wie bist du darauf gekommen?",
      "Okay... und dann?",
      "dum di dum di dum...    ich muss weg...",
      "Ich bin mir nicht sicher, ob ich das verstehe. Kannst du es anders erklären?",
      "Interessant. Was ist deine Meinung dazu?",
      "schönes Wetter heute oder?",
      "sorry - hab gepennt... nochmal bitte!",
      "Ah. Ja. Genau. Was meinst du?",
      "Knampf!",
      "O2 can do!",
      "BerechneSollStundePrüf!",
      "Draußen ist's dunkler als montags!",
      "Die Lösung könnte sein: KATZENVIDEOS!",
      "Das kommt ganz darauf an. Worauf eigentlich?",
      "Warte kurz, ich muss erst mal zustimmend nicken.",
      "Hast du dazu ein Beispiel? Am besten eins mit Enten.",
      "Moment... ich lade gerade meine spontane Reaktion.",
      "Das ist bestimmt wichtig. Sag es noch mal mit Ausrufezeichen.",
      "Was würde ein Toaster dazu sagen?",
      "Verstehe. Und wie kam der Pinguin ins Spiel?",
      "Können wir kurz so tun, als hätte ich aufgepasst?",
      "Das war jetzt entweder sehr schlau oder noch nicht lang genug.",
      "Ja, das sehe ich ähnlich. Was sehen wir denn?",
      "Einen Moment bitte. Mein Gedanke ist gerade unter den Tisch gerollt.",
      "Spannend. Ich setze mal ein virtuelles Lesezeichen.",
      "Hm. Ich bräuchte dazu mehr Kontext. Oder einen Keks.",
      "Ich habe darüber nachgedacht und festgestellt, dass ich noch darüber nachdenke.",
      "Klingt plausibel. Kannst du es zur Sicherheit noch mal flüstern?",
      "Das erinnert mich an etwas. Sobald ich weiß, woran, sage ich Bescheid.",
      "Okay. An dieser Stelle würde ich jetzt bedeutungsvoll aus dem Fenster schauen."
    };

    private static readonly object _RandomSyncRoot = new object();

    private static readonly Random _Random = new Random();

    private readonly int _SimulatedDelayMs;

    /// <summary>
    /// Creates the mock with a small simulated "thinking" delay (to make busy states visible in the UI).
    /// </summary>
    public StupidMockLLM() : this(800) {
    }

    /// <summary>
    /// Creates the mock with the given simulated "thinking" delay in milliseconds (0 = no delay).
    /// </summary>
    public StupidMockLLM(int simulatedDelayMs) {
      _SimulatedDelayMs = simulatedDelayMs;
    }

    /// <summary>
    /// Ignores the given messages and returns a random generic phrase. No provider-side context is used.
    /// Exception (for offline tests of the harness tool loop): if the harness announced the tool 'ui_set_theme'
    /// and the last user message asks for 'dark mode' or 'light mode', the mock calls this tool, and it confirms
    /// a following tool response.
    /// </summary>
    public string CallChatApi(
      CancellationToken cancellationToken, ChatTurn[] newMessages, ref string contextStore, Action<string, int, int> onProgress
    ) {

      if (_SimulatedDelayMs > 0) {

        string[] fakePhases = new string[] { "reading your message", "thinking hard", "formulating an answer" };
        int phaseDelay = _SimulatedDelayMs / fakePhases.Length;

        for (int i = 0; i < fakePhases.Length; i++) {

          if (onProgress != null) {
            onProgress.Invoke(fakePhases[i], i + 1, fakePhases.Length);
          }

          if (cancellationToken.WaitHandle.WaitOne(phaseDelay)) {
            cancellationToken.ThrowIfCancellationRequested();
          }

        }

      }

      cancellationToken.ThrowIfCancellationRequested();

      contextStore = null;

      string scriptedAnswer = BuildScriptedThemeAnswer(newMessages);

      if (scriptedAnswer != null) {
        return scriptedAnswer;
      }

      lock (_RandomSyncRoot) {
        return _Phrases[_Random.Next(_Phrases.Length)];
      }

    }

    private static string BuildScriptedThemeAnswer(ChatTurn[] messages) {

      if (messages == null || messages.Length == 0) {
        return null;
      }

      bool themeToolAnnounced = messages.Any(
        (m) => m.Role == "system" && m.Content != null && m.Content.Contains("\"ui_set_theme\"")
      );

      if (!themeToolAnnounced) {
        return null;
      }

      string lastContent = messages[messages.Length - 1].Content;

      if (lastContent == null) {
        return null;
      }

      if (lastContent.Contains("<tool_response>")) {
        return "Erledigt! Das UI-Theme wurde umgeschaltet.";
      }

      string lowerContent = lastContent.ToLowerInvariant();

      if (lowerContent.Contains("dark mode") || lowerContent.Contains("darkmode")) {
        return "<tool_call>\n{\"name\": \"ui_set_theme\", \"arguments\": {\"theme\": \"dark\"}}\n</tool_call>";
      }

      if (lowerContent.Contains("light mode") || lowerContent.Contains("lightmode")) {
        return "<tool_call>\n{\"name\": \"ui_set_theme\", \"arguments\": {\"theme\": \"light\"}}\n</tool_call>";
      }

      return null;
    }

    public string CallWebSearchApi(string prompt, object inputData = null) {
      throw new NotSupportedException("The StupidMockLLM does not support web search.");
    }

    public T CallWebSearchApi<T>(string prompt, object inputData = null) where T : class {
      throw new NotSupportedException("The StupidMockLLM does not support web search.");
    }

    public byte[] CallImageEditApi(byte[] inputImageBytes, string prompt, byte[] maskImageBytes = null) {
      throw new NotSupportedException("The StupidMockLLM does not support image editing.");
    }

    public byte[] CallImageGeneratorApi(string prompt) {
      throw new NotSupportedException("The StupidMockLLM does not support image generation.");
    }

  }

}
