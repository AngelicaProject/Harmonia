using System.Net;
using System.Net.Http;
using Harmonia.Localization;

namespace Harmonia.Feeds;

// Player-facing text for a failed feed request. The raw exception message is
// often a localized socket error that says nothing to a player; it goes to
// the log instead.
public static class FeedErrors
{
    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } => Lang.T("link.not_found"),
        HttpRequestException { StatusCode: not null } http => Lang.T("link.server_error", (int)http.StatusCode.Value),
        // The TLS handshake failed on this machine (system clock, TLS provider);
        // the server may well be reachable.
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => Lang.T("link.tls"),
        HttpRequestException => Lang.T("link.unreachable"),
        TimeoutException or TaskCanceledException => Lang.T("link.timeout"),
        FormatException => Lang.T("link.not_feed"),
        InvalidDataException => Lang.T("link.bad_download"),
        _ => ex.Message,
    };
}
