using Harmonia.Packs;
using Harmonia.Packs.Hpk;

namespace Harmonia.Runtime;

public enum CellDecision
{
    Applied,

    // The running game's source string differs from the one the translation
    // was made for; the original text stays.
    SourceChanged,

    // Another plugin looks for the original text (KeptRows).
    Kept,
}

public sealed record PackRuntimeInfo(string PackId, string Title, string FilePath, int Sheets, int Cells);

public readonly record struct SheetStats(
    string SheetName,
    long Applied,
    long SourceChanged,
    long RowsRebuilt,
    long RowsUnexpected,
    bool LayoutMismatch);

public readonly record struct RuntimeTotals(
    long Applied,
    long SourceChanged,
    long RowsRebuilt,
    long RowsUnexpected,
    int LayoutMismatchSheets,
    long Kept)
{
    // Share of guarded cells whose source still matched; null before any.
    public double? MatchRate => Applied + SourceChanged == 0 ? null : (double)Applied / (Applied + SourceChanged);
}

// The pack of this session and what happened to it. Row hooks call it from
// game threads, so counters are atomic and everything else is read-only.
public sealed unsafe class TranslationRuntime : IDisposable
{
    private readonly HpkFile pack;
    private readonly bool[] untranslated;
    private readonly IReadOnlySet<uint>?[] keptRows;
    private readonly HashSet<ushort>?[] keptOrdinals;
    private readonly Dictionary<uint, HashSet<ushort>>?[] keptCells;
    private readonly long[] kept;
    private readonly long[] applied;
    private readonly long[] changed;
    private readonly long[] rebuilt;
    private readonly long[] unexpected;
    private readonly int[] layoutMismatch;

    // packId is the installed translation's name in the pack store; the
    // sheets the filter excludes and the kept rows stay in the game's language.
    public TranslationRuntime(HpkFile pack, string packId, string filePath, SheetFilter? untranslatedSheets = null,
        KeptRows? keep = null)
    {
        if (pack.Mode != HpkOpenMode.Full)
            throw new ArgumentException("Runtime packs must be fully verified.", nameof(pack));

        this.pack = pack;
        var sheets = pack.SheetCount;
        applied = new long[sheets];
        changed = new long[sheets];
        rebuilt = new long[sheets];
        unexpected = new long[sheets];
        layoutMismatch = new int[sheets];
        kept = new long[sheets];
        untranslated = new bool[sheets];
        keptRows = new IReadOnlySet<uint>?[sheets];
        keptOrdinals = new HashSet<ushort>?[sheets];
        keptCells = new Dictionary<uint, HashSet<ushort>>?[sheets];
        for (var i = 0; i < sheets; i++)
        {
            var name = pack.SheetNames[i];
            var byPlayer = untranslatedSheets?.Excludes(name) == true;
            if (byPlayer)
                UntranslatedSheets++;
            untranslated[i] = byPlayer || keep?.KeepsSheet(name) == true;
            keptRows[i] = keep?.RowsOf(name);
            if (keep?.ColumnsOfEveryRow(name) is { } columns)
                keptOrdinals[i] = Ordinals(i, columns);
            if (keep?.CellsOf(name) is { } cells)
                keptCells[i] = cells.ToDictionary(static c => c.Key, c => Ordinals(i, c.Value));
        }

        Info = new PackRuntimeInfo(packId, pack.Manifest.Title, filePath, pack.SheetCount, pack.CellCount);
    }

    public PackRuntimeInfo Info { get; }

    // Pack sheets the player keeps in the game's language this session.
    public int UntranslatedSheets { get; }

    // Pack sheet index when the running sheet's String columns equal the pack
    // layout exactly (count, index, offset) and the variant matches, else -1.
    public int BindSheet(string sheetName, bool multiRow, ReadOnlySpan<StringColumn> columns)
    {
        if (!pack.TryGetSheet(sheetName, out var sheet) || untranslated[sheet])
            return -1;

        var layout = pack.GetLayout(sheet);
        var matches = pack.IsSubrowSheet(sheet) == multiRow && layout.Length == columns.Length;
        for (var i = 0; matches && i < layout.Length; i++)
            matches = layout[i].ColumnIndex == columns[i].Index && layout[i].Offset == columns[i].Offset;

        if (matches)
            return sheet;

        Volatile.Write(ref layoutMismatch[sheet], 1);
        return -1;
    }

