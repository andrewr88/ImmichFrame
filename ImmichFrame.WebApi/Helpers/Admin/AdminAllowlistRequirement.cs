using Microsoft.AspNetCore.Authorization;

namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// Requires the caller's <c>sub</c> or <c>email</c> to appear in <c>IMMICHFRAME_OIDC_ADMINS</c>.
/// <para>
/// A requirement rather than a claim assertion on the policy, so that the "no allowlist means
/// nobody" rule lives in one place that a policy built elsewhere cannot accidentally drop.
/// </para>
/// </summary>
public class AdminAllowlistRequirement : IAuthorizationRequirement;
