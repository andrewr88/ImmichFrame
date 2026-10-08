using System.Net.Http;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests;

/// <summary>
/// In Development the application loads <c>docker/.env</c> itself, so <c>dotnet run</c> sees what
/// docker compose would hand the container - and it has to load it before startup reads the
/// environment. It used to load it after <c>Build()</c>, by which time the configuration path and
/// the admin surface's OpenID Connect settings had been read, so a <c>.env</c> configuring the
/// admin editor left it switched off.
/// <para>
/// The host looks for the file relative to its current directory, so this test moves the process's
/// current directory and puts it back afterwards, along with every variable the file sets.
/// <c>WebApplicationFactory</c> runs the host as Development, which is what makes it load at all.
/// </para>
/// </summary>
[TestFixture]
public class DevelopmentDotEnvTests
{
    private const string ConfigPathVariable = "IMMICHFRAME_CONFIG_PATH";

    private static readonly string[] Variables =
    [
        ConfigPathVariable,
        AdminOidcOptions.AuthorityVariable,
        AdminOidcOptions.ClientIdVariable,
        AdminOidcOptions.ClientSecretVariable,
        AdminOidcOptions.AdminsVariable
    ];

    private string _root = null!;
    private string _previousDirectory = null!;
    private Dictionary<string, string?> _previousValues = null!;

    [SetUp]
    public void Setup()
    {
        _previousDirectory = Directory.GetCurrentDirectory();
        _previousValues = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        // The same shape as the repository: the host runs from the project directory and finds the
        // file at ../docker/.env.
        _root = Path.Combine(Path.GetTempPath(), $"immichframe-dotenv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "ImmichFrame.WebApi"));
        Directory.CreateDirectory(Path.Combine(_root, "docker"));
    }

    [TearDown]
    public void TearDown()
    {
        Directory.SetCurrentDirectory(_previousDirectory);

        foreach (var (name, value) in _previousValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void DotEnv_IsLoadedBeforeStartupReadsTheEnvironment()
    {
        // Arrange
        var configPath = Path.Combine(_root, "config-from-dotenv");
        File.WriteAllLines(Path.Combine(_root, "docker", ".env"),
        [
            $"{ConfigPathVariable}={configPath}",
            $"{AdminOidcOptions.AuthorityVariable}=https://idp.example.com",
            $"{AdminOidcOptions.ClientIdVariable}=immichframe",
            $"{AdminOidcOptions.ClientSecretVariable}=client-secret",
            $"{AdminOidcOptions.AdminsVariable}=8ab0c1e2-user"
        ]);
        Directory.SetCurrentDirectory(Path.Combine(_root, "ImmichFrame.WebApi"));

        // Act
        using var factory = CreateFactory();
        var services = factory.Services;

        // Assert
        Assert.Multiple(() =>
        {
            // Read near the top, long before the OpenID Connect settings, so a load that slipped in
            // partway down still fails here.
            Assert.That(services.GetRequiredService<ConfigLocation>().Directory, Is.EqualTo(configPath));
            Assert.That(services.GetRequiredService<AdminOidcOptions>().IsEnabled, Is.True);
        });
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.UseMockHandler(versionHandler)));
    }
}
