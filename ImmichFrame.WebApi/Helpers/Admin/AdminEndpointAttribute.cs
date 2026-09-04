namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// Declares the two deliberate exceptions an endpoint under <c>/api/admin</c> may claim. Both
/// default to the safe answer, so an endpoint that says nothing gets the strictest treatment and a
/// forgotten attribute cannot open anything.
/// <para>
/// One attribute rather than two, because these are two properties of a single decision - how far
/// this endpoint steps outside the admin surface's defaults - and splitting them into separate
/// markers invites applying one and forgetting the other.
/// </para>
/// <para>
/// Pure endpoint metadata rather than a filter: <see cref="AdminSurfaceMiddleware"/> reads it before
/// <c>UseAuthorization</c>, and <see cref="AdminEndpointGuard"/> reads it at startup. MVC's filter
/// pipeline runs after the authorization middleware, so a resource filter would answer 401 on an
/// unconfigured installation - advertising an admin surface that does not exist - instead of 404.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AdminEndpointAttribute : Attribute
{
    /// <summary>
    /// This endpoint is deliberately reachable without an authenticated admin session.
    /// <para>
    /// True for exactly two: the sign-in challenge, which is what a caller with no session uses to
    /// get one, and the session status endpoint, which exists to be asked before anyone has signed
    /// in. Leaving it false is what makes <see cref="AdminEndpointGuard"/> refuse to start a host
    /// whose admin endpoint carries no authorization.
    /// </para>
    /// </summary>
    public bool Anonymous { get; init; }

    /// <summary>
    /// This endpoint is guarded by admin <em>authentication</em> alone - a valid admin cookie - and
    /// deliberately does not require the caller to be on the allowlist.
    /// <para>
    /// True for exactly one: signing out. Ending your own session is not a privileged act, and
    /// gating it on the allowlist strands a signed-in non-administrator whose polling of the session
    /// endpoint keeps renewing the sliding cookie.
    /// </para>
    /// <para>
    /// It is a flag rather than something <see cref="AdminEndpointGuard"/> infers from the
    /// authorization attribute because <c>[Authorize(AuthenticationSchemes = CookieScheme)]</c> is a
    /// fail-open shape for anything else: it admits every identity the provider will authenticate,
    /// which on a provider with open registration is a stranger. Sign-out is the exception, so
    /// sign-out is what has to say so - visibly, at the call site, and greppable - instead of the
    /// shape being blessed everywhere because one endpoint needed it.
    /// </para>
    /// </summary>
    public bool AllowlistNotRequired { get; init; }

    /// <summary>
    /// This endpoint answers even while the admin surface is unconfigured, instead of 404ing with
    /// the rest of the prefix.
    /// <para>
    /// True for the session status endpoint alone: the SPA has to be able to tell "log in" from
    /// "this installation has no admin surface", and it cannot do that if the endpoint that would
    /// say so is hidden along with everything else.
    /// </para>
    /// </summary>
    public bool VisibleWhenUnconfigured { get; init; }
}
