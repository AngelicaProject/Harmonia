using System.Text;
using Harmonia.Runtime;
using Xunit;

namespace Harmonia.Tests.Runtime;

public sealed class CyrillicCaseTests
{
    private static string Upper(string text, bool firstCharOnly, bool everyWord, Func<int, bool>? excluded = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        CyrillicCase.ToUpper(bytes, firstCharOnly, everyWord, excluded);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string Lower(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        CyrillicCase.ToLower(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    [Theory]
    [InlineData("паладин", "Паладин")]
    [InlineData("ёж", "Ёж")]
    [InlineData("яблоко", "Яблоко")]
    [InlineData("їжак", "Їжак")]
    [InlineData("белый маг", "Белый маг")]
    [InlineData("Паладин", "Паладин")]
    [InlineData("paladin", "paladin")]
    public void Head_capitalizes_only_the_first_letter(string text, string expected)
    {
        Assert.Equal(expected, Upper(text, firstCharOnly: true, everyWord: false));
    }

    [Fact]
    public void Head_all_capitalizes_every_word_except_excluded_ones()
    {
        Assert.Equal("Страж Мир Эфира", Upper("страж мир эфира", true, true));

        var text = Encoding.UTF8.GetBytes("страж и мир");
        var and = Encoding.UTF8.GetBytes("и ");
        CyrillicCase.ToUpper(text, true, true, i => text.AsSpan(i).StartsWith(and));
        Assert.Equal("Страж и Мир", Encoding.UTF8.GetString(text));
    }

    [Fact]
    public void Caps_and_lower_change_every_letter()
    {
        Assert.Equal("ЧЁРНЫЙ МАГ 90", Upper("чёрный маг 90", false, false));
        Assert.Equal("чёрный маг 90", Lower("ЧЁРНЫЙ МАГ 90"));
        Assert.Equal("абвгдеёжзийклмнопрстуфхцчшщъыьэюя", Lower("АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ"));
        Assert.Equal("АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ", Upper("абвгдеёжзийклмнопрстуфхцчшщъыьэюя", false, false));
    }

    [Fact]
    public void Payloads_are_left_untouched()
    {
        // <color(…)> with a body byte 0xD0 0xB0 that is not text.
        byte[] payload = [0x02, 0x13, 0x03, 0xD0, 0xB0, 0x03];
        byte[] text = [.. payload, .. Encoding.UTF8.GetBytes(" мир")];
        CyrillicCase.ToUpper(text, false, false);
        Assert.Equal(payload, text[..6]);
        Assert.Equal(" МИР", Encoding.UTF8.GetString(text[6..]));
    }

    [Fact]
    public void Cyrillic_languages_are_recognized()
    {
        Assert.True(CyrillicCase.IsCyrillicLanguage("ru"));
        Assert.True(CyrillicCase.IsCyrillicLanguage("uk-UA"));
        Assert.True(CyrillicCase.IsCyrillicLanguage("sr-Cyrl"));
        Assert.False(CyrillicCase.IsCyrillicLanguage("de"));
        Assert.False(CyrillicCase.IsCyrillicLanguage(null));
    }
}
