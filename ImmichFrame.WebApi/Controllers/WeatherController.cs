using ImmichFrame.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class WeatherController : ControllerBase
    {
        private readonly ILogger<AssetController> _logger;
        private readonly IWeatherService _weatherService;

        public WeatherController(ILogger<AssetController> logger, IWeatherService weatherService)
        {
            _logger = logger;
            _weatherService = weatherService;
        }

        // 'profile' exists on the actions below only so Swashbuckle emits it as a query
        // parameter and the generated client can send it. It is deliberately never read
        // here: DI has already resolved the profile from the query string before an
        // action body runs, and a second resolution path could silently diverge.
        [HttpGet(Name = "GetWeather")]
        public async Task<IWeather?> GetWeather(string clientIdentifier = "", string profile = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Weather requested by '{sanitizedClientIdentifier}'", sanitizedClientIdentifier);
            return await _weatherService.GetWeather();
        }
    }
}
