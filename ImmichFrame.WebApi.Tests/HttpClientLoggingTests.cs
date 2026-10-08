using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests;

/// <summary>
/// What the log shows of the requests ImmichFrame sends once <c>LOG_LEVEL</c> is lowered to report a
/// problem. At Debug or lower <c>IHttpClientFactory</c>'s request logging reaches the log: each
/// request's full URL, and at Trace every request header - redacting neither in this version. So each
/// client's registration in <c>Program.cs</c> is what decides whether a secret carried in a URL or a
/// header reaches the log, and the clients are exercised here as the host registers them, through the
/// code that uses them.
/// <para>
/// The webhook client's share of this is pinned in <c>WebhookTests</c>.
/// </para>
/// </summary>
[TestFixture]
public class HttpClientLoggingTests
{
    private const string ImmichServer = "http://mock-immich-server.com";
    private const string ApiKey = "frame-api-key-6b1f0d";

    // A calendar feed with a secret in each place one goes: Google's and iCloud's secret addresses
    // keep their access token in the path, other providers in the query, and user:password@ is how a
    // feed is configured for Basic authentication.
    private const string CalendarHost = "calendar.example.com";
    private const string PathToken = "private-5f0c2a9e7d41b3c8";
    private const string QuerySecret = "query-secret-93ad1e";
    private const string CalendarUser = "frame";
    private const string CalendarPassword = "calendar-password-27c4";

    private const string CalendarUrl =
        $"https://{CalendarUser}:{CalendarPassword}@{CalendarHost}/calendar/ical/{PathToken}/basic.ics?key={QuerySecret}";

    // The password as it goes out, in the Authorization header.
    private static readonly string BasicCredential =
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CalendarUser}:{CalendarPassword}"));

    /// <summary>
    /// Silenced outright rather than redacted: the factory writes the URL into the request line, and
    /// a header redaction cannot reach that.
    /// </summary>
    [Test]
    public async Task CalendarHttpClient_KeepsAFeedsSecretsOutOfTheLog()
    {
        // Arrange
        string? fetched = null;
        var network = new Mock<HttpMessageHandler>().WithServerVersion();
        network.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == CalendarHost),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                fetched = $"{request.RequestUri} {request.Headers.Authorization}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//ImmichFrame//Tests//EN\nEND:VCALENDAR\n")
                };
            });

        var logs = new LogSink();
        using var factory = CreateFactory(network, logs, new GeneralSettings { Webcalendars = [CalendarUrl] });

        // Act
        using var response = await factory.CreateClient().GetAsync("/api/Calendar");

        // Assert - the feed was fetched with every secret on it, so a log without them is one that
        // kept them out rather than one that never saw them.
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fetched, Does.Contain(PathToken).And.Contain(QuerySecret).And.Contain(BasicCredential));
            Assert.That(logs.Messages, Has.None.Contains(PathToken));
            Assert.That(logs.Messages, Has.None.Contains(QuerySecret));
            Assert.That(logs.Messages, Has.None.Contains(BasicCredential));
        });
    }

    /// <summary>
    /// The frame's own requests to its Immich server, every one carrying the account's key. Redacted
    /// rather than silenced: the request line and its timing are what an operator chasing a slow or
    /// failing server reads.
    /// </summary>
    [Test]
    public async Task ImmichAccountHttpClient_LogsTheRequestButNotTheApiKey()
    {
        // Arrange
        var network = new Mock<HttpMessageHandler>().WithServerVersion().WithAssetStatistics(0).WithNoRandomAssets();
        var logs = new LogSink();
        using var factory = CreateFactory(network, logs);

        // Act
        using var response = await factory.CreateClient().GetAsync("/api/Asset");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(logs.Messages, Has.None.Contains(ApiKey));
            Assert.That(logs.Messages, Has.Some.Contains($"GET {ImmichServer}/api/"));
        });
    }

    /// <summary>
    /// The configuration editor's picker sends whatever key an administrator types in, to whatever
    /// server they name. A server and key of its own here, so that nothing the startup version check
    /// logs on the frame's client can stand in for what the picker's client logs.
    /// </summary>
    [Test]
    public async Task AdminPickerHttpClient_LogsTheRequestButNotTheApiKey()
    {
        // Arrange
        const string typedHost = "typed-immich.example.com";
        const string typedKey = "typed-api-key-0c93e7";

        var logs = new LogSink();
        using var factory = CreateFactory(new Mock<HttpMessageHandler>().WithServerVersion(), logs);
        var immich = factory.Services.GetRequiredService<AdminImmichAccounts>()
            .Connect(new ResolvedImmichAccount($"http://{typedHost}", typedKey, typedHost));

        // Act
        await immich.GetServerVersionAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(logs.Messages, Has.None.Contains(typedKey));
            Assert.That(logs.Messages, Has.Some.Contains($"GET http://{typedHost}/api/server/version"));
        });
    }

    private static WebApplicationFactory<Program> CreateFactory(Mock<HttpMessageHandler> network, LogSink logs,
        GeneralSettings? general = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(network);
                    services.AddSingleton<IConfigCatalog>(new ConfigCatalog(new ServerSettings
                    {
                        GeneralSettingsImpl = general ?? new GeneralSettings(),
                        AccountsImpl = new List<ServerAccountSettings>
                        {
                            new() { ImmichServerUrl = ImmichServer, ApiKey = ApiKey }
                        }
                    }));

                    // Every HttpClient entry reaches the sink, request headers included, whatever
                    // LOG_LEVEL is - so it is shown what a container's console is with LOG_LEVEL=Trace.
                    // A rule naming the sink outranks every rule that does not: Program.cs's Warning
                    // filter, and the Default level appsettings.Development.json sets for the environment
                    // this host runs in, which would otherwise hold Trace back whatever LOG_LEVEL said.
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.Configure<LoggerFilterOptions>(options =>
                        options.AddFilter<LogSink>("System.Net.Http.HttpClient", LogLevel.Trace));
                });
            });

    /// <summary>
    /// Every message the host logs. Concurrent, because the startup version check logs from a thread
    /// of its own.
    /// </summary>
    private sealed class LogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> _messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _messages.Enqueue(formatter(state, exception));
        }
    }
}
