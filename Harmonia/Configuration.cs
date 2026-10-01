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

    /// <summary>Game process id of the session in which the plugin last loaded.</summary>
    public int SessionPid { get; set; }

    /// <summary>
    /// Installed translation applied at the next game start, by its folder name
    /// in the pack store; empty means none.
    /// </summary>
    public string ActivePackId { get; set; } = string.Empty;

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
