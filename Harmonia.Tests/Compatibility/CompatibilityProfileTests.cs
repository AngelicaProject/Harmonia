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

        Assert.Equal(
            ["Artisan", "AutoRetainer", "Henchman", "Lifestream", "PandorasBox", "Questionable", "SimpleTweaksPlugin", "TextAdvance", "TriadBuddy", "YesAlready"],
            profiles.Select(static p => p.Plugin).Order(StringComparer.Ordinal));
        Assert.All(profiles, static p => Assert.Contains(p.Parts, static part =>
            part.Rows.Count + part.Sheets.Count + part.Cells.Count + part.Sources.Count > 0));
        Assert.All(profiles.SelectMany(static p => p.Parts).SelectMany(static part => part.When.Concat(part.Require)),
            static c => Assert.False(Path.IsPathRooted(c.Config), c.Config));
    }

    [Fact]
    public void Profiles_with_unknown_sources_or_no_plugin_are_rejected()
    {
        Assert.Throws<FormatException>(() => CompatibilityProfile.Parse("""{"plugin":"X","sources":["nope"]}"""));
        Assert.Throws<FormatException>(() => CompatibilityProfile.Parse("""{"rows":{"Addon":[1]}}"""));
        Assert.Throws<FormatException>(() => CompatibilityProfile.Parse("""{"plugin":"X","cells":[{"sheet":"Addon","columns":[0],"source":"nope"}]}"""));
        Assert.Throws<FormatException>(() => CompatibilityProfile.Parse("""{"plugin":"X","cells":[{"sheet":"Addon","columns":[0],"rows":[1],"source":"triple-triad-npcs"}]}"""));
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
    public void Plugin_names_match_by_letters_and_digits()
    {
        var pandora = CompatibilityProfile.Parse("""{"plugin":"PandorasBox"}""");

        Assert.Single(CompatibilityProfile.Installed([pandora], ["Pandora's Box"]));
        Assert.Empty(CompatibilityProfile.Installed([pandora], ["Pandora"]));
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
        Assert.Equal(3, runtime.GetTotals().Kept);
        Assert.Equal(0, runtime.GetTotals().Applied);
    }

    [Fact]
    public void Kept_columns_leave_the_other_columns_translated()
    {
        // Addon's layout is columns 0 and 2: ordinal 1 is column 2.
        var everyRow = CompatibilityProfile.Keep([CompatibilityProfile.Parse("""{"plugin":"X","cells":[{"sheet":"Addon","columns":[2]}]}""")], static _ => []);
        using var runtime = new TranslationRuntime(
            HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full), "test", "test.hpk", null, everyRow);
        var addon = runtime.BindSheet("Addon", false, AddonColumns);

        Assert.Equal((CellDecision.Applied, "Привет"), RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello"));
        Assert.Equal(CellDecision.Kept, RuntimeProbe.Lookup(runtime, addon, 1, 0, 1, "World").Decision);
        Assert.Equal(CellDecision.Kept, RuntimeProbe.Lookup(runtime, addon, 7, 0, 1, "Hi").Decision);

        var oneRow = CompatibilityProfile.Keep([CompatibilityProfile.Parse("""{"plugin":"X","cells":[{"sheet":"Addon","columns":[2],"rows":[7]}]}""")], static _ => []);
        using var narrow = new TranslationRuntime(
            HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full), "test", "test.hpk", null, oneRow);
        addon = narrow.BindSheet("Addon", false, AddonColumns);

        Assert.Equal((CellDecision.Applied, "Мир"), RuntimeProbe.Lookup(narrow, addon, 1, 0, 1, "World"));
        Assert.Equal(CellDecision.Kept, RuntimeProbe.Lookup(narrow, addon, 7, 0, 1, "Hi").Decision);
        Assert.Equal(1, narrow.GetTotals().Kept);
    }

    [Fact]
    public void Kept_columns_can_take_their_rows_from_a_source()
    {
        var profile = CompatibilityProfile.Parse("""{"plugin":"X","cells":[{"sheet":"Addon","columns":[2],"source":"triple-triad-npcs"}]}""");
        var keep = CompatibilityProfile.Keep([profile], static _ => [("Addon", 7u), ("Other", 1u)]);
        using var runtime = new TranslationRuntime(
            HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full), "test", "test.hpk", null, keep);
        var addon = runtime.BindSheet("Addon", false, AddonColumns);

        Assert.Equal((CellDecision.Applied, "Мир"), RuntimeProbe.Lookup(runtime, addon, 1, 0, 1, "World"));
        Assert.Equal(CellDecision.Kept, RuntimeProbe.Lookup(runtime, addon, 7, 0, 1, "Hi").Decision);
        Assert.Null(keep.CellsOf("Other"));
        Assert.Equal(1, runtime.GetTotals().Kept);
    }

    [Fact]
    public void Optional_parts_follow_the_plugin_settings()
    {
        var dir = Directory.CreateTempSubdirectory("harmonia-compat-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "X"));
            File.WriteAllText(Path.Combine(dir.FullName, "X", "DefaultConfig.json"),
                """{"Nav": true, "Mount": -1, "Data": [{"Subs": []}, {"Subs": [1]}], "Tweaks": ["A", "B"]}""");
            var profile = CompatibilityProfile.Parse("""
                {"plugin":"X","optional":[
                  {"when":[{"config":"X/DefaultConfig.json","path":"Nav","equals":true}],"rows":{"Addon":[1]}},
                  {"when":[{"config":"X/DefaultConfig.json","path":"Mount","notEquals":-1}],"rows":{"Addon":[2]}},
                  {"when":[{"config":"X/DefaultConfig.json","path":"$.Data[*].Subs[0]","default":false}],"rows":{"Addon":[3]}},
                  {"when":[{"config":"X/DefaultConfig.json","path":"$.Tweaks[?(@ == 'C')]","default":false}],"rows":{"Addon":[4]}},
                  {"require":[{"config":"X/DefaultConfig.json","path":"Nav","equals":true},{"config":"X/DefaultConfig.json","path":"Mount","equals":0}],"rows":{"Addon":[5]}},
                  {"when":[{"config":"Missing.json","path":"Anything","default":true}],"rows":{"Addon":[6]}},
                  {"when":[{"config":"../outside.json","path":"Anything","default":false}],"rows":{"Addon":[7]}}
                ]}
                """);

            var kept = CompatibilityProfile.Keep([profile], static _ => [], c => c.Holds(dir.FullName)).RowsOf("Addon");

            Assert.NotNull(kept);
            Assert.Equal([1u, 3u, 6u], kept.Order());
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void A_broken_profile_is_skipped_and_the_others_load()
    {
        var (profiles, errors) = CompatibilityProfile.Load(
        [
            ("good.json", """{"plugin":"Good","rows":{"Addon":[1]}}"""),
            ("bad.json", """{"plugin":"Bad","rows":{"Addon":["x"]}}"""),
            ("unknown.json", """{"plugin":"Unknown","sources":["nope"]}"""),
            ("twice.json", """{"plugin":"good"}"""),
            ("garbage.json", "{"),
        ]);

        Assert.Equal(["Good"], profiles.Select(static p => p.Plugin));
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void Unreadable_settings_give_the_default()
    {
        var dir = Directory.CreateTempSubdirectory("harmonia-compat-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir.FullName, "Changed.json"), """{"Nav": {"Enabled": true}}""");

            Assert.True(new CompatibilityCondition("Broken.json", "Nav", null, null, true).Holds(dir.FullName));
            Assert.False(new CompatibilityCondition("Broken.json", "Nav", null, null, false).Holds(dir.FullName));
            Assert.True(new CompatibilityCondition("Changed.json", "Nav", true, null, true).Holds(dir.FullName) is false);
            Assert.True(new CompatibilityCondition("Changed.json", "$[?(", null, null, true).Holds(dir.FullName));
            Assert.False(new CompatibilityCondition("Missing.json", "Nav", null, null, false).Holds(Path.Combine(dir.FullName, "nowhere")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Kept_text_that_the_pack_lacks_changes_nothing()
    {
        var profile = CompatibilityProfile.Parse("""
            {"plugin":"X","sheets":["Gone"],"rows":{"Addon":[999],"Gone":[1]},"cells":[{"sheet":"Addon","columns":[77]},{"sheet":"Gone","columns":[0]}]}
            """);
        using var runtime = new TranslationRuntime(
            HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full), "test", "test.hpk", null,
            CompatibilityProfile.Keep([profile], static _ => throw new InvalidOperationException("never asked")));
        var addon = runtime.BindSheet("Addon", false, AddonColumns);

        Assert.Equal((CellDecision.Applied, "Привет"), RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello"));
        Assert.Equal((CellDecision.Applied, "Мир"), RuntimeProbe.Lookup(runtime, addon, 1, 0, 1, "World"));
        Assert.Equal(0, runtime.GetTotals().Kept);
    }

    [Fact]
    public void Installed_plugins_are_read_from_their_folders()
    {
        var dir = Directory.CreateTempSubdirectory("harmonia-installed-");
        try
        {
            void Install(string folder, string version, string manifest)
            {
                var path = Directory.CreateDirectory(Path.Combine(dir.FullName, folder, version)).FullName;
                File.WriteAllText(Path.Combine(path, folder + ".json"), manifest);
            }

            Install("AutoRetainer", "4.6.0.9", """{"InternalName":"AutoRetainer","Name":"AutoRetainer"}""");
            Install("PandorasBox", "1.0", """{"InternalName":"PandorasBox","Name":"Pandora's Box"}""");
            Install("Removed", "1.0", """{"InternalName":"Removed","ScheduledForDeletion":true}""");
            Install("Broken", "1.0", "{");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "Empty"));

            var names = InstalledPluginFolders.Names(dir.FullName);

            Assert.Equal(["AutoRetainer", "AutoRetainer", "Broken", "Pandora's Box", "PandorasBox"], names.Order(StringComparer.Ordinal));
            Assert.Empty(InstalledPluginFolders.Names(Path.Combine(dir.FullName, "missing")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Rows_outside_the_profiles_are_translated()
    {
        var keep = CompatibilityProfile.Keep([CompatibilityProfile.Parse("""{"plugin":"X","rows":{"Addon":[7]}}""")], static _ => []);
        using var runtime = new TranslationRuntime(
            HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full), "test", "test.hpk", null, keep);
        var addon = runtime.BindSheet("Addon", false, AddonColumns);

        Assert.Equal((CellDecision.Applied, "Привет"), RuntimeProbe.Lookup(runtime, addon, 1, 0, 0, "Hello"));
        Assert.Equal(0, runtime.GetTotals().Kept);
    }

    [Fact]
    public void Plugins_given_the_translation_keep_only_what_they_do_not_read_from_the_game_files()
    {
        var profile = CompatibilityProfile.Parse("""
            {"plugin":"X","sheets":["World"],"rows":{"Addon":[1,2,3]},
             "hardcoded":{"rows":{"Addon":[2]}},
             "optional":[
               {"when":[{"config":"X.json","path":"On","equals":true,"default":true}],"rows":{"Lobby":[5]},"hardcoded":true},
               {"when":[{"config":"X.json","path":"On","equals":true,"default":true}],"rows":{"Warp":[9]}}]}
            """);

        var all = CompatibilityProfile.Keep([profile], static _ => []);
        Assert.True(all.KeepsSheet("World"));
        Assert.Equal([1u, 2u, 3u], all.RowsOf("Addon")!.Order());
        Assert.NotNull(all.RowsOf("Lobby"));
        Assert.NotNull(all.RowsOf("Warp"));

        var hardcoded = CompatibilityProfile.Keep([profile], static _ => [], null, hardcodedOnly: true);
        Assert.False(hardcoded.KeepsSheet("World"));
        Assert.Equal([2u], hardcoded.RowsOf("Addon")!);
        Assert.Equal([5u], hardcoded.RowsOf("Lobby")!);
        Assert.Null(hardcoded.RowsOf("Warp"));

        // A condition that fails keeps nothing of its part in either case.
        Assert.Null(CompatibilityProfile.Keep([profile], static _ => [], static _ => false, hardcodedOnly: true).RowsOf("Lobby"));

        Assert.True(profile.KeepsAnything(null, hardcodedOnly: true));
        Assert.False(CompatibilityProfile.Parse("""{"plugin":"Y","rows":{"Addon":[1]}}""").KeepsAnything(null, hardcodedOnly: true));
        Assert.Throws<FormatException>(() => CompatibilityProfile.Parse("""{"plugin":"Z","hardcoded":7}"""));
    }

    [Fact]
    public void Built_in_profiles_keep_text_for_four_plugins_when_plugins_are_given_the_translation()
    {
        Assert.Empty(CompatibilityProfile.LoadErrors);
        var keeping = CompatibilityProfile.All.Where(static p => p.KeepsAnything(null, hardcodedOnly: true)).Select(static p => p.Plugin);

        Assert.Equal(["AutoRetainer", "Henchman", "Lifestream", "TextAdvance"], keeping.Order(StringComparer.Ordinal));

        // What stays is always part of what the whole profile keeps.
        foreach (var profile in CompatibilityProfile.All)
        {
            var whole = CompatibilityProfile.Keep([profile], static _ => []);
            foreach (var part in profile.Parts.Select(static p => p.Hardcoded).OfType<CompatibilityPart>())
            {
                Assert.All(part.Sheets, sheet => Assert.True(whole.KeepsSheet(sheet)));
                foreach (var (sheet, rows) in part.Rows)
                    Assert.Subset(whole.RowsOf(sheet)!.ToHashSet(), rows.ToHashSet());
            }
        }
    }
}
