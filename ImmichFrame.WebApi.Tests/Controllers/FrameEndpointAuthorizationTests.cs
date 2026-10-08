using System.Net;
using System.Net.Http;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers;

/// <summary>
/// The frame API's counterpart to what <see cref="AdminEndpointGuard"/> does for the admin surface:
/// once an <c>AuthenticationSecret</c> is configured, every endpoint a frame calls demands it.
/// <para>
/// Nothing else would notice that stop being true. There is no fallback authorization policy, and
/// <c>ImmichFrameAuthenticationHandler</c> lets a request through to any endpoint that carries no
/// <c>[Authorize]</c> - so a frame controller that loses its attribute in a refactor, or is added
/// without one, answers everybody on the network, secret or none, with every other test still green.
/// The sweep catches the endpoint nobody thought to write a request for; the requests pin the
/// pipeline actually refusing the ones a frame uses most.
/// </para>
/// </summary>
[TestFixture]
public class FrameEndpointAuthorizationTests
{
    private const string Secret = "frame-secret";

    /// <summary>
    /// The frame endpoints that answer without the secret, on purpose: the web client loads its
    /// configuration before it renders anything, the prompt for the secret included, and what it is
    /// sent holds nothing secret - <see cref="ConfigControllerTests"/> pins that. Nor does the version.
    /// </summary>
    private static readonly string[] Anonymous = ["api/Config", "api/Config/Version"];

    /// <summary>
    /// <c>MapFallbackToFile</c>'s route, which hands the SPA shell to anybody: the page has to load
    /// before it can ask for a secret. Static files are served by middleware rather than by an
    /// endpoint, so they never appear in the sweep at all.
    /// </summary>
    private const string SpaFallback = "{*path:nonfile}";

    [Test]
    public void EveryFrameEndpoint_ExceptTheConfiguration_RequiresAuthorization()
    {
        // Arrange
        using var factory = CreateFactory();

        // Act
        var frameEndpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => !AdminAuthentication.IsAdminEndpoint(endpoint))
            .Where(endpoint => endpoint.RoutePattern.RawText != SpaFallback)
            .ToList();

        // The handler's own test, read the same way: an endpoint is open when it carries no
        // authorization or carries [AllowAnonymous] alongside it.
        var open = frameEndpoints
            .Where(endpoint => !Anonymous.Contains(endpoint.RoutePattern.RawText, StringComparer.OrdinalIgnoreCase))
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is null ||
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => $"{endpoint.RoutePattern.RawText} ({endpoint.DisplayName})");

        // Assert
        Assert.Multiple(() =>
        {
            // So that a filter above that let nothing through cannot pass the sweep by sweeping nothing.
            Assert.That(frameEndpoints.Select(endpoint => endpoint.RoutePattern.RawText),
                Is.SupersetOf(new[] { "api/Asset", "api/Asset/{id}/Asset", "api/Asset/RandomImageAndInfo", "api/Calendar", "api/Weather" }));

            Assert.That(open, Is.Empty, "frame endpoints that answer without the AuthenticationSecret");
        });
    }

    [TestCase("/api/Asset")]
    [TestCase("/api/Asset/RandomImageAndInfo")]
    [TestCase("/api/Weather")]
    public async Task FrameEndpoint_RequestedWithoutTheSecret_IsUnauthorized(string url)
    {
        // Arrange
        using var factory = CreateFactory();

        // Act
        var response = await factory.CreateClient().GetAsync(url);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(versionHandler);
                    services.AddSingleton<IConfigCatalog>(new ConfigCatalog(new ServerSettings
                    {
                        GeneralSettingsImpl = new GeneralSettings { AuthenticationSecret = Secret },
                        AccountsImpl = new List<ServerAccountSettings>
                        {
                            new() { ImmichServerUrl = "http://mock-immich-server.com", ApiKey = "test-api-key" }
                        }
                    }));
                });
            });
    }
}
