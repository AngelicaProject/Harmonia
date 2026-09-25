using System.Buffers.Binary;
using System.Text;
using Harmonia.Fonts;
using Harmonia.Packs.Hpk;
using Newtonsoft.Json;

namespace Harmonia.Tests.Fonts;

internal sealed record TestGlyph(uint Codepoint, byte Width, byte Height, byte OffsetY, byte Advance, byte Fill = 255);

internal sealed record TestTarget(string Font, string Size, byte LineHeight, byte Ascent, TestGlyph[] Glyphs);

// Independent writers for game .fdt/.tex files and the pack FONTS section,
// following the documented layouts rather than sharing code with the reader.
internal static class FontTestData
{
    public static byte[] Fdt(int lineHeight, int ascent, (uint Utf8, ushort Page)[] glyphs, (uint Left, uint Right, int Offset)[] kerning)
    {
        var knhd = 0x40 + (glyphs.Length * 16);
        var bytes = new byte[knhd + 0x10 + (kerning.Length * 16)];
        "fcsv0100"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)knhd);
        "fthd"u8.CopyTo(bytes.AsSpan(0x20));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x24), (uint)glyphs.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x28), (uint)kerning.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x30), 1024);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x32), 1024);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x34), 16f);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x38), lineHeight);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), ascent);
        for (var i = 0; i < glyphs.Length; i++)
        {
            var at = bytes.AsSpan(0x40 + (i * 16));
            BinaryPrimitives.WriteUInt32LittleEndian(at, glyphs[i].Utf8);
            BinaryPrimitives.WriteUInt16LittleEndian(at[4..], (ushort)(0x8000 + i));
            BinaryPrimitives.WriteUInt16LittleEndian(at[6..], glyphs[i].Page);
            BinaryPrimitives.WriteUInt16LittleEndian(at[8..], (ushort)(i * 10));
            at[12] = 8;
            at[13] = (byte)lineHeight;
            at[14] = unchecked((byte)-1);
        }

        "knhd"u8.CopyTo(bytes.AsSpan(knhd));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(knhd + 4), (uint)kerning.Length);
        for (var i = 0; i < kerning.Length; i++)
        {
            var at = bytes.AsSpan(knhd + 0x10 + (i * 16));
            BinaryPrimitives.WriteUInt32LittleEndian(at, kerning[i].Left);
            BinaryPrimitives.WriteUInt32LittleEndian(at[4..], kerning[i].Right);
            BinaryPrimitives.WriteInt32LittleEndian(at[12..], kerning[i].Offset);
        }

        return bytes;
    }

    // A 0x1440 texture whose listed channels have one set pixel each.
    public static byte[] Texture(int size, params int[] usedChannels)
    {
        var bytes = new byte[FontTexture.HeaderSize + (size * size * 2)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), FontTexture.Format);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), FontTexture.HeaderSize);
        int[] shifts = [8, 4, 0, 12];
        ushort pixel = 0;
        foreach (var channel in usedChannels)
            pixel |= (ushort)(0x7 << shifts[channel]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(FontTexture.HeaderSize + (((size * size) - 1) * 2)), pixel);
        return bytes;
    }

    public static byte[] Section(params TestTarget[] targets)
    {
        var glyphs = new List<byte>();
        var bitmaps = new List<byte>();
        var targetJson = new List<object>();
        var start = 0;
        foreach (var target in targets)
        {
            foreach (var glyph in target.Glyphs)
            {
                var record = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(record, glyph.Codepoint);
                record[4] = glyph.Width;
                record[5] = glyph.Height;
                record[6] = glyph.OffsetY;
                record[7] = glyph.Advance;
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), (uint)bitmaps.Count);
                glyphs.AddRange(record);
                bitmaps.AddRange(Enumerable.Repeat(glyph.Fill, glyph.Width * glyph.Height));
            }

            targetJson.Add(new
            {
                font = target.Font,
                size = target.Size,
                lineHeight = target.LineHeight,
                ascent = target.Ascent,
                source = 0,
                glyphStart = start,
                glyphCount = target.Glyphs.Length,
            });
            start += target.Glyphs.Length;
        }

        var metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            sources = new[] { new { family = "Test", copyright = "Copyright Test", license = "OFL-1.1", licenseText = "License\n", sha256 = new string('a', 64) } },
            targets = targetJson,
        }, Formatting.Indented) + "\n");
        var header = new byte[32];
        "HPKFONT1"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(glyphs.Count / 16));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)bitmaps.Count);
        var bytes = new List<byte>(header);
        bytes.AddRange(metadata);
        while (bytes.Count % 8 != 0)
            bytes.Add(0);
        bytes.AddRange(glyphs);
        bytes.AddRange(bitmaps);
        return [.. bytes];
    }

    public static HpkFonts Fonts(params TestTarget[] targets) => HpkFonts.Parse(Section(targets));
}
