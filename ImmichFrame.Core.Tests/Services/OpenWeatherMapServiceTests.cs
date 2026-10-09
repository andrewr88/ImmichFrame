using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Language.Flow;
using Moq.Protected;
using NUnit.Framework;
using OpenWeatherMap.Models;

namespace ImmichFrame.Core.Tests.Services;

[TestFixture]
public class OpenWeatherMapServiceTests
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef";

    private Mock<IGeneralSettings> _settings;
    private ListLogger<OpenWeatherMapService> _logger;
    private Mock<OpenWeatherMapService> _sut;

    [SetUp]
    public void Setup()
    {
        _settings = new Mock<IGeneralSettings>();
        _settings.SetupGet(s => s.WeatherApiKey).Returns(ApiKey);
        _settings.SetupGet(s => s.WeatherLatLong).Returns("40.7128,-74.0060");

        _logger = new ListLogger<OpenWeatherMapService>();

        // The service runs for real and only its request to OpenWeatherMap is answered here - with
        // no body, which the service reports as empty weather rather than as no weather at all.
        _sut = new Mock<OpenWeatherMapService>(_settings.Object, _logger) { CallBase = true };
        OpenWeatherMap().ReturnsAsync((WeatherInfo)null!);
    }

    private ISetup<OpenWeatherMapService, Task<WeatherInfo>> OpenWeatherMap() =>
        _sut.Protected().Setup<Task<WeatherInfo>>("GetCurrentWeather", ItExpr.IsAny<double>(), ItExpr.IsAny<double>());

    private void VerifyOpenWeatherMapAsked(Times times) =>
        _sut.Protected().Verify("GetCurrentWeather", times, ItExpr.IsAny<double>(), ItExpr.IsAny<double>());

    private List<string> Warnings() =>
        _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

    [TestCase("40.7128")]           // no comma: Split(',')[1] threw IndexOutOfRangeException
    [TestCase("abc,def")]           // float.Parse threw FormatException
    [TestCase("40,7128,-74,0060")]  // decimal commas: read as latitude 40, longitude 7128
    [TestCase("NaN,-74.0060")]      // float.Parse accepts NaN as a number
    [TestCase("91,0")]              // no such latitude
    [TestCase("0,181")]             // no such longitude
    public async Task GetWeather_LatLongNotACoordinatePair_ReturnsNullAndWarns(string latLong)
    {
        // Arrange
        _settings.SetupGet(s => s.WeatherLatLong).Returns(latLong);

        // Act
        var weather = await _sut.Object.GetWeather();

        // Assert - no weather, rather than a 500 from every request until the setting is fixed, and
        // OpenWeatherMap is not asked about a place that does not exist
        Assert.Multiple(() =>
        {
            Assert.That(weather, Is.Null);
            Assert.That(Warnings(), Has.Count.EqualTo(1));
        });
        VerifyOpenWeatherMapAsked(Times.Never());
    }

    [Test]
    [SetCulture("de-DE")]
    public async Task GetWeather_CommaDecimalCulture_ReadsTheSettingWithAPoint()
    {
        // Arrange - under de-DE '.' is the group separator, so a culture-sensitive parse makes
        // 40.7128 into 407128
        _settings.SetupGet(s => s.WeatherLatLong).Returns("40.7128,-74.006");

        // Act
        var weather = await _sut.Object.GetWeather();

        // Assert
        Assert.That(weather, Is.Not.Null);
        _sut.Protected().Verify("GetCurrentWeather", Times.Once(), 40.7128, -74.006);
    }

    [Test]
    public async Task GetWeather_LongitudePastNinetyDegrees_IsAskedAbout()
    {
        // Arrange - a longitude runs to 180 degrees either way, where a latitude stops at 90
        _settings.SetupGet(s => s.WeatherLatLong).Returns("0,120");

        // Act
        await _sut.Object.GetWeather();

        // Assert
        _sut.Protected().Verify("GetCurrentWeather", Times.Once(), 0d, 120d);
        Assert.That(Warnings(), Is.Empty);
    }

    [TestCase(null)]
    [TestCase("  ")]
    public async Task GetWeather_LatLongBlank_AsksForZeroZero(string? latLong)
    {
        // Arrange
        _settings.SetupGet(s => s.WeatherLatLong).Returns(latLong);

        // Act
        await _sut.Object.GetWeather();

        // Assert - what a blank setting has always done
        _sut.Protected().Verify("GetCurrentWeather", Times.Once(), 0d, 0d);
        Assert.That(Warnings(), Is.Empty);
    }

    [Test]
    public async Task GetWeather_OpenWeatherMapFails_WarnsWithoutTheApiKey()
    {
        // Arrange - a failure quoting the request URL, which carries the key in its query string.
        // The library in use today never puts the URL in a message, but nothing promises that.
        OpenWeatherMap().ThrowsAsync(new HttpRequestException(
            $"GET https://api.openweathermap.org/data/2.5/weather?lat=40.7128&lon=-74.0060&appid={ApiKey} answered 401 (Unauthorized)"));

        // Act
        var weather = await _sut.Object.GetWeather();

        // Assert
        var warnings = Warnings();
        Assert.Multiple(() =>
        {
            Assert.That(weather, Is.Null);
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings.FirstOrDefault(), Does.Contain("HttpRequestException").And.Contain("401"));
            Assert.That(_logger.Entries.Select(e => e.Text), Has.None.Contains(ApiKey));
        });
    }

    /// <summary>
    /// A key set with whitespace around it - pasted with the line break it was copied with - is masked
    /// by the key itself, without the whitespace: that is what a message quoting the request carries,
    /// and the key as set, whitespace and all, matches nothing in it.
    /// </summary>
    [Test]
    public async Task GetWeather_ApiKeySetWithWhitespaceAndOpenWeatherMapFails_WarnsWithoutTheApiKey()
    {
        // Arrange
        _settings.SetupGet(s => s.WeatherApiKey).Returns($" {ApiKey} ");
        OpenWeatherMap().ThrowsAsync(new HttpRequestException(
            $"GET https://api.openweathermap.org/data/2.5/weather?lat=40.7128&lon=-74.0060&appid={ApiKey} answered 401 (Unauthorized)"));

        // Act
        var weather = await _sut.Object.GetWeather();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(weather, Is.Null);
            Assert.That(Warnings(), Has.One.Contains("HttpRequestException").And.Contains("401"));
            Assert.That(_logger.Entries.Select(e => e.Text), Has.None.Contains(ApiKey));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public async Task GetWeather_NoApiKey_ReturnsNullWithoutAskingOpenWeatherMap(string? apiKey)
    {
        // Arrange - weather is switched off, which is how a frame starts out; the clock asks for it
        // every ten minutes all the same
        _settings.SetupGet(s => s.WeatherApiKey).Returns(apiKey);

        // Act
        var weather = await _sut.Object.GetWeather();

        // Assert - OpenWeatherMap could only refuse a request without a key, and every refusal would
        // be a warning in the log of a frame that never asked for weather
        Assert.Multiple(() =>
        {
            Assert.That(weather, Is.Null);
            Assert.That(Warnings(), Is.Empty);
        });
        VerifyOpenWeatherMapAsked(Times.Never());
    }
}
