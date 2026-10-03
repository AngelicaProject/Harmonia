using Harmonia.Compatibility;
using Harmonia.Packs;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Harmonia.Tests.Packs;
using Harmonia.Tests.Runtime;
using Xunit;

namespace Harmonia.Tests.Compatibility;

public sealed class CompatibilityProfileTests
{
    private static readonly StringColumn[] AddonColumns = [new(0, 4), new(2, 8)];

    [Fact]
    public void Built_in_profiles_load_and_name_distinct_plugins()
    {
        var profiles = CompatibilityProfile.All;

        Assert.Contains(profiles, static p => p.Plugin == "Lifestream");
        Assert.Equal(profiles.Count, profiles.Select(static p => p.Plugin).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(profiles, static p => Assert.True(p.Rows.Count + p.Sheets.Count + p.Sources.Count > 0, p.Plugin));
    }

    [Fact]
    public void Profiles_with_unknown_sources_or_no_plugin_are_rejected()
    {
        Assert.Throws<FormatException>(() => CompatibilityProfile.Parse("""{"plugin":"X","sources":["nope"]}"""));
        Assert.Throws<FormatException>(() => CompatibilityProfile.Parse("""{"rows":{"Addon":[1]}}"""));
        Assert.Equal("X", CompatibilityProfile.Parse("""{"plugin":"X"}""").Name);
    }

    [Fact]
    public void Only_installed_plugins_that_were_not_turned_off_apply()
    {
        var a = CompatibilityProfile.Parse("""{"plugin":"Alpha"}""");
        var b = CompatibilityProfile.Parse("""{"plugin":"Beta"}""");

        var installed = CompatibilityProfile.Installed([a, b], ["beta", "Other"]);

        Assert.Equal([b], installed);
        Assert.True(CompatibilityProfile.IsOn(b, ["Alpha"]));
        Assert.False(CompatibilityProfile.IsOn(b, ["BETA"]));
    }

    [Fact]
    public void Kept_rows_and_sheets_stay_in_the_game_language()
    {
        var profile = CompatibilityProfile.Parse("""
            {"plugin":"X","sheets":["quest/000/Test"],"rows":{"Addon":[7]},"sources":["aethernet-place-names"]}
            """);
        var keep = CompatibilityProfile.Keep([profile], static _ => [("Addon", 1u)]);
        using var runtime = new TranslationRuntime(
            HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full), "test", "test.hpk", null, keep);

        Assert.Equal(-1, runtime.BindSheet("quest/000/Test", true, [new StringColumn(1, 0)]));
        Assert.Equal(0, runtime.UntranslatedSheets);

        var addon = runtime.BindSheet("Addon", false, AddonColumns);
        Assert.Null(RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello").Decision);
        Assert.Null(RuntimeProbe.Lookup(runtime, addon, 7, 0, 1, "Hi").Decision);
        Assert.Equal(2, runtime.GetTotals().RowsKept);
        Assert.Equal(0, runtime.GetTotals().Applied);
    }

    [Fact]
    public void Rows_outside_the_profiles_are_translated()
    {
        var keep = CompatibilityProfile.Keep([CompatibilityProfile.Parse("""{"plugin":"X","rows":{"Addon":[7]}}""")], static _ => []);
        using var runtime = new TranslationRuntime(
            HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full), "test", "test.hpk", null, keep);
        var addon = runtime.BindSheet("Addon", false, AddonColumns);

        Assert.Equal((CellDecision.Applied, "Привет"), RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello"));
        Assert.Equal(0, runtime.GetTotals().RowsKept);
    }
}
