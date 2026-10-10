using Dalamud.Configuration;

namespace Harmonia;

// Persisted settings. Property names are the saved JSON keys: renaming one
// drops the user's value.
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>UI language code ("en", "ru").</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Installed translation applied at the next game start, by its folder name
    /// in the pack store; empty means none.
    /// </summary>
    public string ActivePackId { get; set; } = string.Empty;

    /// <summary>
    /// Sheets kept in the game's language: sheet names and folders
    /// ("quest/*"). Applies at the next game start.
    /// </summary>
    public List<string> UntranslatedSheets { get; set; } = [];

    /// <summary>
    /// The player's changes to the sheet groups of the Coverage page: built-in
    /// groups changed or removed, and the player's own groups. Groups only
    /// choose sheets; what is kept is UntranslatedSheets.
    /// </summary>
    public List<Packs.SavedSheetGroup> SheetGroupChanges { get; set; } = [];

    /// <summary>
    /// Plugins (InternalName) whose compatibility profile the player turned
    /// off: the text they look for is translated too. Profiles of installed
    /// plugins are on otherwise. Applies at the next game start.
    /// </summary>
    public List<string> DisabledCompatibility { get; set; } = [];

    /// <summary>
    /// Experimental: other plugins read the translation from the game files
    /// (Game/SharedSheets), so they find the text the game shows without a
    /// compatibility profile. Applies at the next game start.
    /// </summary>
    public bool TranslatePluginData { get; set; }

    /// <summary>
    /// Game languages ("ja", "en", "de", "fr") the name dictionary shows next
    /// to the client language's names. The client language is always shown.
    /// </summary>
    public List<string> DictionaryLanguages { get; set; } = [];

    /// <summary>Feed URLs polled for new releases.</summary>
    public List<string> UpdateFeedUrls { get; set; } = [];

    /// <summary>Install feed releases without asking. Off by default.</summary>
    public bool AutoDownloadUpdates { get; set; }

    /// <summary>Feed poll interval in minutes (clamped 5..1440 by the service).</summary>
    public int UpdateCheckIntervalMinutes { get; set; } = 60;

    /// <summary>Also accept releases from the testing channel.</summary>
    public bool FollowTestingChannel { get; set; }

    public DateTime LastUpdateCheck { get; set; }

    /// <summary>Release version already announced, per feed URL.</summary>
    public Dictionary<string, string> NotifiedFeedVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Game version seen at the last start; a change triggers a feed check.</summary>
    public string LastSeenGameVersion { get; set; } = string.Empty;
}
