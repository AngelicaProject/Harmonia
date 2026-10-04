namespace Harmonia.Dictionary;

public readonly record struct NameSite(string Name, string Url);

// Community sites players look names up on. The databases address rows by
// the game's id; the wiki by the English name, so it needs an English original.
public static class NameSites
{
    public static IReadOnlyList<NameSite> For(NameEntry entry, bool marketable)
    {
        var sites = new List<NameSite>();
        var id = entry.RowId;
        switch (entry.Category)
        {
            case NameCategory.Item:
                if (marketable)
                    sites.Add(new("Universalis", $"https://universalis.app/market/{id}"));
                sites.Add(new("Garland Tools", $"https://www.garlandtools.org/db/#item/{id}"));
                sites.Add(new("Teamcraft", $"https://ffxivteamcraft.com/db/en/item/{id}"));
                break;
            case NameCategory.Action:
                sites.Add(new("Garland Tools", $"https://www.garlandtools.org/db/#action/{id}"));
                sites.Add(new("Teamcraft", $"https://ffxivteamcraft.com/db/en/action/{id}"));
                break;
            case NameCategory.Status:
                sites.Add(new("Garland Tools", $"https://www.garlandtools.org/db/#status/{id}"));
                sites.Add(new("Teamcraft", $"https://ffxivteamcraft.com/db/en/status/{id}"));
                break;
        }

        if (entry.Originals.FirstOrDefault(static o => o.Language == "en").Text is { Length: > 0 } english)
            sites.Add(new("Console Games Wiki", "https://ffxiv.consolegameswiki.com/wiki/" + WikiTitle(english)));

        return sites;
    }

    // MediaWiki titles: underscores for spaces, the first letter capital
    // ("the Praetorium" is the page "The_Praetorium").
    private static string WikiTitle(string name)
    {
        var title = char.ToUpperInvariant(name[0]) + name[1..];
        return Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%27", "'");
    }
}
