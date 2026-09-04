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
/// This covers the unconfigured case only. The configured case - an admin endpoint that forgot its
/// authorization - is refused at startup by <see cref="AdminEndpointGuard"/> instead, so that the
/// mistake is loud rather than a 404 someone spends an afternoon debugging.
/// </para>
/// </summary>
public class AdminSurfaceMiddleware(RequestDelegate _next)
{
    public async Task InvokeAsync(HttpContext context, AdminOidcOptions options)
    {
        if (!options.IsEnabled && context.Request.Path.StartsWithSegments(
                AdminAuthentication.ApiPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Routing runs ahead of the user pipeline, so the endpoint - and its metadata - is
            // already known here. A request under the prefix that routed nowhere carries no
            // attribute and is hidden along with the rest.
            var visible = context.GetEndpoint()?.Metadata.GetMetadata<AdminEndpointAttribute>()?.VisibleWhenUnconfigured is true;

            if (!visible)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        await _next(context);
    }
}
