using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace ImmichFrame.WebApi.Tests.Mocks;

/// <summary>
/// A careless admin endpoint: no policy, no <c>[AdminEndpoint]</c>, nothing. The frame's shared
/// secret is already bypassed for this prefix, so an endpoint like this is what
/// <c>AdminEndpointGuard</c> exists to refuse.
/// <para>
/// Marked <c>[NonController]</c> so MVC's default discovery walks straight past it even though the
/// whole test assembly is added as an application part - <c>ControllerBase</c> itself carries
/// <c>[Controller]</c>, which is inherited, so a name that avoids the <c>Controller</c> suffix is
/// not enough on its own. Only
/// <see cref="ForgottenAdminEndpointProvider"/> can bring it into a host - and any host it reaches
/// is a host that is expected to refuse to start.
/// </para>
/// </summary>
[NonController]
[Route("api/admin/probe")]
public class ForgottenAdminEndpoint : ControllerBase
{
    public const string Route = "api/admin/probe/forgotten";

    [HttpGet("forgotten")]
    public IActionResult Forgotten() => Ok("reached");
}

/// <summary>Adds <see cref="ForgottenAdminEndpoint"/> to a single test host's controller feature.</summary>
public class ForgottenAdminEndpointProvider : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
        feature.Controllers.Add(typeof(ForgottenAdminEndpoint).GetTypeInfo());
}
