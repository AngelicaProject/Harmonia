using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Harmonia.Packs.Hpk;

public sealed record HpkFontSource(string Family, string Copyright, string License, string LicenseText, string Sha256);

public readonly record struct HpkFontGlyph(uint Codepoint, byte Width, byte Height, byte OffsetY, byte Advance, int BitmapOffset);

public sealed record HpkFontTarget(string Font, string Size, byte LineHeight, byte Ascent, int Source, HpkFontGlyph[] Glyphs)
{
    // The .fdt name without the "_lobby" suffix, e.g. "TrumpGothic_184".
    public string Name => Font + "_" + Size;
}

// The optional FONTS section (Aeria docs/formats/pack-v1.md, format minor 1):
// glyphs for game fonts that lack characters of the target language. Parsing
// checks every rule of the section, so a pack that loads has usable glyphs.
public sealed partial class HpkFonts
{
    private const int HeaderSize = 32;
    private const int GlyphRecordSize = 16;

    private HpkFonts(HpkFontSource[] sources, HpkFontTarget[] targets, byte[] bitmaps)
    {
        Sources = sources;
        Targets = targets;
        Bitmaps = bitmaps;
    }

    public IReadOnlyList<HpkFontSource> Sources { get; }
    public IReadOnlyList<HpkFontTarget> Targets { get; }
    public byte[] Bitmaps { get; }

    public static ReadOnlySpan<byte> Magic => "HPKFONT1"u8;

    public ReadOnlySpan<byte> Bitmap(HpkFontGlyph glyph) => Bitmaps.AsSpan(glyph.BitmapOffset, glyph.Width * glyph.Height);

    public static HpkFonts Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < HeaderSize || !section[..8].SequenceEqual(Magic) || section[20..HeaderSize].ContainsAnyExcept((byte)0))
            throw new HpkFormatException("Invalid FONTS header.");

        var metadataLength = (long)U32(section, 8);
        var glyphCount = (long)U32(section, 12);
        var bitmapLength = (long)U32(section, 16);
        var metadataEnd = HeaderSize + metadataLength;
        var glyphsStart = (metadataEnd + 7) & ~7L;
        var bitmapsStart = glyphsStart + (glyphCount * GlyphRecordSize);
        if (bitmapsStart + bitmapLength != section.Length || section[(int)metadataEnd..(int)glyphsStart].ContainsAnyExcept((byte)0))
            throw new HpkFormatException("FONTS section lengths do not match its header.");

        var root = ParseJson(section[HeaderSize..(int)metadataEnd]);
        HpkManifest.Fields(root, "FONTS metadata", "sources", "targets");
        if (root["sources"] is not JArray sourcesJson || root["targets"] is not JArray targetsJson || targetsJson.Count == 0)
            throw new HpkFormatException("FONTS metadata needs sources and at least one target.");

        var sources = new HpkFontSource[sourcesJson.Count];
        for (var i = 0; i < sources.Length; i++)
        {
            if (sourcesJson[i] is not JObject source)
                throw new HpkFormatException("A FONTS source must be an object.");

            HpkManifest.Fields(source, "FONTS source", "family", "copyright", "license", "licenseText", "sha256");
            var licenseText = HpkManifest.Str(source, "licenseText");
            var sha = HpkManifest.Str(source, "sha256");
            if (licenseText.Contains('\r') || sha.Length != 64 || !HexPattern().IsMatch(sha))
                throw new HpkFormatException("Invalid FONTS source.");

            sources[i] = new HpkFontSource(
                HpkManifest.Str(source, "family"), HpkManifest.Str(source, "copyright"), HpkManifest.Str(source, "license"), licenseText, sha);
        }

        var bitmaps = section[(int)bitmapsStart..].ToArray();
        var used = new bool[sources.Length];
        var targets = new HpkFontTarget[targetsJson.Count];
        long nextGlyph = 0;
        long nextBitmap = 0;
        for (var t = 0; t < targets.Length; t++)
        {
            if (targetsJson[t] is not JObject target)
                throw new HpkFormatException("A FONTS target must be an object.");

            HpkManifest.Fields(target, "FONTS target", "font", "size", "lineHeight", "ascent", "source", "glyphStart", "glyphCount");
            var font = HpkManifest.Str(target, "font");
            var size = HpkManifest.Str(target, "size");
            var lineHeight = HpkManifest.Int(target, "lineHeight");
            var ascent = HpkManifest.Int(target, "ascent");
            var sourceIndex = HpkManifest.Int(target, "source");
            var start = HpkManifest.Int(target, "glyphStart");
            var count = HpkManifest.Int(target, "glyphCount");
            var name = font + "_" + size;
            if (!FontPattern().IsMatch(font) || !SizePattern().IsMatch(size) || lineHeight is < 1 or > 255 ||
                ascent < 1 || ascent > lineHeight || sourceIndex >= sources.Length || count < 1 ||
                start != nextGlyph || start + count > glyphCount)
                throw new HpkFormatException($"Invalid FONTS target {name}.");

            if (t > 0)
            {
                var previous = targets[t - 1];
                var order = string.CompareOrdinal(previous.Font, font);
                if (order > 0 || (order == 0 && string.CompareOrdinal(previous.Size, size) >= 0))
                    throw new HpkFormatException("FONTS targets are not sorted and unique.");
            }

            used[sourceIndex] = true;
            var glyphs = new HpkFontGlyph[count];
            for (var g = 0; g < count; g++)
            {
                var at = (int)(glyphsStart + ((start + g) * GlyphRecordSize));
                var record = section.Slice(at, GlyphRecordSize);
                var glyph = new HpkFontGlyph(U32(record, 0), record[4], record[5], record[6], record[7], (int)U32(record, 8));
                var bytes = glyph.Width * glyph.Height;
                if (glyph.Codepoint < 0x80 || glyph.Codepoint > 0x10FFFF || glyph.Codepoint is >= 0xD800 and <= 0xDFFF ||
                    (g > 0 && glyphs[g - 1].Codepoint >= glyph.Codepoint) ||
                    glyph.OffsetY + glyph.Height > lineHeight ||
                    glyph.Advance - glyph.Width is < sbyte.MinValue or > sbyte.MaxValue ||
                    glyph.BitmapOffset != nextBitmap || U32(record, 12) != 0)
                    throw new HpkFormatException($"Invalid glyph U+{glyph.Codepoint:X4} in FONTS target {name}.");

                nextBitmap += bytes;
                if (nextBitmap > bitmapLength)
                    throw new HpkFormatException("FONTS bitmaps are out of range.");
                glyphs[g] = glyph;
            }

            nextGlyph += count;
            targets[t] = new HpkFontTarget(font, size, (byte)lineHeight, (byte)ascent, (int)sourceIndex, glyphs);
        }

        if (nextGlyph != glyphCount || nextBitmap != bitmapLength || used.Contains(false))
            throw new HpkFormatException("FONTS section has unreferenced glyphs, bitmaps, or sources.");

        return new HpkFonts(sources, targets, bitmaps);
    }

    private static JObject ParseJson(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var json = new UTF8Encoding(false, true).GetString(utf8);
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read())
                throw new HpkFormatException("FONTS metadata has trailing content.");
            return root;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            throw new HpkFormatException("FONTS metadata is not valid JSON: " + ex.Message);
        }
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex FontPattern();

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex SizePattern();

    [GeneratedRegex("^[0-9a-f]+$")]
    private static partial Regex HexPattern();
}
