using System.Text;
using Harmonia.Runtime;
using Xunit;

namespace Harmonia.Tests.Runtime;

public sealed class SayTextTests
{
    private static string? Replace(string body, string phrase, string original, bool exact = false) =>
        SayText.Replace(Encoding.UTF8.GetBytes(body), phrase, original, exact) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    private static string Command(string line, out string body)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var command = Encoding.UTF8.GetString(SayText.Command(bytes, out var bodyStart));
        body = Encoding.UTF8.GetString(bytes.AsSpan(bodyStart));
        return command;
    }

    [Fact]
    public void A_line_is_split_into_its_command_and_text()
    {
        Assert.Equal("/say", Command("/say лали-хо", out var body));
        Assert.Equal("лали-хо", body);
        Assert.Equal("/s", Command("/s  два пробела", out body));
        Assert.Equal(" два пробела", body);
        Assert.Equal("/say", Command("/say", out body));
        Assert.Equal(string.Empty, body);
        Assert.Equal(string.Empty, Command("лали-хо", out body));
        Assert.Equal("лали-хо", body);
        Assert.Equal(string.Empty, Command(string.Empty, out _));
    }

    [Fact]
    public void A_phrase_anywhere_in_the_text_becomes_the_games_phrase()
    {
        Assert.Equal("lali-ho", Replace("лали-хо", "лали-хо", "lali-ho"));
        Assert.Equal("ой, lali-ho, друзья!", Replace("ой, лали-хо, друзья!", "лали-хо", "lali-ho"));

        // As the game compares: inside a longer word too.
        Assert.Equal("lali-hoоо", Replace("лали-хооо", "лали-хо", "lali-ho"));
        Assert.Null(Replace("привет", "лали-хо", "lali-ho"));
        Assert.Null(Replace(string.Empty, "лали-хо", "lali-ho"));
        Assert.Null(Replace("лали-хо", string.Empty, "lali-ho"));
    }

    [Fact]
    public void Case_typographic_marks_and_the_letter_yo_are_forgiven()
    {
        Assert.Equal("lali-ho", Replace("ЛАЛИ-ХО", "лали-хо", "lali-ho"));
        Assert.Equal("lali-ho", Replace("лали–хо", "лали-хо", "lali-ho"));
        Assert.Equal("Beaver", Replace("бобер", "Бобёр", "Beaver"));
        Assert.Equal("Y'shtola", Replace("я’штола", "Я'штола", "Y'shtola"));
        Assert.Equal("its whiskers ok", Replace("его усы ok", "его усы", "its whiskers"));
    }

    [Fact]
    public void A_missing_final_mark_is_forgiven_and_the_games_phrase_is_whole()
    {
        Assert.Equal("Ellie!", Replace("Элли", "Элли!", "Ellie!"));
        Assert.Equal("Ellie!!", Replace("Элли!!", "Элли!", "Ellie!"));
        Assert.Equal("I come in peace.", Replace("я пришёл с миром", "Я пришёл с миром.", "I come in peace."));
    }

    [Fact]
    public void An_exact_phrase_must_be_the_whole_text()
    {
        Assert.Equal("Well met!", Replace("Приветствую!", "Приветствую!", "Well met!", exact: true));
        Assert.Equal("Well met!", Replace("  приветствую ", "Приветствую!", "Well met!", exact: true));
        Assert.Null(Replace("Приветствую! Как дела?", "Приветствую!", "Well met!", exact: true));
        Assert.Null(Replace("О, Приветствую!", "Приветствую!", "Well met!", exact: true));
    }

    [Fact]
    public void Macros_in_the_line_stay_as_they_are()
    {
        // An auto-translate entry: STX, code 0x2E, length 3, two bytes, ETX.
        byte[] macro = [0x02, 0x2E, 0x03, 0x02, 0x05, 0x03];
        var body = (byte[])[.. macro, .. Encoding.UTF8.GetBytes(" лали-хо "), .. macro];
        var expected = (byte[])[.. macro, .. Encoding.UTF8.GetBytes(" lali-ho "), .. macro];

        Assert.Equal(expected, SayText.Replace(body, "лали-хо", "lali-ho", false));
        Assert.Null(SayText.Replace(body, "лали-хо", "lali-ho", true));

        // The phrase is not looked for inside a macro, and a damaged macro
        // or text leaves the line alone.
        byte[] holds = [0x02, 0x2E, 0x03, (byte)'h', (byte)'i', 0x03];
        Assert.Null(SayText.Replace(holds, "hi", "ho", false));
        Assert.Null(SayText.Replace([0x02, 0x2E, 0x09, 0x01], "hi", "ho", false));
        Assert.Null(SayText.Replace([(byte)'h', (byte)'i', 0xFF], "hi", "ho", false));
        Assert.Null(SayText.Replace([(byte)'h', 0x00, (byte)'i'], "hi", "ho", false));
    }
}
