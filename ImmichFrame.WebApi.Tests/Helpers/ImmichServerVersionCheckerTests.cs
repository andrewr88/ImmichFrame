using System.Net.Http;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers;

/// <summary>
/// The check ImmichFrame makes, once at startup, of every Immich server it is configured with.
/// <para>
/// <c>Program</c> starts it without awaiting it and only logs what it finds, so that an unreachable or
/// outdated server cannot keep the host from starting - and with it the admin editor that would fix
/// the account. That also means no running host behaves any differently whatever the check decides,
/// so what it decides is asserted here, on its own.
/// </para>
/// </summary>
[TestFixture]
public class ImmichServerVersionCheckerTests
{
    private const string Server = "http://immich.example.com";
    private const string OtherServer = "http://other-immich.example.com";

    /// <summary>How long a server is given to answer where a test waits that out: short, so the suite does not wait on it.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The longest a test waits for a check to give up on a server: room for a slow machine, and short
    /// of both the five seconds a real check allows and the hundred HttpClient allows by default -
    /// either of which would end the check as well, and pass the test with it.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Read off the server, not assumed: below the major version this ImmichFrame supports the account
    /// is reported unsupported, and either way the message names the version the server runs, so an
    /// operator knows what they are updating from.
    /// </summary>
    [TestCase(ImmichServerVersionChecker.MinimumSupportedMajorVersion - 1, false)]
    [TestCase(ImmichServerVersionChecker.MinimumSupportedMajorVersion, true)]
    public async Task CheckAccount_ReportsWhetherTheServersMajorVersionIsSupported(long major, bool supported)
    {
        // Arrange
        var immich = new Mock<HttpMessageHandler>().WithServerVersion(major, minor: 5, patch: 1);
        using var services = Services(immich);

        // Act
        var result = await ImmichServerVersionChecker.CheckAccount(Account(Server), services.GetRequiredService<IHttpClientFactory>());

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.EqualTo(supported));
            Assert.That(result.Version, Is.EqualTo($"{major}.5.1"));
            Assert.That(result.Message, Does.Contain($"v{major}.5.1"));
        });
    }

    /// <summary>
    /// A server that cannot be reached is a failed check rather than an exception - every account is
    /// checked at once, and one throwing would lose the others' results - and its version is unknown,
    /// not made up.
    /// </summary>
    [Test]
    public async Task CheckAccount_ServerUnreachable_FailsWithNoVersion()
    {
        // Arrange
        var immich = new Mock<HttpMessageHandler>();
        immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        using var services = Services(immich);

        // Act
        var result = await ImmichServerVersionChecker.CheckAccount(Account(Server), services.GetRequiredService<IHttpClientFactory>());

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Version, Is.Null);
            Assert.That(result.Message, Does.Contain(Server).And.Contain("Connection refused"));
        });
    }

    /// <summary>
    /// A server that takes the request and never answers it is given only so long before it counts as
    /// unreachable. The frame's client sets no timeout of its own, and every account's result is logged
    /// only once all of them are in, so without this one a single silent server would hold back the
    /// startup log for every other server for HttpClient's hundred seconds.
    /// </summary>
    [Test]
    public async Task CheckAccount_ServerNeverAnswers_FailsOnceItsTimeoutRunsOut()
    {
        // Arrange - a server that holds the request for as long as it is let
        var immich = new Mock<HttpMessageHandler>();
        immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage();
            });
        using var services = Services(immich);

        // Act
        var checking = ImmichServerVersionChecker.CheckAccount(
            Account(Server), services.GetRequiredService<IHttpClientFactory>(), ShortTimeout);

        // Assert
        Assert.That(await Task.WhenAny(checking, Task.Delay(Deadline)), Is.SameAs(checking),
            $"the check was still waiting on the server {Deadline.TotalSeconds} s into a {ShortTimeout.TotalMilliseconds} ms timeout");

        var result = await checking;
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Version, Is.Null);
            Assert.That(result.Message, Does.Contain(Server));
        });
    }

    /// <summary>
    /// A configuration that cannot be loaded leaves no servers to check, which is not the same as every
    /// server being fine. So it is a failed check, and <c>Program</c> points the operator at /admin
    /// for it just as it does for a server that failed.
    /// </summary>
    [Test]
    public async Task CheckServerVersions_ConfigurationCannotBeLoaded_IsFalse()
    {
        // Arrange - the way SwappableConfigCatalog fails: on first use, whichever member that is.
        var failure = new SettingsNotValidException("Settings.json does not parse");
        var catalog = new Mock<IConfigCatalog>();
        catalog.Setup(c => c.Default).Throws(failure);
        catalog.Setup(c => c.ProfileNames).Throws(failure);
        using var services = Services(new Mock<HttpMessageHandler>().WithServerVersion(), catalog.Object);

        // Act
        var allSupported = await ImmichServerVersionChecker.CheckServerVersions(services, NullLogger.Instance);

        // Assert
        Assert.That(allSupported, Is.False);
    }

    /// <summary>
    /// Profiles commonly share an account - one that changes only the clock still shows the default's
    /// photos - and a version belongs to the server, not to a profile. So an account is asked once
    /// however many profiles name it. Matched by server and key rather than by object: each profile is
    /// a settings object of its own, so the account two profiles share is two equal objects.
    /// </summary>
    [Test]
    public async Task CheckServerVersions_ProfilesSharingAnAccount_AskItsServerOnce()
    {
        // Arrange
        var immich = new Mock<HttpMessageHandler>().WithServerVersion();
        var catalog = Catalog(Account(OtherServer), new Dictionary<string, IServerSettings>
        {
            ["kitchen"] = Settings(Account(Server)),
            ["living-room"] = Settings(Account(Server))
        });
        using var services = Services(immich, catalog);

        // Act
        var allSupported = await ImmichServerVersionChecker.CheckServerVersions(services, NullLogger.Instance);

        // Assert
        Assert.That(allSupported, Is.True);
        VerifyVersionRequests(immich, Server, Times.Once());
        VerifyVersionRequests(immich, OtherServer, Times.Once());
    }

    /// <summary>
    /// True only when every server passed. One that cannot be reached among others that can is still
    /// a server the frame cannot show photos from, and the startup log has to say so.
    /// </summary>
    [Test]
    public async Task CheckServerVersions_OneServerUnreachable_IsFalse()
    {
        // Arrange - every server answers but one.
        var immich = new Mock<HttpMessageHandler>().WithServerVersion();
        immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.Host == new Uri(OtherServer).Host),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        using var services = Services(immich, Catalog(Account(Server), Account(OtherServer)));

        // Act
        var allSupported = await ImmichServerVersionChecker.CheckServerVersions(services, NullLogger.Instance);

        // Assert
        Assert.That(allSupported, Is.False);
    }

    private static void VerifyVersionRequests(Mock<HttpMessageHandler> immich, string server, Times times) =>
        immich.Protected().Verify("SendAsync", times,
            ItExpr.Is<HttpRequestMessage>(request =>
                request.RequestUri!.Host == new Uri(server).Host &&
                request.RequestUri.AbsolutePath.EndsWith("/server/version")),
            ItExpr.IsAny<CancellationToken>());

    /// <summary>
    /// What the checker resolves: an HttpClient factory whose clients reach <paramref name="immich"/>,
    /// and the catalog the servers are listed in, when there is one to give it.
    /// </summary>
    private static ServiceProvider Services(Mock<HttpMessageHandler> immich, IConfigCatalog? catalog = null)
    {
        var services = new ServiceCollection().AddHttpClient().UseMockHandler(immich);
        if (catalog is not null) services.AddSingleton(catalog);

        return services.BuildServiceProvider();
    }

    private static ConfigCatalog Catalog(params ServerAccountSettings[] accounts) => new(Settings(accounts));

    private static ConfigCatalog Catalog(ServerAccountSettings account, Dictionary<string, IServerSettings> profiles) =>
        new(Settings(account), profiles);

    private static ServerSettings Settings(params ServerAccountSettings[] accounts) => new()
    {
        GeneralSettingsImpl = new GeneralSettings(),
        AccountsImpl = accounts.ToList()
    };

    // A new object every time, deliberately - see CheckServerVersions_ProfilesSharingAnAccount_AskItsServerOnce.
    private static ServerAccountSettings Account(string server) => new()
    {
        ImmichServerUrl = server,
        ApiKey = "api-key"
    };
}
