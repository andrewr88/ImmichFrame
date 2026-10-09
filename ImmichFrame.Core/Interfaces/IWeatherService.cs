namespace ImmichFrame.Core.Interfaces;

public interface IWeatherService
{
    Task<IWeather?> GetWeather();
}