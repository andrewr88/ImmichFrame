using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers;

/// <summary>
/// What <c>ImmichFrameAuthenticationHandler</c> treats as "no secret configured".
/// <para>
/// An <c>AuthenticationSecret</c> of <c>""</c> used to mean the opposite of what it looks like:
/// authentication stayed switched on, and the only token that satisfied it was the empty one, since
/// the handler trims <c>"Bearer "</c> to <c>""</c> before comparing. Every frame sending its real
/// secret was refused and anyone sending none was let in. A hand-written settings file can still
/// reach that state, so the rule is asserted here rather than left to whatever writes the file.
/// </para>
/// <para>
/// Probed through <c>/api/Calendar</c>, which carries <c>[Authorize]</c> and, with no webcalendars
/// configured, answers without any network I/O. <c>/api/Config</c> cannot show this: it is
/// deliberately unauthenticated.
/// </para>
/// </summary>
[TestFixture]
public class FrameAuthenticationTests
{
    private const string RealSecret = "a-real-secret";

    [Test]
    public async Task EmptySecret_AnonymousRequest_IsAllowed()
    {
        // Arrange
        using var factory = CreateFactory(string.Empty);

        // Act
        var response = await factory.CreateClient().GetAsync("/api/Calendar");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task WhitespaceSecret_AnonymousRequest_IsAllowed()
    {
        // Arrange
        using var factory = CreateFactory("   ");

        // Act
        var response = await factory.CreateClient().GetAsync("/api/Calendar");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>
    /// The control the two tests above need: without it they would still pass on a handler that had
    /// stopped enforcing authentication altogether.
    /// </summary>
    [Test]
    public async Task RealSecret_AnonymousRequest_IsRefused()
    {
        // Arrange
        using var factory = CreateFactory(RealSecret);

        // Act
        var response = await factory.CreateClient().GetAsync("/api/Calendar");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// The other half of the same bug, and the one an operator actually notices: with an empty
    /// secret configured, a frame presenting the token it was given was refused, because the only
    /// token equal to <c>""</c> is the one nobody sends on purpose.
    /// </summary>
    [Test]
    public async Task EmptySecret_ClientPresentingAToken_IsAllowed()
    {
        // Arrange
        using var factory = CreateFactory(string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Calendar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RealSecret);

        // Act
        var response = await factory.CreateClient().SendAsync(request);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task RealSecret_MatchingBearerToken_IsAllowed()
    {
        // Arrange
        using var factory = CreateFactory(RealSecret);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Calendar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RealSecret);

        // Act
        var response = await factory.CreateClient().SendAsync(request);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>
    /// An authentication scheme is a case-insensitive token (RFC 9110, 11.1): "bearer" and "BEARER"
    /// name the scheme "Bearer" does, and a client that spells it either way is presenting the secret
    /// all the same.
    /// </summary>
    [TestCase("bearer")]
    [TestCase("BEARER")]
    public async Task RealSecret_BearerSchemeInAnotherCase_IsAllowed(string scheme)
    {
        // Arrange
        using var factory = CreateFactory(RealSecret);

        // Act
        var context = await SendWithAuthorization(factory, $"{scheme} {RealSecret}");

        // Assert
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    /// <summary>
    /// A Bearer credential may put more than one space between the scheme and the token (RFC 6750,
    /// 2.1), and the secret is the token, not the token with a space in front of it.
    /// </summary>
    [Test]
    public async Task RealSecret_TokenAfterMoreThanOneSpace_IsAllowed()
    {
        // Arrange
        using var factory = CreateFactory(RealSecret);

        // Act
        var context = await SendWithAuthorization(factory, $"Bearer  {RealSecret}");

        // Assert
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    /// <summary>
    /// Nothing above sends a wrong token, so without this a handler that let every bearer token
    /// through would pass the whole fixture. Each case is a near miss that some broken comparison
    /// accepts: one that ignores case, and one that stops at the end of the shorter input - which
    /// takes a prefix of the secret, or the secret with more after it.
    /// </summary>
    [TestCase("a-real-secreT")]
    [TestCase("a-real-secre")]
    [TestCase("a-real-secret-and-more")]
    public async Task RealSecret_NearMissBearerToken_IsRefused(string token)
    {
        // Arrange
        using var factory = CreateFactory(RealSecret);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Calendar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await factory.CreateClient().SendAsync(request);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// Sends the probe with <paramref name="authorization"/> written onto the request the host sees
    /// exactly as given. Through HttpClient it would not arrive that way: TestServer copies a request's
    /// headers by enumerating them, and enumerating parses an Authorization value and writes it back
    /// out in the parser's own spelling - "Bearer" and two spaces arrives with one.
    /// </summary>
    private static Task<HttpContext> SendWithAuthorization(WebApplicationFactory<Program> factory, string authorization) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/api/Calendar";
            context.Request.Headers.Authorization = authorization;
        });

    private static WebApplicationFactory<Program> CreateFactory(string? authenticationSecret)
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // The startup version check only logs what it hears, and nothing waits for it -
                    // but without this its request would go out to the network, to a server that
                    // does not exist.
                    services.UseMockHandler(versionHandler);

                    // Seeded through the catalog, because the per-profile services the handler
                    // reads its settings from are built from the catalog rather than from an
                    // IServerSettings registration.
                    services.AddSingleton<IConfigCatalog>(new ConfigCatalog(Settings(authenticationSecret)));
                });
            });
    }

    private static ServerSettings Settings(string? authenticationSecret) => new()
    {
        GeneralSettingsImpl = new GeneralSettings { AuthenticationSecret = authenticationSecret },
        AccountsImpl = new List<ServerAccountSettings>
        {
            new() { ImmichServerUrl = "http://mock-immich-server.com", ApiKey = "test-api-key" }
        }
    };
}
