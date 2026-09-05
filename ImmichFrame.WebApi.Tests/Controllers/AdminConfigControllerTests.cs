using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers;

/// <summary>
/// The configuration editor's API over the real host, with a real settings file in a temporary
/// directory - the only way to exercise what this endpoint is for: that a save is validated before
/// the file is touched, that it keeps profiles sparse, and that it reaches the running
/// configuration without a restart.
/// </summary>
[TestFixture]
public class AdminConfigControllerTests
{
    private const string AdminSubject = "8ab0c1e2-user";
    private const string OtherSubject = "not-an-admin";
    private const string FrameSecret = "frame-secret";
    private const string ConfigUrl = "/api/admin/config";

    private const string SettingsJson = """
        {
          "General": {
            "Interval": 45,
            "ShowClock": true,
            "PrimaryColor": "#base",
            "AuthenticationSecret": "frame-secret",
            "WeatherApiKey": "weather-key",
            "Webhook": "https://hook.example.com/base"
          },
          "Accounts": [
            {
              "ImmichServerUrl": "http://mock-immich-server.com",
              "ApiKey": "base-api-key"
            }
          ],
          "Profiles": {
            "kitchen": {
              "General": {
                "Interval": 10,
                "ShowClock": false
              }
            },
            "living-room": {
              "General": {
                "PrimaryColor": "#living"
              }
            }
          }
        }
        """;

    private const string SettingsYaml = """
        General:
          Interval: 45
          PrimaryColor: '#base'
        Accounts:
        - ImmichServerUrl: http://mock-immich-server.com
          ApiKey: base-api-key
        Profiles:
          kitchen:
            General:
              Interval: 10
        """;

    private const string TwoAccountsJson = """
        {
          "General": {
            "Interval": 45
          },
          "Accounts": [
            {
              "ImmichServerUrl": "http://server-x.example.com",
              "ApiKey": "key-x",
              "ShowVideos": true
            },
            {
              "ImmichServerUrl": "http://server-y.example.com",
              "ApiKey": "key-y"
            }
          ]
        }
        """;

    private const string ApiKeyFileJson = """
        {
          "General": {
            "Interval": 45
          },
          "Accounts": [
            {
              "ImmichServerUrl": "http://mock-immich-server.com",
              "ApiKeyFile": "API_KEY_PATH"
            }
          ]
        }
        """;

    private const string AnchoredYaml = """
        General: &shared
          Interval: 45
          PrimaryColor: '#base'
        Accounts:
        - ImmichServerUrl: http://mock-immich-server.com
          ApiKey: base-api-key
        Profiles:
          kitchen:
            General: *shared
        """;

    private const string SettingsV1Json = """
        {
          "ImmichServerUrl": "http://mock-immich-server.com",
          "ApiKey": "base-api-key",
          "Interval": 45
        }
        """;

