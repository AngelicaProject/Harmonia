using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Harmonia.Compatibility;

// A setting of the plugin itself, read once at startup from its configuration
// file under Dalamud's pluginConfigs. Path is a JSONPath; the condition holds
// when a value it selects equals EqualTo, differs from NotEqualTo, or, with
// neither, exists and is not null, false, or empty. A missing file or value
// gives Default: the plugin's own default for that setting.
public sealed record CompatibilityCondition(string Config, string Path, JToken? EqualTo, JToken? NotEqualTo, bool Default)
{
    // The settings window asks again every few seconds; a file is parsed
    // again only after it changes.
    private static readonly Dictionary<string, (DateTime Written, JToken Root)> Parsed = new(StringComparer.OrdinalIgnoreCase);

    public bool Holds(string configRoot)
    {
        JToken root;
        try
        {
            var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(configRoot, Config));
            if (!file.StartsWith(System.IO.Path.GetFullPath(configRoot), StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                return Default;
            root = Read(file);
        }
        catch (Exception)
        {
            // Being written, locked, or in a format the plugin no longer uses.
            return Default;
        }

        List<JToken> values;
        try
        {
            values = root.SelectTokens(Path).ToList();
        }
        catch (Exception)
        {
            return Default;
        }

        if (values.Count == 0)
            return Default;
        if (EqualTo is not null)
            return values.Any(v => JToken.DeepEquals(v, EqualTo));
        if (NotEqualTo is not null)
            return values.Any(v => !JToken.DeepEquals(v, NotEqualTo));
        return values.Any(static v => v.Type switch
        {
            JTokenType.Null or JTokenType.Undefined => false,
            JTokenType.Boolean => (bool)v,
            JTokenType.String => ((string?)v)?.Length > 0,
            JTokenType.Array or JTokenType.Object => v.HasValues,
            _ => true,
        });
    }

    private static JToken Read(string file)
    {
        var written = File.GetLastWriteTimeUtc(file);
        lock (Parsed)
        {
            if (Parsed.TryGetValue(file, out var cached) && cached.Written == written)
                return cached.Root;
        }

        var root = JToken.Parse(File.ReadAllText(file));
        lock (Parsed)
            Parsed[file] = (written, root);
        return root;
    }
}
