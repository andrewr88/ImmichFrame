using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
    private Mock<IHttpClientFactory> _httpClientFactory;
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
        _httpClientFactory = new Mock<IHttpClientFactory>();
        _httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_network.Object));

        _sut = new IcalCalendarService(_settings.Object, _logger, _httpClientFactory.Object);

        HostServes("good", () => Ok(DailyCalendar));
    }

    private void Calendars(params string[] urls) =>
        _settings.SetupGet(s => s.Webcalendars).Returns(urls.ToList());

    private ISetup<HttpMessageHandler, Task<HttpResponseMessage>> Host(string host) =>
        _network.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == host), ItExpr.IsAny<CancellationToken>());

    private void HostServes(string host, Func<HttpResponseMessage> response) => Host(host).ReturnsAsync(response);

    // What the service sent host, request by request: the URL and the Authorization header, read as
    // they arrive, since the message itself is disposed once the service is done with it.
    private List<(Uri Url, AuthenticationHeaderValue? Authorization)> RequestsTo(string host, Func<HttpResponseMessage> response)
    {
        var requests = new List<(Uri, AuthenticationHeaderValue?)>();
        Host(host)
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => requests.Add((request.RequestUri!, request.Headers.Authorization)))
            .ReturnsAsync(response);

        return requests;
    }

    private void HostFails(string host, Exception failure) => Host(host).ThrowsAsync(failure);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private List<string> Errors() =>
        _logger.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();

    private List<string> Warnings() =>
        _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

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

    /// <summary>
    /// A zone whose clock is at least an hour away from this machine's at every one of
    /// <paramref name="instants"/> - New York's, or Tokyo's on a machine keeping something near New
    /// York's time - so that a test writing a time in it cannot pass by the two clocks agreeing,
    /// whatever zone the suite runs in. The two are thirteen hours apart or more, so one always is.
    /// </summary>
    private static TimeZoneInfo ZoneOtherThanLocalAt(params DateTime[] instants) =>
        new[] { "America/New_York", "Asia/Tokyo" }
            .Select(TimeZoneInfo.FindSystemTimeZoneById)
            .First(zone => instants.All(instant =>
                (zone.GetUtcOffset(instant) - TimeZoneInfo.Local.GetUtcOffset(instant)).Duration() >= TimeSpan.FromHours(1)));

    // A time on this machine's clock, as the clock in zone reads at that moment, written the way a
    // feed writes a time it gives a zone of its own.
    private static string InZone(TimeZoneInfo zone, DateTime local) =>
        $"TZID={zone.Id}:{Floating(TimeZoneInfo.ConvertTime(local, TimeZoneInfo.Local, zone))}";

    [TestCase("<html><body>Login page</body></html>", "Failed to read calendar data from 'https://bad'")] // Calendar.Load throws on it
    [TestCase("", "Calendar data from 'https://bad' holds no calendar")]                                    // Calendar.Load returns null for it
    public async Task GetAppointments_OneFeedServesSomethingElse_ReturnsTheOtherFeedsAppointments(string body, string error)
    {
        // Arrange - a feed answering 200 with something that is not a calendar, as a provider's
        // sign-in page does once a session or a shared link has expired
        Calendars("https://good/a.ics", "https://bad/private-token/b.ics");
        HostServes("bad", () => Ok(body));

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert - the bad feed costs only its own appointments, and is named in the log by host, saying
        // which of the two it was: a body that would not parse, or one that parsed and held nothing
        Assert.Multiple(() =>
        {
            Assert.That(appointments.Select(a => a.Summary), Is.EqualTo(new[] { "Daily check-in" }));
            Assert.That(Errors(), Has.One.StartsWith(error));
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

    /// <summary>
    /// A feed that takes the connection and never answers is given up on when the client's timeout
    /// runs out - set where the client is registered - and costs only its own appointments. HttpClient
    /// reports its timeout as a TaskCanceledException rather than an HttpRequestException, so a fetch
    /// that caught only the latter would let this one through to fail every calendar.
    /// </summary>
    [Test]
    public async Task GetAppointments_OneFeedTooSlow_ReturnsTheOtherFeedsAppointments()
    {
        // Arrange - the timeout shortened, so this waits on nothing like the real one. The slow feed
        // gives up on its own after ten seconds, with an appointment of its own, so that a client that
        // waited it out fails this test rather than hanging it.
        _httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_network.Object) { Timeout = TimeSpan.FromMilliseconds(100) });
        Calendars("https://good/a.ics", "https://slow/private-token/b.ics");
        Host("slow").Returns<HttpRequestMessage, CancellationToken>(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return Ok(DailyCalendar.Replace("Daily check-in", "Too late"));
        });

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointments.Select(a => a.Summary), Is.EqualTo(new[] { "Daily check-in" }));
            Assert.That(Errors(), Has.One.Contains("'https://slow'"));
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
    /// user:password@ in a feed's URL is how a feed is given Basic authentication. The credential goes
    /// out in the Authorization header - unescaped, since a URL can carry an @ or a : in a user name or
    /// password only percent-encoded - and not in the URL the request is sent to.
    /// </summary>
    [TestCase("https://u:p@host/x", "u:p")]
    [TestCase("https://user%40x:p%3Aq@host/x", "user@x:p:q")]
    public async Task GetAppointments_FeedUrlWithUserInfo_SendsItAsABasicCredentialOnly(string url, string credential)
    {
        // Arrange
        Calendars(url);
        var requests = RequestsTo("host", () => Ok(DailyCalendar));

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        var request = requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.Authorization?.Scheme, Is.EqualTo("Basic"));
            Assert.That(request.Authorization?.Parameter, Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes(credential))));
            Assert.That(request.Url.UserInfo, Is.Empty);
            Assert.That(appointments.Select(a => a.Summary), Is.EqualTo(new[] { "Daily check-in" }));
        });
    }

    /// <summary>
    /// webcal:// is how a calendar link asks to be subscribed to rather than downloaded. It is no
    /// protocol of its own: the feed is fetched over HTTPS.
    /// </summary>
    [Test]
    public async Task GetAppointments_WebcalFeed_IsFetchedOverHttps()
    {
        // Arrange
        Calendars("webcal://feed/calendar.ics");
        var requests = RequestsTo("feed", () => Ok(DailyCalendar));

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(requests.Select(r => r.Url.Scheme), Is.EqualTo(new[] { "https" }));
            Assert.That(appointments.Select(a => a.Summary), Is.EqualTo(new[] { "Daily check-in" }));
        });
    }

    /// <summary>
    /// A feed answering with an error status is not read, whatever its body holds - a calendar
    /// included - and the log says which status it was.
    /// </summary>
    [Test]
    public async Task GetAppointments_FeedAnswersWithAnErrorStatus_IsNotReadAndItsStatusIsLogged()
    {
        // Arrange - a calendar for a body, so that reading it regardless would show
        Calendars("https://good/a.ics", "https://bad/b.ics");
        HostServes("bad", () => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(DailyCalendar.Replace("Daily check-in", "From an error page"))
        });

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointments.Select(a => a.Summary), Is.EqualTo(new[] { "Daily check-in" }));
            Assert.That(Errors(), Has.One.Contains("'https://bad'").And.Contains("InternalServerError"));
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

    /// <summary>
    /// An all-day event with neither DTEND nor DURATION lasts its one day (RFC 5545). Ical.Net gives
    /// its occurrence a day's duration but an end equal to its start, so it ended the moment it began
    /// and the frame printed it as 00:00 - 00:00, where one with a DTEND reads as the whole day.
    /// </summary>
    [Test]
    public async Task GetAppointments_AllDayEventWithNoEnd_LastsTheDay()
    {
        // Arrange
        OnlyFeed($"""
            BEGIN:VEVENT
            UID:holiday@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART;VALUE=DATE:{Day(DateTime.Today)}
            SUMMARY:Bank holiday
            END:VEVENT
            """);

        // Act
        var appointment = (await _sut.GetAppointments()).Single();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointment.StartTime, Is.EqualTo(DateTime.Today));
            Assert.That(appointment.EndTime, Is.EqualTo(DateTime.Today.AddDays(1)));
            Assert.That(appointment.Duration, Is.EqualTo(TimeSpan.FromDays(1)));
        });
    }

    [Test]
    public async Task GetAppointments_EventAtMidnightWithNoEnd_EndsWhereItStarts()
    {
        // Arrange - a time of day that happens to be midnight, which Ical.Net gives a day's
        // duration as it does a date; RFC 5545 gives an event with a time of day and no end none
        var start = DateTime.Today;
        OnlyFeed($"""
            BEGIN:VEVENT
            UID:midnight@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:{Floating(start)}
            SUMMARY:New day
            END:VEVENT
            """);

        // Act
        var appointment = (await _sut.GetAppointments()).Single();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointment.StartTime, Is.EqualTo(start));
            Assert.That(appointment.EndTime, Is.EqualTo(start));
            Assert.That(appointment.Duration, Is.EqualTo(TimeSpan.Zero));
        });
    }

    /// <summary>
    /// A time written in a zone of its own - DTSTART;TZID=..., the usual way a recurring event is
    /// written - names a moment, and the frame shows that moment. Read as a wall-clock time instead, a
    /// call at nine in New York was shown at nine wherever the frame hung.
    /// </summary>
    [Test]
    public async Task GetAppointments_EventWrittenInAnotherTimeZone_IsTimedByTheServersClock()
    {
        // Arrange - noon here, written as the same moment on a clock that reads otherwise
        var start = DateTime.Today.AddHours(12);
        var end = start.AddHours(1);
        var zone = ZoneOtherThanLocalAt(start, end);
        OnlyFeed($"""
            BEGIN:VEVENT
            UID:call@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART;{InZone(zone, start)}
            DTEND;{InZone(zone, end)}
            SUMMARY:Call with the other office
            END:VEVENT
            """);

        // Act
        var appointment = (await _sut.GetAppointments()).Single();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(appointment.StartTime, Is.EqualTo(start));
            Assert.That(appointment.EndTime, Is.EqualTo(end));
            Assert.That(appointment.Duration, Is.EqualTo(TimeSpan.FromHours(1)));
        });
    }

    /// <summary>
    /// Today is this machine's day, whatever zone a feed writes its times in. Ical.Net reads the day it
    /// is asked for in the zone of each event's own DTSTART - it relabels the two midnights with that
    /// zone rather than converting them - so it answered with that zone's day: a frame in Los Angeles,
    /// where the UTC day ends in the late afternoon, lost every event written in UTC after that and was
    /// shown the previous evening's in their place.
    /// </summary>
    [Test]
    public async Task GetAppointments_EventsWrittenInAnotherTimeZoneNearMidnight_AreTodaysByTheServersClock()
    {
        // Arrange - half an hour either side of each of today's two midnights on this machine's clock.
        // Written in a zone an hour or more away, each falls on the other side of that zone's midnight.
        var today = DateTime.Today;
        var times = new Dictionary<string, DateTime>
        {
            ["Late last night"] = today.AddMinutes(-30),
            ["Early this morning"] = today.AddMinutes(30),
            ["Late tonight"] = today.AddDays(1).AddMinutes(-30),
            ["Early tomorrow morning"] = today.AddDays(1).AddMinutes(30)
        };
        var zone = ZoneOtherThanLocalAt([.. times.Values]);
        OnlyFeed([.. times.Select(time => $"""
            BEGIN:VEVENT
            UID:{time.Key.Replace(' ', '-')}@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART;{InZone(zone, time.Value)}
            DTEND;{InZone(zone, time.Value.AddMinutes(10))}
            SUMMARY:{time.Key}
            END:VEVENT
            """)]);

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        Assert.That(appointments.Select(a => (a.Summary, a.StartTime)), Is.EquivalentTo(new[]
        {
            ("Early this morning", times["Early this morning"]),
            ("Late tonight", times["Late tonight"])
        }));
    }

    /// <summary>
    /// An all-day event names a day rather than a moment, and is today's by its date - even written with
    /// a TZID, which RFC 5545 does not allow on a date but which a feed may carry all the same. Taken
    /// for a moment in that zone, today would reach into the end of yesterday's all-day event there, or
    /// the start of tomorrow's.
    /// </summary>
    [Test]
    public async Task GetAppointments_AllDayEventsWrittenWithATimeZone_AreTodaysByTheirDate()
    {
        // Arrange - yesterday, today and tomorrow, each dated in a zone an hour or more away
        var today = DateTime.Today;
        var zone = ZoneOtherThanLocalAt(today, today.AddDays(1));
        OnlyFeed([.. new[] { -1, 0, 1 }.Select(days => $"""
            BEGIN:VEVENT
            UID:day{days}@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART;TZID={zone.Id};VALUE=DATE:{Day(today.AddDays(days))}
            DTEND;TZID={zone.Id};VALUE=DATE:{Day(today.AddDays(days + 1))}
            SUMMARY:Day {days}
            END:VEVENT
            """)]);

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        Assert.That(appointments.Select(a => (a.Summary, a.StartTime)), Is.EqualTo(new[] { ("Day 0", today) }));
    }

    /// <summary>
    /// One instance of a series moved, which a feed writes as the series plus a component with the same
    /// UID whose RECURRENCE-ID names the instance it replaces. The frame shows the instance where it was
    /// moved to, and not also where it used to be.
    /// </summary>
    [Test]
    public async Task GetAppointments_InstanceOfASeriesMovedToday_IsShownOnlyWhereItWasMovedTo()
    {
        // Arrange - a stand-up every day at nine since a fortnight ago, today's moved to eleven
        var firstDay = DateTime.Today.AddDays(-14);
        OnlyFeed(
            $"""
            BEGIN:VEVENT
            UID:stand-up@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:{Floating(firstDay.AddHours(9))}
            DURATION:PT15M
            RRULE:FREQ=DAILY
            SUMMARY:Stand-up
            END:VEVENT
            """,
            $"""
            BEGIN:VEVENT
            UID:stand-up@immichframe.test
            DTSTAMP:20240101T000000Z
            RECURRENCE-ID:{Floating(DateTime.Today.AddHours(9))}
            DTSTART:{Floating(DateTime.Today.AddHours(11))}
            DURATION:PT15M
            SUMMARY:Stand-up
            END:VEVENT
            """);

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert
        Assert.That(appointments.Select(a => a.StartTime), Is.EqualTo(new[] { DateTime.Today.AddHours(11) }));
    }

    /// <summary>
    /// A series recurring far more often than anything a frame can show - FREQ=MINUTELY with every
    /// second listed in BYSECOND is 86,400 a day - is cut off at a thousand rather than kept, and sent
    /// to every frame on every poll, whole. The cut keeps the day's earliest, the order the frame lists
    /// them in, so the feed's other events are still shown.
    /// </summary>
    [Test]
    public async Task GetAppointments_FeedWithARunawaySeries_IsCutOffAndKeepsItsOtherEvents()
    {
        // Arrange - twice a minute from noon, 1,440 times by midnight. Listed ahead of the dentist at
        // nine, so a cut that kept whatever came first rather than the earliest would lose the dentist.
        OnlyFeed(
            $"""
            BEGIN:VEVENT
            UID:runaway@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:{Floating(DateTime.Today.AddHours(12))}
            RRULE:FREQ=MINUTELY;BYSECOND=0,30
            SUMMARY:Runaway
            END:VEVENT
            """,
            $"""
            BEGIN:VEVENT
            UID:dentist@immichframe.test
            DTSTAMP:20240101T000000Z
            DTSTART:{Floating(DateTime.Today.AddHours(9))}
            DTEND:{Floating(DateTime.Today.AddHours(10))}
            SUMMARY:Dentist
            END:VEVENT
            """);

        // Act
        var appointments = await _sut.GetAppointments();

        // Assert - and the warning names the feed by its host alone, as every other line does
        Assert.Multiple(() =>
        {
            Assert.That(appointments, Has.Count.EqualTo(1000));
            Assert.That(appointments.Select(a => a.Summary), Has.One.EqualTo("Dentist"));
            Assert.That(Warnings(), Has.One.Contains("'https://feed'"));
            Assert.That(Logged(), Has.None.Contains("calendar.ics"));
        });
    }

    [TestCase("12:00:00", "12:15:00", TestName = "ExpiryFor_MidDay_IsAQuarterOfAnHourLater")]
    [TestCase("00:00:00", "00:15:00", TestName = "ExpiryFor_ExactlyMidnight_IsAQuarterOfAnHourLater")]
    [TestCase("23:45:00", "1.00:00:00", TestName = "ExpiryFor_AQuarterToMidnight_IsMidnight")]
    [TestCase("23:50:00", "1.00:00:00", TestName = "ExpiryFor_TenToMidnight_IsMidnightNotAQuarterOfAnHourLater")]
    public void ExpiryFor_TimeOfDay_IsAQuarterOfAnHourLaterOrMidnightIfThatIsSooner(TimeSpan timeOfDay, TimeSpan expected)
    {
        // Arrange - a local time, as DateTimeOffset.Now gives one. The appointments are the day's, so
        // a load at ten to midnight cannot be served for a quarter of an hour: five minutes of it would
        // be yesterday's list shown today
        var day = new DateTime(2026, 6, 10);
        var now = new DateTimeOffset(day + timeOfDay);

        // Act
        var expiry = IcalCalendarService.ExpiryFor(now);

        // Assert
        Assert.That(expiry, Is.EqualTo(new DateTimeOffset(day + expected)));
    }

    [Test]
    public async Task GetAppointments_AgainWithinTheQuarterHour_DoesNotFetchTheFeedsAgain()
    {
        // Arrange - through the service's own cache, so that it is the expiry the service actually sets
        // that is tested. A run that straddles midnight would rightly fetch twice.
        Assume.That(DateTime.Now.TimeOfDay, Is.LessThan(new TimeSpan(23, 59, 50)),
            "too close to midnight, when the day's appointments rightly expire");
        Calendars("https://good/a.ics");

        // Act
        await _sut.GetAppointments();
        await Task.Delay(50);
        await _sut.GetAppointments();

        // Assert
        _network.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host == "good"), ItExpr.IsAny<CancellationToken>());
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
