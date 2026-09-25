using System.IO.Compression;
using System.Security.Cryptography;
using Harmonia.Feeds;
using Harmonia.Packs;
using Harmonia.Packs.Hpk;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Harmonia.Tests.Packs;

internal sealed class NullLog : IHarmoniaLog
{
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warning(string message, Exception? exception = null) { }
    public void Error(string message, Exception? exception = null) { }
}

public sealed class PackInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Harmonia тест " + Guid.NewGuid().ToString("N"));
    private readonly Configuration configuration = new();
    private readonly TranslationPackStore store;
    private readonly PackInstaller installer;
    private int saves;

    public PackInstallerTests()
    {
        Directory.CreateDirectory(root);
        store = new TranslationPackStore(Path.Combine(root, "resources"), new NullLog(),
            new FuncGameVersionProvider(static () => "2026.08.12.0000.0000"), "1.0.0", "en");
        installer = new PackInstaller(store, configuration, () => saves++, new NullLog());
    }

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

    private string WritePack(HpkBuilder builder, bool brotli = false, string name = "pack")
    {
        var bytes = builder.Build();
        var path = Path.Combine(root, name + (brotli ? ".hpk.br" : ".hpk"));
        if (!brotli)
        {
            File.WriteAllBytes(path, bytes);
            return path;
        }

        using var file = File.Create(path);
        using var compressor = new BrotliStream(file, CompressionLevel.Optimal);
        compressor.Write(bytes);
        return path;
    }

    private static HpkBuilder Signed(ECDsa key, long sequence = 1)
    {
        var builder = HpkBuilder.WithDefaultSheet();
        builder.Signer = key;
        builder.Sequence = sequence;
        return builder;
    }

    [Fact]
    public void First_signed_import_needs_confirmation_then_pins_the_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var staged = installer.Stage(WritePack(Signed(key)));

        Assert.Equal(PublisherTrustState.FirstUse, staged.Trust);
        Assert.Throws<InvalidOperationException>(() => installer.Commit(staged, trustConfirmed: false));

        installer.Commit(staged, trustConfirmed: true);

        Assert.Equal(HpkBuilder.Fingerprint(key), configuration.PinnedPublisherKeys["test-pack"]);
        var pack = Assert.Single(store.Packs);
        Assert.True(pack.IsValid);
        Assert.True(pack.IsSelectable);
        Assert.True(pack.GameVersionMatches);
        Assert.Equal(staged.PackHash, pack.PackHash);
        Assert.False(File.Exists(staged.StagingPath));
        Assert.True(saves > 0);
    }

    [Fact]
    public void Update_signed_by_the_pinned_key_installs_without_asking()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var first = installer.Stage(WritePack(Signed(key), name: "one")))
            installer.Commit(first, trustConfirmed: true);

        using var second = installer.Stage(WritePack(Signed(key, sequence: 2), brotli: true, name: "two"));
        Assert.Equal(PublisherTrustState.Trusted, second.Trust);
        Assert.False(second.IsDowngrade);
        installer.Commit(second, trustConfirmed: false);

        Assert.Equal(2, store.TryGet("test-pack")!.Manifest!.Sequence);
    }

    [Fact]
    public void Rotated_key_is_accepted_and_repinned()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var first = installer.Stage(WritePack(Signed(oldKey), name: "one")))
            installer.Commit(first, trustConfirmed: true);

        var rotated = Signed(newKey, sequence: 2);
        rotated.PreviousSigner = oldKey;
        using var second = installer.Stage(WritePack(rotated, name: "two"));

        Assert.Equal(PublisherTrustState.Rotated, second.Trust);
        installer.Commit(second, trustConfirmed: false);
        Assert.Equal(HpkBuilder.Fingerprint(newKey), configuration.PinnedPublisherKeys["test-pack"]);
    }

    [Fact]
    public void Different_key_or_unsigned_pack_needs_explicit_confirmation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var first = installer.Stage(WritePack(Signed(key), name: "one")))
            installer.Commit(first, trustConfirmed: true);

        using (var changed = installer.Stage(WritePack(Signed(other, sequence: 2), name: "two")))
        {
            Assert.Equal(PublisherTrustState.KeyChanged, changed.Trust);
            Assert.Throws<InvalidOperationException>(() => installer.Commit(changed, trustConfirmed: false));
        }

        var unsignedBuilder = HpkBuilder.WithDefaultSheet();
        unsignedBuilder.Sequence = 3;
        using var unsigned = installer.Stage(WritePack(unsignedBuilder, name: "three"));
        Assert.Equal(PublisherTrustState.Unsigned, unsigned.Trust);
        installer.Commit(unsigned, trustConfirmed: true);

        // An unsigned install keeps the pin, so a later feed update is still checked against it.
        Assert.Equal(HpkBuilder.Fingerprint(key), configuration.PinnedPublisherKeys["test-pack"]);
        Assert.False(store.TryGet("test-pack")!.IsSigned);
    }

    [Fact]
    public void Older_release_is_reported_as_downgrade()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var first = installer.Stage(WritePack(Signed(key, sequence: 5), name: "one")))
            installer.Commit(first, trustConfirmed: true);

        using var older = installer.Stage(WritePack(Signed(key, sequence: 4), name: "two"));
        Assert.True(older.IsDowngrade);
        Assert.Equal(5, older.InstalledSequence);
    }

    [Fact]
    public void Invalid_file_is_rejected_and_leaves_no_staging_file()
    {
        var path = Path.Combine(root, "garbage.hpk.br");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9]);

        Assert.ThrowsAny<Exception>(() => installer.Stage(path));
        Assert.Empty(Directory.Exists(store.StagingDir) ? Directory.GetFiles(store.StagingDir) : []);
    }

    [Fact]
    public void Oversized_decompressed_pack_is_rejected()
    {
        var path = WritePack(HpkBuilder.WithDefaultSheet(), brotli: true);
        Assert.Throws<HpkFormatException>(() => installer.Stage(path, maxUnpackedBytes: 100));
    }

    [Fact]
    public void Store_lists_folders_without_an_install_record_as_invalid_and_removes_stale_files()
    {
        var unrecorded = Path.Combine(store.PacksDir, "old-pack");
        Directory.CreateDirectory(unrecorded);
        File.WriteAllText(Path.Combine(unrecorded, "manifest.json"), "{}");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var first = installer.Stage(WritePack(Signed(key), name: "one")))
            installer.Commit(first, trustConfirmed: true);
        using (var second = installer.Stage(WritePack(Signed(key, sequence: 2), name: "two")))
            installer.Commit(second, trustConfirmed: false);

        var packDir = Path.Combine(store.PacksDir, "test-pack");
        Assert.Equal(2, Directory.GetFiles(packDir, "*.hpk").Length);

        store.RemoveStaleFiles();
        Assert.Single(Directory.GetFiles(packDir, "*.hpk"));
        Assert.False(store.TryGet("old-pack")!.IsValid);

        using var runtime = store.OpenForRuntime("test-pack", out var error);
        Assert.Null(error);
        Assert.Equal(2, runtime!.Manifest.Sequence);
    }

    [Fact]
    public void Tampered_installed_file_fails_runtime_verification()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var staged = installer.Stage(WritePack(Signed(key))))
            installer.Commit(staged, trustConfirmed: true);

        var file = store.TryGet("test-pack")!.FilePath!;
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        Assert.Null(store.OpenForRuntime("test-pack", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Pack_for_another_client_language_is_not_selectable()
    {
        var builder = HpkBuilder.WithDefaultSheet();
        builder.SourceLanguage = "ja";
        using (var staged = installer.Stage(WritePack(builder)))
            installer.Commit(staged, trustConfirmed: true);

        var pack = store.TryGet("test-pack")!;
        Assert.True(pack.IsValid);
        Assert.False(pack.LanguageCompatible);
        Assert.False(pack.IsSelectable);
    }

    [Fact]
    public void Install_record_names_the_pack_hash()
    {
        using (var staged = installer.Stage(WritePack(HpkBuilder.WithDefaultSheet())))
            installer.Commit(staged, trustConfirmed: true);

        var record = JObject.Parse(File.ReadAllText(Path.Combine(store.PacksDir, "test-pack", "installed.json")));
        Assert.Equal(1, (int)record["formatVersion"]!);
        Assert.Equal(store.TryGet("test-pack")!.PackHash, (string)record["packHash"]!);
    }
}
