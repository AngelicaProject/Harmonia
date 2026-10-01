using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Harmonia.Packs.Hpk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Harmonia.Packs;

// Installed translations live in <data>/packs/<id>/<hash>.hpk (<data> is the
// plugin configuration directory), where
// <id> is a name Harmonia gives the translation when it is first installed
// (packs carry no identifier). The translation's installed.json names the
// current file, the signing key it trusts, and the feed it updates from. Only
// the active pack is fully verified at load; listing uses metadata because
// every file was fully verified when it was installed.
public sealed partial class TranslationPackStore
{
    public const string PacksDirName = "packs";
    public const string InstalledFileName = "installed.json";
    public const string PackExtension = ".hpk";
    private const string StagingDirName = ".staging";
    private const int InstallRecordVersion = 2;

    private readonly string resourcesDir;
    private readonly IHarmoniaLog log;
    private readonly IGameVersionProvider gameVersion;
    private readonly string? pluginVersion;
    private DateTime lastVersionAttempt = DateTime.MinValue;

    public TranslationPackStore(
        string resourcesDir,
        IHarmoniaLog log,
        IGameVersionProvider? gameVersion = null,
        string? pluginVersion = null,
        string? clientLanguage = null)
    {
        this.resourcesDir = resourcesDir ?? string.Empty;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.gameVersion = gameVersion ?? new FuncGameVersionProvider(static () => null);
        this.pluginVersion = pluginVersion;
        ClientLanguage = string.IsNullOrWhiteSpace(clientLanguage) ? null : clientLanguage.Trim();

        try
        {
            Directory.CreateDirectory(PacksDir);
        }
        catch
        {
        }

        Rescan();
    }

    public string ResourcesDir => resourcesDir;

    public string PacksDir => Path.Combine(resourcesDir, PacksDirName);

    public string StagingDir => Path.Combine(PacksDir, StagingDirName);

    public IReadOnlyList<TranslationPack> Packs { get; private set; } = [];

    public string? CurrentGameVersion { get; private set; }

    public string? ClientLanguage { get; }

    public string? PluginVersion => pluginVersion;

    public PackEnvironment Environment => new(ClientLanguage, pluginVersion, CurrentGameVersion);

    public static bool IsValidPackId(string? id) => id is not null && PackIdPattern().IsMatch(id);

    // The translation that updates from this feed.
    public TranslationPack? FindByFeed(string? url) =>
        string.IsNullOrWhiteSpace(url)
            ? null
            : Packs.FirstOrDefault(p => string.Equals(p.FeedUrl, url.Trim(), StringComparison.OrdinalIgnoreCase));

    // The translation that trusts the key of this signature, or the key it
    // endorsed.
    public TranslationPack? FindByKey(HpkSignature signature) =>
        Packs.FirstOrDefault(p => p.PinnedKey is { } pinned &&
            (string.Equals(pinned, signature.Fingerprint, StringComparison.Ordinal) ||
             string.Equals(pinned, signature.PreviousFingerprint, StringComparison.Ordinal)));

    // An unsigned translation with the same title and team: an unsigned file
    // replaces it rather than piling up beside it.
    public TranslationPack? FindUnsigned(HpkManifest manifest) =>
        Packs.FirstOrDefault(p => p.PinnedKey is null && p.Manifest is { } m &&
            string.Equals(m.Title, manifest.Title, StringComparison.Ordinal) &&
            string.Equals(m.TeamName, manifest.TeamName, StringComparison.Ordinal));

    // The game client may not expose its version yet when the plugin loads
    // first, so keep retrying (throttled) until it is known.
    public void RefreshGameVersion()
    {
        if (CurrentGameVersion is not null)
            return;

        var now = DateTime.UtcNow;
        if ((now - lastVersionAttempt).TotalSeconds < 5)
            return;

        lastVersionAttempt = now;
        var current = ReadGameVersion();
        if (current is null)
            return;

        log.Info("Detected game version " + current + ".");
        Rescan();
    }

