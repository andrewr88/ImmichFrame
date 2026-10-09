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

    /// <summary>
    /// The claim an identity provider vouches for an address with. Named once because two places
    /// have to agree on it: the claim mapping in <c>Program.cs</c> that carries it out of the
    /// userinfo response, and <see cref="EmailClaimsAreTrusted"/>, which reads it. A mapping under
    /// any other name would leave the veto reading a claim that never arrives.
    /// </summary>
    public const string EmailVerifiedClaimType = "email_verified";

    public string? Authority { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Allowlisted <c>sub</c> or <c>email</c> values; already trimmed, with empties dropped. An entry
    /// holding an <c>@</c> is an address and any other a <c>sub</c> - see <see cref="IsAdmin"/>.
    /// </summary>
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
    /// Logs one warning when the admin surface has been half set up, naming what it is missing.
    /// <para>
    /// Nothing else would say so. A surface short of any one of its four variables is simply off -
    /// its endpoints 404, and <c>/admin</c> says only that it is off - which from outside is exactly
    /// what an installation that never asked for the editor looks like. Someone who set three of the
    /// variables and misspelled the fourth deserves to be told which; someone who set none of them
    /// did not ask, and is told nothing.
    /// </para>
    /// <para>
    /// The other way to half set it up is an allowlist written one entry per line. Entries are
    /// separated by commas, so it parses into entries with the line breaks still inside them: the
    /// surface is on, and no sign-in can ever match those entries. No <c>sub</c> or address holds a
    /// control character, so any entry that does is one of these.
    /// </para>
    /// <para>
    /// Variables are named, never their values: one of them is the client secret, and the allowlist
    /// is a list of people.
    /// </para>
    /// </summary>
    public void WarnIfHalfConfigured(ILogger logger)
    {
        if (IsEnabled)
        {
            if (Admins.Any(entry => entry.Any(char.IsControl)))
            {
                logger.LogWarning(
                    "An entry in {variable} contains a line break or another control character, so no sign-in can ever match it. Separate administrators with commas.",
                    AdminsVariable);
            }

            return;
        }

        (string Name, bool IsSet)[] variables =
        [
            (AuthorityVariable, !string.IsNullOrWhiteSpace(Authority)),
            (ClientIdVariable, !string.IsNullOrWhiteSpace(ClientId)),
            (ClientSecretVariable, !string.IsNullOrWhiteSpace(ClientSecret)),
            (AdminsVariable, Admins.Count > 0)
        ];

        if (!variables.Any(variable => variable.IsSet))
        {
            return;
        }

        logger.LogWarning(
            "The admin editor at /admin is off. Missing or empty: {missingVariables}. It stays off until the OpenID Connect authority, client id, client secret and administrator list are all set.",
            string.Join(", ", variables.Where(variable => !variable.IsSet).Select(variable => variable.Name)));
    }

    /// <summary>
    /// Whether <paramref name="user"/> is on the allowlist. Each entry is matched by its shape: one
    /// holding an <c>@</c> is an address, compared with the <c>email</c> claim only (ordinal,
    /// case-insensitive - the local part of an address is technically case-sensitive but no
    /// identity provider in practice treats it that way, and a case-sensitive comparison here would
    /// lock administrators out for a capital letter); any other entry is a <c>sub</c>, compared with
    /// the <c>sub</c> claim only (ordinal).
    /// <para>
    /// Routed rather than tried against both claims, because the two are not equally trustworthy and
    /// trying both lets the weaker answer for the stronger. A <c>sub</c> is assigned by the provider,
    /// but on a provider with self-service registration an address is whatever the user typed - so
    /// a <c>sub</c> entry also compared with the email claim could be claimed by putting the
    /// administrator's <c>sub</c> into your own profile's email field. The other way round, a
    /// provider that uses the username as the <c>sub</c> lets a stranger register an allowlisted
    /// address as their username, and an email entry also compared with the <c>sub</c> would let
    /// that past the <c>email_verified</c> veto, which only ever watches the email claim. A provider
    /// set up to use the address itself as the <c>sub</c> is matched on its email claim instead,
    /// which carries the same address.
    /// </para>
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

        return Admins.Any(entry => IsEmailEntry(entry)
            ? emails.Any(email => string.Equals(email, entry, StringComparison.OrdinalIgnoreCase))
            : subjects.Any(subject => string.Equals(subject, entry, StringComparison.Ordinal)));
    }

    /// <summary>Whether an allowlist entry names an address rather than a <c>sub</c> - see <see cref="IsAdmin"/>.</summary>
    private static bool IsEmailEntry(string entry) => entry.Contains('@');

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
        !user.FindAll(EmailVerifiedClaimType).Any(claim => bool.TryParse(claim.Value, out var verified) && !verified);

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
