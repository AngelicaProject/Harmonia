using Harmonia.Dictionary;
using Xunit;

namespace Harmonia.Tests.Dictionary;

public sealed class NameIndexTests
{
    private static NameEntry Item(uint row, string shown, string english, string? japanese = null) =>
        new(NameCategory.Item, row, shown,
            japanese is null ? [new("en", english)] : [new("en", english), new("ja", japanese)]);

    private static readonly NameIndex Index = NameIndex.Build(
    [
        Item(1, "Пальто Креденда защиты", "Credendum Coat of Fending", "クレデンダム・ディフェンダーコート"),
        Item(2, "Улучшенное пальто Креденда защиты", "Augmented Credendum Coat of Fending"),
        Item(3, "Мифриловый слиток", "Mythrite Ingot"),
        Item(4, "Coat of Arms", "Coat of Arms"),
        new(NameCategory.Action, 10, "Святое", [new("en", "Holy")]),
        new(NameCategory.Action, 11, "Святое", [new("en", "Holy")]),
        new(NameCategory.Status, 10, "Святое", [new("en", "Holy")]),
    ]);

    private static List<uint> Rows(string query, int limit = 10) =>
        [.. Index.Search(query, limit).Matches.Select(static m => m.RowId)];

    [Fact]
    public void Finds_a_name_by_any_of_its_languages()
    {
        Assert.Equal([1u, 2u], Rows("Credendum Coat of Fending"));
        Assert.Equal([1u, 2u], Rows("пальто креденда защиты"));
        Assert.Equal([1u], Rows("クレデンダム・ディフェンダーコート"));
    }

    [Fact]
    public void Ranks_exact_then_prefix_then_words_then_substring()
    {
        Assert.Equal([1u, 2u], Rows("credendum coat"));
        Assert.Equal([1u, 2u], Rows("coat credendum"));
        Assert.Equal([4u, 1u, 2u], Rows("coat"));
    }

    [Fact]
    public void Tolerates_typos_only_when_nothing_matches_as_typed()
    {
        Assert.Equal([3u], Rows("Mythirte Ingot"));
        Assert.Equal([3u], Rows("mithrite"));
        Assert.Empty(Rows("mithrite plate"));
    }

    [Fact]
    public void Finds_names_typed_with_the_wrong_keyboard_layout()
    {
        Assert.Equal([3u], Rows("Ьнеркшеу Штпще"));
        Assert.Equal([1u, 2u], Rows("gfkmnj rhtltylf"));
    }

    [Fact]
    public void Names_that_read_the_same_are_listed_once()
    {
        var result = Index.Search("holy", 10);
        Assert.Equal(2, result.Total);
        Assert.Equal([(NameCategory.Action, 10u), (NameCategory.Status, 10u)],
            result.Matches.Select(static m => (m.Category, m.RowId)));
    }

    [Fact]
    public void Limits_the_matches_but_counts_them_all()
    {
        var result = Index.Search("credendum", 1);
        Assert.Equal(2, result.Total);
        Assert.Equal(1u, Assert.Single(result.Matches).RowId);
    }

    [Fact]
    public void Ignores_queries_too_short_to_mean_anything()
    {
        Assert.Equal(0, Index.Search("c", 10).Total);
        Assert.Equal(0, Index.Search(" '’ ", 10).Total);
    }

    [Fact]
    public void Tells_translated_names_from_untranslated_ones()
    {
        Assert.True(Item(1, "Пальто", "Coat").IsTranslated);
        Assert.False(Item(4, "Coat of Arms", "Coat of Arms").IsTranslated);
    }
}
