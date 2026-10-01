namespace Harmonia.Packs;

// A set of game sheets the player can keep in the game's language as one
// choice. An entry is a sheet name ("Item") or a folder of sheets
// ("quest/*"). Key names the group's texts: "sheets.<key>" and
// "sheets.<key>_hint".
public sealed record SheetGroup(string Key, IReadOnlyList<string> Entries);

public static class SheetGroups
{
    // Names first, then descriptions and story text.
    public static readonly IReadOnlyList<SheetGroup> All =
    [
        new("items", ["Item", "EventItem", "EventItemHelp"]),
        new("duties", ["ContentFinderCondition", "ContentFinderConditionTransient", "ContentRoulette", "InstanceContent"]),
        new("places", ["PlaceName", "Town", "Aetheryte"]),
        new("actions", ["Action", "ActionTransient", "Trait", "TraitTransient", "Status", "CraftAction", "GeneralAction", "PetAction", "BuddyAction", "CompanyAction"]),
        new("jobs", ["ClassJob", "ClassJobCategory"]),
        new("characters", ["ENpcResident", "BNpcName"]),
        new("collections", ["Mount", "MountTransient", "Companion", "CompanionTransient", "Ornament", "OrnamentTransient", "Glasses", "GlassesStyle"]),
        new("achievements", ["Achievement", "Title"]),
        new("quest_names", ["Quest"]),
        new("story", ["quest/*", "cut_scene/*", "custom/*"]),
    ];
}

// The sheets the player keeps in the game's language: sheet names and
// folders ("quest/*") from Configuration.UntranslatedSheets.
public sealed class SheetFilter
{
    private const string FolderSuffix = "/*";

    private readonly HashSet<string> sheets = new(StringComparer.Ordinal);
    private readonly List<string> folders = [];

    public SheetFilter(IEnumerable<string>? entries)
    {
        foreach (var entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (IsFolder(entry))
                folders.Add(entry[..^1]);
            else
                sheets.Add(entry);
        }
    }

    public bool IsEmpty => sheets.Count == 0 && folders.Count == 0;

    public static bool IsFolder(string entry) => entry.EndsWith(FolderSuffix, StringComparison.Ordinal);

    // "quest/*" for "quest/000/Test"; null for a sheet outside any folder.
    public static string? FolderOf(string sheet)
    {
        var slash = sheet.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? null : sheet[..slash] + FolderSuffix;
    }

    public bool Excludes(string sheet) => sheets.Contains(sheet) || ExcludedByFolder(sheet);

    public bool ExcludedByFolder(string sheet) =>
        folders.Any(prefix => sheet.StartsWith(prefix, StringComparison.Ordinal));
}
