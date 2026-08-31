using ImmichFrame.Core.Helpers;
using ImmichFrame.WebApi.Helpers.Config;

namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// Rejects a request naming a configuration profile that does not exist, before anything
/// downstream tries to resolve it.
/// <para>
/// Without this the first thing to ask for the profile - often the authentication handler, well
/// before any controller - would throw and the client would see a 500 for what is really a bad
/// request. Turning it away here also means everything downstream can resolve the profile
/// without handling failure.
/// </para>
/// </summary>
public class UnknownProfileMiddleware(RequestDelegate _next, ILogger<UnknownProfileMiddleware> _logger)
{
    public async Task InvokeAsync(HttpContext context, IConfigCatalog catalog)
    {
        var requested = context.Request.Query[CurrentProfile.QueryParameterName].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(requested) && !catalog.TryGet(requested, out _))
        {
            // Sanitized, and only to the log: echoing an arbitrary query value straight back into
            // the response body is not worth the convenience.
            _logger.LogWarning("Request for unknown configuration profile '{profile}'", requested.SanitizeString());

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("The requested configuration profile is not configured.");
            return;
        }

        await _next(context);
    }
}
