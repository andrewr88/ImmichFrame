using ImmichFrame.WebApi.Helpers.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Tests.Mocks;

/// <summary>
/// A stand-in for the endpoints task 003 will add under <c>/api/admin</c>, registered only by the
/// fixtures that ask for it (<c>AddApplicationPart</c>). It exists because this task's own admin
/// controller has no endpoint behind the <c>AdminOnly</c> policy - signing out deliberately is not
/// one - so without a probe the policy would ship unexercised through the real pipeline.
/// </summary>
[ApiController]
[Route("api/admin/probe")]
public class AdminProbeController : ControllerBase
{
    /// <summary>What a real admin endpoint looks like: the policy and nothing else.</summary>
    [HttpGet("policy", Name = "AdminProbePolicy")]
    [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
    public IActionResult Policy() => Ok("allowed");
}
