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

    private readonly List<HarnessSource> _Sources = new List<HarnessSource>();

    private readonly Dictionary<string, string> _SkillSourceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _ToolSourceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly object _SyncRoot = new object();

    #region " Registration "

    /// <summary>
    /// Registers (or replaces) a skill source or tool source. Skills/tools can be assigned to it
    /// via 'AddSkill(skill, sourceId)' / 'AddTool(tool, sourceId)'.
    /// </summary>
    public void AddSource(HarnessSource source) {

      if (source == null) {
        throw new ArgumentNullException(nameof(source));
      }

      lock (_SyncRoot) {
        _Sources.RemoveAll((s) => string.Equals(s.SourceId, source.SourceId, StringComparison.OrdinalIgnoreCase));
        _Sources.Add(source);
      }

    }

    /// <summary>
    /// Removes a source including all skills/tools which belong to it. Unknown ids are ignored.
    /// </summary>
    public void RemoveSource(string sourceId) {

      if (string.IsNullOrWhiteSpace(sourceId)) {
        return;
      }

      lock (_SyncRoot) {

        _Sources.RemoveAll((s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

        string[] skillNames = _SkillSourceIds.Where(
          (e) => string.Equals(e.Value, sourceId, StringComparison.OrdinalIgnoreCase)
        ).Select((e) => e.Key).ToArray();

        foreach (string skillName in skillNames) {
          _SkillSourceIds.Remove(skillName);
          _Skills.RemoveAll((s) => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase));
        }

        string[] toolNames = _ToolSourceIds.Where(
          (e) => string.Equals(e.Value, sourceId, StringComparison.OrdinalIgnoreCase)
        ).Select((e) => e.Key).ToArray();

        foreach (string toolName in toolNames) {
          _ToolSourceIds.Remove(toolName);
          _Tools.RemoveAll((t) => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
        }

      }

    }

    /// <summary>
    /// Returns all registered sources, ordered by id.
    /// </summary>
    public HarnessSource[] GetSources() {

      lock (_SyncRoot) {
        return _Sources.OrderBy((s) => s.SourceId, StringComparer.Ordinal).ToArray();
      }

    }

    /// <summary>
    /// Returns the source with the given id or null.
    /// </summary>
    public HarnessSource GetSource(string sourceId) {

      lock (_SyncRoot) {
        return _Sources.FirstOrDefault((s) => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
      }

    }

    /// <summary>
    /// Registers (or replaces) a skill which belongs to the given (already registered) skill source.
    /// </summary>
    public void AddSkill(HarnessSkill skill, string sourceId) {

      this.EnsureSourceExists(sourceId, false);
      this.AddSkill(skill);

      lock (_SyncRoot) {
        _SkillSourceIds[skill.Name] = sourceId;
      }

    }

    /// <summary>
    /// Registers (or replaces) a tool which belongs to the given (already registered) tool source.
    /// </summary>
    public void AddTool(IHarnessTool tool, string sourceId) {

      this.EnsureSourceExists(sourceId, true);
      this.AddTool(tool);

      lock (_SyncRoot) {
        _ToolSourceIds[tool.Name] = sourceId;
      }

    }

    /// <summary>
    /// Returns the id of the source of the given skill (null for skills without source).
    /// </summary>
    public string GetSkillSourceId(string skillName) {

      lock (_SyncRoot) {

        string sourceId;

        if (skillName != null && _SkillSourceIds.TryGetValue(skillName, out sourceId)) {
          return sourceId;
        }

        return null;
      }

    }

    /// <summary>
    /// Returns the id of the source of the given tool (null for tools without source).
    /// </summary>
    public string GetToolSourceId(string toolName) {

      lock (_SyncRoot) {

        string sourceId;

        if (toolName != null && _ToolSourceIds.TryGetValue(toolName, out sourceId)) {
          return sourceId;
        }

        return null;
      }

    }

    private void EnsureSourceExists(string sourceId, bool isToolSource) {

      if (string.IsNullOrWhiteSpace(sourceId)) {
        throw new ArgumentNullException(nameof(sourceId));
      }

      HarnessSource source = this.GetSource(sourceId);

      if (source == null || source.IsToolSource != isToolSource) {
        throw new ArgumentException($"The source '{sourceId}' is not registered (as matching source kind).", nameof(sourceId));
      }

    }

    #endregion

    #region " Effective levels "

    /// <summary>
    /// Returns the effective level of a skill source within the given scope. Per-chat overrides can only
    /// raise the global default (one-way), fixed sources ignore overrides.
    /// </summary>
    public string GetEffectiveSkillSourceLevel(string sourceId, HarnessScope scope) {

      HarnessSource source = this.GetSource(sourceId);

      if (source == null) {
        return SkillSourceLevels.Hidden;
      }

      string level = source.DefaultLevel;
      string overrideLevel;

      if (!source.IsFixed && scope != null && scope.SkillLevelOverrides.TryGetValue(source.SourceId, out overrideLevel)) {

        if (GetSkillLevelRank(overrideLevel) > GetSkillLevelRank(level)) {
          level = overrideLevel;
        }

      }

      return level;
    }

    /// <summary>
    /// Returns the effective level of a tool source within the given scope (per-chat override or global default).
    /// </summary>
    public string GetEffectiveToolSourceLevel(string sourceId, HarnessScope scope) {

      HarnessSource source = this.GetSource(sourceId);

      if (source == null) {
        return ToolSourceLevels.Hidden;
      }

      string overrideLevel;

      if (!source.IsFixed && scope != null && scope.ToolLevelOverrides.TryGetValue(source.SourceId, out overrideLevel)) {

        if (GetToolLevelRank(overrideLevel) >= 0) {
          return overrideLevel;
        }

      }

      return source.DefaultLevel;
    }

    /// <summary>
    /// Rank of a skill source level (Hidden=0, OnDemand=1, InitializeAtStart=2, unknown=-1).
    /// </summary>
    public static int GetSkillLevelRank(string level) {

      if (string.Equals(level, SkillSourceLevels.Hidden, StringComparison.OrdinalIgnoreCase)) {
        return 0;
      }

      if (string.Equals(level, SkillSourceLevels.OnDemand, StringComparison.OrdinalIgnoreCase)) {
        return 1;
      }

      if (string.Equals(level, SkillSourceLevels.InitializeAtStart, StringComparison.OrdinalIgnoreCase)) {
        return 2;
      }

      return -1;
    }

    /// <summary>
    /// Rank of a tool source level (Hidden=0, RequiresConfirmation=1, NoConfirmation=2, unknown=-1).
    /// </summary>
    public static int GetToolLevelRank(string level) {

      if (string.Equals(level, ToolSourceLevels.Hidden, StringComparison.OrdinalIgnoreCase)) {
        return 0;
      }

      if (string.Equals(level, ToolSourceLevels.RequiresConfirmation, StringComparison.OrdinalIgnoreCase)) {
        return 1;
      }

      if (string.Equals(level, ToolSourceLevels.NoConfirmation, StringComparison.OrdinalIgnoreCase)) {
        return 2;
      }

      return -1;
    }

    /// <summary>
    /// Returns the skills visible within the given scope: pinned (mandatory or level 'InitializeAtStart')
    /// and catalog (level 'OnDemand'). Skills without source keep the legacy behavior.
    /// </summary>
    private void ClassifySkills(HarnessScope scope, List<HarnessSkill> pinnedSkills, List<HarnessSkill> catalogSkills) {

      foreach (HarnessSkill skill in this.GetSkills()) {

        string sourceId = this.GetSkillSourceId(skill.Name);

        if (sourceId == null) {

          if (skill.IsMandatory) {
            pinnedSkills.Add(skill);
          }
          else {
            catalogSkills.Add(skill);
          }

          continue;
        }

        int rank = GetSkillLevelRank(this.GetEffectiveSkillSourceLevel(sourceId, scope));

        if (rank <= 0) {
          continue;
        }

        if (skill.IsMandatory || rank == 2) {
          pinnedSkills.Add(skill);
        }
        else {
          catalogSkills.Add(skill);
        }

      }

    }

    /// <summary>
    /// Returns the tools visible within the given scope (tools without source are always visible).
    /// </summary>
    private IHarnessTool[] GetVisibleTools(HarnessScope scope) {

      List<IHarnessTool> visibleTools = new List<IHarnessTool>();

      foreach (IHarnessTool tool in this.GetTools()) {

        string sourceId = this.GetToolSourceId(tool.Name);

        if (sourceId == null || GetToolLevelRank(this.GetEffectiveToolSourceLevel(sourceId, scope)) > 0) {
          visibleTools.Add(tool);
        }

      }

      return visibleTools.ToArray();
    }

    #endregion

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

      return this.BuildHarnessPrompt(HarnessScope.CreateDefault());
    }

    /// <summary>
    /// Renders the hidden harness prompt for the given (per-chat) scope. Returns null if there is nothing
    /// to provide (no visible skill with content and no visible tool).
    /// </summary>
    public string BuildHarnessPrompt(HarnessScope scope) {

      IHarnessTool[] tools = this.GetVisibleTools(scope);

      List<HarnessSkill> pinnedSkills = new List<HarnessSkill>();
      List<HarnessSkill> catalogSkills = new List<HarnessSkill>();
      this.ClassifySkills(scope, pinnedSkills, catalogSkills);

      List<HarnessSkill> mandatorySkills = new List<HarnessSkill>();
      Dictionary<string, string> mandatoryContents = new Dictionary<string, string>(StringComparer.Ordinal);

      foreach (HarnessSkill skill in pinnedSkills) {

        string content = skill.GetContent();

        if (content.Length > 0) {
          mandatorySkills.Add(skill);
          mandatoryContents[skill.Name] = content;
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
        prompt.AppendLine("Load the body of a skill with the harness tool '" + LoadSkillToolName + "' (via a <tool_call> text block, see below) before you rely on it.");
        prompt.AppendLine();

        foreach (HarnessSkill skill in catalogSkills) {
          prompt.AppendLine("- " + skill.Name + ": " + skill.Description);
        }

      }

      List<JObject> toolDefinitions = this.BuildToolDefinitions(tools, catalogSkills.Count > 0);

      if (toolDefinitions.Count > 0) {

        prompt.AppendLine();
        prompt.AppendLine("## Harness tools (second tool layer - text protocol only)");
        prompt.AppendLine("IMPORTANT: The tools listed below are NOT part of your native/built-in tool set (they are not function-calling");
        prompt.AppendLine("tools, MCP tools or shell commands of your runtime, and you cannot find or execute them yourself).");
        prompt.AppendLine("They form a SECOND tool layer which is provided by the host application that hosts this conversation.");
        prompt.AppendLine("The ONLY way to invoke them is to write a <tool_call> block as plain TEXT into your answer message.");
        prompt.AppendLine("The host parses your answer text, executes the calls on its side and sends the results back to you");
        prompt.AppendLine("as the next message, containing <tool_response></tool_response> blocks.");
        prompt.AppendLine("Rules:");
        prompt.AppendLine("- Never try to call a harness tool via your native tool mechanism, and never claim that a harness tool does not exist.");
        prompt.AppendLine("- Your own native tools (if any) stay available and are independent of this list.");
        prompt.AppendLine("- When the task needs a harness tool, end your answer with the <tool_call> block(s) and wait for the <tool_response>.");
        prompt.AppendLine("- Never invent a <tool_response> yourself.");
        prompt.AppendLine("The signatures of the harness tools are provided within <tools></tools>:");
        prompt.AppendLine("<tools>");

        foreach (JObject definition in toolDefinitions) {
          prompt.AppendLine(definition.ToString(Formatting.None));
        }

        prompt.AppendLine("</tools>");
        prompt.AppendLine("To call a harness tool, answer with one or more blocks of exactly this form (plain text, JSON inside):");
        prompt.AppendLine("<tool_call>");
        prompt.AppendLine("{\"name\": \"<tool-name>\", \"arguments\": {<arguments>}}");
        prompt.AppendLine("</tool_call>");
        prompt.AppendLine("The host executes the calls and answers with <tool_response></tool_response> blocks. Then continue with your task.");
        prompt.AppendLine("If no harness tool is needed, answer normally.");

      }

      return prompt.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns a short fingerprint of the current harness prompt (null if there is none). It is used to detect
    /// changes (for example edited user instructions), which must be re-sent into a provider-side context.
    /// </summary>
    public string BuildHarnessPromptFingerprint() {

      return this.BuildHarnessPromptFingerprint(HarnessScope.CreateDefault());
    }

    /// <summary>
    /// Returns a short fingerprint of the harness prompt for the given scope (null if there is none).
    /// </summary>
    public string BuildHarnessPromptFingerprint(HarnessScope scope) {

      string prompt = this.BuildHarnessPrompt(scope);

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

        string description = tool.Description;
        string sourceId = this.GetToolSourceId(tool.Name);

        if (sourceId != null) {

          HarnessSource source = this.GetSource(sourceId);

          //(the additional description tells the model the purpose / the infrastructure of the source instance)
          if (source != null && source.AdditionalDescription != null) {
            description = description + "\n(Tool source '" + source.DisplayName + "': " + source.AdditionalDescription + ")";
          }

        }

        definitions.Add(BuildToolDefinition(tool.Name, description, tool.ParametersJsonSchema));
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

      return this.ExecuteToolCalls(calls, HarnessScope.CreateDefault());
    }

    /// <summary>
    /// Executes the given calls within the given (per-chat) scope: tools of hidden sources are not available,
    /// tools of sources with level 'RequiresConfirmation' are only executed if the approver of the scope agrees.
    /// Errors are returned as error results, never thrown.
    /// </summary>
    public string ExecuteToolCalls(HarnessToolCall[] calls, HarnessScope scope) {

      if (scope == null) {
        scope = HarnessScope.CreateDefault();
      }

      StringBuilder responses = new StringBuilder();

      foreach (HarnessToolCall call in calls) {

        JObject response = new JObject();
        response["name"] = call.Name;

        HarnessToolExecution execution = new HarnessToolExecution();
        execution.Call = call;
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try {
          string content = this.ExecuteToolCall(call, scope, execution);
          response["content"] = content;
          execution.Success = true;

          if (content != null) {
            execution.ResultSize = content.Length;
          }

        }
        catch (Exception ex) {
          response["is_error"] = true;
          response["content"] = ex.Message;
          execution.Success = false;
          execution.ErrorMessage = ex.Message;
        }

        stopwatch.Stop();
        execution.DurationMs = stopwatch.ElapsedMilliseconds;

        if (scope.OnToolExecuted != null) {
          scope.OnToolExecuted.Invoke(execution);
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

    private string ExecuteToolCall(HarnessToolCall call, HarnessScope scope, HarnessToolExecution execution) {

      if (call.ParseError != null) {
        throw new ArgumentException("The tool call could not be parsed: " + call.ParseError);
      }

      if (string.Equals(call.Name, LoadSkillToolName, StringComparison.OrdinalIgnoreCase)) {
        execution.Executed = true;
        return this.LoadSkill(call.Arguments, scope);
      }

      IHarnessTool tool = this.GetVisibleTools(scope).FirstOrDefault(
        (t) => string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase)
      );

      if (tool == null) {
        execution.SourceId = this.GetToolSourceId(call.Name);
        throw new ArgumentException($"Unknown tool '{call.Name}'.");
      }

      string sourceId = this.GetToolSourceId(tool.Name);
      execution.SourceId = sourceId;

      if (sourceId != null) {

        string level = this.GetEffectiveToolSourceLevel(sourceId, scope);

        if (string.Equals(level, ToolSourceLevels.RequiresConfirmation, StringComparison.OrdinalIgnoreCase)) {

          execution.RequiredConfirmation = true;
          bool approved = false;

          if (scope.ToolApprover != null) {
            approved = scope.ToolApprover.Invoke(call, this.GetSource(sourceId));
          }

          if (!approved) {
            throw new UnauthorizedAccessException($"The call of the tool '{tool.Name}' was denied by the user.");
          }

        }

      }

      execution.Executed = true;

      HarnessSource toolSource = null;

      if (sourceId != null) {
        toolSource = this.GetSource(sourceId);
      }

      HarnessToolContext previousContext = HarnessToolContext.Enter(new HarnessToolContext(call, toolSource, scope));

      try {
        return tool.Invoke(call.Arguments);
      }
      finally {
        HarnessToolContext.Restore(previousContext);
      }

    }

    private string LoadSkill(JObject arguments, HarnessScope scope) {

      string skillName = null;

      if (arguments != null && arguments["name"] != null && arguments["name"].Type == JTokenType.String) {
        skillName = arguments["name"].Value<string>().Trim();
      }

      List<HarnessSkill> visibleSkills = new List<HarnessSkill>();
      this.ClassifySkills(scope, visibleSkills, visibleSkills);

      HarnessSkill skill = visibleSkills.FirstOrDefault(
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
