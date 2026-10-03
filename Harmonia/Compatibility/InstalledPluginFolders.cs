using Newtonsoft.Json.Linq;

namespace Harmonia.Compatibility;

// The plugins Dalamud installed, read from its installedPlugins directory.
// Harmonia loads first, before Dalamud lists the other plugins, so this is
// the only way to know them when the session's text is decided.
public static class InstalledPluginFolders
{
    // InternalName and Name of every installed plugin: a folder per plugin
    // with a folder per version holding "<InternalName>.json". A plugin
    // scheduled for deletion or turned off in its manifest does not count.
    public static IReadOnlyList<string> Names(string installedPluginsDirectory)
    {
        var names = new List<string>();
        try
        {
            if (!Directory.Exists(installedPluginsDirectory))
                return names;

            foreach (var plugin in Directory.EnumerateDirectories(installedPluginsDirectory))
            {
                var internalName = Path.GetFileName(plugin);
                var manifest = Directory.EnumerateDirectories(plugin)
                    .Select(version => Path.Combine(version, internalName + ".json"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (manifest is null)
                    continue;

                JObject json;
                try
                {
                    json = JObject.Parse(File.ReadAllText(manifest));
                }
                catch (Exception)
                {
                    // An unreadable manifest still names an installed plugin.
                    names.Add(internalName);
                    continue;
                }

                if (json.Value<bool?>("ScheduledForDeletion") == true || json.Value<bool?>("Disabled") == true)
                    continue;

                names.Add(json.Value<string>("InternalName") ?? internalName);
                if (json.Value<string>("Name") is { Length: > 0 } name)
                    names.Add(name);
            }
        }
        catch (Exception)
        {
            // Whatever could be read counts; the rest is as if not installed.
        }

        return names;
    }
}
