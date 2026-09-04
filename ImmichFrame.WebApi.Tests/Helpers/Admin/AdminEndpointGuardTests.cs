using System.Net;
using System.Net.Http;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Tests.Controllers;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Admin;

/// <summary>
/// The startup tripwire. Everything under <c>/api/admin</c> is exempt from the frame's shared-secret
/// scheme, so an endpoint there that forgets its authorization answers to anyone - and task 003 puts
/// the configuration file, Immich API keys included, behind exactly that prefix.
/// </summary>
[TestFixture]
public class AdminEndpointGuardTests
{
    [Test]
    public void Validate_AdminEndpointWithNeitherAuthorizationNorAnonymous_Throws()
    {
        var offender = AdminEndpoint("api/admin/config");

        Assert.That(() => AdminEndpointGuard.Validate([offender]),
            Throws.InvalidOperationException.With.Message.Contains("api/admin/config"));
    }

    /// <summary>
    /// The subtle half of the rule. A bare <c>[Authorize]</c> binds to the default policy against
    /// the default scheme, which is still <c>ImmichFrameScheme</c> - so it admits any holder of the
    /// frame's <c>AuthenticationSecret</c> (the secret on every kiosk display), and admits everyone
    /// when no secret is configured. It looks protected and is not.
    /// </summary>
    [Test]
    public void Validate_BareAuthorize_IsTreatedAsUnguarded()
    {
        var bare = AdminEndpoint("api/admin/config", new AuthorizeAttribute());

        Assert.That(() => AdminEndpointGuard.Validate([bare]),
            Throws.InvalidOperationException.With.Message.Contains("api/admin/config"));
    }

