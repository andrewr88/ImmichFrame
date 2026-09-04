using System.Collections.Generic;
using System.Security.Claims;
using ImmichFrame.WebApi.Helpers.Admin;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Admin;

/// <summary>
/// The fail-closed rules, tested directly rather than through the host: everything downstream -
/// the policy, the middleware, the status endpoint - asks these two properties and this one method.
/// </summary>
[TestFixture]
public class AdminOidcOptionsTests
{
    private const string Authority = "https://idp.example.com";
    private const string Subject = "8ab0c1e2-user";
    private const string Email = "Admin@Example.com";

    [Test]
    public void IsEnabled_EverythingConfigured_IsTrue()
    {
        Assert.That(Options(Subject).IsEnabled, Is.True);
    }

    [Test]
    public void IsEnabled_AllowlistEmpty_IsFalse()
    {
        // The single worst failure this could ship: an empty allowlist meaning "everyone".
        var options = Options();

        Assert.Multiple(() =>
        {
            Assert.That(options.IsOidcConfigured, Is.True);
            Assert.That(options.IsEnabled, Is.False);
            Assert.That(options.IsAdmin(Principal(sub: Subject)), Is.False);
            Assert.That(options.IsAdmin(Principal(email: Email)), Is.False);
        });
    }

    [Test]
    public void IsOidcConfigured_AnyCredentialMissing_IsFalse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new AdminOidcOptions { ClientId = "client", ClientSecret = "secret", Admins = [Subject] }.IsEnabled, Is.False);
            Assert.That(new AdminOidcOptions { Authority = Authority, ClientSecret = "secret", Admins = [Subject] }.IsEnabled, Is.False);
            Assert.That(new AdminOidcOptions { Authority = Authority, ClientId = "client", Admins = [Subject] }.IsEnabled, Is.False);
            // Whitespace is not a credential.
            Assert.That(new AdminOidcOptions { Authority = Authority, ClientId = "client", ClientSecret = "  ", Admins = [Subject] }.IsEnabled, Is.False);
        });
    }

    [Test]
    public void IsAdmin_SubjectMatchesOrdinally()
    {
        var options = Options(Subject);

        Assert.Multiple(() =>
        {
            Assert.That(options.IsAdmin(Principal(sub: Subject)), Is.True);
            // A 'sub' is an opaque identifier, so casing is significant and must not be folded.
            Assert.That(options.IsAdmin(Principal(sub: Subject.ToUpperInvariant())), Is.False);
        });
    }

    [Test]
    public void IsAdmin_EmailMatchesIgnoringCase()
    {
        var options = Options(Email);

        Assert.That(options.IsAdmin(Principal(email: "admin@EXAMPLE.com")), Is.True);
    }

    [Test]
    public void IsAdmin_MatchesTheClaimTypesAspNetMapsOidcClaimsOnto()
    {
        // Whether 'sub'/'email' survive as themselves depends on MapInboundClaims, so both spellings
        // have to be accepted or a configuration change elsewhere silently empties the allowlist.
        var options = Options(Subject, Email);

        Assert.Multiple(() =>
        {
            Assert.That(options.IsAdmin(Principal(claimType: ClaimTypes.NameIdentifier, value: Subject)), Is.True);
            Assert.That(options.IsAdmin(Principal(claimType: ClaimTypes.Email, value: Email)), Is.True);
        });
    }

    [Test]
    public void IsAdmin_EmailExplicitlyUnverified_IsRefused()
    {
        // The attack this closes: on an identity provider with self-service registration, anyone can
        // type an allowlisted address into their own profile. A false email_verified is the provider
        // saying nobody checked, and it must veto the match.
        var options = Options(Email);

        var unverified = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", Email), new Claim("email_verified", "false")], "TestScheme"));

        Assert.That(options.IsAdmin(unverified), Is.False);
    }

    [Test]
    public void IsAdmin_EmailExplicitlyVerified_IsAccepted()
    {
        var options = Options(Email);

        var verified = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", Email), new Claim("email_verified", "true")], "TestScheme"));

        Assert.That(options.IsAdmin(verified), Is.True);
    }

    [Test]
    public void IsAdmin_EmailVerifiedAbsentOrUnparseable_DoesNotVeto()
    {
        // Plenty of providers omit the claim, and one that sends something that is not a boolean is
        // saying nothing rather than saying no. Requiring it would lock those installations out.
        var options = Options(Email);

        var unparseable = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", Email), new Claim("email_verified", "sometimes")], "TestScheme"));

        Assert.Multiple(() =>
        {
            Assert.That(options.IsAdmin(Principal(email: Email)), Is.True);
            Assert.That(options.IsAdmin(unparseable), Is.True);
        });
    }

    [Test]
    public void IsAdmin_UnverifiedEmailDoesNotBlockASubjectMatch()
    {
        // The veto is on email matching only: a 'sub' the provider assigned is still trustworthy.
        var options = Options(Subject);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Subject), new Claim("email", "someone@example.com"), new Claim("email_verified", "false")],
            "TestScheme"));

        Assert.That(options.IsAdmin(principal), Is.True);
    }

    [Test]
    public void IsAdmin_UnknownOrAnonymousPrincipal_IsFalse()
    {
        var options = Options(Subject);

        Assert.Multiple(() =>
        {
            Assert.That(options.IsAdmin(null), Is.False);
            Assert.That(options.IsAdmin(new ClaimsPrincipal(new ClaimsIdentity())), Is.False);
            Assert.That(options.IsAdmin(Principal(sub: "someone-else")), Is.False);
            // An unauthenticated identity carrying the right claim is still not a session.
            Assert.That(options.IsAdmin(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject)]))), Is.False);
        });
    }

    [Test]
    public void IsAdmin_IgnoresEmptyClaimValues()
    {
        // An allowlist entry can never be empty, but a claim can - and empty must not match empty.
        var options = new AdminOidcOptions
        {
            Authority = Authority,
            ClientId = "client",
            ClientSecret = "secret",
            Admins = [Subject]
        };

        Assert.That(options.IsAdmin(Principal(sub: string.Empty)), Is.False);
    }

    [Test]
    public void FromEnvironment_TrimsEntriesAndDropsEmptyOnes()
    {
        var restore = Environment.GetEnvironmentVariable(AdminOidcOptions.AdminsVariable);
        try
        {
            Environment.SetEnvironmentVariable(AdminOidcOptions.AdminsVariable, " one , ,two,, three ");
            Assert.That(AdminOidcOptions.FromEnvironment().Admins, Is.EqualTo(new[] { "one", "two", "three" }));

            // A list of nothing but separators and blanks is an empty allowlist, not a wildcard.
            Environment.SetEnvironmentVariable(AdminOidcOptions.AdminsVariable, "  , ,, ");
            Assert.That(AdminOidcOptions.FromEnvironment().Admins, Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdminOidcOptions.AdminsVariable, restore);
        }
    }

    [Test]
    public void FromEnvironment_TrustProxyHeadersDefaultsToOff()
    {
        var restore = Environment.GetEnvironmentVariable(AdminOidcOptions.TrustProxyHeadersVariable);
        try
        {
            Environment.SetEnvironmentVariable(AdminOidcOptions.TrustProxyHeadersVariable, null);
            Assert.That(AdminOidcOptions.FromEnvironment().TrustProxyHeaders, Is.False);

            Environment.SetEnvironmentVariable(AdminOidcOptions.TrustProxyHeadersVariable, "yes please");
            Assert.That(AdminOidcOptions.FromEnvironment().TrustProxyHeaders, Is.False);

            Environment.SetEnvironmentVariable(AdminOidcOptions.TrustProxyHeadersVariable, " True ");
            Assert.That(AdminOidcOptions.FromEnvironment().TrustProxyHeaders, Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdminOidcOptions.TrustProxyHeadersVariable, restore);
        }
    }

    private static AdminOidcOptions Options(params string[] admins) => new()
    {
        Authority = Authority,
        ClientId = "client",
        ClientSecret = "secret",
        Admins = admins
    };

    private static ClaimsPrincipal Principal(string? sub = null, string? email = null, string? claimType = null, string? value = null)
    {
        var claims = new List<Claim>();

        if (sub is not null) claims.Add(new Claim("sub", sub));
        if (email is not null) claims.Add(new Claim("email", email));
        if (claimType is not null && value is not null) claims.Add(new Claim(claimType, value));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
    }
}
