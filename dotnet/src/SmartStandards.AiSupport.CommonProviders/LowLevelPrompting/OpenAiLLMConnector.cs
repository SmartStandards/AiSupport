using Logging.SmartStandards.CopyForAI.SmartStandards;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using static System.Runtime.InteropServices.JavaScript.JSType;


namespace AI.SmartStandards.LowLevelPrompting {

  public class OpenAiLLMConnector : ICommonLLM {

    private string _OpenAiApiKey = null;

    public OpenAiLLMConnector(string openAiApiKey) {

      _OpenAiApiKey = openAiApiKey;

      if (string.IsNullOrWhiteSpace(_OpenAiApiKey)) {
        throw new ArgumentNullException(nameof(openAiApiKey));
      }

    }

    #region " PoC "

    internal string CallGptCompletionsApi(string prompt) {

      if (prompt == null) {
        return null;
      }

      var endpoint = "https://api.openai.com/v1/chat/completions";
      var requestBody = new {
        model = "gpt-4o",// "gpt-4o" alternativ: "gpt-3.5-turbo"
        messages = new[]
          {
                //new { role = "system", content = "Du bist ein präziser Finanzanalyst." },
                new { role = "user", content = prompt }
            },
        temperature = 0.3,
        max_tokens = 200
      };

      var jsonBody = JsonConvert.SerializeObject(requestBody);

      using (HttpClient client = new HttpClient()) {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _OpenAiApiKey);

        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = client.PostAsync(endpoint, content).Result;

        var responseContent = response.Content.ReadAsStringAsync().Result;

        var parsed = JObject.Parse(responseContent);
        string result = parsed["choices"]?[0]?["message"]?["content"]?.ToString();
        return result ?? string.Empty;
      }
    }