    // Every subrow of a kept row stays as the game has it.
    public bool TryGetRow(int sheet, uint rowId, ushort subrowId, out HpkRow row)
    {
        if (!pack.TryGetRow(sheet, rowId, subrowId, out row))
            return false;
        if (keptRows[sheet] is not { } keptIds || !keptIds.Contains(rowId))
            return true;

        Interlocked.Add(ref kept[sheet], row.CellCount);
        row = default;
        return false;
    }

    public HpkCell GetCell(HpkRow row, int index, out ushort ordinal) => pack.GetCell(row, index, out ordinal);

    // ordinal is the cell's position among the sheet's String columns.
    public CellDecision Decide(int sheet, uint rowId, ushort ordinal, HpkCell cell, ReadOnlySpan<byte> source)
    {
        if (keptOrdinals[sheet]?.Contains(ordinal) == true ||
            (keptCells[sheet] is { } cells && cells.TryGetValue(rowId, out var ordinals) && ordinals.Contains(ordinal)))
        {
            Interlocked.Increment(ref kept[sheet]);
            return CellDecision.Kept;
        }

        if (SourceGuard.Compute(source) != cell.SourceGuard)
        {
            Interlocked.Increment(ref changed[sheet]);
            return CellDecision.SourceChanged;
        }

        Interlocked.Increment(ref applied[sheet]);
        return CellDecision.Applied;
    }

    // Positions in the pack layout of the given game column indexes.
    private HashSet<ushort> Ordinals(int sheet, IEnumerable<uint> columns)
    {
        var layout = pack.GetLayout(sheet);
        var result = new HashSet<ushort>();
        foreach (var column in columns)
        {
            var ordinal = Array.FindIndex(layout, l => l.ColumnIndex == column);
            if (ordinal >= 0)
                result.Add((ushort)ordinal);
        }

        return result;
    }

    public void CountRebuilt(int sheet) => Interlocked.Increment(ref rebuilt[sheet]);

    // A row whose buffer was not in the expected layout was left untouched.
    public void CountUnexpected(int sheet) => Interlocked.Increment(ref unexpected[sheet]);

    public RuntimeTotals GetTotals()
    {
        long a = 0, c = 0, r = 0, x = 0, k = 0;
        var mismatched = 0;
        for (var i = 0; i < applied.Length; i++)
        {
            a += Interlocked.Read(ref applied[i]);
            c += Interlocked.Read(ref changed[i]);
            r += Interlocked.Read(ref rebuilt[i]);
            x += Interlocked.Read(ref unexpected[i]);
            k += Interlocked.Read(ref kept[i]);
            mismatched += Volatile.Read(ref layoutMismatch[i]);
        }

        return new RuntimeTotals(a, c, r, x, mismatched, k);
    }

    // Sheets that were touched at runtime, in pack order.
    public IReadOnlyList<SheetStats> GetSheetStats()
    {
        var result = new List<SheetStats>();
        for (var i = 0; i < applied.Length; i++)
        {
            var stats = new SheetStats(
                pack.SheetNames[i],
                Interlocked.Read(ref applied[i]),
                Interlocked.Read(ref changed[i]),
                Interlocked.Read(ref rebuilt[i]),
                Interlocked.Read(ref unexpected[i]),
                Volatile.Read(ref layoutMismatch[i]) != 0);
            if (stats.Applied + stats.SourceChanged + stats.RowsUnexpected != 0 || stats.LayoutMismatch)
                result.Add(stats);
        }

        return result;
    }

    // Only after the row hooks are gone: translations are read from the mapping.
    public void Dispose() => pack.Dispose();
}
