using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harmonia.Game;

// Harmonia also runs as an Angelica plugin: the translation for the days the
// game runs without Dalamud. The game is translated once, and this build
// comes first because it also keeps what other Dalamud plugins read. When it
// translates, it answers the Angelica copy, which then stays off from the
// next game start. The copy counts its game starts in `session.json` of its
// storage folder; the answer, `dalamud.json` next to it, names that session
// and the game build. When Dalamud does not load, nothing answers, and the
// copy translates from the next start.
//
// `angelica.json` in the game folder points to Angelica's installation; a
// plugin's storage folder is `storage/<plugin id>` in it.
public static class AngelicaHandoff
{
    public const string AnswerFile = "dalamud.json";
    private const string SessionFile = "session.json";

    /// <summary>
    /// Answers the Angelica copy of Harmonia in <paramref name="gameDir"/>'s
    /// installation, if there is one that counted a session.
    /// </summary>
    /// <returns>The answer's path, or null when there is no copy to answer.</returns>
    public static string? Answer(string gameDir, string gameVersion)
    {
        var pointer = Path.Combine(gameDir, "angelica.json");
        if (!File.Exists(pointer))
            return null;
        var installation = JsonNode.Parse(File.ReadAllText(pointer))?["installation_dir"]?.GetValue<string>();
        if (string.IsNullOrEmpty(installation))
            return null;
        var storage = Path.Combine(installation, "storage", "harmonia");
        var sessionPath = Path.Combine(storage, SessionFile);
        if (!File.Exists(sessionPath))
            return null;
        var session = JsonNode.Parse(File.ReadAllText(sessionPath))?["session"]?.GetValue<ulong>() ?? 0;
        if (session == 0)
            return null;

        var answer = new JsonObject
        {
            ["format"] = 1,
            ["translating"] = true,
            ["game_version"] = gameVersion,
            ["angelica_session"] = session,
        };
        var path = Path.Combine(storage, AnswerFile);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, answer.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>Answers for the running game; never throws.</summary>
    public static void AnswerRunningGame(IHarmoniaLog log)
    {
        try
        {
            var gameDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (gameDir is null)
                return;
            var version = File.ReadAllText(Path.Combine(gameDir, "ffxivgame.ver")).Trim();
            if (Answer(gameDir, version) is { } path)
                log.Info($"Told Harmonia in Angelica that this build translates: {path}");
        }
        catch (Exception ex)
        {
            log.Warning("Could not tell Harmonia in Angelica that this build translates; both may translate from the next start.", ex);
        }
    }
}
