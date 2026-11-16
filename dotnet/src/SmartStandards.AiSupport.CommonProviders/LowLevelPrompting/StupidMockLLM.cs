using System;
using System.Threading;

namespace AI.SmartStandards.LowLevelPrompting {

  /// <summary>
  /// A "Stupid Mock LLM" which ignores any input and always answers with one of a few generic phrases.
  /// It requires no API key and is intended to test chat UIs without costs or network access.
  /// </summary>
  public class StupidMockLLM : ICommonLLM {

    public const string ProviderName = "StupidMock";

    private static readonly string[] _Phrases = new string[] {
      "Aha, interessant - fahre fort.",
      "Kannst du mir mehr darüber berichten?",
      "Was wäre denn deine Idee dazu?",
      "Hmm, verstehe. Und was bedeutet das für dich?",
      "Das klingt spannend - erzähl weiter.",
      "Wie bist du darauf gekommen?",
      "Okay... und dann?"
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
    /// </summary>
    public string CallChatApi(ChatTurn[] newMessages, ref string contextStore) {

      if (_SimulatedDelayMs > 0) {
        Thread.Sleep(_SimulatedDelayMs);
      }

      contextStore = null;

      lock (_RandomSyncRoot) {
        return _Phrases[_Random.Next(_Phrases.Length)];
      }

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
