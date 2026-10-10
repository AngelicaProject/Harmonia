using System.Buffers.Binary;
using System.Text;
using Harmonia.Runtime;
using Xunit;

namespace Harmonia.Tests.Runtime;

public sealed class ExdPageTests
{
    // Fixed part: an int at 0 and String fields at 4 and 8.
    private const int DataOffset = 12;
    private static readonly StringColumn[] Columns = [new(1, 4), new(3, 8)];

    // A page as the game files hold it; a row is its id and one pair of
    // strings per subrow.
    private static byte[] Page(bool subrows, params (uint Id, (string A, string B)[] Subrows)[] rows)
    {
        var body = new List<byte>();
        var index = new List<(uint Id, int Offset)>();
        var start = 0x20 + (rows.Length * 8);
        foreach (var (id, parts) in rows)
        {
            index.Add((id, start + body.Count));
            var data = new List<byte>();
            var strings = new List<byte>();
            for (var i = 0; i < parts.Length; i++)
            {
                if (subrows)
                    data.AddRange(Big16((ushort)(i + 5)));
                var first = strings.Count;
                strings.AddRange(Encoding.UTF8.GetBytes(parts[i].A));
                strings.Add(0);
                var second = strings.Count;
                strings.AddRange(Encoding.UTF8.GetBytes(parts[i].B));
                strings.Add(0);

                // In a subrow sheet the strings of every subrow follow the
                // last fixed part; a field counts from the end of its own.
                var behind = subrows ? (parts.Length - 1 - i) * (DataOffset + 2) : 0;
                data.AddRange(Big32(0x11223344));
                data.AddRange(Big32((uint)(behind + first)));
                data.AddRange(Big32((uint)(behind + second)));
            }

            body.AddRange(Big32((uint)(data.Count + strings.Count)));
            body.AddRange(Big16((ushort)parts.Length));
            body.AddRange(data);
            body.AddRange(strings);
        }

        var page = new List<byte>("EXDF"u8.ToArray());
        page.AddRange(Big16(2));
        page.AddRange(Big16(0));
        page.AddRange(Big32((uint)(rows.Length * 8)));
        page.AddRange(new byte[0x20 - page.Count]);
        foreach (var (id, offset) in index)
        {
            page.AddRange(Big32(id));
            page.AddRange(Big32((uint)offset));
        }

        page.AddRange(body);
        return [.. page];
    }

    private static byte[] Big32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Big16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    // Reads a string the way a reader of the game files does.
    private static string Text(byte[] page, bool subrows, uint rowId, int subrow, int column)
    {
        for (var entry = 0x20; ; entry += 8)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(entry)) != rowId)
                continue;

            var row = (int)BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(entry + 4)) + 6;
            var start = row + (subrows ? (subrow * (DataOffset + 2)) + 2 : 0);
            var text = start + DataOffset + (int)BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(start + Columns[column].Offset));
            return Encoding.UTF8.GetString(page, text, Array.IndexOf(page, (byte)0, text) - text);
        }
    }

    private static ShownText Replace(params (uint Row, ushort Subrow, ushort Ordinal, string Text)[] cells) =>
        (uint rowId, ushort subrowId, ushort ordinal, ReadOnlySpan<byte> _, out ReadOnlySpan<byte> text) =>
        {
            foreach (var cell in cells)
            {
                if (cell.Row == rowId && cell.Subrow == subrowId && cell.Ordinal == ordinal)
                {
                    text = Encoding.UTF8.GetBytes(cell.Text);
                    return true;
                }
            }

            text = default;
            return false;
        };

    [Fact]
    public void Translated_cells_read_as_the_new_text_and_the_rest_as_before()
    {
        var page = Page(false, (1, [("Hello", "World")]), (7, [("Yes", "No")]));

        var translated = ExdPage.Translate(page, DataOffset, false, Columns, Replace((1, 0, 1, "Мир"), (7, 0, 0, "Да")), out var replaced);

        Assert.NotNull(translated);
        Assert.Equal(2, replaced);
        Assert.Equal("Hello", Text(translated, false, 1, 0, 0));
        Assert.Equal("Мир", Text(translated, false, 1, 0, 1));
        Assert.Equal("Да", Text(translated, false, 7, 0, 0));
        Assert.Equal("No", Text(translated, false, 7, 0, 1));

        // No row moved: the page is the old one with strings after its end.
        Assert.Equal(page.Length + "Мир\0Да\0"u8.Length, translated.Length);
        Assert.Equal("Hello", Text(page, false, 1, 0, 0));
    }

    [Fact]
    public void Subrows_are_found_by_their_own_ids()
    {
        var page = Page(true, (3, [("First", "A"), ("Second", "B")]));
        var seen = new List<(uint, ushort, ushort, string)>();
        ShownText shown = (uint rowId, ushort subrowId, ushort ordinal, ReadOnlySpan<byte> source, out ReadOnlySpan<byte> text) =>
        {
            seen.Add((rowId, subrowId, ordinal, Encoding.UTF8.GetString(source)));
            text = "Вторая"u8;
            return subrowId == 6 && ordinal == 0;
        };

        var translated = ExdPage.Translate(page, DataOffset, true, Columns, shown, out var replaced);

        Assert.Equal([(3u, (ushort)5, (ushort)0, "First"), (3u, (ushort)5, (ushort)1, "A"), (3u, (ushort)6, (ushort)0, "Second"), (3u, (ushort)6, (ushort)1, "B")], seen);
        Assert.NotNull(translated);
        Assert.Equal(1, replaced);
        Assert.Equal("First", Text(translated, true, 3, 0, 0));
        Assert.Equal("Вторая", Text(translated, true, 3, 1, 0));
        Assert.Equal("B", Text(translated, true, 3, 1, 1));
    }

    [Fact]
    public void A_page_with_nothing_to_change_or_that_is_not_a_page_stays()
    {
        var page = Page(false, (1, [("Hello", "World")]));

        Assert.Null(ExdPage.Translate(page, DataOffset, false, Columns, Replace(), out var replaced));
        Assert.Equal(0, replaced);
        Assert.Null(ExdPage.Translate(new byte[64], DataOffset, false, Columns, Replace((1, 0, 0, "x")), out _));
        Assert.Null(ExdPage.Translate(page.AsSpan(0, 16), DataOffset, false, Columns, Replace((1, 0, 0, "x")), out _));

        // A row that points past the page is skipped, not read.
        var cut = page.AsSpan(0, page.Length - 8).ToArray();
        Assert.Null(ExdPage.Translate(cut, DataOffset, false, Columns, Replace((1, 0, 0, "x")), out _));
    }
}