    internal string CallGptWebSearchApi(string prompt) {
      if (prompt == null)
        return null;

      var endpoint = "https://api.openai.com/v1/responses";

      //var requestBody = new {
      //  model = "gpt-4o",
      //  input = prompt,
      //  tools = new[] { new { type = "web_search_preview" } },
      //  max_output_tokens = 8192
      //};
      var requestBody = new {
        model = "gpt-4o",
        input = prompt,
        tools = new[] { new { type = "web_search_preview" } },
        max_output_tokens = 8192
      };
      //var requestBody = new {
      //  model = "gpt-5",
      //  input = prompt,
      //  tools = new[] { new { type = "web_search" } },
      //  max_output_tokens = 8192
      //};

      var jsonBody = JsonConvert.SerializeObject(requestBody);

      using (HttpClient client = new HttpClient()) {
        client.Timeout = TimeSpan.FromSeconds(240);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _OpenAiApiKey);

        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = client.PostAsync(endpoint, content).Result;
        string responseContent = response.Content.ReadAsStringAsync().Result;

        try {
          var parsed = JObject.Parse(responseContent);

          // Suche nach dem assistant-Antwort-Element
          var messageBlock = parsed["output"]?
              .FirstOrDefault(o => o["type"]?.ToString() == "message");

          var contentArray = messageBlock?["content"] as JArray;
          var outputText = contentArray?
              .FirstOrDefault(c => c["type"]?.ToString() == "output_text")?["text"]?.ToString();

          // Quellen extrahieren
          var annotations = contentArray?
              .FirstOrDefault(c => c["type"]?.ToString() == "output_text")?["annotations"] as JArray;

          var sources = new StringBuilder();
          if (annotations != null) {
            foreach (var ann in annotations) {
              if (ann["type"]?.ToString() == "url_citation") {
                var title = ann["title"]?.ToString() ?? "Quelle";
                var url = ann["url"]?.ToString() ?? "";
                sources.AppendLine($"• {title}: {url}");
              }
            }
          }

          if (string.IsNullOrWhiteSpace(outputText)) {
            return string.Empty;
          }
          else {
            return ExtractJsonBlock(outputText);
          }

          var resultBuilder = new StringBuilder();
          resultBuilder.AppendLine(outputText ?? "[Kein Antworttext gefunden]");
          if (sources.Length > 0) {
            resultBuilder.AppendLine();
            resultBuilder.AppendLine("ߔנQuellen:");
            resultBuilder.Append(sources);
          }

          return resultBuilder.ToString().Trim();
        }
        catch (Exception ex) {
          // Für Logging oder Debug
          Console.WriteLine("Fehler beim Parsen: " + ex.Message);
          return string.Empty;
        }
      }

    }

    internal string CallGptWebSearchApi_ALT(string prompt) {

      if (prompt == null) {
        return null;
      }

      var endpoint = "https://api.openai.com/v1/responses";
      var requestBody = new {
        model = "gpt-4o",
        input = prompt,
        tools = new[]
          {
            new { type = "web_search_preview" }
        }
      };

      var jsonBody = JsonConvert.SerializeObject(requestBody);

      using (HttpClient client = new HttpClient()) {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _OpenAiApiKey);

        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = client.PostAsync(endpoint, content).Result;

        var responseContent = response.Content.ReadAsStringAsync().Result;

        var parsed = JObject.Parse(responseContent);
        string result = parsed["output_text"]?.ToString();
        return result ?? string.Empty;
      }

    }

    #endregion

    public string CallWebSearchApi(string prompt, object inputData = null) {
      return CallWebSearchApi<string>(prompt, inputData);
    }

    public T CallWebSearchApi<T>(string prompt, object inputData = null) where T : class {

      var endpoint = "https://api.openai.com/v1/responses";

      StringBuilder promptBuilder = new StringBuilder(1000);

      promptBuilder.AppendLine(prompt);
      promptBuilder.AppendLine();

      Type t = typeof(T);

      if (t == typeof(string)) {

      }
      else {

        T probe;
        if (t.IsArray) {
          probe = Array.CreateInstance(t.GetElementType(), 1) as T;
          (probe as Array).SetValue(Activator.CreateInstance(t.GetElementType()), 0);
        }
        else {
          probe = Activator.CreateInstance<T>();
        }

        promptBuilder.AppendLine("LIEFERE ALS ANTWORT AUSSCHLIEßLICH ANTWORT-JSON, DAS SICH AN FOLGENDE STRUKTUR HÄLT:");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine(JsonConvert.SerializeObject(probe, formatting: Newtonsoft.Json.Formatting.Indented));
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Für alle Arrays wird die Anzahl der zu erzeugenden Einträge durch den Prompt (s.o.) definiert.");
        promptBuilder.AppendLine();

      }

      if (inputData != null) {
        promptBuilder.AppendLine("HIER SIND DIE EINGABEDATEN IN JSON-FORM:");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine(JsonConvert.SerializeObject(inputData, formatting: Newtonsoft.Json.Formatting.Indented));
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("ENDE DER EINGABEDATEN");
        promptBuilder.AppendLine();
      }

      if (t == typeof(string)) {
        promptBuilder.AppendLine("Verzichte komplett auf Rückfragen oder einleitende Sätze zu deiner Aufgabenstellung - LIEFERE NUR DIE ANGEFORDERTE ANTWORT!");

        if (prompt.Contains("arkdown")) {
          promptBuilder.AppendLine("Verzichte zudem auf die einklammerung via '```markdown'");
        }

      }
      else {
        promptBuilder.AppendLine("Verzichte komplett auf einleitende oder erklärende Sätze - LIEFERE NUR DAS BESCHRIEBENE ANTWORT-JSON!");
      }

      //var requestBody = new {
      //  model = "gpt-4o",
      //  input = promptBuilder.ToString(),
      //  tools = new[] { new { type = "web_search_preview" } },
      //  max_output_tokens = 8192
      //};
      var requestBody = new {
        model = "gpt-4o",
        input = promptBuilder.ToString(),
        tools = new[] { new { type = "web_search_preview" } },
        max_output_tokens = 8192
      };
      //var requestBody = new {
      //  model = "gpt-5",
      //  input = promptBuilder.ToString(),
      //  tools = new[] { new { type = "web_search" } },
      //  max_output_tokens = 8192
      //};

      var openAiRequestJson = JsonConvert.SerializeObject(requestBody);

      using (HttpClient client = new HttpClient()) {
        client.Timeout = TimeSpan.FromSeconds(240);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _OpenAiApiKey);

        var content = new StringContent(openAiRequestJson, Encoding.UTF8, "application/json");

        var response = client.PostAsync(endpoint, content).Result;
        string responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        try {
          var parsed = JObject.Parse(responseContent);

          // Suche nach dem assistant-Antwort-Element
          JToken messageBlock = null;
          if (parsed.ContainsKey("output") && parsed["output"].HasValues) {
            messageBlock = parsed["output"]?.FirstOrDefault(o => o["type"]?.ToString() == "message");
          }

          string errorMessage = null;
          if (parsed.ContainsKey("error") && parsed["error"].HasValues) {
            errorMessage = parsed["error"]?["message"]?.ToString();
          }

          var contentArray = messageBlock?["content"] as JArray;

          var outputText = contentArray?
              .FirstOrDefault(c => c["type"]?.ToString() == "output_text")?["text"]?.ToString();

          // Quellen extrahieren
          var annotations = contentArray?
              .FirstOrDefault(c => c["type"]?.ToString() == "output_text")?["annotations"] as JArray;

          var sources = new StringBuilder();
          if (annotations != null) {
            foreach (var ann in annotations) {
              if (ann["type"]?.ToString() == "url_citation") {
                var title = ann["title"]?.ToString() ?? "Quelle";
                var url = ann["url"]?.ToString() ?? "";
                sources.AppendLine($"• {title}: {url}");
              }
            }
          }

          if (string.IsNullOrWhiteSpace(outputText)) {

            if (string.IsNullOrEmpty(errorMessage)) {
              throw new Exception("Response does not contain 'outputText'");
            }
            else {
              throw new Exception(errorMessage);
            }

          }
          else if (t == typeof(string)) {
            string text = (outputText as string);

            //HACK: ekelhaft!
            if (text.StartsWith("```markdown")) {
              text = text.Substring(11);

              if (text.StartsWith(Environment.NewLine)) {
                text = text.Substring(Environment.NewLine.Length);
              }
              if (text.StartsWith("#")) {
                text = text.Substring(1);
              }
              if (text.EndsWith("```")) {
                text = text.Substring(0, text.Length - 3);
              }
            }

            return (text as T);
          }
          else {
            string resultJson = ExtractJsonBlock(outputText);
            return JsonConvert.DeserializeObject<T>(resultJson);
          }

        }
        catch (Exception ex) {
          DevLogger.LogError(ex);
          throw new Exception("Error while calling OpenAI services: " + ex.Message, ex);
        }


      }
    }

    public byte[] CallImageEditApi(byte[] inputImageBytes, string prompt, byte[] maskImageBytes = null) {
      try {


        HttpClientHandler handler = new HttpClientHandler();
        HttpClient client = new HttpClient(handler, false);
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/edits");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _OpenAiApiKey);

        MultipartFormDataContent form = new MultipartFormDataContent();
        form.Add(new StringContent("gpt-image-1"), "model");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent("1024x1024"), "size");
        form.Add(new StringContent("b64_json"), "response_format");


        if (inputImageBytes != null && inputImageBytes.Length > 0) {
          ByteArrayContent imageContent = new ByteArrayContent(inputImageBytes);
          imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
          form.Add(imageContent, "image", "image.dat");
        }

        if (maskImageBytes != null && maskImageBytes.Length > 0) {
          ByteArrayContent maskContent = new ByteArrayContent(maskImageBytes);
          maskContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
          form.Add(maskContent, "mask", "mask.dat");
        }

        request.Content = form;
        HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult();

        if (response == null) {
          throw new InvalidOperationException("No HTTP response received.");
        }

        if (response.StatusCode != HttpStatusCode.OK) {
          string errorText = response.Content == null ? "Unknown error" : response.Content.ReadAsStringAsync().Result;
          throw new InvalidOperationException("OpenAI API error: " + (int)response.StatusCode + " - " + errorText);
        }

        string json = response.Content.ReadAsStringAsync().Result;
        ImageGenerationResponse result = JsonConvert.DeserializeObject<ImageGenerationResponse>(json);

        if (result == null) {
          throw new InvalidOperationException("Failed to parse API response.");
        }

        if (result.Data == null || result.Data.Length == 0) {
          throw new InvalidOperationException("Response contains no image data.");
        }

        byte[] bytes = Convert.FromBase64String(result.Data[0].B64Json);
        return bytes;

      }
      catch (Exception ex) {
        return null;
      }
    }

    public byte[] CallImageGeneratorApi(string prompt) {

      try {

        // Request-Payload vorbereiten
        ImageGenerationRequest request = new ImageGenerationRequest();
        request.Model = "gpt-image-1";
        request.Prompt = prompt;
        request.Size = "1024x1024";
        request.N = 1; // Anzahl Bilder

        string jsonBody = JsonConvert.SerializeObject(request);

        // HTTP-Request senden (synchron)
        HttpClientHandler handler = new HttpClientHandler();
        HttpClient client = new HttpClient(handler, false);
        HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations");
        httpRequest.Headers.Add("Authorization", "Bearer " + _OpenAiApiKey);
        httpRequest.Headers.Add("Accept", "application/json");
        httpRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        HttpResponseMessage response = client.SendAsync(httpRequest).GetAwaiter().GetResult();

        if (response == null) {
          throw new InvalidOperationException("No HTTP response received.");
        }

        if (response.StatusCode != HttpStatusCode.OK) {
          string errorText = response.Content == null ? "Unknown error" : response.Content.ReadAsStringAsync().Result;
          throw new InvalidOperationException("OpenAI API error: " + (int)response.StatusCode + " - " + errorText);
        }

        string responseText = response.Content.ReadAsStringAsync().Result;
        ImageGenerationResponse result = JsonConvert.DeserializeObject<ImageGenerationResponse>(responseText);

        if (result == null) {
          throw new InvalidOperationException("Failed to parse API response.");
        }

        if (result.Data == null) {
          throw new InvalidOperationException("Response contains no data array.");
        }

        if (result.Data.Length == 0) {
          throw new InvalidOperationException("Response contains empty data array.");
        }

        string base64 = result.Data[0].B64Json;
        if (string.IsNullOrEmpty(base64) == true) {
          throw new InvalidOperationException("b64_json was empty.");
        }

        byte[] pngBytes = Convert.FromBase64String(base64);
        return pngBytes;

      }
      catch (Exception ex) {
        return null;
      }
    }

    private static string ExtractJsonBlock(string input) {
      if (string.IsNullOrWhiteSpace(input))
        return null;

      // Regex: fängt den Block zwischen ```json ... ``` ab
      var match = Regex.Match(input, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
      string jsonCandidate;
      if (!match.Success || match.Groups.Count < 2) {
        if (input.StartsWith("{") || input.StartsWith("[")) {
          jsonCandidate = input;
        }
        else {
          return null;
        }
      }
      else {
        jsonCandidate = match.Groups[1].Value.Trim();
      }

      // Prüfen, ob das JSON escaped ist (beginnt und endet mit Anführungszeichen
      // und enthält viele \" Sequenzen)
      if (jsonCandidate.StartsWith("\\")) {
        try {
          // Versuch, das Escaping aufzulösen
          string unescaped = JsonConvert.DeserializeObject<string>("\"" + jsonCandidate + "\"") ?? jsonCandidate;
          jsonCandidate = unescaped;
        }
        catch {
          // Falls das Deserialisieren fehlschlägt, belassen wir es
        }
      }

      return jsonCandidate;
    }

    /// <summary>
    /// Request-DTO für /v1/images/generations
    /// </summary>
    internal sealed class ImageGenerationRequest {

      [JsonProperty("model")]
      public string Model {
        get { return _Model; }
        set { _Model = value; }
      }
      private string _Model;

      [JsonProperty("prompt")]
      public string Prompt {
        get { return _Prompt; }
        set { _Prompt = value; }
      }
      private string _Prompt;

      [JsonProperty("size")]
      public string Size {
        get { return _Size; }
        set { _Size = value; }
      }
      private string _Size;

      [JsonProperty("n")]
      public int N {
        get { return _N; }
        set { _N = value; }
      }
      private int _N;

      //[JsonProperty("response_format")]
      //public string ResponseFormat {
      //  get { return _ResponseFormat; }
      //  set { _ResponseFormat = value; }
      //}
      //private string _ResponseFormat = "b64_json";

    }

    /// <summary>
    /// Response-DTOs gemäß OpenAI Images API (b64_json).
    /// </summary>
    internal sealed class ImageGenerationResponse {

      [JsonProperty("created")]
      public long Created {
        get { return _Created; }
        set { _Created = value; }
      }
      private long _Created;

      [JsonProperty("data")]
      public ImageData[] Data {
        get { return _Data; }
        set { _Data = value; }
      }
      private ImageData[] _Data;

    }

    internal sealed class ImageData {

      [JsonProperty("b64_json")]
      public string B64Json {
        get { return _B64Json; }
        set { _B64Json = value; }
      }
      private string _B64Json;

      [JsonProperty("revised_prompt")]
      public string RevisedPrompt {
        get { return _RevisedPrompt; }
        set { _RevisedPrompt = value; }
      }
      private string _RevisedPrompt;

    }

  }

}
