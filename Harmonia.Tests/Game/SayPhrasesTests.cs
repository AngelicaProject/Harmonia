using Harmonia.Game;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Harmonia.Tests.Packs;
using Lumina;
using Lumina.Excel.Sheets;
using Xunit;
using Xunit.Abstractions;

namespace Harmonia.Tests.Game;

// Needs an installed game, and for the second test a pack made for it:
//   HARMONIA_GAME=<game>/game/sqpack HARMONIA_PACK=<file.hpk> dotnet test --filter SayPhrases
// Without them it does nothing. Run after a game patch: a quest script the
// reader does not know leaves that quest's phrase translated.
public sealed class SayPhrasesTests(ITestOutputHelper output)
{
    private static string SheetName(string id) => $"quest/{id[^5..][..3]}/{id}";

    private static IEnumerable<string> QuestIds(GameData game) =>
        game.GetExcelSheet<Quest>()!.Select(static q => q.Id.ExtractText()).Where(static id => id.Length > 5);

    [Fact]
    public void Every_quest_script_that_takes_said_text_is_understood()
    {
        var path = Environment.GetEnvironmentVariable("HARMONIA_GAME");
        if (string.IsNullOrEmpty(path))
            return;

        using var game = new GameData(path, new LuminaOptions { PanicOnSheetChecksumMismatch = false });
        int scripts = 0, quests = 0, phrases = 0;
        var unknown = new List<string>();
        foreach (var id in QuestIds(game))
        {
            if (game.GetFile($"game_script/{SheetName(id)}.luab") is not { } script)
                continue;

            scripts++;
            var read = SayScript.Read(script.Data);
            if (read.Kind == SayScriptKind.Unknown)
                unknown.Add(id);
            if (read.Kind != SayScriptKind.Recognized)
                continue;

            quests++;
            phrases += read.Phrases.Count;
            Assert.All(read.Phrases, static p => Assert.NotEmpty(p.Targets));
        }

        output.WriteLine($"{scripts} scripts, {quests} quests take said text, {phrases} phrases");
        Assert.True(quests > 0);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Every_phrase_the_pack_translates_is_prepared()
    {
        var path = Environment.GetEnvironmentVariable("HARMONIA_GAME");
        var packPath = Environment.GetEnvironmentVariable("HARMONIA_PACK");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(packPath))
            return;

        using var game = new GameData(path, new LuminaOptions { PanicOnSheetChecksumMismatch = false });
        using var pack = HpkFile.Open(packPath, HpkOpenMode.Metadata);
        var say = new SayPhrases(game, pack, new NullLog());
        var rows = 0;
        foreach (var id in QuestIds(game))
            rows += say.Rows(SheetName(id))?.Count ?? 0;

        foreach (var quest in say.Quests.Values.OrderBy(static q => q.QuestId))
        {
            foreach (var phrase in quest.Phrases)
                output.WriteLine($"{quest.Name} step {phrase.Sequence} near {string.Join(",", phrase.Targets)}: {phrase.Translation} -> {phrase.Original}{(phrase.Exact ? " (exact)" : string.Empty)}");
        }

        foreach (var (sheet, reason) in say.Skipped)
            output.WriteLine($"skipped {sheet}: {reason}");
        output.WriteLine($"{say.Quests.Count} quests, {rows} rows kept");
        Assert.True(say.Quests.Count > 0);
        Assert.Empty(say.Skipped);
        Assert.All(say.Quests.Values, static q => Assert.All(q.Phrases, static p => Assert.NotEqual(p.Original, p.Translation)));
    }
}
