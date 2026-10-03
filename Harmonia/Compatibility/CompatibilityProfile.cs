using System.Reflection;
using Harmonia.Runtime;
using Newtonsoft.Json;

namespace Harmonia.Compatibility;

// What one community plugin compares with the text the game shows. Such a
// plugin reads the original text from the game files (Lumina) and looks for
// it in menus and dialogs, which show the translation: it stops finding what
// it clicks. While the plugin is installed, these rows stay in the game's
// language, unless the player turns the profile off.
//
// Profiles are data in Assets/Compatibility, one file per plugin:
//   plugin   the plugin's InternalName;
//   name     the name shown to the player;
//   sheets   sheets kept whole;
//   rows     sheet name -> row ids (every subrow of a row is kept);
//   sources  rows found in the game data at startup (CompatibilitySources).
public sealed record CompatibilityProfile(
    string Plugin,
    string Name,
    IReadOnlyList<string> Sheets,
    IReadOnlyDictionary<string, IReadOnlyList<uint>> Rows,
    IReadOnlyList<string> Sources)
{
    private const string ResourcePrefix = "Compatibility.";

    // Rows that profiles name but that only the game data can list.
    public static readonly IReadOnlyList<string> KnownSources = ["aethernet-place-names"];

    private static readonly Lazy<IReadOnlyList<CompatibilityProfile>> BuiltIn = new(LoadBuiltIn);

    public static IReadOnlyList<CompatibilityProfile> All => BuiltIn.Value;

    public static CompatibilityProfile Parse(string json)
    {
        var file = JsonConvert.DeserializeObject<ProfileFile>(json) ?? throw new FormatException("Empty profile.");
        if (string.IsNullOrWhiteSpace(file.Plugin))
            throw new FormatException("A profile needs a plugin.");

        var sources = file.Sources ?? [];
        var unknown = sources.FirstOrDefault(s => !KnownSources.Contains(s, StringComparer.Ordinal));
        if (unknown is not null)
            throw new FormatException($"Unknown source \"{unknown}\".");

        return new CompatibilityProfile(
            file.Plugin,
            string.IsNullOrWhiteSpace(file.Name) ? file.Plugin : file.Name,
            file.Sheets ?? [],
            (file.Rows ?? []).ToDictionary(static p => p.Key, static p => (IReadOnlyList<uint>)p.Value, StringComparer.Ordinal),
            sources);
    }

    // Profiles of the installed plugins, in profile order.
    public static IReadOnlyList<CompatibilityProfile> Installed(
        IEnumerable<CompatibilityProfile> profiles, IEnumerable<string> installedPlugins)
    {
        var installed = installedPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profiles.Where(p => installed.Contains(p.Plugin)).ToList();
    }

    public static bool IsOn(CompatibilityProfile profile, IEnumerable<string> disabledPlugins) =>
        !disabledPlugins.Contains(profile.Plugin, StringComparer.OrdinalIgnoreCase);

    // sourceRows lists the rows of a source; a source that fails keeps nothing.
    public static KeptRows Keep(IEnumerable<CompatibilityProfile> profiles, Func<string, IEnumerable<(string Sheet, uint Row)>> sourceRows)
    {
        var keep = new KeptRows();
        foreach (var profile in profiles)
        {
            foreach (var sheet in profile.Sheets)
                keep.AddSheet(sheet);
            foreach (var (sheet, rows) in profile.Rows)
                keep.AddRows(sheet, rows);
            foreach (var source in profile.Sources)
            {
                foreach (var group in sourceRows(source).GroupBy(static r => r.Sheet, StringComparer.Ordinal))
                    keep.AddRows(group.Key, group.Select(static r => r.Row));
            }
        }

        return keep;
    }

    private static IReadOnlyList<CompatibilityProfile> LoadBuiltIn()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new List<CompatibilityProfile>();
        foreach (var name in assembly.GetManifestResourceNames().Where(static n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            result.Add(Parse(reader.ReadToEnd()));
        }

        return result;
    }

    private sealed class ProfileFile
    {
        public string? Plugin { get; set; }
        public string? Name { get; set; }
        public List<string>? Sheets { get; set; }
        public Dictionary<string, List<uint>>? Rows { get; set; }
        public List<string>? Sources { get; set; }
    }
}
