using Harmonia.Runtime;
using Xunit;

namespace Harmonia.Tests;

public sealed class ActionNameAliasesTests
{
    [Fact]
    public void Original_names_find_the_shown_name_without_changing_translated_input()
    {
        var aliases = new ActionNameAliases([(7389, "Inner Release", "Внутреннее освобождение")]);
        var found = Assert.Single(aliases.Find("inner release"));
        Assert.Equal("Внутреннее освобождение", found.Shown);
        Assert.Contains(7389u, found.Rows);
        Assert.Empty(aliases.Find("Внутреннее освобождение"));
        Assert.Empty(aliases.Find("Inner Release <me>"));
        Assert.Empty(aliases.Find("Unknown"));
    }

    [Fact]
    public void Duplicate_names_keep_all_matching_rows_and_distinct_translations()
    {
        var aliases = new ActionNameAliases([
            (1, "Same", "Первый"), (2, "Same", "Первый"), (3, "Same", "Второй"),
            (4, "Different", "Первый"), (5, "Unchanged", "Unchanged"),
        ]);
        var found = aliases.Find("Same");
        Assert.Equal(2, found.Count);
        Assert.Equal(new HashSet<uint> { 1, 2 }, found[0].Rows);
        Assert.DoesNotContain(4u, found[0].Rows);
        Assert.Contains(3u, found[1].Rows);
        Assert.Empty(aliases.Find("Unchanged"));
    }
}
