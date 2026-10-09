using System.Security.Claims;
using ImmichFrame.WebApi.Helpers.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Tests.Mocks;

/// <summary>
/// Signs the caller into the admin cookie scheme, so that a test can read the <c>Set-Cookie</c> the
/// application's own cookie handler writes. Registered only by the fixtures that ask for it
/// (<c>AddApplicationPart</c>), like <see cref="AdminProbeController"/>.
/// <para>
/// It stands in for the end of the OpenID Connect handshake - the one thing in the application that
/// signs into this scheme, and one that cannot finish here without an identity provider. The test
/// hosts forward the scheme's <em>authentication</em> to <see cref="TestAdminAuthHandler"/>, never its
/// sign-in, so the cookie issued here is the real handler's, built from the options <c>Program</c>
/// configured.
/// </para>
/// <para>
/// Anonymous - and declared so, for <c>AdminEndpointGuard</c> - because the handshake it stands in for
/// is reachable without a session too. Like the login, it is hidden with the rest of the prefix while
/// the admin surface is off.
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/probe")]
public class AdminSignInProbeController : ControllerBase
{
    [HttpPost("sign-in", Name = "AdminProbeSignIn")]
    [AllowAnonymous]
    [AdminEndpoint(Anonymous = true)]
    public async Task<IActionResult> SignIn()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "8ab0c1e2-user")], AdminAuthentication.CookieScheme);

        await HttpContext.SignInAsync(AdminAuthentication.CookieScheme, new ClaimsPrincipal(identity));

        return Ok();
    }
}
