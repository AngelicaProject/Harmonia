using System.IO.Compression;
using System.Security.Cryptography;
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
    private const string FeedUrl = "https://owner.github.io/ru/harmonia/feed-v1.json";

    private readonly string root = Path.Combine(Path.GetTempPath(), "Harmonia тест " + Guid.NewGuid().ToString("N"));
    private readonly TranslationPackStore store;
    private readonly PackInstaller installer;

    public PackInstallerTests()
    {
        Directory.CreateDirectory(root);
        store = new TranslationPackStore(Path.Combine(root, "resources"), new NullLog(),
            new FuncGameVersionProvider(static () => "2026.08.12.0000.0000"), "1.0.0", "en");
        installer = new PackInstaller(store, new NullLog());
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

    private static HpkBuilder Signed(ECDsa key, int release = 1)
    {
        var builder = HpkBuilder.WithDefaultSheet();
        builder.Signer = key;
        builder.Version = $"2026.10.01.{release:D4}";
        return builder;
    }

    private string Install(HpkBuilder builder, string name, string? feedUrl = null)
    {
        using var staged = installer.Stage(WritePack(builder, name: name), feedUrl: feedUrl);
        return installer.Commit(staged, trustConfirmed: true);
    }

    [Fact]
    public void First_signed_import_needs_confirmation_then_pins_the_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var staged = installer.Stage(WritePack(Signed(key)));

        Assert.Equal(PublisherTrustState.FirstUse, staged.Trust);
        Assert.Null(staged.Target);
        Assert.Throws<InvalidOperationException>(() => installer.Commit(staged, trustConfirmed: false));

        var id = installer.Commit(staged, trustConfirmed: true);

        var pack = Assert.Single(store.Packs);
        Assert.Equal(id, pack.Id);
        Assert.Matches("^[0-9a-f]{12}$", id);
        Assert.Equal(HpkBuilder.Fingerprint(key), pack.PinnedKey);
        Assert.True(pack.IsValid);
        Assert.True(pack.IsSelectable);
        Assert.True(pack.GameVersionMatches);
        Assert.Equal(staged.PackHash, pack.PackHash);
        Assert.False(File.Exists(staged.StagingPath));
    }

    [Fact]
    public void A_file_signed_by_the_pinned_key_updates_that_translation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Install(Signed(key), "one");

        using var second = installer.Stage(WritePack(Signed(key, release: 2), brotli: true, name: "two"));
        Assert.Equal(PublisherTrustState.Trusted, second.Trust);
        Assert.Equal(id, second.Target?.Id);
        Assert.False(second.IsDowngrade);
        Assert.Equal(id, installer.Commit(second, trustConfirmed: false));

        Assert.Equal("2026.10.01.0002", Assert.Single(store.Packs).Manifest!.Version);
    }

    [Fact]
    public void Rotated_key_is_accepted_and_repinned()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Install(Signed(oldKey), "one");

        var rotated = Signed(newKey, release: 2);
        rotated.PreviousSigner = oldKey;
        using var second = installer.Stage(WritePack(rotated, name: "two"));

        Assert.Equal(PublisherTrustState.Rotated, second.Trust);
        Assert.Equal(id, installer.Commit(second, trustConfirmed: false));
        Assert.Equal(HpkBuilder.Fingerprint(newKey), store.TryGet(id)!.PinnedKey);
    }

    [Fact]
    public void A_file_signed_by_another_key_is_another_translation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = Install(Signed(key), "one");

        using var staged = installer.Stage(WritePack(Signed(other, release: 2), name: "two"));
        Assert.Equal(PublisherTrustState.FirstUse, staged.Trust);
        Assert.Null(staged.Target);
        var second = installer.Commit(staged, trustConfirmed: true);

        Assert.NotEqual(first, second);
        Assert.Equal(2, store.Packs.Count);
        Assert.Equal(HpkBuilder.Fingerprint(key), store.TryGet(first)!.PinnedKey);
    }

    [Fact]
    public void An_unsigned_file_replaces_the_unsigned_translation_with_its_title_and_team()
    {
        var first = HpkBuilder.WithDefaultSheet();
        using (var staged = installer.Stage(WritePack(first, name: "one")))
        {
            Assert.Equal(PublisherTrustState.Unsigned, staged.Trust);
            Assert.Throws<InvalidOperationException>(() => installer.Commit(staged, trustConfirmed: false));
            installer.Commit(staged, trustConfirmed: true);
        }

        var pack = Assert.Single(store.Packs);
        Assert.Null(pack.PinnedKey);
        Assert.False(pack.IsSigned);

        var again = HpkBuilder.WithDefaultSheet();
        again.Version = "2026.10.01.0002";
        using (var staged = installer.Stage(WritePack(again, name: "two")))
        {
            Assert.Equal(pack.Id, staged.Target?.Id);
            installer.Commit(staged, trustConfirmed: true);
        }

        var renamed = HpkBuilder.WithDefaultSheet();
        renamed.Title = "Other pack";
        Install(renamed, "three");
        Assert.Equal(2, store.Packs.Count);
    }

    [Fact]
    public void A_feed_pack_updates_the_translation_of_its_feed_and_must_keep_its_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Install(Signed(key), "one", FeedUrl);
        Assert.Equal(FeedUrl, store.TryGet(id)!.FeedUrl);
        Assert.Equal(id, store.FindByFeed(FeedUrl)?.Id);

        using (var update = installer.Stage(WritePack(Signed(key, release: 2), name: "two"), feedUrl: FeedUrl))
        {
            Assert.Equal(PublisherTrustState.Trusted, update.Trust);
            Assert.Equal(id, installer.Commit(update, trustConfirmed: false));
        }

        Assert.Equal(FeedUrl, store.TryGet(id)!.FeedUrl);

        using var changed = installer.Stage(WritePack(Signed(other, release: 3), name: "three"), feedUrl: FeedUrl);
        Assert.Equal(PublisherTrustState.KeyChanged, changed.Trust);
        Assert.Equal(id, changed.Target?.Id);
        Assert.Throws<InvalidOperationException>(() => installer.Commit(changed, trustConfirmed: false));
    }

    [Fact]
    public void A_translation_installed_from_a_file_can_be_linked_to_its_feed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Install(Signed(key), "one");
        Assert.Null(store.FindByFeed(FeedUrl));

        store.LinkFeed(id, FeedUrl);

        var pack = store.TryGet(id)!;
        Assert.Equal(FeedUrl, pack.FeedUrl);
        Assert.Equal(HpkBuilder.Fingerprint(key), pack.PinnedKey);
    }

    [Fact]
    public void Older_release_is_reported_as_downgrade()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Install(Signed(key, release: 5), "one");

        using var older = installer.Stage(WritePack(Signed(key, release: 4), name: "two"));
        Assert.True(older.IsDowngrade);
        Assert.Equal("2026.10.01.0005", older.InstalledVersion);
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
        var id = Install(Signed(key), "one");
        using (var second = installer.Stage(WritePack(Signed(key, release: 2), name: "two")))
            installer.Commit(second, trustConfirmed: false);

        var packDir = Path.Combine(store.PacksDir, id);
        Assert.Equal(2, Directory.GetFiles(packDir, "*.hpk").Length);

        store.RemoveStaleFiles();
        Assert.Single(Directory.GetFiles(packDir, "*.hpk"));
        Assert.False(store.TryGet("old-pack")!.IsValid);

        using var runtime = store.OpenForRuntime(id, out var error);
        Assert.Null(error);
        Assert.Equal("2026.10.01.0002", runtime!.Manifest.Version);
    }

    [Fact]
    public void Tampered_installed_file_fails_runtime_verification()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Install(Signed(key), "one");

        var file = store.TryGet(id)!.FilePath!;
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        Assert.Null(store.OpenForRuntime(id, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Pack_for_another_client_language_is_not_selectable()
    {
        var builder = HpkBuilder.WithDefaultSheet();
        builder.GameLanguage = "ja";
        var id = Install(builder, "one");

        var pack = store.TryGet(id)!;
        Assert.True(pack.IsValid);
        Assert.False(pack.LanguageCompatible);
        Assert.False(pack.IsSelectable);
    }

    [Fact]
    public void Install_record_names_the_pack_hash_key_and_feed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Install(Signed(key), "one", FeedUrl);

        var record = JObject.Parse(File.ReadAllText(Path.Combine(store.PacksDir, id, "installed.json")));
        Assert.Equal(2, (int)record["formatVersion"]!);
        Assert.Equal(store.TryGet(id)!.PackHash, (string)record["packHash"]!);
        Assert.Equal(HpkBuilder.Fingerprint(key), (string)record["pinnedKey"]!);
        Assert.Equal(FeedUrl, (string)record["feedUrl"]!);
    }
}
