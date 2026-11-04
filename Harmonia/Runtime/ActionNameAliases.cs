namespace Harmonia.Runtime;

public sealed record ActionNameAlias(string Shown, IReadOnlySet<uint> Rows);

// A fallback for names the native command search did not recognize. The
// native search still chooses the row: duplicate translations must not turn
// an original name into a different action.
public sealed class ActionNameAliases
{
    public const string MatchSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 57 48 83 EC ?? 8B 6C 24 ?? 49 8B F9 4D 8B F8 4C 8B E2 48 8B D9";

    private readonly Dictionary<string, ActionNameAlias[]> aliases;

    public ActionNameAliases(IEnumerable<(uint Row, string Original, string Shown)> names)
    {
        aliases = names
            .Where(static n => n.Original.Length > 0 && n.Shown.Length > 0)
            .GroupBy(static n => n.Original, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key,
                static g => g.Where(n => !string.Equals(n.Original, n.Shown, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(static n => n.Shown, StringComparer.OrdinalIgnoreCase)
                    .Select(static shown => new ActionNameAlias(shown.Key, shown.Select(static n => n.Row).ToHashSet()))
                    .ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ActionNameAlias> Find(string original) =>
        aliases.TryGetValue(original, out var found) ? found : [];
}
