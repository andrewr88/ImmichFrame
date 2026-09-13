using System.Diagnostics.CodeAnalysis;
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

    // What a graph answers GetTotalAssets with. Deliberately not zero: an assertion on zero would
    // still pass against a graph that was never consulted at all.
    private const long TotalImages = 7;

    private ServiceProvider _root = null!;
    private SwappableConfigCatalog _catalog = null!;
    private ProfileRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        // Only what the per-profile object graph asks for by type. Nothing in it does network I/O
        // at construction time, so the swap can be exercised without a host. The handler answers the
        // one call GetTotalAssets makes, so the lease tests can ask a graph that outlived a swap for
        // a real number rather than for the absence of an exception.
        _root = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .UseMockHandler(new Mock<HttpMessageHandler>().WithAssetStatistics(TotalImages))
            .BuildServiceProvider();

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
    /// The reported production failure: saving in the configuration editor swapped the catalog while
    /// a request was in flight, and the graph that request was holding was torn down underneath it -
    /// its next call came back as an <see cref="ObjectDisposedException"/> from the account's API
    /// cache. A lease is what now bounds the gap between a graph stopping being handed out and the
    /// graph being destroyed.
    /// </summary>
    [Test]
    public async Task SwapDoesNotTearDownALeasedGraph()
    {
        var leased = _registry.Lease(null);

        _catalog.Swap(Catalog(AfterInterval, withKitchen: true));

        // Reaches the account's API cache before it reaches the network, so a graph the swap took
        // down answers here with ObjectDisposedException rather than with a count.
        Assert.That(await leased.Logic.GetTotalAssets(), Is.EqualTo(TotalImages));
    }

    /// <summary>
    /// The other half of that bargain: deferring the teardown must not quietly become a leak, so the
    /// last lease to go is what ends a graph the swap retired.
    /// </summary>
    [Test]
    public void LastLeaseReleasedAfterASwapTearsTheGraphDown()
    {
        var leased = _registry.Lease(null);

        _catalog.Swap(Catalog(AfterInterval, withKitchen: true));
        leased.Release();

        Assert.That(async () => await leased.Logic.GetTotalAssets(), Throws.InstanceOf<ObjectDisposedException>());
    }

    /// <summary>
    /// Nothing is deferred when there is nothing to defer for: a graph no request holds is torn down
    /// by the swap itself, exactly as it was before leases existed.
    /// </summary>
    [Test]
    public void SwapTearsDownAnUnleasedGraphImmediately()
    {
        var unleased = _registry.For(null);

        _catalog.Swap(Catalog(AfterInterval, withKitchen: true));

        Assert.That(async () => await unleased.Logic.GetTotalAssets(), Throws.InstanceOf<ObjectDisposedException>());
    }

    /// <summary>
    /// A lease is given back exactly once. One given back that was never held would drive the count
    /// below the zero the teardown waits for, and no later release could bring it back - the graph
    /// would stay pinned with nothing left that could free it - so it is refused loudly rather than
    /// leaking quietly.
    /// </summary>
    [Test]
    public void ReleasingALeaseThatWasNeverHeldIsRefused()
    {
        var leased = _registry.Lease(null);

        leased.Release();

        Assert.That(() => leased.Release(), Throws.InstanceOf<InvalidOperationException>());
    }

    /// <summary>
    /// The holder's side of that once-only contract, which is why it clears its reference before it
    /// releases. Disposing twice is ordinary - a <c>using</c> inside a scope the container also
    /// disposes - and taking a lease after the release has already happened would leave one nothing
    /// will ever give back. Neither arrives through the container today, which refuses to resolve
    /// from a disposed scope at all, but <see cref="ProfileScope"/> is a public type with a public
    /// property and that is a container implementation detail to be relying on.
    /// </summary>
    [Test]
    public void DisposedScopeReleasesOnceAndWillNotLeaseAgain()
    {
        var scope = new ProfileScope(_registry, Mock.Of<ICurrentProfile>());
        _ = scope.Services;

        scope.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(() => scope.Dispose(), Throws.Nothing,
                "a second dispose must not give back a lease this scope no longer holds");
            Assert.That(() => scope.Services, Throws.InstanceOf<ObjectDisposedException>(),
                "a resolve after the scope ended would take a lease nothing is left to release");
        });
    }

    /// <summary>
    /// What retirement means to someone still holding a reference: the graph serves out the leases
    /// it already has, and no new one can be taken on it. <see cref="ProfileRegistry.Lease"/> reads
    /// that refusal as "this one has been superseded, go round again and build from the catalog that
    /// is current now" - without it, a swap landing between resolving a graph and leasing it would
    /// pin the outgoing graph for the whole of that request.
    /// </summary>
    [Test]
    public void RetiredGraphRefusesANewLease()
    {
        var leased = _registry.Lease(null);

        _catalog.Swap(Catalog(AfterInterval, withKitchen: true));

        Assert.That(leased.TryAcquire(), Is.False);
    }

    /// <summary>
    /// The narrow race in the same path: a swap landing while a graph is still being built removes
    /// that graph's entry before there is a value under it, so <see cref="ProfileRegistry.Invalidate"/>
    /// has nothing to retire and the finished graph belongs to no dictionary at all. Taking the lease
    /// notices that the key no longer resolves to it, so it dies with the request that built it
    /// instead of outliving the process.
    /// </summary>
    [Test]
    public async Task GraphEvictedWhileItWasBeingBuiltDiesWithTheRequestThatBuiltIt()
    {
        ProfileRegistry registry = null!;
        var catalog = new SwappableConfigCatalog(() => Catalog(BeforeInterval, withKitchen: true), () => registry);

        // ProfileServices reads its settings from inside its constructor, which runs inside the Lazy
        // the registry has already published: swapping from there lands in exactly the window
        // Invalidate cannot see, because the entry it removes has no value under it yet.
        registry = new ProfileRegistry(
            new CatalogSwappingOnFirstSettingsRead(catalog, Catalog(AfterInterval, withKitchen: true)),
            _root);

        var leased = registry.Lease(null);
        var rebuilt = registry.For(null);
        var servedByTheRebuilt = await rebuilt.Logic.GetTotalAssets();

        leased.Release();

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt, Is.Not.SameAs(leased), "the swap evicted the graph that was being built");
            Assert.That(leased.GeneralSettings.Interval, Is.EqualTo(BeforeInterval),
                "the evicted graph was built from the configuration that was current when it started");
            Assert.That(async () => await leased.Logic.GetTotalAssets(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(servedByTheRebuilt, Is.EqualTo(TotalImages),
                "only the evicted graph is retired - the one the key now holds is serving requests");
        });
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
            // Through the forwarder the scope is handed instead of the graph's own logic - see
            // NonOwningImmichFrameLogic for why it is there.
            Assert.That(((NonOwningImmichFrameLogic)logic).Target, Is.SameAs(before.Logic),
                "a service resolved after the swap must come from the graph the scope started with");
        });
    }

    /// <summary>
    /// The lease belongs to the request's own scope. <see cref="ProfileScope"/> takes one when the
    /// request first resolves anything out of the profile, and the container gives it back by
    /// disposing the holder when the scope ends. Driven through the container rather than by
    /// releasing by hand, so that the holder ceasing to be disposable - or the container ceasing to
    /// own it - fails here. That it is registered scoped rather than transient is not what this
    /// pins: <see cref="OneRequestScopeKeepsOneGraphAcrossASwap"/> is, because a transient holder
    /// still gives every lease back at the end of the scope and only shows itself by splitting one
    /// request across two graphs.
    /// </summary>
    [Test]
    public async Task RequestScopeGivesItsLeaseBackWhenItEnds()
    {
        using var factory = CreateFactory();
        _ = factory.CreateClient();

        // No HttpContext in a bare scope, so this resolves the default profile - the path a request
        // that names no profile takes.
        var scope = factory.Services.CreateScope();
        var logic = scope.ServiceProvider.GetRequiredService<IImmichFrameLogic>();

        // The graph that resolution leased, reached without taking a second lease on it: the scope
        // holds the only one, so the scope ending is the only thing that can end the graph.
        var graph = factory.Services.GetRequiredService<ProfileRegistry>().For(null);

        factory.Services.GetRequiredService<SwappableConfigCatalog>().Swap(Catalog(AfterInterval, withKitchen: true));

        var servedAfterTheSwap = await logic.GetTotalAssets();

        scope.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(servedAfterTheSwap, Is.EqualTo(TotalImages),
                "the request goes on being served out of the graph it started with");
            Assert.That(async () => await graph.Logic.GetTotalAssets(), Throws.InstanceOf<ObjectDisposedException>(),
                "the end of the scope releases the last lease, which is what tears the retired graph down");
        });
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion().WithAssetStatistics(TotalImages);

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

    /// <summary>
    /// Swaps the configuration the first time a profile's settings are read out of it. Since
    /// <see cref="ProfileServices"/> reads them in its constructor, that read happens inside the
    /// <c>Lazy</c> the registry has already published - the one window where an entry exists with no
    /// value under it, which is the only way to make the eviction race happen on purpose.
    /// <para>
    /// Only <see cref="Get"/> is hooked, because it is the only member <see cref="ProfileRegistry"/>
    /// resolves a profile through.
    /// </para>
    /// </summary>
    private sealed class CatalogSwappingOnFirstSettingsRead(SwappableConfigCatalog _catalog, IConfigCatalog _replacement)
        : IConfigCatalog
    {
        private bool _swapped;

        public IServerSettings Default => _catalog.Default;

        public IReadOnlyCollection<string> ProfileNames => _catalog.ProfileNames;

        public bool TryGet(string? name, [NotNullWhen(true)] out IServerSettings? settings) =>
            _catalog.TryGet(name, out settings);

        public IServerSettings Get(string? name) => new SettingsSwappingOnRead(_catalog.Get(name), SwapOnce);

        private void SwapOnce()
        {
            if (_swapped) return;

            _swapped = true;
            _catalog.Swap(_replacement);
        }
    }

    /// <summary>
    /// Settings that run <paramref name="_onRead"/> when the general settings are asked for, which is
    /// what puts the swap above inside a graph's construction.
    /// </summary>
    private sealed class SettingsSwappingOnRead(IServerSettings _inner, Action _onRead) : IServerSettings
    {
        public IEnumerable<IAccountSettings> Accounts => _inner.Accounts;

        public IGeneralSettings GeneralSettings
        {
            get
            {
                _onRead();

                return _inner.GeneralSettings;
            }
        }

        public void Validate() => _inner.Validate();
    }
}
