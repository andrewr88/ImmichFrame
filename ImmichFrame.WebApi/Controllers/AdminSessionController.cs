using ImmichFrame.WebApi.Helpers.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers;

/// <summary>
/// What the admin SPA needs before it can show anything: whether an admin surface exists here, and
/// who - if anyone - the browser is signed in as.
/// </summary>
/// <param name="Configured">
/// False when the admin surface is off, either because the OpenID Connect variables are absent or
/// because the allowlist is empty. Every other admin endpoint 404s while this is false.
/// </param>
/// <param name="Authenticated">Whether the browser carries a valid admin cookie.</param>
/// <param name="IsAdmin">Whether that session is on the allowlist. Signed in is not the same as allowed.</param>
public record AdminSessionDto(bool Configured, bool Authenticated, bool IsAdmin, string? Subject, string? Email);

[ApiController]
[Route("api/admin")]
public class AdminSessionController(AdminOidcOptions _options) : ControllerBase
{
    /// <summary>
    /// The one admin endpoint that answers without authentication, and the only one that still
    /// answers when the surface is unconfigured - the SPA has to be able to tell "log in",
    /// "you are not an administrator" and "this installation has no admin surface" apart, and it
    /// cannot do that if the endpoint that would say so is itself behind the login.
    /// </summary>
    [HttpGet("session", Name = "GetAdminSession")]
    [AllowAnonymous]
    [AdminEndpoint(Anonymous = true, VisibleWhenUnconfigured = true)]
    public async Task<AdminSessionDto> GetSession()
    {
        // HttpContext.User was produced by the default scheme, which is still the frame's, so the
        // admin identity has to be asked for by name rather than read off the request.
        var result = await HttpContext.AuthenticateAsync(AdminAuthentication.CookieScheme);
        var user = result.Succeeded ? result.Principal : null;

        return new AdminSessionDto(
            _options.IsEnabled,
            user is not null,
            _options.IsAdmin(user),
            AdminOidcOptions.SubjectOf(user),
            AdminOidcOptions.EmailOf(user));
    }

    /// <summary>
    /// Starts the OpenID Connect handshake. Anonymous of necessity - it is what a caller with no
    /// session uses to get one - and reachable only while the surface is configured, since it
    /// declares Anonymous but not VisibleWhenUnconfigured.
    /// </summary>
    [HttpGet("login", Name = "AdminLogin")]
    [AllowAnonymous]
    [AdminEndpoint(Anonymous = true)]
    public IActionResult Login(string? returnUrl = null)
    {
        // IsLocalUrl, not a null check: returnUrl comes off the query string, and handing an
        // absolute or protocol-relative URL to the handler would turn the login into an open
        // redirect that borrows the identity provider's credibility.
        var redirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl! : AdminAuthentication.SpaPath;

        return Challenge(new AuthenticationProperties { RedirectUri = redirectUri }, AdminAuthentication.OidcScheme);
    }

    /// <summary>
    /// Drops the caller's own admin cookie.
    /// <para>
    /// Authentication against the cookie scheme, deliberately <em>not</em> the AdminOnly policy.
    /// Destroying your own session is not a privileged act, and gating it on the allowlist would
    /// strand exactly the person who most needs it: a signed-in non-administrator sits on the "you
    /// are not an administrator" screen, whose polling of <c>session</c> re-issues the sliding
    /// cookie, so the session would never expire and only clearing browser cookies would end it.
    /// </para>
    /// <para>
    /// No CSRF token: the cookie is <c>SameSite=Lax</c>, which is not sent with a cross-site POST,
    /// and the worst a forged sign-out could achieve is signing the administrator out.
    /// </para>
    /// <para>
    /// <c>AllowlistNotRequired</c> is what makes that exception legal - <see cref="AdminEndpointGuard"/>
    /// otherwise refuses to start on scheme-only authorization, because it admits every identity the
    /// provider will authenticate. Do not copy this pair onto an endpoint that reads or writes
    /// anything; those take <c>[Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]</c>.
    /// </para>
    /// </summary>
    [HttpPost("logout", Name = "AdminLogout")]
    [Authorize(AuthenticationSchemes = AdminAuthentication.CookieScheme)]
    [AdminEndpoint(AllowlistNotRequired = true)]
    public IActionResult Logout() => SignOut(AdminAuthentication.CookieScheme);
}
