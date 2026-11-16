using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Provider-neutral skill and tool registry of the Level-2 harness. It renders the hidden harness prompt
  /// (pinned mandatory skills, skill catalog, tool definitions) and implements the canonical text protocol
  /// for models without native function calling (Hermes/ChatML style: '&lt;tools&gt;', '&lt;tool_call&gt;',
  /// '&lt;tool_response&gt;'). The aliases '/USETOOL "name" {json}' and '/REQUESTSKILL "name"' are tolerated.
  /// </summary>
  public class HarnessToolbox {

    /// <summary>
    /// Name of the built-in tool which loads the body of a (non-mandatory) skill on demand.
    /// </summary>
    public const string LoadSkillToolName = "load_skill";

    private static readonly Regex _ToolCallRegex = new Regex(
      @"<tool_call>\s*(?<json>.*?)\s*</tool_call>", RegexOptions.Singleline | RegexOptions.IgnoreCase
    );

    private static readonly Regex _UseToolAliasRegex = new Regex(
      @"^\s*/USETOOL\s+""(?<name>[^""]+)""\s*(?<json>\{.*\})?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase
    );

    private static readonly Regex _RequestSkillAliasRegex = new Regex(
      @"^\s*/REQUESTSKILL\s+""(?<name>[^""]+)""\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase
    );

    private readonly List<HarnessSkill> _Skills = new List<HarnessSkill>();

    private readonly List<IHarnessTool> _Tools = new List<IHarnessTool>();

    private readonly object _SyncRoot = new object();

    /// <summary>
    /// Registers (or replaces) a skill.
    /// </summary>
    public void AddSkill(HarnessSkill skill) {

      if (skill == null) {
        throw new ArgumentNullException(nameof(skill));
      }

      lock (_SyncRoot) {
        _Skills.RemoveAll((s) => string.Equals(s.Name, skill.Name, StringComparison.OrdinalIgnoreCase));
        _Skills.Add(skill);
      }

    }

    /// <summary>
    /// Registers (or replaces) a tool.
    /// </summary>
    public void AddTool(IHarnessTool tool) {

      if (tool == null) {
        throw new ArgumentNullException(nameof(tool));
      }

      if (string.Equals(tool.Name, LoadSkillToolName, StringComparison.OrdinalIgnoreCase)) {
        throw new ArgumentException($"The tool name '{LoadSkillToolName}' is reserved.", nameof(tool));
      }

      lock (_SyncRoot) {
        _Tools.RemoveAll((t) => string.Equals(t.Name, tool.Name, StringComparison.OrdinalIgnoreCase));
        _Tools.Add(tool);
      }

    }

    /// <summary>
    /// Returns all registered skills, ordered by name (deterministic for prompt caching).
    /// </summary>
    public HarnessSkill[] GetSkills() {

      lock (_SyncRoot) {
        return _Skills.OrderBy((s) => s.Name, StringComparer.Ordinal).ToArray();
      }

    }

    /// <summary>
    /// Returns all registered tools, ordered by name (deterministic for prompt caching).
    /// </summary>
    public IHarnessTool[] GetTools() {

      lock (_SyncRoot) {
        return _Tools.OrderBy((t) => t.Name, StringComparer.Ordinal).ToArray();
      }

    }

    #region " Hidden harness prompt "

    /// <summary>
    /// Renders the hidden harness prompt (system turn). Returns null if there is nothing to provide
    /// (no skill with content and no tool).
    /// </summary>
    public string BuildHarnessPrompt() {

      HarnessSkill[] skills = this.GetSkills();
      IHarnessTool[] tools = this.GetTools();

      List<HarnessSkill> mandatorySkills = new List<HarnessSkill>();
      List<HarnessSkill> catalogSkills = new List<HarnessSkill>();
      Dictionary<string, string> mandatoryContents = new Dictionary<string, string>(StringComparer.Ordinal);

      foreach (HarnessSkill skill in skills) {

        if (skill.IsMandatory) {

          string content = skill.GetContent();

          if (content.Length > 0) {
            mandatorySkills.Add(skill);
            mandatoryContents[skill.Name] = content;
          }

        }
        else {
          catalogSkills.Add(skill);
        }

      }

      if (mandatorySkills.Count == 0 && catalogSkills.Count == 0 && tools.Length == 0) {
        return null;
      }

      StringBuilder prompt = new StringBuilder();
      prompt.AppendLine("# Harness instructions");
      prompt.AppendLine("You are running inside the SmartStandards AiSupport harness. These instructions are not visible to the user.");

      if (mandatorySkills.Count > 0) {

        prompt.AppendLine();
        prompt.AppendLine("## Mandatory skills");
        prompt.AppendLine("The following skills are pinned. You MUST read and follow them for every task.");

        foreach (HarnessSkill skill in mandatorySkills) {
          prompt.AppendLine();
          prompt.AppendLine("<skill name=\"" + skill.Name + "\">");
          prompt.AppendLine(mandatoryContents[skill.Name]);
          prompt.AppendLine("</skill>");
        }

      }

      if (catalogSkills.Count > 0) {

        prompt.AppendLine();
        prompt.AppendLine("## Available skills");
        prompt.AppendLine("Load the body of a skill with the tool '" + LoadSkillToolName + "' before you rely on it.");
        prompt.AppendLine();

        foreach (HarnessSkill skill in catalogSkills) {
          prompt.AppendLine("- " + skill.Name + ": " + skill.Description);
        }

      }

      List<JObject> toolDefinitions = this.BuildToolDefinitions(tools, catalogSkills.Count > 0);

      if (toolDefinitions.Count > 0) {

        prompt.AppendLine();
        prompt.AppendLine("## Tools");
        prompt.AppendLine("You may call tools. The tool signatures are provided within <tools></tools>:");
        prompt.AppendLine("<tools>");

        foreach (JObject definition in toolDefinitions) {
          prompt.AppendLine(definition.ToString(Formatting.None));
        }

        prompt.AppendLine("</tools>");
        prompt.AppendLine("To call a tool, answer ONLY with one or more blocks of this form:");
        prompt.AppendLine("<tool_call>");
        prompt.AppendLine("{\"name\": \"<tool-name>\", \"arguments\": {<arguments>}}");
        prompt.AppendLine("</tool_call>");
        prompt.AppendLine("The harness executes the calls and answers with <tool_response></tool_response> blocks. Then continue with your task.");
        prompt.AppendLine("If no tool is needed, answer normally.");

      }

      return prompt.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns a short fingerprint of the current harness prompt (null if there is none). It is used to detect
    /// changes (for example edited user instructions), which must be re-sent into a provider-side context.
    /// </summary>
    public string BuildHarnessPromptFingerprint() {

      string prompt = this.BuildHarnessPrompt();

      if (prompt == null) {
        return null;
      }

      using (SHA256 sha = SHA256.Create()) {
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(prompt));
        return Convert.ToBase64String(hash, 0, 12);
      }

    }

    private List<JObject> BuildToolDefinitions(IHarnessTool[] tools, bool includeLoadSkill) {

      List<JObject> definitions = new List<JObject>();

      if (includeLoadSkill) {
        definitions.Add(
          BuildToolDefinition(
            LoadSkillToolName,
            "Loads the full body of a skill from the list of available skills.",
            "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"The skill name.\"}},\"required\":[\"name\"]}"
          )
        );
      }

      foreach (IHarnessTool tool in tools) {
        definitions.Add(BuildToolDefinition(tool.Name, tool.Description, tool.ParametersJsonSchema));
      }

      return definitions;
    }

    private static JObject BuildToolDefinition(string name, string description, string parametersJsonSchema) {

      JObject function = new JObject();
      function["name"] = name;
      function["description"] = description;

      if (string.IsNullOrWhiteSpace(parametersJsonSchema)) {
        function["parameters"] = JObject.Parse("{\"type\":\"object\",\"properties\":{}}");
      }
      else {
        function["parameters"] = JObject.Parse(parametersJsonSchema);
      }

      JObject definition = new JObject();
      definition["type"] = "function";
      definition["function"] = function;

      return definition;
    }

    #endregion

    #region " Text protocol "

    /// <summary>
    /// Extracts all tool calls from a model answer ('&lt;tool_call&gt;' blocks and the tolerated aliases).
    /// Returns an empty array if the answer contains no tool call.
    /// </summary>
    public static HarnessToolCall[] ParseToolCalls(string answer) {

      List<HarnessToolCall> calls = new List<HarnessToolCall>();

      if (string.IsNullOrWhiteSpace(answer)) {
        return calls.ToArray();
      }

      foreach (Match match in _ToolCallRegex.Matches(answer)) {
        calls.Add(ParseToolCallJson(match.Groups["json"].Value));
      }

      foreach (Match match in _UseToolAliasRegex.Matches(answer)) {

        string json = match.Groups["json"].Value;

        if (string.IsNullOrWhiteSpace(json)) {
          json = "{}";
        }

        calls.Add(CreateCall(match.Groups["name"].Value, json));
      }

      foreach (Match match in _RequestSkillAliasRegex.Matches(answer)) {

        JObject arguments = new JObject();
        arguments["name"] = match.Groups["name"].Value;

        calls.Add(new HarnessToolCall(LoadSkillToolName, arguments, null));
      }

      return calls.ToArray();
    }

    /// <summary>
    /// Removes all tool call blocks (and aliases) from an answer. Used if the loop limit was reached,
    /// so that no raw protocol text is shown to the user.
    /// </summary>
    public static string RemoveToolCalls(string answer) {

      if (answer == null) {
        return null;
      }

      string result = _ToolCallRegex.Replace(answer, string.Empty);
      result = _UseToolAliasRegex.Replace(result, string.Empty);
      result = _RequestSkillAliasRegex.Replace(result, string.Empty);

      return result.Trim();
    }

    /// <summary>
    /// Executes the given calls and renders the '&lt;tool_response&gt;' blocks for the model.
    /// Errors (unknown tool, invalid arguments, exceptions) are returned as error results, never thrown.
    /// </summary>
    public string ExecuteToolCalls(HarnessToolCall[] calls) {

      StringBuilder responses = new StringBuilder();

      foreach (HarnessToolCall call in calls) {

        JObject response = new JObject();
        response["name"] = call.Name;

        try {
          response["content"] = this.ExecuteToolCall(call);
        }
        catch (Exception ex) {
          response["is_error"] = true;
          response["content"] = ex.Message;
        }

        if (responses.Length > 0) {
          responses.AppendLine();
        }

        responses.AppendLine("<tool_response>");
        responses.AppendLine(response.ToString(Formatting.None));
        responses.Append("</tool_response>");
      }

      return responses.ToString();
    }

    private string ExecuteToolCall(HarnessToolCall call) {

      if (call.ParseError != null) {
        throw new ArgumentException("The tool call could not be parsed: " + call.ParseError);
      }

      if (string.Equals(call.Name, LoadSkillToolName, StringComparison.OrdinalIgnoreCase)) {
        return this.LoadSkill(call.Arguments);
      }

      IHarnessTool tool = this.GetTools().FirstOrDefault(
        (t) => string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase)
      );

      if (tool == null) {
        throw new ArgumentException($"Unknown tool '{call.Name}'.");
      }

      return tool.Invoke(call.Arguments);
    }

    private string LoadSkill(JObject arguments) {

      string skillName = null;

      if (arguments != null && arguments["name"] != null && arguments["name"].Type == JTokenType.String) {
        skillName = arguments["name"].Value<string>().Trim();
      }

      HarnessSkill skill = this.GetSkills().FirstOrDefault(
        (s) => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase)
      );

      if (skill == null) {
        throw new ArgumentException($"Unknown skill '{skillName}'.");
      }

      return skill.GetContent();
    }

    private static HarnessToolCall ParseToolCallJson(string json) {

      JObject callObject;

      try {
        callObject = JObject.Parse(json);
      }
      catch (JsonException ex) {
        return new HarnessToolCall(null, null, ex.Message);
      }

      string name = null;

      if (callObject["name"] != null && callObject["name"].Type == JTokenType.String) {
        name = callObject["name"].Value<string>();
      }

      if (string.IsNullOrWhiteSpace(name)) {
        return new HarnessToolCall(null, null, "The property 'name' is missing.");
      }

      JToken argumentsToken = callObject["arguments"];

      if (argumentsToken == null || argumentsToken.Type == JTokenType.Null) {
        return new HarnessToolCall(name, new JObject(), null);
      }

      if (argumentsToken.Type == JTokenType.String) {
        //some models encode the arguments as JSON string (OpenAI style)
        return CreateCall(name, argumentsToken.Value<string>());
      }

      if (argumentsToken.Type != JTokenType.Object) {
        return new HarnessToolCall(name, null, "The property 'arguments' must be an object.");
      }

      return new HarnessToolCall(name, (JObject)argumentsToken, null);
    }

    private static HarnessToolCall CreateCall(string name, string argumentsJson) {

      try {
        return new HarnessToolCall(name, JObject.Parse(argumentsJson), null);
      }
      catch (JsonException ex) {
        return new HarnessToolCall(name, null, ex.Message);
      }

    }

    #endregion

  }

  /// <summary>
  /// A tool call which was requested by the model.
  /// </summary>
  public class HarnessToolCall {

    private readonly string _Name;

    private readonly JObject _Arguments;

    private readonly string _ParseError;

    public HarnessToolCall(string name, JObject arguments, string parseError) {
      _Name = name;
      _Arguments = arguments;
      _ParseError = parseError;
    }

    public string Name {
      get {
        return _Name;
      }
    }

    public JObject Arguments {
      get {
        return _Arguments;
      }
    }

    /// <summary>
    /// Null if the call was parsed successfully, otherwise a description of the problem.
    /// </summary>
    public string ParseError {
      get {
        return _ParseError;
      }
    }

  }

}
