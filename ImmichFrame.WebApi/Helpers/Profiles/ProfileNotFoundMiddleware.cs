using ImmichFrame.Core.Exceptions;

namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// Answers 404 for a request whose configuration profile stopped existing while it was in flight.
/// <para>
/// <see cref="UnknownProfileMiddleware"/> already turns away a profile that was unknown when the
/// request arrived, and everything downstream resolves profiles on the assumption that it did. A
/// configuration swap breaks that assumption for the width of one request: a request admitted before
/// the swap, resolving its <see cref="ProfileServices"/> after it, asks for a profile the new
/// configuration no longer declares and gets a <see cref="ProfileNotFoundException"/> out of the
/// registry - thrown from the authentication handler or from controller activation, well outside
/// MVC's exception filters. Left alone it surfaces as a 500 for what is a 404.
/// </para>
/// <para>
/// Deliberately one exception type and one status code rather than a general problem-details layer:
/// every other domain exception in the tree means something went wrong, and blanket-mapping them
/// here would hide it.
/// </para>
/// </summary>
public class ProfileNotFoundMiddleware(RequestDelegate _next, ILogger<ProfileNotFoundMiddleware> _logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ProfileNotFoundException ex) when (!context.Response.HasStarted)
        {
            // Debug rather than Warning: the only way to get here is a configuration swap landing
            // mid-request, which is a race the administrator caused on purpose by saving.
            _logger.LogDebug("A request outlived its configuration profile ({errorMessage})", ex.Message);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("The requested configuration profile is not configured.");
        }
    }
}
