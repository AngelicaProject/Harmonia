using System.Reflection;
using Harmonia.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Harmonia.Compatibility;

// What one community plugin compares with the text the game shows. Such a
// plugin reads the original text from the game files (Lumina) and looks for
// it in menus and dialogs, which show the translation: it stops finding what
// it clicks. While the plugin is installed, this text stays in the game's
// language, unless the player turns the profile off.
//
// Profiles are data in Assets/Compatibility, one file per plugin:
//   plugin    the plugin's InternalName;
//   name      the name shown to the player;
//   sheets    sheets kept whole;
//   rows      sheet name -> row ids (every subrow of a row is kept);
//   cells     [{ sheet, columns, rows? | source? }]: only these columns (the
//             game's column indexes) of the rows, of the source's rows in
//             that sheet, or of every row without either;
//   sources   rows found in the game data at startup (CompatibilitySources);
//   optional  [{ when, require, sheets, rows, cells, sources }]: kept only
//             while one of the "when" conditions on the plugin's own
//             settings holds and every "require" condition does, for
//             features that are off by default or cost much text;
//   hardcoded what of a part stays even when the plugin is given the
//             translated text (Game/SharedSheets), as a part of its own
//             (sheets, rows, cells, sources) or `true` for all of it: text
//             the plugin has in its own code in the game's languages, or
//             reads from another row than the one the game shows. The rest
//             of a part is text the plugin reads from the game files.
public sealed record CompatibilityProfile(string Plugin, string Name, IReadOnlyList<CompatibilityPart> Parts)
{
    private const string ResourcePrefix = "Compatibility.";

    // Rows that profiles name but that only the game data can list.
    public static readonly IReadOnlyList<string> KnownSources = ["aethernet-place-names", "triple-triad-npcs"];

    private static readonly Lazy<(IReadOnlyList<CompatibilityProfile> Profiles, IReadOnlyList<string> Errors)> BuiltIn =
        new(static () => Load(ReadResources()));

    public static IReadOnlyList<CompatibilityProfile> All => BuiltIn.Value.Profiles;

    // Built-in profiles that could not be read; the others still apply.
    public static IReadOnlyList<string> LoadErrors => BuiltIn.Value.Errors;

    public static CompatibilityProfile Parse(string json)
    {
        var file = JsonConvert.DeserializeObject<ProfileFile>(json) ?? throw new FormatException("Empty profile.");
        if (string.IsNullOrWhiteSpace(file.Plugin))
            throw new FormatException("A profile needs a plugin.");

        var parts = new List<CompatibilityPart> { ParsePart(file, [], []) };
        foreach (var optional in file.Optional ?? [])
        {
            if (optional.When is not { Count: > 0 } && optional.Require is not { Count: > 0 })
                throw new FormatException("An optional part needs a condition.");
            parts.Add(ParsePart(optional,
                (optional.When ?? []).Select(ParseCondition).ToList(),
                (optional.Require ?? []).Select(ParseCondition).ToList()));
        }

        return new CompatibilityProfile(file.Plugin, string.IsNullOrWhiteSpace(file.Name) ? file.Plugin : file.Name, parts);
    }

    // Profiles of the installed plugins, in profile order. Names are compared
    // by letters and digits only: "Pandora's Box" is PandorasBox.
    public static IReadOnlyList<CompatibilityProfile> Installed(
        IEnumerable<CompatibilityProfile> profiles, IEnumerable<string> installedPlugins)
    {
        var installed = installedPlugins.Select(NameKey).ToHashSet(StringComparer.Ordinal);
        return profiles.Where(p => installed.Contains(NameKey(p.Plugin))).ToList();
    }

    public static bool IsOn(CompatibilityProfile profile, IEnumerable<string> disabledPlugins) =>
        !disabledPlugins.Contains(profile.Plugin, StringComparer.OrdinalIgnoreCase);

    // sourceRows lists the rows of a source (a source that fails keeps
    // nothing); holds decides an optional part's condition. With
    // hardcodedOnly, plugins read the translation from the game files and
    // only what they do not read there is kept.
    public static KeptRows Keep(
        IEnumerable<CompatibilityProfile> profiles,
        Func<string, IEnumerable<(string Sheet, uint Row)>> sourceRows,
        Func<CompatibilityCondition, bool>? holds = null,
        bool hardcodedOnly = false)
    {
        var keep = new KeptRows();
        foreach (var whole in profiles.SelectMany(static p => p.Parts))
        {
            if (!whole.Applies(holds) || (hardcodedOnly ? whole.Hardcoded : whole) is not { } part)
                continue;

            foreach (var sheet in part.Sheets)
                keep.AddSheet(sheet);
            foreach (var (sheet, rows) in part.Rows)
                keep.AddRows(sheet, rows);
            foreach (var cells in part.Cells)
            {
                var rows = cells.Source is null
                    ? cells.Rows
                    : sourceRows(cells.Source).Where(r => string.Equals(r.Sheet, cells.Sheet, StringComparison.Ordinal)).Select(static r => r.Row).ToList();
                keep.AddColumns(cells.Sheet, cells.Columns, rows);
            }
            foreach (var source in part.Sources)
            {
                foreach (var group in sourceRows(source).GroupBy(static r => r.Sheet, StringComparer.Ordinal))
                    keep.AddRows(group.Key, group.Select(static r => r.Row));
            }
        }

        return keep;
    }

