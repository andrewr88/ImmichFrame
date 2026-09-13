using System.Net.Http;
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
/// How long a profile's object graph lives. It is shared by every request on that profile, so no
/// single request may end its life: <see cref="ProfileRegistry.Invalidate"/> retires it and the last
/// lease to be released is what tears it down.
/// <para>
/// The trap these pin is that Microsoft.Extensions.DependencyInjection takes ownership of any
/// <see cref="IDisposable"/> a factory registration returns, including one the container never
/// created, and disposes it when the scope ends. That is why the scoped
/// <see cref="IImmichFrameLogic"/> registration hands out a
/// <see cref="NonOwningImmichFrameLogic"/> instead of the graph's own logic.
/// </para>
/// </summary>
[TestFixture]
public class ProfileGraphLifetimeTests
{
    // Deliberately not the zero a disposed - or never-consulted - graph would be mistaken for.
    private const long TotalImages = 7;

    /// <summary>
    /// Nothing the container is handed out of a profile's graph may be disposable, because the
    /// container disposes whatever it is handed - <see cref="IDisposable"/> and
    /// <see cref="IAsyncDisposable"/> alike. An assertion on the type rather than on behaviour, so
    /// that unwrapping the forwarder fails here with the reason rather than somewhere downstream
    /// with a symptom.
    /// <para>
    /// Every shared member <c>Program.cs</c> registers scoped is checked, not only the logic. The
    /// weather and calendar services are handed out as they are, because their Core types are of
    /// neither disposable kind today - and an upstream merge quietly making a Core type disposable
    /// is exactly how this outage arrived in the first place. For those two it would be worse than
    /// for the logic: a retired <see cref="ProfileServices"/> does not tear them down at all, so a
    /// request scope that destroyed one would leave the registry serving it dead with nothing that
    /// ever rebuilds it. Add a fourth scoped handout and it belongs in this list too.
    /// </para>
    /// </summary>
    [Test]
    public void RequestScopeIsHandedSharedServicesItCannotDispose()
    {
        using var factory = CreateFactory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        Assert.Multiple(() =>
        {
            AssertTheContainerCannotOwnIt(scope.ServiceProvider.GetRequiredService<IImmichFrameLogic>());
            AssertTheContainerCannotOwnIt(scope.ServiceProvider.GetRequiredService<IWeatherService>());
            AssertTheContainerCannotOwnIt(scope.ServiceProvider.GetRequiredService<ICalendarService>());
        });
    }

    private static void AssertTheContainerCannotOwnIt(object service)
    {
        var name = service.GetType().Name;

        Assert.That(service, Is.Not.InstanceOf<IDisposable>(),
            $"the container disposes every IDisposable a scoped factory returns, so it must not be handed {name}");
        Assert.That(service, Is.Not.InstanceOf<IAsyncDisposable>(),
            $"the container disposes every IAsyncDisposable a scoped factory returns, so it must not be handed {name}");
    }

    /// <summary>
    /// The outage this reproduces: the first request to resolve <see cref="IImmichFrameLogic"/> tore
    /// down the profile's pools and API caches when its scope ended, and every later request on that
    /// profile failed with <see cref="ObjectDisposedException"/> out of the cache.
    /// </summary>
    [Test]
    public async Task ProfileGraphSurvivesTheRequestScopesThatUseIt()
    {
        using var factory = CreateFactory();
        _ = factory.CreateClient();

        var shared = factory.Services.GetRequiredService<ProfileRegistry>().For(null).Logic;

        using (var scope = factory.Services.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<IImmichFrameLogic>();
        }

        // Reaches the profile's MemoryCache before it reaches the network, so a graph the scope took
        // down with it fails here with ObjectDisposedException rather than with a network error.
        Assert.That(await shared.GetTotalAssets(), Is.EqualTo(TotalImages));
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        // The statistics call is the only Immich call GetTotalAssets makes on an unnarrowed account,
        // and it is made behind the API cache - which is the piece that used to be disposed.
        var handler = new Mock<HttpMessageHandler>().WithServerVersion().WithAssetStatistics(TotalImages);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Without this the startup version gate calls Environment.Exit and takes the
                    // test host down with it.
                    services.UseMockHandler(handler);

                    // Seeding the catalog rather than IServerSettings directly: the per-profile
                    // services are built from the catalog, so a settings override never reaches them.
                    services.AddSingleton<IConfigCatalog>(new ConfigCatalog(Settings()));
                });
            });
    }

    private static ServerSettings Settings() => new()
    {
        GeneralSettingsImpl = new GeneralSettings(),
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
