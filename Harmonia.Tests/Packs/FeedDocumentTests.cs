using Harmonia.Feeds;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed class FeedDocumentTests
{
    private static JObject Release(string version, string channel = "stable", string language = "en", string minHarmonia = "1.0.0") => new()
    {
        ["version"] = version,
        ["channel"] = channel,
        ["language"] = "ru",
        ["game"] = new JObject { ["language"] = language, ["version"] = "2026.08.12.0000.0000" },
        ["minHarmonia"] = minHarmonia,
        ["packHash"] = "sha256:" + new string('a', 64),
        ["download"] = new JObject
        {
            ["url"] = $"https://github.com/o/r/releases/download/harmonia/{version}/pack-{version}.hpk.br",
            ["encoding"] = "br",
            ["size"] = 10,
            ["sha256"] = new string('b', 64),
            ["unpackedSize"] = 20,
        },
        ["changelog"] = null,
        ["futureField"] = "ignored",
    };

    private static string Feed(params JObject[] releases) => new JObject
    {
        ["format"] = "harmonia-feed",
        ["version"] = 1,
        ["title"] = "Russian",
        ["publisherKeyFingerprint"] = new string('c', 64),
        ["releases"] = new JArray(releases),
    }.ToString();

    [Fact]
    public void Parses_a_feed_and_ignores_unknown_fields()
    {
        var feed = FeedDocument.Parse(Feed(Release("2026.10.02.0001"), Release("2026.10.01.0001", "testing")));

        Assert.Equal("Russian", feed.Title);
        Assert.Equal(new string('c', 64), feed.PublisherKeyFingerprint);
        Assert.Equal(2, feed.Releases.Count);
        var release = feed.Releases[0];
        Assert.Equal("2026.10.02.0001", release.Version);
        Assert.Equal(("en", "2026.08.12.0000.0000", "ru"), (release.GameLanguage, release.GameVersion, release.Language));
        Assert.True(release.Download.Brotli);
        Assert.Equal(20, release.Download.UnpackedSize);
    }

    [Theory]
    [InlineData("""{"format":"other","version":1}""")]
    [InlineData("""{"format":"harmonia-feed","version":2,"releases":[]}""")]
    [InlineData("""{"format":"harmonia-feed","version":1,"publisherKeyFingerprint":"BAD","releases":[]}""")]
    public void Rejects_foreign_or_future_documents(string json)
    {
        Assert.Throws<FormatException>(() => FeedDocument.Parse(json));
    }

    [Fact]
    public void Rejects_plain_http_downloads_and_bad_versions()
    {
        var http = Release("2026.10.01.0001");
        http["download"]!["url"] = "http://example.com/p.hpk.br";
        Assert.Throws<FormatException>(() => FeedDocument.Parse(Feed(http)));
        Assert.Throws<FormatException>(() => FeedDocument.Parse(Feed(Release("42"))));
    }

    [Fact]
    public void Selection_takes_the_newest_release_of_the_followed_channels()
    {
        var feed = FeedDocument.Parse(Feed(
            Release("2026.10.01.0002"),
            Release("2026.10.03.0001", channel: "testing"),
            Release("2026.09.30.0007")));

        Assert.Equal("2026.10.01.0002", FeedReleaseSelector.SelectNewest(feed, followTesting: false)!.Version);
        Assert.Equal("2026.10.03.0001", FeedReleaseSelector.SelectNewest(feed, followTesting: true)!.Version);
        Assert.Null(FeedReleaseSelector.SelectNewest(FeedDocument.Parse(Feed()), followTesting: true));
    }

    [Fact]
    public void Only_a_higher_version_is_an_upgrade()
    {
        var release = FeedDocument.Parse(Feed(Release("2026.10.01.0002"))).Releases[0];
        Assert.True(FeedReleaseSelector.IsUpgrade(release, installed: null));
    }
}
