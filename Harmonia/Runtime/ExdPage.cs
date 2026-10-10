using System.Buffers;
using System.Buffers.Binary;

namespace Harmonia.Runtime;

// The text one cell shows. False keeps the string the page has.
public delegate bool ShownText(uint rowId, ushort subrowId, ushort ordinal, ReadOnlySpan<byte> source, out ReadOnlySpan<byte> text);

// A page of a sheet as the game files hold it (an .exd file): a 0x20 byte
// header, a table of (row id, offset) pairs, and the rows. A row is a 6 byte
// header (size, subrow count) and its data: the fixed part of the sheet's
// data offset bytes, or, in a subrow sheet, one (subrow id, fixed part) per
// subrow. A String field holds the distance from the end of its fixed part
// to its NUL-terminated text. Every number is big-endian.
//
// A translated page is the same page with the new strings added after its
// end and the fields of the translated cells pointing at them: no row moves,
// so whatever a reader already knows about the page stays true.
public static class ExdPage
{
    private const int HeaderSize = 0x20;
    private const int RowHeaderSize = 6;

    // The page with the strings `shown` gives, or null when it gives none or
    // the bytes are not a page. `replaced` counts the cells that changed.
    public static byte[]? Translate(
        ReadOnlySpan<byte> page, int dataOffset, bool subrows, ReadOnlySpan<StringColumn> columns, ShownText shown, out int replaced)
    {
        replaced = 0;
        if (page.Length < HeaderSize || dataOffset <= 0 || columns.Length == 0 ||
            page[0] != 'E' || page[1] != 'X' || page[2] != 'D' || page[3] != 'F')
            return null;

        var indexSize = BinaryPrimitives.ReadUInt32BigEndian(page[8..]);
        if (indexSize > (uint)(page.Length - HeaderSize))
            return null;

        ArrayBufferWriter<byte>? result = null;
        var fields = new List<(int Field, uint Value)>();
        for (var entry = HeaderSize; entry + 8 <= HeaderSize + (int)indexSize; entry += 8)
        {
            var rowId = BinaryPrimitives.ReadUInt32BigEndian(page[entry..]);
            long row = BinaryPrimitives.ReadUInt32BigEndian(page[(entry + 4)..]);
            if (row + RowHeaderSize > page.Length)
                continue;

            var count = subrows ? BinaryPrimitives.ReadUInt16BigEndian(page[((int)row + 4)..]) : 1;
            for (var i = 0; i < count; i++)
            {
                // Where the fixed part of this row or subrow starts.
                var start = row + RowHeaderSize + (subrows ? ((long)i * (dataOffset + 2)) + 2 : 0);
                if (start + dataOffset > page.Length)
                    break;

                var subrowId = subrows ? BinaryPrimitives.ReadUInt16BigEndian(page[((int)start - 2)..]) : (ushort)0;
                for (var ordinal = 0; ordinal < columns.Length; ordinal++)
                {
                    var offset = columns[ordinal].Offset;
                    if (offset + 4 > dataOffset)
                        continue;

                    var field = (int)start + offset;
                    var text = start + dataOffset + BinaryPrimitives.ReadUInt32BigEndian(page[field..]);
                    if (text >= page.Length)
                        continue;

                    var length = page[(int)text..].IndexOf((byte)0);
                    if (length < 0 || !shown(rowId, subrowId, (ushort)ordinal, page.Slice((int)text, length), out var translated))
                        continue;

                    if (result is null)
                    {
                        result = new ArrayBufferWriter<byte>(page.Length + (page.Length / 2));
                        result.Write(page);
                    }

                    fields.Add((field, (uint)(result.WrittenCount - start - dataOffset)));
                    result.Write(translated);
                    result.Write<byte>([0]);
                }
            }
        }

        if (result is null)
            return null;

        var bytes = result.WrittenSpan.ToArray();
        foreach (var (field, value) in fields)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(field), value);
        replaced = fields.Count;
        return bytes;
    }
}
