using System.Net;
using System.Net.Http;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Profiles;

/// <summary>
/// The 500 that <c>SwappableConfigCatalog</c> documented and left for the first caller of
/// <c>Swap</c>, which the admin configuration editor now is.
/// <para>
/// The window is narrow and racy in production - a request admitted by
/// <c>UnknownProfileMiddleware</c> while its profile existed, resolving its services after a save
/// removed it - so it is reproduced here with a catalog that admits the profile and then refuses to
/// hand it over, which is exactly the state that race leaves the request in. Anything that resolves
/// a profile does so from dependency injection, well outside MVC's exception filters, so this has to
/// be caught in the pipeline or not at all.
/// </para>
/// </summary>
[TestFixture]
public class ProfileNotFoundMiddlewareTests
{
    [Test]
    public async Task ProfileRemovedAfterTheRequestWasAdmitted_Is404NotAServerError()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var vanished = await client.GetAsync("/api/Config?profile=vanishing");
        var stillWorking = await client.GetAsync("/api/Config");

        Assert.Multiple(() =>
        {
            Assert.That(vanished.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(stillWorking.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "catching the exception must not swallow the requests that are fine");
        });
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
                    services.AddSingleton(RacingCatalog());
                });
            });
    }

    /// <summary>
    /// A catalog caught mid-swap: it still admits 'vanishing' to <c>TryGet</c>, the way the outgoing
    /// catalog did when the request arrived, but no longer hands the settings over.
    /// </summary>
    private static IConfigCatalog RacingCatalog()
    {
        IServerSettings? settings = new ServerSettings
        {
            GeneralSettingsImpl = new GeneralSettings(),
            AccountsImpl = new List<ServerAccountSettings>
            {
                new() { ImmichServerUrl = "http://mock-immich-server.com", ApiKey = "test-api-key" }
            }
        };

        var catalog = new Mock<IConfigCatalog>();
        catalog.Setup(c => c.Default).Returns(settings);
        // Empty, so the startup version check only walks the default configuration.
        catalog.Setup(c => c.ProfileNames).Returns([]);
        catalog.Setup(c => c.TryGet(It.IsAny<string?>(), out settings)).Returns(true);
        catalog.Setup(c => c.Get(It.Is<string?>(name => name != "vanishing"))).Returns(settings);
        catalog.Setup(c => c.Get("vanishing"))
            .Throws(new ProfileNotFoundException("No configuration profile named 'vanishing' is configured."));

        return catalog.Object;
    }
}
