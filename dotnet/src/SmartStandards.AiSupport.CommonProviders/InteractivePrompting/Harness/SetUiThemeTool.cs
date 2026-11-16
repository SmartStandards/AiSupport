using Newtonsoft.Json.Linq;
using System;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Internal test tool which allows the model to switch the chat UI between light and dark mode.
  /// It is mainly intended to test the tool handshake of the harness end-to-end (the effect is immediately visible).
  /// </summary>
  public class SetUiThemeTool : IHarnessTool {

    public const string ToolName = "ui_set_theme";

    private readonly IPromptingSettingsService _SettingsService;

    /// <summary>
    /// Creates the tool. The theme is persisted via the given settings service (the UI reloads it after each answer).
    /// </summary>
    public SetUiThemeTool(IPromptingSettingsService settingsService) {

      if (settingsService == null) {
        throw new ArgumentNullException(nameof(settingsService));
      }

      _SettingsService = settingsService;

    }

    public string Name {
      get {
        return ToolName;
      }
    }

    public string Description {
      get {
        return "Switches the chat UI of the user between light mode and dark mode. Use it when the user asks to change the theme/appearance.";
      }
    }

    public string ParametersJsonSchema {
      get {
        return (
          "{\"type\":\"object\",\"properties\":{\"theme\":{\"type\":\"string\",\"enum\":[\"light\",\"dark\"]," +
          "\"description\":\"The theme to activate.\"}},\"required\":[\"theme\"]}"
        );
      }
    }

    /// <summary>
    /// Validates the 'theme' argument and persists it.
    /// </summary>
    public string Invoke(JObject arguments) {

      string requestedTheme = null;

      if (arguments != null) {

        JToken token = arguments["theme"];

        if (token != null && token.Type == JTokenType.String) {
          requestedTheme = token.Value<string>().Trim().ToLowerInvariant();
        }

      }

      if (requestedTheme != UiThemeNames.Light && requestedTheme != UiThemeNames.Dark) {
        throw new ArgumentException("The argument 'theme' must be 'light' or 'dark'.");
      }

      string previousTheme = _SettingsService.GetUiTheme();
      _SettingsService.SaveUiTheme(requestedTheme);

      return "The UI theme was switched from '" + previousTheme + "' to '" + requestedTheme + "'.";
    }

  }

}
