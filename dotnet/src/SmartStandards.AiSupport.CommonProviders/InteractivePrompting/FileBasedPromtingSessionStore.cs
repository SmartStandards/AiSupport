using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Persists every chat session as one JSON file ('{sessionId}.json') within a directory.
  /// </summary>
  public class FileBasedPromptingSessionStore : IPromptingSessionStore {

    private readonly string _RootDirectory;

    private readonly object _SyncRoot = new object();

    /// <summary>
    /// Creates a store which persists into the given directory (it is created on demand).
    /// </summary>
    public FileBasedPromptingSessionStore(string rootDirectory) {

      if (string.IsNullOrWhiteSpace(rootDirectory)) {
        throw new ArgumentNullException(nameof(rootDirectory));
      }

      _RootDirectory = rootDirectory;

    }

    public string RootDirectory {
      get {
        return _RootDirectory;
      }
    }

    /// <summary>
    /// Returns the header information (id, name, last action) of all stored sessions (most recent first).
    /// </summary>
    public ChatNameAndId[] GetSessionHeaders() {

      List<ChatNameAndId> headers = new List<ChatNameAndId>();

      lock (_SyncRoot) {

        if (!Directory.Exists(_RootDirectory)) {
          return headers.ToArray();
        }

        foreach (string fileFullName in Directory.GetFiles(_RootDirectory, "*.json")) {

          ChatSession session = this.ReadSessionFile(fileFullName);

          if (session != null) {
            ChatNameAndId header = new ChatNameAndId();
            header.Id = session.Id;
            header.Name = session.Name;
            header.LastAction = session.LastAction;
            headers.Add(header);
          }

        }

      }

      return headers.OrderByDescending((h) => h.LastAction).ToArray();
    }

    /// <summary>
    /// Returns true if a session with the given id exists.
    /// </summary>
    public bool SessionExists(string sessionId) {

      if (!IsValidSessionId(sessionId)) {
        return false;
      }

      lock (_SyncRoot) {
        return File.Exists(this.GetFileFullName(sessionId));
      }

    }

    /// <summary>
    /// Loads the given session (or returns null if it does not exist).
    /// </summary>
    public ChatSession LoadSession(string sessionId) {

      if (!IsValidSessionId(sessionId)) {
        return null;
      }

      lock (_SyncRoot) {

        string fileFullName = this.GetFileFullName(sessionId);

        if (!File.Exists(fileFullName)) {
          return null;
        }

        return this.ReadSessionFile(fileFullName);
      }

    }

    /// <summary>
    /// Creates or overwrites the given session.
    /// </summary>
    public void SaveSession(ChatSession session) {

      if (session == null) {
        throw new ArgumentNullException(nameof(session));
      }

      if (!IsValidSessionId(session.Id)) {
        throw new ArgumentException($"Invalid session id '{session.Id}'.", nameof(session));
      }

      if (session.Messages == null) {
        session.Messages = new ChatMessage[0];
      }

      string json = JsonConvert.SerializeObject(session, Formatting.Indented);

      lock (_SyncRoot) {

        Directory.CreateDirectory(_RootDirectory);

        string fileFullName = this.GetFileFullName(session.Id);
        string tempFileFullName = fileFullName + ".tmp";

        //write into a temp file first, to avoid corrupted sessions on crashes
        File.WriteAllText(tempFileFullName, json);

        if (File.Exists(fileFullName)) {
          File.Replace(tempFileFullName, fileFullName, null);
        }
        else {
          File.Move(tempFileFullName, fileFullName);
        }

      }

    }

    /// <summary>
    /// Deletes the given session (does nothing if it does not exist).
    /// </summary>
    public void DeleteSession(string sessionId) {

      if (!IsValidSessionId(sessionId)) {
        return;
      }

      lock (_SyncRoot) {

        string fileFullName = this.GetFileFullName(sessionId);

        if (File.Exists(fileFullName)) {
          File.Delete(fileFullName);
        }

      }

    }

    /// <summary>
    /// Only alphanumeric characters, '-' and '_' are allowed (prevents path injection).
    /// </summary>
    private static bool IsValidSessionId(string sessionId) {

      if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 100) {
        return false;
      }

      foreach (char c in sessionId) {
        if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') {
          return false;
        }
      }

      return true;
    }

    private string GetFileFullName(string sessionId) {
      return Path.Combine(_RootDirectory, sessionId + ".json");
    }

    private ChatSession ReadSessionFile(string fileFullName) {

      string json = File.ReadAllText(fileFullName);
      ChatSession session = JsonConvert.DeserializeObject<ChatSession>(json);

      if (session != null && session.Messages == null) {
        session.Messages = new ChatMessage[0];
      }

      return session;
    }

  }

}
