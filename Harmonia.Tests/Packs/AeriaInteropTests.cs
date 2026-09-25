using System.Text;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Harmonia.Tests.Runtime;
using Xunit;

namespace Harmonia.Tests.Packs;

// TestData/harmonia-interop.hpk is produced by Aeria's aeria-export tests
// (crates/aeria-export/tests/fixtures). Reading it here proves the two
// implementations agree on Pack Format v1, including the signature.
public sealed unsafe class AeriaInteropTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "harmonia-interop.hpk");

    [Fact]
    public void Aeria_pack_passes_full_verification()
    {
        using var file = HpkFile.Open(FixturePath, HpkOpenMode.Full);
        var manifest = file.Manifest;

        Assert.Equal("interop-test", manifest.PackId);
        Assert.Equal(7, manifest.Sequence);
        Assert.Equal("en", manifest.SourceLanguage);
        Assert.Equal("2026.08.12.0000.0000", manifest.SourceGameVersion);
        Assert.Equal(HpkContentPolicy.All, manifest.ContentPolicy);
        Assert.Equal("CC0-1.0", manifest.License);
        Assert.Null(manifest.PublisherUrl);
        Assert.Equal(["Addon", "quest/000/Test"], file.SheetNames);
        Assert.NotNull(file.Signature);
        Assert.Null(file.Signature!.PreviousFingerprint);
    }

    [Fact]
    public void Aeria_pack_serves_guarded_translations()
    {
        using var runtime = new TranslationRuntime(HpkFile.Open(FixturePath, HpkOpenMode.Full), applyUnreviewed: false, FixturePath);
        var addon = runtime.BindSheet("Addon", false, [new StringColumn(0, 4), new StringColumn(2, 8)]);
        var quest = runtime.BindSheet("quest/000/Test", true, [new StringColumn(1, 0)]);

        Assert.Equal((CellDecision.Applied, "Мир"), RuntimeProbe.Lookup(runtime, addon, 1, 0, 1, "World"));
        Assert.Equal(CellDecision.Applied, RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello").Decision);

        // The fixture exports Addon/7 column 2 as unreviewed; the source check comes first.
        Assert.Equal(CellDecision.Unreviewed, RuntimeProbe.Lookup(runtime, addon, 7, 0, 1, "Hi").Decision);
        Assert.Equal(CellDecision.SourceChanged, RuntimeProbe.Lookup(runtime, addon, 7, 0, 1, "Hello").Decision);

        Assert.Equal((CellDecision.Applied, "Вторая"), RuntimeProbe.Lookup(runtime, quest, 3, 1, 0, "Second"));
    }

    [Fact]
    public void Aeria_fonts_pack_passes_full_verification()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "harmonia-interop-fonts.hpk");
        using var file = HpkFile.Open(path, HpkOpenMode.Full);
        Assert.True(file.HasFonts);
        var fonts = file.ReadFonts()!;
        Assert.Equal("Test Sans", Assert.Single(fonts.Sources).Family);
        Assert.Equal(["Jupiter_16", "TrumpGothic_184"], fonts.Targets.Select(static t => t.Name));
        var jupiter = fonts.Targets[0];
        Assert.Equal((26, 19), (jupiter.LineHeight, jupiter.Ascent));
        Assert.Equal(['Б', 'Ж'], jupiter.Glyphs.Select(static g => (char)g.Codepoint));
        var zhe = jupiter.Glyphs[1];
        Assert.Equal((14, 13, 6, 13), (zhe.Width, zhe.Height, zhe.OffsetY, zhe.Advance));
        Assert.Equal(Enumerable.Range(0, 14 * 13).Select(static i => (byte)(i * 37 % 256)), fonts.Bitmap(zhe).ToArray());

        using var plain = HpkFile.Open(FixturePath, HpkOpenMode.Full);
        Assert.False(plain.HasFonts);
        Assert.Null(plain.ReadFonts());
    }
}
