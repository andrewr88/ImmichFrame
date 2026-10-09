using System.Collections.Concurrent;
using System.Net;
using ImmichFrame.Core.Services;
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
/// The bounds on the client calendar feeds are fetched on. A feed is whatever its host chooses to
/// send, and every frame's request for the calendar waits on the load that fetches the feeds, so the
/// client is registered in <c>Program.cs</c> with a timeout and a cap on how much of a feed it reads -
/// and since nothing in the calendar service can see that registration, it is pinned here, off a host
/// built from the application's own.
/// <para>
/// What the same client keeps out of the log is pinned in <c>HttpClientLoggingTests</c>.
/// </para>
/// </summary>
[TestFixture]
public class CalendarHttpClientTests
{
    private const string ImmichServer = "http://mock-immich-server.com";

    [Test]
    public void CalendarHttpClient_IsWiredWithTheRequestTimeout()
    {
        // Read off a client the application's own registrations build. Left at HttpClient's default, a
        // feed that takes the connection and then says nothing held every frame's request for the
        // calendar for a hundred seconds.
        using var factory = CreateFactory(new Mock<HttpMessageHandler>().WithServerVersion(), new LogSink());

        var client = factory.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(IcalCalendarService.HttpClientName);

        Assert.That(client.Timeout, Is.EqualTo(IcalCalendarService.RequestTimeout));
    }

    /// <summary>
    /// A feed announcing more than the client will read is refused, and costs only its own
    /// appointments. Without the cap HttpClient buffers up to two gigabytes of whatever a feed's host
    /// sends - the same unbounded read the configuration editor's picker is protected from, and with
    /// the same handler.
    /// </summary>
    [Test]
    public async Task Calendar_AFeedAnnouncingMoreThanTheLimit_CostsOnlyItsOwnAppointments()
    {
        // Arrange - one byte over the limit, in front of a calendar that is fine in itself: a client
        // that read it regardless would be seen to, by its appointment reaching the frame
        var network = new Mock<HttpMessageHandler>().WithServerVersion();
        FeedServes(network, "good.example.com", () => Feed("Dentist"));
        FeedServes(network, "huge.example.com", () =>
        {
            var response = Feed("Never shown");
            response.Content.Headers.ContentLength = IcalCalendarService.MaxFeedBytes + 1;
            return response;
        });

        var logs = new LogSink();
        using var factory = CreateFactory(network, logs,
            "https://good.example.com/a.ics", "https://huge.example.com/private-token/b.ics");

        // Act
        using var response = await factory.CreateClient().GetAsync("/api/Calendar");
        var appointments = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(appointments, Does.Contain("Dentist").And.Not.Contain("Never shown"));
            Assert.That(logs.Errors, Has.One.Contains("'https://huge.example.com'"));
        });
    }

    private static void FeedServes(Mock<HttpMessageHandler> network, string host, Func<HttpResponseMessage> response) =>
        network.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == host), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

    // At noon every day since 2024, so the feed has an appointment today whenever the test runs.
    private static HttpResponseMessage Feed(string summary) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//ImmichFrame//Tests//EN
            BEGIN:VEVENT
            UID:{summary.Replace(' ', '-')}@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:20240101T120000
            DTEND:20240101T123000
            RRULE:FREQ=DAILY
            SUMMARY:{summary}
            END:VEVENT
            END:VCALENDAR
            """)
    };

    private static WebApplicationFactory<Program> CreateFactory(Mock<HttpMessageHandler> network, LogSink logs,
        params string[] calendars) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // In place of every client's primary handler only: the handlers Program.cs chains
                    // in front of it, the size limit among them, stay where they are.
                    services.UseMockHandler(network);
                    services.AddSingleton<IConfigCatalog>(new ConfigCatalog(new ServerSettings
                    {
                        GeneralSettingsImpl = new GeneralSettings { Webcalendars = [.. calendars] },
                        AccountsImpl = new List<ServerAccountSettings>
                        {
                            new() { ImmichServerUrl = ImmichServer, ApiKey = "test-api-key" }
                        }
                    }));
                    services.AddSingleton<ILoggerProvider>(logs);
                });
            });

    /// <summary>
    /// Every entry the host logs, with its level. Concurrent, because the startup version check logs
    /// from a thread of its own.
    /// </summary>
    private sealed class LogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IEnumerable<string> Errors => _entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message);

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
