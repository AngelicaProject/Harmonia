using System.Text;
using Harmonia.Packs;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Harmonia.Tests.Packs;
using Xunit;

namespace Harmonia.Tests.Runtime;

internal static unsafe class RuntimeProbe
{
    // What the row hook decides for one string, and the text it would write.
    public static (CellDecision? Decision, string? Text) Lookup(
        TranslationRuntime runtime, int sheet, uint row, ushort subrow, ushort ordinal, string source)
    {
        if (!runtime.TryGetRow(sheet, row, subrow, out var packRow))
            return (null, null);

        for (var i = 0; i < packRow.CellCount; i++)
        {
            var cell = runtime.GetCell(packRow, i, out var cellOrdinal);
            if (cellOrdinal != ordinal)
                continue;

            var decision = runtime.Decide(sheet, row, ordinal, cell, Encoding.UTF8.GetBytes(source));
            return (decision, decision == CellDecision.Applied ? Encoding.UTF8.GetString(cell.String, cell.Length) : null);
        }

        return (null, null);
    }
}

public sealed class TranslationRuntimeTests
{
    private static readonly StringColumn[] AddonColumns = [new(0, 4), new(2, 8)];

    private static TranslationRuntime Open(HpkBuilder builder) =>
        new(HpkFile.FromBytes(builder.Build(), HpkOpenMode.Full), "test", "test.hpk");

    [Fact]
    public void Sheets_bind_only_when_the_running_layout_matches_exactly()
    {
        using var runtime = Open(HpkBuilder.WithDefaultSheet());

        Assert.True(runtime.BindSheet("Addon", false, AddonColumns) >= 0);
        Assert.Equal(-1, runtime.BindSheet("Addon", false, [new StringColumn(0, 4), new StringColumn(2, 12)]));
        Assert.Equal(-1, runtime.BindSheet("Addon", false, [new StringColumn(0, 4)]));
        Assert.Equal(-1, runtime.BindSheet("Addon", true, AddonColumns));
        Assert.Equal(-1, runtime.BindSheet("Missing", false, AddonColumns));
        Assert.Equal(1, runtime.GetTotals().LayoutMismatchSheets);
    }

    [Fact]
    public void Cells_apply_only_to_the_source_they_were_made_for()
    {
        using var runtime = Open(HpkBuilder.WithDefaultSheet());
        var addon = runtime.BindSheet("Addon", false, AddonColumns);

        Assert.Equal((CellDecision.Applied, "Привет"), RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello"));
        Assert.Equal(CellDecision.SourceChanged, RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello!").Decision);
        Assert.Null(RuntimeProbe.Lookup(runtime, addon, 2, 0, 0, "Hello").Decision);

        var totals = runtime.GetTotals();
        Assert.Equal(1, totals.Applied);
        Assert.Equal(1, totals.SourceChanged);
        Assert.Equal(0.5, totals.MatchRate);
    }

    [Fact]
    public void Subrows_are_addressed_by_their_own_id()
    {
        using var runtime = Open(HpkBuilder.WithDefaultSheet());
        var quest = runtime.BindSheet("quest/000/Test", true, [new StringColumn(1, 0)]);

        Assert.Equal((CellDecision.Applied, "Первая"), RuntimeProbe.Lookup(runtime, quest, 3, 0, 0, "First"));
        Assert.Equal((CellDecision.Applied, "Вторая"), RuntimeProbe.Lookup(runtime, quest, 3, 1, 0, "Second"));
    }

    [Fact]
    public void Sheets_kept_in_the_game_language_are_not_bound()
    {
        using var runtime = new TranslationRuntime(
            HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full), "test", "test.hpk",
            new SheetFilter(["quest/*"]));

        Assert.Equal(-1, runtime.BindSheet("quest/000/Test", true, [new StringColumn(1, 0)]));
        Assert.True(runtime.BindSheet("Addon", false, AddonColumns) >= 0);
        Assert.Equal(1, runtime.UntranslatedSheets);
        Assert.Equal(0, runtime.GetTotals().LayoutMismatchSheets);
    }

    [Fact]
    public void Sheet_filter_matches_names_and_folders()
    {
        var filter = new SheetFilter(["Item", "quest/*", " "]);

        Assert.True(filter.Excludes("Item"));
        Assert.False(filter.Excludes("ItemUICategory"));
        Assert.True(filter.Excludes("quest/000/Test"));
        Assert.True(filter.ExcludedByFolder("quest/000/Test"));
        Assert.False(filter.Excludes("questlike"));
        Assert.False(filter.Excludes("cut_scene/000/Test"));
        Assert.Equal("cut_scene/*", SheetFilter.FolderOf("cut_scene/000/Test"));
        Assert.Null(SheetFilter.FolderOf("Item"));
        Assert.True(new SheetFilter(null).IsEmpty);
    }

    [Fact]
    public void Sheet_groups_have_unique_entries()
    {
        var entries = SheetGroups.All.SelectMany(static g => g.Entries).ToList();
        Assert.Equal(entries.Count, entries.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Sheet_stats_list_only_sheets_that_were_touched()
    {
        using var runtime = Open(HpkBuilder.WithDefaultSheet());
        var addon = runtime.BindSheet("Addon", false, AddonColumns);
        RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello");
        runtime.CountRebuilt(addon);

        var stats = Assert.Single(runtime.GetSheetStats());
        Assert.Equal("Addon", stats.SheetName);
        Assert.Equal(1, stats.Applied);
        Assert.Equal(1, stats.RowsRebuilt);
    }
}
