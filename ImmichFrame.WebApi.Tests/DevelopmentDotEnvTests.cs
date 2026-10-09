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
/// <c>WebApplicationFactory</c> runs the host as Development, which is what makes it load at all -
/// and every other host in the suite is kept from loading it by <see cref="DotEnvIsolation"/>, so
/// the test that wants the load opts back in.
/// </para>
/// </summary>
[TestFixture]
public class DevelopmentDotEnvTests
{
    private const string ConfigPathVariable = "IMMICHFRAME_CONFIG_PATH";

    private static readonly string[] OidcVariables =
    [
        AdminOidcOptions.AuthorityVariable,
        AdminOidcOptions.ClientIdVariable,
        AdminOidcOptions.ClientSecretVariable,
        AdminOidcOptions.AdminsVariable
    ];

    private static readonly string[] Variables = [Program.SkipDotEnvVariable, ConfigPathVariable, .. OidcVariables];

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
        // Arrange - opted back in: the suite keeps every host it starts from loading the file.
        Environment.SetEnvironmentVariable(Program.SkipDotEnvVariable, null);

        var configPath = Path.Combine(_root, "config-from-dotenv");
        WriteDotEnv(configPath);
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

    /// <summary>
    /// And no other host in the suite loads it. The file is found from wherever the test runner
    /// started, and from a project directory that is the developer's own: its admin sign-in settings
    /// would reach the whole process and switch the OpenID Connect handler on in every host started
    /// after it. The switch is left as the suite set it rather than set here, so this fails if either
    /// half stops holding - <c>Program</c> honouring it, or the suite setting it.
    /// </summary>
    [Test]
    public void DotEnv_IsNotLoadedByTheSuitesHosts()
    {
        // Arrange - what the environment says, and a .env next door that says otherwise.
        var configPath = Path.Combine(_root, "config-from-environment");
        Environment.SetEnvironmentVariable(ConfigPathVariable, configPath);
        foreach (var variable in OidcVariables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        WriteDotEnv(Path.Combine(_root, "config-from-dotenv"));
        Directory.SetCurrentDirectory(Path.Combine(_root, "ImmichFrame.WebApi"));

        // Act
        using var factory = CreateFactory();
        var services = factory.Services;

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(services.GetRequiredService<ConfigLocation>().Directory, Is.EqualTo(configPath));
            Assert.That(services.GetRequiredService<AdminOidcOptions>().IsEnabled, Is.False);
        });
    }

    /// <summary>A <c>.env</c> that moves the configuration directory and switches the admin surface on.</summary>
    private void WriteDotEnv(string configPath) =>
        File.WriteAllLines(Path.Combine(_root, "docker", ".env"),
        [
            $"{ConfigPathVariable}={configPath}",
            $"{AdminOidcOptions.AuthorityVariable}=https://idp.example.com",
            $"{AdminOidcOptions.ClientIdVariable}=immichframe",
            $"{AdminOidcOptions.ClientSecretVariable}=client-secret",
            $"{AdminOidcOptions.AdminsVariable}=8ab0c1e2-user"
        ]);

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.UseMockHandler(versionHandler)));
    }
}
