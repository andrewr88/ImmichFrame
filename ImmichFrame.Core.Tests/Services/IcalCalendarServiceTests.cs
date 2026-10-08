using System.Net;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Language.Flow;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Services;

[TestFixture]
public class IcalCalendarServiceTests
{
    // At noon every day since 2024, so the feed has an appointment today whenever the test runs.
    private const string DailyCalendar = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//ImmichFrame//Tests//EN
        BEGIN:VEVENT
        UID:daily@immichframe.test
        DTSTAMP:20240101T000000Z
        DTSTART:20240101T120000
        DTEND:20240101T123000
        RRULE:FREQ=DAILY
        SUMMARY:Daily check-in
        END:VEVENT
        END:VCALENDAR
        """;

    private Mock<IGeneralSettings> _settings;
    private Mock<HttpMessageHandler> _network;
    private ListLogger<IcalCalendarService> _logger;
    private IcalCalendarService _sut;

    [SetUp]
    public void Setup()
    {
        _settings = new Mock<IGeneralSettings>();
        _logger = new ListLogger<IcalCalendarService>();

        // Feeds are faked below HttpClient, one host at a time, so a test says what each feed it
        // configures does and the service's own request and response handling still runs.
        _network = new Mock<HttpMessageHandler>();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_network.Object));

        _sut = new IcalCalendarService(_settings.Object, _logger, httpClientFactory.Object);

        HostServes("good", () => Ok(DailyCalendar));
    }

    private void Calendars(params string[] urls) =>
        _settings.SetupGet(s => s.Webcalendars).Returns(urls.ToList());

    private ISetup<HttpMessageHandler, Task<HttpResponseMessage>> Host(string host) =>
        _network.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == host), ItExpr.IsAny<CancellationToken>());

    private void HostServes(string host, Func<HttpResponseMessage> response) => Host(host).ReturnsAsync(response);

    private void HostFails(string host, Exception failure) => Host(host).ThrowsAsync(failure);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private List<string> Errors() =>
        _logger.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();

    private List<string> Logged() => _logger.Entries.Select(e => e.Text).ToList();

    [TestCase("<html><body>Login page</body></html>")] // Calendar.Load throws on it
    [TestCase("")]                                     // Calendar.Load returns null for it
    public async Task GetAppointments_OneFeedServesSomethingElse_ReturnsTheOtherFeedsAppointments(string body)
    {
        // Arrange - a feed answering 200 with something that is not a calendar, as a provider's
        // sign-in page does once a session or a shared link has expired
        Calendars("https://good/a.ics", "https://bad/private-token/b.ics");
        HostServes("bad", () => Ok(body));

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert - the bad feed costs only its own appointments, and is named in the log by host
        Assert.Multiple(() =>
        {
            Assert.That(appointments.Select(a => a.Summary), Is.EqualTo(new[] { "Daily check-in" }));
            Assert.That(Errors(), Has.One.Contains("'https://bad'"));
            Assert.That(Logged(), Has.None.Contains("private-token"));
        });
    }

    [Test]
    public async Task GetAppointments_OneFeedUnreachable_ReturnsTheOtherFeedsAppointments()
    {
        // Arrange
        Calendars("https://good/a.ics", "https://bad/private-token/b.ics");
        HostFails("bad", new HttpRequestException("Name or service not known (bad:443)"));

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointments.Select(a => a.Summary), Is.EqualTo(new[] { "Daily check-in" }));
            Assert.That(Errors(), Has.One.Contains("'https://bad'"));
            Assert.That(Logged(), Has.None.Contains("private-token"));
        });
    }

    [Test]
    public async Task GetAppointments_FeedUrlInvalidOrRefused_LogsOnlySchemeHostAndPort()
    {
        // Arrange - a calendar URL is a credential more often than not: user:password@ is how a feed
        // is given Basic authentication, and Google's "secret address" and iCloud's published
        // calendars carry their access token in the path
        Calendars(
            "calendar.example.com/no-scheme-token/basic.ics",
            "https://frame-user:hunter2@missing:8443/private-token/basic.ics?key=query-token");
        HostServes("missing", () => new HttpResponseMessage(HttpStatusCode.NotFound));

        // Act
        await _sut.GetAppointments();

        // Assert - at every level, Debug included
        var logged = Logged();
        Assert.Multiple(() =>
        {
            foreach (var secret in new[] { "no-scheme-token", "frame-user", "hunter2", "private-token", "query-token" })
            {
                Assert.That(logged, Has.None.Contains(secret));
            }

            Assert.That(Errors(), Has.One.Contains("(unparseable URL)"));
            Assert.That(Errors(), Has.One.Contains("'https://missing:8443'"));
        });
    }
}
