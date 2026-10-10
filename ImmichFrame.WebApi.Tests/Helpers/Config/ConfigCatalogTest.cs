using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Database;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    [TestCase("admin")]
    [TestCase("AdMiN")]
    [TestCase("default")]
    [TestCase("Default")]
    public void ReservedProfileNamesAreRejected(string name)
    {
        CatalogWithProfile(name).Should().Throw<SettingsNotValidException>()
            .WithMessage($"*'{name}' is a reserved name*");
    }

    /// <summary>
    /// The paths the admin surface's OpenID Connect handler answers on, which a browser asking for
    /// the profile at <c>/{profile}</c> would reach instead of the web client. Reserved on every
    /// installation, not only those with the admin surface switched on: switching it on takes nothing
    /// but environment variables, and a profile that stopped opening the day someone did would be a
    /// stranger failure than one refused when it was named.
    /// </summary>
    [TestCase("signin-oidc")]
    [TestCase("SignIn-OIDC")]
    [TestCase("signout-callback-oidc")]
    [TestCase("SignOut-Callback-Oidc")]
    [TestCase("signout-oidc")]
    [TestCase("SIGNOUT-OIDC")]
    public void OpenIdConnectHandshakePathsAreRejectedAsProfileNames(string name)
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

    /// <summary>
    /// Every configuration that fails is reported in the one exception, each by name - the default
    /// configuration's failure included, which used to end validation there, in its own words and
    /// naming nothing. The first failure stays where a single one would be: down the inner chain.
    /// </summary>
    [Test]
    public void ValidateReportsEveryConfigurationThatFailsByName()
    {
        var first = new SettingsNotValidException("ImagesFromDays is out of range.");
        var second = new SettingsNotValidException("ApiKey is missing.");

        var @default = new Mock<IServerSettings>();
        @default.Setup(settings => settings.Validate()).Throws(first);
        var kitchen = new Mock<IServerSettings>();
        kitchen.Setup(settings => settings.Validate()).Throws(second);
        var hallway = new Mock<IServerSettings>();

        var catalog = new ConfigCatalog(@default.Object,
            [KeyValuePair.Create("kitchen", kitchen.Object), KeyValuePair.Create("hallway", hallway.Object)]);

        var failure = catalog.Invoking(c => c.Validate()).Should().Throw<SettingsNotValidException>().Which;

        failure.Message.Should().Match("*default configuration*ImagesFromDays is out of range.*'kitchen'*ApiKey is missing.*");
        Chain(failure).Should().Contain(first).And.NotContain(second);
        hallway.Verify(settings => settings.Validate(), Times.Once);
    }

    /// <summary>
    /// The reason validation goes on past an account that fails it. Validating an account is also what
    /// reads its ApiKeyFile into its key, and stored settings that fail validation are run regardless,
    /// so stopping at the first failure left every account after it - in its own configuration and in
    /// every configuration validated after that one - running with an empty key.
    /// </summary>
    [Test]
    public void ValidateReadsEveryKeyFilePastAnAccountThatFails()
    {
        InTemporaryDirectory(directory =>
        {
            var secondKey = Path.Combine(directory, "second-key");
            var kitchenKey = Path.Combine(directory, "kitchen-key");
            File.WriteAllText(secondKey, "second-key\n");
            File.WriteAllText(kitchenKey, "kitchen-key\n");

            var catalog = ConfigLoader.BuildCatalog(ConfigLoader.CreateDocument(ConfigFormat.Json, $$"""
                {
                  "Accounts": [
                    { "ImmichServerUrl": "https://first.example.com", "ApiKey": "first-key", "ImagesFromDays": 99999 },
                    { "ImmichServerUrl": "https://second.example.com", "ApiKeyFile": {{JsonSerializer.Serialize(secondKey)}} }
                  ],
                  "Profiles": {
                    "kitchen": {
                      "Accounts": [{ "ImmichServerUrl": "https://kitchen.example.com", "ApiKeyFile": {{JsonSerializer.Serialize(kitchenKey)}} }]
                    }
                  }
                }
                """));

            catalog.Invoking(c => c.Validate()).Should().Throw<SettingsNotValidException>()
                .WithMessage("*default configuration*ImagesFromDays*");

            catalog.Default.Accounts.Select(account => account.ApiKey).Should().Equal("first-key", "second-key");
            catalog.Get("kitchen").Accounts.Single().ApiKey.Should().Be("kitchen-key");
        });
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    [Test]
    public void ProfilesLoadAndValidateFromAConfigDirectory()
    {
        InConfigDirectory("Settings.json", File.ReadAllText(ResourcePath(JsonProfiles)), directory =>
        {
            var log = new CriticalLog();
            var catalog = Served(directory, log);

            catalog.ProfileNames.Should().BeEquivalentTo("kitchen", "living-room", "empty");
            catalog.Get("kitchen").GeneralSettings.Interval.Should().Be(10);
            log.Lines.Should().BeEmpty("every configuration in the file validates");
        });
    }

    /// <summary>
    /// Stored settings that fail validation are served anyway, because the editor that fixes them is
    /// served by the same process - so the Critical line they are reported in is where the profile at
    /// fault has to be named.
    /// </summary>
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
        {
            var log = new CriticalLog();
            var catalog = Served(directory, log);

            log.Lines.Should().ContainSingle().Which.Should().Contain("'kitchen'");
            catalog.Get("kitchen").Accounts.Single().ImmichServerUrl.Should().Be("https://kitchen.example.com");
            catalog.Default.Accounts.Single().ApiKey.Should().Be("base-key");
        });
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
            var catalog = Served(directory);

            catalog.ProfileNames.Should().BeEmpty();
            catalog.Default.Accounts.Select(account => account.ImmichServerUrl).Should().Equal("https://v1.example.com");
            catalog.Default.GeneralSettings.Interval.Should().Be(12);
        });
    }

    /// <summary>
    /// A v1 file's lists spelled as null. The v1 reader calls each setter with that null, over the
    /// empty list the class starts out with, and the adapter hands the list straight on - as a
    /// configuration here, and to the conversion on import - behind interfaces that say these lists
    /// are never null.
    /// </summary>
    [TestCase("Settings.json", """
                               {
                                 "ImmichServerUrl": "https://v1.example.com",
                                 "ApiKey": "v1-key",
                                 "Webcalendars": null,
                                 "Albums": null,
                                 "ExcludedAlbums": null,
                                 "People": null,
                                 "Tags": null
                               }
                               """, TestName = "LegacySettingsFileWithNullListsLoadsThemAsEmpty")]
    [TestCase("Settings.yml", """
                              ImmichServerUrl: https://v1.example.com
                              ApiKey: v1-key
                              Webcalendars:
                              Albums:
                              ExcludedAlbums:
                              People:
                              Tags:
                              """, TestName = "LegacyYamlFileWithBareListKeysLoadsThemAsEmpty")]
    public void LegacySettingsFileWithListsSpelledAsNullLoadsThemAsEmpty(string fileName, string settings)
    {
        InConfigDirectory(fileName, settings, directory =>
        {
            var catalog = Served(directory);
            var account = catalog.Default.Accounts.Single();

            catalog.Default.GeneralSettings.Webcalendars.Should().NotBeNull().And.BeEmpty();
            account.Albums.Should().NotBeNull().And.BeEmpty();
            account.ExcludedAlbums.Should().NotBeNull().And.BeEmpty();
            account.People.Should().NotBeNull().And.BeEmpty();
            account.Tags.Should().NotBeNull().And.BeEmpty();
        });
    }

    /// <summary>
    /// An empty entry in a list of strings - a null in JSON, a bare <c>-</c> or a <c>~</c> in YAML -
    /// binds as a null string, which a setter replacing only a null list let through. The calendar
    /// service calls <c>Replace</c> on every calendar URL on every poll and <c>AccountSearchPool</c>
    /// looks every tag up as a dictionary key, so one null failed every <c>GET /api/Calendar</c>, or
    /// every request for the account's photos. An entry that is only whitespace names nothing either.
    /// </summary>
    [TestCase(ConfigFormat.Json, """
                                 {
                                   "General": { "Webcalendars": [null, "https://calendar.example.com/c.ics", " "] },
                                   "Accounts": [{
                                     "ImmichServerUrl": "https://base.example.com",
                                     "ApiKey": "base-key",
                                     "Tags": [null, "Travel", ""]
                                   }]
                                 }
                                 """, TestName = "SettingsFileListEntriesSpelledAsNullAreDropped")]
    [TestCase(ConfigFormat.Yaml, """
                                 General:
                                   Webcalendars:
                                   -
                                   - https://calendar.example.com/c.ics
                                   - ' '
                                 Accounts:
                                 - ImmichServerUrl: https://base.example.com
                                   ApiKey: base-key
                                   Tags:
                                   - ~
                                   - Travel
                                   -
                                 """, TestName = "YamlListEntriesLeftEmptyAreDropped")]
    public void ListEntriesSpelledAsNullAreDropped(ConfigFormat format, string settings)
    {
        var catalog = ConfigLoader.BuildCatalog(ConfigLoader.CreateDocument(format, settings));

        catalog.Default.GeneralSettings.Webcalendars.Should().Equal("https://calendar.example.com/c.ics");
        catalog.Default.Accounts.Single().Tags.Should().Equal("Travel");
    }

    /// <summary>
    /// The same empty entries in a v1 file, whose lists the adapter hands straight on - as a
    /// configuration here, and into the conversion on import.
    /// </summary>
    [Test]
    public void LegacySettingsFileListEntriesSpelledAsNullAreDropped()
    {
        const string settings = """
                                {
                                  "ImmichServerUrl": "https://v1.example.com",
                                  "ApiKey": "v1-key",
                                  "Webcalendars": [null, "https://calendar.example.com/c.ics", " "],
                                  "Tags": [null, "Travel", ""]
                                }
                                """;

        InConfigDirectory("Settings.json", settings, directory =>
        {
            var catalog = Served(directory);

            catalog.Default.GeneralSettings.Webcalendars.Should().Equal("https://calendar.example.com/c.ics");
            catalog.Default.Accounts.Single().Tags.Should().Equal("Travel");
        });
    }

    /// <summary>
    /// A YAML <c>Profiles</c> key spelled as null declares no profiles, however the null is spelled -
    /// the spellings the binder itself reads as null, so the two cannot disagree about one file.
    /// </summary>
    [TestCase("")]
    [TestCase("~")]
    [TestCase("null")]
    [TestCase("Null")]
    [TestCase("NULL")]
    public void YamlProfilesSpelledAsNullDeclareNoProfiles(string value)
    {
        new YamlConfigDocument($"General:\n  Interval: 45\nProfiles: {value}\n").ProfileNames.Should().BeEmpty();
    }

    /// <summary>
    /// The JSON spellings of the same thing: null, and the empty string YAML's <c>Profiles: ''</c> was
    /// always read as. Refusing <c>""</c> sent a current-schema file down the loader's v1 fallback,
    /// as refusing <c>Profiles: ~</c> once did for YAML.
    /// </summary>
    [TestCase("null")]
    [TestCase("\"\"")]
    public void JsonProfilesSpelledAsNullOrEmptyDeclareNoProfiles(string value)
    {
        var document = new JsonConfigDocument($$"""{ "General": { "Interval": 45 }, "Profiles": {{value}} }""");

        document.ProfileNames.Should().BeEmpty();
        ConfigLoader.BuildCatalog(document).ProfileNames.Should().BeEmpty();
    }

    /// <summary>Any other string is not an object of profiles, and is still refused.</summary>
    [TestCase("\" \"")]
    [TestCase("\"kitchen\"")]
    public void JsonProfilesSpelledAsAnyOtherStringAreRejected(string value)
    {
        var document = new JsonConfigDocument($$"""{ "General": { "Interval": 45 }, "Profiles": {{value}} }""");

        document.Invoking(d => d.ProfileNames).Should().Throw<SettingsNotValidException>()
            .WithMessage("*must be an object*");
    }

    /// <summary>Quoted, the same characters are a string - which is not a mapping of profiles either.</summary>
    [TestCase("'null'")]
    [TestCase("'~'")]
    [TestCase("\"null\"")]
    public void YamlProfilesQuotedAsAStringAreRejected(string value)
    {
        var document = new YamlConfigDocument($"General:\n  Interval: 45\nProfiles: {value}\n");

        document.Invoking(d => d.ProfileNames).Should().Throw<SettingsNotValidException>()
            .WithMessage("*must be a mapping*");
    }

    /// <summary>
    /// What refusing <c>Profiles: ~</c> actually cost: the refusal sent a perfectly good current-schema
    /// file down the loader's v1 fallback, which binds it to a single account with no server and no
    /// key - and on first run that is what got imported in place of the real ones.
    /// </summary>
    [Test]
    public void YamlSettingsFileWithNullProfilesLoadsItsOwnAccountsRatherThanTheV1Fallback()
    {
        const string settings = """
                                General:
                                  Interval: 12
                                Accounts:
                                - ImmichServerUrl: https://base.example.com
                                  ApiKey: base-key
                                Profiles: ~
                                """;

        InConfigDirectory("Settings.yml", settings, directory =>
        {
            var catalog = Served(directory);

            catalog.Default.Accounts.Select(account => account.ImmichServerUrl).Should().Equal("https://base.example.com");
            catalog.Default.GeneralSettings.Interval.Should().Be(12);
            catalog.ProfileNames.Should().BeEmpty();
        });
    }

    /// <summary>
    /// A current-schema file that names no accounts - no <c>Accounts</c> key at all, or one spelled
    /// null - is a configuration with nothing to show, not a broken one. It used to bind to a null
    /// account list, which validation dereferenced as "Object reference not set to an instance of an
    /// object." The explicit null is its own case because it overwrites whatever the settings class
    /// starts the list out as.
    /// </summary>
    [TestCase("""{ "General": { "Interval": 45 } }""",
        TestName = "SettingsFileWithNoAccountsKeyLoadsWithAnEmptyAccountList")]
    [TestCase("""{ "General": { "Interval": 45 }, "Accounts": null }""",
        TestName = "SettingsFileWithNullAccountsLoadsWithAnEmptyAccountList")]
    public void SettingsFileNamingNoAccountsLoadsWithAnEmptyAccountList(string settings)
    {
        InConfigDirectory("Settings.json", settings, directory =>
        {
            var log = new CriticalLog();
            var catalog = Served(directory, log);

            log.Lines.Should().BeEmpty("a configuration with nothing to show still validates");
            catalog.Default.Accounts.Should().NotBeNull().And.BeEmpty();
        });
    }

    /// <summary>
    /// A profile's own <c>Accounts:</c> with nothing after it, or JSON's <c>"Accounts": null</c>. The
    /// merge replaces the inherited list with that null outright, as it replaces any list, so the
    /// profile has to bind to an empty list rather than a null one - everything downstream of the
    /// binder enumerates it.
    /// </summary>
    [TestCase(ConfigFormat.Yaml, """
                                 General:
                                   Interval: 45
                                 Accounts:
                                 - ImmichServerUrl: https://base.example.com
                                   ApiKey: base-key
                                 Profiles:
                                   kitchen:
                                     Accounts:
                                 """, TestName = "YamlProfileWithAnEmptyAccountsKeyBindsToAnEmptyAccountList")]
    [TestCase(ConfigFormat.Json, """
                                 {
                                   "General": { "Interval": 45 },
                                   "Accounts": [{ "ImmichServerUrl": "https://base.example.com", "ApiKey": "base-key" }],
                                   "Profiles": { "kitchen": { "Accounts": null } }
                                 }
                                 """, TestName = "JsonProfileWithNullAccountsBindsToAnEmptyAccountList")]
    public void ProfileWithAccountsSpelledAsNullBindsToAnEmptyAccountList(ConfigFormat format, string settings)
    {
        var kitchen = ConfigLoader.BuildCatalog(ConfigLoader.CreateDocument(format, settings)).Get("kitchen");

        kitchen.Accounts.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// The other side of that rule: a <c>General</c> section that is there but empty - YAML's
    /// <c>General:</c> with the only setting under it commented out, or JSON's <c>"General": null</c> -
    /// is a profile naming no general setting, so it inherits every one of them. It used to replace
    /// the default configuration's whole section with nothing: the profile lost each value it
    /// inherited, the frame's secret among them, and served without authentication.
    /// </summary>
    [TestCase(ConfigFormat.Json, """
                                 {
                                   "General": { "Interval": 12, "AuthenticationSecret": "frame-secret" },
                                   "Accounts": [{ "ImmichServerUrl": "https://base.example.com", "ApiKey": "base-key" }],
                                   "Profiles": { "kitchen": { "General": null } }
                                 }
                                 """, TestName = "JsonProfileWithANullGeneralSectionInheritsTheDefaultOne")]
    [TestCase(ConfigFormat.Yaml, """
                                 General:
                                   Interval: 12
                                   AuthenticationSecret: frame-secret
                                 Accounts:
                                 - ImmichServerUrl: https://base.example.com
                                   ApiKey: base-key
                                 Profiles:
                                   kitchen:
                                     General:
                                       # AuthenticationSecret: null
                                 """, TestName = "YamlProfileWithItsOnlyGeneralSettingCommentedOutInheritsTheDefaultOne")]
    public void ProfileWithAnEmptyGeneralSectionInheritsTheDefaultOne(ConfigFormat format, string settings)
    {
        var kitchen = ConfigLoader.BuildCatalog(ConfigLoader.CreateDocument(format, settings)).Get("kitchen").GeneralSettings;

        kitchen.AuthenticationSecret.Should().Be("frame-secret");
        kitchen.Interval.Should().Be(12);
    }

    /// <summary>
    /// A key written into the settings arrives with the line break it was copied with as readily as
    /// one read from a key file: a JSON string that kept it, or a YAML block scalar, which keeps the
    /// one it ends on. The key becomes an X-API-KEY header, and a header cannot carry a line break.
    /// </summary>
    [TestCase("Settings.json", """
                               {
                                 "Accounts": [{ "ImmichServerUrl": "https://base.example.com", "ApiKey": "abc123\n" }]
                               }
                               """, TestName = "SettingsFileApiKeyWithATrailingNewlineLoadsTrimmed")]
    [TestCase("Settings.yml", """
                              Accounts:
                              - ApiKey: |
                                  abc123
                                ImmichServerUrl: https://base.example.com
                              """, TestName = "YamlBlockScalarApiKeyLoadsTrimmed")]
    public void ApiKeyWithATrailingNewlineLoadsTrimmed(string fileName, string settings)
    {
        InConfigDirectory(fileName, settings, directory =>
            Served(directory).Default.Accounts.Single().ApiKey.Should().Be("abc123"));
    }

    /// <summary>
    /// The weather API key arrives padded as readily as an account's: pasted with a space or the line
    /// break it was copied with, or ending a YAML block scalar. OpenWeatherMap takes it in its query
    /// string, padding and all, and refuses every request made with it.
    /// </summary>
    [TestCase("Settings.json", """
                               {
                                 "General": { "WeatherApiKey": " 0123456789abcdef\n" },
                                 "Accounts": [{ "ImmichServerUrl": "https://base.example.com", "ApiKey": "base-key" }]
                               }
                               """, TestName = "SettingsFileWeatherApiKeyWithSurroundingWhitespaceLoadsTrimmed")]
    [TestCase("Settings.yml", """
                              General:
                                WeatherApiKey: |
                                  0123456789abcdef
                              Accounts:
                              - ImmichServerUrl: https://base.example.com
                                ApiKey: base-key
                              """, TestName = "YamlBlockScalarWeatherApiKeyLoadsTrimmed")]
    public void WeatherApiKeyWithSurroundingWhitespaceLoadsTrimmed(string fileName, string settings)
    {
        InConfigDirectory(fileName, settings, directory =>
            Served(directory).Default.GeneralSettings.WeatherApiKey.Should().Be("0123456789abcdef"));
    }

    /// <summary>
    /// A line break inside the key, which trimming cannot help, is refused while the settings are
    /// validated. Accepted, it failed only once the account's services were built - inside a Lazy that
    /// caches the exception, so every request for the profile failed until the configuration was
    /// replaced or the process restarted. Stored settings are reported in the log rather than thrown,
    /// and the editor shows its own refusal of a save, so neither may quote the key.
    /// </summary>
    [Test]
    public void SettingsFileApiKeyWithALineBreakInsideIsReportedWithoutQuotingIt()
    {
        const string settings = """
                                {
                                  "Accounts": [{ "ImmichServerUrl": "https://base.example.com", "ApiKey": "abc\n123" }]
                                }
                                """;

        InConfigDirectory("Settings.json", settings, directory =>
        {
            var log = new CriticalLog();
            Served(directory, log);

            log.Lines.Should().ContainSingle()
                .Which.Should().Contain("API key").And.NotContain("abc").And.NotContain("123");
        });
    }

    /// <summary>
    /// <c>ImagesFromDays</c> is taken off today's date on every request for the account's photos. A
    /// number of days <see cref="DateTime"/> cannot count back - <c>int.MaxValue</c>, or
    /// <c>int.MinValue</c>, whose negation is itself - threw there, so every one of those requests
    /// failed; and any other negative number moved the cut-off into the future, where no photo is.
    /// Refused while the settings are validated, which the admin editor's save runs as well.
    /// </summary>
    [TestCase(int.MaxValue)]
    [TestCase(36501)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void ImagesFromDaysOutsideItsRangeIsRefused(int days)
    {
        var catalog = ConfigLoader.BuildCatalog(ConfigLoader.CreateDocument(ConfigFormat.Json, AccountShowingImagesFromDays(days)));

        catalog.Invoking(c => c.Validate())
            .Should().Throw<SettingsNotValidException>().WithMessage("*ImagesFromDays*0*36500*");
    }

    /// <summary>
    /// Left out, it is no limit at all; 0 is today's photos alone, the cut-off being today's
    /// midnight; and both ends of the range are its own.
    /// </summary>
    [TestCase(null)]
    [TestCase(0)]
    [TestCase(30)]
    [TestCase(36500)]
    public void ImagesFromDaysInsideItsRangeIsAccepted(int? days)
    {
        var catalog = ConfigLoader.BuildCatalog(ConfigLoader.CreateDocument(ConfigFormat.Json, AccountShowingImagesFromDays(days)));

        catalog.Invoking(c => c.Validate()).Should().NotThrow();
        catalog.Default.Accounts.Single().ImagesFromDays.Should().Be(days);
    }

    private static string AccountShowingImagesFromDays(int? days) =>
        $$"""
          {
            "Accounts": [{
              "ImmichServerUrl": "https://base.example.com",
              "ApiKey": "base-key",
              "ImagesFromDays": {{days?.ToString(CultureInfo.InvariantCulture) ?? "null"}}
            }]
          }
          """;

    private static string ResourcePath(string resource)
        => Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", resource);

    /// <summary>
    /// What ImmichFrame serves out of a configuration directory: the settings file imported into a
    /// settings database there, as a first run does, and the stored document then bound, validated and
    /// served by <see cref="SettingsService.LoadCatalog"/> - the seed of the catalog everything else
    /// injects, and the only path a settings file takes into a running ImmichFrame.
    /// </summary>
    private IConfigCatalog Served(string directory, ILogger<SettingsService>? logger = null)
    {
        // Unpooled, so that no connection outlives the call and holds the database file open.
        using var services = new ServiceCollection()
            .AddDbContextFactory<SettingsDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory, "immichframe.db")};Pooling=False"))
            .BuildServiceProvider();

        var store = new SettingsService(
            services.GetRequiredService<IDbContextFactory<SettingsDbContext>>(),
            _configLoader,
            new ConfigLocation(directory),
            logger ?? NullLogger<SettingsService>.Instance);

        store.InitializeAsync().GetAwaiter().GetResult();

        return store.LoadCatalog();
    }

    /// <summary>
    /// The Critical lines a <see cref="SettingsService"/> logs, which is where stored settings that do
    /// not validate are reported: they are served regardless rather than thrown.
    /// </summary>
    private sealed class CriticalLog : ILogger<SettingsService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Critical) Lines.Add(formatter(state, exception));
        }
    }

    private static void InConfigDirectory(string fileName, string contents, Action<string> test) =>
        InTemporaryDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, fileName), contents);
            test(directory);
        });

    private static void InTemporaryDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"immichframe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
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
