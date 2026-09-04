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
    /// Every path the admin surface owns. Two middlewares step aside for exactly this list -
    /// <see cref="CustomAuthenticationMiddleware"/> so the frame's shared secret is not demanded of
    /// a browser session, and <c>UnknownProfileMiddleware</c> so a stray <c>?profile=</c> cannot
    /// answer for the admin API - and they read it from here rather than each keeping a copy,
    /// because two lists that must agree eventually will not.
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
    public static bool IsAdminPath(PathString path) =>
        OwnedPaths.Any(owned => path.StartsWithSegments(owned, StringComparison.OrdinalIgnoreCase));
}
