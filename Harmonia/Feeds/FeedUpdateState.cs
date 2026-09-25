namespace Harmonia.Feeds;

public enum FeedPackStatus
{
    Unknown,
    Checking,
    UpToDate,
    UpdateAvailable,

    // The release is signed by a key not yet trusted for this pack id.
    NeedsTrust,
    Downloading,
    Incompatible,
    Error,
}

public sealed class FeedStatus
{
    public string Url { get; init; } = string.Empty;

    public string? PackId { get; set; }

    public string? Title { get; set; }

    // Fingerprint the user is asked to trust (feed hint before download,
    // the pack's actual key after it).
    public string? TrustFingerprint { get; set; }

    public string? RemoteVersion { get; set; }

    public string? InstalledVersion { get; set; }

    public string? Changelog { get; set; }

    public FeedPackStatus Status { get; set; } = FeedPackStatus.Unknown;

    public string? Error { get; set; }

    public float Progress { get; set; }
}

// Runtime update status: background checker writes, UI thread reads, so
// reference swaps (atomic) and volatile flags are sufficient.
public sealed class FeedUpdateState
{
    private volatile bool checking;
    private IReadOnlyList<FeedStatus> feeds = [];

    public bool Checking
    {
        get => checking;
        set => checking = value;
    }

    public IReadOnlyList<FeedStatus> Feeds
    {
        get => feeds;
        set => feeds = value ?? [];
    }

    public DateTime? LastCheck { get; set; }

    public int AvailableCount => feeds.Count(static f => f.Status is FeedPackStatus.UpdateAvailable or FeedPackStatus.NeedsTrust);

    public FeedStatus? UpdateForPack(string packId) =>
        feeds.FirstOrDefault(f =>
            f.Status is FeedPackStatus.UpdateAvailable or FeedPackStatus.NeedsTrust &&
            string.Equals(f.PackId, packId, StringComparison.Ordinal));
}
