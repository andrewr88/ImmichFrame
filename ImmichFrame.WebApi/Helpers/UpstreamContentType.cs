namespace ImmichFrame.WebApi.Helpers;

/// <summary>
/// What a body fetched from an Immich server is labelled as when ImmichFrame serves it on: the
/// upstream's own media type when it is one the caller expects, and the caller's fallback otherwise.
/// <para>
/// An allowlist rather than a sanitiser, because the failure mode is not a malformed header but a
/// plausible one. <c>text/html</c> and <c>image/svg+xml</c> are perfectly valid Content-Types, and
/// either one turns a response served from ImmichFrame's own origin - the origin the frame and the
/// admin editor are served from - into script running on it. The bytes still come from Immich;
/// there is no way to show a photo otherwise. The label is ImmichFrame's.
/// </para>
/// <para>
/// One comparison for every caller, so that none of them can drift into comparing differently:
/// the person thumbnail and the frame's own assets are the same risk.
/// </para>
/// </summary>
public static class UpstreamContentType
{
    /// <summary>
    /// The entry of <paramref name="allowed"/> that <paramref name="declared"/> names, or
    /// <paramref name="fallback"/> when it names none of them - an absent Content-Type included.
    /// </summary>
    public static string Allowlisted(string? declared, IEnumerable<string> allowed, string fallback)
    {
        // Compared on the media type alone, and without regard to case: "IMAGE/JPEG; charset=utf-8"
        // is still a JPEG, and it is the part before the parameters that decides how a browser
        // treats the body. What is served is the allowlist's own spelling, never the upstream's.
        var mediaType = declared?.Split(';')[0].Trim();

        return allowed.FirstOrDefault(
            type => string.Equals(type, mediaType, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }
}
