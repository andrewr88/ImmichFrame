using System.Globalization;
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

    // The only calendar configured, holding these components.
    private void OnlyFeed(params string[] components)
    {
        Calendars("https://feed/calendar.ics");
        HostServes("feed", () => Ok($"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//ImmichFrame//Tests//EN
            {string.Join("\n", components)}
            END:VCALENDAR
            """));
    }

    // A DATE-TIME with no zone - "floating" in RFC 5545 - is local time wherever it is read, so a test
    // that writes one expects the same wall-clock time back in any time zone.
    private static string Floating(DateTime value) => value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    private static string Day(DateTime value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

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

    /// <summary>
    /// A recurring event's DTSTART and DTEND are those of its first instance. Today's appointment has
    /// to carry today's times - the frame sorts the day's appointments by them and prints them - not
    /// those of an instance two months back. An end given as a DURATION rather than a DTEND has to
    /// arrive the same way.
    /// </summary>
    [TestCase(false, TestName = "GetAppointments_DailyEventThatBeganWeeksAgo_IsTimedForToday")]
    [TestCase(true, TestName = "GetAppointments_DailyEventWithADurationThatBeganWeeksAgo_IsTimedForToday")]
    public async Task GetAppointments_DailyEventThatBeganWeeksAgo_IsTimedForToday(bool endsByDuration)
    {
        // Arrange
        var firstDay = DateTime.Today.AddDays(-60);
        var end = endsByDuration ? "DURATION:PT1H" : $"DTEND:{Floating(firstDay.AddHours(10))}";
        OnlyFeed($"""
            BEGIN:VEVENT
            UID:stand-up@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:{Floating(firstDay.AddHours(9))}
            {end}
            RRULE:FREQ=DAILY
            SUMMARY:Stand-up
            END:VEVENT
            """);

        // Act
        var appointment = (await _sut.GetAppointments()).Single();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointment.StartTime, Is.EqualTo(DateTime.Today.AddHours(9)));
            Assert.That(appointment.EndTime, Is.EqualTo(DateTime.Today.AddHours(10)));
            Assert.That(appointment.Duration, Is.EqualTo(TimeSpan.FromHours(1)));
        });
    }

    [Test]
    public async Task GetAppointments_WeeklyAllDayEventThatBeganWeeksAgo_IsDatedToday()
    {
        // Arrange - two weeks back is today's weekday, so the series has an instance today
        var firstDay = DateTime.Today.AddDays(-14);
        OnlyFeed($"""
            BEGIN:VEVENT
            UID:bin-day@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART;VALUE=DATE:{Day(firstDay)}
            DTEND;VALUE=DATE:{Day(firstDay.AddDays(1))}
            RRULE:FREQ=WEEKLY
            SUMMARY:Bin day
            END:VEVENT
            """);

        // Act
        var appointment = (await _sut.GetAppointments()).Single();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointment.StartTime, Is.EqualTo(DateTime.Today));
            Assert.That(appointment.EndTime, Is.EqualTo(DateTime.Today.AddDays(1)));
        });
    }

    [Test]
    public async Task GetAppointments_SingleEvent_IsMappedAsWritten()
    {
        // Arrange
        OnlyFeed($"""
            BEGIN:VEVENT
            UID:dentist@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:{Floating(DateTime.Today.AddHours(14))}
            DTEND:{Floating(DateTime.Today.AddHours(15).AddMinutes(30))}
            SUMMARY:Dentist
            DESCRIPTION:Bring the referral letter
            LOCATION:High Street
            END:VEVENT
            """);

        // Act
        var appointment = (await _sut.GetAppointments()).Single();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointment.Summary, Is.EqualTo("Dentist"));
            Assert.That(appointment.Description, Is.EqualTo("Bring the referral letter"));
            Assert.That(appointment.Location, Is.EqualTo("High Street"));
            Assert.That(appointment.StartTime, Is.EqualTo(DateTime.Today.AddHours(14)));
            Assert.That(appointment.EndTime, Is.EqualTo(DateTime.Today.AddHours(15).AddMinutes(30)));
            Assert.That(appointment.Duration, Is.EqualTo(TimeSpan.FromMinutes(90)));
        });
    }

    /// <summary>
    /// Ical.Net gives the occurrences of a to-do no end time at all, and an event with neither DTEND
    /// nor DURATION has none of its own - RFC 5545 makes it a moment rather than a span. Either used to
    /// throw while the feed was read, which cost the feed every appointment it had.
    /// </summary>
    [TestCase("VTODO", TestName = "GetAppointments_FeedWithATodo_KeepsItsAppointments")]
    [TestCase("VEVENT", TestName = "GetAppointments_FeedWithAnEventThatHasNoEnd_KeepsItsAppointments")]
    public async Task GetAppointments_ComponentWithNoEnd_EndsWhereItStarts(string component)
    {
        // Arrange
        var start = DateTime.Today.AddHours(8);
        OnlyFeed(
            $"""
            BEGIN:{component}
            UID:plants@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:{Floating(start)}
            SUMMARY:Water the plants
            END:{component}
            """,
            $"""
            BEGIN:VEVENT
            UID:dentist@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:{Floating(DateTime.Today.AddHours(14))}
            DTEND:{Floating(DateTime.Today.AddHours(15))}
            SUMMARY:Dentist
            END:VEVENT
            """);

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointments.Select(a => (a.StartTime, a.EndTime)), Is.EquivalentTo(new[]
            {
                (start, start),
                (DateTime.Today.AddHours(14), DateTime.Today.AddHours(15))
            }));
            Assert.That(Errors(), Is.Empty);
        });
    }
}
