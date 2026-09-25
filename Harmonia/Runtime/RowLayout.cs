namespace Harmonia.Runtime;

// A String column of a running sheet: its position among all columns and the
// byte offset of its field in the fixed part of a row.
public readonly record struct StringColumn(ushort Index, ushort Offset);

// One string of a parsed row: the field that points to it and its text.
public readonly record struct RowString(int FieldOffset, int Start, int Length);

// Text that replaces one string of a row. Null text keeps the original.
public readonly unsafe struct Replacement(byte* text, int length)
{
    public byte* Text { get; } = text;
    public int Length { get; } = length;
    public bool IsSet => Text != null;
}

// The row buffer of a version 3 sheet as ExcelRow_Parse_v3 builds it: the
// fixed part of sheet->DataOffset bytes, then one NUL-terminated string per
// String column, in column order and with nothing in between. A String field
// holds the distance from the field to its text in the low 24 bits and a hash
// of the text in the high byte. Rows are rebuilt in exactly this form, so the
// game cannot tell a translated row from one it parsed itself.
public static unsafe class RowLayout
{
    public const uint OffsetMask = 0xFFFFFF;

    // Far below the 24-bit field offset limit; real rows are a few KiB.
    public const int MaxRowBytes = 1 << 20;

    // Reads the strings of a row. False when the buffer is not in the layout
    // above; such a row is left untouched.
    public static bool TryRead(byte* data, int dataOffset, ReadOnlySpan<StringColumn> columns, Span<RowString> strings)
    {
        if (data == null || dataOffset <= 0 || dataOffset > MaxRowBytes || strings.Length < columns.Length)
            return false;

        var cursor = dataOffset;
        for (var i = 0; i < columns.Length; i++)
        {
            var field = columns[i].Offset;
            if (field + 4 > dataOffset)
                return false;
            if (field + (*(uint*)(data + field) & OffsetMask) != cursor)
                return false;

            var length = 0;
            while (data[cursor + length] != 0)
            {
                if (++length + cursor >= MaxRowBytes)
                    return false;
            }

            strings[i] = new RowString(field, cursor, length);
            cursor += length + 1;
        }

        return true;
    }

    // Size of the rebuilt buffer, or -1 when it would exceed MaxRowBytes.
    public static int Measure(int dataOffset, ReadOnlySpan<RowString> strings, ReadOnlySpan<Replacement> replacements)
    {
        long size = dataOffset;
        for (var i = 0; i < strings.Length; i++)
            size += (replacements[i].IsSet ? replacements[i].Length : strings[i].Length) + 1;

        return size > MaxRowBytes ? -1 : (int)size;
    }

    // Writes the rebuilt row into target, which holds Measure() bytes.
    // Untouched strings keep their original hash byte: the game hashes the
    // resolved text of "_rsv_" strings, which cannot be recomputed here.
    public static void Write(
        byte* source,
        byte* target,
        int dataOffset,
        ReadOnlySpan<RowString> strings,
        ReadOnlySpan<Replacement> replacements)
    {
        Buffer.MemoryCopy(source, target, dataOffset, dataOffset);
        var cursor = dataOffset;
        for (var i = 0; i < strings.Length; i++)
        {
            var original = strings[i];
            var replacement = replacements[i];
            var text = replacement.IsSet ? replacement.Text : source + original.Start;
            var length = replacement.IsSet ? replacement.Length : original.Length;

            Buffer.MemoryCopy(text, target + cursor, length, length);
            target[cursor + length] = 0;

            var field = target + original.FieldOffset;
            *(uint*)field = (uint)(cursor - original.FieldOffset);
            field[3] = replacement.IsSet ? Hash(text, length) : source[original.FieldOffset + 3];
            cursor += length + 1;
        }
    }

    // ExcelRow_CalculateStringColumnHash for plain text.
    public static byte Hash(byte* text, int length)
    {
        byte hash = 0;
        for (var i = 0; i < length; i++)
            hash = (byte)(text[i] ^ (hash << 1));
        return hash;
    }
}
