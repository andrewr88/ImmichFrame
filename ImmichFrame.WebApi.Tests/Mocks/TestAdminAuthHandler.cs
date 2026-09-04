using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImmichFrame.WebApi.Tests.Mocks;

/// <summary>
/// Stands in for the admin cookie handler so the policy, the fail-closed paths and the middleware
/// bypass can be tested without an identity provider. The real cookie scheme forwards
/// authentication here (<c>CookieAuthenticationOptions.ForwardAuthenticate</c>), so everything the
/// application does with the scheme - the policy's explicit scheme list, the status endpoint's
/// explicit <c>AuthenticateAsync</c>, the 401/403 events - is exercised unchanged; only the source
/// of the principal differs.
/// <para>
/// The claims come from request headers rather than fixture state so a single host can serve an
/// anonymous, an allowlisted and a non-allowlisted caller in the same test.
/// </para>
/// </summary>
public class TestAdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestAdminSession";
    public const string SubjectHeader = "X-Test-Admin-Sub";
    public const string EmailHeader = "X-Test-Admin-Email";
    public const string EmailVerifiedHeader = "X-Test-Admin-Email-Verified";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers[SubjectHeader].FirstOrDefault();
        var email = Request.Headers[EmailHeader].FirstOrDefault();

        if (string.IsNullOrEmpty(subject) && string.IsNullOrEmpty(email))
        {
            // No headers means no cookie: the caller has no admin session.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(subject)) claims.Add(new Claim("sub", subject));
        if (!string.IsNullOrEmpty(email)) claims.Add(new Claim("email", email));

        var emailVerified = Request.Headers[EmailVerifiedHeader].FirstOrDefault();
        if (!string.IsNullOrEmpty(emailVerified)) claims.Add(new Claim("email_verified", emailVerified));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "sub", ClaimTypes.Role));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
