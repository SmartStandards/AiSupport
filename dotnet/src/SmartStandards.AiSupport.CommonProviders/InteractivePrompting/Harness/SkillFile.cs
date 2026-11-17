using System;
using System.IO;
using System.Text;

namespace AI.SmartStandards.InteractivePrompting.Harness {

  /// <summary>
  /// Reads and writes skill files in the Agent Skills format: an optional YAML front matter
  /// ('---' / 'name: ...' / 'description: ...' / '---') followed by the markdown body.
  /// </summary>
  public class SkillFile {

    /// <summary>
    /// Conventional file name of a skill within its folder.
    /// </summary>
    public const string SkillFileName = "SKILL.md";

    private string _Name = string.Empty;

    private string _Description = string.Empty;

    private string _Body = string.Empty;

    public string Name {
      get {
        return _Name;
      }
      set {
        _Name = value;
      }
    }

    public string Description {
      get {
        return _Description;
      }
      set {
        _Description = value;
      }
    }

    public string Body {
      get {
        return _Body;
      }
      set {
        _Body = value;
      }
    }

    /// <summary>
    /// Reads and parses the given skill file. If the front matter does not provide a name, it is derived
    /// from the folder name ('SKILL.md') or from the file name.
    /// </summary>
    public static SkillFile Load(string fileFullName) {

      string text = File.ReadAllText(fileFullName);

      SkillFile skillFile = Parse(text);

      if (string.IsNullOrWhiteSpace(skillFile.Name)) {
        skillFile.Name = DeriveNameFromPath(fileFullName);
      }

      return skillFile;
    }

    /// <summary>
    /// Parses the content of a skill file (front matter is optional).
    /// </summary>
    public static SkillFile Parse(string text) {

      SkillFile skillFile = new SkillFile();

      if (text == null) {
        return skillFile;
      }

      string normalized = text.Replace("\r\n", "\n").TrimStart('\uFEFF');

      if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) {
        skillFile.Body = normalized.Trim();
        return skillFile;
      }

      int endIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);

      if (endIndex < 0) {
        skillFile.Body = normalized.Trim();
        return skillFile;
      }

      string frontMatter = normalized.Substring(4, endIndex - 4);
      int bodyStart = normalized.IndexOf('\n', endIndex + 4);

      if (bodyStart < 0) {
        skillFile.Body = string.Empty;
      }
      else {
        skillFile.Body = normalized.Substring(bodyStart + 1).Trim();
      }

      foreach (string line in frontMatter.Split('\n')) {

        int colonIndex = line.IndexOf(':');

        if (colonIndex <= 0) {
          continue;
        }

        string key = line.Substring(0, colonIndex).Trim();
        string value = Unquote(line.Substring(colonIndex + 1).Trim());

        if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)) {
          skillFile.Name = value;
        }
        else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase)) {
          skillFile.Description = value;
        }

      }

      return skillFile;
    }

    /// <summary>
    /// Renders the skill file content (front matter + body).
    /// </summary>
    public string Compose() {

      StringBuilder builder = new StringBuilder();

      builder.Append("---\n");
      builder.Append("name: ").Append(_Name).Append('\n');
      builder.Append("description: ").Append(_Description.Replace("\n", " ")).Append('\n');
      builder.Append("---\n\n");
      builder.Append(_Body);
      builder.Append('\n');

      return builder.ToString();
    }

    /// <summary>
    /// Writes the skill file (the directory is created if necessary).
    /// </summary>
    public void Save(string fileFullName) {

      string directory = Path.GetDirectoryName(fileFullName);

      if (!string.IsNullOrWhiteSpace(directory)) {
        Directory.CreateDirectory(directory);
      }

      File.WriteAllText(fileFullName, this.Compose());

    }

    /// <summary>
    /// Derives a skill name from a path: the folder name for 'SKILL.md', otherwise the file name without extension.
    /// </summary>
    public static string DeriveNameFromPath(string fileFullName) {

      string fileName = Path.GetFileName(fileFullName);

      if (string.Equals(fileName, SkillFileName, StringComparison.OrdinalIgnoreCase)) {

        string folderName = Path.GetFileName(Path.GetDirectoryName(fileFullName));

        if (!string.IsNullOrWhiteSpace(folderName)) {
          return folderName;
        }

      }

      return Path.GetFileNameWithoutExtension(fileFullName);
    }

    private static string Unquote(string value) {

      if (value.Length >= 2) {

        bool doubleQuoted = value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal);
        bool singleQuoted = value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal);

        if (doubleQuoted || singleQuoted) {
          return value.Substring(1, value.Length - 2);
        }

      }

      return value;
    }

  }

}
