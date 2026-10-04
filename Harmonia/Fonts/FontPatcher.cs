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

// Replaced: the target came from the font-replacements section.
public sealed record FontTargetReport(string Set, string Target, FontTargetStatus Status, int Glyphs, bool Replaced = false);

public sealed record FontPatchResult(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<FontTargetReport> Reports);

// Applies a pack's glyphs to the game's own font files. FONTS glyphs are only
// added: glyphs whose character the font already has are skipped. Replacement
// glyphs (format minor 2) take over the record of a character the font has,
// keeping its Shift-JIS code, and are added otherwise. Either way kerning
// pairs and used atlas pages are never changed: new bitmaps go only into
// candidate pages that are empty in the game's texture, and a replaced
// glyph's old bitmap stays where it was.
public static class FontPatcher
{
    // Shift-JIS codes the game stores for these characters come from its own
    // AXIS table, which has Cyrillic.
    private const string ShiftJisReference = "AXIS_12";

    public static FontPatchResult Patch(HpkFonts fonts, Func<string, byte[]?> readGameFile) =>
        Patch(fonts, null, readGameFile);

    public static FontPatchResult Patch(HpkFonts? fonts, HpkFonts? replacements, Func<string, byte[]?> readGameFile)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var reports = new List<FontTargetReport>();
        foreach (var set in FontSet.All)
            PatchSet(set, fonts, replacements, readGameFile, files, reports);

        return new FontPatchResult(files, reports);
    }

    public static byte Quantize(byte coverage) => (byte)(((coverage * 15) + 127) / 255);

    private static void PatchSet(
        FontSet set,
        HpkFonts? fonts,
        HpkFonts? replacements,
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

        // A table both sections touch is read and written once.
        var tables = new Dictionary<string, FdtFile>(StringComparer.Ordinal);
        var changedTables = new List<string>();
        FdtFile? Table(string path)
        {
            if (tables.TryGetValue(path, out var table))
                return table;
            var bytes = read(path);
            if (bytes is null)
                return null;
            table = FdtFile.Parse(bytes);
            tables[path] = table;
            return table;
        }

        var free = set.CandidatePages
            .Where(page => Texture(page) is { } texture && texture.IsChannelEmpty(page % FontTexture.Channels))
            .ToArray();
        var first = free.Length > 0 ? Texture(free[0]) : null;
        var packer = new AtlasPacker(free, first?.Width ?? 0, first?.Height ?? 0);
        var shiftJis = ReadShiftJis(read(set.FdtPath(ShiftJisReference)));
        var touched = new HashSet<string>(StringComparer.Ordinal);

        void Apply(HpkFonts section, bool replace)
        {
            foreach (var target in section.Targets)
            {
                var path = set.FdtPath(target.Name);
                var fdt = Table(path);
                if (fdt is null)
                {
                    reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.MissingFont, 0, replace));
                    continue;
                }

                if (fdt.LineHeight != target.LineHeight || fdt.Ascent != target.Ascent)
                {
                    reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.MetricsChanged, 0, replace));
                    continue;
                }

                var glyphs = replace
                    ? target.Glyphs
                    : target.Glyphs.Where(glyph => !fdt.Contains(FdtFile.PackUtf8(glyph.Codepoint))).ToArray();
                if (glyphs.Length == 0)
                {
                    reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.AlreadyPresent, 0, replace));
                    continue;
                }

                if (!packer.TryPlace(glyphs.Select(static glyph => ((int)glyph.Width, (int)glyph.Height)).ToArray(), out var placements))
                {
                    reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.NoRoom, 0, replace));
                    continue;
                }

                for (var i = 0; i < glyphs.Length; i++)
                {
                    var glyph = glyphs[i];
                    var place = placements[i];
                    var texture = Texture(place.Page)!;
                    var bitmap = section.Bitmap(glyph);
                    for (var y = 0; y < glyph.Height; y++)
                    {
                        for (var x = 0; x < glyph.Width; x++)
                            texture.Set(place.Page % FontTexture.Channels, place.X + x, place.Y + y, Quantize(bitmap[(y * glyph.Width) + x]));
                    }

                    var utf8 = FdtFile.PackUtf8(glyph.Codepoint);
                    var existing = fdt.TryGet(utf8, out var old);
                    var record = new FdtGlyph(
                        utf8,
                        existing ? old.ShiftJis : shiftJis.GetValueOrDefault(utf8),
                        (ushort)place.Page,
                        (ushort)place.X,
                        (ushort)place.Y,
                        glyph.Width,
                        glyph.Height,
                        (sbyte)(glyph.Advance - glyph.Width),
                        (sbyte)glyph.OffsetY);
                    if (existing)
                        fdt.Replace(record);
                    else
                        fdt.Add(record);
                    if (glyph.Width > 0 && glyph.Height > 0)
                        touched.Add(set.TexturePath(place.Page));
                }

                if (!changedTables.Contains(path))
                    changedTables.Add(path);
                reports.Add(new FontTargetReport(set.Name, target.Name, FontTargetStatus.Applied, glyphs.Length, replace));
            }
        }

        if (fonts is not null)
            Apply(fonts, replace: false);
        if (replacements is not null)
            Apply(replacements, replace: true);

        foreach (var path in changedTables)
            files[path] = tables[path].Write();
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
