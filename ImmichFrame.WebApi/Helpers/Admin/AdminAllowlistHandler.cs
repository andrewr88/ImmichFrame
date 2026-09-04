using ImmichFrame.Core.Helpers;
using Microsoft.AspNetCore.Authorization;

namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// Decides <see cref="AdminAllowlistRequirement"/>.
/// <para>
/// The only way this handler succeeds is by finding the caller on a non-empty allowlist. It never
/// calls <c>Fail</c>: leaving the requirement unmet is enough to deny, and failing outright would
/// also veto any other policy evaluated on the same request.
/// </para>
/// </summary>
public class AdminAllowlistHandler(AdminOidcOptions _options, ILogger<AdminAllowlistHandler> _logger)
    : AuthorizationHandler<AdminAllowlistRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminAllowlistRequirement requirement)
    {
        if (_options.IsAdmin(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User?.Identity?.IsAuthenticated == true)
        {
            // The subject comes from a token the identity provider signed, but it is still a
            // remote-controlled string reaching a log line, so it is sanitized like any other.
            _logger.LogWarning(
                "Admin access denied for subject '{sanitizedSubject}': not on IMMICHFRAME_OIDC_ADMINS",
                (AdminOidcOptions.SubjectOf(context.User) ?? "unknown").SanitizeString());
        }

        return Task.CompletedTask;
    }
}
