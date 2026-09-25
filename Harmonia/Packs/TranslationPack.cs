using Harmonia.Packs.Hpk;

namespace Harmonia.Packs;

// One installed pack as listed by the store. Invalid packs stay listed with a
// reason instead of failing the whole scan.
public sealed class TranslationPack
{
    public TranslationPack(
        string id,
        string packDirectory,
        string? filePath,
        HpkManifest? manifest,
        string? packHash,
        string? publisherFingerprint,
        string? invalidReason,
        PackEnvironment environment)
    {
        Id = id;
        PackDirectory = packDirectory;
        FilePath = filePath;
        Manifest = manifest;
        PackHash = packHash;
        PublisherFingerprint = publisherFingerprint;
        InvalidReason = invalidReason;

        if (manifest is not null && invalidReason is null)
        {
            LanguageCompatible = PackCompatibility.IsLanguageCompatible(manifest, environment.ClientLanguage);
            PluginCompatible = PackCompatibility.IsPluginSupported(manifest, environment.PluginVersion);
            GameVersionMatches = PackCompatibility.GameVersionMatches(manifest, environment.GameVersion);
        }
    }

    public string Id { get; }
    public string PackDirectory { get; }
    public string? FilePath { get; }
    public HpkManifest? Manifest { get; }
    public string? PackHash { get; }
    public string? PublisherFingerprint { get; }
    public string? InvalidReason { get; }
    public bool IsValid => InvalidReason is null;
    public bool IsSigned => PublisherFingerprint is not null;
    public bool LanguageCompatible { get; }
    public bool PluginCompatible { get; }

    // Null while the game version is unknown. A mismatch does not block the
    // pack: every cell is still checked against its source string.
    public bool? GameVersionMatches { get; }

    public bool IsSelectable => IsValid && LanguageCompatible && PluginCompatible;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Manifest?.Title) ? Manifest.Title.Trim() : Id;
}

public readonly record struct PackEnvironment(string? ClientLanguage, string? PluginVersion, string? GameVersion);

public static class PackCompatibility
{
    // Unknown client language cannot be judged here; the per-cell source
    // guard still rejects every string from another language at runtime.
    public static bool IsLanguageCompatible(HpkManifest manifest, string? clientLanguage) =>
        string.IsNullOrWhiteSpace(clientLanguage) ||
        string.Equals(manifest.SourceLanguage, clientLanguage.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool IsPluginSupported(HpkManifest manifest, string? pluginVersion) =>
        IsPluginSupported(manifest.MinHarmonia, pluginVersion);

    public static bool IsPluginSupported(Version minHarmonia, string? pluginVersion)
    {
        if (string.IsNullOrWhiteSpace(pluginVersion) || !Version.TryParse(pluginVersion.Trim(), out var current))
            return true;

        return Normalize(current) >= Normalize(minHarmonia);
    }

    public static bool? GameVersionMatches(HpkManifest manifest, string? gameVersion) =>
        GameVersionMatches(manifest.SourceGameVersion, gameVersion);

    public static bool? GameVersionMatches(string packGameVersion, string? gameVersion) =>
        string.IsNullOrWhiteSpace(gameVersion)
            ? null
            : string.Equals(packGameVersion.Trim(), gameVersion.Trim(), StringComparison.Ordinal);

    // "1.4" and "1.4.0.0" are the same version.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
