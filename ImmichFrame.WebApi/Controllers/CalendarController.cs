using ImmichFrame.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class CalendarController : ControllerBase
    {
        private readonly ILogger<AssetController> _logger;
        private readonly ICalendarService _calendarService;

        public CalendarController(ILogger<AssetController> logger, ICalendarService calendarService)
        {
            _logger = logger;
            _calendarService = calendarService;
        }

        // 'profile' exists on the actions below only so Swashbuckle emits it as a query
        // parameter and the generated client can send it. It is deliberately never read
        // here: DI has already resolved the profile from the query string before an
        // action body runs, and a second resolution path could silently diverge.
        [HttpGet(Name = "GetAppointments")]
        public async Task<List<IAppointment>> GetAppointments(string clientIdentifier = "", string profile = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Calendar requested by '{sanitizedClientIdentifier}'", sanitizedClientIdentifier);
            return await _calendarService.GetAppointments();
        }
    }
}
