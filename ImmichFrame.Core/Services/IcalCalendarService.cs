using System.Net.Http.Headers;
using System.Text;
using Ical.Net;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Services;

public class IcalCalendarService : ICalendarService
{
    /// <summary>
    /// The named <c>HttpClient</c> feeds are fetched on, configured in <c>Program.cs</c> without the
    /// factory's request logging. Once LOG_LEVEL is Debug or lower that logging writes each request's
    /// full URL - the access token a Google or iCloud feed carries in its path included - and at Trace
    /// every request header, a feed's Basic credential among them. <see cref="RedactUrl"/> cannot reach
    /// a line this class does not write.
    /// </summary>
    public const string HttpClientName = "CalendarClient";

    /// <summary>
    /// How long fetching one feed may take, set on <see cref="HttpClientName"/>. Feeds are fetched one
    /// after another while every frame's request for the calendar waits on the load, so a feed that
    /// accepts the connection and never finishes answering held them all for HttpClient's default of
    /// a hundred seconds.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most of one feed that will be read, set on <see cref="HttpClientName"/>. Room for years of a
    /// busy calendar, which a feed carries whole; without a cap HttpClient would buffer up to two
    /// gigabytes of whatever a feed's host chose to send. A feed over it fails on its own, as an
    /// unreachable one does, and says so in the log.
    /// </summary>
    public const long MaxFeedBytes = 10L * 1024 * 1024;

    /// <summary>
    /// The most of a day's occurrences one feed contributes. A frame shows a handful, but a series can
    /// recur every minute, or every second of every minute - 86,400 a day for FREQ=MINUTELY with every
    /// second in BYSECOND - and each one was kept in the cache and sent to every frame on every poll.
    /// <para>
    /// It bounds what is kept and served, not what is worked out: Ical.Net 4.3.1's GetOccurrences
    /// expands the whole day into a set before handing any of it back, so the cap applies only after.
    /// </para>
    /// </summary>
    private const int MaxOccurrencesPerFeed = 1000;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly IGeneralSettings _serverSettings;
    private readonly ILogger<IcalCalendarService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApiCache _appointmentCache = new(() => new MemoryCacheEntryOptions
    {
        AbsoluteExpiration = ExpiryFor(DateTimeOffset.Now)
    });

