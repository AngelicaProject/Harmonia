using System.Text;
using Harmonia.Dictionary;
using Xunit;

namespace Harmonia.Tests.Dictionary;

public sealed class NameColumnTests
{
    // A row of String columns; the typed sheet reads the name from `name`.
    private static NameColumnSample Row(string name, params string[] columns) =>
        new(Encoding.UTF8.GetBytes(name), i => Encoding.UTF8.GetBytes(columns[i]));

    [Fact]
    public void Finds_the_column_that_holds_the_name_in_every_row()
    {
        // Item-like: the singular agrees with the name until a row tells them apart.
        var ordinal = NameColumn.Find(3,
        [
            Row("Gil", "Gil", "Gils", "Gil"),
            Row("Mythrite Ingot", "mythrite ingot", "mythrite ingots", "Mythrite Ingot"),
        ]);

        Assert.Equal(2, ordinal);
    }

    [Fact]
    public void Rows_without_a_name_say_nothing()
    {
        Assert.Equal(1, NameColumn.Find(2, [Row("", "a", "b"), Row("Holy", "Ruin", "Holy")]));
    }

    [Fact]
    public void Columns_that_always_agree_resolve_to_the_first()
    {
        Assert.Equal(0, NameColumn.Find(2, [Row("Holy", "Holy", "Holy"), Row("Ruin", "Ruin", "Ruin")]));
    }

    [Fact]
    public void A_name_no_column_holds_is_not_found()
    {
        Assert.Equal(-1, NameColumn.Find(2, [Row("Holy", "Holy", "x"), Row("Ruin", "x", "Ruinga")]));
        Assert.Equal(-1, NameColumn.Find(0, [Row("Holy")]));
    }

    [Fact]
    public void Reads_a_bounded_number_of_rows()
    {
        var read = 0;
        IEnumerable<NameColumnSample> Rows()
        {
            for (var i = 0; ; i++)
            {
                read++;
                yield return Row("Name" + i, "other", "Name" + i);
            }
        }

        Assert.Equal(1, NameColumn.Find(2, Rows()));
        Assert.Equal(NameColumn.Samples, read);
    }
}
