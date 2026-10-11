using System.Buffers.Binary;
using Harmonia.Fonts;
using Harmonia.Game;
using Harmonia.Packs.Hpk;
using Lumina;
using Xunit;
using Xunit.Abstractions;

namespace Harmonia.Tests.Game;

// Needs an installed game, and for the pack's glyphs a pack with fonts:
//   HARMONIA_GAME=<game>/game/sqpack HARMONIA_PACK=<file.hpk> dotnet test --filter GameFontTables
// Without them it does nothing. Run after a game patch: it reads the font
// tables from the game's executable the way the plugin reads them from the
// running game, and checks that every glyph of the game's fonts, and every
// glyph the pack adds, is on a page the game loads with its font.
public sealed class GameFontTablesTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_glyph_is_on_a_page_the_game_loads_with_its_font()
    {
        var path = Environment.GetEnvironmentVariable("HARMONIA_GAME");
        if (string.IsNullOrEmpty(path))
            return;

        var image = LoadImage(Path.Combine(path, "..", "ffxiv_dx11.exe"), out var baseAddress);
        var function = Find(image, GameFonts.ReloadFontsSignature);
        Assert.True(function >= 0, "The font reload function was not found.");
        var tables = FontTables.Parse(FontTableScan.Read(image, baseAddress, function));
        foreach (var set in FontSet.All)
        {
            foreach (var group in tables.Fonts(set).GroupBy(name => tables.Pages(set, name)).OrderBy(static group => group.Key))
                output.WriteLine($"{set.Name}, {group.Key} pages: {string.Join(", ", group)}");
        }

        using var game = new GameData(path, new LuminaOptions { PanicOnSheetChecksumMismatch = false });
        byte[]? Read(string file) => game.FileExists(file) ? game.GetFile(file)?.Data : null;

        var packPath = Environment.GetEnvironmentVariable("HARMONIA_PACK");
        using var pack = string.IsNullOrEmpty(packPath) ? null : HpkFile.Open(packPath, HpkOpenMode.Metadata);

        // The patcher refuses tables the game's own glyphs do not fit.
        var result = FontPatcher.Patch(pack?.ReadFonts(), pack?.ReadFontReplacements(), tables, Read);
        foreach (var report in result.Reports)
            output.WriteLine($"{report.Set} {report.Target}: {report.Status} {report.Glyphs}{(report.Replaced ? " replaced" : string.Empty)}");

        foreach (var set in FontSet.All)
        {
            foreach (var name in tables.Fonts(set))
            {
                if (!result.Files.TryGetValue(set.FdtPath(name), out var patched))
                    continue;

                var native = FdtFile.Parse(Read(set.FdtPath(name))!).Glyphs.ToHashSet();
                var added = FdtFile.Parse(patched).Glyphs.Where(glyph => !native.Contains(glyph)).ToList();
                Assert.NotEmpty(added);
                Assert.All(added, glyph => Assert.InRange(glyph.TexIndex, 0, tables.Pages(set, name) - 1));
                output.WriteLine($"{set.FdtPath(name)}: {added.Count} glyphs on pages {string.Join(", ", added.Select(static glyph => glyph.TexIndex).Distinct().Order())}");
            }
        }
    }

    // The executable as the game maps it: every section at its address.
    private static byte[] LoadImage(string path, out ulong baseAddress)
    {
        var file = File.ReadAllBytes(path);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pe + 6));
        var optional = pe + 24;
        baseAddress = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(optional + 24));
        var image = new byte[BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(optional + 56))];
        var table = optional + BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pe + 20));
        for (var i = 0; i < sections; i++)
        {
            var section = file.AsSpan(table + (i * 40));
            var address = BinaryPrimitives.ReadInt32LittleEndian(section[12..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(section[16..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(section[20..]);
            file.AsSpan(offset, size).CopyTo(image.AsSpan(address));
        }

        return image;
    }

    private static int Find(ReadOnlySpan<byte> bytes, string signature)
    {
        var pattern = signature.Split(' ').Select(static part => part == "??" ? -1 : Convert.ToInt32(part, 16)).ToArray();
        for (var start = 0; start + pattern.Length <= bytes.Length; start++)
        {
            var i = 0;
            while (i < pattern.Length && (pattern[i] < 0 || pattern[i] == bytes[start + i]))
                i++;
            if (i == pattern.Length)
                return start;
        }

        return -1;
    }
}
