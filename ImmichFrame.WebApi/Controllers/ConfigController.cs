using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConfigController : ControllerBase
    {
        private readonly ILogger<AssetController> _logger;
        private readonly IClientSettings _settings;

        public ConfigController(ILogger<AssetController> logger, IClientSettings settings)
        {
            _logger = logger;
            _settings = settings;
        }

        // 'profile' exists on GetConfig only so Swashbuckle emits it as a query parameter
        // and the generated client can send it. It is deliberately never read here: DI has
        // already resolved the profile from the query string before the action body runs,
        // and a second resolution path could silently diverge. GetVersion takes none - no
        // profile can vary the assembly version.
        [HttpGet(Name = "GetConfig")]
        public ClientSettingsDto GetConfig(string clientIdentifier = "", string profile = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Config requested by '{sanitizedClientIdentifier}'", sanitizedClientIdentifier);
            return new ClientSettingsDto(_settings);
        }

        [HttpGet("Version", Name = "GetVersion")]
        public string GetVersion()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
    }
}