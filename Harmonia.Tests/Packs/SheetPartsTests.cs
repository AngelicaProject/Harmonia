using Harmonia.Packs;
using Harmonia.Runtime;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed class SheetPartsTests
{
    private static SheetPartMap Map(string sheet) => SheetParts.Of(sheet)!;

    [Fact]
    public void Every_map_has_a_part_and_columns_only_when_split()
    {
        Assert.Equal(SheetParts.All.Count, SheetParts.All.Select(static m => m.Sheet).Distinct(StringComparer.Ordinal).Count());
        foreach (var map in SheetParts.All)
        {
            Assert.True(map.Has(SheetPart.Names) || map.Has(SheetPart.Descriptions), map.Sheet);
            if (map.Split)
            {
                Assert.NotEmpty(map.Names);
                Assert.NotEmpty(map.Descriptions);
                Assert.Empty(map.Names.Intersect(map.Descriptions));
            }
            else
                Assert.Empty(map.Names.Concat(map.Descriptions));
        }
    }

    [Fact]
    public void A_split_part_is_an_entry_and_a_whole_part_is_the_sheet()
    {
        List<string> sheets = [];
        List<string> parts = [];

        SheetParts.Keep(sheets, parts, Map("Item"), SheetPart.Names);
        SheetParts.Keep(sheets, parts, Map("Action"), SheetPart.Names);
        SheetParts.Keep(sheets, parts, Map("ActionTransient"), SheetPart.Names);

        Assert.Equal(["Item:names"], parts);
        Assert.Equal(["Action"], sheets);
        Assert.True(SheetParts.IsKept(sheets, parts, Map("Item"), SheetPart.Names));
        Assert.False(SheetParts.IsKept(sheets, parts, Map("Item"), SheetPart.Descriptions));
    }

    [Fact]
    public void Translating_a_part_of_a_kept_sheet_keeps_the_other_part()
    {
        List<string> sheets = ["Item"];
        List<string> parts = [];

        Assert.True(SheetParts.IsKept(sheets, parts, Map("Item"), SheetPart.Descriptions));
        SheetParts.Translate(sheets, parts, Map("Item"), SheetPart.Names);

        Assert.Empty(sheets);
        Assert.Equal(["Item:descriptions"], parts);
    }

    [Fact]
    public void A_sheet_kept_whole_drops_its_parts()
    {
        List<string> parts = ["Item:names", "Status:descriptions", "Future:names"];

        SheetParts.Forget(parts, "Item");

        Assert.Equal(["Status:descriptions", "Future:names"], parts);
    }

    [Fact]
    public void State_counts_only_sheets_with_the_part()
    {
        var maps = new[] { Map("Action"), Map("ActionTransient"), Map("Status") };
        List<string> sheets = ["Action"];
        List<string> parts = ["Status:names"];

        Assert.Equal(KeepState.All, SheetParts.StateOf(sheets, parts, maps, SheetPart.Names));
        Assert.Equal(KeepState.None, SheetParts.StateOf(sheets, parts, maps, SheetPart.Descriptions));

        sheets.Add("ActionTransient");
        Assert.Equal(KeepState.Some, SheetParts.StateOf(sheets, parts, maps, SheetPart.Descriptions));
    }

    [Fact]
    public void Kept_parts_become_columns_of_every_row()
    {
        var keep = new KeptRows();

        SheetParts.AddTo(keep, ["Item:names", "Status:descriptions", "Action:names", "Unknown:names", "Item:lore"]);

        Assert.Equal(new uint[] { 0, 2, 9 }, keep.ColumnsOfEveryRow("Item")!.Order());
        Assert.Equal(new uint[] { 1 }, keep.ColumnsOfEveryRow("Status")!);
        // A whole part is a sheet in UntranslatedSheets, never an entry.
        Assert.Null(keep.ColumnsOfEveryRow("Action"));
        Assert.False(keep.KeepsSheet("Action"));
    }

    [Fact]
    public void The_tree_shows_a_sheet_with_a_part_as_partly_kept()
    {
        var tree = SheetTree.Build([("Item", 10), ("Status", 5), ("quest/000/A", 1)]);

        tree.Refresh(["quest/*"], SheetParts.SheetsOf(["Item:names"]));

        var item = tree.Root.Children.Single(static c => c.Path == "Item");
        Assert.Equal(KeepState.Some, item.State);
        Assert.Equal(0, item.KeptSheets);
        Assert.Equal(KeepState.None, tree.Root.Children.Single(static c => c.Path == "Status").State);
        Assert.Equal(KeepState.Some, tree.Root.State);
    }
}
