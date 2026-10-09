using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Api;
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
using Moq.Protected;
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

    /// <summary>
    /// Which account an asset is fetched from is looked up among the assets the accounts have handed
    /// out, by a tracker that asks every account it has recorded. Shared between profiles, it would
    /// serve a request on one profile out of an account configured for another - which
    /// <see cref="EachProfileGetsItsOwnServices"/> cannot show, as two profiles' logic can be separate
    /// objects with one tracker underneath. Every account here is on the one server, which describes the
    /// asset to any account that asks, so only which accounts get asked can keep it from a profile.
    /// </summary>
    [Test]
    public async Task AssetOneProfileHandedOutIsNotFoundThroughAnother()
    {
        var asset = new AssetResponseDto
        {
            Id = Guid.NewGuid(),
            Type = AssetTypeEnum.IMAGE,
            Visibility = AssetVisibility.Timeline,
            OriginalPath = "/photos/kitchen.jpg",
            OriginalFileName = "kitchen.jpg",
            Checksum = "kitchen-checksum",
        };
        using var root = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .UseMockHandler(ImmichHolding(asset))
            .BuildServiceProvider();
        var registry = new ProfileRegistry(Catalog(), root);
        var kitchen = registry.For("kitchen").Logic;
        var bedroom = registry.For("bedroom").Logic;

        // Handing the asset out is what records which of the kitchen's accounts holds it.
        var handedOut = await kitchen.GetNextAsset();
        var foundByKitchen = await kitchen.GetAssetInfoById(asset.Id);

        Assert.Multiple(() =>
        {
            // Without these two, the refusal below could be nothing more than an asset that was never
            // recorded, or a server that never answers for it.
            Assert.That(handedOut?.Id, Is.EqualTo(asset.Id));
            Assert.That(foundByKitchen.Id, Is.EqualTo(asset.Id), "the profile that handed the asset out finds it again");
            Assert.That(async () => await bedroom.GetAssetInfoById(asset.Id), Throws.InstanceOf<AssetNotFoundException>(),
                "none of the bedroom's accounts handed this asset out, so none of them may be asked for it");
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

    /// <summary>
    /// A name of nothing but whitespace is the default profile to the catalog, and
    /// <see cref="UnknownProfileMiddleware"/> lets <c>?profile=%20</c> through as one. The cache in front
    /// of the catalog has to agree: keyed as itself, each such spelling would build and keep a whole
    /// second graph - pools, API caches, HTTP clients - for the default profile.
    /// </summary>
    [Test]
    public void WhitespaceNameResolvesToTheDefaultProfile()
    {
        var byNull = _registry.For(null);

        Assert.Multiple(() =>
        {
            Assert.That(_registry.For(" "), Is.SameAs(byNull));
            Assert.That(_registry.For("\t "), Is.SameAs(byNull));
        });
    }

    /// <summary>
    /// The catalog matches profile names case-insensitively, so the cache in front of it has to as
    /// well. Keyed by the name as the client happened to spell it, <c>?profile=Kitchen</c> and
    /// <c>?profile=kitchen</c> would each build and keep a whole graph - pools, API caches, HTTP
    /// clients - for what is one profile, and so would every casing of the default's own name.
    /// </summary>
    [Test]
    public void NamesDifferingOnlyInCaseResolveToTheSameServices()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_registry.For("Kitchen"), Is.SameAs(_registry.For("kitchen")));
            Assert.That(_registry.For("DEFAULT"), Is.SameAs(_registry.For(null)));
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
                    // The startup version check only logs what it hears, and nothing waits for it -
                    // but without this its request would go out to the network, to a server that
                    // does not exist.
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

    /// <summary>
    /// An Immich server holding <paramref name="asset"/> and nothing else: it counts it, draws it at
    /// random, and describes it to whichever account asks. Answered from the generated DTOs, as
    /// <c>AssetControllerTests</c> does, so a change to the OpenAPI schema fails at compile time.
    /// </summary>
    private static Mock<HttpMessageHandler> ImmichHolding(AssetResponseDto asset)
    {
        var immich = new Mock<HttpMessageHandler>().WithAssetStatistics(1);

        Answer(immich, "/search/random", JsonSerializer.Serialize(new[] { asset }));
        Answer(immich, $"/assets/{asset.Id}", JsonSerializer.Serialize(asset));

        return immich;
    }

    // Answers the requests to one of Immich's endpoints, named by the end of its path, with a JSON body.
    private static void Answer(Mock<HttpMessageHandler> immich, string endpoint, string json) =>
        immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith(endpoint)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

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
