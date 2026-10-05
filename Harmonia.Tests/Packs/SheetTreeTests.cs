using Harmonia.Packs;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed class SheetTreeTests
{
    private static readonly (string, long)[] Sheets =
    [
        ("Item", 100),
        ("Action", 50),
        ("quest/000/A", 10),
        ("quest/000/B", 20),
        ("quest/001/C", 30),
        ("quest", 1),
        ("cut_scene/000/D", 5),
    ];

    private static SheetNode Find(SheetTree tree, string path)
    {
        var node = tree.Root;
        while (!string.Equals(node.Path, path, StringComparison.Ordinal))
            node = node.Children.Single(c => path.StartsWith(c.Path, StringComparison.Ordinal) && (c.IsFolder || c.Path == path));
        return node;
    }

    // The tree must agree with the filter the runtime applies.
    private static void AssertMatchesFilter(SheetTree tree, List<string> entries)
    {
        tree.Refresh(entries);
        var filter = new SheetFilter(entries);
        foreach (var (name, _) in Sheets)
            Assert.Equal(filter.Excludes(name), Find(tree, name).KeptSheets == 1);
    }

    [Fact]
    public void Builds_folders_with_totals()
    {
        var tree = SheetTree.Build(Sheets);

        Assert.Equal(["cut_scene", "quest", "Action", "Item", "quest"], tree.Root.Children.Select(static c => c.Name));
        var quest = Find(tree, "quest/");
        Assert.True(quest.IsFolder);
        Assert.Equal(3, quest.Sheets);
        Assert.Equal(60, quest.Cells);
        Assert.Equal("quest/*", quest.Entry);
        Assert.Equal(1, Find(tree, "quest/000/").Depth);
        Assert.Equal(7, tree.Root.Sheets);
        Assert.False(Find(tree, "quest").IsFolder);
    }

    [Fact]
    public void Sorts_by_lines_with_folders_first()
    {
        var tree = SheetTree.Build(Sheets);
        tree.Sort(SheetSort.Lines, true);

        Assert.Equal(["quest/", "cut_scene/", "Item", "Action", "quest"], tree.Root.Children.Select(static c => c.Path));
    }

    [Fact]
    public void Refresh_counts_kept_sheets()
    {
        var tree = SheetTree.Build(Sheets);
        tree.Refresh(["quest/000/*", "quest/001/C", "Item"]);

        var quest = Find(tree, "quest/");
        Assert.Equal(KeepState.All, quest.State);
        Assert.False(quest.Listed);
        Assert.True(Find(tree, "quest/000/A").Covered);
        Assert.Equal(KeepState.None, Find(tree, "cut_scene/").State);
        Assert.Equal(4, tree.Root.KeptSheets);
        Assert.Equal(160, tree.Root.KeptCells);
        Assert.Equal(KeepState.Some, tree.Root.State);
    }

    [Fact]
    public void Keeping_a_folder_replaces_entries_under_it()
    {
        var tree = SheetTree.Build(Sheets);
        List<string> entries = ["quest/000/A", "quest/001/*", "Item"];
        tree.Refresh(entries);

        SheetTree.Keep(entries, Find(tree, "quest/"));

        Assert.Equal(["Item", "quest/*"], entries);
        AssertMatchesFilter(tree, entries);
    }

    [Fact]
    public void Translating_inside_a_kept_folder_keeps_the_rest()
    {
        var tree = SheetTree.Build(Sheets);
        List<string> entries = ["quest/*"];
        tree.Refresh(entries);

        SheetTree.Translate(entries, Find(tree, "quest/000/A"));

        Assert.Equal(["quest/001/*", "quest/000/B"], entries);
        AssertMatchesFilter(tree, entries);
        Assert.Equal(KeepState.Some, Find(tree, "quest/").State);
    }

    [Fact]
    public void Translating_a_folder_clears_everything_under_it()
    {
        var tree = SheetTree.Build(Sheets);
        List<string> entries = ["quest/000/*", "quest/001/C", "quest", "Item"];

        SheetTree.Translate(entries, Find(tree, "quest/"));

        Assert.Equal(["quest", "Item"], entries);
        AssertMatchesFilter(tree, entries);
    }

    [Fact]
    public void Keeping_a_sheet_adds_its_name()
    {
        var tree = SheetTree.Build(Sheets);
        List<string> entries = [];
        tree.Refresh(entries);

        SheetTree.Keep(entries, Find(tree, "quest/001/C"));
        SheetTree.Keep(entries, Find(tree, "quest"));

        Assert.Equal(["quest/001/C", "quest"], entries);
        AssertMatchesFilter(tree, entries);
    }

    [Fact]
    public void Group_entries_keep_and_translate_without_the_tree()
    {
        List<string> entries = ["quest/000/*", "quest/001/C", "Item"];

        Assert.Equal(KeepState.Some, SheetTree.StateOf(entries, "quest/*"));
        Assert.Equal(KeepState.All, SheetTree.StateOf(entries, "Item"));
        Assert.Equal(KeepState.None, SheetTree.StateOf(entries, "Action"));
        Assert.Equal(KeepState.None, SheetTree.StateOf(entries, "cut_scene/*"));

        SheetTree.Keep(entries, "quest/*");
        Assert.Equal(["Item", "quest/*"], entries);
        Assert.Equal(KeepState.All, SheetTree.StateOf(entries, "quest/*"));

        SheetTree.Keep(entries, "quest/*");
        Assert.Equal(["Item", "quest/*"], entries);

        entries.Add("quest/002/E");
        SheetTree.Translate(entries, "quest/*");
        Assert.Equal(["Item"], entries);

        SheetTree.Translate(entries, "Item");
        Assert.Empty(entries);
    }

    [Fact]
    public void Finds_nodes_by_entry()
    {
        var tree = SheetTree.Build(Sheets);

        Assert.Same(Find(tree, "quest/"), tree.Find("quest/*"));
        Assert.Same(Find(tree, "quest/000/"), tree.Find("quest/000/*"));
        Assert.Same(Find(tree, "quest"), tree.Find("quest"));
        Assert.Null(tree.Find("quest/"));
        Assert.Null(tree.Find("Missing"));
    }
}
