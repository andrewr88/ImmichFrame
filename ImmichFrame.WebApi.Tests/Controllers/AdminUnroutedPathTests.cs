using System.IO;
using System.Net;
using System.Net.Http;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers;

/// <summary>
/// An unrouted path under <c>/api/admin</c> must not answer with the SPA shell.
/// <para>
/// This fixture supplies a real web root containing an <c>index.html</c>, which is the only way the
/// assertion means anything: the other integration fixtures run without one, so
/// <c>MapFallbackToFile</c> answers 404 there whatever the middleware does, and a test written
/// against those hosts passes just as happily with the guard removed.
/// </para>
/// </summary>
[TestFixture]
public class AdminUnroutedPathTests
{
    private const string ShellMarker = "immichframe-spa-shell-marker";

    private static readonly AdminOidcOptions Enabled = new()
    {
        Authority = "https://idp.example.com",
        ClientId = "immichframe",
        ClientSecret = "client-secret",
        Admins = ["8ab0c1e2-user"]
    };

    private string _webRoot = null!;

    [OneTimeSetUp]
    public void CreateWebRoot()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "immichframe-webroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), $"<html><body>{ShellMarker}</body></html>");
    }

    [OneTimeTearDown]
    public void RemoveWebRoot() => Directory.Delete(_webRoot, recursive: true);

    [TestCase("/api/admin/does-not-exist")]
    // Routing ignores case, so the fallback answers /API/ADMIN/... as readily as /api/admin/... - and a
    // prefix recognised only as written would hand a differently-cased typo the editor's HTML after all.
    [TestCase("/API/ADMIN/does-not-exist")]
    [TestCase("/Api/Admin/typo")]
    public async Task UnroutedAdminPath_Is404WhileTheShellStillServesElsewhere(string unroutedPath)
    {
        using var factory = CreateFactory(Enabled);
        var client = factory.CreateClient();

        var spaRoute = await client.GetAsync("/some-spa-route");
        var shell = await spaRoute.Content.ReadAsStringAsync();
        var unroutedAdmin = await client.GetAsync(unroutedPath);

        Assert.Multiple(() =>
        {
            // Establishes that the fallback really is live in this host. Without it the assertion
            // below would pass on a host that answers 404 to everything.
            Assert.That(spaRoute.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(shell, Does.Contain(ShellMarker));

            Assert.That(unroutedAdmin.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    /// <summary>The SPA route itself must keep serving the shell - that is what hosts the editor.</summary>
    [Test]
    public async Task TheAdminSpaRouteStillServesTheShell()
    {
        using var factory = CreateFactory(Enabled);

        var response = await factory.CreateClient().GetAsync(AdminAuthentication.SpaPath);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(ShellMarker));
        });
    }

    private WebApplicationFactory<Program> CreateFactory(AdminOidcOptions adminOptions)
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseWebRoot(_webRoot);
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(versionHandler);
                    services.AddSingleton<IConfigCatalog>(AdminSessionControllerTests.Catalog());
                    services.AddSingleton(adminOptions);
                });
            });
    }
}
