using System.Text.RegularExpressions;
using Harmonia.Packs.Hpk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Harmonia.Packs;

// Installed packs live in <resources>/packs/<packId>/<hash>.hpk; the
// per-pack installed.json names the current file. Only the active pack is
// fully verified at load; listing uses metadata because every file was fully
// verified when it was installed.
public sealed partial class TranslationPackStore
{
    public const string PacksDirName = "packs";
    public const string InstalledFileName = "installed.json";
    public const string PackExtension = ".hpk";
    private const string StagingDirName = ".staging";

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
            if (file.PackHashText != pack.PackHash || file.Manifest.PackId != pack.Id)
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

    internal string PackDirectory(string packId) => Path.Combine(PacksDir, packId);

    internal static string FileNameFor(string packHash) => packHash["sha256:".Length..] + PackExtension;

    internal void WriteInstallRecord(string packId, string packHash)
    {
        var dir = PackDirectory(packId);
        var record = new JObject
        {
            ["formatVersion"] = 1,
            ["packHash"] = packHash,
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
            return new TranslationPack(folderId, dir, filePath, null, null, null, reason, Environment);
        }

        if (!IsValidPackId(folderId))
            return Invalid("Folder name is not a pack id.");

        string packHash;
        try
        {
            var recordPath = Path.Combine(dir, InstalledFileName);
            if (!File.Exists(recordPath))
                return Invalid("Missing " + InstalledFileName + ".");

            var record = JObject.Parse(File.ReadAllText(recordPath));
            if ((int?)record["formatVersion"] != 1 || record["packHash"]?.Type != JTokenType.String)
                return Invalid("Unsupported " + InstalledFileName + ".");

            packHash = (string)record["packHash"]!;
            if (!PackHashPattern().IsMatch(packHash))
                return Invalid("Invalid pack hash in " + InstalledFileName + ".");
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
            if (file.Manifest.PackId != folderId)
                return Invalid("Pack id '" + file.Manifest.PackId + "' does not match folder '" + folderId + "'.", filePath);

            return new TranslationPack(
                folderId, dir, filePath, file.Manifest, packHash, file.Signature?.Fingerprint, null, Environment);
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
}
