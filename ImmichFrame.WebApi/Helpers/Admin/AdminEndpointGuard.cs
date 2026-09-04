using Microsoft.AspNetCore.Authorization;

namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// Refuses to start the application if any endpoint on an admin-surface path is not guarded by the
/// admin surface's own authorization.
/// <para>
/// The scope is every path <see cref="AdminAuthentication.IsAdminPath"/> claims, not just the API:
/// the <c>/api/admin</c> prefix, the <c>/admin</c> SPA route, and the three OpenID Connect callback
/// paths. <see cref="CustomAuthenticationMiddleware"/> steps aside for them: unconditionally for the
/// API prefix and the SPA route, and for the three callback paths whenever OpenID Connect is
/// configured. So an endpoint mapped on any of them that forgets <c>[Authorize]</c> has no
/// authentication of its own, not even the frame's shared secret, and serves whoever asks. That is
/// the highest-consequence mistake available in this codebase, since what lives under the API
/// prefix reads and rewrites the configuration file.
/// </para>
/// <para>
/// A deliberately public endpoint on one of those paths - a static-file fallback serving the
/// editor's shell, say - declares itself with <c>[AdminEndpoint(Anonymous = true)]</c> on a
/// controller action, or <c>.WithMetadata(new AdminEndpointAttribute { Anonymous = true })</c> on an
/// endpoint mapped directly. The current SPA fallback is not caught: its route pattern is
/// <c>{*path:nonfile}</c>, which is not an admin path.
/// </para>
/// <para>
/// A bare <c>[Authorize]</c> is treated as unguarded, and this is the subtle half of the rule.
/// Bare authorization resolves to the default policy against the <em>default scheme</em>, which is
/// still <c>ImmichFrameScheme</c> - and <c>ImmichFrameAuthenticationHandler</c> succeeds with an
/// authenticated identity for any holder of the frame's <c>AuthenticationSecret</c>, or for
/// everybody when no secret is configured. Either way a bare <c>[Authorize]</c> on an admin
/// endpoint admits kiosk displays, or the entire network, to the configuration editor.
/// </para>
/// <para>
/// Naming the admin cookie scheme is not sufficient either, and this is the half that looks safe.
/// <c>[Authorize(AuthenticationSchemes = CookieScheme)]</c> authenticates the caller without ever
/// consulting the allowlist, so it admits every identity the provider will authenticate - on a
/// provider with open registration, a stranger. What counts is the <c>AdminOnly</c> policy. The
/// cookie scheme is accepted only alongside an explicit
/// <see cref="AdminEndpointAttribute.AllowlistNotRequired"/> waiver, and exactly one endpoint
/// declares that waiver: <c>AdminSessionController.Logout</c>, where ending your own session
/// genuinely does not require being an administrator. Read it before writing a second.
/// </para>
/// <para>
/// Checked once at startup rather than per request, so the mistake is a crash on boot with the
/// offending route named, not an endpoint that quietly answers until somebody notices. It is a
/// tripwire, not a proof: an endpoint carrying admin authorization at class level and
/// <c>[AllowAnonymous]</c> on the action satisfies it while still being anonymous. It catches the
/// things that actually happen - forgetting the attribute, reaching for the bare one, or copying
/// sign-out's scheme-only authorization onto an endpoint that reads or writes something.
/// </para>
/// </summary>
public static class AdminEndpointGuard
{
    public static void Validate(IEnumerable<Endpoint> endpoints)
    {
        var unguarded = endpoints
            .OfType<RouteEndpoint>()
            .Where(AdminAuthentication.IsAdminEndpoint)
            .Where(endpoint =>
                !IsGuardedByAdminAuthorization(endpoint) &&
                endpoint.Metadata.GetMetadata<AdminEndpointAttribute>()?.Anonymous is not true)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "(unnamed)")
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToList();

        if (unguarded.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Admin endpoint(s) not guarded by admin authorization: {string.Join(", ", unguarded)}. " +
            "Admin-surface paths are exempt from the frame's shared-secret scheme, so an endpoint " +
            "mapped on one answers to anyone unless it carries admin authorization. Note that a " +
            "bare [Authorize] does not count - it falls back to the frame's own scheme, which " +
            "admits any holder of AuthenticationSecret, or everyone when none is set - and neither " +
            "does naming the admin cookie scheme alone, which authenticates without checking the " +
            "allowlist. " +
            "Add [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]. If the endpoint is " +
            "deliberately public, declare it: [AdminEndpoint(Anonymous = true)] on a controller " +
            "action, or .WithMetadata(new AdminEndpointAttribute { Anonymous = true }) on an " +
            "endpoint mapped directly, such as a static-file fallback.");
    }

    /// <summary>
    /// Only the <c>AdminOnly</c> policy counts, with one opt-in exception.
    /// <para>
    /// Naming the cookie scheme is deliberately <em>not</em> sufficient on its own.
    /// <c>[Authorize(AuthenticationSchemes = CookieScheme)]</c> enforces authentication without the
    /// allowlist, so it admits every identity the provider will authenticate - correct for
    /// <c>AdminSessionController.Logout</c>, catastrophic for an endpoint that reads or rewrites the
    /// configuration. Since sign-out would otherwise be the nearest in-repo example for anyone
    /// adding an admin endpoint, the shape is refused unless the endpoint also declares
    /// <see cref="AdminEndpointAttribute.AllowlistNotRequired"/>.
    /// </para>
    /// <para>
    /// Every <c>IAuthorizeData</c> on the endpoint is considered, not just the nearest one, because
    /// class-level and action-level attributes both land in the metadata and either may be the one
    /// naming the policy.
    /// </para>
    /// </summary>
    private static bool IsGuardedByAdminAuthorization(Endpoint endpoint)
    {
        var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

        if (authorization.Any(data =>
                string.Equals(data.Policy, AdminAuthentication.AdminOnlyPolicy, StringComparison.Ordinal)))
        {
            return true;
        }

        // The declared exception still has to be authenticated against the admin cookie - the flag
        // waives the allowlist, never authorization altogether, so a bare [Authorize] alongside it
        // is refused exactly as it would be on its own.
        return endpoint.Metadata.GetMetadata<AdminEndpointAttribute>()?.AllowlistNotRequired is true &&
               authorization.Any(data => NamesTheAdminCookieScheme(data.AuthenticationSchemes));
    }

    // AuthenticationSchemes is a comma-separated list when more than one scheme is named.
    private static bool NamesTheAdminCookieScheme(string? schemes) =>
        schemes is not null &&
        schemes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(scheme => string.Equals(scheme, AdminAuthentication.CookieScheme, StringComparison.Ordinal));
}