    // Whether the profile keeps any text with these plugin settings.
    public bool KeepsAnything(Func<CompatibilityCondition, bool>? holds = null, bool hardcodedOnly = false) =>
        Parts.Any(whole => whole.Applies(holds) && (hardcodedOnly ? whole.Hardcoded : whole) is { } p &&
                           p.Sheets.Count + p.Rows.Count + p.Cells.Count + p.Sources.Count > 0);

    private static string NameKey(string name) =>
        new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static CompatibilityPart ParsePart(
        PartFile file, IReadOnlyList<CompatibilityCondition> when, IReadOnlyList<CompatibilityCondition> require)
    {
        var sources = file.Sources ?? [];
        var unknown = sources.Concat((file.Cells ?? []).Select(static c => c.Source).OfType<string>())
            .FirstOrDefault(s => !KnownSources.Contains(s, StringComparer.Ordinal));
        if (unknown is not null)
            throw new FormatException($"Unknown source \"{unknown}\".");

        var cells = (file.Cells ?? []).Select(static c =>
            string.IsNullOrWhiteSpace(c.Sheet) || c.Columns is not { Count: > 0 }
                ? throw new FormatException("Cells need a sheet and columns.")
                : c.Rows is not null && c.Source is not null
                    ? throw new FormatException("Cells take rows or a source, not both.")
                    : new CompatibilityCells(c.Sheet, c.Columns, c.Rows, c.Source)).ToList();

        var part = new CompatibilityPart(
            when,
            require,
            file.Sheets ?? [],
            (file.Rows ?? []).ToDictionary(static p => p.Key, static p => (IReadOnlyList<uint>)p.Value, StringComparer.Ordinal),
            cells,
            sources);
        return file.Hardcoded switch
        {
            null or { Type: JTokenType.Null } => part,
            { Type: JTokenType.Boolean } flag => (bool)flag ? part with { Hardcoded = part } : part,
            JObject subset => part with
            {
                Hardcoded = ParsePart(subset.ToObject<PartFile>() ?? throw new FormatException("Empty hardcoded part."), [], []),
            },
            _ => throw new FormatException("\"hardcoded\" is true or a part."),
        };
    }

    private static CompatibilityCondition ParseCondition(ConditionFile c) =>
        string.IsNullOrWhiteSpace(c.Config) || string.IsNullOrWhiteSpace(c.Path)
            ? throw new FormatException("A condition needs a config file and a path.")
            : new CompatibilityCondition(c.Config, c.Path, c.EqualsValue, c.NotEquals, c.Default ?? true);

    // A profile that does not parse is skipped, never the plugin.
    public static (IReadOnlyList<CompatibilityProfile> Profiles, IReadOnlyList<string> Errors) Load(
        IEnumerable<(string Name, string Json)> files)
    {
        var profiles = new List<CompatibilityProfile>();
        var errors = new List<string>();
        foreach (var (name, json) in files)
        {
            try
            {
                var profile = Parse(json);
                if (profiles.Any(p => string.Equals(NameKey(p.Plugin), NameKey(profile.Plugin), StringComparison.Ordinal)))
                    throw new FormatException($"Another profile is for {profile.Plugin}.");
                profiles.Add(profile);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }

        return (profiles, errors);
    }

    private static List<(string Name, string Json)> ReadResources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new List<(string, string)>();
        foreach (var name in assembly.GetManifestResourceNames().Where(static n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            files.Add((name, reader.ReadToEnd()));
        }

        return files;
    }

    private class PartFile
    {
        public List<string>? Sheets { get; set; }
        public Dictionary<string, List<uint>>? Rows { get; set; }
        public List<CellsFile>? Cells { get; set; }
        public List<string>? Sources { get; set; }
        public JToken? Hardcoded { get; set; }
    }

    private sealed class ProfileFile : PartFile
    {
        public string? Plugin { get; set; }
        public string? Name { get; set; }
        public List<OptionalFile>? Optional { get; set; }
    }

    private sealed class OptionalFile : PartFile
    {
        public List<ConditionFile>? When { get; set; }
        public List<ConditionFile>? Require { get; set; }
    }

    private sealed class CellsFile
    {
        public string? Sheet { get; set; }
        public List<uint>? Columns { get; set; }
        public List<uint>? Rows { get; set; }
        public string? Source { get; set; }
    }

    private sealed class ConditionFile
    {
        public string? Config { get; set; }
        public string? Path { get; set; }

        [JsonProperty("equals")]
        public JToken? EqualsValue { get; set; }

        public JToken? NotEquals { get; set; }
        public bool? Default { get; set; }
    }
}

// Applies when any When condition holds (or When is empty) and every Require
// condition holds; both are empty for the part that always applies.
public sealed record CompatibilityPart(
    IReadOnlyList<CompatibilityCondition> When,
    IReadOnlyList<CompatibilityCondition> Require,
    IReadOnlyList<string> Sheets,
    IReadOnlyDictionary<string, IReadOnlyList<uint>> Rows,
    IReadOnlyList<CompatibilityCells> Cells,
    IReadOnlyList<string> Sources)
{
    // What of this part is kept even when the plugin reads the translation
    // from the game files; null when nothing is.
    public CompatibilityPart? Hardcoded { get; init; }

    // Without holds every condition counts as met.
    public bool Applies(Func<CompatibilityCondition, bool>? holds)
    {
        holds ??= static _ => true;
        return (When.Count == 0 || When.Any(holds)) && Require.All(holds);
    }
}

// Rows and Source null: every row of the sheet. Source: the rows the source
// lists in this sheet.
public sealed record CompatibilityCells(string Sheet, IReadOnlyList<uint> Columns, IReadOnlyList<uint>? Rows, string? Source = null);
