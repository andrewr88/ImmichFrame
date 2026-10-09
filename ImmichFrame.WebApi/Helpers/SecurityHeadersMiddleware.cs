using ImmichFrame.WebApi.Helpers.Admin;

namespace ImmichFrame.WebApi.Helpers;

/// <summary>
/// The response headers every answer carries, whichever part of the pipeline gives it.
/// <para>
/// <c>X-Content-Type-Options: nosniff</c> on everything. Without it a browser may second-guess the
/// declared content type of a response and treat as a script or a page what was served as something
/// else - and this origin serves the admin editor, with its session cookie.
/// </para>
/// <para>
/// <c>Content-Security-Policy: frame-ancestors 'none'</c> on the admin editor alone - its page and its
/// API - so that no other site can load the editor in a frame and lay its own buttons over an
/// administrator's signed-in session. Not on anything else, and no <c>X-Frame-Options</c> anywhere:
/// the frame is made to be embedded - on a Home Assistant dashboard, in an app's webview - and a
/// header refusing that would break exactly what it is for. Nor on the OpenID Connect paths, for the
/// reason <see cref="AdminAuthentication.IsEditorPath"/> gives.
/// </para>
/// <para>
/// Set before anything downstream runs rather than as the response starts, so that an answer setting
/// a policy of its own - the sandbox <c>AssetController</c> puts on an asset - replaces this one
/// instead of being overwritten by it.
/// </para>
/// </summary>
public class SecurityHeadersMiddleware(RequestDelegate _next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers.XContentTypeOptions = "nosniff";

        if (AdminAuthentication.IsEditorPath(context.Request.Path))
        {
            headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        }

        return _next(context);
    }
}
