using System.IO.Compression;
using Harmonia.Packs.Hpk;

namespace Harmonia.Packs;

// A downloaded or imported pack that passed full verification and waits in
// the staging folder for a trust decision. Disposing discards it.
public sealed class StagedPack : IDisposable
{
    internal StagedPack(string stagingPath, HpkFile file, PublisherTrustState trust, TranslationPack? target, string? feedUrl)
    {
        StagingPath = stagingPath;
        Manifest = file.Manifest;
        PackHash = file.PackHashText;
        Fingerprint = file.Signature?.Fingerprint;
        Trust = trust;
        Target = target;
        FeedUrl = feedUrl;
    }

    public string StagingPath { get; }
    public HpkManifest Manifest { get; }
    public string PackHash { get; }
    public string? Fingerprint { get; }
    public PublisherTrustState Trust { get; }

    // The installed translation this pack updates; null for a new one.
    public TranslationPack? Target { get; }

    // The feed the pack was downloaded from; null for a file.
    public string? FeedUrl { get; }

    public string? InstalledVersion => Target?.Manifest?.Version;

    public bool IsDowngrade => InstalledVersion is { } installed && PackVersion.Compare(Manifest.Version, installed) < 0;

    internal bool Committed { get; set; }

    public void Dispose()
    {
        if (Committed)
            return;

        try
        {
            File.Delete(StagingPath);
        }
        catch
        {
        }
    }
}

// Installs .hpk and .hpk.br files. Feed updates and manual imports share this
// path so both get the same verification and trust rules.
public sealed class PackInstaller
{
    private readonly TranslationPackStore store;
    private readonly IHarmoniaLog log;

    public PackInstaller(TranslationPackStore store, IHarmoniaLog log)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // feedUrl names the feed the file came from; null for a file the player
    // chose. A pack from a feed updates the translation of that feed; a file
    // updates the translation that trusts its key, or, unsigned, the
    // unsigned translation with its title and team.
    public StagedPack Stage(string sourcePath, long maxUnpackedBytes = HpkFormat.MaxPackBytes, string? feedUrl = null)
    {
        maxUnpackedBytes = Math.Min(maxUnpackedBytes, HpkFormat.MaxPackBytes);
        Directory.CreateDirectory(store.StagingDir);
        var stagingPath = Path.Combine(store.StagingDir, Guid.NewGuid().ToString("N") + TranslationPackStore.PackExtension);

        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Span<byte> magic = stackalloc byte[8];
                var read = source.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
                source.Position = 0;
                if (read == magic.Length && magic.SequenceEqual(HpkFormat.Magic))
                {
                    CopyBounded(source, target, maxUnpackedBytes);
                }
                else
                {
                    using var brotli = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
                    CopyBounded(brotli, target, maxUnpackedBytes);
                }
            }

            using var file = HpkFile.Open(stagingPath, HpkOpenMode.Full);
            var installed = feedUrl is not null ? store.FindByFeed(feedUrl)
                : file.Signature is { } signature ? store.FindByKey(signature)
                : store.FindUnsigned(file.Manifest);
            var trust = PublisherTrust.Evaluate(installed?.PinnedKey, file.Signature);
            return new StagedPack(stagingPath, file, trust, installed, feedUrl);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }
    }

    // trustConfirmed records the user's explicit decision for FirstUse,
    // KeyChanged or Unsigned packs; Trusted and Rotated packs never need it.
    // Returns the id of the installed translation.
    public string Commit(StagedPack staged, bool trustConfirmed)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Committed)
            throw new InvalidOperationException("Pack is already installed.");
        if (!PublisherTrust.InstallsWithoutConfirmation(staged.Trust) && !trustConfirmed)
            throw new InvalidOperationException("Publisher trust was not confirmed.");

        var packId = staged.Target?.Id ?? store.NewPackId();
        var dir = store.PackDirectory(packId);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, TranslationPackStore.FileNameFor(staged.PackHash));

        // Identical content is already installed under its hash name.
        if (File.Exists(target))
            File.Delete(staged.StagingPath);
        else
            File.Move(staged.StagingPath, target);

        staged.Committed = true;

        // The key of the new file becomes the trusted one, which also moves
        // the pin along an endorsed key rotation.
        store.WriteInstallRecord(packId, staged.PackHash, staged.Fingerprint, staged.FeedUrl ?? staged.Target?.FeedUrl);
        store.Rescan();
        log.Info($"Installed translation '{packId}' version {staged.Manifest.Version} ({staged.Trust}).");
        return packId;
    }

    private static void CopyBounded(Stream source, Stream target, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new HpkFormatException("Pack is larger than allowed.");

            target.Write(buffer, 0, read);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
