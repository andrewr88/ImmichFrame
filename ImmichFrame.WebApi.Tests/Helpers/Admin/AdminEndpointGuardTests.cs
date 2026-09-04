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

    [Test]
    public void Validate_AdminEndpointWithAuthorization_IsAccepted()
    {
        var authorized = AdminEndpoint("api/admin/config", new AuthorizeAttribute());

        Assert.That(() => AdminEndpointGuard.Validate([authorized]), Throws.Nothing);
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
            AdminEndpoint("api/admin/ok", new AuthorizeAttribute())
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
        using var factory = ForgetfulFactory();

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.That(exception!.Message, Does.Contain(ForgottenAdminEndpoint.Route));
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

    private static WebApplicationFactory<Program> ForgetfulFactory()
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
                            manager.FeatureProviders.Add(new ForgottenAdminEndpointProvider()));
                });
            });
    }

    private static RouteEndpoint AdminEndpoint(string pattern, params object[] metadata) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, new EndpointMetadataCollection(metadata), pattern);
}
