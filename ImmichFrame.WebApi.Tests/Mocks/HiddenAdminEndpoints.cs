using System.Reflection;
using ImmichFrame.WebApi.Helpers.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace ImmichFrame.WebApi.Tests.Mocks;

/// <summary>
/// Admin endpoints that <c>AdminEndpointGuard</c> must refuse to start a host with.
/// <para>
/// All of them are marked <c>[NonController]</c> so MVC's default discovery walks straight past them
/// even though the whole test assembly is added as an application part - <c>ControllerBase</c>
/// itself carries <c>[Controller]</c>, which is inherited, so a name that avoids the
/// <c>Controller</c> suffix is not enough on its own. Only <see cref="HiddenAdminEndpointProvider"/>
/// can bring one into a host, and any host one reaches is a host expected to refuse to start.
/// </para>
/// </summary>
[NonController]
[Route("api/admin/probe")]
public class ForgottenAdminEndpoint : ControllerBase
{
    public const string Route = "api/admin/probe/forgotten";

    /// <summary>No policy, no <c>[AdminEndpoint]</c>, nothing.</summary>
    [HttpGet("forgotten")]
    public IActionResult Forgotten() => Ok("reached");
}

/// <summary>
/// The likelier mistake, and the more dangerous one because it looks protected. A bare
/// <c>[Authorize]</c> binds to the default scheme - still <c>ImmichFrameScheme</c> - so it admits
/// any holder of the frame's <c>AuthenticationSecret</c>, which is the secret sitting on every
/// kiosk display, and admits everyone when no secret is configured at all.
/// </summary>
[NonController]
[Route("api/admin/probe")]
public class BareAuthorizeAdminEndpoint : ControllerBase
{
    public const string Route = "api/admin/probe/bare-authorize";

    [HttpGet("bare-authorize")]
    [Authorize]
    public IActionResult BareAuthorize() => Ok("reached");
}

/// <summary>
/// Authorization that names a real policy, but not the admin one. Anything the frame already knows
/// how to satisfy is not an admin guard.
/// </summary>
[NonController]
[Route("api/admin/probe")]
public class ForeignPolicyAdminEndpoint : ControllerBase
{
    public const string Route = "api/admin/probe/foreign-policy";

    [HttpGet("foreign-policy")]
    [Authorize(Policy = "AllowAnonymous")]
    public IActionResult ForeignPolicy() => Ok("reached");
}

/// <summary>
/// The fail-open shape, and the likeliest one to be copied: sign-out is the only in-repo example of
/// admin authorization that is not the policy, so an endpoint added by copying it inherits
/// authentication without the allowlist - reachable by every identity the provider will
/// authenticate. Legal only when the endpoint also declares <c>AllowlistNotRequired</c>.
/// </summary>
[NonController]
[Route("api/admin/probe")]
public class SchemeOnlyAdminEndpoint : ControllerBase
{
    public const string Route = "api/admin/probe/scheme-only";

    [HttpGet("scheme-only")]
    [Authorize(AuthenticationSchemes = AdminAuthentication.CookieScheme)]
    public IActionResult SchemeOnly() => Ok("reached");
}

/// <summary>
/// The flag waives the allowlist, never authorization altogether, so pairing it with a bare
/// <c>[Authorize]</c> - which binds to the frame's own scheme - must still be refused.
/// </summary>
[NonController]
[Route("api/admin/probe")]
public class FlaggedButBarelyAuthorizedAdminEndpoint : ControllerBase
{
    public const string Route = "api/admin/probe/flagged-bare";

    [HttpGet("flagged-bare")]
    [Authorize]
    [AdminEndpoint(AllowlistNotRequired = true)]
    public IActionResult FlaggedBare() => Ok("reached");
}

/// <summary>Adds one hidden endpoint type to a single test host's controller feature.</summary>
public class HiddenAdminEndpointProvider(Type _endpoint) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
        feature.Controllers.Add(_endpoint.GetTypeInfo());
}
