namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// The names and paths the admin surface authenticates with.
/// <para>
/// They live in one place because two unrelated parts of the pipeline have to agree on them: the
/// schemes and remote paths configured on the OpenID Connect handler in <c>Program.cs</c>, and the
/// list of paths <see cref="CustomAuthenticationMiddleware"/> steps aside for. Were the middleware
/// to hardcode its own copies, changing a callback path would silently leave the frame scheme
/// 401ing the identity provider's redirect back to us.
/// </para>
/// </summary>
public static class AdminAuthentication
{
    /// <summary>
    /// Cookie scheme holding the admin session. Deliberately not the application's default scheme:
    /// the default stays <c>ImmichFrameScheme</c> so every frame endpoint keeps authenticating
    /// exactly as it did, and admin endpoints name this scheme explicitly instead of inheriting.
    /// </summary>
    public const string CookieScheme = "ImmichFrameAdminCookie";

    /// <summary>Scheme that performs the OpenID Connect handshake and signs into <see cref="CookieScheme"/>.</summary>
    public const string OidcScheme = "ImmichFrameAdminOidc";

    /// <summary>Authorization policy gating every admin endpoint except the session status endpoint.</summary>
    public const string AdminOnlyPolicy = "AdminOnly";

    /// <summary>Route prefix owned by the admin API. Matched by path segment, never as a bare string prefix.</summary>
    public const string ApiPathPrefix = "/api/admin";

    /// <summary>The SPA route that hosts the editor; served as <c>index.html</c> by the fallback endpoint.</summary>
    public const string SpaPath = "/admin";

    /// <summary>Where the identity provider redirects back to after a successful sign-in.</summary>
    public const string CallbackPath = "/signin-oidc";

    /// <summary>Where the identity provider redirects back to after an RP-initiated sign-out.</summary>
    public const string SignedOutCallbackPath = "/signout-callback-oidc";

    /// <summary>Where the identity provider posts a front-channel sign-out notification.</summary>
    public const string RemoteSignOutPath = "/signout-oidc";

    /// <summary>
    /// Every path the admin surface owns: its API, the SPA route that hosts the editor, and the
    /// three paths the OpenID Connect handler answers on.
    /// <para>
    /// Kept here rather than copied into each caller because several unrelated parts of the pipeline
    /// have to agree on it - <c>UnknownProfileMiddleware</c> and <c>CurrentProfile</c> (no admin path
    /// carries a configuration profile) and <see cref="CustomAuthenticationMiddleware"/> (the frame's
    /// shared secret is not demanded of a browser session) - and two lists that must agree
    /// eventually will not.
    /// </para>
    /// </summary>
    private static readonly PathString[] OwnedPaths =
    [
        ApiPathPrefix,
        SpaPath,
        CallbackPath,
        SignedOutCallbackPath,
        RemoteSignOutPath
    ];

    /// <summary>
    /// The subset of <see cref="OwnedPaths"/> that exists only while the OpenID Connect handler is
    /// registered. Nothing answers on them otherwise, so they are exempted from the frame scheme
    /// only when the admin surface is actually enabled.
    /// </summary>
    private static readonly PathString[] HandshakePaths =
    [
        CallbackPath,
        SignedOutCallbackPath,
        RemoteSignOutPath
    ];

    /// <summary>
    /// Whether <paramref name="path"/> belongs to the admin surface.
    /// <para>
    /// Matched with <see cref="PathString.StartsWithSegments(PathString, StringComparison)"/> rather
    /// than <c>string.StartsWith</c>: segment matching admits <c>/api/admin</c> and
    /// <c>/api/admin/session</c> but not <c>/api/adminfoo</c> or <c>/admins</c>. A prefix matching
    /// more than intended here would silently unauthenticate part of the frame API, so every entry
    /// is a path the admin surface owns outright and none is a prefix of an existing route
    /// (<c>/api/Asset</c>, <c>/api/Calendar</c>, <c>/api/Config</c>, <c>/api/Weather</c>).
    /// </para>
    /// <para>
    /// Case-insensitive because routing is: <c>/API/Admin</c> reaches the same endpoint as
    /// <c>/api/admin</c>, so a case-sensitive test would leave the admin API refusing a
    /// differently-cased URL.
    /// </para>
    /// </summary>
    public static bool IsAdminPath(PathString path) => StartsWithAny(path, OwnedPaths);

    /// <summary>
    /// Whether <paramref name="path"/> is excused from the frame's shared-secret scheme.
    /// <para>
    /// The API prefix and the SPA route always are - the session endpoint has to be able to report
    /// that the surface is off, and the editor page has to load in order to say so.
    /// </para>
    /// <para>
    /// The three handshake paths are excused only while <paramref name="oidcEnabled"/>. Be clear
    /// about what that buys today: <em>nothing observable</em>. With OpenID Connect unconfigured
    /// those paths match no endpoint but the SPA fallback, which carries no <c>[Authorize]</c>, and
    /// <c>ImmichFrameAuthenticationHandler</c> succeeds anonymously for any endpoint without one -
    /// so they answer 200 with the SPA shell either way, exactly as <c>/whatever</c> does. This is
    /// defence in depth against a future in which the frame handler stops succeeding anonymously,
    /// not a hole being closed.
    /// </para>
    /// </summary>
    public static bool IsFrameAuthenticationExempt(PathString path, bool oidcEnabled) =>
        StartsWithAny(path, [ApiPathPrefix, SpaPath]) ||
        (oidcEnabled && StartsWithAny(path, HandshakePaths));

    /// <summary>
    /// Whether <paramref name="endpoint"/> is one the admin surface actually declares, as opposed to
    /// whatever routing happened to match for a path under the prefix.
    /// <para>
    /// The SPA fallback's <c>{*path:nonfile}</c> pattern matches anything without a file extension,
    /// <c>/api/admin/typo</c> included, so "the request routed somewhere" is not the same as "the
    /// request reached an admin endpoint". Both the startup guard and
    /// <see cref="AdminSurfaceMiddleware"/> need that distinction, and comparing the matched
    /// endpoint's own route pattern is what draws it.
    /// </para>
    /// </summary>
    public static bool IsAdminEndpoint(Endpoint? endpoint) =>
        endpoint is RouteEndpoint route &&
        !string.IsNullOrEmpty(route.RoutePattern.RawText) &&
        IsAdminPath("/" + route.RoutePattern.RawText.TrimStart('/'));

    private static bool StartsWithAny(PathString path, PathString[] prefixes) =>
        prefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
