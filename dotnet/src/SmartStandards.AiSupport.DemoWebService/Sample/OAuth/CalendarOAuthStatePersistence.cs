using AI.SmartStandards.DemoWebService.Sample.Calendar;
using Logging.SmartStandards;
using Microsoft.AspNetCore.DataProtection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace AI.SmartStandards.DemoWebService.Sample.OAuth {

  /// <summary>
  /// Persists the OAuth server state (access grants, refresh grants, registered clients) of the calendar demo
  /// into ONE encrypted file, so that logins of MCP clients (ChatGPT, Claude, browser login) survive a restart.
  /// The content contains the CalDAV credentials of the subjects and is therefore ALWAYS encrypted via
  /// ASP.NET Core Data Protection (key ring in the subfolder 'keys' - on Windows additionally protected via DPAPI).
  /// Each component owns a named section and serializes it itself (see 'Save').
  /// </summary>
  public sealed class CalendarOAuthStatePersistence {

    private const string _FileName = "oauth-state.protected";
    private const string _KeyFolderName = "keys";
    private const string _ProtectorPurpose = "AI.SmartStandards.DemoWebService.Sample.OAuth.CalendarOAuthState.v1";

    private readonly string _FilePath;
    private readonly IDataProtector _Protector;
    private readonly object _SyncRoot = new object();
    private readonly JObject _Sections;

    /// <summary>
    /// Creates the persistence for the given directory (created if missing) and loads the existing state.
    /// An unreadable state file (e.g. lost key ring) is ignored - the clients then simply have to log in again.
    /// </summary>
    public CalendarOAuthStatePersistence(string directory) {
      if (string.IsNullOrWhiteSpace(directory)) {
        throw new ArgumentException("The directory must not be empty.", nameof(directory));
      }
      string fullDirectory = Path.GetFullPath(directory);
      Directory.CreateDirectory(fullDirectory);
      _FilePath = Path.Combine(fullDirectory, _FileName);
      IDataProtectionProvider provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(fullDirectory, _KeyFolderName)));
      _Protector = provider.CreateProtector(_ProtectorPurpose);
      _Sections = this.ReadFile();
    }

    /// <summary>
    /// The full path of the (encrypted) state file.
    /// </summary>
    public string FilePath {
      get {
        return _FilePath;
      }
    }

    /// <summary>
    /// Returns a copy of the stored section content or null if there is none.
    /// </summary>
    public JToken Load(string sectionName) {
      lock (_SyncRoot) {
        JToken section = _Sections[sectionName];
        if (section == null) {
          return null;
        }
        return section.DeepClone();
      }
    }

    /// <summary>
    /// Replaces the section with the content created by 'createContent' and writes the file.
    /// 'createContent' is invoked INSIDE the lock, so concurrent snapshots can never overwrite newer ones.
    /// </summary>
    public void Save(string sectionName, Func<JToken> createContent) {
      lock (_SyncRoot) {
        _Sections[sectionName] = createContent.Invoke();
        this.WriteFile();
      }
    }

    private JObject ReadFile() {
      if (!File.Exists(_FilePath)) {
        return new JObject();
      }
      try {
        string protectedContent = File.ReadAllText(_FilePath);
        string json = _Protector.Unprotect(protectedContent);
        return JObject.Parse(json);
      }
      catch (CryptographicException ex) {
        DevLogger.LogError(ex);
        return new JObject();
      }
      catch (JsonException ex) {
        DevLogger.LogError(ex);
        return new JObject();
      }
    }

    private void WriteFile() {
      string json = _Sections.ToString(Formatting.None);
      string protectedContent = _Protector.Protect(json);
      string tempFilePath = _FilePath + ".tmp";
      File.WriteAllText(tempFilePath, protectedContent);
      File.Move(tempFilePath, _FilePath, true);
    }

    #region " Serialization Helpers "

    /// <summary>
    /// Converts a UTC timestamp into unix milliseconds (avoids any local-time conversion of JSON date parsing).
    /// </summary>
    public static long ToUnixMilliseconds(DateTime utcValue) {
      DateTime utc = DateTime.SpecifyKind(utcValue, DateTimeKind.Utc);
      return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Converts unix milliseconds back into a UTC timestamp.
    /// </summary>
    public static DateTime FromUnixMilliseconds(JToken value) {
      return DateTimeOffset.FromUnixTimeMilliseconds(value.Value<long>()).UtcDateTime;
    }

    /// <summary>
    /// Converts a string array into a JSON array (null becomes an empty array).
    /// </summary>
    public static JArray ToJArray(string[] values) {
      JArray array = new JArray();
      if (values != null) {
        foreach (string value in values) {
          array.Add(value);
        }
      }
      return array;
    }

    /// <summary>
    /// Converts a JSON array into a string array (null becomes an empty array).
    /// </summary>
    public static string[] ToStringArray(JToken token) {
      List<string> values = new List<string>();
      JArray array = token as JArray;
      if (array != null) {
        foreach (JToken item in array) {
          values.Add(item.ToString());
        }
      }
      return values.ToArray();
    }

    /// <summary>
    /// Writes the credentials into the given (encrypted persisted) object.
    /// </summary>
    public static void WriteCredentials(JObject target, CalDavCredentials credentials) {
      target["user"] = credentials.UserName;
      target["pass"] = credentials.Password;
    }

    /// <summary>
    /// Reads the credentials from the given object.
    /// </summary>
    public static CalDavCredentials ReadCredentials(JObject source) {
      return new CalDavCredentials(source.Value<string>("user"), source.Value<string>("pass"));
    }

    #endregion

  }

}
