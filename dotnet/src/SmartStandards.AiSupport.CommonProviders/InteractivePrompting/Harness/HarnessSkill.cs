using System;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// A skill (Agent Skills format: name + description + body) which is provided to the model by the Level-2 harness.
  /// Mandatory skills are pinned: their body is part of the hidden harness prompt of every context. Other skills
  /// only appear in the catalog (name + description) and are loaded on demand via the built-in tool 'load_skill'
  /// (progressive disclosure).
  /// </summary>
  public class HarnessSkill {

    /// <summary>
    /// Name of the mandatory skill which carries the user instructions from the settings.
    /// </summary>
    public const string UserInstructionsSkillName = "mustread-user-instructions";

    private readonly string _Name;

    private readonly string _Description;

    private readonly Func<string> _ContentGetter;

    private readonly bool _IsMandatory;

    /// <summary>
    /// Creates a skill.
    /// </summary>
    /// <param name="name">Unique skill name (lower case, digits and hyphens).</param>
    /// <param name="description">Short description for the catalog (what and when to use it).</param>
    /// <param name="contentGetter">Returns the current body (evaluated for every prompt; empty = skill is omitted).</param>
    /// <param name="isMandatory">True if the body must always be part of the context (pinned).</param>
    public HarnessSkill(string name, string description, Func<string> contentGetter, bool isMandatory) {

      if (string.IsNullOrWhiteSpace(name)) {
        throw new ArgumentNullException(nameof(name));
      }

      if (contentGetter == null) {
        throw new ArgumentNullException(nameof(contentGetter));
      }

      _Name = name.Trim();
      _Description = description;
      _ContentGetter = contentGetter;
      _IsMandatory = isMandatory;

    }

    /// <summary>
    /// Creates the mandatory skill 'mustread-user-instructions', whose body is provided by the given getter
    /// (usually 'IPromptingSettingsService.GetUserInstructions').
    /// </summary>
    public static HarnessSkill CreateUserInstructionsSkill(Func<string> userInstructionsGetter) {

      return new HarnessSkill(
        UserInstructionsSkillName,
        "Instructions of the user (preferences, conventions, rules). They MUST be read and followed for every task.",
        userInstructionsGetter,
        true
      );
    }

    public string Name {
      get {
        return _Name;
      }
    }

    public string Description {
      get {
        return _Description;
      }
    }

    public bool IsMandatory {
      get {
        return _IsMandatory;
      }
    }

    /// <summary>
    /// Returns the current body of the skill (never null).
    /// </summary>
    public string GetContent() {

      string content = _ContentGetter.Invoke();

      if (content == null) {
        return string.Empty;
      }

      return content.Trim();
    }

  }

}
