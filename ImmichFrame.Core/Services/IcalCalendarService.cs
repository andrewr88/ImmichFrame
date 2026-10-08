using System.Net.Http.Headers;
using System.Text;
using Ical.Net;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Services;

public class IcalCalendarService : ICalendarService
{
    private readonly IGeneralSettings _serverSettings;
    private readonly ILogger<IcalCalendarService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApiCache _appointmentCache = new(TimeSpan.FromMinutes(15));

    public IcalCalendarService(IGeneralSettings serverSettings, ILogger<IcalCalendarService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _serverSettings = serverSettings;
        _httpClientFactory = httpClientFactory;
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

                    // Expanded in full before any of it is added, so a feed that fails partway through
                    // contributes nothing rather than some of its day.
                    appointments.AddRange(calendar.GetOccurrences(DateTime.Today, DateTime.Today.AddDays(1)).Select(x => x.ToAppointment()).ToList());
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
        var client = _httpClientFactory.CreateClient();

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