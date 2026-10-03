namespace Harmonia.Runtime;

// Text that stays in the game's language this session although the pack
// translates it: what other plugins compare with what the game shows. A sheet
// kept whole binds like a sheet the player keeps untranslated; a row is kept
// with every column; columns (by the game's column index) are kept in the
// listed rows, or in every row.
public sealed class KeptRows
{
    private readonly Dictionary<string, HashSet<uint>> rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<uint>> everyRowColumns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<uint, HashSet<uint>>> cells = new(StringComparer.Ordinal);

    public bool IsEmpty => rows.Count == 0 && sheets.Count == 0 && everyRowColumns.Count == 0 && cells.Count == 0;

    public void AddSheet(string sheet) => sheets.Add(sheet);

    public void AddRows(string sheet, IEnumerable<uint> ids)
    {
        if (!rows.TryGetValue(sheet, out var set))
            rows[sheet] = set = [];
        set.UnionWith(ids);
    }

    // ids null: every row of the sheet.
    public void AddColumns(string sheet, IEnumerable<uint> columns, IEnumerable<uint>? ids)
    {
        if (ids is null)
        {
            if (!everyRowColumns.TryGetValue(sheet, out var all))
                everyRowColumns[sheet] = all = [];
            all.UnionWith(columns);
            return;
        }

        if (!cells.TryGetValue(sheet, out var byRow))
            cells[sheet] = byRow = [];
        var list = columns.ToList();
        foreach (var id in ids)
        {
            if (!byRow.TryGetValue(id, out var set))
                byRow[id] = set = [];
            set.UnionWith(list);
        }
    }

    public bool KeepsSheet(string sheet) => sheets.Contains(sheet);

    public IReadOnlySet<uint>? RowsOf(string sheet) => rows.GetValueOrDefault(sheet);

    public IReadOnlySet<uint>? ColumnsOfEveryRow(string sheet) => everyRowColumns.GetValueOrDefault(sheet);

    public IReadOnlyDictionary<uint, HashSet<uint>>? CellsOf(string sheet) => cells.GetValueOrDefault(sheet);
}
