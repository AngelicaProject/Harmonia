using Harmonia.Packs.Hpk;
using Xunit;

namespace Harmonia.Tests.Packs;

// TestData/well_formed.vectors.txt is a copy of Aeria's
// crates/aeria-se/tests/fixtures/well_formed.vectors.txt: the well-formed
// string rule of Pack Format v1, with every macro text golden vector and
// real game strings. Update the copy when Aeria changes it.
public class SeStringCheckTests
{
    private static readonly string VectorsPath = Path.Combine(AppContext.BaseDirectory, "TestData", "well_formed.vectors.txt");

    public static TheoryData<string, bool, string> Vectors()
    {
        var data = new TheoryData<string, bool, string>();
        foreach (var line in File.ReadLines(VectorsPath))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('\t');
            var expected = parts[0] switch
            {
                "yes" => true,
                "no" => false,
                _ => throw new FormatException($"Unexpected answer in '{line}'."),
            };
            data.Add(parts[1], expected, parts.Length > 2 ? parts[2] : "");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void MatchesAeriaVectors(string name, bool expected, string hex)
    {
        Assert.True(expected == SeStringCheck.IsWellFormed(Convert.FromHexString(hex)), name);
    }

    [Fact]
    public void VectorsCoverAcceptedAndRejectedStrings()
    {
        var answers = Vectors().Select(row => (bool)row[1]).ToList();
        Assert.Contains(true, answers);
        Assert.Contains(false, answers);
    }
}
