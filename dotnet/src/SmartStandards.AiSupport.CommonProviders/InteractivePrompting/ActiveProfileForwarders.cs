using System;
using System.Threading;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// 'IPromptingUIBackend' which forwards every call to the runtime of the active profile. Calls related to an
  /// existing chat are forwarded to the runtime which owns the chat (running turns survive a profile switch).
  /// </summary>
  public class ActiveProfileUIBackend : IPromptingUIBackend {

    private readonly HarnessProfileManager _Manager;

    public ActiveProfileUIBackend(HarnessProfileManager manager) {

      if (manager == null) {
        throw new ArgumentNullException(nameof(manager));
      }

      _Manager = manager;

    }

    private IPromptingUIBackend Active {
      get {
        return _Manager.ActiveRuntime.Backend;
      }
    }

    private IPromptingUIBackend ForChat(string chatId) {
      return _Manager.GetRuntimeForChat(chatId).Backend;
    }

    public ChatNameAndId[] GetRecentChats() {
      return this.Active.GetRecentChats();
    }

    public int GetChatMessageCount(string chatId) {
      return this.ForChat(chatId).GetChatMessageCount(chatId);
    }

    public ChatMessage[] GetChatMessages(string chatId, int startIndex, int endIndex) {
      return this.ForChat(chatId).GetChatMessages(chatId, startIndex, endIndex);
    }

    public ChatMessage[] GetChatMessagesByTime(string chatId, DateTime from, DateTime to) {
      return this.ForChat(chatId).GetChatMessagesByTime(chatId, from, to);
    }

#if NET_FX
    public ChatMessage SendMessage(string chatId, string message) {
      return this.ForChat(chatId).SendMessage(chatId, message);
    }

    public void CompressChatContext(string chatId) {
      this.ForChat(chatId).CompressChatContext(chatId);
    }
#else
    public ChatMessage SendMessage(CancellationToken cancellationToken, string chatId, string message, Action<string, int, int> onProgress) {
      return this.ForChat(chatId).SendMessage(cancellationToken, chatId, message, onProgress);
    }

    public void CompressChatContext(CancellationToken cancellationToken, string chatId) {
      this.ForChat(chatId).CompressChatContext(cancellationToken, chatId);
    }
#endif

    public void DeleteChat(string chatId) {
      this.ForChat(chatId).DeleteChat(chatId);
    }

    public bool ChatIdExists(string chatId) {
      return this.Active.ChatIdExists(chatId);
    }

    public string BeginNewChat(string name) {
      return this.Active.BeginNewChat(name);
    }

    public void RenameChat(string chatId, string newName) {
      this.ForChat(chatId).RenameChat(chatId, newName);
    }

    public ChatDetails GetChatDetails(string chatId) {
      return this.ForChat(chatId).GetChatDetails(chatId);
    }

    public void SetChatProvider(string chatId, string providerName, string model) {
      this.ForChat(chatId).SetChatProvider(chatId, providerName, model);
    }

    public void RaiseChatSkillSourceLevel(string chatId, string sourceId, string level) {
      this.ForChat(chatId).RaiseChatSkillSourceLevel(chatId, sourceId, level);
    }

    public void SetChatToolSourceLevel(string chatId, string sourceId, string level) {
      this.ForChat(chatId).SetChatToolSourceLevel(chatId, sourceId, level);
    }

    public PendingToolApproval[] GetPendingToolApprovals(string chatId) {
      return this.ForChat(chatId).GetPendingToolApprovals(chatId);
    }

    public void AnswerToolApproval(string chatId, string approvalId, string decision) {
      this.ForChat(chatId).AnswerToolApproval(chatId, approvalId, decision);
    }

    public ToolAuditEntry[] GetToolAuditLog(string chatId) {
      return this.ForChat(chatId).GetToolAuditLog(chatId);
    }

  }

  /// <summary>
  /// 'IPromptingSettingsService' which forwards every call to the runtime of the active profile.
  /// The UI theme is shared by all profiles (stored within the default profile, PRF-2); the user instructions
  /// are shared anyway (one 'global-user-preferences' file).
  /// </summary>
  public class ActiveProfileSettingsService : IPromptingSettingsService {

    private readonly HarnessProfileManager _Manager;

    public ActiveProfileSettingsService(HarnessProfileManager manager) {

      if (manager == null) {
        throw new ArgumentNullException(nameof(manager));
      }

      _Manager = manager;

    }

    private IPromptingSettingsService Active {
      get {
        return _Manager.ActiveRuntime.SettingsService;
      }
    }

    public LLMProviderInfo[] GetAvailableProviders() {
      return this.Active.GetAvailableProviders();
    }

    public PromptingSettingsInfo GetSettings() {
      return this.Active.GetSettings();
    }

    public void SaveSettings(string providerName, string model, string apiKey) {
      this.Active.SaveSettings(providerName, model, apiKey);
    }

    public void DeleteApiKey(string providerName) {
      this.Active.DeleteApiKey(providerName);
    }

    public string[] GetDisabledCapabilities(string providerName) {
      return this.Active.GetDisabledCapabilities(providerName);
    }

    public void SaveDisabledCapabilities(string providerName, string[] disabledCapabilityNames) {
      this.Active.SaveDisabledCapabilities(providerName, disabledCapabilityNames);
    }

    public string GetUserInstructions() {
      return this.Active.GetUserInstructions();
    }

    public void SaveUserInstructions(string userInstructions) {
      this.Active.SaveUserInstructions(userInstructions);
    }

    public string GetUiTheme() {
      return _Manager.GetSharedSettingsService().GetUiTheme();
    }

    public void SaveUiTheme(string themeName) {
      _Manager.GetSharedSettingsService().SaveUiTheme(themeName);
    }

  }

  /// <summary>
  /// 'IPromptingExtensionService' which forwards every call to the runtime of the active profile.
  /// </summary>
  public class ActiveProfileExtensionService : IPromptingExtensionService {

    private readonly HarnessProfileManager _Manager;

    public ActiveProfileExtensionService(HarnessProfileManager manager) {

      if (manager == null) {
        throw new ArgumentNullException(nameof(manager));
      }

      _Manager = manager;

    }

    private IPromptingExtensionService Active {
      get {
        return _Manager.ActiveRuntime.ExtensionService;
      }
    }

    public SkillSourceInfo[] GetSkillSources() {
      return this.Active.GetSkillSources();
    }

    public string AddSkillFileReference(string filePath) {
      return this.Active.AddSkillFileReference(filePath);
    }

    public string AddSkillSource(string kind, string location, string credential) {
      return this.Active.AddSkillSource(kind, location, credential);
    }

    public void RemoveSkillSource(string sourceId) {
      this.Active.RemoveSkillSource(sourceId);
    }

    public void SetSkillSourceLevel(string sourceId, string level) {
      this.Active.SetSkillSourceLevel(sourceId, level);
    }

    public void SetSystemPreferencesLocation(string location) {
      this.Active.SetSystemPreferencesLocation(location);
    }

    public string GetSystemPreferencesLocation() {
      return this.Active.GetSystemPreferencesLocation();
    }

    public void SetSkillSourceSecureAvailability(string sourceId, bool enabled) {
      this.Active.SetSkillSourceSecureAvailability(sourceId, enabled);
    }

    public void SetSkillSourceCredential(string sourceId, string credential) {
      this.Active.SetSkillSourceCredential(sourceId, credential);
    }

    public void SetSkillSourceDisplayName(string sourceId, string displayName) {
      this.Active.SetSkillSourceDisplayName(sourceId, displayName);
    }

    public FileSystemEntryInfo[] BrowseFileSystem(string path, bool includeFiles) {
      return this.Active.BrowseFileSystem(path, includeFiles);
    }

    public ToolSourceInfo[] GetToolSources() {
      return this.Active.GetToolSources();
    }

    public string AddMcpServer(string url, string token) {
      return this.Active.AddMcpServer(url, token);
    }

    public ContractInterfaceInfo[] UploadToolAssembly(string fileName, string contentBase64) {
      return this.Active.UploadToolAssembly(fileName, contentBase64);
    }

    public ContractInterfaceInfo[] AddToolAssemblyReference(string fullPath) {
      return this.Active.AddToolAssemblyReference(fullPath);
    }

    public FileSystemEntryInfo[] BrowseFileSystemFiltered(string path, string filePattern) {
      return this.Active.BrowseFileSystemFiltered(path, filePattern);
    }

    public string RegisterContractInterface(string assemblyId, string interfaceName, string url, string token) {
      return this.Active.RegisterContractInterface(assemblyId, interfaceName, url, token);
    }

    public void RemoveToolSource(string sourceId) {
      this.Active.RemoveToolSource(sourceId);
    }

    public void SetToolSourceLevel(string sourceId, string level) {
      this.Active.SetToolSourceLevel(sourceId, level);
    }

    public void SetToolSourceCredential(string sourceId, string credential) {
      this.Active.SetToolSourceCredential(sourceId, credential);
    }

    public void SetToolSourceLocation(string sourceId, string url) {
      this.Active.SetToolSourceLocation(sourceId, url);
    }

    public void SetToolSourceAdditionalDescription(string sourceId, string additionalDescription) {
      this.Active.SetToolSourceAdditionalDescription(sourceId, additionalDescription);
    }

    public string GetToolProviderSettingsUi(string sourceId) {
      return this.Active.GetToolProviderSettingsUi(sourceId);
    }

    public string GetToolProviderSettings(string sourceId) {
      return this.Active.GetToolProviderSettings(sourceId);
    }

    public void SaveToolProviderSettings(string sourceId, string settingsJson) {
      this.Active.SaveToolProviderSettings(sourceId, settingsJson);
    }

  }

}
