using System.Buffers.Binary;
using System.Text;

namespace Harmonia.Fonts;

// One glyph record of a game .fdt file. Utf8 is the character's UTF-8 bytes
// packed big-endian into a u32 ('A' = 0x41, 'А' = 0xD090), so record order by
// Utf8 is codepoint order.
public readonly record struct FdtGlyph(
    uint Utf8,
    ushort ShiftJis,
    ushort TexIndex,
    ushort X,
    ushort Y,
    byte Width,
    byte Height,
    sbyte NextOffsetX,
    sbyte OffsetY)
{
    public const int Size = 16;
}

// A game font table (common/font/*.fdt):
//   0x00 "fcsv0100", u32 fthd offset, u32 knhd offset, zero padding to 0x20
//   fthd (0x20 bytes): "fthd", u32 glyphCount, u32 kerningCount, u32 pad,
//        u16 texture width, u16 texture height, f32 size, i32 lineHeight, i32 ascent
//   glyphCount × 16-byte records sorted by Utf8
//   knhd: "knhd", u32 count, 8 bytes pad, count × 16-byte kerning pairs
// Only glyph records are ever added; everything else is written back as read.
public sealed class FdtFile
{
    private const int FileHeaderSize = 0x20;
    private const int FontHeaderSize = 0x20;
    private const int KerningHeaderSize = 0x10;
    private const int KerningEntrySize = 16;

    private readonly byte[] fileHeader;
    private readonly byte[] fontHeader;
    private readonly List<FdtGlyph> glyphs;
    private readonly byte[] kerning;

    private FdtFile(byte[] fileHeader, byte[] fontHeader, List<FdtGlyph> glyphs, byte[] kerning)
    {
        this.fileHeader = fileHeader;
        this.fontHeader = fontHeader;
        this.glyphs = glyphs;
        this.kerning = kerning;
    }

    public static ReadOnlySpan<byte> Magic => "fcsv0100"u8;

    public IReadOnlyList<FdtGlyph> Glyphs => glyphs;

    public int LineHeight => BinaryPrimitives.ReadInt32LittleEndian(fontHeader.AsSpan(0x18));

    public int Ascent => BinaryPrimitives.ReadInt32LittleEndian(fontHeader.AsSpan(0x1C));

    public int KerningCount => BinaryPrimitives.ReadInt32LittleEndian(kerning.AsSpan(4));

    public static FdtFile Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < FileHeaderSize + FontHeaderSize + KerningHeaderSize || !bytes[..8].SequenceEqual(Magic))
            throw new FormatException("Not an fcsv0100 font table.");

        var fthd = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var knhd = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (fthd != FileHeaderSize || !bytes.Slice(fthd, 4).SequenceEqual("fthd"u8))
            throw new FormatException("Unexpected fthd header.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(fthd + 4)..]);
        var kerningCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(fthd + 8)..]);
        if (count > 0x10000 || knhd != fthd + FontHeaderSize + ((int)count * FdtGlyph.Size) ||
            knhd + KerningHeaderSize > bytes.Length || !bytes.Slice(knhd, 4).SequenceEqual("knhd"u8))
            throw new FormatException("Unexpected knhd header.");

        var pairs = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(knhd + 4)..]);
        if (pairs != kerningCount || bytes.Length != knhd + KerningHeaderSize + ((long)pairs * KerningEntrySize))
            throw new FormatException("Kerning table does not match the header.");

        var list = new List<FdtGlyph>((int)count);
        for (var i = 0; i < count; i++)
        {
            var at = bytes.Slice(fthd + FontHeaderSize + (i * FdtGlyph.Size), FdtGlyph.Size);
            var glyph = new FdtGlyph(
                BinaryPrimitives.ReadUInt32LittleEndian(at),
                BinaryPrimitives.ReadUInt16LittleEndian(at[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(at[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(at[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(at[10..]),
                at[12],
                at[13],
                (sbyte)at[14],
                (sbyte)at[15]);
            // The game's AXIS tables hold the space twice; equal neighbours
            // are kept and written back as read.
            if (list.Count > 0 && list[^1].Utf8 > glyph.Utf8)
                throw new FormatException("Glyph records are not sorted.");
            list.Add(glyph);
        }

        return new FdtFile(
            bytes[..FileHeaderSize].ToArray(),
            bytes.Slice(fthd, FontHeaderSize).ToArray(),
            list,
            bytes[knhd..].ToArray());
    }

    public static uint PackUtf8(uint codepoint)
    {
        Span<byte> utf8 = stackalloc byte[4];
        var length = Encoding.UTF8.GetBytes(char.ConvertFromUtf32((int)codepoint), utf8);
        uint packed = 0;
        for (var i = 0; i < length; i++)
            packed = (packed << 8) | utf8[i];
        return packed;
    }

    public bool Contains(uint utf8) => Find(utf8) >= 0;

    public bool TryGet(uint utf8, out FdtGlyph glyph)
    {
        var index = Find(utf8);
        glyph = index >= 0 ? glyphs[index] : default;
        return index >= 0;
    }

    // Inserts a glyph in Utf8 order. Existing glyphs are never replaced.
    public void Add(FdtGlyph glyph)
    {
        var index = Find(glyph.Utf8);
        if (index >= 0)
            throw new InvalidOperationException($"The font already has glyph 0x{glyph.Utf8:X}.");
        glyphs.Insert(~index, glyph);
    }

    // Replaces the record of a glyph the font has. The pack's replacement
    // glyphs use it; the old bitmap stays in the atlas, unused.
    public void Replace(FdtGlyph glyph)
    {
        var index = Find(glyph.Utf8);
        if (index < 0)
            throw new InvalidOperationException($"The font has no glyph 0x{glyph.Utf8:X}.");
        glyphs[index] = glyph;
    }

    public byte[] Write()
    {
        var knhd = FileHeaderSize + FontHeaderSize + (glyphs.Count * FdtGlyph.Size);
        var bytes = new byte[knhd + kerning.Length];
        fileHeader.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)knhd);
        fontHeader.CopyTo(bytes, FileHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(FileHeaderSize + 4), (uint)glyphs.Count);
        for (var i = 0; i < glyphs.Count; i++)
        {
            var glyph = glyphs[i];
            var at = bytes.AsSpan(FileHeaderSize + FontHeaderSize + (i * FdtGlyph.Size), FdtGlyph.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(at, glyph.Utf8);
            BinaryPrimitives.WriteUInt16LittleEndian(at[4..], glyph.ShiftJis);
            BinaryPrimitives.WriteUInt16LittleEndian(at[6..], glyph.TexIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(at[8..], glyph.X);
            BinaryPrimitives.WriteUInt16LittleEndian(at[10..], glyph.Y);
            at[12] = glyph.Width;
            at[13] = glyph.Height;
            at[14] = (byte)glyph.NextOffsetX;
            at[15] = (byte)glyph.OffsetY;
        }

        kerning.CopyTo(bytes, knhd);
        return bytes;
    }

    private int Find(uint utf8)
    {
        int low = 0, high = glyphs.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var value = glyphs[mid].Utf8;
            if (value < utf8)
                low = mid + 1;
            else if (value > utf8)
                high = mid - 1;
            else
                return mid;
        }

        return ~low;
    }
}
