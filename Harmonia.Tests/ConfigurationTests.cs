using System.Text.Json;
using Xunit;

namespace Harmonia.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Existing_dictionary_shortcut_keeps_ctrl_shift_when_modifiers_are_missing()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>(
            """{"DictionaryShortcutKey":"F8"}""")!;
        Assert.Equal("F8", configuration.DictionaryShortcutKey);
        Assert.Equal(3, configuration.DictionaryShortcutModifiers);
        var custom = JsonSerializer.Deserialize<Configuration>(
            """{"DictionaryShortcutKey":"F12","DictionaryShortcutModifiers":0}""")!;
        Assert.Equal(0, custom.DictionaryShortcutModifiers);
    }

    [Fact]
    public void Missing_translation_options_use_stable_defaults()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>("{}")!;
        Assert.True(configuration.TranslatePluginData);
        Assert.True(configuration.OriginalSayPhrases);
        Assert.False(configuration.DictionaryTabFocusSearch);
        Assert.False(configuration.DictionaryClearSearchOnOpen);
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
