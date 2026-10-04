namespace Harmonia.Dictionary;

// A named row: the name as a typed sheet reads it, and the bytes of the row's
// String column at a given ordinal.
public readonly record struct NameColumnSample(ReadOnlyMemory<byte> Name, Func<int, ReadOnlyMemory<byte>> Column);

// Typed sheets know which column is the name, the pack knows String column
// ordinals. The name's ordinal is the String column that holds the same bytes
// in every sampled named row; the first, if two always agree.
public static class NameColumn
{
    public const int Samples = 200;

    // -1 when no column holds the name in every sample.
    public static int Find(int stringColumns, IEnumerable<NameColumnSample> rows)
    {
        var candidates = Enumerable.Range(0, stringColumns).ToList();
        var sampled = 0;
        foreach (var row in rows)
        {
            if (row.Name.IsEmpty)
                continue;

            candidates.RemoveAll(c => !row.Column(c).Span.SequenceEqual(row.Name.Span));
            // A single candidate is still checked: the name may be in no column.
            if (candidates.Count == 0 || ++sampled >= Samples)
                break;
        }

        return candidates.Count > 0 ? candidates[0] : -1;
    }
}
