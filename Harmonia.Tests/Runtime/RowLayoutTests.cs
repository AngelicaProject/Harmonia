using System.Buffers.Binary;
using System.Text;
using Harmonia.Runtime;
using Xunit;

namespace Harmonia.Tests.Runtime;

public sealed unsafe class RowLayoutTests
{
    // Fixed part: an int at 0 and String fields at 4 and 8; strings follow at 12.
    private const int DataOffset = 12;
    private static readonly StringColumn[] Columns = [new(1, 4), new(3, 8)];

    // A row as ExcelRow_Parse_v3 lays it out. The hash bytes are arbitrary so
    // tests can tell kept bytes from recomputed ones.
    private static byte[] Row(params string[] texts)
    {
        var buffer = new List<byte>(new byte[DataOffset]);
        BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer), 0x11223344);
        for (var i = 0; i < texts.Length; i++)
        {
            var field = Columns[i].Offset;
            var offset = (uint)(buffer.Count - field) | ((uint)(0xA0 + i) << 24);
            var bytes = BitConverter.GetBytes(offset);
            for (var b = 0; b < 4; b++)
                buffer[field + b] = bytes[b];
            buffer.AddRange(Encoding.UTF8.GetBytes(texts[i]));
            buffer.Add(0);
        }

        return buffer.ToArray();
    }

    private static string Text(byte[] row, int column)
    {
        var field = Columns[column].Offset;
        var start = field + (int)(BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(field)) & RowLayout.OffsetMask);
        var end = Array.IndexOf(row, (byte)0, start);
        return Encoding.UTF8.GetString(row, start, end - start);
    }

    [Fact]
    public void Strings_of_a_parsed_row_are_read()
    {
        var row = Row("Hello", "World");
        Span<RowString> strings = stackalloc RowString[2];
        fixed (byte* data = row)
            Assert.True(RowLayout.TryRead(data, DataOffset, Columns, strings));

        Assert.Equal(new RowString(4, 12, 5), strings[0]);
        Assert.Equal(new RowString(8, 18, 5), strings[1]);
    }

    [Fact]
    public void Rebuilt_row_has_the_game_layout_and_keeps_untouched_bytes()
    {
        var row = Row("Hello", "World");
        var translation = Encoding.UTF8.GetBytes("Мир");
        Span<RowString> strings = stackalloc RowString[2];
        byte[] rebuilt;
        fixed (byte* data = row)
        fixed (byte* text = translation)
        {
            Assert.True(RowLayout.TryRead(data, DataOffset, Columns, strings));
            Span<Replacement> replacements = [default, new Replacement(text, translation.Length)];
            var size = RowLayout.Measure(DataOffset, strings, replacements);
            Assert.Equal(DataOffset + 6 + translation.Length + 1, size);

            rebuilt = new byte[size];
            fixed (byte* target = rebuilt)
                RowLayout.Write(data, target, DataOffset, strings, replacements);

            Assert.Equal(RowLayout.Hash(text, translation.Length), rebuilt[8 + 3]);
        }

        Assert.Equal(0x11223344, BinaryPrimitives.ReadInt32LittleEndian(rebuilt));
        Assert.Equal("Hello", Text(rebuilt, 0));
        Assert.Equal("Мир", Text(rebuilt, 1));
        Assert.Equal(0xA0, rebuilt[4 + 3]);
        Assert.Equal(0, rebuilt[^1]);

        // The rebuilt row parses like one the game built.
        Span<RowString> reread = stackalloc RowString[2];
        fixed (byte* data = rebuilt)
            Assert.True(RowLayout.TryRead(data, DataOffset, Columns, reread));
        Assert.Equal(translation.Length, reread[1].Length);
    }

    [Fact]
    public void Rows_in_another_layout_are_rejected()
    {
        Span<RowString> strings = stackalloc RowString[2];

        // A gap between the fixed part and the first string.
        var gap = Row("Hello", "World").ToList();
        gap.Insert(DataOffset, 0x20);
        var gapRow = gap.ToArray();
        fixed (byte* data = gapRow)
            Assert.False(RowLayout.TryRead(data, DataOffset, Columns, strings));

        // Strings stored in another order than their columns.
        var swapped = Row("Hello", "World");
        (swapped[4], swapped[8]) = ((byte)(swapped[8] + 4), (byte)(swapped[4] - 4));
        fixed (byte* data = swapped)
            Assert.False(RowLayout.TryRead(data, DataOffset, Columns, strings));

        // A String field outside the fixed part.
        var row = Row("Hello", "World");
        fixed (byte* data = row)
            Assert.False(RowLayout.TryRead(data, DataOffset, [new StringColumn(1, 10)], strings));
    }

    [Fact]
    public void Rows_that_would_grow_past_the_limit_are_not_rebuilt()
    {
        var row = Row("Hello", "World");
        Span<RowString> strings = stackalloc RowString[2];
        byte one = 1;
        fixed (byte* data = row)
            Assert.True(RowLayout.TryRead(data, DataOffset, Columns, strings));

        Span<Replacement> replacements = [new Replacement(&one, RowLayout.MaxRowBytes), default];
        Assert.Equal(-1, RowLayout.Measure(DataOffset, strings, replacements));
    }
}
