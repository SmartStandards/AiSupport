using System;

namespace AI.SmartStandards.InteractivePrompting {

  /// <summary>
  /// Well-known user levels (ROL-1..13), which restrict what a user may do in the chat UI. The restrictions are
  /// enforced within the services, the UI only reflects them. Unknown values (for example 0 = not delivered by an
  /// older server) must be treated as 'Admin' by clients (convention credo: no restriction without information).
  /// </summary>
  public static class PromptingUserLevels {

    /// <summary>
    /// Start, delete, navigate and compress chats, change the own user instructions. Always uses the default
    /// provider, the chat details are read-only.
    /// </summary>
    public const int User = 1;

    /// <summary>
    /// Additionally: switch provider/model of a chat and load/unload (non-hidden) skill and tool sources per chat.
    /// No changes of the defaults, no new sources, no provider-specific tool settings.
    /// </summary>
    public const int PowerUser = 2;

    /// <summary>
    /// No restriction (= the behavior before the user levels were introduced).
    /// </summary>
    public const int Admin = 3;

    /// <summary>
    /// Returns the normalized level (unknown values are treated as 'Admin').
    /// </summary>
    public static int Normalize(int level) {

      if (level == User || level == PowerUser) {
        return level;
      }

      return Admin;
    }

    /// <summary>
    /// Evaluates the given level getter (null = no restriction = 'Admin').
    /// </summary>
    public static int Resolve(Func<int> userLevelGetter) {

      if (userLevelGetter == null) {
        return Admin;
      }

      return Normalize(userLevelGetter.Invoke());
    }

    /// <summary>
    /// Throws an 'UnauthorizedAccessException' if the current level (see 'Resolve') is below the required level.
    /// </summary>
    public static void Demand(Func<int> userLevelGetter, int requiredLevel, string operationName) {

      int currentLevel = Resolve(userLevelGetter);

      if (currentLevel < requiredLevel) {
        throw new UnauthorizedAccessException(
          $"The operation '{operationName}' requires the user level '{GetDisplayName(requiredLevel)}' (current: '{GetDisplayName(currentLevel)}')."
        );
      }

    }

    /// <summary>
    /// Returns a display name of the level.
    /// </summary>
    public static string GetDisplayName(int level) {

      if (level == User) {
        return "User";
      }

      if (level == PowerUser) {
        return "Power user";
      }

      return "Admin";
    }

  }

}
