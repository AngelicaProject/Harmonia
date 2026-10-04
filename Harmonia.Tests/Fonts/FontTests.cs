using Harmonia.Fonts;
using Harmonia.Packs.Hpk;
using Xunit;

namespace Harmonia.Tests.Fonts;

public sealed class FontTests
{
    private const uint LatinA = 0x41;
    private const uint CyrillicA = 0xD090; // А
    private const uint CyrillicBe = 0xD091; // Б
    private const uint Yo = 0xD081; // Ё
    private const uint GaijiE036 = 0xEE80B6;

    [Fact]
    public void Fdt_insertion_keeps_order_existing_records_and_kerning()
    {
        var original = FontTestData.Fdt(26, 19, [(LatinA, 4), (CyrillicBe, 4), (GaijiE036, 5)], [(LatinA, LatinA, -1), (LatinA, GaijiE036, 2)]);
        var fdt = FdtFile.Parse(original);
        Assert.Equal(original, fdt.Write());

        fdt.Add(new FdtGlyph(CyrillicA, 0x8440, 10, 1, 2, 9, 13, 1, 6));
        fdt.Add(new FdtGlyph(Yo, 0, 10, 12, 2, 9, 16, 1, 3));
        Assert.Throws<InvalidOperationException>(() => fdt.Add(new FdtGlyph(LatinA, 0, 10, 0, 0, 1, 1, 0, 0)));

        var written = fdt.Write();
        var reread = FdtFile.Parse(written);
        Assert.Equal([LatinA, Yo, CyrillicA, CyrillicBe, GaijiE036], reread.Glyphs.Select(static g => g.Utf8));
        Assert.Equal(26, reread.LineHeight);
        Assert.Equal(19, reread.Ascent);
        Assert.Equal(2, reread.KerningCount);

        var before = FdtFile.Parse(original);
        foreach (var glyph in before.Glyphs)
            Assert.Equal(glyph, reread.Glyphs.Single(g => g.Utf8 == glyph.Utf8));

        // The kerning table moves as a block and is unchanged.
        Assert.Equal(original.AsSpan(0x40 + (3 * 16)).ToArray(), written.AsSpan(0x40 + (5 * 16)).ToArray());
        Assert.Equal(original.AsSpan(0x10, 0x10).ToArray(), written.AsSpan(0x10, 0x10).ToArray());
    }

    [Fact]
    public void Fdt_with_a_repeated_record_like_the_game_axis_round_trips()
    {
        var bytes = FontTestData.Fdt(17, 13, [(0x20, 1), (0x20, 1), (CyrillicA, 0)], []);
        var fdt = FdtFile.Parse(bytes);
        Assert.Equal(bytes, fdt.Write());
        fdt.Replace(fdt.Glyphs[2] with { TexIndex = 10 });
        fdt.Add(new FdtGlyph(CyrillicBe, 0, 10, 0, 0, 1, 1, 0, 0));
        Assert.Equal([0x20u, 0x20u, CyrillicA, CyrillicBe], FdtFile.Parse(fdt.Write()).Glyphs.Select(static g => g.Utf8));
    }

    [Fact]
    public void Malformed_fdt_is_rejected()
    {
        var valid = FontTestData.Fdt(26, 19, [(LatinA, 4), (CyrillicBe, 4)], [(LatinA, LatinA, -1)]);
        var unsorted = FontTestData.Fdt(26, 19, [(CyrillicBe, 4), (LatinA, 4)], []);
        var truncated = valid[..^4];
        var badMagic = (byte[])valid.Clone();
        badMagic[0] = (byte)'x';
        foreach (var bytes in new[] { unsorted, truncated, badMagic })
            Assert.Throws<FormatException>(() => FdtFile.Parse(bytes));
    }

    [Fact]
    public void Packs_utf8_like_the_game()
    {
        Assert.Equal(0x41u, FdtFile.PackUtf8('A'));
        Assert.Equal(0xD090u, FdtFile.PackUtf8('А'));
        Assert.Equal(0xE28496u, FdtFile.PackUtf8('№'));
    }

