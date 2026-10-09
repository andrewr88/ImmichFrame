using System.Net;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Tests.Controllers;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests;

/// <summary>
/// What <c>Program.cs</c>'s pipeline does around the controllers, which no controller test sees: the
/// headers every answer carries, the static files the web client is served from, and Swagger being
/// there only in Development.
/// <para>
/// On a host with a real web root holding the SPA shell and a script, as <c>AdminUnroutedPathTests</c>
/// has one. Without it the SPA fallback answers every page with a 404, and a script is a 404 whether
/// or not anything serves static files.
/// </para>
/// </summary>
[TestFixture]
public class HostPipelineTests
{
    private const string Script = "frame.js";

    private string _webRoot = null!;

    [OneTimeSetUp]
    public void CreateWebRoot()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "immichframe-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html><body>shell</body></html>");
        File.WriteAllText(Path.Combine(_webRoot, Script), "console.log('frame');");
    }

    [OneTimeTearDown]
    public void RemoveWebRoot() => Directory.Delete(_webRoot, recursive: true);

    /// <summary>
    /// The editor refuses to be loaded in another site's frame - its page, the routes under it, and
    /// its API. Framed, it is a page an administrator is signed in to, laid out under someone else's
    /// buttons, and a click meant for theirs can land on Save.
    /// </summary>
    [TestCase("/admin")]
    [TestCase("/admin/general")]
    [TestCase("/api/admin/session")]
    public async Task AdminEditor_RefusesToBeFramed(string path)
    {
        // Arrange
        using var factory = CreateFactory();

        // Act
        using var response = await factory.CreateClient().GetAsync(path);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Header(response, "Content-Security-Policy"), Is.EqualTo("frame-ancestors 'none'"));
        });
    }

    /// <summary>
    /// The frame itself goes on being embeddable - that is how it is shown on a Home Assistant
    /// dashboard, or in an app's webview - so neither a frame-ancestors policy nor X-Frame-Options
    /// reaches anything outside the editor. Nor the path an identity provider's front-channel sign-out
    /// loads in a frame of its own, although it belongs to the admin surface: refused there, signing
    /// out at the provider would leave the editor's session behind.
    /// </summary>
    [TestCase("/")]
    [TestCase("/api/Config")]
    [TestCase("/signout-oidc")]
    public async Task Frame_CanStillBeEmbedded(string path)
    {
        // Arrange
        using var factory = CreateFactory();

        // Act
        using var response = await factory.CreateClient().GetAsync(path);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Header(response, "Content-Security-Policy"), Is.Null);
            Assert.That(Header(response, "X-Frame-Options"), Is.Null);
        });
    }

    /// <summary>
    /// Every answer tells the browser to take its content type as declared - the SPA shell, a static
    /// file, the API, the editor - rather than leaving it to guess its way to a script.
    /// </summary>
    [TestCase("/")]
    [TestCase("/" + Script)]
    [TestCase("/api/Config")]
    [TestCase("/admin")]
    public async Task EveryAnswer_IsMarkedNotToBeSniffed(string path)
    {
        // Arrange
        using var factory = CreateFactory();

        // Act
        using var response = await factory.CreateClient().GetAsync(path);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Header(response, "X-Content-Type-Options"), Is.EqualTo("nosniff"));
        });
    }

    /// <summary>
    /// The web client's own files - its scripts, styles and images - come out of the web root as they
    /// are. The SPA fallback cannot stand in for this: it answers only paths without a file extension.
    /// </summary>
    [Test]
    public async Task StaticFile_IsServedFromTheWebRoot()
    {
        // Arrange
        using var factory = CreateFactory();

        // Act
        using var response = await factory.CreateClient().GetAsync("/" + Script);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Does.Contain("javascript"));
            Assert.That(body, Is.EqualTo("console.log('frame');"));
        });
    }

    /// <summary>
    /// Swagger describes every endpoint the host has, the admin API's included, so it is served in
    /// Development only. Development is asked as well, so that the 404 is known to come from the gate
    /// rather than from a document that was never at that address.
    /// </summary>
    [TestCase("Development", HttpStatusCode.OK)]
    [TestCase("Production", HttpStatusCode.NotFound)]
    public async Task SwaggerDocument_IsServedOnlyInDevelopment(string environment, HttpStatusCode expected)
    {
        // Arrange
        using var factory = CreateFactory(environment);

        // Act
        using var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private WebApplicationFactory<Program> CreateFactory(string environment = "Development")
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.UseWebRoot(_webRoot);
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(versionHandler);
                    services.AddSingleton<IConfigCatalog>(AdminSessionControllerTests.Catalog());
                });
            });
    }
}
