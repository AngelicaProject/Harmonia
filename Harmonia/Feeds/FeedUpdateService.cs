using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Harmonia.Localization;
using Harmonia.Packs;

namespace Harmonia.Feeds;

// Polls pack feeds and installs new releases through the pack installer. No
// push channel exists for a game plugin, so periodic polling, a manual check
// and a check after a game version change are the whole strategy.
public sealed class FeedUpdateService : IDisposable
{
    private const int InitialDelaySeconds = 30;
    private const int MinIntervalMinutes = 5;
    private const int MaxIntervalMinutes = 1440;

    private readonly TranslationPackStore packs;
    private readonly PackInstaller installer;
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private readonly FeedUpdateState state;
    private readonly SessionState session;
    private readonly IHarmoniaLog log;
    private readonly Action<string, string> notify;
    private readonly string? loadedPackId;
    private readonly HttpClient client;
    private readonly Dictionary<string, (string ETag, FeedDocument Feed)> feedCache = new(StringComparer.Ordinal);

    private int checkInProgress;
    private CancellationTokenSource? loopCts;
    private bool disposed;

    public FeedUpdateService(
        TranslationPackStore packs,
        PackInstaller installer,
        Configuration configuration,
        Action saveConfiguration,
        FeedUpdateState state,
        SessionState session,
        IHarmoniaLog log,
        Action<string, string> notify,
        string pluginVersion,
        string? loadedPackId,
        HttpMessageHandler? httpHandler = null)
    {
        this.packs = packs;
        this.installer = installer;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.state = state;
        this.session = session;
        this.log = log;
        this.notify = notify;
        this.loadedPackId = loadedPackId;

        // Downloads are bounded by the feed-declared size instead of a
        // wall-clock timeout, so slow connections still finish.
        client = new HttpClient(httpHandler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Harmonia/" + pluginVersion);
    }

    public void Start()
    {
        if (loopCts is not null)
            return;

        loopCts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(loopCts.Token));
    }

    public static bool IsFeedUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    public bool AddFeed(string url, out string errorKey)
    {
        errorKey = string.Empty;
        var clean = url?.Trim() ?? string.Empty;

        if (!IsFeedUrl(clean))
        {
            errorKey = "updates.invalid_url";
            return false;
        }

        if (configuration.UpdateFeedUrls.Any(u => string.Equals(u, clean, StringComparison.OrdinalIgnoreCase)))
        {
            errorKey = "updates.already_added";
            return false;
        }

        configuration.UpdateFeedUrls.Add(clean);
        saveConfiguration();
        _ = CheckUrlAsync(clean);
        return true;
    }

    // Reads a feed without adding it, so a pasted link can be checked before
    // it is saved. The timeout surfaces as TaskCanceledException.
    public Task<FeedDocument> ProbeAsync(string url, CancellationToken cancellationToken = default) =>
        FetchFeedAsync(url.Trim(), cancellationToken);

    public void RemoveFeed(string url)
    {
        configuration.UpdateFeedUrls.RemoveAll(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
        state.Feeds = state.Feeds.Where(f => !string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)).ToArray();
        lock (feedCache)
            feedCache.Remove(url);
        saveConfiguration();
    }

