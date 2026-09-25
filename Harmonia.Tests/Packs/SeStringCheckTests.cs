using Harmonia.Packs.Hpk;
using Xunit;

namespace Harmonia.Tests.Packs;

public class SeStringCheckTests
{
    [Theory]
    // Addon 17: <kilo(lnum1,\,)> followed by a private-use icon glyph, as shipped by the game.
    [InlineData("022206E802FF022C03EE8189")]
    [InlineData("022203E80203")]
    [InlineData("416263")]
    public void AcceptsGameStrings(string hex)
    {
        Assert.True(SeStringCheck.IsWellFormed(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("410042")]
    // Macro whose declared body runs past the end of the string.
    [InlineData("022209E80203")]
    public void RejectsMalformedStrings(string hex)
    {
        Assert.False(SeStringCheck.IsWellFormed(Convert.FromHexString(hex)));
    }
}
