using ImmichFrame.Core.Helpers;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;

namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// Rejects a request naming a configuration profile that does not exist, before anything
/// downstream tries to resolve it.
/// <para>
/// It is no longer what stands between an unknown profile and a 500: without it the first thing to
/// ask for the profile - the authentication handler, well before any controller - would throw, and
/// <see cref="ProfileNotFoundMiddleware"/>, further out, would answer the same 404. What it adds is
/// what that cannot. The request stops here, before anything downstream has run, so everything
/// downstream can still resolve the profile without handling failure. The operator is told which
/// profile was asked for, at Warning - caught out there, a mistyped URL would be logged at Debug, as
/// a request that outlived a configuration swap. And an unknown profile stays a 404 should that
/// middleware ever be reordered or removed.
/// </para>
/// </summary>
public class UnknownProfileMiddleware(RequestDelegate _next, ILogger<UnknownProfileMiddleware> _logger)
{
    public async Task InvokeAsync(HttpContext context, IConfigCatalog catalog)
    {
        // The admin surface has no configuration profile: its endpoints resolve nothing per profile,
        // and /api/admin/session in particular has to answer whatever is on the query string, since
        // the SPA calls it to find out whether an admin surface exists at all. Answering the profile
        // 404 there would make an unconfigured installation indistinguishable from a mistyped URL.
        if (AdminAuthentication.IsAdminPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var requested = context.Request.Query[CurrentProfile.QueryParameterName].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(requested) && !catalog.TryGet(requested, out _))
        {
            // Sanitized, and only to the log: echoing an arbitrary query value straight back into
            // the response body is not worth the convenience.
            _logger.LogWarning("Request for unknown configuration profile '{profile}'", requested.SanitizeString());

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("The requested configuration profile is not configured.");
            return;
        }

        await _next(context);
    }
}
