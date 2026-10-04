using Harmonia.Dictionary;
using Xunit;

namespace Harmonia.Tests.Dictionary;

public sealed class NameSitesTests
{
    [Fact]
    public void Items_open_by_id_and_on_the_market_only_when_sold_there()
    {
        var item = new NameEntry(NameCategory.Item, 12519, "Слиток мизрита", [new("en", "Mythrite Ingot")]);

        Assert.Equal(
        [
            new NameSite("Universalis", "https://universalis.app/market/12519"),
            new NameSite("Garland Tools", "https://www.garlandtools.org/db/#item/12519"),
            new NameSite("Teamcraft", "https://ffxivteamcraft.com/db/en/item/12519"),
            new NameSite("Console Games Wiki", "https://ffxiv.consolegameswiki.com/wiki/Mythrite_Ingot"),
        ], NameSites.For(item, marketable: true));
        Assert.DoesNotContain(NameSites.For(item, marketable: false), static s => s.Name == "Universalis");
    }

    [Fact]
    public void Wiki_titles_start_with_a_capital_and_keep_apostrophes()
    {
        var duty = new NameEntry(NameCategory.Duty, 16, "Преторий", [new("en", "the Praetorium")]);
        var item = new NameEntry(NameCategory.Item, 1950, "Лук Ифрита", [new("en", "Ifrit's Bow")]);

        Assert.Equal("https://ffxiv.consolegameswiki.com/wiki/The_Praetorium", Assert.Single(NameSites.For(duty, false)).Url);
        Assert.EndsWith("/wiki/Ifrit's_Bow", NameSites.For(item, false)[^1].Url);
    }

    [Fact]
    public void The_wiki_needs_an_english_name()
    {
        var place = new NameEntry(NameCategory.Place, 430, "Преториум", [new("ja", "魔導城プラエトリウム")]);
        Assert.Empty(NameSites.For(place, false));
    }
}
