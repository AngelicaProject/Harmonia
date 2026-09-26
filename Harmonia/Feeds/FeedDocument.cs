using System.Text.RegularExpressions;
using Harmonia.Packs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Harmonia.Feeds;

public sealed record FeedDownload(string Url, bool Brotli, long Size, string Sha256, long UnpackedSize);

public sealed record FeedRelease(
    long Sequence,
    string Version,
    string Channel,
    string PackHash,
    string SourceLanguage,
    string SourceGameVersion,
    Version MinHarmonia,
    FeedDownload Download,
    string? Changelog);

// Feed format v1 (Aeria docs/formats/feed-v1.md). The feed is only a hint:
// every fact used for a decision is re-checked against the downloaded pack.
// Unknown fields are ignored by contract.
public sealed partial class FeedDocument
{
    public const long MaxFeedBytes = 1 << 20;

    public string PackId { get; private init; } = string.Empty;
    public string? Title { get; private init; }
    public string? PublisherKeyFingerprint { get; private init; }
    public IReadOnlyList<FeedRelease> Releases { get; private init; } = [];

    public static FeedDocument Parse(string json)
    {
        JObject root;
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(reader);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Feed is not valid JSON: " + ex.Message);
        }

        if ((string?)root["format"] != "harmonia-feed" || root["version"]?.Type != JTokenType.Integer)
            throw new FormatException("Not a Harmonia feed.");
        if ((long)root["version"]! != 1)
            throw new FormatException("Unsupported feed version " + root["version"] + ".");

        var packId = Str(root, "packId");
        if (!TranslationPackStore.IsValidPackId(packId))
            throw new FormatException("Invalid packId.");

        var fingerprint = OptStr(root, "publisherKeyFingerprint");
        if (fingerprint is not null && !Sha256Hex().IsMatch(fingerprint))
            throw new FormatException("Invalid publisherKeyFingerprint.");

        if (root["releases"] is not JArray releases)
            throw new FormatException("Feed has no releases array.");

        return new FeedDocument
        {
            PackId = packId,
            Title = OptStr(root, "title"),
            PublisherKeyFingerprint = fingerprint,
            Releases = releases.Select(ParseRelease).ToArray(),
        };
    }

    private static FeedRelease ParseRelease(JToken token)
    {
        if (token is not JObject release)
            throw new FormatException("Feed release must be an object.");

        var source = release["source"] as JObject ?? throw new FormatException("Release has no source.");
        var download = release["download"] as JObject ?? throw new FormatException("Release has no download.");

        var channel = Str(release, "channel");
        if (channel is not ("stable" or "testing"))
            throw new FormatException("Invalid release channel.");

        var packHash = Str(release, "packHash");
        if (!packHash.StartsWith("sha256:", StringComparison.Ordinal) || !Sha256Hex().IsMatch(packHash[7..]))
            throw new FormatException("Invalid packHash.");

        if (!System.Version.TryParse(Str(release, "minHarmonia"), out var minHarmonia))
            throw new FormatException("Invalid minHarmonia.");

        var url = Str(download, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException("Release download must be an https URL.");

        var encoding = Str(download, "encoding");
        if (encoding is not ("br" or "identity"))
            throw new FormatException("Unsupported download encoding.");

        var sha = Str(download, "sha256");
        if (!Sha256Hex().IsMatch(sha))
            throw new FormatException("Invalid download sha256.");

        var sequence = Int(release, "sequence");
        if (sequence < 1)
            throw new FormatException("Invalid release sequence.");

        return new FeedRelease(
            sequence,
            Str(release, "version"),
            channel,
            packHash,
            Str(source, "language"),
            Str(source, "gameVersion"),
            minHarmonia,
            new FeedDownload(url, encoding == "br", Int(download, "size"), sha, Int(download, "unpackedSize")),
            OptStr(release, "changelog"));
    }

    private static string Str(JObject obj, string field)
    {
        if (obj[field] is not JValue { Type: JTokenType.String } value || string.IsNullOrWhiteSpace((string?)value))
            throw new FormatException($"Feed field '{field}' must be a non-empty string.");

        return ((string)value!).Trim();
    }

    private static string? OptStr(JObject obj, string field)
    {
        var token = obj[field];
        return token is null || token.Type == JTokenType.Null ? null : Str(obj, field);
    }

    private static long Int(JObject obj, string field)
    {
        if (obj[field] is not JValue { Type: JTokenType.Integer, Value: long number } || number < 0)
            throw new FormatException($"Feed field '{field}' must be a non-negative integer.");

        return number;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}

public static class FeedReleaseSelector
{
    // Best release for this client (feed-v1.md, "Release selection").
    public static FeedRelease? SelectBest(
        FeedDocument feed,
        string? clientLanguage,
        string? gameVersion,
        string? pluginVersion,
        bool followTesting)
    {
        return feed.Releases
            .Where(r => followTesting || r.Channel == "stable")
            .Where(r => PackCompatibility.IsPluginSupported(r.MinHarmonia, pluginVersion))
            .Where(r => string.IsNullOrWhiteSpace(clientLanguage) ||
                string.Equals(r.SourceLanguage, clientLanguage, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => PackCompatibility.GameVersionMatches(r.SourceGameVersion, gameVersion) == true)
            .ThenByDescending(static r => r.Sequence)
            .FirstOrDefault();
    }

    // Whether the selected release should replace the installed pack.
    public static bool IsUpgrade(FeedRelease release, TranslationPack? installed, string? gameVersion)
    {
        var manifest = installed?.Manifest;
        if (manifest is null)
            return true;
        if (release.Sequence > manifest.Sequence)
            return true;

        var releaseMatches = PackCompatibility.GameVersionMatches(release.SourceGameVersion, gameVersion) == true;
        var installedMatches = PackCompatibility.GameVersionMatches(manifest, gameVersion) == true;
        return releaseMatches && !installedMatches && release.PackHash != installed!.PackHash;
    }
}
