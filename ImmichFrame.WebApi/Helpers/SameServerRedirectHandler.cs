using System.Net;
using ImmichFrame.Core.Helpers;

namespace ImmichFrame.WebApi.Helpers;

/// <summary>
/// Follows an Immich server's redirects for the frame's own client - but only while they stay on the
/// server the request was sent to.
/// <para>
/// Every request on that client carries the account's API key in <c>X-API-KEY</c>. The framework's
/// own redirect handling strips <c>Authorization</c> before it follows a redirect and leaves every
/// other header where it is, so with redirects left to it, anything able to answer for an Immich
/// server - the server itself, a reverse proxy in front of it, whoever holds a domain the
/// configuration still names - could hand the key to a host of its choosing with one 302. The
/// primary handler behind this one therefore follows nothing, and this handler decides instead.
/// </para>
/// <para>
/// It does not refuse them all, as the configuration editor's picker does: within one server,
/// redirects are ordinary in front of Immich - a reverse proxy upgrading http to https, an
/// installation moved to another path - and refusing those would stop frames that work today. The
/// same server is the same host, compared without regard to case, on the same port - two URLs each on
/// their own scheme's default port counting as on the same one: <c>http://immich</c> to
/// <c>https://immich</c> is followed, <c>http://immich:2283</c> to <c>https://immich</c> is not. A
/// redirect from https to http is never followed, whatever host it names, because the key would then
/// cross the network in clear text - which the framework refused as well.
/// </para>
/// <para>
/// Any other redirect is handed back as it is, and the caller fails on a status it does not expect,
/// as it would on any other. The warning logged alongside names the two servers and nothing more of
/// either URL: a redirect's target is chosen by whoever sent it, and can carry a token of its own.
/// </para>
/// </summary>
internal sealed class SameServerRedirectHandler(ILogger<SameServerRedirectHandler> _logger) : DelegatingHandler
{
    /// <summary>
    /// The most redirects one request follows. A real installation needs one or two - http to https,
    /// a path that moved - and a server sending more than this is going round in circles. The last
    /// redirect is then handed back, as one to another server is.
    /// </summary>
    internal const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Never null here: HttpClient refuses to send a request without an absolute URI.
        var origin = request.RequestUri!;

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        for (var followed = 0; followed < MaxRedirects; followed++)
        {
            var status = response.StatusCode;
            if (!IsRedirect(status) || response.Headers.Location is not { } location)
            {
                return response;
            }

            // A relative Location, the usual kind, is relative to the URL that was just requested.
            var current = request.RequestUri!;
            var target = location.IsAbsoluteUri ? location : new Uri(current, location);

            if (!IsSameServer(origin, current, target))
            {
                _logger.LogWarning(
                    "Not following a redirect from {redirectedFrom} to {redirectedTo}: an account's API key is only sent on to the server the account names, and never from https to plain http. If the Immich server has moved, change the account's server URL to where it answers now.",
                    Server(current), Server(target).SanitizeString());

                return response;
            }

            // Released before the next request goes out: it holds the connection it arrived on.
            response.Dispose();

            request.RequestUri = target;
            if (IsFollowedAsGet(status, request.Method))
            {
                request.Method = HttpMethod.Get;
                request.Content = null;
                if (request.Headers.TransferEncodingChunked == true)
                {
                    request.Headers.TransferEncodingChunked = false;
                }
            }

            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Whether <paramref name="target"/> is on the server <paramref name="origin"/> named, and reached
    /// from <paramref name="current"/> without giving up TLS. The host is compared in its ASCII form,
    /// so that a name and its punycode spelling are the same host; the port by number, or as each
    /// scheme's default.
    /// </summary>
    private static bool IsSameServer(Uri origin, Uri current, Uri target) =>
        (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps) &&
        !(current.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp) &&
        string.Equals(origin.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        (origin.Port == target.Port || (origin.IsDefaultPort && target.IsDefaultPort));

    /// <summary>
    /// Whether a redirect with <paramref name="status"/> is followed with a GET rather than with the
    /// request as it was sent - the framework's rules, which are every browser's: 303 means "fetch this
    /// instead" whatever was asked, a POST answered with 301 or 302 is repeated as a GET for historical
    /// reasons nobody can now undo, and 307 and 308 exist to repeat the request exactly, body and all.
    /// </summary>
    private static bool IsFollowedAsGet(HttpStatusCode status, HttpMethod method) => status switch
    {
        HttpStatusCode.SeeOther => method != HttpMethod.Get && method != HttpMethod.Head,
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found => method == HttpMethod.Post,
        _ => false
    };

    /// <summary>
    /// The scheme, host and port of <paramref name="uri"/> and nothing else - not its user information,
    /// path or query, any of which can carry a secret.
    /// </summary>
    private static string Server(Uri uri) => $"{uri.Scheme}://{uri.Authority}";
}