    [Fact]
    public void Packer_places_groups_whole_or_not_at_all()
    {
        var packer = new AtlasPacker([10, 11], 32, 32);
        Assert.True(packer.TryPlace([(10, 10), (10, 10), (10, 10)], out var first));
        Assert.All(first, static p => Assert.Equal(10, p.Page));
        Assert.Equal(3, first.Select(static p => (p.X, p.Y)).Distinct().Count());

        // A group with a rectangle that fits nowhere places nothing, so the
        // space its other rectangle would have taken stays free.
        Assert.False(packer.TryPlace([(20, 20), (40, 5)], out _));
        Assert.True(packer.TryPlace([(20, 20)], out var second));
        Assert.Equal(new AtlasPlacement(11, 0, 0), second[0]);
        Assert.False(packer.TryPlace([(20, 20)], out _));
    }

    [Fact]
    public void Packer_overflow_of_a_large_target_is_reported_not_partial()
    {
        // Sixty-six 60×80 glyphs, like a large display size, do not fit one small page.
        var packer = new AtlasPacker([23], 256, 256);
        var sizes = Enumerable.Repeat((60, 80), 66).ToArray();
        Assert.False(packer.TryPlace(sizes, out var placements));
        Assert.Empty(placements);
        Assert.True(packer.TryPlace(Enumerable.Repeat((10, 10), 20).ToArray(), out _));
    }

