using ImmichFrame.Core.Exceptions;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers;

/// <summary>
/// The configuration editor's own API: read the settings file, and write it back.
/// <para>
/// Both actions carry the <c>AdminOnly</c> policy, which is authentication against the admin cookie
/// <em>plus</em> the allowlist. Nothing here may follow <c>AdminSessionController.Logout</c>'s
/// scheme-only shape: that authenticates without consulting the allowlist, so on an identity
/// provider with open registration it would let a stranger read and rewrite every Immich API key
/// this installation holds. <see cref="AdminEndpointGuard"/> refuses to start a host that gets this
/// wrong, and its waiver exists for signing out alone.
/// </para>
/// <para>
/// The policy is stated on the class as well as on each action deliberately. The action-level
/// attribute is what a reader checks and what the brief for this endpoint asks for; the class-level
/// one is what covers an action added here later by someone who forgets.
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/config")]
[Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
public class AdminConfigController(ILogger<AdminConfigController> _logger, AdminConfigService _config) : ControllerBase
{
    /// <summary>
    /// The default configuration and every profile, with each profile's declared keys distinguishable
    /// from what it inherits, and every secret masked.
    /// </summary>
    [HttpGet(Name = "GetAdminConfig")]
    [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
    public ActionResult<AdminConfigDto> GetConfig()
    {
        try
        {
            return _config.Read();
        }
        catch (ImmichFrameException ex)
        {
            // The file on disk no longer loads - hand-edited since startup, most likely. The whole
            // family is caught because reading it can fail in three different ways: a file that will
            // not parse (ConfigSaveRefusedException), one that parses but the editor cannot represent
            // faithfully, such as a YAML document using anchors (SettingsNotValidException), and a
            // directory holding no readable configuration at all (the base type). Saying which beats
            // a 500, and this is the one endpoint whose reader can fix it.
            _logger.LogWarning("The configuration could not be read for editing: {sanitizedReason}",
                ex.Message.SanitizeString());

            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Writes the configuration back and applies it without a restart.
    /// <para>
    /// 409 for the four refusals an operator can act on - a stale version token, a configuration that
    /// came from environment variables, a v1 file with no conversion consent, and a directory that
    /// cannot be written - and 400 for a configuration that simply does not load. In every one of
    /// them the settings file is left exactly as it was.
    /// </para>
    /// </summary>
    [HttpPut(Name = "SaveAdminConfig")]
    [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
    public ActionResult<AdminConfigDto> SaveConfig([FromBody] AdminConfigUpdateDto request)
    {
        try
        {
            return _config.Save(request);
        }
        catch (ConfigSaveRefusedException ex)
        {
            _logger.LogWarning("Configuration save refused: {sanitizedReason}", ex.Message.SanitizeString());

            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (SettingsNotValidException ex)
        {
            // The message names what is wrong and the file has not been touched, so handing it back
            // verbatim is both safe and the whole point of validating before writing.
            _logger.LogWarning("Configuration save rejected as invalid: {sanitizedReason}",
                ex.Message.SanitizeString());

            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ImmichFrameException ex)
        {
            // Last, after its two subclasses: the configuration on disk could not be read at all, so
            // there is nothing to save against. Not the request's fault, hence 409 rather than 400.
            _logger.LogWarning("Configuration save could not proceed: {sanitizedReason}",
                ex.Message.SanitizeString());

            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
