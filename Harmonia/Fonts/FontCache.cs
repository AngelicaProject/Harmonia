using Newtonsoft.Json;

namespace Harmonia.Fonts;

public sealed record FontCacheEntry(IReadOnlyDictionary<string, string> Files, IReadOnlyList<FontTargetReport> Reports);

// Patched font files per (game version, packHash) under <root>/<key>/, laid
// out by game path. entry.json is written last, so a directory without it is
// incomplete and rebuilt. Only the current key is kept.
public sealed class FontCache
{
    private const string EntryFile = "entry.json";

    private readonly string root;

    public FontCache(string root)
    {
        this.root = root;
    }

    public static string Key(string gameVersion, byte[] packHash)
    {
        var version = new string(gameVersion.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return version + "-" + Convert.ToHexStringLower(packHash);
    }

    public FontCacheEntry? TryLoad(string key)
    {
        var dir = Path.Combine(root, key);
        var entryPath = Path.Combine(dir, EntryFile);
        if (!File.Exists(entryPath))
            return null;

        try
        {
            var stored = JsonConvert.DeserializeObject<StoredEntry>(File.ReadAllText(entryPath));
            if (stored?.Files is null || stored.Reports is null)
                return null;

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var gamePath in stored.Files)
            {
                var file = FilePath(dir, gamePath);
                if (!File.Exists(file))
                    return null;
                files[gamePath] = file;
            }

            return new FontCacheEntry(files, stored.Reports);
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            return null;
        }
    }

    public FontCacheEntry Store(string key, FontPatchResult result)
    {
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, key);
        var staging = dir + ".tmp";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (gamePath, bytes) in result.Files)
        {
            var file = FilePath(staging, gamePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
            files[gamePath] = FilePath(dir, gamePath);
        }

        var entry = new StoredEntry { Files = [.. result.Files.Keys.Order(StringComparer.Ordinal)], Reports = [.. result.Reports] };
        File.WriteAllText(Path.Combine(staging, EntryFile), JsonConvert.SerializeObject(entry, Formatting.Indented));
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.Move(staging, dir);
        return new FontCacheEntry(files, result.Reports);
    }

    // Removes every cached key except `keep`.
    public void RemoveOthers(string? keep)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(dir), keep, StringComparison.Ordinal))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string FilePath(string dir, string gamePath)
    {
        if (gamePath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(gamePath))
            throw new ArgumentException("Invalid game path.", nameof(gamePath));
        return Path.Combine(dir, gamePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class StoredEntry
    {
        public List<string>? Files { get; set; }
        public List<FontTargetReport>? Reports { get; set; }
    }
}
