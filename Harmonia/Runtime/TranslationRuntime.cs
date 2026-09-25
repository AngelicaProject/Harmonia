using Harmonia.Packs.Hpk;

namespace Harmonia.Runtime;

public enum CellDecision
{
    Applied,

    // The running game's source string differs from the one the translation
    // was made for; the original text stays.
    SourceChanged,

    // Exported as unreviewed while the player turned those off.
    Unreviewed,
}

public sealed record PackRuntimeInfo(string PackId, string Title, string FilePath, int Sheets, int Cells);

public readonly record struct SheetStats(
    string SheetName,
    long Applied,
    long SourceChanged,
    long Unreviewed,
    long RowsRebuilt,
    long RowsUnexpected,
    bool LayoutMismatch);

public readonly record struct RuntimeTotals(
    long Applied,
    long SourceChanged,
    long Unreviewed,
    long RowsRebuilt,
    long RowsUnexpected,
    int LayoutMismatchSheets)
{
    // Share of guarded cells whose source still matched; null before any.
    public double? MatchRate => Applied + SourceChanged == 0 ? null : (double)Applied / (Applied + SourceChanged);
}

// The pack of this session and what happened to it. Row hooks call it from
// game threads, so counters are atomic and everything else is read-only.
public sealed unsafe class TranslationRuntime : IDisposable
{
    private readonly HpkFile pack;
    private readonly bool applyUnreviewed;
    private readonly long[] applied;
    private readonly long[] changed;
    private readonly long[] unreviewed;
    private readonly long[] rebuilt;
    private readonly long[] unexpected;
    private readonly int[] layoutMismatch;

    public TranslationRuntime(HpkFile pack, bool applyUnreviewed, string filePath)
    {
        if (pack.Mode != HpkOpenMode.Full)
            throw new ArgumentException("Runtime packs must be fully verified.", nameof(pack));

        this.pack = pack;
        this.applyUnreviewed = applyUnreviewed;
        var sheets = pack.SheetCount;
        applied = new long[sheets];
        changed = new long[sheets];
        unreviewed = new long[sheets];
        rebuilt = new long[sheets];
        unexpected = new long[sheets];
        layoutMismatch = new int[sheets];
        Info = new PackRuntimeInfo(pack.Manifest.PackId, pack.Manifest.Title, filePath, pack.SheetCount, pack.CellCount);
    }

    public PackRuntimeInfo Info { get; }

    // Pack sheet index when the running sheet's String columns equal the pack
    // layout exactly (count, index, offset) and the variant matches, else -1.
    public int BindSheet(string sheetName, bool multiRow, ReadOnlySpan<StringColumn> columns)
    {
        if (!pack.TryGetSheet(sheetName, out var sheet))
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

    public bool TryGetRow(int sheet, uint rowId, ushort subrowId, out HpkRow row) =>
        pack.TryGetRow(sheet, rowId, subrowId, out row);

    public HpkCell GetCell(HpkRow row, int index, out ushort ordinal) => pack.GetCell(row, index, out ordinal);

    public CellDecision Decide(int sheet, HpkCell cell, ReadOnlySpan<byte> source)
    {
        if (SourceGuard.Compute(source) != cell.SourceGuard)
        {
            Interlocked.Increment(ref changed[sheet]);
            return CellDecision.SourceChanged;
        }

        if (cell.State != HpkFormat.StateReviewed && !applyUnreviewed)
        {
            Interlocked.Increment(ref unreviewed[sheet]);
            return CellDecision.Unreviewed;
        }

        Interlocked.Increment(ref applied[sheet]);
        return CellDecision.Applied;
    }

    public void CountRebuilt(int sheet) => Interlocked.Increment(ref rebuilt[sheet]);

    // A row whose buffer was not in the expected layout was left untouched.
    public void CountUnexpected(int sheet) => Interlocked.Increment(ref unexpected[sheet]);

    public RuntimeTotals GetTotals()
    {
        long a = 0, c = 0, u = 0, r = 0, x = 0;
        var mismatched = 0;
        for (var i = 0; i < applied.Length; i++)
        {
            a += Interlocked.Read(ref applied[i]);
            c += Interlocked.Read(ref changed[i]);
            u += Interlocked.Read(ref unreviewed[i]);
            r += Interlocked.Read(ref rebuilt[i]);
            x += Interlocked.Read(ref unexpected[i]);
            mismatched += Volatile.Read(ref layoutMismatch[i]);
        }

        return new RuntimeTotals(a, c, u, r, x, mismatched);
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
                Interlocked.Read(ref unreviewed[i]),
                Interlocked.Read(ref rebuilt[i]),
                Interlocked.Read(ref unexpected[i]),
                Volatile.Read(ref layoutMismatch[i]) != 0);
            if (stats.Applied + stats.SourceChanged + stats.Unreviewed + stats.RowsUnexpected != 0 || stats.LayoutMismatch)
                result.Add(stats);
        }

        return result;
    }

    // Only after the row hooks are gone: translations are read from the mapping.
    public void Dispose() => pack.Dispose();
}
