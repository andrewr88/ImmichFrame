using ImmichFrame.WebApi.Helpers.Admin;
using Microsoft.AspNetCore.Authentication;

public class CustomAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public CustomAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // This middleware authenticates every request against ImmichFrameScheme and 401s on failure,
        // ahead of the endpoint's own [Authorize]. On an installation with an AuthenticationSecret
        // that would reject the administrator's browser - which carries a cookie, not the frame's
        // bearer token - and the identity provider's redirect back to us, before the cookie or
        // OpenID Connect schemes ever ran. AdminAuthentication owns the list of paths that skip it.
        if (AdminAuthentication.IsAdminPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var result = await context.AuthenticateAsync("ImmichFrameScheme");

        if (!result.Succeeded)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(result.Failure?.Message ?? "Unauthorized");
            return;
        }

        await _next(context);
    }
}
