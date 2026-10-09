using System.Collections.Generic;
using System.Security.Claims;
using ImmichFrame.WebApi.Helpers.Admin;
using Microsoft.Extensions.Logging;
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
    public void IsAdmin_AllowlistSetButOidcNotConfigured_IsFalse()
    {
        // The half of fail-closed an empty allowlist cannot reach: an allowlist on its own is not a
        // surface. IsAdmin is still asked - the session endpoint asks it on every installation - and
        // must not need its caller to have checked IsEnabled first, or a principal matching an entry,
        // from a cookie minted while the surface was still on, say, would be called an administrator
        // of a surface that is off.
        var options = new AdminOidcOptions { Admins = [Subject, Email] };

        Assert.Multiple(() =>
        {
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
    public void IsAdmin_AnEmailEqualToAnAllowlistedSubject_IsRefused()
    {
        // A sub is the provider's to choose, and on a provider with self-service registration an
        // address is the user's. Comparing a sub entry with the email claim as well would hand the
        // choice back: put the administrator's sub in your own profile's email field and you are them.
        var options = Options(Subject);

        Assert.That(options.IsAdmin(Principal(sub: "someone-else", email: Subject)), Is.False);
    }

    [Test]
    public void IsAdmin_ASubjectEqualToAnAllowlistedEmail_IsRefused()
    {
        // And the other way round. A provider that uses the username as the sub lets a stranger
        // register an allowlisted address as their username, and comparing an email entry with the
        // sub would let that past the email_verified veto, which only ever watches the email claim.
        var options = Options(Email);

        Assert.That(options.IsAdmin(Principal(sub: Email)), Is.False);
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

    // "False" is how a JSON boolean arrives from the userinfo endpoint (JsonElement.ToString), and an
    // id token carries "false" - so the veto has to read the value as a boolean, not compare it with
    // one spelling.
    [TestCase("false")]
    [TestCase("False")]
    [TestCase(" false ")]
    [TestCase("FALSE")]
    public void IsAdmin_EmailExplicitlyUnverified_IsRefused(string emailVerified)
    {
        // The attack this closes: on an identity provider with self-service registration, anyone can
        // type an allowlisted address into their own profile. A false email_verified is the provider
        // saying nobody checked, and it must veto the match.
        var options = Options(Email);

        var unverified = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", Email), new Claim("email_verified", emailVerified)], "TestScheme"));

        Assert.That(options.IsAdmin(unverified), Is.False);
    }

    [TestCase("true")]
    [TestCase("True")]
    public void IsAdmin_EmailExplicitlyVerified_IsAccepted(string emailVerified)
    {
        var options = Options(Email);

        var verified = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", Email), new Claim("email_verified", emailVerified)], "TestScheme"));

        Assert.That(options.IsAdmin(verified), Is.True);
    }

    [TestCase("sometimes")]
    [TestCase("0")]
    [TestCase("")]
    public void IsAdmin_EmailVerifiedAbsentOrUnparseable_DoesNotVeto(string emailVerified)
    {
        // Plenty of providers omit the claim, and one that sends something that is not a boolean is
        // saying nothing rather than saying no. Requiring it would lock those installations out.
        // That is decided by bool.TryParse alone, so a 0 - false in some languages, not a boolean
        // here - is saying nothing too: the veto is for a provider that says false, not one that
        // might mean it.
        var options = Options(Email);

        var unparseable = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", Email), new Claim("email_verified", emailVerified)], "TestScheme"));

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

    /// <summary>
    /// A half-configured surface is off, and from outside an installation whose surface is off looks
    /// exactly like one that never asked for the editor - so the startup log is the one place that
    /// can say which variable is missing, and it has to name exactly those.
    /// </summary>
    [TestCase(true, true, true, false, new[] { AdminOidcOptions.AdminsVariable })]
    [TestCase(false, false, false, true,
        new[] { AdminOidcOptions.AuthorityVariable, AdminOidcOptions.ClientIdVariable, AdminOidcOptions.ClientSecretVariable })]
    [TestCase(true, true, false, true, new[] { AdminOidcOptions.ClientSecretVariable })]
    public void WarnIfHalfConfigured_NamesExactlyWhatIsMissing(
        bool authority, bool clientId, bool clientSecret, bool admins, string[] missing)
    {
        var options = new AdminOidcOptions
        {
            Authority = authority ? Authority : null,
            ClientId = clientId ? "client" : null,
            ClientSecret = clientSecret ? SecretValue : null,
            Admins = admins ? [Subject, Email] : []
        };

        var log = new CapturingLog();
        options.WarnIfHalfConfigured(log);

        Assert.That(log.Entries, Has.Count.EqualTo(1));

        var (level, message) = log.Entries[0];
        Assert.Multiple(() =>
        {
            Assert.That(level, Is.EqualTo(LogLevel.Warning));

            foreach (var variable in AllVariables)
            {
                Assert.That(message, missing.Contains(variable) ? Does.Contain(variable) : Does.Not.Contain(variable));
            }

            // Names, never values: one of them is the client secret, and the allowlist is people.
            Assert.That(message, Does.Not.Contain(SecretValue));
            Assert.That(message, Does.Not.Contain(Subject));
            Assert.That(message, Does.Not.Contain(Email));
        });
    }

    [Test]
    public void WarnIfHalfConfigured_NothingOrEverythingConfigured_SaysNothing()
    {
        // Nothing set is an installation that never asked for the editor, and is not to be nagged
        // about it on every start. Everything set is a surface that is on.
        var nothing = new CapturingLog();
        var everything = new CapturingLog();

        new AdminOidcOptions().WarnIfHalfConfigured(nothing);
        Options(Subject, Email).WarnIfHalfConfigured(everything);

        Assert.Multiple(() =>
        {
            Assert.That(nothing.Entries, Is.Empty);
            Assert.That(everything.Entries, Is.Empty);
        });
    }

    [Test]
    public void WarnIfHalfConfigured_AnAllowlistWrittenOnePerLine_SaysNobodyCanMatchIt()
    {
        // Entries are separated by commas, so a list written one per line - easy to do in a compose
        // file - parses into a single entry with the line break inside it. The surface is on, and
        // no sign-in can ever match that entry.
        var restore = Environment.GetEnvironmentVariable(AdminOidcOptions.AdminsVariable);
        IReadOnlyList<string> parsed;
        try
        {
            Environment.SetEnvironmentVariable(AdminOidcOptions.AdminsVariable, $"{Subject}\n{Email}\n");
            parsed = AdminOidcOptions.FromEnvironment().Admins;
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdminOidcOptions.AdminsVariable, restore);
        }

        var options = Options([.. parsed]);
        var log = new CapturingLog();
        options.WarnIfHalfConfigured(log);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Has.Count.EqualTo(1), "the premise: a line break does not separate entries");
            Assert.That(options.IsEnabled, Is.True);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(log.Entries[0].Message, Does.Contain(AdminOidcOptions.AdminsVariable).And.Contain("line break"));
            Assert.That(log.Entries[0].Message, Does.Not.Contain(Subject));
        });
    }

    private const string SecretValue = "client-secret-value";

    private static readonly string[] AllVariables =
    [
        AdminOidcOptions.AuthorityVariable,
        AdminOidcOptions.ClientIdVariable,
        AdminOidcOptions.ClientSecretVariable,
        AdminOidcOptions.AdminsVariable
    ];

    /// <summary>Everything logged, with its level, so a warning can be told from anything else.</summary>
    private sealed class CapturingLog : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
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