    [Test]
    public void Validate_AuthorizationNamingSomeOtherPolicyOrScheme_IsTreatedAsUnguarded()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => AdminEndpointGuard.Validate(
                [AdminEndpoint("api/admin/config", new AuthorizeAttribute { Policy = "AllowAnonymous" })]),
                Throws.InvalidOperationException);

            Assert.That(() => AdminEndpointGuard.Validate(
                [AdminEndpoint("api/admin/config", new AuthorizeAttribute { AuthenticationSchemes = "ImmichFrameScheme" })]),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void Validate_AuthorizationNamingTheAdminPolicy_IsAccepted()
    {
        var byPolicy = AdminEndpoint("api/admin/config",
            new AuthorizeAttribute { Policy = AdminAuthentication.AdminOnlyPolicy });

        Assert.That(() => AdminEndpointGuard.Validate([byPolicy]), Throws.Nothing);
    }

    /// <summary>
    /// Scheme-only authorization authenticates without consulting the allowlist, so it admits every
    /// identity the provider will authenticate - a stranger, on a provider with open registration.
    /// Correct for sign-out and catastrophic for anything that reads or writes, so it is refused
    /// unless the endpoint says out loud that it means it.
    /// </summary>
    [Test]
    public void Validate_CookieSchemeWithoutTheAllowlistWaiver_IsTreatedAsUnguarded()
    {
        var schemeOnly = AdminEndpoint("api/admin/config",
            new AuthorizeAttribute { AuthenticationSchemes = AdminAuthentication.CookieScheme });

        Assert.That(() => AdminEndpointGuard.Validate([schemeOnly]),
            Throws.InvalidOperationException.With.Message.Contains("api/admin/config"));
    }

    [Test]
    public void Validate_CookieSchemeWithTheAllowlistWaiverDeclared_IsAccepted()
    {
        var declared = AdminEndpoint("api/admin/logout",
            new AuthorizeAttribute { AuthenticationSchemes = AdminAuthentication.CookieScheme },
            new AdminEndpointAttribute { AllowlistNotRequired = true });
        // A scheme list is comma separated when more than one is named.
        var declaredWithList = AdminEndpoint("api/admin/other",
            new AuthorizeAttribute { AuthenticationSchemes = $"SomethingElse,{AdminAuthentication.CookieScheme}" },
            new AdminEndpointAttribute { AllowlistNotRequired = true });

        Assert.That(() => AdminEndpointGuard.Validate([declared, declaredWithList]), Throws.Nothing);
    }

    [Test]
    public void Validate_AllowlistWaiverDoesNotExcuseAbsentOrForeignAuthorization()
    {
        // The waiver drops the allowlist, not authorization: a bare [Authorize] binds to the frame's
        // scheme, and the waiver must not launder that into an admin guard.
        var flaggedButBare = AdminEndpoint("api/admin/config",
            new AuthorizeAttribute(),
            new AdminEndpointAttribute { AllowlistNotRequired = true });
        var flaggedButNaked = AdminEndpoint("api/admin/other",
            new AdminEndpointAttribute { AllowlistNotRequired = true });

        Assert.Multiple(() =>
        {
            Assert.That(() => AdminEndpointGuard.Validate([flaggedButBare]), Throws.InvalidOperationException);
            Assert.That(() => AdminEndpointGuard.Validate([flaggedButNaked]), Throws.InvalidOperationException);
        });
    }

    /// <summary>
    /// Class-level and action-level attributes both land in the endpoint's metadata, and either may
    /// be the one that names the admin policy, so every <c>IAuthorizeData</c> has to be considered.
    /// </summary>
    [Test]
    public void Validate_AdminAuthorizationAlongsideABareOne_IsAccepted()
    {
        var endpoint = AdminEndpoint("api/admin/config",
            new AuthorizeAttribute(),
            new AuthorizeAttribute { Policy = AdminAuthentication.AdminOnlyPolicy });

        Assert.That(() => AdminEndpointGuard.Validate([endpoint]), Throws.Nothing);
    }

    [Test]
    public void Validate_AdminEndpointDeclaredAnonymous_IsAccepted()
    {
        var declared = AdminEndpoint("api/admin/login", new AdminEndpointAttribute { Anonymous = true });

        Assert.That(() => AdminEndpointGuard.Validate([declared]), Throws.Nothing);
    }

    [Test]
    public void Validate_VisibleWhenUnconfiguredAloneDoesNotExcuseMissingAuthorization()
    {
        // The two flags are independent: being answerable on an unconfigured install says nothing
        // about who may call it once the surface is on.
        var visibleOnly = AdminEndpoint("api/admin/config", new AdminEndpointAttribute { VisibleWhenUnconfigured = true });

        Assert.That(() => AdminEndpointGuard.Validate([visibleOnly]), Throws.InvalidOperationException);
    }

    [Test]
    public void Validate_UnauthorizedEndpointsOutsideTheAdminPrefix_AreNotItsBusiness()
    {
        // /api/Config really is anonymous, and the near-misses must not be mistaken for admin routes.
        Assert.That(() => AdminEndpointGuard.Validate(
        [
            AdminEndpoint("api/Config"),
            AdminEndpoint("api/adminfoo"),
            AdminEndpoint("admins"),
            AdminEndpoint("api/Asset/{id}/Image")
        ]), Throws.Nothing);
    }

    [Test]
    public void Validate_NamesEveryOffender()
    {
        var message = Assert.Throws<InvalidOperationException>(() => AdminEndpointGuard.Validate(
        [
            AdminEndpoint("api/admin/config"),
            AdminEndpoint("api/admin/albums"),
            AdminEndpoint("api/admin/ok", new AuthorizeAttribute { Policy = AdminAuthentication.AdminOnlyPolicy })
        ]))!.Message;

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("api/admin/config"));
            Assert.That(message, Does.Contain("api/admin/albums"));
            Assert.That(message, Does.Not.Contain("api/admin/ok"));
        });
    }

    /// <summary>
    /// The guard through the real composition root: a host whose admin surface contains an endpoint
    /// with no authorization must refuse to start rather than serve it. This is why
    /// <see cref="ForgottenAdminEndpoint"/> is invisible to controller discovery - it can only ever
    /// reach a host that expects this.
    /// </summary>
    [Test]
    public void Host_RefusesToStartWhenAnAdminEndpointHasNoAuthorization()
    {
        using var factory = HostWith(typeof(ForgottenAdminEndpoint));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.That(exception!.Message, Does.Contain(ForgottenAdminEndpoint.Route));
    }

    /// <summary>
    /// The regression that matters most: this endpoint boots and serves happily without the guard,
    /// handing the configuration editor to any holder of the frame secret - or, on an installation
    /// with no secret set, to anyone at all.
    /// </summary>
    [Test]
    public void Host_RefusesToStartWhenAnAdminEndpointCarriesOnlyABareAuthorize()
    {
        using var factory = HostWith(typeof(BareAuthorizeAdminEndpoint));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.That(exception!.Message, Does.Contain(BareAuthorizeAdminEndpoint.Route));
    }

    /// <summary>
    /// Copying sign-out - the only in-repo example of admin authorization that is not the policy -
    /// onto a config endpoint is the mistake this refuses. Without the guard the host boots and the
    /// endpoint answers to any identity the provider authenticates.
    /// </summary>
    [Test]
    public void Host_RefusesToStartWhenAnAdminEndpointHasCookieSchemeButNoAllowlistWaiver()
    {
        using var factory = HostWith(typeof(SchemeOnlyAdminEndpoint));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.That(exception!.Message, Does.Contain(SchemeOnlyAdminEndpoint.Route));
    }

    [Test]
    public void Host_RefusesToStartWhenTheAllowlistWaiverIsPairedWithABareAuthorize()
    {
        using var factory = HostWith(typeof(FlaggedButBarelyAuthorizedAdminEndpoint));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.That(exception!.Message, Does.Contain(FlaggedButBarelyAuthorizedAdminEndpoint.Route));
    }

    /// <summary>
    /// And the real sign-out endpoint, which declares the waiver, must not trip any of this - the
    /// whole application boots in every other fixture, but assert it here where the rule lives.
    /// </summary>
    [Test]
    public async Task Host_StartsWithTheRealLogoutEndpointDeclaringTheWaiver()
    {
        using var factory = AdminSessionControllerTests.CreateFactory(new AdminOidcOptions());

        var response = await factory.CreateClient().GetAsync("/api/admin/session");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public void Host_RefusesToStartWhenAnAdminEndpointNamesSomeOtherPolicy()
    {
        using var factory = HostWith(typeof(ForeignPolicyAdminEndpoint));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.That(exception!.Message, Does.Contain(ForeignPolicyAdminEndpoint.Route));
    }

    /// <summary>
    /// And the same host started without the careless endpoint, so the test above is failing on the
    /// endpoint rather than on anything else about the fixture.
    /// </summary>
    [Test]
    public async Task Host_StartsNormallyWithoutIt()
    {
        using var factory = AdminSessionControllerTests.CreateFactory(new AdminOidcOptions());

        var response = await factory.CreateClient().GetAsync("/api/admin/session");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private static WebApplicationFactory<Program> HostWith(Type hiddenEndpoint)
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(versionHandler);
                    services.AddSingleton<IConfigCatalog>(AdminSessionControllerTests.Catalog());

                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                            manager.FeatureProviders.Add(new HiddenAdminEndpointProvider(hiddenEndpoint)));
                });
            });
    }

    private static RouteEndpoint AdminEndpoint(string pattern, params object[] metadata) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, new EndpointMetadataCollection(metadata), pattern);
}
