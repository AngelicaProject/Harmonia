using System.Globalization;
using Newtonsoft.Json;

namespace Harmonia.Localization;

/// <summary>
/// UI localization. Dictionaries are flat JSON key → string files, one file per
/// language ("en.json", "ru.json", ...) in the plugin's "Localization" folder.
/// Files placed in that folder are loaded at startup (and on <see cref="Reload"/>),
/// so new languages can be added without recompiling the plugin. Embedded copies
/// of en/ru guarantee a usable baseline when the folder is missing.
/// Folder entries override embedded ones per key; per-file JSON errors are
/// reported through the optional warning callback and skipped.
/// A key resolves against the active language first, then the fallback language
/// ("en"), and finally returns the key itself.
/// Technical keys (see <see cref="TechnicalPrefixes"/>) always resolve against
/// the fallback language: their wording stays English in every UI
/// language and must not be duplicated into non-default dictionaries.
/// </summary>
public static class Lang
{
    /// <summary>Special dictionary key holding the human-readable language name.</summary>
    public const string NameKey = "_name";

    /// <summary>Language used as the fallback when a key is missing.</summary>
    public const string DefaultLanguage = "en";

    /// <summary>
    /// Key prefixes that are never translated: <see cref="T(string)"/> resolves
    /// them against the fallback language even when another language is active.
    /// Covers the per-sheet diagnostics table and the command help, which
    /// Dalamud reads once at load and lists with every other plugin's commands.
    /// </summary>
    public static readonly string[] TechnicalPrefixes = ["diagnostics.", "command."];

    private const string EmbeddedPrefix = "Localization.";
    private const string EmbeddedSuffix = ".json";

    private static readonly object gate = new();
    private static Dictionary<string, Dictionary<string, string>> languages = new(StringComparer.OrdinalIgnoreCase);
    private static string current = DefaultLanguage;
    private static string fallback = DefaultLanguage;
    private static string? folderPath;
    private static Action<string>? logWarning;

    /// <summary>Code of the active language.</summary>
    public static string CurrentLanguage
    {
        get { lock (gate) return current; }
    }

    /// <summary>Folder that is scanned for additional language files, if any.</summary>
    public static string? FolderPath => folderPath;

    /// <summary>Codes of all currently loaded languages, sorted.</summary>
    public static IReadOnlyCollection<string> AvailableLanguages
    {
        get
        {
            lock (gate)
                return [.. languages.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Human-readable name of a language ("Русский"), or the code itself when unnamed.</summary>
    public static string GetDisplayName(string code)
    {
        lock (gate)
            return languages.TryGetValue(code, out var dict) && dict.TryGetValue(NameKey, out var name)
                ? name
                : code;
    }

    /// <summary>Managed estimate over the loaded UI dictionaries (all languages).</summary>
    public static (int LanguageCount, int EntryCount, long EstimatedBytes) GetMemoryStats()
    {
        lock (gate)
        {
            var entries = 0;
            var bytes = 0L;
            foreach (var dict in languages.Values)
            {
                foreach (var (key, value) in dict)
                {
                    entries++;
                    bytes += 64 + ((key.Length + value.Length) * 2L);
                }
            }

            return (languages.Count, entries, bytes);
        }
    }

    /// <summary>
    /// Loads embedded dictionaries, then the folder, then applies the saved language.
    /// Call once during plugin construction.
    /// </summary>
    public static void Initialize(string? folderPath, string savedLanguage, Action<string>? logWarning = null)
    {
        Lang.folderPath = folderPath;
        Lang.logWarning = logWarning;
        Reload();
        SetLanguage(savedLanguage);
    }

    /// <summary>Rescans the Localization folder; folder entries override embedded ones per key.</summary>
    public static void Reload()
    {
        lock (gate)
        {
            var loaded = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (code, dict) in LoadEmbedded())
                loaded[code] = dict;

            foreach (var (code, dict) in LoadFolder())
            {
                if (loaded.TryGetValue(code, out var existing))
                {
                    foreach (var (key, value) in dict)
                        existing[key] = value;
                }
                else
                {
                    loaded[code] = dict;
                }
            }

            languages = loaded;
            if (!languages.ContainsKey(fallback))
                fallback = languages.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? DefaultLanguage;
            if (!languages.ContainsKey(current))
                current = fallback;
        }
    }

    /// <summary>Switches the active language. Returns false when the code is not loaded.</summary>
    public static bool SetLanguage(string code)
    {
        lock (gate)
        {
            var match = languages.Keys.FirstOrDefault(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return false;
            current = match;
            return true;
        }
    }

    /// <summary>
    /// Translates a key: active language → fallback language → the key itself.
    /// Technical keys (<see cref="TechnicalPrefixes"/>) skip the active language
    /// and always resolve against the fallback language.
    /// </summary>
    public static string T(string key)
    {
        lock (gate)
        {
            if (!IsTechnicalKey(key) &&
                languages.TryGetValue(current, out var dict) && dict.TryGetValue(key, out var value)) return value;
            if (languages.TryGetValue(fallback, out dict) && dict.TryGetValue(key, out value)) return value;
            return key;
        }
    }

    /// <summary><see cref="T(string)"/> with <c>{0}</c>-style formatting.</summary>
    public static string T(string key, params object?[] args)
    {
        var format = T(key);
        return args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
    }

    private static bool IsTechnicalKey(string key)
    {
        foreach (var prefix in TechnicalPrefixes)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static IEnumerable<KeyValuePair<string, Dictionary<string, string>>> LoadEmbedded()
    {
        var assembly = typeof(Lang).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(EmbeddedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(EmbeddedSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var code = name[EmbeddedPrefix.Length..^EmbeddedSuffix.Length];
            Dictionary<string, string>? dict = null;
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                logWarning?.Invoke($"Failed to read embedded localization '{name}': {ex.Message}");
            }

            if (dict is not null)
                yield return new(code, dict);
        }
    }

    private static IEnumerable<KeyValuePair<string, Dictionary<string, string>>> LoadFolder()
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            yield break;

        foreach (var path in Directory.GetFiles(folderPath, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(code))
                continue;

            Dictionary<string, string>? dict = null;
            try
            {
                dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                logWarning?.Invoke($"Failed to read localization file '{path}': {ex.Message}");
            }

            if (dict is not null)
                yield return new(code, dict);
        }
    }
}
