using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing.Patterns;

namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// Refuses to start the application if any endpoint under <c>/api/admin</c> has neither
/// authorization metadata nor <see cref="AdminEndpointAttribute.Anonymous"/>.
/// <para>
/// <see cref="CustomAuthenticationMiddleware"/> steps aside for this whole prefix, so an endpoint
/// here that forgets <c>[Authorize]</c> has no authentication of its own - not even the frame's
/// shared secret - and serves whoever asks. That is the highest-consequence mistake available in
/// this codebase, since what lives under this prefix reads and rewrites the configuration file.
/// </para>
/// <para>
/// Checked once at startup rather than per request, so the mistake is a crash on boot with the
/// offending route named, not an endpoint that quietly answers until somebody notices. It is a
/// tripwire, not a proof: an endpoint carrying <c>[Authorize]</c> at class level and
/// <c>[AllowAnonymous]</c> on the action satisfies it while still being anonymous. It catches the
/// thing that actually happens, which is forgetting the attribute altogether.
/// </para>
/// </summary>
public static class AdminEndpointGuard
{
    public static void Validate(IEnumerable<Endpoint> endpoints)
    {
        var unguarded = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsAdminRoute(endpoint.RoutePattern))
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is null &&
                endpoint.Metadata.GetMetadata<AdminEndpointAttribute>()?.Anonymous is not true)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "(unnamed)")
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToList();

        if (unguarded.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Admin endpoint(s) with no authorization: {string.Join(", ", unguarded)}. " +
            $"Everything under {AdminAuthentication.ApiPathPrefix} is exempt from the frame's " +
            "shared-secret scheme, so an endpoint without authorization is reachable by anyone. " +
            $"Add [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)], or " +
            "[AdminEndpoint(Anonymous = true)] if it is meant to be reachable without a session.");
    }

    // Through the same PathString segment matching the middlewares use, so a route named
    // 'api/adminfoo' is not mistaken for part of the admin surface. Route parameters in the raw
    // text ('api/admin/config/{name}') do not affect the leading segments being compared.
    private static bool IsAdminRoute(RoutePattern pattern) =>
        !string.IsNullOrEmpty(pattern.RawText) &&
        AdminAuthentication.IsAdminPath("/" + pattern.RawText.TrimStart('/'));
}
