using System.Reflection;
using Newtonsoft.Json;
using Xunit;

namespace Harmonia.Tests;

/// <summary>
/// Localization dictionary tests: embedded en/ru JSON must be valid, contain no
/// empty values, and expose their embedded resource names under the
/// "Localization." prefix that Lang.LoadEmbedded expects. "en" is the baseline
/// covering every key used in code; other languages hold only user-facing keys
/// (a subset of en, no technical prefixes) since technical keys always resolve
/// to English (see Lang.TechnicalPrefixes).
/// </summary>
public class LocalizationDictionaryTests
{
    private static readonly Assembly PluginAssembly =
        typeof(Harmonia.Localization.Lang).Assembly;

    private const string EmbeddedPrefix = "Localization.";
    private const string EmbeddedSuffix = ".json";

    private static Dictionary<string, string> LoadEmbeddedDict(string code)
    {
        var name = PluginAssembly.GetManifestResourceNames()
            .Single(n => n.Equals(EmbeddedPrefix + code + EmbeddedSuffix, StringComparison.OrdinalIgnoreCase));
        using var stream = PluginAssembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
        Assert.NotNull(dict);
        return dict!;
    }

    private static readonly string[] Codes = ["en", "ru"];

    public static TheoryData<string> LanguageCodes() => new() { "en", "ru" };

    [Fact]
    public void Embedded_resources_are_present_for_all_bundled_languages()
    {
        var names = PluginAssembly.GetManifestResourceNames();
        foreach (var code in Codes)
            Assert.Contains(EmbeddedPrefix + code + EmbeddedSuffix, names);
    }

    [Theory]
    [MemberData(nameof(LanguageCodes))]
    public void Dictionary_parses_and_has_no_empty_values(string code)
    {
        var dict = LoadEmbeddedDict(code);
        Assert.NotEmpty(dict);
        Assert.All(dict, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{code}:{kv.Key} is empty"));
    }

    [Fact]
    public void Non_default_languages_are_subsets_of_en()
    {
        var en = LoadEmbeddedDict("en");
        foreach (var code in Codes.Where(c => !c.Equals("en", StringComparison.OrdinalIgnoreCase)))
        {
            var extra = LoadEmbeddedDict(code).Keys.Where(k => !en.ContainsKey(k)).ToList();
            Assert.True(extra.Count == 0, $"{code} has keys missing from en.json: " + string.Join(", ", extra));
        }
    }

    [Fact]
    public void Non_default_languages_contain_no_technical_keys()
    {
        var prefixes = Localization.Lang.TechnicalPrefixes;
        foreach (var code in Codes.Where(c => !c.Equals("en", StringComparison.OrdinalIgnoreCase)))
        {
            var leaked = LoadEmbeddedDict(code).Keys
                .Where(k => prefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.True(leaked.Count == 0, $"{code} must not translate technical keys: " + string.Join(", ", leaked));
        }
    }

    [Fact]
    public void Technical_keys_always_resolve_to_english()
    {
        var previous = Localization.Lang.CurrentLanguage;
        try
        {
            Localization.Lang.Initialize(null, "ru");
            Assert.True(Localization.Lang.SetLanguage("ru"));
            Assert.Equal("Source changed", Localization.Lang.T("diagnostics.source_changed"));
            Assert.Equal("Состояние", Localization.Lang.T("tab.status"));
        }
        finally
        {
            Localization.Lang.SetLanguage(previous);
        }
    }

    [Fact]
    public void Every_dictionary_has_a_display_name()
    {
        foreach (var code in Codes)
            Assert.False(string.IsNullOrWhiteSpace(LoadEmbeddedDict(code)["_name"]));
    }

    [Fact]
    public void Code_keys_are_covered_by_dictionaries()
    {
        var en = LoadEmbeddedDict("en");
        var used = ScanCodeKeys();
        Assert.NotEmpty(used);
        var missing = used.Where(k => !en.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Keys missing from en.json: " + string.Join(", ", missing));
    }

    [Fact]
    public void Dictionary_keys_are_not_leftover_placeholders()
    {
        // Values must never equal their own key (untranslated placeholder).
        foreach (var code in Codes)
        {
            var dict = LoadEmbeddedDict(code);
            Assert.All(dict.Where(kv => kv.Key != "_name"),
                kv => Assert.NotEqual(kv.Key, kv.Value));
        }
    }

    private static HashSet<string> ScanCodeKeys()
    {
        // Walk the repo for Lang.T("key") literals so the test catches new
        // untranslated keys even when dictionaries live outside the test project.
        var roots = new[]
        {
            FindRepoRoot(),                                        // <repo>/Harmonia
            Path.Combine(FindRepoRoot(), "..", "Harmonia.Tests"),  // sibling tests
        };

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(text, @"Lang\.T\(\s*""([^""]+)"""))
                {
                    // Real keys always contain a group dot; this also skips the
                    // dynamic wrapper Lang.T(labelKey) — "labelKey" has no dot.
                    var key = m.Groups[1].Value;
                    if (key.Length == 0 || !key.Contains('.')) continue;
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Harmonia", "Localization");
            if (Directory.Exists(candidate)) return Path.Combine(dir.FullName, "Harmonia");
            dir = dir.Parent!;
        }

        Assert.Fail("Could not locate Harmonia/Localization from the test bin directory.");
        return "";
    }
}
