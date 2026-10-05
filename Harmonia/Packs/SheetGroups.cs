namespace Harmonia.Packs;

// A set of game sheets the player can keep in the game's language as one
// choice. An entry is a sheet name ("Item") or a folder of sheets
// ("quest/*"). A built-in group's Key names its texts, "sheets.<key>" and
// "sheets.<key>_hint", unless the player renamed it (Name).
public sealed record SheetGroup(string Key, IReadOnlyList<string> Entries)
{
    public string? Name { get; init; }

    public bool BuiltIn { get; init; } = true;

    // The player changed the built-in group's sheets.
    public bool EntriesEdited { get; init; }

    // The player changed the built-in group in any way.
    public bool Edited { get; init; }
}

// A change the player made to the groups, saved in Configuration: a built-in
// group (a key of SheetGroups.All) with other sheets, another name, or
// removed; or a group of the player's own (any other key).
[Serializable]
public sealed class SavedSheetGroup
{
    public string Key { get; set; } = string.Empty;

    public string? Name { get; set; }

    public List<string> Entries { get; set; } = [];

    public bool Removed { get; set; }
}

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

    public static SheetGroup? BuiltIn(string key) =>
        All.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.Ordinal));

    // The groups the player sees: the built-in ones with the player's changes,
    // then the player's own, in the order they were made.
    public static IReadOnlyList<SheetGroup> Effective(IReadOnlyList<SavedSheetGroup>? saved)
    {
        saved ??= [];
        var groups = new List<SheetGroup>();
        foreach (var group in All)
        {
            var change = saved.LastOrDefault(s => string.Equals(s.Key, group.Key, StringComparison.Ordinal));
            if (change is null)
            {
                groups.Add(group);
                continue;
            }

            if (change.Removed)
                continue;

            var edited = !change.Entries.SequenceEqual(group.Entries, StringComparer.Ordinal);
            groups.Add(group with
            {
                Entries = edited ? [.. change.Entries] : group.Entries,
                Name = string.IsNullOrWhiteSpace(change.Name) ? null : change.Name,
                EntriesEdited = edited,
                Edited = true,
            });
        }

        foreach (var own in saved)
        {
            if (own.Removed || BuiltIn(own.Key) is not null || groups.Any(g => string.Equals(g.Key, own.Key, StringComparison.Ordinal)))
                continue;

            groups.Add(new SheetGroup(own.Key, [.. own.Entries])
            {
                Name = string.IsNullOrWhiteSpace(own.Name) ? own.Key : own.Name,
                BuiltIn = false,
                Edited = true,
            });
        }

        return groups;
    }

    // Saves a group as the player left it. A built-in group back in its
    // original state leaves no change behind.
    public static void Save(List<SavedSheetGroup> saved, string key, string? name, IReadOnlyList<string> entries)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var index = saved.FindIndex(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        if (BuiltIn(key) is { } builtIn && name is null && entries.SequenceEqual(builtIn.Entries, StringComparer.Ordinal))
        {
            if (index >= 0)
                saved.RemoveAt(index);
            return;
        }

        var change = new SavedSheetGroup { Key = key, Name = name, Entries = [.. entries.Distinct(StringComparer.Ordinal)] };
        if (index >= 0)
            saved[index] = change;
        else
            saved.Add(change);
    }

    public static void Remove(List<SavedSheetGroup> saved, string key)
    {
        saved.RemoveAll(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        if (BuiltIn(key) is not null)
            saved.Add(new SavedSheetGroup { Key = key, Removed = true });
    }

    // Restores a built-in group as Harmonia ships it.
    public static void Restore(List<SavedSheetGroup> saved, string key) =>
        saved.RemoveAll(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    public static string NewKey(IReadOnlyList<SavedSheetGroup> saved)
    {
        for (var n = 1; ; n++)
        {
            var key = "user" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!saved.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal)))
                return key;
        }
    }
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
