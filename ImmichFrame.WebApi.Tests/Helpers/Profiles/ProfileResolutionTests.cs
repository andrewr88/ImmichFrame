using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Helpers.Profiles;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Profiles;

[TestFixture]
public class ProfileResolutionTests
{
    // Deliberately none of these is GeneralSettings.Interval's own default of 45: an assertion
    // on 45 would still pass if the profile's settings were never consulted at all.
    private const int DefaultInterval = 5;
    private const int KitchenInterval = 11;
    private const int BedroomInterval = 22;

    private const string DefaultSecret = "default-secret";
    private const string KitchenSecret = "kitchen-secret";

    private ServiceProvider _root = null!;
    private ProfileRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        // Only what the per-profile object graph asks for by type. Nothing in it does network I/O
        // at construction time, so the registry can be exercised without a host.
        _root = new ServiceCollection().AddLogging().AddHttpClient().BuildServiceProvider();

        _registry = new ProfileRegistry(Catalog(), _root);
    }

    [TearDown]
    public void TearDown()
    {
        _root.Dispose();
    }

    [Test]
    public void ProfileIsBuiltOnceAndReused()
    {
        var first = _registry.For("kitchen");
        var second = _registry.For("kitchen");

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void EachProfileGetsItsOwnServices()
    {
        var kitchen = _registry.For("kitchen");
        var bedroom = _registry.For("bedroom");

        Assert.Multiple(() =>
        {
            Assert.That(kitchen, Is.Not.SameAs(bedroom));
            // Reference equality on Logic only - it says the two profiles were handed separate
            // delegates, not that nothing underneath them is shared.
            Assert.That(kitchen.Logic, Is.Not.SameAs(bedroom.Logic));
        });
    }

    [Test]
    public void NullEmptyAndDefaultNameAllResolveToTheDefaultProfile()
    {
        var byNull = _registry.For(null);

        Assert.Multiple(() =>
        {
            Assert.That(_registry.For(string.Empty), Is.SameAs(byNull));
            Assert.That(_registry.For(ConfigCatalog.DefaultProfileName), Is.SameAs(byNull));
        });
    }

    [Test]
    public void UnknownProfileThrows()
    {
        Assert.That(() => _registry.For("bogus"), Throws.TypeOf<ProfileNotFoundException>());
    }

    [Test]
    public void ProfileServicesCarryThatProfilesSettings()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_registry.For(null).GeneralSettings.Interval, Is.EqualTo(DefaultInterval));
            Assert.That(_registry.For("kitchen").GeneralSettings.Interval, Is.EqualTo(KitchenInterval));
            Assert.That(_registry.For("bedroom").GeneralSettings.Interval, Is.EqualTo(BedroomInterval));
        });
    }

    /// <summary>
    /// End to end over the real host: the only thing that proves the scoped registrations in
    /// <c>Program.cs</c> hand each request the profile it asked for.
    /// </summary>
    [Test]
    public async Task RequestedProfileDecidesTheSettingsAnEndpointSees()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // Default first, deliberately: were the settings singletons again, the default would be
        // cached here and the kitchen request below would be served the default's interval.
        var byDefault = await GetInterval(client, "/api/Config");
        var byProfile = await GetInterval(client, "/api/Config?profile=kitchen");
        var unknown = await client.GetAsync("/api/Config?profile=bogus");

        Assert.Multiple(() =>
        {
            Assert.That(byDefault, Is.EqualTo(DefaultInterval));
            Assert.That(byProfile, Is.EqualTo(KitchenInterval));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    /// <summary>
    /// The authentication secret is per profile, so a token minted for one profile must not open
    /// another. <c>/api/Config</c> cannot show this - it is deliberately unauthenticated -
    /// so probe <c>/api/Calendar</c>, which carries <c>[Authorize]</c> and, with no webcalendars
    /// configured, answers without any network I/O.
    /// </summary>
    [Test]
    public async Task ProfileSecretDoesNotAuthenticateAnotherProfile()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var kitchenWithOwnSecret = await GetCalendarStatus(client, "/api/Calendar?profile=kitchen", KitchenSecret);
        var kitchenWithDefaultSecret = await GetCalendarStatus(client, "/api/Calendar?profile=kitchen", DefaultSecret);
        var defaultWithOwnSecret = await GetCalendarStatus(client, "/api/Calendar", DefaultSecret);
        var defaultWithKitchenSecret = await GetCalendarStatus(client, "/api/Calendar", KitchenSecret);

        Assert.Multiple(() =>
        {
            Assert.That(kitchenWithOwnSecret, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(kitchenWithDefaultSecret, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(defaultWithOwnSecret, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(defaultWithKitchenSecret, Is.EqualTo(HttpStatusCode.Unauthorized));
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
                    // Without this the startup version gate calls Environment.Exit and takes the
                    // test host down with it.
                    services.UseMockHandler(versionHandler);

                    // Seeding the catalog rather than IServerSettings directly: the per-profile
                    // services are built from the catalog, so a settings override never reaches them.
                    services.AddSingleton<IConfigCatalog>(Catalog());
                });
            });
    }

    private static async Task<int?> GetInterval(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        response.EnsureSuccessStatusCode();

        return (int?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["interval"];
    }

    private static async Task<HttpStatusCode> GetCalendarStatus(HttpClient client, string url, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var response = await client.SendAsync(request);

        return response.StatusCode;
    }

    private static ConfigCatalog Catalog() =>
        new(Settings(DefaultInterval, DefaultSecret),
        [
            new("kitchen", Settings(KitchenInterval, KitchenSecret)),
            new("bedroom", Settings(BedroomInterval))
        ]);

    private static ServerSettings Settings(int interval, string? authenticationSecret = null) => new()
    {
        GeneralSettingsImpl = new GeneralSettings
        {
            Interval = interval,
            AuthenticationSecret = authenticationSecret,
        },
        AccountsImpl = new List<ServerAccountSettings>
        {
            new()
            {
                ImmichServerUrl = "http://mock-immich-server.com",
                ApiKey = "test-api-key",
            }
        }
    };
}
