using AwesomeAssertions;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Config;

[TestFixture]
public class ConfigCatalogTest
{
    private const string JsonProfiles = "TestProfiles.json";
    private const string YamlProfiles = "TestProfiles.yml";

    private ConfigLoader _configLoader = null!;

    [SetUp]
    public void Setup()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _configLoader = new ConfigLoader(loggerFactory.CreateLogger<ConfigLoader>());
    }

    private ConfigCatalog Load(string resource)
    {
        var path = ResourcePath(resource);

        return resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? _configLoader.LoadCatalogJson(path)
            : _configLoader.LoadCatalogYaml(path);
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfilesAreDiscovered(string resource)
    {
        Load(resource).ProfileNames.Should().BeEquivalentTo("kitchen", "living-room", "empty");
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfileInheritsSettingsItDoesNotOverride(string resource)
    {
        var kitchen = Load(resource).Get("kitchen").GeneralSettings;

        kitchen.ShowAlbumName.Should().BeTrue();
        kitchen.Language.Should().Be("en");
        kitchen.PrimaryColor.Should().Be("#base");
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfileOverridesSettingsItNames(string resource)
    {
        Load(resource).Get("kitchen").GeneralSettings.Interval.Should().Be(10);
    }

    /// <summary>
    /// The reason profile overrides are merged as documents: <c>ShowClock</c> defaults to true, so
    /// merging bound objects could not tell an explicit false from an unmentioned setting.
    /// </summary>
    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfileCanOverrideABooleanToItsDefaultOppositeValue(string resource)
    {
        var catalog = Load(resource);

        catalog.Default.GeneralSettings.ShowClock.Should().BeTrue();
        catalog.Get("kitchen").GeneralSettings.ShowClock.Should().BeFalse();
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfileWithoutAccountsInheritsThem(string resource)
    {
        Load(resource).Get("kitchen").Accounts.Select(account => account.ImmichServerUrl)
            .Should().Equal("https://base1.example.com", "https://base2.example.com");
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfileAccountsReplaceTheBaseAccountsRatherThanAddToThem(string resource)
    {
        Load(resource).Get("living-room").Accounts.Select(account => account.ImmichServerUrl)
            .Should().Equal("https://living.example.com");
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfileListsReplaceTheBaseListRatherThanAppendToIt(string resource)
    {
        Load(resource).Get("living-room").GeneralSettings.Webcalendars
            .Should().Equal("https://calendar.example.com/living.ics");
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void SettingsNoProfileMentionsStillFallBackToTheirDefaults(string resource)
    {
        var kitchen = Load(resource).Get("kitchen").GeneralSettings;

        kitchen.Layout.Should().Be(new GeneralSettings().Layout);
        kitchen.TransitionDuration.Should().Be(new GeneralSettings().TransitionDuration);
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void DefaultConfigIsUnaffectedByProfiles(string resource)
    {
        var @default = Load(resource).Default;

        @default.GeneralSettings.Interval.Should().Be(45);
        @default.GeneralSettings.ShowClock.Should().BeTrue();
        @default.GeneralSettings.PrimaryColor.Should().Be("#base");
        @default.Accounts.Should().HaveCount(2);
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfileWithoutAnyOverridesMatchesTheDefault(string resource)
    {
        var catalog = Load(resource);

        catalog.Get("empty").GeneralSettings.Should().BeEquivalentTo(catalog.Default.GeneralSettings);
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void ProfileLookupIsCaseInsensitive(string resource)
    {
        Load(resource).Get("KiTcHeN").GeneralSettings.Interval.Should().Be(10);
    }

    [TestCase(JsonProfiles)]
    [TestCase(YamlProfiles)]
    public void UnknownProfileIsReported(string resource)
    {
        var catalog = Load(resource);

        catalog.Invoking(c => c.Get("bedroom")).Should().Throw<ProfileNotFoundException>();
        catalog.TryGet("bedroom", out _).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    [TestCase("default")]
    [TestCase("DEFAULT")]
    public void DefaultConfigIsReturnedWhenNoProfileIsAskedFor(string? name)
    {
        var catalog = Load(JsonProfiles);

        catalog.Get(name).Should().BeSameAs(catalog.Default);
    }

    [Test]
    public void SettingsFileWithoutProfilesLoadsAsASingleDefaultConfig()
    {
        var catalog = Load("TestNoProfiles.json");

        catalog.ProfileNames.Should().BeEmpty();
        catalog.Default.GeneralSettings.Interval.Should().Be(45);
        catalog.Invoking(c => c.Get("kitchen")).Should().Throw<ProfileNotFoundException>();
    }

    [Test]
    public void ProfilesKeyDoesNotLeakIntoTheLoadedSettings()
    {
        // 'Profiles' is not a member of ServerSettings, so a leak would show up as a load failure
        // rather than a stray property - loading at all is the assertion.
        Load(JsonProfiles).Default.Should().BeOfType<ServerSettings>();
    }

    [TestCase("api")]
    [TestCase("static")]
    [TestCase("swagger")]
    [TestCase("default")]
    [TestCase("Default")]
    public void ReservedProfileNamesAreRejected(string name)
    {
        CatalogWithProfile(name).Should().Throw<SettingsNotValidException>()
            .WithMessage($"*'{name}' is a reserved name*");
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("has space")]
    [TestCase("kitchen/2")]
    [TestCase("kitchen.2")]
    [TestCase("kitchen?")]
    [TestCase("../etc")]
    [TestCase("kitchen\n")]
    public void InvalidProfileNamesAreRejected(string name)
    {
        CatalogWithProfile(name).Should().Throw<SettingsNotValidException>()
            .WithMessage("*is not a valid configuration profile name*");
    }

    /// <summary>
    /// A trailing newline must not smuggle a reserved name past the name check: '$' matches just
    /// before one, so anchoring on it would let 'api\n' through and shadow a backend path.
    /// </summary>
    [Test]
    public void ReservedProfileNameWithATrailingNewlineIsRejected()
    {
        CatalogWithProfile("api\n").Should().Throw<SettingsNotValidException>();
    }

    [Test]
    public void OverlyLongProfileNamesAreRejected()
    {
        CatalogWithProfile(new string('a', 65)).Should().Throw<SettingsNotValidException>()
            .WithMessage("*is not a valid configuration profile name*");
    }

    [Test]
    public void ProfileNameAtTheLengthLimitWithATrailingNewlineIsRejected()
    {
        CatalogWithProfile(new string('a', 64) + "\n").Should().Throw<SettingsNotValidException>()
            .WithMessage("*is not a valid configuration profile name*");
    }

    [Test]
    public void ProfileNamesDifferingOnlyInCaseAreRejected()
    {
        var settings = new Mock<IServerSettings>().Object;

        var construct = () => new ConfigCatalog(settings, [
            KeyValuePair.Create("kitchen", settings),
            KeyValuePair.Create("Kitchen", settings)
        ]);

        construct.Should().Throw<SettingsNotValidException>().WithMessage("*more than one*");
    }

    [Test]
    public void ValidateNamesTheProfileThatIsBroken()
    {
        var valid = new Mock<IServerSettings>();
        var broken = new Mock<IServerSettings>();
        broken.Setup(settings => settings.Validate()).Throws(new SettingsNotValidException("ApiKey is missing"));

        var catalog = new ConfigCatalog(valid.Object, [KeyValuePair.Create("kitchen", broken.Object)]);

        catalog.Invoking(c => c.Validate()).Should().Throw<SettingsNotValidException>()
            .WithMessage("*'kitchen'*ApiKey is missing*");
    }

    [Test]
    public void ValidateChecksTheDefaultConfigAndEveryProfile()
    {
        var @default = new Mock<IServerSettings>();
        var profile = new Mock<IServerSettings>();

        new ConfigCatalog(@default.Object, [KeyValuePair.Create("kitchen", profile.Object)]).Validate();

        @default.Verify(settings => settings.Validate(), Times.Once);
        profile.Verify(settings => settings.Validate(), Times.Once);
    }

    [Test]
    public void ProfilesLoadAndValidateFromAConfigDirectory()
    {
        InConfigDirectory("Settings.json", File.ReadAllText(ResourcePath(JsonProfiles)), directory =>
        {
            var catalog = _configLoader.LoadCatalog(directory);

            catalog.ProfileNames.Should().BeEquivalentTo("kitchen", "living-room", "empty");
            catalog.Get("kitchen").GeneralSettings.Interval.Should().Be(10);
        });
    }

    [Test]
    public void ProfileThatFailsValidationIsReportedByName()
    {
        const string settings = """
                                {
                                  "Accounts": [{ "ImmichServerUrl": "https://base.example.com", "ApiKey": "base-key" }],
                                  "Profiles": {
                                    "kitchen": { "Accounts": [{ "ImmichServerUrl": "https://kitchen.example.com" }] }
                                  }
                                }
                                """;

        InConfigDirectory("Settings.json", settings, directory =>
            _configLoader.Invoking(loader => loader.LoadCatalog(directory))
                .Should().Throw<SettingsNotValidException>().WithMessage("*'kitchen'*"));
    }

    /// <summary>
    /// A pre-'General'/'Accounts' settings file binds to an empty current-version config rather than
    /// failing, so the catalog has to reject it on shape for the V1 reader to get its turn.
    /// </summary>
    [Test]
    public void LegacySettingsFileStillLoadsAsASingleDefaultConfig()
    {
        const string settings = """{ "ImmichServerUrl": "https://v1.example.com", "ApiKey": "v1-key", "Interval": 12 }""";

        InConfigDirectory("Settings.json", settings, directory =>
        {
            var catalog = _configLoader.LoadCatalog(directory);

            catalog.ProfileNames.Should().BeEmpty();
            catalog.Default.Accounts.Select(account => account.ImmichServerUrl).Should().Equal("https://v1.example.com");
            catalog.Default.GeneralSettings.Interval.Should().Be(12);
        });
    }

    private static string ResourcePath(string resource)
        => Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", resource);

    private static void InConfigDirectory(string fileName, string contents, Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"immichframe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, fileName), contents);
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static Func<ConfigCatalog> CatalogWithProfile(string name)
    {
        var settings = new Mock<IServerSettings>().Object;

        return () => new ConfigCatalog(settings, [KeyValuePair.Create(name, settings)]);
    }
}
