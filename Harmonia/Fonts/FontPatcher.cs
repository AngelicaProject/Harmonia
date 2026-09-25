using Harmonia.Packs.Hpk;

namespace Harmonia.Fonts;

// A family of .fdt files that share atlas textures. The main set is used in
// game; the title screen loads its own "_lobby" copies with separate textures.
public sealed record FontSet(string Name, string FdtSuffix, string TexturePrefix, int[] CandidatePages)
{
    // Pages no game font table references. A page is used only when it is
    // also completely empty in the running game's texture.
    public static readonly FontSet Main = new("main", string.Empty, "common/font/font", [10, 11, 25, 26, 27]);
    public static readonly FontSet Lobby = new("lobby", "_lobby", "common/font/font_lobby", [23]);
    public static readonly FontSet[] All = [Main, Lobby];

    public string FdtPath(string name) => $"common/font/{name}{FdtSuffix}.fdt";

    public string TexturePath(int page) => $"{TexturePrefix}{(page / FontTexture.Channels) + 1}.tex";
}

public enum FontTargetStatus
{
    Applied,
    AlreadyPresent,
    MissingFont,
    MetricsChanged,
    NoRoom,
}

public sealed record FontTargetReport(string Set, string Target, FontTargetStatus Status, int Glyphs);

public sealed record FontPatchResult(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<FontTargetReport> Reports);

// Adds the glyphs of a FONTS section to the game's own font files. Existing
// glyphs, kerning pairs and used atlas pages are never changed: glyphs whose
// character the font already has are skipped, and new glyphs go only into
// candidate pages that are empty in the game's texture.
public static class FontPatcher
{
    // Shift-JIS codes the game stores for these characters come from its own
    // AXIS table, which has Cyrillic.
    private const string ShiftJisReference = "AXIS_12";

    public static FontPatchResult Patch(HpkFonts fonts, Func<string, byte[]?> readGameFile)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var reports = new List<FontTargetReport>();
        foreach (var set in FontSet.All)
            PatchSet(set, fonts, readGameFile, files, reports);

        return new FontPatchResult(files, reports);
    }

    public static byte Quantize(byte coverage) => (byte)(((coverage * 15) + 127) / 255);

    private static void PatchSet(
        FontSet set,
        HpkFonts fonts,
        Func<string, byte[]?> read,
        Dictionary<string, byte[]> files,
        List<FontTargetReport> reports)
    {
        var textures = new Dictionary<string, FontTexture>(StringComparer.Ordinal);
        FontTexture? Texture(int page)
        {
            var path = set.TexturePath(page);
            if (textures.TryGetValue(path, out var texture))
                return texture;
            var bytes = read(path);
            if (bytes is null)
                return null;
            texture = FontTexture.Parse(bytes);
            textures[path] = texture;
            return texture;
        }

        var free = set.CandidatePages
            .Where(page => Texture(page) is { } texture && texture.IsChannelEmpty(page % FontTexture.Channels))
            .ToArray();
        var first = free.Length > 0 ? Texture(free[0]) : null;
        var packer = new AtlasPacker(free, first?.Width ?? 0, first?.Height ?? 0);
        var shiftJis = ReadShiftJis(read(set.FdtPath(ShiftJisReference)));
        var touched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in fonts.Targets)
        {
            var path = set.FdtPath(target.Name);
            var bytes = read(path);
            if (bytes is null)
            {
                reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.MissingFont, 0));
                continue;
            }

            var fdt = FdtFile.Parse(bytes);
            if (fdt.LineHeight != target.LineHeight || fdt.Ascent != target.Ascent)
            {
                reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.MetricsChanged, 0));
                continue;
            }

            var missing = target.Glyphs.Where(glyph => !fdt.Contains(FdtFile.PackUtf8(glyph.Codepoint))).ToArray();
            if (missing.Length == 0)
            {
                reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.AlreadyPresent, 0));
                continue;
            }

            if (!packer.TryPlace(missing.Select(static glyph => ((int)glyph.Width, (int)glyph.Height)).ToArray(), out var placements))
            {
                reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.NoRoom, 0));
                continue;
            }

            for (var i = 0; i < missing.Length; i++)
            {
                var glyph = missing[i];
                var place = placements[i];
                var texture = Texture(place.Page)!;
                var bitmap = fonts.Bitmap(glyph);
                for (var y = 0; y < glyph.Height; y++)
                {
                    for (var x = 0; x < glyph.Width; x++)
                        texture.Set(place.Page % FontTexture.Channels, place.X + x, place.Y + y, Quantize(bitmap[(y * glyph.Width) + x]));
                }

                var utf8 = FdtFile.PackUtf8(glyph.Codepoint);
                fdt.Add(new FdtGlyph(
                    utf8,
                    shiftJis.GetValueOrDefault(utf8),
                    (ushort)place.Page,
                    (ushort)place.X,
                    (ushort)place.Y,
                    glyph.Width,
                    glyph.Height,
                    (sbyte)(glyph.Advance - glyph.Width),
                    (sbyte)glyph.OffsetY));
                if (glyph.Width > 0 && glyph.Height > 0)
                    touched.Add(set.TexturePath(place.Page));
            }

            files[path] = fdt.Write();
            reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.Applied, missing.Length));
        }

        foreach (var path in touched)
            files[path] = textures[path].ToArray();
    }

    private static Dictionary<uint, ushort> ReadShiftJis(byte[]? axis)
    {
        var map = new Dictionary<uint, ushort>();
        if (axis is null)
            return map;

        try
        {
            foreach (var glyph in FdtFile.Parse(axis).Glyphs)
                map[glyph.Utf8] = glyph.ShiftJis;
        }
        catch (FormatException)
        {
        }

        return map;
    }
}
