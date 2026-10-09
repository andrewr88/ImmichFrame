using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers;

/// <summary>
/// The one fixture that lets <c>Program</c> register the real OpenID Connect handler.
/// <para>
/// Everywhere else the <c>AdminOidcOptions</c> singleton is replaced after the host is built, which
/// cannot reach the <c>AddOpenIdConnect</c> call that already ran - so the challenge, and with it
/// the open-redirect guard on <c>returnUrl</c>, would never be exercised, and neither would the
/// claim mapping that decides what of the userinfo response reaches the allowlist. Here the
/// environment variables are set before <see cref="WebApplicationFactory{T}"/> builds anything, and
/// the handler's discovery document is supplied statically so no identity provider has to exist.
/// </para>
/// </summary>
[TestFixture]
public class AdminLoginChallengeTests
{
    private const string Authority = "https://idp.example.com/realms/immichframe";
    private const string AuthorizationEndpoint = Authority + "/protocol/openid-connect/auth";

    private static readonly string[] Variables =
    [
        AdminOidcOptions.AuthorityVariable,
        AdminOidcOptions.ClientIdVariable,
        AdminOidcOptions.ClientSecretVariable,
        AdminOidcOptions.AdminsVariable
    ];

    private readonly Dictionary<string, string?> _restore = new();

    [OneTimeSetUp]
    public void SetEnvironment()
    {
        foreach (var variable in Variables)
        {
            _restore[variable] = Environment.GetEnvironmentVariable(variable);
        }

        Environment.SetEnvironmentVariable(AdminOidcOptions.AuthorityVariable, Authority);
        Environment.SetEnvironmentVariable(AdminOidcOptions.ClientIdVariable, "immichframe");
        Environment.SetEnvironmentVariable(AdminOidcOptions.ClientSecretVariable, "client-secret");
        Environment.SetEnvironmentVariable(AdminOidcOptions.AdminsVariable, "admin@example.com");
    }

    [OneTimeTearDown]
    public void RestoreEnvironment()
    {
        foreach (var (variable, value) in _restore)
        {
            Environment.SetEnvironmentVariable(variable, value);
        }
    }

