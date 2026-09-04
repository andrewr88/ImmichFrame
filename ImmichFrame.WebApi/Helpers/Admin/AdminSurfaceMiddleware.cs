namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// Hides the entire <c>/api/admin</c> prefix while the admin surface is not configured.
/// <para>
/// The surface is off unless an authority, a client id, a client secret <em>and</em> a non-empty
/// allowlist are all present, so a half-configured installation exposes nothing rather than an
/// endpoint whose protection depends on the piece that is missing. 404 rather than 401 or 500 keeps
/// an unconfigured installation from advertising an admin API it cannot let anyone into.
/// </para>
/// <para>
/// The rule is the whole prefix minus an explicit
/// <see cref="AdminEndpointAttribute.VisibleWhenUnconfigured"/>, not a per-endpoint opt-in. Since
/// <c>CustomAuthenticationMiddleware</c> also steps aside for this prefix, an endpoint added under
/// it that forgets its attributes would otherwise be reachable with no authentication at all on an
/// installation that has an <c>AuthenticationSecret</c> set. Forgetting has to fail closed.
/// </para>
/// <para>
/// It also 404s any path under the prefix that did not reach an admin endpoint at all, configured
/// or not. The SPA fallback's <c>{*path:nonfile}</c> pattern matches <c>/api/admin/typo</c>
/// happily, and answering an unrouted API path with the editor's HTML discloses nothing but
/// contradicts everything else this surface is careful to say about what does and does not exist
/// here.
/// </para>
/// <para>
/// Between them these cover the unconfigured case. The configured case - an admin endpoint that
/// forgot its authorization - is refused at startup by <see cref="AdminEndpointGuard"/> instead, so
/// that the mistake is loud rather than a 404 someone spends an afternoon debugging.
/// </para>
/// </summary>
public class AdminSurfaceMiddleware(RequestDelegate _next)
{
    public async Task InvokeAsync(HttpContext context, AdminOidcOptions options)
    {
        if (context.Request.Path.StartsWithSegments(
                AdminAuthentication.ApiPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Routing runs ahead of the user pipeline, so the matched endpoint - and its metadata -
            // is already known here.
            var endpoint = context.GetEndpoint();

            // Matching the SPA fallback, or nothing at all, is not reaching an admin endpoint.
            if (!AdminAuthentication.IsAdminEndpoint(endpoint))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var visible = endpoint!.Metadata.GetMetadata<AdminEndpointAttribute>()?.VisibleWhenUnconfigured is true;

            if (!options.IsEnabled && !visible)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        await _next(context);
    }
}
