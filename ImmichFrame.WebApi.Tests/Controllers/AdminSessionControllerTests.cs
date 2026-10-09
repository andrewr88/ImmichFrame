using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace ImmichFrame.WebApi.Tests.Controllers;

[TestFixture]
public class AdminSessionControllerTests
{
    private const string FrameSecret = "frame-secret";
    private const string AdminSubject = "8ab0c1e2-user";
    private const string OtherSubject = "not-an-admin";

    private const string PolicyProbe = "/api/admin/probe/policy";
    private const string SignInProbe = "/api/admin/probe/sign-in";

    // Every host here runs with a frame AuthenticationSecret configured: without one the frame
    // scheme succeeds anonymously and the middleware bypass would prove nothing.
    private static AdminOidcOptions Enabled(params string[] admins) => new()
    {
        Authority = "https://idp.example.com",
        ClientId = "immichframe",
        ClientSecret = "client-secret",
        Admins = admins.Length == 0 ? [AdminSubject] : admins
    };

    private static readonly AdminOidcOptions EmptyAllowlist = new()
    {
        Authority = "https://idp.example.com",
        ClientId = "immichframe",
        ClientSecret = "client-secret",
        Admins = []
    };

    private static readonly AdminOidcOptions Unconfigured = new();

    [Test]
    public async Task Session_OidcNotConfigured_ReportsUnconfiguredWithoutAuthentication()
    {
        using var factory = CreateFactory(Unconfigured);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/session");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            // Reachable with no bearer token and no session - the SPA has to be able to ask.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((bool?)body?["configured"], Is.False);
            Assert.That((bool?)body?["authenticated"], Is.False);
            Assert.That((bool?)body?["isAdmin"], Is.False);
        });
    }

    /// <summary>
    /// The session endpoint is documented as always reachable, and the SPA relies on it to tell
    /// "log in" from "this installation has no admin surface". A profile on the query string must
    /// not be able to answer for it - <c>UnknownProfileMiddleware</c> runs ahead of everything.
    /// </summary>
    [Test]
    public async Task Session_UnknownProfileOnTheQueryString_StillAnswers()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/session?profile=bogus");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((bool?)body?["configured"], Is.True);
        });

        // ...while a frame endpoint still gets the profile 404 it always did.
        var frame = await client.GetAsync("/api/Config?profile=bogus");
        Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AdminEndpoints_OidcNotConfigured_Are404()
    {
        using var factory = CreateFactory(Unconfigured);
        var client = factory.CreateClient();

        var logoutAnonymous = await client.PostAsync("/api/admin/logout", null);
        var logoutWithSession = await Post(client, "/api/admin/logout", AdminSubject);
        var login = await client.GetAsync("/api/admin/login");
        var policy = await Get(client, PolicyProbe, AdminSubject);

        Assert.Multiple(() =>
        {
            // Not 401, and not an unprotected endpoint: the surface simply is not here.
            Assert.That(logoutAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(logoutWithSession.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(policy.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    /// <summary>
    /// The flags on <c>[AdminEndpoint]</c> are independent, and this is where that shows.
    /// <c>login</c> declares <c>Anonymous</c> but not <c>VisibleWhenUnconfigured</c>, so being
    /// deliberately reachable without a session does not make it answer on an installation that has
    /// no admin surface; only <c>session</c> claims both and survives. The prefix is hidden by
    /// default, so an endpoint added later that claims neither is hidden without anyone deciding it
    /// should be.
    /// <para>
    /// In any casing, because routing ignores case: <c>/API/ADMIN/login</c> reaches the login just as
    /// <c>/api/admin/login</c> does, so a prefix recognised only as written would leave every endpoint
    /// answering on an installation with no admin surface - the login with a handshake that has no
    /// handler registered.
    /// </para>
    /// </summary>
    [TestCase("/api/admin")]
    [TestCase("/API/ADMIN")]
    [TestCase("/Api/Admin")]
    public async Task AdminPrefix_Unconfigured_HidesEvenAnonymousEndpoints(string prefix)
    {
        using var factory = CreateFactory(Unconfigured);
        var client = factory.CreateClient();

        var anonymousButNotVisible = await client.GetAsync($"{prefix}/login");
        var authorizedButNotVisible = await client.GetAsync($"{prefix}/probe/policy");
        var both = await client.GetAsync($"{prefix}/session");

        Assert.Multiple(() =>
        {
            Assert.That(anonymousButNotVisible.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(authorizedButNotVisible.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(both.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    /// <summary>
    /// A unit test of the exemption predicate, not of pipeline behaviour - it never enters the
    /// pipeline, and it is named that way on purpose. The distinction it asserts has no observable
    /// effect today: with OpenID Connect unconfigured the handshake paths match only the SPA
    /// fallback, which carries no <c>[Authorize]</c>, so the frame handler succeeds anonymously and
    /// they answer 200 either way. The predicate is defence in depth, and this pins its shape.
    /// </summary>
    [Test]
    public void IsFrameAuthenticationExempt_ExcusesHandshakePathsOnlyWhenOidcIsEnabled()
    {
        Assert.Multiple(() =>
        {
            foreach (var path in new[] { "/signin-oidc", "/signout-oidc", "/signout-callback-oidc" })
            {
                Assert.That(AdminAuthentication.IsFrameAuthenticationExempt(path, oidcEnabled: true), Is.True, path);
                Assert.That(AdminAuthentication.IsFrameAuthenticationExempt(path, oidcEnabled: false), Is.False, path);
            }

            // The API and the SPA route stay exempt either way: the session endpoint has to be able
            // to report that the surface is off, and the editor page has to load in order to say so.
            foreach (var path in new[] { "/api/admin/session", "/admin" })
            {
                Assert.That(AdminAuthentication.IsFrameAuthenticationExempt(path, oidcEnabled: true), Is.True, path);
                Assert.That(AdminAuthentication.IsFrameAuthenticationExempt(path, oidcEnabled: false), Is.True, path);
            }

            // And the frame API is never exempt, whatever the admin surface is doing.
            foreach (var path in new[] { "/api/Config", "/api/Calendar", "/api/adminfoo" })
            {
                Assert.That(AdminAuthentication.IsFrameAuthenticationExempt(path, oidcEnabled: true), Is.False, path);
                Assert.That(AdminAuthentication.IsFrameAuthenticationExempt(path, oidcEnabled: false), Is.False, path);
            }
        });
    }

    [Test]
    public async Task AdminEndpoints_AllowlistEmpty_RefuseEveryone()
    {
        using var factory = CreateFactory(EmptyAllowlist);
        var client = factory.CreateClient();

        var policy = await Get(client, PolicyProbe, AdminSubject);
        var login = await client.GetAsync("/api/admin/login");
        var session = JsonNode.Parse(await (await client.GetAsync("/api/admin/session")).Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            // An empty allowlist is not "everyone" - it is "nobody", so the surface stays off.
            Assert.That(policy.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((bool?)session?["configured"], Is.False);
            Assert.That((bool?)session?["isAdmin"], Is.False);
        });
    }

    [Test]
    public async Task AdminEndpoint_NoSession_Is401AndNotTheFrameSchemesRefusal()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var response = await client.GetAsync(PolicyProbe);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            // The frame scheme writes its failure message into the body; the admin cookie scheme
            // answers with a bare status code. Seeing the former would mean the bypass never fired.
            Assert.That(body, Does.Not.Contain("Authorization"));
            Assert.That(body, Does.Not.Contain("AuthenticationSecret"));
        });
    }

    [Test]
    public async Task AdminEndpoint_SessionNotOnAllowlist_Is403()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var response = await Get(client, PolicyProbe, OtherSubject);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task AdminEndpoint_AllowlistedSession_Is200()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var response = await Get(client, PolicyProbe, AdminSubject);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task AdminEndpoint_EmailAllowlistedButUnverified_Is403()
    {
        using var factory = CreateFactory(Enabled("admin@example.com"));
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, PolicyProbe);
        request.Headers.Add(TestAdminAuthHandler.EmailHeader, "admin@example.com");
        request.Headers.Add(TestAdminAuthHandler.EmailVerifiedHeader, "false");

        var response = await client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    /// <summary>
    /// Sign-out is authentication-only on purpose. A signed-in non-administrator has to be able to
    /// end their own session: the "you are not an administrator" screen polls the session endpoint,
    /// which re-issues the sliding cookie, so a policy-gated sign-out would never let them out.
    /// <para>
    /// And succeeding has to mean the session is over: the browser is told to drop the cookie - its
    /// value emptied and its expiry in the past. A 200 that left the cookie alone would sign nobody out.
    /// </para>
    /// </summary>
    [Test]
    public async Task Logout_SignedInButNotOnAllowlist_StillSucceeds()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var response = await Post(client, "/api/admin/logout", OtherSubject);
        var cleared = AdminCookieSetBy(factory, response);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(cleared, Is.Not.Null, "the sign-out did not touch the admin cookie");
            Assert.That(cleared?.Value.ToString(), Is.Empty);
            Assert.That(cleared?.Expires, Is.LessThan(DateTimeOffset.UtcNow));
        });
    }

    /// <summary>
    /// The admin cookie is the whole session, so what it tells the browser is pinned twice: as the
    /// scheme is configured, and as a sign-in actually writes it.
    /// <para>
    /// HttpOnly, so that no script on the editor's origin - an injected one included - can read the
    /// session and carry it off. Lax rather than None, because staying off a cross-site POST is what
    /// lets sign-out go without a CSRF token; and rather than Strict, which would withhold the cookie on
    /// the top-level redirect back from the identity provider - Program.cs has the longer note.
    /// </para>
    /// </summary>
    [Test]
    public async Task AdminCookie_IsHttpOnlyAndSameSiteLax()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuthentication.CookieScheme).Cookie;

        var response = await client.PostAsync(SignInProbe, null);
        var issued = response.Headers.TryGetValues("Set-Cookie", out var headers)
            ? headers.SingleOrDefault(header => header.StartsWith(cookie.Name + "=", StringComparison.Ordinal))
            : null;
        var attributes = issued?.Split(';', StringSplitOptions.TrimEntries).Skip(1).ToList() ?? [];

        Assert.Multiple(() =>
        {
            Assert.That(cookie.HttpOnly, Is.True);
            Assert.That(cookie.SameSite, Is.EqualTo(SameSiteMode.Lax));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(issued, Is.Not.Null, "the sign-in issued no admin cookie");
            Assert.That(attributes, Has.Some.EqualTo("httponly").IgnoreCase);
            Assert.That(attributes, Has.Some.EqualTo("samesite=lax").IgnoreCase);
        });
    }

    /// <summary>
    /// How long a session lasts: eight hours, renewed while it is in use. Renewal is what keeps an
    /// administrator who is still using the editor signed in; the eight hours are what end a session
    /// somebody walked away from.
    /// </summary>
    [Test]
    public void AdminCookie_LastsEightHoursAndIsRenewedByUse()
    {
        using var factory = CreateFactory(Enabled());

        var options = AdminCookieOptions(factory);

        Assert.Multiple(() =>
        {
            Assert.That(options.ExpireTimeSpan, Is.EqualTo(TimeSpan.FromHours(8)));
            Assert.That(options.SlidingExpiration, Is.True);
        });
    }

    /// <summary>
    /// Secure exactly when the sign-in came over https. Always would have the browser refuse the
    /// cookie from a frame served over plain http, so nobody could sign in there at all; never would
    /// let a session issued over https be sent over plain http as well.
    /// </summary>
    [Test]
    public async Task AdminCookie_IsSecureWhenTheSignInCameOverHttps()
    {
        using var factory = CreateFactory(Enabled());
        var httpsClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var overHttps = await httpsClient.PostAsync(SignInProbe, null);
        var overHttp = await factory.CreateClient().PostAsync(SignInProbe, null);

        Assert.Multiple(() =>
        {
            Assert.That(AdminCookieOptions(factory).Cookie.SecurePolicy, Is.EqualTo(CookieSecurePolicy.SameAsRequest));

            Assert.That(AdminCookieSetBy(factory, overHttps)?.Secure, Is.True, "signed in over https");
            Assert.That(AdminCookieSetBy(factory, overHttp)?.Secure, Is.False, "signed in over plain http");
        });
    }

    /// <summary>
    /// Crossing the schemes in the direction that matters: the frame's bearer token is a valid
    /// credential for <c>ImmichFrameScheme</c>, which is still the application default. If the
    /// admin endpoint ever lost its explicit scheme, this token would authenticate the caller and
    /// the answer would change from 401 to something else entirely.
    /// </summary>
    [Test]
    public async Task AdminEndpoint_FrameBearerTokenIsNotAnAdminSession()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var logout = await GetWithSecret(client, "/api/admin/logout", FrameSecret, HttpMethod.Post);
        var policy = await GetWithSecret(client, PolicyProbe, FrameSecret, HttpMethod.Get);

        Assert.Multiple(() =>
        {
            Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(policy.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    /// <summary>And the other direction: an admin session is not a frame credential.</summary>
    [Test]
    public async Task FrameEndpoint_AdminSessionIsNotAFrameCredential()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var response = await Get(client, "/api/Calendar", AdminSubject);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// The mandatory regression. The bypass is a path list consulted for every request, so the frame
    /// API has to be shown still refusing an unauthenticated caller and still accepting the bearer
    /// token, on a host where the admin surface is fully configured.
    /// </summary>
    [Test]
    public async Task FrameEndpoints_AreUnaffectedByTheAdminBypass()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var calendarAnonymous = await client.GetAsync("/api/Calendar");
        var calendarWithSecret = await GetWithSecret(client, "/api/Calendar", FrameSecret, HttpMethod.Get);
        var calendarWithWrongSecret = await GetWithSecret(client, "/api/Calendar", "wrong", HttpMethod.Get);
        var weatherAnonymous = await client.GetAsync("/api/Weather");
        // ConfigController deliberately carries no [Authorize], so it answers anonymously with or
        // without a secret; asserted here so a future bypass change cannot quietly alter it either.
        var config = await client.GetAsync("/api/Config");

        Assert.Multiple(() =>
        {
            Assert.That(calendarAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(calendarWithSecret.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(calendarWithWrongSecret.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(weatherAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(config.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task Session_AllowlistedByEmail_ReportsTheSignedInAdministrator()
    {
        using var factory = CreateFactory(Enabled("Admin@Example.com"));
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/session");
        request.Headers.Add(TestAdminAuthHandler.EmailHeader, "admin@example.com");

        var response = await client.SendAsync(request);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That((bool?)body?["configured"], Is.True);
            Assert.That((bool?)body?["authenticated"], Is.True);
            Assert.That((bool?)body?["isAdmin"], Is.True);
            Assert.That((string?)body?["email"], Is.EqualTo("admin@example.com"));
        });
    }

    /// <summary>
    /// The answer names whoever the cookie on the request belongs to, and is a different answer for
    /// every cookie. So no cache may keep a copy: the browser's could go on showing that person signed
    /// in after they have signed out, and one between it and the server could hand them to whoever
    /// asked next.
    /// </summary>
    [Test]
    public async Task Session_IsNotStoredByAnyCache()
    {
        using var factory = CreateFactory(Enabled("admin@example.com"));
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/session");
        request.Headers.Add(TestAdminAuthHandler.EmailHeader, "admin@example.com");

        var response = await client.SendAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        });
    }

    [Test]
    public async Task Session_SignedInButNotOnAllowlist_SaysAuthenticatedButNotAdmin()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/session");
        request.Headers.Add(TestAdminAuthHandler.SubjectHeader, OtherSubject);

        var body = JsonNode.Parse(await (await client.SendAsync(request)).Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That((bool?)body?["authenticated"], Is.True);
            Assert.That((bool?)body?["isAdmin"], Is.False);
        });
    }

    internal static WebApplicationFactory<Program> CreateFactory(AdminOidcOptions adminOptions)
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(versionHandler);
                    services.AddSingleton<IConfigCatalog>(Catalog());

                    // Last registration wins, so this replaces the instance Program built from the
                    // environment - no process-wide environment variables in a test.
                    services.AddSingleton(adminOptions);

                    // The application's own cookie scheme, forwarding authentication to a handler
                    // that mints a principal from request headers.
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>(TestAdminAuthHandler.SchemeName, _ => { });
                    services.Configure<CookieAuthenticationOptions>(
                        AdminAuthentication.CookieScheme,
                        options => options.ForwardAuthenticate = TestAdminAuthHandler.SchemeName);

                    services.AddControllers().AddApplicationPart(typeof(AdminProbeController).Assembly);
                });
            });
    }

    private static CookieAuthenticationOptions AdminCookieOptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuthentication.CookieScheme);

    /// <summary>What a response's <c>Set-Cookie</c> says of the admin cookie; null if it says nothing.</summary>
    private static SetCookieHeaderValue? AdminCookieSetBy(WebApplicationFactory<Program> factory, HttpResponseMessage response)
    {
        var name = AdminCookieOptions(factory).Cookie.Name;

        return response.Headers.TryGetValues("Set-Cookie", out var headers)
            ? SetCookieHeaderValue.ParseList(headers.ToList()).SingleOrDefault(cookie => cookie.Name.Equals(name, StringComparison.Ordinal))
            : null;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, string subject) =>
        Send(client, HttpMethod.Post, url, subject);

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string subject) =>
        Send(client, HttpMethod.Get, url, subject);

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string subject)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAdminAuthHandler.SubjectHeader, subject);

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetWithSecret(HttpClient client, string url, string secret, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        return client.SendAsync(request);
    }

    internal static ConfigCatalog Catalog() => new(new ServerSettings
    {
        GeneralSettingsImpl = new GeneralSettings { AuthenticationSecret = FrameSecret },
        AccountsImpl = new List<ServerAccountSettings>
        {
            new()
            {
                ImmichServerUrl = "http://mock-immich-server.com",
                ApiKey = "test-api-key",
            }
        }
    }, []);
}