    public IcalCalendarService(IGeneralSettings serverSettings, ILogger<IcalCalendarService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _serverSettings = serverSettings;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// When appointments loaded at <paramref name="now"/> stop being served: <see cref="RefreshInterval"/>
    /// later, or at the midnight that ends <paramref name="now"/>'s day if that comes first. They are
    /// that day's appointments, so a load at ten to midnight went on serving yesterday's for five
    /// minutes of today. In local time, as <c>DateTimeOffset.Now</c> is, and taken apart from the
    /// options - as <c>DailyApiCache.ExpiryFor</c> is - so that it can be checked against fixed times.
    /// </summary>
    internal static DateTimeOffset ExpiryFor(DateTimeOffset now)
    {
        DateTimeOffset midnight = now.Date.AddDays(1);
        var refresh = now + RefreshInterval;

        return refresh < midnight ? refresh : midnight;
    }

    public async Task<List<IAppointment>> GetAppointments()
    {
        return await _appointmentCache.GetOrAddAsync("appointments", async () =>
        {
            var appointments = new List<IAppointment>();

            List<(string? auth, string url)> cals = _serverSettings.Webcalendars.Select<string, (string? auth, string url)?>(x =>
            {
                try
                {
                    var uri = new Uri(x.Replace("webcal://", "https://"));
                    if (!string.IsNullOrEmpty(uri.UserInfo))
                    {
                        var url = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
                        return (Uri.UnescapeDataString(uri.UserInfo), url);
                    }
                    return (null, x);
                }
                catch (UriFormatException)
                {
                    _logger.LogError("Invalid calendar URL: '{redactedUrl}'", RedactUrl(x));
                    return null;
                }
            }).Where(x => x != null).Select(x => x!.Value).ToList();

            var icals = await GetCalendars(cals);

            // Once, for every feed: read per feed, or per bound, a poll running across midnight asked one
            // feed for yesterday and the next for today - or one feed for two days at once.
            var today = DateTime.Today;

            // Feed by feed, like the fetch: one that is not a calendar - typically a provider's sign-in
            // page, served with a 200 once a session or a shared link has expired - costs its own
            // appointments, rather than failing this factory and with it every calendar on every poll.
            foreach (var (url, ical) in icals)
            {
                try
                {
                    var calendar = Calendar.Load(ical);
                    if (calendar == null)
                    {
                        // Load's answer to a body that parses but holds no calendar - an empty 200, for one.
                        _logger.LogError("Calendar data from '{redactedUrl}' holds no calendar", RedactUrl(url));
                        continue;
                    }

                    var occurrences = calendar.GetOccurrencesOn(today);
                    if (occurrences.Count > MaxOccurrencesPerFeed)
                    {
                        _logger.LogWarning("Calendar data from '{redactedUrl}' has {occurrenceCount} appointments today; only the first {maxOccurrences} are shown",
                            RedactUrl(url), occurrences.Count, MaxOccurrencesPerFeed);
                    }

                    // Expanded in full before any of it is added, so a feed that fails partway through
                    // contributes nothing rather than some of its day. The earliest are kept, which is the
                    // order the frame lists them in, so a runaway series that starts later in the day
                    // leaves the feed's other events where they were.
                    appointments.AddRange(occurrences
                        .OrderBy(occurrence => occurrence.Period.StartTime.AsUtc)
                        .Take(MaxOccurrencesPerFeed)
                        .Select(x => x.ToAppointment())
                        .ToList());
                }
                catch (Exception ex)
                {
                    // The type alone: Ical.Net's messages quote the line they could not parse, which is
                    // whatever the far end sent - for a minified sign-in page, the whole page.
                    _logger.LogError("Failed to read calendar data from '{redactedUrl}' ({exceptionType})", RedactUrl(url), ex.GetType().Name);
                }
            }

            return appointments;
        });
    }

    public async Task<List<(string url, string ical)>> GetCalendars(IEnumerable<(string? auth, string url)> calendars)
    {
        var icals = new List<(string url, string ical)>();
        var client = _httpClientFactory.CreateClient(HttpClientName);

        foreach (var calendar in calendars)
        {
            _logger.LogDebug("Loading calendar: {authentication} - {redactedUrl}", calendar.auth != null ? "[authenticated]" : "no auth", RedactUrl(calendar.url));

            string httpUrl = calendar.url.Replace("webcal://", "https://");

            // A feed that cannot be fetched at all - DNS, a refused connection, a timeout, a scheme
            // HttpClient does not speak - is skipped and logged the way an error status already is.
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, httpUrl);

                if (!string.IsNullOrEmpty(calendar.auth))
                {
                    var byteArray = Encoding.UTF8.GetBytes(calendar.auth);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                }

                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    icals.Add((httpUrl, await response.Content.ReadAsStringAsync()));
                }
                else
                {
                    _logger.LogError("Failed to load calendar data from '{redactedUrl}' (Status: {status})", RedactUrl(httpUrl), response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                // Sanitized, as AdminImmichController does with the same kind of message: a transport
                // failure can quote what the far end sent, an unparseable status line for one.
                _logger.LogError("Failed to load calendar data from '{redactedUrl}': {sanitizedReason}", RedactUrl(httpUrl), ex.Message.SanitizeString());
            }
        }

        return icals;
    }

    /// <summary>
    /// A calendar URL as it may appear in the log: its scheme, host and any non-default port, and
    /// nothing else.
    /// <para>
    /// The rest of a calendar URL is a credential more often than not. user:password@ is how a feed
    /// is configured for Basic authentication, and Google's "secret address" and iCloud's published
    /// calendars carry their access token in the path - which is why audit/THREAT_MODEL.md keeps
    /// calendar URLs with the API keys. The host is all an operator needs to tell which feed a
    /// message is about.
    /// </para>
    /// </summary>
    private static string RedactUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : "(unparseable URL)";
}