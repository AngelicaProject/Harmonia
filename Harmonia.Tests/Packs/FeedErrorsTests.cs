using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using Harmonia.Feeds;
using Harmonia.Localization;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed class FeedErrorsTests
{
    [Fact]
    public void Tls_failure_is_not_reported_as_an_unreachable_server()
    {
        var tls = new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established.",
            new AuthenticationException());

        Assert.Equal(Lang.T("link.tls"), FeedErrors.Describe(tls));
        Assert.Equal(Lang.T("link.unreachable"), FeedErrors.Describe(new HttpRequestException(HttpRequestError.NameResolutionError)));
        Assert.Equal(Lang.T("link.not_found"), FeedErrors.Describe(new HttpRequestException(null, null, HttpStatusCode.NotFound)));
    }
}