    private static Dictionary<string, byte[]> Game(int textureSize = 64)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var set in FontSet.All)
        {
            files[set.FdtPath("Jupiter_16")] = FontTestData.Fdt(26, 19, [(LatinA, 4), (0xC2AB, 4)], [(LatinA, LatinA, -1)]);
            files[set.FdtPath("TrumpGothic_23")] = FontTestData.Fdt(30, 24, [(LatinA, 4)], []);
            files[set.FdtPath("AXIS_12")] = FontTestData.Fdt(17, 13, [(Yo, 0), (CyrillicA, 0), (CyrillicBe, 0)], []);
        }

        // Main: font3 channels 0–1 used, 2–3 free (pages 10, 11); font7 channel 1 used, so page 25 is not free.
        files["common/font/font3.tex"] = FontTestData.Texture(textureSize, 0, 1);
        files["common/font/font7.tex"] = FontTestData.Texture(textureSize, 0, 1);
        files["common/font/font_lobby6.tex"] = FontTestData.Texture(textureSize, 0, 1, 2);
        return files;
    }

    private static readonly TestTarget Jupiter = new("Jupiter", "16", 26, 19,
    [
        new TestGlyph(0xAB, 5, 7, 8, 6), // « exists in the font
        new TestGlyph('Ё', 9, 16, 3, 10),
        new TestGlyph('А', 10, 13, 6, 10, 128),
        new TestGlyph('Б', 9, 13, 6, 10),
    ]);

    [Fact]
    public void Patcher_adds_missing_glyphs_to_both_sets_without_touching_native_data()
    {
        var game = Game();
        var result = FontPatcher.Patch(FontTestData.Fonts(Jupiter), path => game.GetValueOrDefault(path));

        Assert.Contains(new FontTargetReport("main", "Jupiter_16", FontTargetStatus.Applied, 3), result.Reports);
        Assert.Contains(new FontTargetReport("lobby", "Jupiter_16", FontTargetStatus.Applied, 3), result.Reports);
        Assert.Equal(
            ["common/font/Jupiter_16.fdt", "common/font/Jupiter_16_lobby.fdt", "common/font/font3.tex", "common/font/font_lobby6.tex"],
            result.Files.Keys.Order(StringComparer.Ordinal));

        var original = FdtFile.Parse(game["common/font/Jupiter_16.fdt"]);
        var patched = FdtFile.Parse(result.Files["common/font/Jupiter_16.fdt"]);
        foreach (var glyph in original.Glyphs)
            Assert.Equal(glyph, patched.Glyphs.Single(g => g.Utf8 == glyph.Utf8));
        Assert.Equal(1, patched.KerningCount);

        var a = patched.Glyphs.Single(static g => g.Utf8 == CyrillicA);
        Assert.Equal((ushort)10, a.TexIndex);
        Assert.Equal((10, 13, 0, 6), (a.Width, a.Height, a.NextOffsetX, a.OffsetY));
        Assert.Equal((ushort)0x8001, a.ShiftJis); // from AXIS_12

        var lobbyA = FdtFile.Parse(result.Files["common/font/Jupiter_16_lobby.fdt"]).Glyphs.Single(static g => g.Utf8 == CyrillicA);
        Assert.Equal((ushort)23, lobbyA.TexIndex);

        // Only the free channel changed; coverage 128 is stored as 8 of 15.
        var before = FontTexture.Parse(game["common/font/font3.tex"]);
        var after = FontTexture.Parse(result.Files["common/font/font3.tex"]);
        for (var y = 0; y < after.Height; y++)
        {
            for (var x = 0; x < after.Width; x++)
            {
                for (var channel = 0; channel < FontTexture.Channels; channel++)
                {
                    if (channel != 2)
                        Assert.Equal(before.Get(channel, x, y), after.Get(channel, x, y));
                }
            }
        }

        Assert.Equal(8, after.Get(2, a.X, a.Y));
    }

    [Fact]
    public void Patcher_replaces_glyphs_the_font_has_keeping_their_codes_and_native_pixels()
    {
        var game = Game();
        var axis = new TestTarget("AXIS", "12", 17, 13,
        [
            new TestGlyph('А', 7, 9, 4, 8),
            new TestGlyph('Б', 7, 9, 4, 8),
            new TestGlyph('Г', 6, 9, 4, 7), // the font lacks it
        ]);
        var result = FontPatcher.Patch(FontTestData.Fonts(Jupiter), FontTestData.Fonts(axis), path => game.GetValueOrDefault(path));

        Assert.Contains(new FontTargetReport("main", "Jupiter_16", FontTargetStatus.Applied, 3), result.Reports);
        Assert.Contains(new FontTargetReport("main", "AXIS_12", FontTargetStatus.Applied, 3, Replaced: true), result.Reports);
        Assert.Contains(new FontTargetReport("lobby", "AXIS_12", FontTargetStatus.Applied, 3, Replaced: true), result.Reports);

        var original = FdtFile.Parse(game["common/font/AXIS_12.fdt"]);
        var patched = FdtFile.Parse(result.Files["common/font/AXIS_12.fdt"]);
        Assert.Equal(original.Glyphs.Count + 1, patched.Glyphs.Count);
        Assert.Equal(original.Glyphs.Single(static g => g.Utf8 == Yo), patched.Glyphs.Single(static g => g.Utf8 == Yo));
        var before = original.Glyphs.Single(static g => g.Utf8 == CyrillicA);
        var a = patched.Glyphs.Single(static g => g.Utf8 == CyrillicA);
        Assert.Equal(before.ShiftJis, a.ShiftJis);
        Assert.Contains(a.TexIndex, FontSet.Main.CandidatePages.Select(static page => (ushort)page));
        Assert.Equal((7, 9, 1, 4), (a.Width, a.Height, a.NextOffsetX, a.OffsetY));

        // The old bitmap is still there; only the free channel changed.
        var texture = FontTexture.Parse(game["common/font/font3.tex"]);
        var after = FontTexture.Parse(result.Files["common/font/font3.tex"]);
        for (var y = 0; y < after.Height; y++)
        {
            for (var x = 0; x < after.Width; x++)
            {
                for (var channel = 0; channel < 2; channel++)
                    Assert.Equal(texture.Get(channel, x, y), after.Get(channel, x, y));
            }
        }
    }

    [Fact]
    public void Patcher_skips_changed_metrics_missing_fonts_and_full_atlases()
    {
        var game = Game(textureSize: 16);
        game["common/font/font_lobby6.tex"] = FontTestData.Texture(16, 0, 1, 2, 3);
        var changed = Jupiter with { LineHeight = 27 };
        var trump = new TestTarget("TrumpGothic", "23", 30, 24, [new TestGlyph('Ж', 10, 12, 6, 11)]);
        var missing = new TestTarget("MiedingerMid", "10", 14, 11, [new TestGlyph('Ж', 5, 5, 4, 6)]);
        var result = FontPatcher.Patch(FontTestData.Fonts(changed, missing, trump), path => game.GetValueOrDefault(path));

        Assert.Contains(new FontTargetReport("main", "Jupiter_16", FontTargetStatus.MetricsChanged, 0), result.Reports);
        Assert.Contains(new FontTargetReport("main", "MiedingerMid_10", FontTargetStatus.MissingFont, 0), result.Reports);
        Assert.Contains(new FontTargetReport("main", "TrumpGothic_23", FontTargetStatus.Applied, 1), result.Reports);
        Assert.Contains(new FontTargetReport("lobby", "TrumpGothic_23", FontTargetStatus.NoRoom, 0), result.Reports);
        Assert.DoesNotContain("common/font/TrumpGothic_23_lobby.fdt", result.Files.Keys);
        Assert.DoesNotContain("common/font/font_lobby6.tex", result.Files.Keys);
    }

    [Fact]
    public void Patcher_reports_fonts_that_already_have_every_glyph()
    {
        var game = Game();
        var present = new TestTarget("Jupiter", "16", 26, 19, [new TestGlyph(0xAB, 5, 7, 8, 6)]);
        var result = FontPatcher.Patch(FontTestData.Fonts(present), path => game.GetValueOrDefault(path));
        Assert.All(result.Reports, static r => Assert.Equal(FontTargetStatus.AlreadyPresent, r.Status));
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Invalid_sections_are_rejected()
    {
        var valid = FontTestData.Section(Jupiter);
        HpkFonts.Parse(valid);
        var unsorted = FontTestData.Section(Jupiter with { Glyphs = [.. Jupiter.Glyphs.Reverse()] });
        var ascii = FontTestData.Section(Jupiter with { Glyphs = [new TestGlyph('A', 1, 1, 0, 1)] });
        var tooLow = FontTestData.Section(Jupiter with { Glyphs = [new TestGlyph('Ж', 1, 20, 10, 1)] });
        var targetsUnsorted = FontTestData.Section(new TestTarget("TrumpGothic", "23", 30, 24, [new TestGlyph('Ж', 1, 1, 0, 1)]), Jupiter);
        var trailing = valid.Append((byte)0).ToArray();
        foreach (var bytes in new[] { unsorted, ascii, tooLow, targetsUnsorted, trailing })
            Assert.Throws<HpkFormatException>(() => HpkFonts.Parse(bytes));
    }

    [Fact]
    public void Cache_round_trips_and_keeps_only_the_current_key()
    {
        var root = Path.Combine(Path.GetTempPath(), "harmonia-font-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FontCache(root);
            var game = Game();
            var result = FontPatcher.Patch(FontTestData.Fonts(Jupiter), path => game.GetValueOrDefault(path));
            var old = FontCache.Key("2026.01.01.0000.0000", [1, 2]);
            cache.Store(old, result);
            var key = FontCache.Key("2026.08.12.0000.0000", [0xab, 0xcd]);
            Assert.Equal("2026.08.12.0000.0000-abcd", key);
            Assert.Null(cache.TryLoad(key));

            cache.Store(key, result);
            cache.RemoveOthers(key);
            var loaded = cache.TryLoad(key);
            Assert.NotNull(loaded);
            Assert.Null(cache.TryLoad(old));
            Assert.Equal(result.Reports, loaded!.Reports);
            foreach (var (gamePath, file) in loaded.Files)
                Assert.Equal(result.Files[gamePath], File.ReadAllBytes(file));

            File.Delete(loaded.Files["common/font/font3.tex"]);
            Assert.Null(cache.TryLoad(key));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
