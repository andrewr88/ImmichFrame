using ImmichFrame.WebApi.Helpers.Admin;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Admin;

/// <summary>
/// The single riskiest decision in the admin work. <c>CustomAuthenticationMiddleware</c> runs the
/// frame's shared-secret scheme for every request and <c>UnknownProfileMiddleware</c> 404s an
/// unknown profile, and both step aside for this list - so a path matched here that should not be
/// would silently unauthenticate part of the frame API. Every existing route prefix is asserted
/// against, alongside the near-misses a naive <c>string.StartsWith</c> would let through.
/// </summary>
[TestFixture]
public class AdminBypassPathTests
{
    [TestCase("/api/admin")]
    [TestCase("/api/admin/session")]
    [TestCase("/api/admin/login")]
    [TestCase("/API/Admin/Session")]
    [TestCase("/admin")]
    [TestCase("/admin/")]
    [TestCase("/signin-oidc")]
    [TestCase("/signout-callback-oidc")]
    [TestCase("/signout-oidc")]
    public void IsAdminPath_AdminOwnedPath_Bypasses(string path)
    {
        Assert.That(AdminAuthentication.IsAdminPath(path), Is.True);
    }

    [TestCase("/api/adminfoo")]
    [TestCase("/api/admins")]
    [TestCase("/admins")]
    [TestCase("/adminfoo")]
    [TestCase("/api")]
    [TestCase("/api/Config")]
    [TestCase("/api/Config/Version")]
    [TestCase("/api/Asset")]
    [TestCase("/api/Asset/abc/Image")]
    [TestCase("/api/Calendar")]
    [TestCase("/api/Weather")]
    [TestCase("/signin-oidc-not-really")]
    [TestCase("/")]
    [TestCase("")]
    public void IsAdminPath_EverythingElse_StillRunsTheFrameScheme(string path)
    {
        Assert.That(AdminAuthentication.IsAdminPath(path), Is.False);
    }
}
