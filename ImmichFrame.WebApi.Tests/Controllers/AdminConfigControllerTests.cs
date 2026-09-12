using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Services;
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

    /// <summary>
    /// An account on the default configuration, a profile that declares none of its own, and a
    /// profile that does - so a handle can be resolved in either direction.
    /// <para>
    /// Both stored accounts spell out a setting whose value is also its built-in default
    /// (<c>ShowFavorites</c>, <c>ShowArchived</c>). Those are the only settings that make "this entry
    /// declared it" visible in the written file, and so the only ones that can show a profile
    /// adopting an account being de-sparsified by the entry it adopted from.
    /// </para>
    /// </summary>
    private const string SharedAccountsJson = """
        {
          "General": {
            "Interval": 45
          },
          "Accounts": [
            {
              "ImmichServerUrl": "http://server-x.example.com",
              "ApiKey": "key-x",
              "ShowFavorites": false
            }
          ],
          "Profiles": {
            "kitchen": {
              "General": {
                "Interval": 10
              }
            },
            "studio": {
              "Accounts": [
                {
                  "ImmichServerUrl": "http://server-z.example.com",
                  "ApiKey": "key-z",
                  "ShowArchived": false
                }
              ]
            }
          }
        }
        """;

    /// <summary>
    /// Two Immich logins on one server, which is the setup a label is for: the URLs are identical,
    /// the API keys never reach the browser, and so nothing else in the file tells these two
    /// accounts apart. The second is deliberately unlabelled - an account without a label has to
    /// keep not having one.
    /// </summary>
    private const string LabelledAccountsJson = """
        {
          "General": {
            "Interval": 45
          },
          "Accounts": [
            {
              "Label": "Mum's photos",
              "ImmichServerUrl": "http://server-x.example.com",
              "ApiKey": "key-mum"
            },
            {
              "ImmichServerUrl": "http://server-x.example.com",
              "ApiKey": "key-dad"
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
        var saved = JsonNode.Parse(StoredText(factory));

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
        var saved = JsonNode.Parse(StoredText(factory));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((string?)saved?["General"]?["WeatherApiKey"], Is.EqualTo("typed-by-the-administrator"));
            Assert.That((string?)saved?["General"]?["AuthenticationSecret"], Is.EqualTo(FrameSecret));
            Assert.That((string?)saved?["General"]?["Webhook"], Is.EqualTo("https://hook.example.com/base"));
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("base-api-key"));
        });
    }

    /// <summary>
    /// Clearing a secret has to remove it, not write it as <c>""</c>.
    /// <para>
    /// The two read back differently everywhere they are used: null is "not configured", while an
    /// empty string is a configured secret. For <c>AuthenticationSecret</c> that difference is an
    /// outright lockout - the frame's handler goes on demanding a bearer token, and the only token
    /// equal to <c>""</c> is the one no client sends - so the editor must not be able to produce it.
    /// </para>
    /// </summary>
    [Test]
    public async Task SaveConfig_ClearedSecrets_AreRemovedRatherThanWrittenEmpty()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        // The empty string is how the API documents "clear this", as opposed to the placeholder's
        // "keep what is stored".
        config.Default.General.AuthenticationSecret = string.Empty;
        config.Default.General.WeatherApiKey = "   ";
        config.Default.General.Webhook = AdminSecret.Placeholder;

        var response = await Put(client, config);
        var saved = JsonNode.Parse(StoredText(factory));
        var general = saved?["General"] as JsonObject;

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(general!.ContainsKey(nameof(GeneralSettings.AuthenticationSecret)), Is.False,
                "a cleared secret must leave no key behind, not an empty one");
            Assert.That(general.ContainsKey(nameof(GeneralSettings.WeatherApiKey)), Is.False,
                "whitespace is not a secret either");
            Assert.That((string?)general[nameof(GeneralSettings.Webhook)],
                Is.EqualTo("https://hook.example.com/base"),
                "the secret that was left alone still has to survive");
        });
    }

    /// <summary>
    /// A profile's third state: <c>AuthenticationSecret: null</c> overrides an inherited secret with
    /// none, which <c>docs/getting-started/configuration.md</c> documents as deliberate - it is how a
    /// frame on a trusted network skips the prompt. Dropping the key instead would hand the profile
    /// the default's secret back, so the editor would be unable to express something hand-editing can.
    /// </summary>
    [Test]
    public async Task SaveConfig_ProfileWithNoSecret_WritesNullRatherThanDroppingTheKey()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        var response = await Put(client, WithNoSecretOn(config, "kitchen"));
        var general = KitchenGeneral(factory);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(general.ContainsKey(nameof(GeneralSettings.AuthenticationSecret)), Is.True,
                "dropping the key would give the profile the default's secret back");
            Assert.That(general[nameof(GeneralSettings.AuthenticationSecret)], Is.Null,
                "and it has to be null, never the empty string");
        });
    }

    /// <summary>
    /// The capability that null is for: the profile is reachable without a token while the default
    /// configuration still demands one.
    /// </summary>
    [Test]
    public async Task SaveConfig_ProfileWithNoSecret_LeavesThatProfileUnauthenticated()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        var response = await Put(client, WithNoSecretOn(config, "kitchen"));
        var profile = await client.GetAsync("/api/Calendar?profile=kitchen");
        var fallback = await client.GetAsync("/api/Calendar");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(profile.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fallback.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "the default configuration keeps its secret");
        });
    }

    /// <summary>
    /// Round-tripping that profile unchanged has to leave the null where it is. The editor sends the
    /// masking placeholder back for a secret it never received, and resolving that against a stored
    /// null must not be mistaken for "there is nothing here, so drop the key".
    /// </summary>
    [Test]
    public async Task SaveConfig_ProfileWithAStoredNullSecret_KeepsItThroughAnUneditedRoundTrip()
    {
        WriteSettings("Settings.json",
            SettingsJson.Replace("\"Interval\": 10,", "\"Interval\": 10,\n            \"AuthenticationSecret\": null,"));

        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        Assume.That(config.Profiles.Single(profile => profile.Name == "kitchen").DeclaredKeys,
            Does.Contain("General.AuthenticationSecret"), "the fixture has to start out declaring it");

        var response = await Put(client, config);
        var general = KitchenGeneral(factory);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(general.ContainsKey(nameof(GeneralSettings.AuthenticationSecret)), Is.True);
            Assert.That(general[nameof(GeneralSettings.AuthenticationSecret)], Is.Null);
        });
    }

    /// <summary>
    /// And on the default configuration, where null and absent say the same thing, no secret still
    /// means no key. Neither level may write the empty string: it is the one value that reads back as
    /// "authentication is on" while matching only the bearer token nobody sends.
    /// </summary>
    [Test]
    public async Task SaveConfig_NoSecretOnTheDefault_RemovesTheKeyInstead()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.General.AuthenticationSecret = string.Empty;
        config.Default.General.WeatherApiKey = "   ";

        var response = await Put(client, WithNoSecretOn(config, "kitchen"));
        var root = JsonNode.Parse(StoredText(factory));
        var general = (root?["General"] as JsonObject)!;

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(general.ContainsKey(nameof(GeneralSettings.AuthenticationSecret)), Is.False);
            Assert.That(general.ContainsKey(nameof(GeneralSettings.WeatherApiKey)), Is.False,
                "whitespace is not a secret either");
            Assert.That(general[nameof(GeneralSettings.Webhook)]?.GetValue<string>(),
                Is.EqualTo("https://hook.example.com/base"),
                "the secret that was left alone still has to survive");
            // ContainsKey, not a null check on the value: a JsonObject indexer answers null for a
            // key that is not there, so the weaker form could not tell the profile's explicit null
            // apart from the key having been dropped like the default's.
            Assert.That(KitchenGeneral(factory).ContainsKey(nameof(GeneralSettings.AuthenticationSecret)), Is.True,
                "the profile in the same save keeps its explicit null");
        });
    }

    /// <summary>
    /// What the editor does when the administrator picks "no secret" on a profile: the key joins that
    /// profile's declared keys and carries the empty string, which is how <see cref="AdminSecret"/>
    /// documents "not the stored one".
    /// </summary>
    private static AdminConfigUpdateDto WithNoSecretOn(AdminConfigDto config, string profileName)
    {
        var profiles = config.Profiles
            .Select(entry =>
            {
                if (entry.Name != profileName) return entry;

                entry.General.AuthenticationSecret = string.Empty;

                return entry with { DeclaredKeys = [.. entry.DeclaredKeys, "General.AuthenticationSecret"] };
            })
            .ToList();

        return new AdminConfigUpdateDto(config.Version, config.Default, profiles);
    }

    private static JsonObject KitchenGeneral(WebApplicationFactory<Program> factory) =>
        (JsonNode.Parse(StoredText(factory))
            ?["Profiles"]?["kitchen"]?["General"] as JsonObject)!;

    [Test]
    public async Task SaveConfig_InvalidConfiguration_IsRejectedWithTheStoredDocumentUntouched()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var versionBefore = StoredVersion(factory);

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
            Assert.That(StoredText(factory), Is.EqualTo(before),
                "a rejected configuration must never reach the store");

            // The version is the editor's concurrency token, so a refused save must not consume one:
            // bumping it here would make every other open editor's token stale over a write that
            // never happened. This replaced a check that the atomic file write left no .tmp or .bak
            // behind - there is no file write left to leave debris.
            Assert.That(StoredVersion(factory), Is.EqualTo(versionBefore),
                "and must not consume a version");
        });
    }

    [Test]
    public async Task SaveConfig_StaleVersionToken_Is409WithTheStoredDocumentUntouched()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // Loaded into one editor, and then not saved yet.
        var stale = await GetConfig(client);

        // Somebody else got there first. A second save through the API rather than an edit to the
        // settings file: the file stopped being the source of truth when the database took over, so
        // a hand edit to it is no longer a way to make an open editor stale - only another save is.
        var winner = await GetConfig(client);
        winner.Default.General.Interval = 46;
        var first = await Put(client, winner);

        var before = StoredText(factory);
        var response = await Put(client, stale);

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the first save wins");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(StoredText(factory), Is.EqualTo(before));
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
        var saved = JsonNode.Parse(StoredText(factory));
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

        var before = StoredText(factory);

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
            Assert.That(StoredText(factory), Is.EqualTo(before));
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
        var saved = StoredText(factory);
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
    public async Task SaveConfig_LegacySchemaFile_IsConvertedOnImportAndNeedsNoConsent()
    {
        // This replaced a test that required AdminConfigUpdateDto.ConvertLegacySchema before a v1
        // file could be saved. The consent existed because saving rewrote the file in place and that
        // was not reversible. Nothing rewrites the file now: it is read once, converted on the way
        // into the database, and left alone, so there is nothing to consent to.
        WriteSettings("Settings.json", SettingsV1Json);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var onImport = JsonNode.Parse(StoredText(factory));

        var config = await GetConfig(client);
        var saved = await Put(client, Update(config));

        Assert.Multiple(() =>
        {
            // Converted before the editor ever saw it.
            Assert.That(onImport?["General"], Is.Not.Null, "the stored document is current-schema");
            Assert.That((string?)onImport?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("base-api-key"));
            Assert.That(config.Source.LegacySchema, Is.False,
                "the editor is never handed a v1 document any more");

            Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK), "and saves without consent");

            // The file it came from is untouched, which is what makes the conversion safe to do
            // without asking.
            Assert.That(File.ReadAllText(SettingsPath("Settings.json")), Is.EqualTo(SettingsV1Json));
        });
    }

    [Test]
    public void ReadOnlyConfigurationDirectory_FailsToStartAndNamesTheDirectory()
    {
        // This replaced a test that took the write bit off mid-run and expected the save to 409. It
        // cannot work that way any more, and not because the contract got weaker: SQLite already has
        // the database file open by then, so chmod on the directory does not stop the write and the
        // save genuinely succeeds.
        //
        // The contract moved to startup instead, which is where the problem now surfaces: the
        // database cannot be created or opened at all, so ImmichFrame refuses to boot rather than
        // coming up and failing at the first save. Note the behaviour change for anyone mounting the
        // configuration directory read-only - that used to run fine for a frame that never edits its
        // settings, and now does not start.
        WriteSettings("Settings.json", SettingsJson);
        SetMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using var factory = CreateFactory();

        // Thrown out of Program.cs's InitializeAsync, so it surfaces when the host is first built.
        var failure = Assert.Catch(() => factory.CreateClient());

        Assert.That(Unwrap(failure), Does.Contain(_directory),
            "the operator has to be told which directory to fix");
    }

    /// <summary>The innermost message, since the host wraps a startup failure more than once.</summary>
    private static string Unwrap(Exception? exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
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
        var saved = JsonNode.Parse(StoredText(factory));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), problem);
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKeyFile"], Is.EqualTo(keyPath));
            Assert.That(saved?["Accounts"]?[0]?.AsObject().Select(pair => pair.Key),
                Does.Not.Contain("ApiKey"),
                "an ApiKey beside an ApiKeyFile is exactly what the loader refuses");
            Assert.That(StoredText(factory),
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
        var saved = JsonNode.Parse(StoredText(factory));

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
        var saved = JsonNode.Parse(StoredText(factory));

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
    /// no positional fallback to quietly land on, so both an absent handle and one two accounts in the
    /// same list claim have to end the same way: ask for the key again. Two accounts in <em>different</em>
    /// lists claiming one stored account is a different matter, and is allowed.
    /// </summary>
    [TestCase(false, TestName = "SaveConfig_MaskedKeyWithNoAccountHandle_IsRefused")]
    [TestCase(true, TestName = "SaveConfig_TwoAccountsClaimingOneStoredAccount_IsRefused")]
    public async Task SaveConfig_UnresolvableMaskedApiKey_IsRefused(bool ambiguous)
    {
        WriteSettings("Settings.json", TwoAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
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
            Assert.That(problem,
                ambiguous ? Does.Contain("name the same stored account") : Does.Contain("could not be matched"),
                "and has to say which of the two it is - they are not fixed the same way");
            Assert.That(StoredText(factory), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// A profile taking over the account list names no stored account: the read issues no handle for
    /// an account a profile inherits, so there is nothing saying which stored key this one is.
    /// Failing closed is right; the message has to say why, rather than leaving the administrator
    /// with the loader's "Either ApiKey or ApiKeyFile must be provided."
    /// </summary>
    [Test]
    public async Task SaveConfig_ProfileNewlyOverridingAccountsWithNoHandle_SaysTheKeyMustBeRetyped()
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");

        Assume.That(kitchen.Accounts[0].Id, Is.Null,
            "an account a profile inherits is issued no handle, which is what makes this unresolvable");

        // The inherited account, now declared by the profile, with its key still masked.
        config = config with
        {
            Profiles = [kitchen with { DeclaredKeys = [.. kitchen.DeclaredKeys, "Accounts"] }]
        };

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("could not be matched"));
            Assert.That(problem, Does.Contain("kitchen"));
            Assert.That(problem, Does.Not.Contain("Either ApiKey or ApiKeyFile"));
        });
    }

    /// <summary>
    /// The point of the whole handle mechanism, and what a handle now buys that it did not: an
    /// account the default configuration declares can be assigned to a profile, and the profile keeps
    /// that account's API key without an administrator retyping a credential the browser was never
    /// shown.
    /// <para>
    /// The adopted account also has to be written as sparsely as any other new one. The entry it came
    /// from spelled <c>ShowFavorites</c> out; the profile did not, so writing it here would hand the
    /// profile an override of a built-in default it never asked for - and cut it off from a later
    /// change to that default, which is the drift the declared-key machinery exists to prevent.
    /// </para>
    /// </summary>
    [Test]
    public async Task SaveConfig_ProfileAdoptingTheDefaultsAccount_KeepsItsStoredApiKeyAndStaysSparse()
    {
        // Arrange
        WriteSettings("Settings.json", SharedAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");
        var studio = config.Profiles.Single(profile => profile.Name == "studio");

        // What the editor sends once the default configuration's account is assigned to this profile:
        // the handle that account was read under, and the key still masked.
        kitchen.Accounts[0].Id = config.Default.Accounts[0].Id;
        kitchen.Accounts[0].ApiKey = AdminSecret.Placeholder;

        config = config with
        {
            Profiles = [kitchen with { DeclaredKeys = [.. kitchen.DeclaredKeys, "Accounts"] }, studio]
        };

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var saved = JsonNode.Parse(StoredText(factory));
        var adopted = saved?["Profiles"]?["kitchen"]?["Accounts"]?[0];

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);

            Assert.That((string?)adopted?["ImmichServerUrl"], Is.EqualTo("http://server-x.example.com"));
            Assert.That((string?)adopted?["ApiKey"], Is.EqualTo("key-x"),
                "the profile keeps the key of the account it was given");

            Assert.That(adopted?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "ImmichServerUrl", "ApiKey" }),
                "an adopted account is new to this profile, so only what differs from the built-in defaults is written");

            // The entries it was resolved across are untouched, each still declaring its own.
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-x"));
            Assert.That(saved?["Accounts"]?[0]?.AsObject().Select(pair => pair.Key),
                Does.Contain("ShowFavorites"),
                "the entry that did spell a default-valued setting out still writes it");
            Assert.That((string?)saved?["Profiles"]?["studio"]?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-z"));
            Assert.That(saved?["Profiles"]?["studio"]?["Accounts"]?[0]?.AsObject().Select(pair => pair.Key),
                Does.Contain("ShowArchived"));

            Assert.That(body, Does.Not.Contain("key-x"), "and the key itself never travels to the browser");
        });
    }

    /// <summary>
    /// The same in the other direction, between two profiles: neither the default configuration nor
    /// the entry being saved is special, the handle simply names the account it was read from.
    /// </summary>
    [Test]
    public async Task SaveConfig_ProfileAdoptingAnotherProfilesAccount_KeepsItsStoredApiKey()
    {
        // Arrange
        WriteSettings("Settings.json", SharedAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");
        var studio = config.Profiles.Single(profile => profile.Name == "studio");

        kitchen.Accounts[0].Id = studio.Accounts[0].Id;
        kitchen.Accounts[0].ImmichServerUrl = studio.Accounts[0].ImmichServerUrl;
        kitchen.Accounts[0].ApiKey = AdminSecret.Placeholder;

        config = config with
        {
            Profiles = [kitchen with { DeclaredKeys = [.. kitchen.DeclaredKeys, "Accounts"] }, studio]
        };

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That((string?)saved?["Profiles"]?["kitchen"]?["Accounts"]?[0]?["ImmichServerUrl"],
                Is.EqualTo("http://server-z.example.com"));
            Assert.That((string?)saved?["Profiles"]?["kitchen"]?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-z"));

            // And the profile it came from still has it too - adopting is copying, not moving.
            Assert.That((string?)saved?["Profiles"]?["studio"]?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-z"));
            Assert.That(body, Does.Not.Contain("key-z"));
        });
    }

    /// <summary>
    /// Two entries naming one stored account in the same save is legitimate - each writes its own copy
    /// of that key into its own list - and is the case that separates "this list already claimed it",
    /// which is an ambiguity, from "another list claimed it", which is not.
    /// </summary>
    [Test]
    public async Task SaveConfig_TwoProfilesAdoptingOneStoredAccount_BothKeepItsApiKey()
    {
        // Arrange
        WriteSettings("Settings.json", SharedAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");
        var shared = config.Default.Accounts[0].Id;

        kitchen.Accounts[0].Id = shared;
        kitchen.Accounts[0].ApiKey = AdminSecret.Placeholder;

        // A profile created in this very save, taking the same account.
        var hallway = new AdminConfigEntryDto("hallway", ["Accounts"], new AdminGeneralSettingsDto(),
        [
            new AdminAccountSettingsDto
            {
                Id = shared,
                ImmichServerUrl = "http://server-x.example.com",
                ApiKey = AdminSecret.Placeholder
            }
        ]);

        config = config with
        {
            Profiles =
            [
                kitchen with { DeclaredKeys = [.. kitchen.DeclaredKeys, "Accounts"] },
                config.Profiles.Single(profile => profile.Name == "studio"),
                hallway
            ]
        };

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That((string?)saved?["Profiles"]?["kitchen"]?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-x"));
            Assert.That((string?)saved?["Profiles"]?["hallway"]?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-x"));
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-x"));
        });
    }

    /// <summary>
    /// The counterpart to the widened lookup: it is wider, not looser. A handle no entry in the
    /// document issued still matches nothing, and there is no stored key to fall back on.
    /// </summary>
    [Test]
    public async Task SaveConfig_AccountHandleMatchingNothingInTheDocument_IsRefused()
    {
        // Arrange
        WriteSettings("Settings.json", SharedAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);

        // Well-formed and of the right shape, but issued by no read of this document.
        config.Default.Accounts[0].Id = new string('a', 16);

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("could not be matched"));
            Assert.That(problem, Does.Contain("server-x"), "the message has to name the account to retype");
            Assert.That(StoredText(factory), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// Saving a configuration nobody edited has to be a fixpoint, both in what is written and in what
    /// each entry declares. It is also the only case that exercises a handle across a rolled version
    /// token: the first save rewrites the file, so every handle the second read issues is computed
    /// from a version that did not exist when the first one was.
    /// </summary>
    [Test]
    public async Task SaveConfig_UneditedConfigurationSavedTwice_IsWrittenIdenticallyAndStaysSparse()
    {
        // Arrange
        WriteSettings("Settings.json", SharedAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // Act
        var first = await Put(client, await GetConfig(client));
        var afterFirst = StoredText(factory);

        var second = await Put(client, await GetConfig(client));
        var afterSecond = StoredText(factory);

        var saved = JsonNode.Parse(afterSecond);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(afterSecond, Is.EqualTo(afterFirst), "an unedited save must change nothing at all");

            Assert.That(saved?["Profiles"]?["kitchen"]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "General" }),
                "a profile that inherited its accounts must not start declaring them");
            Assert.That(saved?["Profiles"]?["studio"]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "Accounts" }),
                "and one that declared nothing else must not acquire a General section");

            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-x"));
            Assert.That((string?)saved?["Profiles"]?["studio"]?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-z"));
        });
    }

    /// <summary>
    /// Accounts have to stay as sparse as everything else. Writing all sixteen settings out would
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
        var saved = JsonNode.Parse(StoredText(factory));

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
    /// A label is the only thing in the file that survives a reload and says which account is which,
    /// so the read has to report it - and has to report its absence as an absence rather than
    /// inventing one.
    /// </summary>
    [Test]
    public async Task GetConfig_AccountWithALabel_ReportsItAndLeavesAnUnlabelledAccountWithout()
    {
        // Arrange
        WriteSettings("Settings.json", LabelledAccountsJson);
        using var factory = CreateFactory();

        // Act
        var config = await GetConfig(factory.CreateClient());

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(config.Default.Accounts[0].Label, Is.EqualTo("Mum's photos"));
            Assert.That(config.Default.Accounts[1].Label, Is.Null,
                "the two accounts are otherwise identical, so a label must not be guessed for one of them");
        });
    }

    /// <summary>
    /// The write half: a label is written like every other account setting - sparsely - so an
    /// account that never had one does not acquire an empty <c>Label</c> key from a round trip.
    /// </summary>
    [Test]
    public async Task SaveConfig_UneditedLabelledAccounts_KeepTheirLabelsAndGainNoNewOnes()
    {
        // Arrange
        WriteSettings("Settings.json", LabelledAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        // Act
        var response = await Put(client, config);
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((string?)saved?["Accounts"]?[0]?["Label"], Is.EqualTo("Mum's photos"));
            Assert.That(saved?["Accounts"]?[1]?.AsObject().ContainsKey("Label"), Is.False,
                "an account with no label must leave no key behind");
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-mum"),
                "and the labels must not have disturbed which key belongs to which account");
            Assert.That((string?)saved?["Accounts"]?[1]?["ApiKey"], Is.EqualTo("key-dad"));
        });
    }

    /// <summary>
    /// Absent, empty and whitespace-only are one state - this account has no label - in both
    /// directions. The editor sends its form fields verbatim, so a label the administrator cleared
    /// arrives as <c>""</c> or as the spaces left behind, and neither may be written: the next read
    /// would report no label while the file claimed one, and two accounts "labelled" with different
    /// runs of spaces would look distinct to any later uniqueness rule.
    /// </summary>
    [Test]
    public async Task SaveConfig_LabelThatIsEmptyOrOnlyWhitespace_WritesNoLabelAtAll()
    {
        // Arrange
        WriteSettings("Settings.json", LabelledAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.Accounts[0].Label = "   ";
        config.Default.Accounts[1].Label = string.Empty;

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadFromJsonAsync<AdminConfigDto>(Camel);
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(saved?["Accounts"]?[0]?.AsObject().ContainsKey("Label"), Is.False,
                "clearing a label removes the key rather than writing whitespace into it");
            Assert.That(saved?["Accounts"]?[1]?.AsObject().ContainsKey("Label"), Is.False);
            Assert.That(body?.Default.Accounts.Select(account => account.Label), Is.All.Null);
        });
    }

    /// <summary>
    /// Two accounts in one list sharing a label are the same class of silent credential
    /// misattribution the account handles exist to prevent: the editor would show one row for two
    /// sets of credentials, and editing it would write one account's server URL or key over the
    /// other's. Case and surrounding whitespace do not make them two accounts to anyone reading the
    /// screen, so they do not here either.
    /// </summary>
    [TestCase("mum's PHOTOS", TestName = "SaveConfig_TwoAccountsLabelledAlikeButForCase_IsRefused")]
    [TestCase("  Mum's photos  ", TestName = "SaveConfig_TwoAccountsLabelledAlikeButForWhitespace_IsRefused")]
    public async Task SaveConfig_TwoAccountsInOneEntryWithTheSameLabel_IsRefused(string duplicate)
    {
        // Arrange
        WriteSettings("Settings.json", LabelledAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        config.Default.Accounts[1].Label = duplicate;

        // Act
        var response = await Put(client, config);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((string?)problem?["detail"], Does.Contain(duplicate.Trim()),
                "the message has to name the label that is doubled up");
            Assert.That(StoredText(factory), Is.EqualTo(before),
                "and nothing may be written");
        });
    }

    /// <summary>
    /// The opposite rule one entry down: the same label in the default configuration and in a profile
    /// is not a collision, it is how those two entries say they mean the same account. A uniqueness
    /// check across entries would forbid exactly the thing labels are for.
    /// </summary>
    [Test]
    public async Task SaveConfig_SameLabelInTheDefaultConfigurationAndInAProfile_IsAccepted()
    {
        // Arrange
        WriteSettings("Settings.json", SharedAccountsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.Accounts[0].Label = "Family";
        config.Profiles.Single(profile => profile.Name == "studio").Accounts[0].Label = "Family";

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That((string?)saved?["Accounts"]?[0]?["Label"], Is.EqualTo("Family"));
            Assert.That((string?)saved?["Profiles"]?["studio"]?["Accounts"]?[0]?["Label"], Is.EqualTo("Family"));
        });
    }

    /// <summary>
    /// A v1 file has nowhere to keep a label, and is read through an adapter that is not the current
    /// schema's account class at all - so the read has to report no label rather than reaching for
    /// one, and converting the file must not invent one either.
    /// </summary>
    [Test]
    public async Task SaveConfig_LegacySchemaFile_ConvertsToAccountsWithNoLabels()
    {
        // Arrange
        WriteSettings("Settings.json", SettingsV1Json);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        // Act
        var converted = await Put(client, Update(config) with { ConvertLegacySchema = true });
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(config.Default.Accounts[0].Label, Is.Null);
            Assert.That(converted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(saved?["Accounts"]?[0]?.AsObject().ContainsKey("Label"), Is.False);
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
    public async Task Config_SettingsFileDoesNotParseAtImport_StartsUnconfiguredRatherThanRefusing()
    {
        // This replaced a test that hand-edited the settings file after startup and expected both
        // editor actions to 409. The file is read exactly once now, to import it, and ignored
        // afterwards - so it can no longer move under a running host and there is no such conflict
        // to report. What is left worth pinning is the import itself failing: a file that parses as
        // neither schema must not take the host down with it, because the editor that fixes it is
        // served by this very process.
        WriteSettings("Settings.json", "{ \"General\": { \"Interval\": 45, } }");
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var read = await Send(client, HttpMethod.Get, ConfigUrl);
        var config = await GetConfig(client);

        Assert.Multiple(() =>
        {
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the editor still answers");
            Assert.That(config.Source.Editable, Is.True, "and can be used to configure the instance");
            Assert.That(config.Default.Accounts, Is.Empty, "nothing was imported from the broken file");
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
            Assert.That(StoredText(factory), Is.EqualTo(AnchoredYaml));
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

        var before = StoredText(factory);
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
            Assert.That(StoredText(factory), Is.EqualTo(before));
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
        var saved = StoredText(factory);

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
    public async Task SaveConfig_EnvironmentVariablesAreNotImported_AndTheEditorConfiguresFromScratch()
    {
        // This replaced a test that asserted a 409 and an uneditable editor. The environment
        // fallback was removed upstream when the database became the source of truth, so these
        // variables now configure nothing at all: the instance comes up unconfigured, and the point
        // of the admin editor on a fresh install is that it can configure one. Refusing here would
        // leave an installation that used to work with no way back in.
        var url = Environment.GetEnvironmentVariable("ImmichServerUrl");
        var key = Environment.GetEnvironmentVariable("ApiKey");
        Environment.SetEnvironmentVariable("ImmichServerUrl", "http://mock-immich-server.com");
        Environment.SetEnvironmentVariable("ApiKey", "env-api-key");

        try
        {
            using var factory = CreateFactory();
            var client = factory.CreateClient();

            var config = await GetConfig(client);

            // Nothing was imported, so there is nothing to inherit: the editor opens on an empty
            // default configuration rather than on the environment's values.
            var accountsBefore = config.Default.Accounts.Count;

            var configured = config with
            {
                Default = config.Default with
                {
                    DeclaredKeys = [.. config.Default.DeclaredKeys, "Accounts"],
                    Accounts =
                    [
                        new AdminAccountSettingsDto
                        {
                            ImmichServerUrl = "http://mock-immich-server.com",
                            ApiKey = "typed-in-the-editor"
                        }
                    ]
                }
            };

            var response = await Put(client, configured);
            var saved = JsonNode.Parse(StoredText(factory));

            Assert.Multiple(() =>
            {
                Assert.That(accountsBefore, Is.Zero, "the environment configures nothing");
                Assert.That(config.Source.Editable, Is.True, "a fresh install has to be configurable");
                Assert.That(config.Source.NotEditableReason, Is.Null);

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("typed-in-the-editor"),
                    "and what the editor saves is what is stored, not the environment's key");
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

    /// <summary>
    /// The settings document as the store now holds it. This replaced reading the settings file
    /// back: the database is the source of truth, the file is read once on import and never
    /// rewritten, so what a save produced is here rather than on disk.
    /// </summary>
    private static string StoredText(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<SettingsService>().Read().Text;

    /// <summary>The stored row's version - the token the editor round-trips as its concurrency check.</summary>
    private static long StoredVersion(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<SettingsService>().Read().Version;

    /// <summary>The format the stored document is written in - a YAML import stays YAML.</summary>
    private static ConfigFormat StoredFormat(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<SettingsService>().Read().Format;

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
