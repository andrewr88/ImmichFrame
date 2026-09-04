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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers;

[TestFixture]
public class AdminSessionControllerTests
{
    private const string FrameSecret = "frame-secret";
    private const string AdminSubject = "8ab0c1e2-user";
    private const string OtherSubject = "not-an-admin";

    private const string PolicyProbe = "/api/admin/probe/policy";

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
    /// The two flags on <c>[AdminEndpoint]</c> are independent, and this is where that shows.
    /// <c>login</c> declares <c>Anonymous</c> but not <c>VisibleWhenUnconfigured</c>, so being
    /// deliberately reachable without a session does not make it answer on an installation that has
    /// no admin surface; only <c>session</c> claims both and survives. The prefix is hidden by
    /// default, so an endpoint added later that claims neither is hidden without anyone deciding it
    /// should be.
    /// </summary>
    [Test]
    public async Task AdminPrefix_Unconfigured_HidesEvenAnonymousEndpoints()
    {
        using var factory = CreateFactory(Unconfigured);
        var client = factory.CreateClient();

        var anonymousButNotVisible = await client.GetAsync("/api/admin/login");
        var authorizedButNotVisible = await client.GetAsync(PolicyProbe);
        var both = await client.GetAsync("/api/admin/session");

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
    /// </summary>
    [Test]
    public async Task Logout_SignedInButNotOnAllowlist_StillSucceeds()
    {
        using var factory = CreateFactory(Enabled());
        var client = factory.CreateClient();

        var response = await Post(client, "/api/admin/logout", OtherSubject);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
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