    public TranslationPack? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return Packs.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.Ordinal));
    }

    public string? ResolveLoadedPackId(string? activePackId)
    {
        var pack = TryGet(activePackId);
        return pack is not null && pack.IsSelectable ? pack.Id : null;
    }

    public void Rescan()
    {
        CurrentGameVersion = ReadGameVersion();

        var packs = new List<TranslationPack>();
        try
        {
            if (!string.IsNullOrEmpty(resourcesDir) && Directory.Exists(PacksDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(PacksDir).Order(StringComparer.Ordinal))
                {
                    if (Path.GetFileName(dir) == StagingDirName)
                        continue;

                    packs.Add(LoadPack(dir));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning("Failed to scan translation packs.", ex);
        }

        Packs = packs;
    }

    // Startup only: nothing is mapped yet, so files replaced by an update
    // during the previous session and abandoned staging files can go.
    public void RemoveStaleFiles()
    {
        TryDeleteDirectory(StagingDir);
        foreach (var pack in Packs)
        {
            if (pack.FilePath is null)
                continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(pack.PackDirectory, "*" + PackExtension))
                {
                    if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(pack.FilePath), StringComparison.OrdinalIgnoreCase))
                        File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                log.Warning("Failed to remove old files of pack '" + pack.Id + "'.", ex);
            }
        }
    }

    // Opens the active pack with full verification for runtime use.
    public HpkFile? OpenForRuntime(string? activePackId, out string? error)
    {
        error = null;
        var pack = TryGet(activePackId);
        if (pack is null || !pack.IsSelectable || pack.FilePath is null)
            return null;

        try
        {
            var file = HpkFile.Open(pack.FilePath, HpkOpenMode.Full);
            if (file.PackHashText != pack.PackHash)
            {
                file.Dispose();
                error = "Pack file does not match its install record.";
                return null;
            }

            return file;
        }
        catch (Exception ex) when (ex is HpkFormatException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            log.Warning("Active pack '" + pack.Id + "' failed verification: " + ex.Message);
            return null;
        }
    }

    // Connects an installed translation to the feed it updates from.
    public void LinkFeed(string packId, string url)
    {
        var pack = TryGet(packId) ?? throw new InvalidOperationException("No such translation.");
        if (pack.PackHash is null)
            throw new InvalidOperationException("The translation is not valid.");

        WriteInstallRecord(packId, pack.PackHash, pack.PinnedKey, url.Trim());
        Rescan();
    }

    internal string PackDirectory(string packId) => Path.Combine(PacksDir, packId);

    // A folder name for a translation installed for the first time.
    internal string NewPackId()
    {
        while (true)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            if (!Directory.Exists(PackDirectory(id)))
                return id;
        }
    }

    internal static string FileNameFor(string packHash) => packHash["sha256:".Length..] + PackExtension;

    internal void WriteInstallRecord(string packId, string packHash, string? pinnedKey, string? feedUrl)
    {
        var dir = PackDirectory(packId);
        var record = new JObject
        {
            ["formatVersion"] = InstallRecordVersion,
            ["packHash"] = packHash,
            ["pinnedKey"] = pinnedKey,
            ["feedUrl"] = feedUrl,
        };
        var temp = Path.Combine(dir, InstalledFileName + ".tmp");
        File.WriteAllText(temp, record.ToString(Formatting.Indented) + "\n");
        File.Move(temp, Path.Combine(dir, InstalledFileName), overwrite: true);
    }

    private string? ReadGameVersion()
    {
        string? current = null;
        try
        {
            current = gameVersion.GetCurrentGameVersion();
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(current) ? null : current.Trim();
    }

    private TranslationPack LoadPack(string dir)
    {
        var folderId = Path.GetFileName(dir);
        TranslationPack Invalid(string reason, string? filePath = null)
        {
            log.Warning("Translation pack '" + folderId + "' is invalid: " + reason);
            return new TranslationPack(folderId, dir, filePath, null, null, null, null, null, reason, Environment);
        }

        if (!IsValidPackId(folderId))
            return Invalid("Folder name is not a translation id.");

        string packHash;
        string? pinnedKey;
        string? feedUrl;
        try
        {
            var recordPath = Path.Combine(dir, InstalledFileName);
            if (!File.Exists(recordPath))
                return Invalid("Missing " + InstalledFileName + ".");

            var record = JObject.Parse(File.ReadAllText(recordPath));
            if ((int?)record["formatVersion"] != InstallRecordVersion || record["packHash"]?.Type != JTokenType.String ||
                record["pinnedKey"]?.Type is not (JTokenType.String or JTokenType.Null) ||
                record["feedUrl"]?.Type is not (JTokenType.String or JTokenType.Null))
                return Invalid("Unsupported " + InstalledFileName + ".");

            packHash = (string)record["packHash"]!;
            pinnedKey = (string?)record["pinnedKey"];
            feedUrl = (string?)record["feedUrl"];
            if (!PackHashPattern().IsMatch(packHash))
                return Invalid("Invalid pack hash in " + InstalledFileName + ".");
            if (pinnedKey is not null && !FingerprintPattern().IsMatch(pinnedKey))
                return Invalid("Invalid key in " + InstalledFileName + ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Invalid(ex.Message);
        }

        var filePath = Path.Combine(dir, FileNameFor(packHash));
        try
        {
            using var file = HpkFile.Open(filePath, HpkOpenMode.Metadata);
            if (file.PackHashText != packHash)
                return Invalid("Pack file does not match its install record.", filePath);

            return new TranslationPack(
                folderId, dir, filePath, file.Manifest, packHash, file.Signature?.Fingerprint, pinnedKey, feedUrl, null, Environment);
        }
        catch (Exception ex) when (ex is HpkFormatException or IOException or UnauthorizedAccessException)
        {
            return Invalid(ex.Message, filePath);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            log.Warning("Failed to remove " + path + ".", ex);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex PackIdPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex PackHashPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex FingerprintPattern();
}
