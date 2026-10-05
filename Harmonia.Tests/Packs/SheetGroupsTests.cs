using Harmonia.Packs;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed class SheetGroupsTests
{
    [Fact]
    public void Without_changes_the_built_in_groups_are_shown()
    {
        Assert.Equal(SheetGroups.All, SheetGroups.Effective([]));
        Assert.Equal(SheetGroups.All, SheetGroups.Effective(null));
    }

    [Fact]
    public void Built_in_groups_can_be_changed_and_restored()
    {
        List<SavedSheetGroup> saved = [];
        SheetGroups.Save(saved, "items", "Мои предметы", ["Item", "ItemUICategory"]);

        var items = SheetGroups.Effective(saved).Single(g => g.Key == "items");
        Assert.Equal(["Item", "ItemUICategory"], items.Entries);
        Assert.Equal("Мои предметы", items.Name);
        Assert.True(items.BuiltIn);
        Assert.True(items.EntriesEdited);

        SheetGroups.Save(saved, "items", " ", SheetGroups.BuiltIn("items")!.Entries);
        Assert.Empty(saved);
    }

    [Fact]
    public void Removed_built_in_groups_are_hidden_until_restored()
    {
        List<SavedSheetGroup> saved = [];
        SheetGroups.Remove(saved, "places");
        Assert.DoesNotContain(SheetGroups.Effective(saved), g => g.Key == "places");

        SheetGroups.Restore(saved, "places");
        Assert.Contains(SheetGroups.Effective(saved), g => g.Key == "places");
    }

    [Fact]
    public void Own_groups_follow_the_built_in_ones()
    {
        List<SavedSheetGroup> saved = [];
        var key = SheetGroups.NewKey(saved);
        SheetGroups.Save(saved, key, "Лавки", ["shop/*", "GilShop", "GilShop"]);
        SheetGroups.Save(saved, SheetGroups.NewKey(saved), "Ещё", ["Addon"]);

        var groups = SheetGroups.Effective(saved);
        var own = groups[SheetGroups.All.Count];
        Assert.Equal("user1", own.Key);
        Assert.Equal("Лавки", own.Name);
        Assert.False(own.BuiltIn);
        Assert.Equal(["shop/*", "GilShop"], own.Entries);
        Assert.Equal("user2", groups[^1].Key);

        SheetGroups.Remove(saved, key);
        Assert.Single(saved);
    }
}