    public Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref checkInProgress, 1) != 0)
            return Task.CompletedTask;

        return CheckAsync(cancellationToken);
    }

    // A pack built for the previous game version still works for unchanged
    // strings, but a release for the new version is likely waiting.
    public void OnGameVersionKnown(string gameVersion)
    {
        if (string.Equals(configuration.LastSeenGameVersion, gameVersion, StringComparison.Ordinal))
            return;

        var first = string.IsNullOrEmpty(configuration.LastSeenGameVersion);
        configuration.LastSeenGameVersion = gameVersion;
        saveConfiguration();
        if (!first)
            _ = CheckNowAsync();
    }

    // Single-feed check after pasting a URL: never downloads, the user
    // installs explicitly with the Update button.
    public Task CheckUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref checkInProgress, 1) != 0)
            return Task.CompletedTask;

        return CheckSingleAsync(url, cancellationToken);
    }

    // confirmedFingerprint is the key the user just accepted for a feed with
    // nothing installed yet; it applies only if the pack is signed by it.
    public async Task<bool> InstallUpdateAsync(string url, string? confirmedFingerprint = null, CancellationToken cancellationToken = default)
    {
        var status = new FeedStatus { Url = url, Status = FeedPackStatus.Checking };
        Publish(status);

        try
        {
            var feed = await FetchFeedAsync(url, cancellationToken).ConfigureAwait(false);
            var release = Evaluate(feed, status);
            if (release is null || status.Status is not (FeedPackStatus.UpdateAvailable or FeedPackStatus.NeedsTrust))
            {
                Publish(status);
                return false;
            }

            return await DownloadAndInstallAsync(release, status, confirmedFingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            status.Status = FeedPackStatus.UpdateAvailable;
            Publish(status);
            throw;
        }
        catch (Exception ex)
        {
            Fail(status, FeedErrors.Describe(ex), ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        try
        {
            loopCts?.Cancel();
        }
        catch
        {
        }

        loopCts?.Dispose();
        client.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(InitialDelaySeconds), cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckNowAsync(cancellationToken).ConfigureAwait(false);

                var minutes = Math.Clamp(configuration.UpdateCheckIntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(minutes), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Error("Update loop failed", ex);
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        state.Checking = true;

        try
        {
            var results = new List<FeedStatus>();
            foreach (var url in configuration.UpdateFeedUrls.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await CheckFeedAsync(url, allowDownload: true, cancellationToken).ConfigureAwait(false));
            }

            state.Feeds = results;
            state.LastCheck = DateTime.Now;
            configuration.LastUpdateCheck = DateTime.Now;
            saveConfiguration();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Error("Update check failed", ex);
        }
        finally
        {
            state.Checking = false;
            Interlocked.Exchange(ref checkInProgress, 0);
        }
    }

    private async Task CheckSingleAsync(string url, CancellationToken cancellationToken)
    {
        state.Checking = true;

        try
        {
            await CheckFeedAsync(url, allowDownload: false, cancellationToken).ConfigureAwait(false);
            state.LastCheck = DateTime.Now;
            configuration.LastUpdateCheck = DateTime.Now;
            saveConfiguration();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Error("Update check failed", ex);
        }
        finally
        {
            state.Checking = false;
            Interlocked.Exchange(ref checkInProgress, 0);
        }
    }

    private async Task<FeedStatus> CheckFeedAsync(string url, bool allowDownload, CancellationToken cancellationToken)
    {
        var status = new FeedStatus { Url = url, Status = FeedPackStatus.Checking };
        Publish(status);

        try
        {
            var feed = await FetchFeedAsync(url, cancellationToken).ConfigureAwait(false);
            var release = Evaluate(feed, status);
            Publish(status);
            if (release is null || status.Status != FeedPackStatus.UpdateAvailable)
                return status;

            if (allowDownload && configuration.AutoDownloadUpdates)
                await DownloadAndInstallAsync(release, status, null, cancellationToken).ConfigureAwait(false);
            else
                NotifyOnce(status, release);

            return status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(status, FeedErrors.Describe(ex), ex);
            return status;
        }
    }

    private FeedRelease? Evaluate(FeedDocument feed, FeedStatus status)
    {
        var installed = packs.FindByFeed(status.Url);
        status.PackId = installed?.Id;
        status.Title = feed.Title;
        status.InstalledVersion = installed?.Manifest?.Version;

        var release = FeedReleaseSelector.SelectNewest(feed, configuration.FollowTestingChannel);
        if (release is null || !PackCompatibility.IsLanguageCompatible(release.GameLanguage, packs.ClientLanguage))
        {
            status.Status = FeedPackStatus.Incompatible;
            return null;
        }

        status.RemoteVersion = release.Version;
        status.Changelog = release.Changelog;

        if (!FeedReleaseSelector.IsUpgrade(release, installed))
        {
            status.Status = FeedPackStatus.UpToDate;
            return release;
        }

        if (!PackCompatibility.IsPluginSupported(release.MinHarmonia, packs.PluginVersion))
        {
            status.Status = FeedPackStatus.PluginTooOld;
            return null;
        }

        status.TrustFingerprint = installed?.PinnedKey is null ? feed.PublisherKeyFingerprint : null;
        status.Status = FeedPackStatus.UpdateAvailable;
        return release;
    }

    private async Task<FeedDocument> FetchFeedAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        (string ETag, FeedDocument Feed) cached;
        bool hasCached;
        lock (feedCache)
            hasCached = feedCache.TryGetValue(url, out cached);
        if (hasCached)
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (hasCached && response.StatusCode == HttpStatusCode.NotModified)
            return cached.Feed;

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > FeedDocument.MaxFeedBytes)
            throw new InvalidDataException("Feed is too large.");

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(body, buffer, FeedDocument.MaxFeedBytes, null, null, timeout.Token).ConfigureAwait(false);
        var feed = FeedDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));

        var etag = response.Headers.ETag?.ToString();
        lock (feedCache)
        {
            if (etag is not null)
                feedCache[url] = (etag, feed);
            else
                feedCache.Remove(url);
        }

        return feed;
    }

    private async Task<bool> DownloadAndInstallAsync(
        FeedRelease release,
        FeedStatus status,
        string? confirmedFingerprint,
        CancellationToken cancellationToken)
    {
        status.Status = FeedPackStatus.Downloading;
        status.Progress = 0;
        Publish(status);

        var tempFile = Path.Combine(Path.GetTempPath(), "harmonia-" + Guid.NewGuid().ToString("N") + ".download");

        try
        {
            var download = release.Download;
            using (var response = await client.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(tempFile);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await CopyBoundedAsync(body, file, download.Size, sha,
                    bytes => status.Progress = download.Size == 0 ? 0 : (float)bytes / download.Size,
                    cancellationToken).ConfigureAwait(false);

                if (file.Length != download.Size || Convert.ToHexStringLower(sha.GetHashAndReset()) != download.Sha256)
                    throw new InvalidDataException("Downloaded file does not match the feed.");
            }

            using var staged = installer.Stage(tempFile, download.Brotli ? download.UnpackedSize : download.Size, status.Url);
            var manifest = staged.Manifest;
            if (staged.PackHash != release.PackHash || manifest.Version != release.Version ||
                manifest.GameVersion != release.GameVersion ||
                !string.Equals(manifest.GameLanguage, release.GameLanguage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded pack does not match the feed entry.");

            var confirmed = staged.Trust == PublisherTrustState.FirstUse &&
                confirmedFingerprint is not null &&
                string.Equals(staged.Fingerprint, confirmedFingerprint, StringComparison.Ordinal);

            if (!PublisherTrust.InstallsWithoutConfirmation(staged.Trust) && !confirmed)
            {
                if (staged.Trust == PublisherTrustState.FirstUse)
                {
                    status.Status = FeedPackStatus.NeedsTrust;
                    status.TrustFingerprint = staged.Fingerprint;
                    Publish(status);
                    return false;
                }

                Fail(status, staged.Trust == PublisherTrustState.Unsigned
                    ? Lang.T("updates.error_unsigned")
                    : Lang.T("updates.error_key_changed"));
                return false;
            }

            var packId = installer.Commit(staged, trustConfirmed: confirmed);

            if (string.Equals(packId, loadedPackId, StringComparison.Ordinal))
                session.IsRestartRequired = true;

            configuration.NotifiedFeedVersions[status.Url] = release.Version;
            saveConfiguration();

            status.PackId = packId;
            status.Status = FeedPackStatus.UpToDate;
            status.InstalledVersion = manifest.Version;
            status.TrustFingerprint = null;
            Publish(status);

            notify("Harmonia", string.Format(Lang.T("updates.notify_installed"), manifest.Title, manifest.Version));
            return true;
        }
        catch (OperationCanceledException)
        {
            status.Status = FeedPackStatus.UpdateAvailable;
            Publish(status);
            throw;
        }
        catch (Exception ex)
        {
            Fail(status, FeedErrors.Describe(ex), ex);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
            }
        }
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long limit,
        IncrementalHash? hash,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
                throw new InvalidDataException("Download is larger than declared.");

            hash?.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Invoke(total);
        }
    }

    private void NotifyOnce(FeedStatus status, FeedRelease release)
    {
        if (configuration.NotifiedFeedVersions.TryGetValue(status.Url, out var notified) &&
            string.Equals(notified, release.Version, StringComparison.Ordinal))
            return;

        configuration.NotifiedFeedVersions[status.Url] = release.Version;
        saveConfiguration();

        notify("Harmonia", string.Format(
            Lang.T("updates.notify_available"), status.Title ?? status.Url, release.Version));
    }

    private void Publish(FeedStatus status)
    {
        var list = state.Feeds.ToList();
        var index = list.FindIndex(f => string.Equals(f.Url, status.Url, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            list[index] = status;
        else
            list.Add(status);

        state.Feeds = list;
    }

    private void Fail(FeedStatus status, string error, Exception? exception = null)
    {
        status.Status = FeedPackStatus.Error;
        status.Error = error;
        Publish(status);
        log.Warning("Feed check failed for " + status.Url + ": " + (exception?.Message ?? error));
    }
}
