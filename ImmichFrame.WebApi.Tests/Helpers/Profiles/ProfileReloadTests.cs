using System.Net;
using System.Net.Http;
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

/// <summary>
/// Replacing the configuration of a running ImmichFrame: what the catalog forwards after a swap,
/// and what the registry does with the services it had already built from the outgoing one.
/// </summary>
[TestFixture]
public class ProfileReloadTests
{
    // Deliberately none of these is GeneralSettings.Interval's own default of 45: an assertion on
    // 45 would still pass if the settings under test were never consulted at all.
    private const int BeforeInterval = 5;
    private const int AfterInterval = 33;
    private const int KitchenInterval = 11;

    private ServiceProvider _root = null!;
    private SwappableConfigCatalog _catalog = null!;
    private ProfileRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        // Only what the per-profile object graph asks for by type. Nothing in it does network I/O
        // at construction time, so the swap can be exercised without a host.
        _root = new ServiceCollection().AddLogging().AddHttpClient().BuildServiceProvider();

        // The registry is handed to the catalog as a factory, so the two can point at each other
        // without a construction cycle - the same reason Program.cs passes a resolver rather than
        // an instance.
        _catalog = new SwappableConfigCatalog(() => Catalog(BeforeInterval, withKitchen: true), () => _registry);
        _registry = new ProfileRegistry(_catalog, _root);
    }

    [TearDown]
    public void TearDown()
    {
        _root.Dispose();
    }

    /// <summary>
    /// The registration in <c>Program.cs</c> hands the catalog a factory rather than a loaded
    /// catalog, so that a host whose catalog is overridden in tests never reads the settings file.
    /// </summary>
    [Test]
    public void CatalogIsNotSeededUntilSomethingAsksForIt()
    {
        var seeded = 0;
        var catalog = new SwappableConfigCatalog(() =>
        {
            seeded++;
            return Catalog(BeforeInterval, withKitchen: true);
        }, () => _registry);

        Assert.That(seeded, Is.Zero);

        _ = catalog.Default;
        _ = catalog.ProfileNames;

        Assert.That(seeded, Is.EqualTo(1), "the seeded catalog should be built once and then reused");
    }

    [Test]
    public void SwappedCatalogIsWhatConsumersOfTheInterfaceSee()
    {
        Assert.That(_catalog.Default.GeneralSettings.Interval, Is.EqualTo(BeforeInterval));

        _catalog.Swap(Catalog(AfterInterval, withKitchen: true));

        Assert.Multiple(() =>
        {
            Assert.That(_catalog.Default.GeneralSettings.Interval, Is.EqualTo(AfterInterval));
            Assert.That(_catalog.TryGet("kitchen", out var kitchen), Is.True);
            Assert.That(kitchen!.GeneralSettings.Interval, Is.EqualTo(KitchenInterval));
        });
    }

    /// <summary>
    /// The reason swapping and invalidating are one operation: a cached <see cref="ProfileServices"/>
    /// captures its settings at construction, so leaving it in place would go on serving the
    /// configuration that was just replaced.
    /// </summary>
    [Test]
    public void ProfileServicesAreRebuiltFromTheNewCatalog()
    {
        var before = _registry.For(null);

        _catalog.Swap(Catalog(AfterInterval, withKitchen: true));

        var after = _registry.For(null);

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.Not.SameAs(before));
            Assert.That(after.GeneralSettings.Interval, Is.EqualTo(AfterInterval));
            Assert.That(before.GeneralSettings.Interval, Is.EqualTo(BeforeInterval),
                "a graph already handed out keeps the settings it was built with");
        });
    }

    [Test]
    public void ProfileTheNewCatalogDoesNotDeclareStopsResolving()
    {
        Assert.That(_registry.For("kitchen").GeneralSettings.Interval, Is.EqualTo(KitchenInterval));

        _catalog.Swap(Catalog(AfterInterval, withKitchen: false));

        Assert.That(() => _registry.For("kitchen"), Throws.TypeOf<ProfileNotFoundException>());
    }

    /// <summary>
    /// End to end over the real host: the only thing that proves the scoped registrations in
    /// <c>Program.cs</c> resolve through the swapped catalog rather than through one captured at
    /// startup, and that a profile the swap dropped is turned away by
    /// <see cref="UnknownProfileMiddleware"/> rather than failing further down.
    /// </summary>
    [Test]
    public async Task SwapChangesTheConfigurationRequestsAreServed()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var kitchenBefore = await GetInterval(client, "/api/Config?profile=kitchen");
        var byDefaultBefore = await GetInterval(client, "/api/Config");

        factory.Services.GetRequiredService<SwappableConfigCatalog>().Swap(Catalog(AfterInterval, withKitchen: false));

        var byDefaultAfter = await GetInterval(client, "/api/Config");
        var kitchenAfter = await client.GetAsync("/api/Config?profile=kitchen");

        Assert.Multiple(() =>
        {
            Assert.That(kitchenBefore, Is.EqualTo(KitchenInterval));
            Assert.That(byDefaultBefore, Is.EqualTo(BeforeInterval));
            Assert.That(byDefaultAfter, Is.EqualTo(AfterInterval));
            Assert.That(kitchenAfter.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    /// <summary>
    /// A swap landing between two resolutions inside one request must not split that request across
    /// two configurations: the authentication handler resolves <see cref="IServerSettings"/> while
    /// the controller resolves <see cref="IImmichFrameLogic"/> later on, so the profile's graph is
    /// pinned to the request scope and everything reads from that one instance.
    /// </summary>
    [Test]
    public void OneRequestScopeKeepsOneGraphAcrossASwap()
    {
        using var factory = CreateFactory();
        _ = factory.CreateClient();

        var before = factory.Services.GetRequiredService<ProfileRegistry>().For(null);

        // No HttpContext in a bare scope, so this resolves the default profile - the path a request
        // that names no profile takes.
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IServerSettings>();

        factory.Services.GetRequiredService<SwappableConfigCatalog>().Swap(Catalog(AfterInterval, withKitchen: true));

        var logic = scope.ServiceProvider.GetRequiredService<IImmichFrameLogic>();

        Assert.Multiple(() =>
        {
            Assert.That(settings.GeneralSettings.Interval, Is.EqualTo(BeforeInterval));
            Assert.That(settings, Is.SameAs(before.Settings));
            Assert.That(logic, Is.SameAs(before.Logic),
                "a service resolved after the swap must come from the graph the scope started with");
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

                    // Seeding the swappable catalog rather than replacing IConfigCatalog outright:
                    // the forwarding registration has to stay in place for a swap to reach the host.
                    services.AddSingleton(srv => new SwappableConfigCatalog(
                        () => Catalog(BeforeInterval, withKitchen: true),
                        srv.GetRequiredService<ProfileRegistry>));
                });
            });
    }

    private static async Task<int?> GetInterval(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        response.EnsureSuccessStatusCode();

        return (int?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["interval"];
    }

    private static ConfigCatalog Catalog(int defaultInterval, bool withKitchen) =>
        new(Settings(defaultInterval),
            withKitchen ? [new("kitchen", Settings(KitchenInterval))] : []);

    private static ServerSettings Settings(int interval) => new()
    {
        GeneralSettingsImpl = new GeneralSettings
        {
            Interval = interval
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
