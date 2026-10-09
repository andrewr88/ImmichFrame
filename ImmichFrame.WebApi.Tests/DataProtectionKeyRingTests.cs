using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Tests.Controllers;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests;

/// <summary>
/// Where the keys signing the admin session cookie, and the OpenID Connect handshake's state, are
/// kept. Left to the framework they went under the home directory of the user ImmichFrame runs as -
/// which in the container is <c>/</c>, and not writable by that user - so the key ring lived in memory
/// and every restart signed every administrator out, and failed every sign-in that was half-way
/// through.
/// </summary>
[TestFixture]
public class DataProtectionKeyRingTests
{
    private string _directory = null!;

    [SetUp]
    public void Setup() =>
        _directory = Path.Combine(Path.GetTempPath(), $"immichframe-keys-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// In the configuration directory, beside the settings database: the one directory an installation
    /// already keeps. Read from the directory the host was actually given, so a host moved to a
    /// directory of its own - as this one is - keeps its keys there, and nowhere it shares.
    /// </summary>
    [Test]
    public void KeyRing_IsKeptInTheConfigurationDirectory()
    {
        // Arrange
        using var factory = CreateFactory();

        // Act - starting the host is what loads the key ring, and creates its first key
        var keyManagement = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var dataProtection = factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;

        // Assert
        var keys = Path.Combine(_directory, "keys");
        Assert.Multiple(() =>
        {
            Assert.That(keyManagement.XmlRepository, Is.InstanceOf<FileSystemXmlRepository>());
            Assert.That((keyManagement.XmlRepository as FileSystemXmlRepository)?.Directory.FullName, Is.EqualTo(keys));
            Assert.That(Directory.Exists(keys) ? Directory.GetFiles(keys, "key-*.xml") : [], Is.Not.Empty,
                "no key was written to the configuration directory");

            // Named rather than left to default to the content root's path, so that the keys still
            // fit after the binary moves.
            Assert.That(dataProtection.ApplicationDiscriminator, Is.EqualTo("ImmichFrame"));
        });
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();
        var directory = _directory;

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(versionHandler);
                    services.AddSingleton(new ConfigLocation(directory));
                    services.AddSingleton<IConfigCatalog>(AdminSessionControllerTests.Catalog());
                });
            });
    }
}
