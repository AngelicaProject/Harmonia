using System.Text.Json.Nodes;
using Harmonia.Game;
using Xunit;

namespace Harmonia.Tests.Game;

public sealed class AngelicaHandoffTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("harmonia-handoff-");

    private string GameDir => Path.Combine(root.FullName, "game");

    private string Storage => Path.Combine(root.FullName, "angelica", "storage", "harmonia");

    public AngelicaHandoffTests()
    {
        Directory.CreateDirectory(GameDir);
        Directory.CreateDirectory(Storage);
        var pointer = new JsonObject { ["installation_dir"] = Path.Combine(root.FullName, "angelica") };
        File.WriteAllText(Path.Combine(GameDir, "angelica.json"), pointer.ToJsonString());
    }

    public void Dispose() => root.Delete(recursive: true);

    [Fact]
    public void The_answer_names_the_session_the_Angelica_copy_counted_and_the_game_build()
    {
        File.WriteAllText(Path.Combine(Storage, "session.json"), """{ "session": 7 }""");

        var path = AngelicaHandoff.Answer(GameDir, "2026.09.15.0000.0000");

        Assert.Equal(Path.Combine(Storage, AngelicaHandoff.AnswerFile), path);
        var answer = JsonNode.Parse(File.ReadAllText(path!))!;
        Assert.True(answer["translating"]!.GetValue<bool>());
        Assert.Equal("2026.09.15.0000.0000", answer["game_version"]!.GetValue<string>());
        Assert.Equal(7UL, answer["angelica_session"]!.GetValue<ulong>());
    }

    [Fact]
    public void Nothing_is_written_without_a_counted_session()
    {
        Assert.Null(AngelicaHandoff.Answer(GameDir, "2026.09.15.0000.0000"));
        Assert.False(File.Exists(Path.Combine(Storage, AngelicaHandoff.AnswerFile)));
    }

    [Fact]
    public void Nothing_is_written_without_Angelica_in_the_game_folder()
    {
        File.Delete(Path.Combine(GameDir, "angelica.json"));
        File.WriteAllText(Path.Combine(Storage, "session.json"), """{ "session": 7 }""");

        Assert.Null(AngelicaHandoff.Answer(GameDir, "2026.09.15.0000.0000"));
    }
}