    [Test]
    public async Task Login_RedirectsToTheAuthorityWithOurCallback()
    {
        using var factory = CreateFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/api/admin/login");
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.Location!.GetLeftPart(UriPartial.Path), Is.EqualTo(AuthorizationEndpoint));
            Assert.That(query["client_id"].ToString(), Is.EqualTo("immichframe"));
            Assert.That(query["response_type"].ToString(), Is.EqualTo("code"));
            Assert.That(query["redirect_uri"].ToString(), Does.EndWith(AdminAuthentication.CallbackPath));
            Assert.That(query["scope"].ToString(), Does.Contain("email"));
            // PKCE, so an intercepted authorization code cannot be redeemed by anyone else.
            Assert.That(query["code_challenge"].ToString(), Is.Not.Empty);
        });
    }

    [Test]
    public async Task Login_LocalReturnUrl_IsHonoured()
    {
        using var factory = CreateFactory();

        Assert.That(await RedirectUriAfterLogin(factory, "?returnUrl=/admin/general"), Is.EqualTo("/admin/general"));
    }

    [Test]
    public async Task Login_OffSiteReturnUrl_IsReplacedByTheAdminRoute()
    {
        using var factory = CreateFactory();

        // Honouring these would make the login an open redirect that borrows the identity
        // provider's credibility: the victim really does sign in, and lands somewhere else.
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await RedirectUriAfterLogin(factory, "?returnUrl=https://evil.example"), Is.EqualTo(AdminAuthentication.SpaPath));
            Assert.That(await RedirectUriAfterLogin(factory, "?returnUrl=//evil.example"), Is.EqualTo(AdminAuthentication.SpaPath));
            Assert.That(await RedirectUriAfterLogin(factory, ""), Is.EqualTo(AdminAuthentication.SpaPath));
        });
    }

    /// <summary>
    /// What a userinfo response says about the address, carried into the session by the handler
    /// <c>Program</c> registered - run through that handler's own claim actions, because those are
    /// exactly what <c>GetClaimsFromUserInfoEndpoint</c> applies to the response, and a claim they do
    /// not map is dropped before <c>AdminOidcOptions</c> ever sees it.
    /// <para>
    /// This is the case the id token cannot cover. A provider following the specification's code
    /// flow puts the email claims in userinfo rather than in the id token - Zitadel does by default,
    /// Authelia does under its claims policies - so the only <c>email_verified</c> it ever sends is
    /// this one, and dropping it turned the veto off: a self-registered account carrying an
    /// allowlisted address was let in.
    /// </para>
    /// </summary>
    [TestCase("false", false)]
    // Sent as a string by some providers rather than as a JSON boolean.
    [TestCase("\"false\"", false)]
    [TestCase("true", true)]
    // Absent is the provider saying nothing either way, which is not a veto - see
    // AdminOidcOptions.EmailClaimsAreTrusted for why demanding the claim is not an option.
    [TestCase(null, true)]
    public void UserInfo_EmailVerified_IsWhatTheAllowlistDecidesAnEmailEntryOn(string? emailVerified, bool admitted)
    {
        using var factory = CreateFactory();

        var handlerOptions = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AdminAuthentication.OidcScheme);

        var json = emailVerified is null
            ? """{"sub":"8ab0c1e2-user","email":"admin@example.com"}"""
            : $$"""{"sub":"8ab0c1e2-user","email":"admin@example.com","email_verified":{{emailVerified}}}""";

        using var userInfo = JsonDocument.Parse(json);
        var identity = new ClaimsIdentity(AdminAuthentication.OidcScheme);

        foreach (var action in handlerOptions.ClaimActions)
        {
            action.Run(userInfo.RootElement, identity, Authority);
        }

        // The allowlist the host itself built from the environment, whose one entry is that address.
        var allowlist = factory.Services.GetRequiredService<AdminOidcOptions>();

        Assert.That(allowlist.IsAdmin(new ClaimsPrincipal(identity)), Is.EqualTo(admitted));
    }

    /// <summary>
    /// Where the browser is sent <em>after</em> the handshake finishes. It travels inside the
    /// protected <c>state</c> parameter, so the assertion has to unprotect it with the handler's own
    /// data format - reading the redirect's query string would only ever show the identity
    /// provider's URL and would pass whatever the guard did.
    /// </summary>
    private static async Task<string?> RedirectUriAfterLogin(WebApplicationFactory<Program> factory, string queryString)
    {
        var response = await CreateClient(factory).GetAsync("/api/admin/login" + queryString);
        var state = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();

        // The handler prefixes the protected blob with its properties key; Uri canonicalisation may
        // or may not have left the separator intact by the time it gets here, so tolerate both.
        var prefix = OpenIdConnectDefaults.AuthenticationPropertiesKey + "=";
        var protectedProperties = state.StartsWith(prefix, StringComparison.Ordinal) ? state[prefix.Length..] : state;

        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AdminAuthentication.OidcScheme);

        var properties = options.StateDataFormat.Unprotect(protectedProperties);

        // Null would mean the assertions below silently pass on an empty comparison.
        Assert.That(properties, Is.Not.Null);

        return properties!.RedirectUri;
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(versionHandler);
                    services.AddSingleton<IConfigCatalog>(AdminSessionControllerTests.Catalog());

                    // Deliberately no AdminOidcOptions override: the point of this fixture is that
                    // the options, the scheme registration and the policy all come from the
                    // environment, exactly as they do in production.
                    //
                    // Supplying Configuration makes the post-configure step build a static
                    // configuration manager, so the handler never fetches a discovery document.
                    services.Configure<OpenIdConnectOptions>(AdminAuthentication.OidcScheme, options =>
                        options.Configuration = new OpenIdConnectConfiguration
                        {
                            Issuer = Authority,
                            AuthorizationEndpoint = AuthorizationEndpoint,
                            TokenEndpoint = Authority + "/protocol/openid-connect/token"
                        });
                });
            });
    }
}
