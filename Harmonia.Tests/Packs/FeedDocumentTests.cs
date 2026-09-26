using Harmonia.Feeds;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed class FeedDocumentTests
{
    private static JObject Release(long sequence, string gameVersion, string channel = "stable", string language = "en", string minHarmonia = "1.0.0") => new()
    {
        ["sequence"] = sequence,
        ["version"] = "v" + sequence,
        ["channel"] = channel,
        ["packHash"] = "sha256:" + new string('a', 64),
        ["source"] = new JObject { ["language"] = language, ["gameVersion"] = gameVersion },
        ["target"] = new JObject { ["language"] = "ru" },
        ["contentPolicy"] = "reviewed",
        ["minHarmonia"] = minHarmonia,
        ["download"] = new JObject
        {
            ["url"] = $"https://github.com/o/r/releases/download/harmonia/{sequence}/p-{sequence}.hpk.br",
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
        ["packId"] = "ru-main",
        ["title"] = "Russian",
        ["homepage"] = "https://example.com",
        ["publisherKeyFingerprint"] = new string('c', 64),
        ["releases"] = new JArray(releases),
    }.ToString();

    [Fact]
    public void Parses_a_feed_and_ignores_unknown_fields()
    {
        var feed = FeedDocument.Parse(Feed(Release(2, "g2"), Release(1, "g1")));

        Assert.Equal("ru-main", feed.PackId);
        Assert.Equal(new string('c', 64), feed.PublisherKeyFingerprint);
        Assert.Equal(2, feed.Releases.Count);
        Assert.True(feed.Releases[0].Download.Brotli);
        Assert.Equal(20, feed.Releases[0].Download.UnpackedSize);
    }

    [Theory]
    [InlineData("""{"format":"other","version":1}""")]
    [InlineData("""{"format":"harmonia-feed","version":2,"packId":"a","releases":[]}""")]
    [InlineData("""{"format":"harmonia-feed","version":1,"packId":"Bad Id","releases":[]}""")]
    public void Rejects_foreign_or_future_documents(string json)
    {
        Assert.Throws<FormatException>(() => FeedDocument.Parse(json));
    }

    [Fact]
    public void Rejects_plain_http_downloads()
    {
        var release = Release(1, "g1");
        release["download"]!["url"] = "http://example.com/p.hpk.br";
        Assert.Throws<FormatException>(() => FeedDocument.Parse(Feed(release)));
    }

    [Fact]
    public void Selection_prefers_the_running_game_version_then_the_newest()
    {
        var feed = FeedDocument.Parse(Feed(Release(5, "new"), Release(4, "old"), Release(3, "old")));

        Assert.Equal(4, FeedReleaseSelector.SelectBest(feed, "en", "old", "1.0.0", false)!.Sequence);
        Assert.Equal(5, FeedReleaseSelector.SelectBest(feed, "en", "unknown", "1.0.0", false)!.Sequence);
        Assert.Equal(5, FeedReleaseSelector.SelectBest(feed, "en", null, "1.0.0", false)!.Sequence);
    }

    [Fact]
    public void Selection_filters_channel_language_and_plugin_version()
    {
        var feed = FeedDocument.Parse(Feed(
            Release(4, "g", channel: "testing"),
            Release(3, "g", language: "ja"),
            Release(2, "g", minHarmonia: "9.0.0"),
            Release(1, "g")));

        Assert.Equal(1, FeedReleaseSelector.SelectBest(feed, "en", "g", "1.0.0", false)!.Sequence);
        Assert.Equal(4, FeedReleaseSelector.SelectBest(feed, "en", "g", "1.0.0", true)!.Sequence);
        Assert.Equal(3, FeedReleaseSelector.SelectBest(feed, "ja", "g", "1.0.0", false)!.Sequence);
        Assert.Null(FeedReleaseSelector.SelectBest(feed, "fr", "g", "1.0.0", false));
    }
}
