using System.Globalization;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Services;

public class OpenWeatherMapService : IWeatherService
{
    private readonly IGeneralSettings _settings;
    private readonly ILogger<OpenWeatherMapService> _logger;
    private readonly IApiCache _weatherCache = new ApiCache(TimeSpan.FromMinutes(5));
    public OpenWeatherMapService(IGeneralSettings settings, ILogger<OpenWeatherMapService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<IWeather?> GetWeather()
    {
        return await _weatherCache.GetOrAddAsync("weather", async () =>
        {
            var weatherLatLong = _settings.WeatherLatLong;

            // A blank setting has always meant 0,0 rather than no weather, and still does. Anything
            // else is checked rather than trusted: this runs inside the cache's factory, which caches
            // nothing when it throws, so a value that could not be parsed failed every request for
            // weather with a 500 until the setting was fixed.
            double weatherLat = 0, weatherLong = 0;
            if (!string.IsNullOrWhiteSpace(weatherLatLong) && !TryParseLatLong(weatherLatLong, out weatherLat, out weatherLong))
            {
                // The value itself stays out of the log: it is where the frame hangs, which is most
                // often someone's home, and logs are what gets pasted into a bug report.
                _logger.LogWarning("WeatherLatLong is not a 'latitude,longitude' pair in decimal degrees, such as '40.7128,-74.0060'; no weather will be shown");
                return null;
            }

            var weather = await GetWeather(weatherLat, weatherLong);

            return weather;
        });
    }

    private async Task<IWeather?> GetWeather(double latitude, double longitude)
    {
        var apiKey = _settings.WeatherApiKey;

        // Weather is switched on by setting an API key, and the clock asks for it whether or not one
        // is set. Without a key OpenWeatherMap can only answer 401, so it is not asked at all - which
        // also keeps the warning below out of the log of every frame that never wanted weather.
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            var weatherInfo = await GetCurrentWeather(latitude, longitude);

            return weatherInfo.ToWeather();
        }
        catch (Exception ex)
        {
            // The type and the message, and never the exception itself, whose inner exceptions the
            // mask below would not reach: OpenWeatherMap takes the API key in its query string, so
            // the request URL is as secret as the key. No failure OpenWeatherMap.API 2.1.2 raises
            // here quotes it - HttpClient names a host and port at most, EnsureSuccessStatusCode the
            // status, Newtonsoft a position in the JSON - and the key is masked all the same, so a
            // later version of the library that does quote its URL still leaves the key out.
            _logger.LogWarning("Could not get the weather from OpenWeatherMap: {exceptionType}: {reason}",
                ex.GetType().Name, ex.Message.Replace(apiKey.Trim(), "***", StringComparison.Ordinal));
        }

        return null;
    }

    /// <summary>
    /// Reads the setting's "latitude,longitude" in decimal degrees.
    /// <para>
    /// In the invariant culture, because the setting is written with a '.' for the decimal point
    /// whatever the server's locale: under de-DE, say, '.' is the group separator, and a
    /// culture-sensitive parse makes 40.7128 into 407128. The range check turns away NaN and the
    /// infinities - which parse as numbers - as well as coordinates that cannot exist: NaN fails
    /// every comparison, and an infinity is outside every range.
    /// </para>
    /// </summary>
    private static bool TryParseLatLong(string value, out double latitude, out double longitude)
    {
        latitude = longitude = 0;

        var parts = value.Split(',');
        return parts.Length == 2
            && TryParseDegrees(parts[0], 90, out latitude)
            && TryParseDegrees(parts[1], 180, out longitude);
    }

    private static bool TryParseDegrees(string value, double limit, out double degrees) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out degrees)
        && degrees >= -limit && degrees <= limit;

    /// <summary>
    /// Asks OpenWeatherMap for the current weather at a point - the one call in here that leaves the
    /// process, and virtual so that a test can answer it in OpenWeatherMap's place.
    /// </summary>
    protected virtual Task<OpenWeatherMap.Models.WeatherInfo> GetCurrentWeather(double latitude, double longitude)
    {
        OpenWeatherMap.OpenWeatherMapOptions options = new OpenWeatherMap.OpenWeatherMapOptions
        {
            ApiKey = _settings.WeatherApiKey,
            UnitSystem = _settings.UnitSystem,
            Language = _settings.Language,
        };

        OpenWeatherMap.IOpenWeatherMapService openWeatherMapService = new OpenWeatherMap.OpenWeatherMapService(options);
        return openWeatherMapService.GetCurrentWeatherAsync(latitude, longitude);
    }
}