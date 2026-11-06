using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Harmonia.Tests.Packs;
using Xunit;

namespace Harmonia.Tests.Runtime;

public sealed class CommandSyntaxTests
{
    [Fact]
    public unsafe void Command_tokens_stay_original_while_help_and_pet_names_remain_translated()
    {
        var builder = HpkBuilder.WithDefaultSheet();
        builder.Sheets.Add(new HpkTestSheet("TextCommandParam", false, [(0, 0)],
        [
            new HpkTestCell(195, 0, 0, "имя", "first"),
            new HpkTestCell(197, 0, 0, "фамилия", "last"),
        ]));
        builder.Sheets.Add(new HpkTestSheet("TextCommand", false, [(5, 0), (6, 4), (7, 8), (8, 12), (9, 16)],
        [
            new HpkTestCell(153, 0, 0, "/поиск", "/search"),
            new HpkTestCell(153, 0, 1, "/пск", "/sea"),
            new HpkTestCell(153, 0, 2, "Поиск игрока", "Player search"),
            new HpkTestCell(153, 0, 3, "/найти", "/find"),
            new HpkTestCell(153, 0, 4, "/н", "/f"),
        ]));
        builder.Sheets.Add(new HpkTestSheet("PetMirage", false, [(2, 0)],
        [
            new HpkTestCell(3, 0, 0, "Рубиновый карбункул", "Ruby Carbuncle"),
        ]));
        var keep = new KeptRows();
        CommandSyntax.AddTo(keep);
        using var runtime = new TranslationRuntime(
            HpkFile.FromBytes(builder.Build(), HpkOpenMode.Full), "test", "test.hpk", keep: keep);

        Assert.Equal(-1, runtime.BindSheet("TextCommandParam", false, [new StringColumn(0, 0)]));
        Assert.Equal(-1, runtime.FindSheet("TextCommandParam", false, [new StringColumn(0, 0)]));
        var command = runtime.BindSheet("TextCommand", false,
            [new StringColumn(5, 0), new StringColumn(6, 4), new StringColumn(7, 8), new StringColumn(8, 12), new StringColumn(9, 16)]);
        string[] sources = ["/search", "/sea", "Player search", "/find", "/f"];
        foreach (ushort ordinal in new ushort[] { 0, 1, 3, 4 })
        {
            Assert.Equal(CellDecision.Kept, RuntimeProbe.Lookup(runtime, command, 153, 0, ordinal, sources[ordinal]).Decision);
            Assert.False(runtime.TryGetShown(command, 153, 0, ordinal, System.Text.Encoding.UTF8.GetBytes(sources[ordinal]), out _));
        }
        Assert.Equal((CellDecision.Applied, "Поиск игрока"), RuntimeProbe.Lookup(runtime, command, 153, 0, 2, "Player search"));
        var pet = runtime.BindSheet("PetMirage", false, [new StringColumn(2, 0)]);
        Assert.Equal((CellDecision.Applied, "Рубиновый карбункул"), RuntimeProbe.Lookup(runtime, pet, 3, 0, 0, "Ruby Carbuncle"));
    }
}
