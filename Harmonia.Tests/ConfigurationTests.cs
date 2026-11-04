using System.Text.Json;
using Xunit;

namespace Harmonia.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Missing_translation_options_use_stable_defaults()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>("{}")!;
        Assert.True(configuration.TranslatePluginData);
        Assert.True(configuration.OriginalSayPhrases);
    }

    [Fact]
    public void Old_experimental_settings_adopt_stable_defaults_once()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>(
            """{"TranslatePluginData":false,"OriginalSayPhrases":false}""")!;
        Assert.True(configuration.ApplyStableTranslationDefaults());
        Assert.True(configuration.TranslatePluginData);
        Assert.True(configuration.OriginalSayPhrases);
        Assert.True(configuration.StableTranslationDefaultsApplied);

        configuration.TranslatePluginData = false;
        configuration.OriginalSayPhrases = false;
        var reloaded = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(configuration))!;
        Assert.False(reloaded.ApplyStableTranslationDefaults());
        Assert.False(reloaded.TranslatePluginData);
        Assert.False(reloaded.OriginalSayPhrases);
    }

    [Fact]
    public void Saved_stable_opt_out_is_preserved()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>(
            """{"TranslatePluginData":false,"OriginalSayPhrases":false,"StableTranslationDefaultsApplied":true}""")!;
        Assert.False(configuration.ApplyStableTranslationDefaults());
        Assert.False(configuration.TranslatePluginData);
        Assert.False(configuration.OriginalSayPhrases);
    }
}
