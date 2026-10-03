namespace Harmonia.Runtime;

// Rows that stay in the game's language this session although the pack
// translates them: the text other plugins compare with what the game shows.
// A sheet kept whole binds like a sheet the player keeps untranslated.
public sealed class KeptRows
{
    private readonly Dictionary<string, HashSet<uint>> rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> sheets = new(StringComparer.Ordinal);

    public bool IsEmpty => rows.Count == 0 && sheets.Count == 0;

    public void AddSheet(string sheet) => sheets.Add(sheet);

    public void AddRows(string sheet, IEnumerable<uint> ids)
    {
        if (!rows.TryGetValue(sheet, out var set))
            rows[sheet] = set = [];
        set.UnionWith(ids);
    }

    public bool KeepsSheet(string sheet) => sheets.Contains(sheet);

    public IReadOnlySet<uint>? RowsOf(string sheet) => rows.GetValueOrDefault(sheet);
}
