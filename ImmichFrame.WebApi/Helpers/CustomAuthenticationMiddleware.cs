using ImmichFrame.WebApi.Helpers.Admin;
using Microsoft.AspNetCore.Authentication;

public class CustomAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public CustomAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AdminOidcOptions adminOptions)
    {
        // This middleware authenticates every request against ImmichFrameScheme and 401s on failure,
        // ahead of the endpoint's own [Authorize]. On an installation with an AuthenticationSecret
        // that would reject the administrator's browser - which carries a cookie, not the frame's
        // bearer token - and the identity provider's redirect back to us, before the cookie or
        // OpenID Connect schemes ever ran. AdminAuthentication owns the list of paths that skip it,
        // and narrows that list to the API and the SPA route when OpenID Connect is not configured.
        if (AdminAuthentication.IsFrameAuthenticationExempt(context.Request.Path, adminOptions.IsEnabled))
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
