using System.Security.Claims;

namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// The admin surface's OpenID Connect configuration, read from the environment only.
/// <para>
/// Deliberately not part of <c>Settings.json</c>/<c>Settings.yml</c> or <c>IConfigCatalog</c>: the
/// editor these values guard is what writes that file, so putting them there would let the editor
/// break the way in to itself - and would put the client secret into a file that is routinely
/// pasted into bug reports.
/// </para>
/// </summary>
public class AdminOidcOptions
{
    public const string AuthorityVariable = "IMMICHFRAME_OIDC_AUTHORITY";
    public const string ClientIdVariable = "IMMICHFRAME_OIDC_CLIENT_ID";
    public const string ClientSecretVariable = "IMMICHFRAME_OIDC_CLIENT_SECRET";
    public const string AdminsVariable = "IMMICHFRAME_OIDC_ADMINS";
    public const string TrustProxyHeadersVariable = "IMMICHFRAME_OIDC_TRUST_PROXY_HEADERS";

    public string? Authority { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    /// <summary>Allowlisted <c>sub</c> or <c>email</c> values; already trimmed, with empties dropped.</summary>
    public IReadOnlyList<string> Admins { get; init; } = [];

    /// <summary>
    /// Whether <c>X-Forwarded-Proto</c>/<c>X-Forwarded-Host</c> may be trusted. Opt-in, and off by
    /// default - see the note where the middleware is registered in <c>Program.cs</c>.
    /// </summary>
    public bool TrustProxyHeaders { get; init; }

    /// <summary>Whether enough is configured to run the OpenID Connect handshake at all.</summary>
    public bool IsOidcConfigured =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// Whether the admin surface exists at all. An empty allowlist counts as not configured, never
    /// as "everyone": with no way to say who is an administrator there is no safe way to answer
    /// the question, so the surface stays off and its endpoints 404.
    /// </summary>
    public bool IsEnabled => IsOidcConfigured && Admins.Count > 0;

    public static AdminOidcOptions FromEnvironment() => new()
    {
        Authority = Environment.GetEnvironmentVariable(AuthorityVariable),
        ClientId = Environment.GetEnvironmentVariable(ClientIdVariable),
        ClientSecret = Environment.GetEnvironmentVariable(ClientSecretVariable),
        Admins = ParseAdmins(Environment.GetEnvironmentVariable(AdminsVariable)),
        TrustProxyHeaders = ParseBoolean(Environment.GetEnvironmentVariable(TrustProxyHeadersVariable))
    };

    /// <summary>
    /// Whether <paramref name="user"/> is on the allowlist, by <c>sub</c> (ordinal) or <c>email</c>
    /// (ordinal, case-insensitive - the local part of an address is technically case-sensitive but
    /// no identity provider in practice treats it that way, and a case-sensitive comparison here
    /// would lock administrators out for a capital letter).
    /// <para>
    /// Both the raw OpenID Connect claim names and the SOAP claim types ASP.NET maps them onto are
    /// accepted, because whether the mapping happened depends on
    /// <c>OpenIdConnectOptions.MapInboundClaims</c>.
    /// </para>
    /// <para>
    /// An email entry only ever matches an address the identity provider vouches for - see
    /// <see cref="EmailClaimsAreTrusted"/>. A <c>sub</c> entry has no such caveat, which is why it
    /// is the safer thing to put on the allowlist.
    /// </para>
    /// </summary>
    public bool IsAdmin(ClaimsPrincipal? user)
    {
        // Fail closed rather than trusting the caller to have checked: an unconfigured surface has
        // no administrators, so no principal can match.
        if (!IsEnabled || user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var subjects = ClaimValues(user, "sub", ClaimTypes.NameIdentifier);
        var emails = EmailClaimsAreTrusted(user) ? ClaimValues(user, "email", ClaimTypes.Email) : [];

        return Admins.Any(entry =>
            subjects.Any(subject => string.Equals(subject, entry, StringComparison.Ordinal)) ||
            emails.Any(email => string.Equals(email, entry, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Whether the principal's email claims may be matched against the allowlist at all.
    /// <para>
    /// On an identity provider that allows self-service registration - or on a shared multi-tenant
    /// one - a user can put any address they like into their own profile, and
    /// <c>GetClaimsFromUserInfoEndpoint</c> brings that self-asserted value here. Matching it would
    /// let a stranger become an administrator by typing an allowlisted address into a profile page.
    /// </para>
    /// <para>
    /// So an <c>email_verified</c> claim that parses false vetoes every email match: the provider is
    /// explicitly saying nobody checked this address. A claim that is absent, or present but not a
    /// boolean, is not treated as a veto - the provider is saying nothing either way, and demanding
    /// the claim would break every provider that omits it.
    /// </para>
    /// </summary>
    private static bool EmailClaimsAreTrusted(ClaimsPrincipal user) =>
        !user.FindAll("email_verified").Any(claim => bool.TryParse(claim.Value, out var verified) && !verified);

    /// <summary>The principal's <c>sub</c>, for logging and for the session status endpoint.</summary>
    public static string? SubjectOf(ClaimsPrincipal? user) =>
        user is null ? null : ClaimValues(user, "sub", ClaimTypes.NameIdentifier).FirstOrDefault();

    /// <summary>The principal's <c>email</c>, for the session status endpoint.</summary>
    public static string? EmailOf(ClaimsPrincipal? user) =>
        user is null ? null : ClaimValues(user, "email", ClaimTypes.Email).FirstOrDefault();

    private static List<string> ClaimValues(ClaimsPrincipal user, params string[] types) =>
        user.Claims
            .Where(claim => types.Contains(claim.Type, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(claim.Value))
            .Select(claim => claim.Value)
            .ToList();

    private static List<string> ParseAdmins(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    private static bool ParseBoolean(string? value) =>
        bool.TryParse(value?.Trim(), out var parsed) && parsed;
}
