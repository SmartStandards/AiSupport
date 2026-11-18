using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// ENV-1..5: built-in tool source 'Chat environment', which gives the model access to the chat application itself
  /// (list/read the chat history, switch the UI theme permanently, rename the current chat). All tools run without
  /// confirmation by default. The tools only see the chats of the backend they are bound to, which is already
  /// scoped to the current profile (PRF-11).
  /// </summary>
  public class ChatEnvironmentToolProvider : BuiltInToolProviderBase {

    public const string Id = "oob-chat-environment";

    public const string ListChatsToolName = "chat_list_history";

    public const string ReadChatToolName = "chat_read_history";

    public const string RenameChatToolName = "chat_rename_current";

    private const int _DefaultMaxMessages = 50;

    private readonly IPromptingSettingsService _SettingsService;

    private readonly Func<IPromptingUIBackend> _BackendGetter;

    /// <summary>
    /// Creates the provider.
    /// </summary>
    /// <param name="settingsService">Persists the UI theme.</param>
    /// <param name="backendGetter">Returns the chat backend (resolved lazily, because it is created after the toolbox).</param>
    public ChatEnvironmentToolProvider(IPromptingSettingsService settingsService, Func<IPromptingUIBackend> backendGetter) {

      if (settingsService == null) {
        throw new ArgumentNullException(nameof(settingsService));
      }

      if (backendGetter == null) {
        throw new ArgumentNullException(nameof(backendGetter));
      }

      _SettingsService = settingsService;
      _BackendGetter = backendGetter;

    }

    public override string ProviderId {
      get {
        return Id;
      }
    }

    public override string DisplayName {
      get {
        return "Chat environment";
      }
    }

    public override string DefaultLevel {
      get {
        return ToolSourceLevels.NoConfirmation;
      }
    }

    /// <summary>
    /// Creates the tools 'chat_list_history', 'chat_read_history', 'ui_set_theme' and 'chat_rename_current'.
    /// </summary>
    public override IHarnessTool[] CreateTools(IToolProviderContext context) {

      IHarnessTool listTool = new DelegateHarnessTool(
        ListChatsToolName,
        "Lists the chats of the chat history of the current user (id, name, last activity in UTC), most recent first.",
        "{\"type\":\"object\",\"properties\":{}}",
        (arguments) => this.ListChats()
      );

      string readSchema = (
        "{\"type\":\"object\",\"properties\":{" +
        "\"chatId\":{\"type\":\"string\",\"description\":\"Id of the chat (see chat_list_history)\"}," +
        "\"maxMessages\":{\"type\":\"integer\",\"description\":\"Maximum number of (most recent) messages to return, default 50\"}" +
        "},\"required\":[\"chatId\"]}"
      );

      IHarnessTool readTool = new DelegateHarnessTool(
        ReadChatToolName,
        "Returns the messages (role, author, time, content) of a chat from the chat history of the current user.",
        readSchema,
        (arguments) => this.ReadChat(arguments)
      );

      IHarnessTool themeTool = new SetUiThemeTool(_SettingsService);

      string renameSchema = (
        "{\"type\":\"object\",\"properties\":{" +
        "\"name\":{\"type\":\"string\",\"description\":\"New short and meaningful name of the chat (max. 40 characters)\"}" +
        "},\"required\":[\"name\"]}"
      );

      IHarnessTool renameTool = new DelegateHarnessTool(
        RenameChatToolName,
        "Renames the current chat. It is preferred to call this tool once, immediately after the first prompt of a new chat, so that the chat gets a short meaningful name.",
        renameSchema,
        (arguments) => this.RenameCurrentChat(arguments)
      );

      return new IHarnessTool[] { listTool, readTool, themeTool, renameTool };
    }

    /// <summary>
    /// ENV-1: lists the chats of the backend.
    /// </summary>
    private string ListChats() {

      ChatNameAndId[] chats = this.GetBackend().GetRecentChats();

      JArray result = new JArray();

      foreach (ChatNameAndId chat in chats) {

        JObject entry = new JObject();
        entry["id"] = chat.Id;
        entry["name"] = chat.Name;
        entry["lastActivityUtc"] = chat.LastAction.ToUniversalTime().ToString("o");
        result.Add(entry);

      }

      return result.ToString(Formatting.None);
    }

    /// <summary>
    /// ENV-2: returns the (most recent) messages of a chat.
    /// </summary>
    private string ReadChat(JObject arguments) {

      string chatId = DelegateHarnessTool.GetRequiredString(arguments, "chatId");
      int maxMessages = _DefaultMaxMessages;
      string maxMessagesText = DelegateHarnessTool.GetOptionalString(arguments, "maxMessages");

      if (!string.IsNullOrWhiteSpace(maxMessagesText)) {

        int parsed;

        if (int.TryParse(maxMessagesText, out parsed) && parsed > 0) {
          maxMessages = parsed;
        }

      }

      IPromptingUIBackend backend = this.GetBackend();

      if (!backend.ChatIdExists(chatId)) {
        throw new ArgumentException($"The chat '{chatId}' does not exist.");
      }

      int count = backend.GetChatMessageCount(chatId);
      int startIndex = Math.Max(0, count - maxMessages);
      ChatMessage[] messages = backend.GetChatMessages(chatId, startIndex, count - 1);

      JArray result = new JArray();

      foreach (ChatMessage message in messages.Where((m) => m.Kind != ChatMessageKinds.Activity)) {

        JObject entry = new JObject();
        entry["role"] = message.Role;
        entry["author"] = message.Author;
        entry["timeUtc"] = message.LastAction.ToUniversalTime().ToString("o");
        entry["content"] = message.Content;
        result.Add(entry);

      }

      return result.ToString(Formatting.None);
    }

    /// <summary>
    /// ENV-4: renames the chat of the running turn.
    /// </summary>
    private string RenameCurrentChat(JObject arguments) {

      string name = DelegateHarnessTool.GetRequiredString(arguments, "name").Trim();

      if (name.Length == 0) {
        throw new ArgumentException("The argument 'name' must not be empty.");
      }

      HarnessToolContext context = HarnessToolContext.Current;

      if (context == null || context.Scope == null || string.IsNullOrWhiteSpace(context.Scope.ChatId)) {
        throw new InvalidOperationException("There is no current chat.");
      }

      this.GetBackend().RenameChat(context.Scope.ChatId, name);

      return $"The current chat was renamed to '{name}'.";
    }

    private IPromptingUIBackend GetBackend() {

      IPromptingUIBackend backend = _BackendGetter.Invoke();

      if (backend == null) {
        throw new InvalidOperationException("The chat backend is not available.");
      }

      return backend;
    }

  }

}
