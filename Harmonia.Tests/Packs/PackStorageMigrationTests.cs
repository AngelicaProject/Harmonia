using Harmonia.Packs;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed class PackStorageMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Harmonia миграция " + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private string Version(string name) => Path.Combine(root, "installedPlugins", "HarmoniaEngine", name);

    private static void Pack(string versionDir, string id)
    {
        var dir = Path.Combine(versionDir, "resources", "packs", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "installed.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "abc.hpk"), "pack");
        Directory.CreateDirectory(Path.Combine(versionDir, "resources", "packs", ".staging"));
    }

    [Fact]
    public void Packs_next_to_the_running_assembly_are_copied_once()
    {
        var current = Version("0.1.4.0");
        Pack(current, "9287330bec79");
        var packsDir = Path.Combine(root, "pluginConfigs", "HarmoniaEngine", "packs");

        Assert.Equal(1, PackStorageMigration.CopyLegacyPacks(current, packsDir, new NullLog()));
        Assert.True(File.Exists(Path.Combine(packsDir, "9287330bec79", "abc.hpk")));
        Assert.False(Directory.Exists(Path.Combine(packsDir, ".staging")));

        // Packs already in the configuration directory are never overwritten.
        Assert.Equal(0, PackStorageMigration.CopyLegacyPacks(current, packsDir, new NullLog()));
    }

    [Fact]
    public void Packs_of_another_version_folder_are_copied_when_the_running_one_has_none()
    {
        var old = Version("0.1.3.1");
        Pack(old, "aaaaaaaaaaaa");
        var current = Version("0.1.5.0");
        Directory.CreateDirectory(current);
        var packsDir = Path.Combine(root, "pluginConfigs", "HarmoniaEngine", "packs");

        Assert.Equal(1, PackStorageMigration.CopyLegacyPacks(current, packsDir, new NullLog()));
        Assert.True(File.Exists(Path.Combine(packsDir, "aaaaaaaaaaaa", "installed.json")));
    }

    [Fact]
    public void Nothing_to_copy_is_fine()
    {
        var current = Version("0.1.5.0");
        Directory.CreateDirectory(current);
        Assert.Equal(0, PackStorageMigration.CopyLegacyPacks(current, Path.Combine(root, "packs"), new NullLog()));
    }
}
