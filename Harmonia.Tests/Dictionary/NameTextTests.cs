using Harmonia.Dictionary;
using Xunit;

namespace Harmonia.Tests.Dictionary;

public sealed class NameTextTests
{
    [Fact]
    public void Plain_text_drops_macros_but_keeps_the_characters_they_stand_for()
    {
        byte[] bytes =
        [
            .. "Credendum"u8, 0x02, 0x16, 0x01, 0x03, .. "Coat"u8,
            0x02, 0x10, 0x01, 0x03, .. "of"u8,
            0x02, 0x1D, 0x01, 0x03, .. "Fending"u8,
            0x02, 0x1F, 0x01, 0x03, .. "II"u8,
            0x02, 0x13, 0x02, 0xEC, 0x03,
        ];

        Assert.Equal("CredendumCoat of Fending-II", NameText.Plain(bytes));
    }

    [Fact]
    public void Plain_text_stops_at_a_broken_macro()
    {
        Assert.Equal("Coat", NameText.Plain([.. "Coat"u8, 0x02, 0x13, 0x09, 0xEC, 0x03]));
    }

    [Theory]
    [InlineData("Augmented Credendum Coat of Fending", "augmented credendum coat of fending")]
    [InlineData("  Allagan’s  Tomestone: of Poetics ", "allagans tomestone of poetics")]
    [InlineData("Mythrite Ingot", "mythrite ingot")]
    [InlineData("Pâté de Campagne", "pate de campagne")]
    [InlineData("Шлем «Ёжик»", "шлем ежик")]
    [InlineData("Йога-пояс", "йога пояс")]
    [InlineData("Ｃｒｅｄｅｎｄｕｍ", "credendum")]
    [InlineData("ｶﾞﾝﾌﾞﾚｰﾄﾞ", "ガンブレード")]
    public void Keys_ignore_what_copying_and_typing_change(string text, string key)
    {
        Assert.Equal(key, NameText.Normalize(text));
    }

    [Theory]
    [InlineData("Скувутвгь", "credendum")]
    [InlineData("credendum", "скувутвгь")]
    [InlineData("[jkv", "холм")]
    [InlineData("Ьщщтуе", "moonet")]
    public void Layout_swap_maps_each_key(string text, string swapped)
    {
        Assert.Equal(swapped, NameText.SwapLayout(text));
    }

    [Fact]
    public void Layout_swap_needs_letters_to_decide_the_direction()
    {
        Assert.Null(NameText.SwapLayout("123"));
        Assert.Null(NameText.SwapLayout("ガンブレード"));
    }
}
