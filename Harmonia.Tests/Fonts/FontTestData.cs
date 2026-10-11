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

    // A 0x1440 texture whose listed channels have pixels down to a row.
    public static byte[] TextureRows(int size, params (int Channel, int Rows)[] used)
    {
        var bytes = Texture(size);
        int[] shifts = [8, 4, 0, 12];
        foreach (var (channel, rows) in used)
        {
            var at = bytes.AsSpan(FontTexture.HeaderSize + ((rows - 1) * size * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(at) | (0x7 << shifts[channel])));
        }

        return bytes;
    }

    public const ulong ImageBase = 0x140000000;
    public const int ImageTables = 0x400;
    public const int ImageNames = 0x800;

    // A game image with the part of the font reload function that chooses
    // and walks the font tables: at +0x20 the choice (the standard and the
    // high-resolution title screen tables, then the in-game one), at +0x60
    // the loop. Every table lists AXIS_12 and Jupiter_16 with `textures` of
    // its own, in the order in-game, standard, high resolution.
    public static (byte[] Image, int Function) GameImage(int records, int[] textures)
    {
        const int Function = 0x100;
        var image = new byte[0x1000];
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextName = ImageNames;
        ulong Name(string text)
        {
            if (!names.TryGetValue(text, out var at))
            {
                at = nextName;
                Encoding.ASCII.GetBytes(text, image.AsSpan(at));
                nextName += text.Length + 1;
                names[text] = at;
            }

            return ImageBase + (ulong)at;
        }

        string[] fonts = ["AXIS_12", "Jupiter_16"];
        var tables = new int[3];
        for (var table = 0; table < 3; table++)
        {
            tables[table] = ImageTables + (table * records * 0x18);
            for (var i = 0; i < records; i++)
            {
                var record = image.AsSpan(tables[table] + (i * 0x18));
                BinaryPrimitives.WriteInt32LittleEndian(record, textures[table]);
                BinaryPrimitives.WriteUInt64LittleEndian(record[8..], Name(table == 0 ? "font%d.tex" : "font_lobby%d.tex"));
                BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], Name(fonts[i] + (table == 0 ? ".fdt" : "_lobby.fdt")));
            }
        }

        var code = image.AsSpan(Function + 0x20);
        new byte[] { 0x40, 0x80, 0xFE, 0x01, 0x75, 0x1C, 0x41, 0x83, 0xBD, 0x80, 0x02, 0x00, 0x00, 0x01 }.CopyTo(code);
        Lea(code, 14, 0x35, tables[1]);
        Lea(code, 21, 0x05, tables[2]);
        new byte[] { 0x48, 0x0F, 0x45, 0xF0, 0xEB, 0x07 }.CopyTo(code[28..]);
        Lea(code, 34, 0x35, tables[0]);
        new byte[] { 0x41, 0xFF, 0xC4, 0x48, 0x83, 0xC6, 0x18, 0x41, 0x83, 0xFC, (byte)records, 0x0F, 0x8C }.CopyTo(image.AsSpan(Function + 0x60));
        return (image, Function);

        // lea reg, [rip + target]
        static void Lea(Span<byte> at, int offset, byte register, int target)
        {
            at[offset] = 0x48;
            at[offset + 1] = 0x8D;
            at[offset + 2] = register;
            BinaryPrimitives.WriteInt32LittleEndian(at[(offset + 3)..], target - (Function + 0x20 + offset + 7));
        }
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
