using ImmichFrame.Core.Helpers;
using NUnit.Framework;
using OpenWeatherMap.Models;
using UnitsNet;

namespace ImmichFrame.Core.Tests.Helpers;

[TestFixture]
public class WeatherExtensionsTests
{
    /// <summary>
    /// No weather from OpenWeatherMap is empty weather to the frame, rather than a failure.
    /// </summary>
    [Test]
    public void ToWeather_NoWeather_IsEmptyWeather()
    {
        // Act
        var weather = ((WeatherInfo?)null).ToWeather();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(weather.Location, Is.Empty);
            Assert.That(weather.Description, Is.Empty);
            Assert.That(weather.IconId, Is.Empty);
            Assert.That(weather.Temperature, Is.Zero);
            Assert.That(weather.Unit, Is.Empty);
        });
    }

    /// <summary>
    /// OpenWeatherMap can report more than one condition at once. Each is in the description and in
    /// the icon ids, joined by a bare comma: the frame shows the icon of the id before the first one.
    /// </summary>
    [Test]
    public void ToWeather_TwoConditions_AreJoinedByACommaAlongsideTheRest()
    {
        // Arrange
        var weatherInfo = new WeatherInfo
        {
            CityName = "Berlin",
            Weather =
            [
                new WeatherCondition { Description = "light rain", IconId = "10d" },
                new WeatherCondition { Description = "mist", IconId = "50d" }
            ],
            Main = new TemperatureInfo { Temperature = Temperature.FromDegreesCelsius(21.5) }
        };

        // Act
        var weather = weatherInfo.ToWeather();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(weather.Location, Is.EqualTo("Berlin"));
            Assert.That(weather.Description, Is.EqualTo("light rain,mist"));
            Assert.That(weather.IconId, Is.EqualTo("10d,50d"));
            Assert.That(weather.Temperature, Is.EqualTo(21.5));
            Assert.That(weather.Unit, Is.EqualTo("°C"));
        });
    }
}
