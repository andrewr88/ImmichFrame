using System.Collections.Concurrent;
using System.Net.Http;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Tests.Controllers;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests;

/// <summary>
/// What <c>Program</c> makes of the admin surface's environment variables while it starts, before
/// any request arrives.
/// <para>
/// Replacing the <c>AdminOidcOptions</c> singleton, as the other admin fixtures do, reaches everything
/// a request asks of the options - and nothing <c>Program</c> already did with the ones it read for
/// itself: whether to register the OpenID Connect handler, and whether to warn that the surface is
/// half set up. So here, as in <c>AdminLoginChallengeTests</c>, the variables are set before
/// <see cref="WebApplicationFactory{T}"/> builds anything - all four by every test, and all four put
/// back after it.
/// </para>
/// </summary>
[TestFixture]
public class AdminSurfaceStartupTests
{
    private const string Authority = "https://idp.example.com/realms/immichframe";
    private const string ClientId = "immichframe";
    private const string ClientSecret = "client-secret";
    private const string Admin = "admin@example.com";

    private static readonly string[] Variables =
    [
        AdminOidcOptions.AuthorityVariable,
        AdminOidcOptions.ClientIdVariable,
        AdminOidcOptions.ClientSecretVariable,
        AdminOidcOptions.AdminsVariable
    ];

    private Dictionary<string, string?> _restore = null!;

    [SetUp]
    public void SaveEnvironment() =>
        _restore = Variables.ToDictionary(variable => variable, Environment.GetEnvironmentVariable);

    [TearDown]
    public void RestoreEnvironment()
    {
        foreach (var (variable, value) in _restore)
        {
            Environment.SetEnvironmentVariable(variable, value);
        }
    }

    /// <summary>
    /// Every credential set and nobody on the allowlist. Registered, the handshake would run to the end
    /// for anyone the identity provider will sign in, and mint them a cookie no policy will ever
    /// accept - so it is not registered at all, just as on an installation that never asked for the
    /// editor.
    /// </summary>
    [Test]
    public async Task OidcHandler_AllowlistEmpty_IsNotRegistered()
    {
        SetEnvironment(Authority, ClientId, ClientSecret, admins: null);
        using var factory = CreateFactory();

        Assert.That(await OidcScheme(factory), Is.Null);
    }

    /// <summary>
    /// And the same host with the allowlist filled in, so that the test above is failing on the
    /// allowlist rather than on the name the scheme is looked up by.
    /// </summary>
    [Test]
    public async Task OidcHandler_FullyConfigured_IsRegistered()
    {
        SetEnvironment(Authority, ClientId, ClientSecret, Admin);
        using var factory = CreateFactory();

        Assert.That(await OidcScheme(factory), Is.Not.Null);
    }

    /// <summary>
    /// A half-configured surface is simply off, which from outside looks exactly like an installation
    /// that never asked for the editor - so the startup log is the one place that says which variable
    /// is missing, and only <c>Program</c> calling <c>WarnIfHalfConfigured</c> puts it there. What the
    /// warning says is pinned in <c>AdminOidcOptionsTests</c>; this is that a starting host says it,
    /// once.
    /// </summary>
    [Test]
    public void Startup_HalfConfiguredSurface_WarnsOnceNamingWhatIsMissing()
    {
        SetEnvironment(Authority, ClientId, ClientSecret, admins: null);
        var log = new LogSink();
        using var factory = CreateFactory(log);

        // Program runs as far as app.Run(), the warning included, before the host is handed over.
        _ = factory.Services;

        var said = AboutTheVariables(log);

        Assert.That(said, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(said[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(said[0].Message, Does.Contain(AdminOidcOptions.AdminsVariable));
        });
    }

    /// <summary>
    /// And nothing at all from a host that never asked for the editor - which is not to be nagged
    /// about it on every start - or from one whose surface is on.
    /// </summary>
    [TestCase(null, null, null, null)]
    [TestCase(Authority, ClientId, ClientSecret, Admin)]
    public void Startup_UnconfiguredOrFullyConfiguredSurface_SaysNothingAboutIt(
        string? authority, string? clientId, string? clientSecret, string? admins)
    {
        SetEnvironment(authority, clientId, clientSecret, admins);
        var log = new LogSink();
        using var factory = CreateFactory(log);

        _ = factory.Services;

        Assert.Multiple(() =>
        {
            // Establishes that the sink heard the host start at all. Without it the assertion below
            // would pass just as happily on a sink nothing was ever written to.
            Assert.That(log.Entries, Is.Not.Empty);

            Assert.That(AboutTheVariables(log), Is.Empty);
        });
    }

    private static void SetEnvironment(string? authority, string? clientId, string? clientSecret, string? admins)
    {
        Environment.SetEnvironmentVariable(AdminOidcOptions.AuthorityVariable, authority);
        Environment.SetEnvironmentVariable(AdminOidcOptions.ClientIdVariable, clientId);
        Environment.SetEnvironmentVariable(AdminOidcOptions.ClientSecretVariable, clientSecret);
        Environment.SetEnvironmentVariable(AdminOidcOptions.AdminsVariable, admins);
    }

    private static Task<AuthenticationScheme?> OidcScheme(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetSchemeAsync(AdminAuthentication.OidcScheme);

    /// <summary>
    /// Whatever the host logged that names one of the four variables. Naming what is missing is the
    /// warning's whole job, and nothing else a host logs while it starts mentions them.
    /// </summary>
    private static List<(LogLevel Level, string Message)> AboutTheVariables(LogSink log) =>
        log.Entries.Where(entry => Variables.Any(variable => entry.Message.Contains(variable))).ToList();

    private static WebApplicationFactory<Program> CreateFactory(LogSink? log = null)
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(versionHandler);
                    services.AddSingleton<IConfigCatalog>(AdminSessionControllerTests.Catalog());

                    // Deliberately no AdminOidcOptions override: what is under test is what Program
                    // made of the environment.

                    if (log is not null)
                    {
                        // A rule naming the sink outranks every rule that does not, so the sink hears
                        // the whole startup log whatever LOG_LEVEL says.
                        services.AddSingleton<ILoggerProvider>(log);
                        services.Configure<LoggerFilterOptions>(options =>
                            options.AddFilter<LogSink>(null, LogLevel.Trace));
                    }
                });
            });
    }

    /// <summary>
    /// Every entry the host logs, with its level. Concurrent, because the startup version check logs
    /// from a thread of its own.
    /// </summary>
    private sealed class LogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IEnumerable<(LogLevel Level, string Message)> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new Logger(_entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(LogLevel Level, string Message)> _entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