    private static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web);

    private string _directory = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"immichframe-admin-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (!Directory.Exists(_directory)) return;

        // A read-only case may have taken the write bit off the directory itself.
        SetMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task GetConfig_MasksEverySecretAndReportsWhetherItIsSet()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await Send(client, HttpMethod.Get, ConfigUrl);
        var body = await response.Content.ReadAsStringAsync();
        var config = JsonNode.Parse(body);
        var general = config?["default"]?["general"];

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // The values themselves never leave the server, whatever the editor asks for.
            Assert.That(body, Does.Not.Contain("frame-secret"));
            Assert.That(body, Does.Not.Contain("weather-key"));
            Assert.That(body, Does.Not.Contain("base-api-key"));
            Assert.That(body, Does.Not.Contain("hook.example.com"));

            // ...but the editor still has to be able to say "a key is set".
            Assert.That((bool?)general?["hasAuthenticationSecret"], Is.True);
            Assert.That((bool?)general?["hasWeatherApiKey"], Is.True);
            Assert.That((bool?)general?["hasWebhook"], Is.True);
            Assert.That((bool?)config?["default"]?["accounts"]?[0]?["hasApiKey"], Is.True);
            Assert.That((bool?)config?["default"]?["accounts"]?[0]?["apiKeyFromFile"], Is.False);
        });
    }

    /// <summary>
    /// The read half of the sparse-override problem: a profile's own keys have to be
    /// distinguishable from the ones it inherits, or the editor cannot show which is which.
    /// </summary>
    [Test]
    public async Task GetConfig_SeparatesWhatAProfileDeclaresFromWhatItInherits()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();

        var config = await GetConfig(factory.CreateClient());
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");

        Assert.Multiple(() =>
        {
            Assert.That(kitchen.DeclaredKeys,
                Is.EquivalentTo(new[] { "General.Interval", "General.ShowClock" }));

            // Declared, so the profile's own value...
            Assert.That(kitchen.General.Interval, Is.EqualTo(10));
            Assert.That(kitchen.General.ShowClock, Is.False);

            // ...and not declared, so the default configuration's.
            Assert.That(kitchen.General.PrimaryColor, Is.EqualTo("#base"));

            Assert.That(config.Default.DeclaredKeys, Does.Contain("General.Interval"));
            Assert.That(config.Default.DeclaredKeys, Does.Contain("Accounts"));
            Assert.That(config.Default.DeclaredKeys, Does.Not.Contain("General.Layout"),
                "the default configuration declares only what the file says, not every setting's default");
        });
    }

    /// <summary>
    /// The write half, and the one that would bite silently: saving a profile back as its merged
    /// result would turn every inherited value into an override, and the next edit to the default
    /// configuration would stop reaching it.
    /// </summary>
    [Test]
    public async Task SaveConfig_ChangingOneProfile_LeavesTheOthersDeclaringExactlyWhatTheyDid()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Profiles.Single(profile => profile.Name == "kitchen").General.Interval = 99;

        var response = await Put(client, config);
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath("Settings.json")));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Every profile still declares exactly the keys it declared, the edited one included.
            Assert.That(saved?["Profiles"]?["kitchen"]?["General"]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "Interval", "ShowClock" }));
            Assert.That(saved?["Profiles"]?["living-room"]?["General"]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "PrimaryColor" }));
            Assert.That((string?)saved?["Profiles"]?["living-room"]?["General"]?["PrimaryColor"],
                Is.EqualTo("#living"));

            Assert.That((int?)saved?["Profiles"]?["kitchen"]?["General"]?["Interval"], Is.EqualTo(99));
        });
    }

    [Test]
    public async Task SaveConfig_OmittedSecretsAreKeptAndSuppliedOnesReplaceThem()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        // The browser never had the stored values, so it sends back exactly what it was given: a
        // masked field and nothing else. Only the weather key is actually typed.
        config.Default.General.WeatherApiKey = "typed-by-the-administrator";
        config.Default.General.AuthenticationSecret = AdminSecret.Placeholder;

        var response = await Put(client, config);
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath("Settings.json")));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((string?)saved?["General"]?["WeatherApiKey"], Is.EqualTo("typed-by-the-administrator"));
            Assert.That((string?)saved?["General"]?["AuthenticationSecret"], Is.EqualTo(FrameSecret));
            Assert.That((string?)saved?["General"]?["Webhook"], Is.EqualTo("https://hook.example.com/base"));
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("base-api-key"));
        });
    }

    [Test]
    public async Task SaveConfig_InvalidConfiguration_IsRejectedWithTheFileUntouched()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = File.ReadAllText(SettingsPath("Settings.json"));

        var config = await GetConfig(client);
        // Neither an ApiKey nor an ApiKeyFile: ValidateAndInitialize refuses it, locally, without
        // ever contacting Immich.
        config.Default.Accounts[0].ApiKey = string.Empty;

        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("ApiKey"), "the operator has to be told what is wrong");
            Assert.That(File.ReadAllText(SettingsPath("Settings.json")), Is.EqualTo(before),
                "a rejected configuration must never reach the file");
            Assert.That(Directory.EnumerateFiles(_directory), Has.Exactly(1).Items,
                "and must leave no backup or temporary file behind either");
        });
    }

    [Test]
    public async Task SaveConfig_StaleVersionToken_Is409WithTheFileUntouched()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        // Somebody else - a second administrator, or a hand edit - got there first.
        File.WriteAllText(SettingsPath("Settings.json"), SettingsJson.Replace("\"Interval\": 45", "\"Interval\": 46"));
        var before = File.ReadAllText(SettingsPath("Settings.json"));

        var response = await Put(client, config);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(File.ReadAllText(SettingsPath("Settings.json")), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// What the swappable catalog was built for: the save has to reach the configuration requests
    /// are served from, not just the file.
    /// </summary>
    [Test]
    public async Task SaveConfig_AppliesToTheRunningConfigurationWithoutARestart()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = await FrameInterval(client, "/api/Config?profile=kitchen");

        var config = await GetConfig(client);
        config.Profiles.Single(profile => profile.Name == "kitchen").General.Interval = 99;
        var response = await Put(client, config);
        var after = await FrameInterval(client, "/api/Config?profile=kitchen");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(before, Is.EqualTo(10));
            Assert.That(after, Is.EqualTo(99));
        });
    }

    [Test]
    public async Task SaveConfig_DeletingAndCreatingProfiles()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");

        // Drop living-room, and add a new profile that overrides one setting.
        config = config with
        {
            Profiles =
            [
                kitchen,
                new AdminConfigEntryDto("hallway", ["General.Interval"],
                    new AdminGeneralSettingsDto { Interval = 7 }, [])
            ]
        };

        var response = await Put(client, config);
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath("Settings.json")));
        var hallway = await FrameInterval(client, "/api/Config?profile=hallway");
        var gone = await client.GetAsync("/api/Config?profile=living-room");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(saved?["Profiles"]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "kitchen", "hallway" }));
            Assert.That(hallway, Is.EqualTo(7));
            Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [TestCase("admin", TestName = "SaveConfig_ReservedProfileName_IsRejected")]
    [TestCase("KITCHEN", TestName = "SaveConfig_CaseInsensitivelyDuplicateProfileName_IsRejected")]
    [TestCase("bad name", TestName = "SaveConfig_MalformedProfileName_IsRejected")]
    public async Task SaveConfig_UnacceptableProfileName_IsRejected(string name)
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = File.ReadAllText(SettingsPath("Settings.json"));

        var config = await GetConfig(client);
        config = config with
        {
            Profiles =
            [
                config.Profiles.Single(profile => profile.Name == "kitchen"),
                new AdminConfigEntryDto(name, ["General.Interval"], new AdminGeneralSettingsDto { Interval = 7 }, [])
            ]
        };

        var response = await Put(client, config);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(File.ReadAllText(SettingsPath("Settings.json")), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task SaveConfig_YamlInstallation_StaysYaml()
    {
        WriteSettings("Settings.yml", SettingsYaml);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Profiles.Single(profile => profile.Name == "kitchen").General.Interval = 99;

        var response = await Put(client, config);
        var saved = File.ReadAllText(SettingsPath("Settings.yml"));
        var after = await FrameInterval(client, "/api/Config?profile=kitchen");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(config.Source.Format, Is.EqualTo("yaml"));
            Assert.That(saved, Does.Not.Contain("{"), "a YAML installation stays YAML");
            // A YAML document's scalars carry no type, so this is the case where writing numbers back
            // as quoted strings would slip through unnoticed.
            Assert.That(saved, Does.Contain("Interval: 99"));
            Assert.That(after, Is.EqualTo(99));
        });
    }

    [Test]
    public async Task SaveConfig_LegacySchemaFile_NeedsExplicitConsentAndThenConverts()
    {
        WriteSettings("Settings.json", SettingsV1Json);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var refused = await Put(client, config);

        var converted = await Put(client, Update(config) with { ConvertLegacySchema = true });
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath("Settings.json")));

        Assert.Multiple(() =>
        {
            Assert.That(config.Source.LegacySchema, Is.True);
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
                "rewriting a v1 file in the current schema is not something to do silently");

            Assert.That(converted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(saved?["General"], Is.Not.Null);
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("base-api-key"));
        });
    }

    [Test]
    [Platform("Unix")]
    public async Task SaveConfig_ReadOnlyConfigurationDirectory_Is409WithTheFileUntouched()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.General.Interval = 99;

        var before = File.ReadAllText(SettingsPath("Settings.json"));
        SetMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(problem, Does.Contain(_directory), "the operator has to be told which directory");
            Assert.That(File.ReadAllText(SettingsPath("Settings.json")), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// An installation whose account keeps its key in a file, saved unchanged. The editor sends the
    /// masking placeholder back in the ApiKey field like any other form value, and writing it
    /// verbatim would put both an ApiKey and an ApiKeyFile in the settings file - which the loader
    /// refuses outright, leaving such an installation unable to save its configuration at all.
    /// </summary>
    [Test]
    public async Task SaveConfig_AccountWhoseKeyLivesInAFile_RoundTripsUnchanged()
    {
        var keyPath = Path.Combine(_directory, "api-key");
        File.WriteAllText(keyPath, "key-from-a-file\n");
        WriteSettings("Settings.json", ApiKeyFileJson.Replace("API_KEY_PATH", keyPath));

        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var account = config.Default.Accounts[0];

        // What the editor would send back for an account it was never given a key for.
        account.ApiKey = AdminSecret.Placeholder;

        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath("Settings.json")));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), problem);
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKeyFile"], Is.EqualTo(keyPath));
            Assert.That(saved?["Accounts"]?[0]?.AsObject().Select(pair => pair.Key),
                Does.Not.Contain("ApiKey"),
                "an ApiKey beside an ApiKeyFile is exactly what the loader refuses");
            Assert.That(File.ReadAllText(SettingsPath("Settings.json")),
                Does.Not.Contain(AdminSecret.Placeholder));
        });
    }

    /// <summary>
    /// And the read has to say the account has a key. These settings are bound without
    /// ValidateAndInitialize, so the ApiKey property is empty for an ApiKeyFile account - reporting
    /// that as "no key set" would show a working account as an unconfigured one and invite an
    /// administrator to type a key it cannot accept alongside the file.
    /// </summary>
    [Test]
    public async Task GetConfig_AccountWhoseKeyLivesInAFile_ReportsTheKeyAsPresentAndFromAFile()
    {
        var keyPath = Path.Combine(_directory, "api-key");
        File.WriteAllText(keyPath, "key-from-a-file\n");
        WriteSettings("Settings.json", ApiKeyFileJson.Replace("API_KEY_PATH", keyPath));

        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await Send(client, HttpMethod.Get, ConfigUrl);
        var body = await response.Content.ReadAsStringAsync();
        var account = JsonNode.Parse(body)?["default"]?["accounts"]?[0];

        Assert.Multiple(() =>
        {
            Assert.That((bool?)account?["hasApiKey"], Is.True);
            Assert.That((bool?)account?["apiKeyFromFile"], Is.True);
            Assert.That(body, Does.Not.Contain("key-from-a-file"), "the key itself still never travels");
        });
    }

    /// <summary>
    /// The one that would leak a credential. Delete the first of two accounts and save the survivor
    /// with its key still masked: matching the stored key by position would read slot 0 - the deleted
    /// account's key - and write it under the surviving account's server URL, so ImmichFrame would
    /// start sending server X's API key to server Y. It validates, it swaps in, and the version token
    /// refreshes, so nothing about the save looks wrong.
    /// </summary>
    [Test]
    public async Task SaveConfig_DeletingTheFirstAccount_LeavesTheSurvivorWithItsOwnKey()
    {
        WriteSettings("Settings.json", TwoAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var survivor = config.Default.Accounts[1];

        config = config with { Default = config.Default with { Accounts = [survivor] } };

        var response = await Put(client, config);
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath("Settings.json")));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(saved?["Accounts"]?.AsArray(), Has.Exactly(1).Items);
            Assert.That((string?)saved?["Accounts"]?[0]?["ImmichServerUrl"],
                Is.EqualTo("http://server-y.example.com"));
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-y"),
                "the surviving account must keep its own key, not the deleted account's");
        });
    }

    /// <summary>
    /// Reordering is the other half of the same problem, and the handles have to carry the keys with
    /// the accounts rather than with the positions.
    /// </summary>
    [Test]
    public async Task SaveConfig_ReorderingAccounts_MovesEachKeyWithItsAccount()
    {
        WriteSettings("Settings.json", TwoAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config = config with
        {
            Default = config.Default with { Accounts = [config.Default.Accounts[1], config.Default.Accounts[0]] }
        };

        var response = await Put(client, config);
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath("Settings.json")));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((string?)saved?["Accounts"]?[0]?["ImmichServerUrl"], Is.EqualTo("http://server-y.example.com"));
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-y"));
            Assert.That((string?)saved?["Accounts"]?[1]?["ImmichServerUrl"], Is.EqualTo("http://server-x.example.com"));
            Assert.That((string?)saved?["Accounts"]?[1]?["ApiKey"], Is.EqualTo("key-x"));
        });
    }

    /// <summary>
    /// When the handle cannot name exactly one stored account the save is refused outright. There is
    /// no positional fallback to quietly land on, so both an absent handle and one two accounts claim
    /// have to end the same way: ask for the key again.
    /// </summary>
    [TestCase(false, TestName = "SaveConfig_MaskedKeyWithNoAccountHandle_IsRefused")]
    [TestCase(true, TestName = "SaveConfig_TwoAccountsClaimingOneStoredAccount_IsRefused")]
    public async Task SaveConfig_UnresolvableMaskedApiKey_IsRefused(bool ambiguous)
    {
        WriteSettings("Settings.json", TwoAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = File.ReadAllText(SettingsPath("Settings.json"));
        var config = await GetConfig(client);

        if (ambiguous)
        {
            // Both entries name the same stored account - which of them keeps its key is unanswerable.
            config.Default.Accounts[1].Id = config.Default.Accounts[0].Id;
        }
        else
        {
            config.Default.Accounts[0].Id = null;
        }

        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("server-"), "the message has to name the account to retype");
            Assert.That(File.ReadAllText(SettingsPath("Settings.json")), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// A profile taking over the account list has no stored key of its own to keep. Failing closed is
    /// right; the message has to say why, rather than leaving the administrator with the loader's
    /// "Either ApiKey or ApiKeyFile must be provided."
    /// </summary>
    [Test]
    public async Task SaveConfig_ProfileNewlyOverridingAccounts_SaysTheKeyMustBeRetyped()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");

        // The inherited account, now declared by the profile, with its key still masked.
        config = config with
        {
            Profiles = [kitchen with { DeclaredKeys = [.. kitchen.DeclaredKeys, "Accounts"] }]
        };

        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            // The distinguishing sentence, not just "kitchen" and "API key" - the could-not-be-matched
            // refusal next door contains both of those too, so a looser assertion would pass whichever
            // branch ran.
            Assert.That(problem, Does.Contain("declare its own account list instead of inheriting one"));
            Assert.That(problem, Does.Contain("kitchen"));
            Assert.That(problem, Does.Not.Contain("Either ApiKey or ApiKeyFile"));
        });
    }

    /// <summary>
    /// Accounts have to stay as sparse as everything else. Writing all fifteen settings out would
    /// freeze today's built-in defaults into the file, so a later change to one of them would stop
    /// reaching any installation that had used the editor - the same drift the declared-key machinery
    /// exists to prevent, one level down.
    /// </summary>
    [Test]
    public async Task SaveConfig_WritesOnlyTheAccountSettingsThatDifferFromTheDefaults()
    {
        WriteSettings("Settings.json", TwoAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var response = await Put(client, config);
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath("Settings.json")));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // ShowVideos survives because the file spelled it out; the other eleven defaults do not
            // appear at all.
            Assert.That(saved?["Accounts"]?[0]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "ImmichServerUrl", "ApiKey", "ShowVideos" }));
            Assert.That(saved?["Accounts"]?[1]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "ImmichServerUrl", "ApiKey" }));
        });
    }

    /// <summary>
    /// The settings file holds every Immich API key and the frame's shared secret. An operator who
    /// tightened its mode must not lose that to a save: the temporary file is renamed over the
    /// original, so it is the temporary file's permissions the live file ends up with.
    /// </summary>
    [Test]
    [Platform("Unix")]
    public async Task SaveConfig_KeepsTheSettingsFilesPermissions()
    {
        WriteSettings("Settings.json", SettingsJson);
        var path = SettingsPath("Settings.json");

        // 0640, deliberately: a newly created file is 0666 minus the umask, so 0600 and 0644 are both
        // modes this test could be handed for free. Group-read-without-other is one no umask produces,
        // so the assertion below can only pass if the mode was actually carried across.
        const UnixFileMode hardened = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        SetMode(path, hardened);

        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.General.Interval = 99;
        var response = await Put(client, config);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(ModeOf(path), Is.EqualTo(hardened));
        });
    }

    /// <summary>
    /// The two-writers case the version token exists for, in its uglier form: somebody hand-edits the
    /// file and leaves it unparseable while an administrator has the editor open. The loader falls
    /// through to the environment when a file will not parse, so without help the editor would
    /// announce that this installation is configured from environment variables - and answer 500.
    /// </summary>
    [Test]
    public async Task Config_SettingsFileNoLongerParses_Is409OnBothActionsAndNotReportedAsEnvironment()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        // Hand-edited into something neither schema can read, after the host booted.
        File.WriteAllText(SettingsPath("Settings.json"), "{ \"General\": { \"Interval\": 45, } }");

        var read = await Send(client, HttpMethod.Get, ConfigUrl);
        var readProblem = await read.Content.ReadAsStringAsync();
        var save = await Put(client, config);

        Assert.Multiple(() =>
        {
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(readProblem, Does.Contain("Settings.json"));
            Assert.That(readProblem, Does.Not.Contain("environment variables"),
                "an unreadable file is not an installation configured from the environment");
        });
    }

    /// <summary>
    /// YamlStream resolves an alias to the very node the anchor named, so an aliased mapping is
    /// indistinguishable from one written out longhand: every inherited value would be reported as a
    /// key the profile declares, and the save would write them all literally. Refusing is the
    /// deliberate choice - losing comments was an accepted trade, silently flattening the structure of
    /// the configuration is not.
    /// </summary>
    [Test]
    public async Task Config_YamlAnchors_AreRefusedRatherThanSilentlyFlattened()
    {
        WriteSettings("Settings.yml", AnchoredYaml);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await Send(client, HttpMethod.Get, ConfigUrl);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(problem, Does.Contain("anchor"));
            Assert.That(File.ReadAllText(SettingsPath("Settings.yml")), Is.EqualTo(AnchoredYaml));
        });
    }

    /// <summary>
    /// The default configuration is the only one with nothing to inherit from, so it is the only one
    /// an empty account list breaks outright. Both shapes have to say so in words: an absent Accounts
    /// key otherwise reaches the loader as a null dereference reported as "Object reference not set to
    /// an instance of an object."
    /// </summary>
    [TestCase(false, TestName = "SaveConfig_DefaultConfigurationWithNoAccountsKey_IsRefusedReadably")]
    [TestCase(true, TestName = "SaveConfig_DefaultConfigurationWithAnEmptyAccountList_IsRefusedReadably")]
    public async Task SaveConfig_DefaultConfigurationWithoutAnAccount_IsRefusedReadably(bool declaredButEmpty)
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = File.ReadAllText(SettingsPath("Settings.json"));
        var config = await GetConfig(client);

        config = config with
        {
            Default = declaredButEmpty
                ? config.Default with { Accounts = [] }
                : config.Default with { DeclaredKeys = [.. config.Default.DeclaredKeys.Where(key => key != "Accounts")] }
        };

        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("at least one Immich account"));
            Assert.That(problem, Does.Not.Contain("Object reference"));
            Assert.That(File.ReadAllText(SettingsPath("Settings.json")), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// The settings file is meant to stay hand-editable, and the default JSON encoder escapes
    /// everything outside ASCII - so one save through the editor would turn non-English tags into runs
    /// of \uXXXX that only the parser can read.
    /// </summary>
    [Test]
    public async Task SaveConfig_NonAsciiValues_StayReadableInTheFile()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.Accounts[0].Tags = ["Kücheneinweihung", "日本"];

        var response = await Put(client, config);
        var saved = File.ReadAllText(SettingsPath("Settings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(saved, Does.Contain("Kücheneinweihung"));
            Assert.That(saved, Does.Contain("日本"));
            Assert.That(saved, Does.Not.Contain("\\u"));
        });
    }

    /// <summary>
    /// The environment-variable fallback has no file behind it, so there is nothing to write and
    /// nothing a restart would read back. Saying so is the whole job - inventing a settings file the
    /// next restart would then load <em>instead</em> of the environment would be worse than refusing.
    /// </summary>
    [Test]
    public async Task SaveConfig_ConfigurationCameFromEnvironmentVariables_Is409()
    {
        // No settings file in the directory at all, so ConfigLoader falls through to the environment.
        var url = Environment.GetEnvironmentVariable("ImmichServerUrl");
        var key = Environment.GetEnvironmentVariable("ApiKey");
        Environment.SetEnvironmentVariable("ImmichServerUrl", "http://mock-immich-server.com");
        Environment.SetEnvironmentVariable("ApiKey", "env-api-key");

        try
        {
            using var factory = CreateFactory();
            var client = factory.CreateClient();

            var config = await GetConfig(client);
            var response = await Put(client, config);
            var problem = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(config.Source.Format, Is.EqualTo("environment"));
                Assert.That(config.Source.Editable, Is.False);
                Assert.That(config.Source.NotEditableReason, Is.Not.Null);

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(problem, Does.Contain("environment variables"));
                Assert.That(Directory.EnumerateFileSystemEntries(_directory), Is.Empty,
                    "refusing means writing nothing, not creating the file the editor wishes were there");
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ImmichServerUrl", url);
            Environment.SetEnvironmentVariable("ApiKey", key);
        }
    }

    [Test]
    public async Task AdminConfig_IsBehindTheAllowlist_NotJustAuthentication()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync(ConfigUrl);
        var signedInButNotAnAdmin = await Send(client, HttpMethod.Get, ConfigUrl, OtherSubject);

        // The frame's own bearer token is a valid credential for the default scheme, which is what
        // makes a bare [Authorize] here a hole rather than a guard.
        using var withFrameSecret = new HttpRequestMessage(HttpMethod.Get, ConfigUrl);
        withFrameSecret.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FrameSecret);
        var frameToken = await client.SendAsync(withFrameSecret);

        var put = new HttpRequestMessage(HttpMethod.Put, ConfigUrl) { Content = JsonContent.Create(new { }) };
        var anonymousPut = await client.SendAsync(put);

        Assert.Multiple(() =>
        {
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(anonymousPut.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(signedInButNotAnAdmin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(frameToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    private async Task<AdminConfigDto> GetConfig(HttpClient client)
    {
        var response = await Send(client, HttpMethod.Get, ConfigUrl);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AdminConfigDto>(Camel))!;
    }

    /// <summary>What the editor sends back: the configuration it was given, edited in place.</summary>
    private static AdminConfigUpdateDto Update(AdminConfigDto config) =>
        new(config.Version, config.Default, config.Profiles);

    private static Task<HttpResponseMessage> Put(HttpClient client, AdminConfigDto config) =>
        Put(client, Update(config));

    private static Task<HttpResponseMessage> Put(HttpClient client, AdminConfigUpdateDto update)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, ConfigUrl)
        {
            Content = JsonContent.Create(update, options: Camel)
        };
        request.Headers.Add(TestAdminAuthHandler.SubjectHeader, AdminSubject);

        return client.SendAsync(request);
    }

    private static async Task<int?> FrameInterval(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        return (int?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["interval"];
    }

    private static Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string url, string subject = AdminSubject)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAdminAuthHandler.SubjectHeader, subject);

        return client.SendAsync(request);
    }

    // Guarded rather than called directly: the file-mode APIs are unsupported on Windows, and an
    // unguarded call site is a CA1416 warning in an otherwise clean build. The two tests that turn on
    // what the mode actually is carry [Platform("Unix")] as well.
    private static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(path, mode);
    }

    private static UnixFileMode? ModeOf(string path) =>
        OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

    private string SettingsPath(string name) => Path.Combine(_directory, name);

    private void WriteSettings(string name, string contents) => File.WriteAllText(SettingsPath(name), contents);

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

                    // One override moves everything: the catalog is seeded from ConfigLocation too,
                    // so the running configuration and the file the editor writes are the same file.
                    // Overriding only one of the two would let a save convert one file using another
                    // file's API keys.
                    services.AddSingleton(new ConfigLocation(directory));

                    services.AddSingleton(new AdminOidcOptions
                    {
                        Authority = "https://idp.example.com",
                        ClientId = "immichframe",
                        ClientSecret = "client-secret",
                        Admins = [AdminSubject]
                    });

                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>(
                            TestAdminAuthHandler.SchemeName, _ => { });
                    services.Configure<CookieAuthenticationOptions>(
                        AdminAuthentication.CookieScheme,
                        options => options.ForwardAuthenticate = TestAdminAuthHandler.SchemeName);
                });
            });
    }
}
