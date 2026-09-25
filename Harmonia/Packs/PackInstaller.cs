using System.IO.Compression;
using Harmonia.Packs.Hpk;

namespace Harmonia.Packs;

// A downloaded or imported pack that passed full verification and waits in
// the staging folder for a trust decision. Disposing discards it.
public sealed class StagedPack : IDisposable
{
    internal StagedPack(string stagingPath, HpkFile file, PublisherTrustState trust, TranslationPack? installed)
    {
        StagingPath = stagingPath;
        Manifest = file.Manifest;
        PackHash = file.PackHashText;
        Fingerprint = file.Signature?.Fingerprint;
        Trust = trust;
        InstalledSequence = installed?.Manifest?.Sequence;
    }

    public string StagingPath { get; }
    public HpkManifest Manifest { get; }
    public string PackHash { get; }
    public string? Fingerprint { get; }
    public PublisherTrustState Trust { get; }
    public long? InstalledSequence { get; }
    public bool IsDowngrade => InstalledSequence is { } installed && Manifest.Sequence < installed;
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
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private readonly IHarmoniaLog log;

    public PackInstaller(TranslationPackStore store, Configuration configuration, Action saveConfiguration, IHarmoniaLog log)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public StagedPack Stage(string sourcePath, long maxUnpackedBytes = HpkFormat.MaxPackBytes)
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
            configuration.PinnedPublisherKeys.TryGetValue(file.Manifest.PackId, out var pinned);
            var trust = PublisherTrust.Evaluate(pinned, file.Signature);
            return new StagedPack(stagingPath, file, trust, store.TryGet(file.Manifest.PackId));
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }
    }

    // trustConfirmed records the user's explicit decision for FirstUse,
    // KeyChanged or Unsigned packs; Trusted and Rotated packs never need it.
    public void Commit(StagedPack staged, bool trustConfirmed)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Committed)
            throw new InvalidOperationException("Pack is already installed.");
        if (!PublisherTrust.InstallsWithoutConfirmation(staged.Trust) && !trustConfirmed)
            throw new InvalidOperationException("Publisher trust was not confirmed.");

        var packId = staged.Manifest.PackId;
        var dir = store.PackDirectory(packId);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, TranslationPackStore.FileNameFor(staged.PackHash));

        // Identical content is already installed under its hash name.
        if (File.Exists(target))
            File.Delete(staged.StagingPath);
        else
            File.Move(staged.StagingPath, target);

        staged.Committed = true;
        store.WriteInstallRecord(packId, staged.PackHash);

        if (staged.Fingerprint is not null)
            configuration.PinnedPublisherKeys[packId] = staged.Fingerprint;
        saveConfiguration();

        store.Rescan();
        log.Info($"Installed translation pack '{packId}' release {staged.Manifest.Sequence} ({staged.Trust}).");
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
