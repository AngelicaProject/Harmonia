using Harmonia.Runtime;

namespace Harmonia.Packs;

public enum SheetPart
{
    Names,
    Descriptions,
}

// What a sheet holds: one part in every String column (Whole), or both parts
// in listed columns by the game's column index (a sheet with one part is
// Whole, so a split sheet always has two rows to choose from). The columns were read from a
// pack's translations; tools/CompatibilityCheck checks that each is a
// String column of the running game.
public sealed record SheetPartMap(string Sheet, SheetPart? Whole, IReadOnlyList<uint> Names, IReadOnlyList<uint> Descriptions)
{
    public IReadOnlyList<uint> Columns(SheetPart part) => part == SheetPart.Names ? Names : Descriptions;

    public bool Has(SheetPart part) => Whole == part || Columns(part).Count > 0;

    public bool Split => Whole is null;
}

// Names and descriptions as the player keeps them in the game's language,
// across sheets. A sheet that is all one part is kept as a sheet
// (Configuration.UntranslatedSheets); a part of a sheet that holds both is
// an entry "Sheet:part" in Configuration.UntranslatedParts. Singular and
// plural forms belong to the names: the game builds log messages from
// them, and a name kept in one form and translated in another would mix
// in one line.
public static class SheetParts
{
    private const char Separator = ':';

    public static readonly IReadOnlyList<SheetPartMap> All =
    [
        Split("Item", [0, 2, 9], [8]),
        Whole("EventItem", SheetPart.Names),
        Whole("EventItemHelp", SheetPart.Descriptions),
        Whole("ContentFinderCondition", SheetPart.Names),
        Whole("ContentFinderConditionTransient", SheetPart.Descriptions),
        Split("ContentRoulette", [0, 1], [3, 4]),
        Whole("Action", SheetPart.Names),
        Whole("ActionTransient", SheetPart.Descriptions),
        Whole("Trait", SheetPart.Names),
        Whole("TraitTransient", SheetPart.Descriptions),
        Split("Status", [0], [1]),
        Split("CraftAction", [0], [1]),
        Split("GeneralAction", [0], [1]),
        Split("PetAction", [0], [1]),
        Split("BuddyAction", [0], [1]),
        Split("CompanyAction", [0], [1]),
        Whole("Mount", SheetPart.Names),
        Whole("MountTransient", SheetPart.Descriptions),
        Whole("Companion", SheetPart.Names),
        // The minion's action name is column 3.
        Split("CompanionTransient", [3], [0, 1, 2, 4]),
        Whole("Ornament", SheetPart.Names),
        Whole("OrnamentTransient", SheetPart.Descriptions),
        Split("Glasses", [4, 6, 13], [12]),
        Whole("GlassesStyle", SheetPart.Names),
        Split("Achievement", [1], [2]),
        Whole("Title", SheetPart.Names),
    ];

    private static readonly Dictionary<string, SheetPartMap> BySheet = All.ToDictionary(static m => m.Sheet, StringComparer.Ordinal);

    public static SheetPartMap? Of(string sheet) => BySheet.GetValueOrDefault(sheet);

    public static string Entry(string sheet, SheetPart part) => sheet + Separator + Name(part);

    // The sheets with a part kept, from the entries; unknown entries are
    // skipped.
    public static IEnumerable<string> SheetsOf(IEnumerable<string> parts) =>
        parts.Select(Parse).Where(static p => p is not null).Select(static p => p!.Value.Map.Sheet).Distinct(StringComparer.Ordinal);

    // The columns the player keeps, added to what stays in the game's
    // language this session.
    public static void AddTo(KeptRows keep, IEnumerable<string> parts)
    {
        foreach (var entry in parts)
        {
            if (Parse(entry) is { Map.Split: true } p && p.Map.Columns(p.Part).Count > 0)
                keep.AddColumns(p.Map.Sheet, p.Map.Columns(p.Part), null);
        }
    }

    public static bool IsKept(IReadOnlyCollection<string> sheets, IReadOnlyCollection<string> parts, SheetPartMap map, SheetPart part)
    {
        if (!map.Has(part))
            return false;
        if (new SheetFilter(sheets).Excludes(map.Sheet))
            return true;
        return map.Split && parts.Contains(Entry(map.Sheet, part), StringComparer.Ordinal);
    }

    // How much of a part the sheets keep, over those that have it.
    public static KeepState StateOf(IReadOnlyCollection<string> sheets, IReadOnlyCollection<string> parts, IEnumerable<SheetPartMap> maps, SheetPart part)
    {
        var states = maps.Where(m => m.Has(part)).Select(m => IsKept(sheets, parts, m, part)).ToArray();
        if (states.Length == 0 || !states.Any(static s => s))
            return KeepState.None;
        return states.All(static s => s) ? KeepState.All : KeepState.Some;
    }

    public static void Keep(List<string> sheets, List<string> parts, SheetPartMap map, SheetPart part)
    {
        if (!map.Has(part) || IsKept(sheets, parts, map, part))
            return;

        if (map.Whole == part)
        {
            SheetTree.Keep(sheets, map.Sheet);
            Forget(parts, map.Sheet);
        }
        else
        {
            parts.Add(Entry(map.Sheet, part));
        }
    }

    // A sheet kept whole is translated again except for its other part:
    // its columns that hold neither part are translated with it.
    public static void Translate(List<string> sheets, List<string> parts, SheetPartMap map, SheetPart part)
    {
        if (!map.Has(part))
            return;

        if (map.Whole == part)
        {
            SheetTree.Translate(sheets, map.Sheet);
            return;
        }

        parts.RemoveAll(e => string.Equals(e, Entry(map.Sheet, part), StringComparison.Ordinal));
        if (!sheets.Contains(map.Sheet, StringComparer.Ordinal))
            return;

        SheetTree.Translate(sheets, map.Sheet);
        var other = part == SheetPart.Names ? SheetPart.Descriptions : SheetPart.Names;
        if (map.Has(other))
            parts.Add(Entry(map.Sheet, other));
    }

    // A sheet or folder kept or translated as a whole leaves no parts
    // behind.
    public static void Forget(List<string> parts, string entry) =>
        parts.RemoveAll(e => Parse(e) is { } p && (SheetFilter.IsFolder(entry)
            ? p.Map.Sheet.StartsWith(entry[..^1], StringComparison.Ordinal)
            : string.Equals(p.Map.Sheet, entry, StringComparison.Ordinal)));

    private static (SheetPartMap Map, SheetPart Part)? Parse(string entry)
    {
        var separator = entry.LastIndexOf(Separator);
        if (separator <= 0 || Of(entry[..separator]) is not { } map)
            return null;

        SheetPart? part = entry[(separator + 1)..] switch
        {
            "names" => SheetPart.Names,
            "descriptions" => SheetPart.Descriptions,
            _ => null,
        };
        return part is { } p ? (map, p) : null;
    }

    private static string Name(SheetPart part) => part == SheetPart.Names ? "names" : "descriptions";

    private static SheetPartMap Whole(string sheet, SheetPart part) => new(sheet, part, [], []);

    private static SheetPartMap Split(string sheet, uint[] names, uint[] descriptions) => new(sheet, null, names, descriptions);
}
