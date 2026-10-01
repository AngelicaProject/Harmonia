namespace Harmonia.Packs;

// Harmonia 0.1.4.0 and earlier kept installed packs next to the plugin
// assembly, in <plugin dir>/resources/packs. Dalamud installs every version
// into its own folder and deletes the old ones, so an update lost them. Packs
// now live in the plugin's configuration directory; at startup, when that has
// none yet, the packs an earlier build left next to an assembly are copied
// over: the running version's folder first, then other version folders.
public static class PackStorageMigration
{
    private const string LegacyResourcesDirName = "resources";
    private const string StagingDirName = ".staging";

    // Returns the number of translations copied.
    public static int CopyLegacyPacks(string pluginDir, string packsDir, IHarmoniaLog log)
    {
        try
        {
            if (HasPacks(packsDir))
                return 0;

            var source = Candidates(pluginDir).FirstOrDefault(HasPacks);
            if (source is null)
                return 0;

            var copied = 0;
            foreach (var pack in Directory.EnumerateDirectories(source))
            {
                var name = Path.GetFileName(pack);
                if (name == StagingDirName)
                    continue;

                var target = Path.Combine(packsDir, name);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(pack))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                copied++;
            }

            log.Info($"Copied {copied} installed translation(s) from {source} to {packsDir}.");
            return copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning("Installed translations of an earlier version could not be copied.", ex);
            return 0;
        }
    }

    private static IEnumerable<string> Candidates(string pluginDir)
    {
        yield return Path.Combine(pluginDir, LegacyResourcesDirName, TranslationPackStore.PacksDirName);

        var versions = Directory.GetParent(pluginDir);
        if (versions is null || !versions.Exists)
            yield break;

        foreach (var sibling in versions.EnumerateDirectories().OrderByDescending(static d => d.LastWriteTimeUtc))
        {
            if (!string.Equals(sibling.FullName.TrimEnd(Path.DirectorySeparatorChar), pluginDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(sibling.FullName, LegacyResourcesDirName, TranslationPackStore.PacksDirName);
        }
    }

    private static bool HasPacks(string packsDir) =>
        Directory.Exists(packsDir) &&
        Directory.EnumerateDirectories(packsDir).Any(static d => Path.GetFileName(d) != StagingDirName);
}
