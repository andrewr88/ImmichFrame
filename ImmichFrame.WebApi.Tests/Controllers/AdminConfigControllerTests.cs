using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ImmichFrame.WebApi.Helpers;
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
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using YamlDotNet.Serialization;

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

    /// <summary>
    /// <see cref="SettingsJson"/> written as YAML, key for key, for the tests that run against both
    /// formats. Not <see cref="SettingsYaml"/>, which is a smaller fixture of its own: the YAML-only
    /// tests need a frame with no secret.
    /// </summary>
    private const string SettingsAsYaml = """
        General:
          Interval: 45
          ShowClock: true
          PrimaryColor: '#base'
          AuthenticationSecret: frame-secret
          WeatherApiKey: weather-key
          Webhook: https://hook.example.com/base
        Accounts:
        - ImmichServerUrl: http://mock-immich-server.com
          ApiKey: base-api-key
        Profiles:
          kitchen:
            General:
              Interval: 10
              ShowClock: false
          living-room:
            General:
              PrimaryColor: '#living'
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

    /// <summary><see cref="TwoAccountsJson"/> written as YAML, key for key.</summary>
    private const string TwoAccountsAsYaml = """
        General:
          Interval: 45
        Accounts:
        - ImmichServerUrl: http://server-x.example.com
          ApiKey: key-x
          ShowVideos: true
        - ImmichServerUrl: http://server-y.example.com
          ApiKey: key-y
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
    /// <see cref="SharedAccountsJson"/> written as YAML, key for key - the two settings spelled out at
    /// their built-in defaults included, since they are what makes "this entry declared it" visible.
    /// </summary>
    private const string SharedAccountsAsYaml = """
        General:
          Interval: 45
        Accounts:
        - ImmichServerUrl: http://server-x.example.com
          ApiKey: key-x
          ShowFavorites: false
        Profiles:
          kitchen:
            General:
              Interval: 10
          studio:
            Accounts:
            - ImmichServerUrl: http://server-z.example.com
              ApiKey: key-z
              ShowArchived: false
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

    /// <summary>
    /// An empty entry after the first account. It binds to no account, so every account after it is
    /// shown one place earlier than it is stored - which is what a handle has to survive.
    /// </summary>
    private const string AccountsAroundAnEmptyEntryJson = """
        {
          "General": {
            "Interval": 45
          },
          "Accounts": [
            {
              "ImmichServerUrl": "http://server-x.example.com",
              "ApiKey": "key-x"
            },
            null,
            {
              "ImmichServerUrl": "http://server-y.example.com",
              "ApiKey": "key-y"
            },
            {
              "ImmichServerUrl": "http://server-z.example.com",
              "ApiKey": "key-z"
            }
          ]
        }
        """;

    /// <summary>
    /// Empty account entries in two places - after the default configuration's real account, and as
    /// the whole of a profile's own list - beside a profile that inherits the default configuration's
    /// list, and one whose account keeps its key in a file.
    /// </summary>
    private const string EmptyAccountEntriesJson = """
        {
          "General": {
            "Interval": 45
          },
          "Accounts": [
            {
              "ImmichServerUrl": "http://mock-immich-server.com",
              "ApiKey": "base-api-key"
            },
            null
          ],
          "Profiles": {
            "kitchen": {
              "Accounts": [ null ]
            },
            "living-room": {
              "General": {
                "Interval": 10
              }
            },
            "studio": {
              "Accounts": [
                {
                  "ImmichServerUrl": "http://server-z.example.com",
                  "ApiKeyFile": "API_KEY_PATH"
                }
              ]
            }
          }
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
    /// A secret that is only whitespace is not a secret to anything that reads it. The frame's
    /// handler serves every request without asking for one, a save drops it, and validation refuses
    /// an account whose key is only whitespace. The read reported each one as set anyway, so the
    /// editor told an administrator the frames were guarded while anyone could open them. This
    /// settings file is written by hand, because the editor never saves such a value.
    /// </summary>
    [Test]
    public async Task GetConfig_SecretsThatAreOnlyWhitespace_AreReportedAsNotSet()
    {
        // Arrange
        WriteSettings("Settings.json", """
            {
              "General": {
                "AuthenticationSecret": "   ",
                "WeatherApiKey": " ",
                "Webhook": "\t"
              },
              "Accounts": [
                { "ImmichServerUrl": "http://server-x.example.com", "ApiKey": "key-x" },
                { "ImmichServerUrl": "http://server-y.example.com", "ApiKey": "  " }
              ]
            }
            """);
        using var factory = CreateFactory();

        // Act
        var config = await GetConfig(factory.CreateClient());
        var general = config.Default.General;

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(general.HasAuthenticationSecret, Is.False);
            Assert.That(general.HasWeatherApiKey, Is.False);
            Assert.That(general.HasWebhook, Is.False);
            Assert.That(config.Default.Accounts[1].HasApiKey, Is.False);
            Assert.That(config.Default.Accounts[0].HasApiKey, Is.True, "a real key is still reported as set");
        });
    }

    /// <summary>
    /// Not everything this answers with is masked: a calendar's URL is sent as it stands, and that URL
    /// is often the only secret the calendar has. So no cache may keep a copy - not the browser's, for
    /// the next person at that browser to be shown, and not one between it and the server, for whoever
    /// asks next.
    /// </summary>
    [Test]
    public async Task GetConfig_IsNotStoredByAnyCache()
    {
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await Send(client, HttpMethod.Get, ConfigUrl);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        });
    }

    /// <summary>
    /// The read half of the sparse-override problem: a profile's own keys have to be
    /// distinguishable from the ones it inherits, or the editor cannot show which is which.
    /// </summary>
    [TestCase("Settings.json", SettingsJson, TestName = "{m}({0})")]
    [TestCase("Settings.yml", SettingsAsYaml, TestName = "{m}({0})")]
    public async Task GetConfig_SeparatesWhatAProfileDeclaresFromWhatItInherits(string fileName, string settings)
    {
        WriteSettings(fileName, settings);
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

    [TestCase("Settings.json", SettingsJson, TestName = "{m}({0})")]
    [TestCase("Settings.yml", SettingsAsYaml, TestName = "{m}({0})")]
    public async Task SaveConfig_OmittedSecretsAreKeptAndSuppliedOnesReplaceThem(string fileName, string settings)
    {
        WriteSettings(fileName, settings);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        // The browser never had the stored values, so it sends back exactly what it was given: a
        // masked field and nothing else. Only the weather key is actually typed.
        config.Default.General.WeatherApiKey = "typed-by-the-administrator";
        config.Default.General.AuthenticationSecret = AdminSecret.Placeholder;

        var response = await Put(client, config);
        var saved = StoredDocument(factory);

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
    /// "Keep what is stored" on a profile that stores nothing. The editor only sends the masking
    /// placeholder for a secret the profile already declares, but the endpoint can be sent it - or
    /// nothing at all - for a secret the profile inherits. That was resolved against the profile's own
    /// settings, found nothing, and written as the explicit null that overrides an inherited secret
    /// with none: a request asking to keep the frame's secret turned its authentication off. There is
    /// nothing to keep, so it is refused, with the stored settings and the running profile untouched.
    /// </summary>
    [TestCase(AdminSecret.Placeholder, TestName = "SaveConfig_ProfileKeepingAnInheritedSecretWithThePlaceholder_IsRefused")]
    [TestCase(null, TestName = "SaveConfig_ProfileKeepingAnInheritedSecretByLeavingItOut_IsRefused")]
    public async Task SaveConfig_ProfileKeepingASecretItDoesNotStore_IsRefused(string? sent)
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");

        Assume.That(kitchen.DeclaredKeys, Does.Not.Contain("General.AuthenticationSecret"),
            "the profile has to start out inheriting the secret, or there is a stored one to keep");

        kitchen.General.AuthenticationSecret = sent;
        config = config with
        {
            Profiles =
            [
                .. config.Profiles.Select(profile => profile == kitchen
                    ? kitchen with { DeclaredKeys = [.. kitchen.DeclaredKeys, "General.AuthenticationSecret"] }
                    : profile)
            ]
        };

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();
        var frame = await client.GetAsync("/api/Calendar?profile=kitchen");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("Nothing is stored for AuthenticationSecret").And.Contain("'kitchen'"),
                "the message has to name the secret and the profile");
            Assert.That(StoredText(factory), Is.EqualTo(before), "a refused save must never reach the store");
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "and the running profile still asks for the secret it inherits");
        });
    }

    /// <summary>
    /// The keep that has something to keep goes on working: a secret the profile declares itself,
    /// sent back masked or left out, is written back exactly as it was stored.
    /// </summary>
    [TestCase(AdminSecret.Placeholder, TestName = "SaveConfig_ProfileKeepingItsOwnSecretWithThePlaceholder_KeepsIt")]
    [TestCase(null, TestName = "SaveConfig_ProfileKeepingItsOwnSecretByLeavingItOut_KeepsIt")]
    public async Task SaveConfig_ProfileKeepingTheSecretItStores_KeepsIt(string? sent)
    {
        // Arrange
        WriteSettings("Settings.json",
            SettingsJson.Replace("\"Interval\": 10,", "\"Interval\": 10,\n            \"AuthenticationSecret\": \"kitchen-secret\","));
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");

        Assume.That(kitchen.DeclaredKeys, Does.Contain("General.AuthenticationSecret"),
            "the fixture has to start out declaring it");

        kitchen.General.AuthenticationSecret = sent;

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();

        using var withTheProfilesSecret = new HttpRequestMessage(HttpMethod.Get, "/api/Calendar?profile=kitchen");
        withTheProfilesSecret.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "kitchen-secret");
        var frame = await client.SendAsync(withTheProfilesSecret);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That(KitchenGeneral(factory)[nameof(GeneralSettings.AuthenticationSecret)]?.GetValue<string>(),
                Is.EqualTo("kitchen-secret"));
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the profile still takes its own secret");
            Assert.That(body, Does.Not.Contain("kitchen-secret"), "and the secret itself never travels");
        });
    }

    /// <summary>
    /// A profile whose <c>General</c> section is there but empty - YAML's <c>General:</c> with its only
    /// setting commented out, JSON's <c>"General": null</c> - names no general setting, so it runs on
    /// the default configuration's, the frame's secret included. It used to run on none of them, and
    /// served without authentication. The editor has to agree on both counts: the read declares
    /// nothing under <c>General</c> for it, and saving that read unedited changes nothing the frame
    /// serves.
    /// </summary>
    [TestCase("Settings.json", """
        {
          "General": { "Interval": 12, "AuthenticationSecret": "frame-secret" },
          "Accounts": [{ "ImmichServerUrl": "http://mock-immich-server.com", "ApiKey": "base-api-key" }],
          "Profiles": { "kitchen": { "General": null } }
        }
        """, TestName = "Config_JsonProfileWithANullGeneralSection_InheritsTheDefaultsThroughAnUneditedSave")]
    [TestCase("Settings.yml", """
        General:
          Interval: 12
          AuthenticationSecret: frame-secret
        Accounts:
        - ImmichServerUrl: http://mock-immich-server.com
          ApiKey: base-api-key
        Profiles:
          kitchen:
            General:
              # AuthenticationSecret: null
        """, TestName = "Config_YamlProfileWithItsGeneralSectionCommentedOut_InheritsTheDefaultsThroughAnUneditedSave")]
    public async Task Config_ProfileWithAnEmptyGeneralSection_InheritsTheDefaultsThroughAnUneditedSave(
        string fileName, string settings)
    {
        // Arrange
        WriteSettings(fileName, settings);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var frameBefore = await client.GetAsync("/api/Calendar?profile=kitchen");
        var config = await GetConfig(client);
        var kitchen = config.Profiles.Single(profile => profile.Name == "kitchen");

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var frameAfter = await client.GetAsync("/api/Calendar?profile=kitchen");
        var running = factory.Services.GetRequiredService<IConfigCatalog>().Get("kitchen").GeneralSettings;

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(frameBefore.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "the profile inherits the frame's secret");
            Assert.That(kitchen.DeclaredKeys, Is.Empty, "an empty section declares nothing");
            Assert.That(kitchen.General.Interval, Is.EqualTo(12), "so the editor shows what the profile inherits");
            Assert.That(kitchen.General.HasAuthenticationSecret, Is.True);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That(frameAfter.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "and an unedited save keeps it asking");
            Assert.That(running.Interval, Is.EqualTo(12));
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
    /// A secret pasted with a trailing space or line break. The frame's handler trims the bearer token
    /// a frame sends, so a secret stored with whitespace around it matched no token that any frame
    /// could send. Every frame was refused, while the editor reported the secret as set.
    /// </summary>
    [Test]
    public async Task SaveConfig_SecretWithSurroundingWhitespace_IsMatchedByTheFramesToken()
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.General.AuthenticationSecret = "s3cret \n";

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var (frame, anonymous) = await CalendarWithAndWithoutToken(client, "s3cret");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a frame sending the secret is let in");
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "and the secret still guards the frames");
        });
    }

    /// <summary>
    /// The same secret written into a settings file by hand. The import never goes through the
    /// editor's save, only through the validation that a restart runs. The trim lives in that
    /// validation, so it has to hold on this path too.
    /// </summary>
    [Test]
    public async Task Config_ImportedSecretWithSurroundingWhitespace_IsMatchedByTheFramesToken()
    {
        // Arrange
        WriteSettings("Settings.json",
            SettingsJson.Replace("\"AuthenticationSecret\": \"frame-secret\"", "\"AuthenticationSecret\": \" s3cret \\n\""));
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // Act
        var (frame, anonymous) = await CalendarWithAndWithoutToken(client, "s3cret");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a frame sending the secret is let in");
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "and the secret still guards the frames");
        });
    }

    /// <summary>
    /// <c>/api/Calendar</c> asked for by a frame presenting <paramref name="token"/>, and by one
    /// presenting nothing. It carries <c>[Authorize]</c> and, with no calendars configured, answers
    /// without any network I/O.
    /// </summary>
    private static async Task<(HttpResponseMessage Frame, HttpResponseMessage Anonymous)> CalendarWithAndWithoutToken(
        HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Calendar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (await client.SendAsync(request), await client.GetAsync("/api/Calendar"));
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

    /// <summary>
    /// The editor cannot send an API key with a line break inside it, but the endpoint can be sent one
    /// directly. Saved, it was swapped in and then failed every request for the configuration from
    /// inside the services built for it; it has to be refused instead, in words that do not quote it.
    /// </summary>
    [Test]
    public async Task SaveConfig_ApiKeyWithALineBreakInside_IsRefusedWithoutQuotingIt()
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        config.Default.Accounts[0].ApiKey = "abc\n123";

        // Act
        var response = await Put(client, config);
        var detail = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["detail"];

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(detail, Does.Contain("API key").And.Not.Contain("abc").And.Not.Contain("123"));
            Assert.That(StoredText(factory), Is.EqualTo(before),
                "a rejected configuration must never reach the store");
        });
    }

    /// <summary>
    /// The endpoint takes any number an int holds for <c>ImagesFromDays</c>. One that
    /// <see cref="DateTime"/> cannot count back from today was saved and swapped in, and then failed
    /// every request for the configuration's photos; it has to be refused instead, by name.
    /// </summary>
    [Test]
    public async Task SaveConfig_ImagesFromDaysPastWhatADateCanCountBack_IsRefused()
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        config.Default.Accounts[0].ImagesFromDays = int.MaxValue;

        // Act
        var response = await Put(client, config);
        var detail = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["detail"];

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(detail, Does.Contain("ImagesFromDays"), "the operator has to be told what is wrong");
            Assert.That(StoredText(factory), Is.EqualTo(before),
                "a rejected configuration must never reach the store");
        });
    }

    /// <summary>
    /// Immich rates a photo from 1 to 5 stars (since v3, no longer -1 or 0), and an account with a Rating shows only
    /// photos with exactly that rating. A number outside the range therefore matched no photo, and the
    /// frame went blank without saying why. The endpoint accepts any number an int holds, so the save
    /// has to refuse such a number by name.
    /// </summary>
    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(6)]
    [TestCase(int.MaxValue)]
    public async Task SaveConfig_RatingOutsideWhatImmichRates_IsRefused(int rating)
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        config.Default.Accounts[0].Rating = rating;

        // Act
        var response = await Put(client, config);
        var detail = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["detail"];

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(detail, Does.Contain("Rating"), "the operator has to be told what is wrong");
            Assert.That(StoredText(factory), Is.EqualTo(before),
                "a rejected configuration must never reach the store");
        });
    }

    /// <summary>Both ends of the range are ratings Immich gives a photo, so both are saved.</summary>
    [TestCase(1)]
    [TestCase(5)]
    public async Task SaveConfig_RatingAtEitherEndOfWhatImmichRates_IsSaved(int rating)
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.Accounts[0].Rating = rating;

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That((int?)JsonNode.Parse(StoredText(factory))?["Accounts"]?[0]?["Rating"], Is.EqualTo(rating));
        });
    }

    /// <summary>
    /// An account shows the photos taken from its ImagesFromDate up to its ImagesUntilDate. A from
    /// date later than the until date leaves no photo between them, and the frame went blank with
    /// nothing said, so the save has to refuse it by name.
    /// </summary>
    [Test]
    public async Task SaveConfig_ImagesFromDateAfterImagesUntilDate_IsRefused()
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        config.Default.Accounts[0].ImagesFromDate = new DateTime(2024, 6, 1);
        config.Default.Accounts[0].ImagesUntilDate = new DateTime(2024, 5, 31);

        // Act
        var response = await Put(client, config);
        var detail = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["detail"];

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(detail, Does.Contain("ImagesFromDate").And.Contain("ImagesUntilDate"),
                "the operator has to be told which two settings disagree");
            Assert.That(StoredText(factory), Is.EqualTo(before),
                "a rejected configuration must never reach the store");
        });
    }

    /// <summary>
    /// RefreshAlbumPeopleInterval is how many hours an account's albums and people stay cached, and
    /// the endpoint accepts any number an int holds. Two such numbers broke every request for the
    /// configuration's photos once saved. int.MaxValue hours is more than a TimeSpan can hold, and
    /// a hundred million hours is further ahead than the cache can set an expiry. The save refuses
    /// both by name. It also refuses a negative number, which is not a number of hours.
    /// </summary>
    [TestCase(int.MaxValue)]
    [TestCase(100_000_000)]
    [TestCase(87601)]
    [TestCase(-1)]
    public async Task SaveConfig_RefreshAlbumPeopleIntervalOutsideItsRange_IsRefused(int hours)
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = WithRefreshAlbumPeopleInterval(await GetConfig(client), hours);

        // Act
        var response = await Put(client, config);
        var detail = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["detail"];

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(detail, Does.Contain("RefreshAlbumPeopleInterval"), "the operator has to be told what is wrong");
            Assert.That(StoredText(factory), Is.EqualTo(before),
                "a rejected configuration must never reach the store");
        });
    }

    /// <summary>
    /// Both ends of the range are saved. 0 is a cache that expires at once, so every request fetches
    /// the albums and people again. Ten years is far longer than any frame should cache them.
    /// </summary>
    [TestCase(0)]
    [TestCase(87600)]
    public async Task SaveConfig_RefreshAlbumPeopleIntervalAtEitherEndOfItsRange_IsSaved(int hours)
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = WithRefreshAlbumPeopleInterval(await GetConfig(client), hours);

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That((int?)JsonNode.Parse(StoredText(factory))?["General"]?["RefreshAlbumPeopleInterval"],
                Is.EqualTo(hours));
        });
    }

    /// <summary>
    /// The default configuration given its own RefreshAlbumPeopleInterval. The key is declared along
    /// with the value, because a save writes only the keys an entry declares.
    /// </summary>
    private static AdminConfigDto WithRefreshAlbumPeopleInterval(AdminConfigDto config, int hours)
    {
        config.Default.General.RefreshAlbumPeopleInterval = hours;

        return config with
        {
            Default = config.Default with
            {
                DeclaredKeys = [.. config.Default.DeclaredKeys, "General.RefreshAlbumPeopleInterval"]
            }
        };
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

    /// <summary>
    /// A string whose value is spelled like YAML's null has to be written quoted, or the next load
    /// reads it as null instead. For <c>AuthenticationSecret</c> that is the difference between a
    /// frame that asks for a token and one that does not: a secret of "null" written bare reloads as
    /// no secret at all.
    /// </summary>
    [Test]
    public async Task SaveConfig_YamlStringsSpelledLikeNull_ReloadAsThoseStrings()
    {
        // Arrange
        WriteSettings("Settings.yml", SettingsYaml);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.General.AuthenticationSecret = "null";
        config.Default.Accounts[0].Tags = ["null", "~", "ok"];
        config = config with
        {
            Default = config.Default with
            {
                DeclaredKeys = [.. config.Default.DeclaredKeys, "General.AuthenticationSecret"]
            }
        };

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();

        // What the next start would load: the stored document, bound afresh.
        var reloaded = factory.Services.GetRequiredService<SettingsService>().LoadCatalog().Default;
        var frame = await client.GetAsync("/api/Calendar");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That(reloaded.GeneralSettings.AuthenticationSecret, Is.EqualTo("null"));
            Assert.That(reloaded.Accounts.Single().Tags, Is.EqualTo(new[] { "null", "~", "ok" }));
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "the frame has a secret now, so it has to ask for it");
        });
    }

    /// <summary>
    /// The other side of that quoting: a value YAML reads as null has to stay null through a save
    /// nobody edited, however the file spelled it. The binder reads <c>NULL</c> as null, so this frame
    /// has no secret - and a save that read the stored value back as the string "NULL" would now
    /// write it quoted, handing the frame a secret nobody set.
    /// </summary>
    [Test]
    public async Task SaveConfig_YamlSecretSpelledAsAnotherNull_StaysNullThroughAnUneditedSave()
    {
        // Arrange
        WriteSettings("Settings.yml",
            SettingsYaml.Replace("  PrimaryColor: '#base'", "  PrimaryColor: '#base'\n  AuthenticationSecret: NULL"));
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        Assume.That(config.Default.DeclaredKeys, Does.Contain("General.AuthenticationSecret"),
            "the fixture has to start out declaring it");

        // Act
        var response = await Put(client, config);
        var frame = await client.GetAsync("/api/Calendar");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK), "no secret was ever set");
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

    /// <summary>
    /// A v1 file is converted on import as sparsely as the editor writes: only what differs from the
    /// built-in defaults is stored. Converted in full, every default was stored as if it had been
    /// chosen - an empty weather key among them - so a later change to a default could never reach an
    /// installation that started from a v1 file. A null where the default is not still has to be
    /// stored, as the null it is: left out, it would bind to the default instead. Either way the frame
    /// runs on exactly what the v1 file said.
    /// </summary>
    [Test]
    public void FirstRun_LegacySchemaFile_IsStoredWithOnlyWhatDiffersFromTheDefaults()
    {
        // Arrange - next to what differs from the defaults: a setting, and a list, spelled out at theirs
        const string legacy = """
            {
              "ImmichServerUrl": "http://mock-immich-server.com",
              "ApiKey": "base-api-key",
              "Interval": 12,
              "ShowClock": false,
              "ClockFormat": null,
              "Language": "en",
              "ShowFavorites": false,
              "Albums": ["5b3c7d26-1d6c-4c3e-9a3b-6f1d2e8c9a01"],
              "Tags": []
            }
            """;
        WriteSettings("Settings.json", legacy);

        // Act
        using var factory = CreateFactory();
        var stored = JsonNode.Parse(StoredText(factory));
        var served = factory.Services.GetRequiredService<IConfigCatalog>().Default;
        var meant = new ServerSettingsV1Adapter(JsonSerializer.Deserialize<ServerSettingsV1>(legacy)!);

        // Assert
        served.GeneralSettings.Should().BeEquivalentTo(meant.GeneralSettings,
            "the frame runs on what the v1 file said, defaults and all");
        served.Accounts.Single().Should().BeEquivalentTo(meant.Accounts.Single());

        Assert.Multiple(() =>
        {
            Assert.That(stored?["General"]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "Interval", "ShowClock", "ClockFormat" }));
            Assert.That(stored?["General"]?.AsObject().ContainsKey("ClockFormat"), Is.True);
            Assert.That(stored?["General"]?["ClockFormat"], Is.Null, "the null is stored as one");
            Assert.That(stored?["Accounts"]?[0]?.AsObject().Select(pair => pair.Key),
                Is.EquivalentTo(new[] { "ImmichServerUrl", "ApiKey", "Albums" }));
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
    /// An API key file is read by the server, and its contents are then sent to the account's Immich
    /// server as the key. Taken from the editor, that would let an administrator - an allowlisted
    /// identity, not someone with access to this machine - read any file ImmichFrame can and post it
    /// to a server of their choosing. So a path the editor names is refused before it is opened, and
    /// in the same words whether or not it exists: reading it first answered a missing path with the
    /// operating system's "Could not find a part of the path", which made the refusal a probe of the
    /// server's file system.
    /// </summary>
    [TestCase(true, TestName = "SaveConfig_NewAccountNamingAnExistingFileAsItsApiKeyFile_IsRefused")]
    [TestCase(false, TestName = "SaveConfig_NewAccountNamingAMissingFileAsItsApiKeyFile_IsRefusedWithoutSayingSo")]
    public async Task SaveConfig_NewAccountNamingAnApiKeyFile_IsRefusedBeforeTheFileIsRead(bool exists)
    {
        // Arrange
        var path = Path.Combine(_directory, "not-for-the-editor", "secret");
        if (exists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "server-side-secret");
        }

        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);

        config = config with
        {
            Default = config.Default with
            {
                Accounts =
                [
                    .. config.Default.Accounts,
                    new AdminAccountSettingsDto { ImmichServerUrl = "http://collector.example.com", ApiKeyFile = path }
                ]
            }
        };

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("can only be set in the settings on the server"));
            Assert.That(problem, Does.Contain("collector.example.com"), "the message has to name the account");
            Assert.That(problem, Does.Not.Contain("Could not find"));
            Assert.That(problem, Does.Not.Contain(path));
            Assert.That(StoredText(factory), Is.EqualTo(before));
            Assert.That(RunningApiKeys(factory), Does.Not.Contain("server-side-secret"),
                "the file's contents must never become a credential");
        });
    }

    /// <summary>
    /// The same refusal for an account that is already stored: its handle vouches for the account,
    /// not for a path it never had. Changing an ApiKeyFile and giving one to an account that stores
    /// its key inline both introduce a path the operator did not write.
    /// </summary>
    [TestCase(true, TestName = "SaveConfig_ChangingTheApiKeyFileOfAStoredAccount_IsRefused")]
    [TestCase(false, TestName = "SaveConfig_GivingAStoredAccountAnApiKeyFile_IsRefused")]
    public async Task SaveConfig_StoredAccountNamingAnotherApiKeyFile_IsRefused(bool storedFromAFile)
    {
        // Arrange
        var keyPath = Path.Combine(_directory, "api-key");
        var otherPath = Path.Combine(_directory, "other-file");
        File.WriteAllText(keyPath, "key-from-a-file\n");
        File.WriteAllText(otherPath, "server-side-secret\n");
        WriteSettings("Settings.json", storedFromAFile ? ApiKeyFileJson.Replace("API_KEY_PATH", keyPath) : SettingsJson);

        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);

        // The handle still names the stored account; only the path is new.
        config.Default.Accounts[0].ApiKeyFile = otherPath;

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("can only be set in the settings on the server"));
            Assert.That(StoredText(factory), Is.EqualTo(before));
            Assert.That(RunningApiKeys(factory), Does.Not.Contain("server-side-secret"));
        });
    }

    /// <summary>
    /// What still has to work: an account keeping the key file it is stored with can be edited
    /// freely - server URL included - and handed to a profile by its handle, path and all.
    /// </summary>
    [Test]
    public async Task SaveConfig_KeepingTheStoredApiKeyFile_SavesWhileTheRestOfTheAccountIsEdited()
    {
        // Arrange
        var keyPath = Path.Combine(_directory, "api-key");
        File.WriteAllText(keyPath, "key-from-a-file\n");
        WriteSettings("Settings.json", ApiKeyFileJson.Replace("API_KEY_PATH", keyPath));

        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var account = config.Default.Accounts[0];

        account.ImmichServerUrl = "http://moved.example.com";
        account.ShowVideos = true;

        var hallway = new AdminConfigEntryDto("hallway", ["Accounts"], new AdminGeneralSettingsDto(),
        [
            new AdminAccountSettingsDto { Id = account.Id, ImmichServerUrl = account.ImmichServerUrl, ApiKeyFile = keyPath }
        ]);

        config = config with { Profiles = [hallway] };

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKeyFile"], Is.EqualTo(keyPath));
            Assert.That((string?)saved?["Accounts"]?[0]?["ImmichServerUrl"], Is.EqualTo("http://moved.example.com"));
            Assert.That((bool?)saved?["Accounts"]?[0]?["ShowVideos"], Is.True);
            Assert.That((string?)saved?["Profiles"]?["hallway"]?["Accounts"]?[0]?["ApiKeyFile"], Is.EqualTo(keyPath));
        });
    }

    /// <summary>
    /// The operator's own key file can still be unreadable - moved, deleted or re-permissioned since
    /// it was stored. That is refused without the operating system's message as well, which would
    /// otherwise tell the editor whether the path is missing, a directory or merely unreadable; the
    /// operator finds it in the log instead.
    /// </summary>
    [Test]
    public async Task SaveConfig_StoredApiKeyFileThatCanNoLongerBeRead_IsRefusedWithoutTheOperatingSystemsMessage()
    {
        // Arrange
        var keyPath = Path.Combine(_directory, "api-key");
        File.WriteAllText(keyPath, "key-from-a-file\n");
        WriteSettings("Settings.json", ApiKeyFileJson.Replace("API_KEY_PATH", keyPath));

        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        File.Delete(keyPath);

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("could not be read"));
            Assert.That(problem, Does.Not.Contain("Could not find"));
            Assert.That(problem, Does.Not.Contain(keyPath));
            Assert.That(StoredText(factory), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// Validation goes on past its first failure, so an unreadable key file can now be the second thing
    /// wrong with a save rather than the first - and it still must not reach the editor in the
    /// operating system's words. An exception's inner chain only reaches the first failure, so
    /// searching that alone would pass this one's message, path and all, straight through.
    /// </summary>
    [Test]
    public async Task SaveConfig_UnreadableApiKeyFileBehindAnotherFailure_IsStillRefusedWithoutTheOperatingSystemsMessage()
    {
        // Arrange
        var keyPath = Path.Combine(_directory, "api-key");
        File.WriteAllText(keyPath, "key-from-a-file\n");
        WriteSettings("Settings.json",
            TwoAccountsJson.Replace("\"ApiKey\": \"key-y\"", $"\"ApiKeyFile\": {JsonSerializer.Serialize(keyPath)}"));

        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        config.Default.Accounts[0].ImagesFromDays = int.MaxValue;
        File.Delete(keyPath);

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("could not be read"));
            Assert.That(problem, Does.Not.Contain("Could not find"));
            Assert.That(problem, Does.Not.Contain(keyPath));
            Assert.That(StoredText(factory), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// The one that would leak a credential. Delete the first of two accounts and save the survivor
    /// with its key still masked: matching the stored key by position would read slot 0 - the deleted
    /// account's key - and write it under the surviving account's server URL, so ImmichFrame would
    /// start sending server X's API key to server Y. It validates, it swaps in, and the version token
    /// refreshes, so nothing about the save looks wrong.
    /// </summary>
    [TestCase("Settings.json", TwoAccountsJson, TestName = "{m}({0})")]
    [TestCase("Settings.yml", TwoAccountsAsYaml, TestName = "{m}({0})")]
    public async Task SaveConfig_DeletingTheFirstAccount_LeavesTheSurvivorWithItsOwnKey(string fileName, string settings)
    {
        WriteSettings(fileName, settings);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        var survivor = config.Default.Accounts[1];

        config = config with { Default = config.Default with { Accounts = [survivor] } };

        var response = await Put(client, config);
        var saved = StoredDocument(factory);

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
    [TestCase("Settings.json", TwoAccountsJson, TestName = "{m}({0})")]
    [TestCase("Settings.yml", TwoAccountsAsYaml, TestName = "{m}({0})")]
    public async Task SaveConfig_ReorderingAccounts_MovesEachKeyWithItsAccount(string fileName, string settings)
    {
        WriteSettings(fileName, settings);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config = config with
        {
            Default = config.Default with { Accounts = [config.Default.Accounts[1], config.Default.Accounts[0]] }
        };

        var response = await Put(client, config);
        var saved = StoredDocument(factory);

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
    /// An empty entry in a stored account list shifts every account after it: the read shows those
    /// accounts one position earlier than the document stores them. A handle issued by one count and
    /// resolved by the other landed the account after the empty entry on the empty entry's own slot,
    /// which holds no stored account - so it could not keep its key, and an unedited save was refused.
    /// </summary>
    [Test]
    public async Task SaveConfig_AccountListWithAnEmptyEntry_KeepsEachKeyOnItsOwnAccount()
    {
        // Arrange
        WriteSettings("Settings.json", AccountsAroundAnEmptyEntryJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);

        // Act - saved as it was read: every key masked, each account carrying the handle it was issued
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(config.Default.Accounts.Select(account => account.ImmichServerUrl),
                Is.EqualTo(new[] { "http://server-x.example.com", "http://server-y.example.com", "http://server-z.example.com" }),
                "the editor is shown the accounts, and nothing for the empty entry");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That(saved?["Accounts"]?.AsArray(), Has.Exactly(3).Items,
                "a save writes the empty entry out of the stored settings");
            Assert.That((string?)saved?["Accounts"]?[0]?["ApiKey"], Is.EqualTo("key-x"));
            Assert.That((string?)saved?["Accounts"]?[1]?["ImmichServerUrl"], Is.EqualTo("http://server-y.example.com"));
            Assert.That((string?)saved?["Accounts"]?[1]?["ApiKey"], Is.EqualTo("key-y"),
                "the account after the empty entry keeps its own key");
            Assert.That((string?)saved?["Accounts"]?[2]?["ApiKey"], Is.EqualTo("key-z"));
        });
    }

    /// <summary>
    /// The same shift, where it would leak a credential rather than refuse a save. Asked to retype the
    /// key of the account after the empty entry, an administrator does; the account after that one
    /// then resolved its handle to the retyped account's stored slot, and was saved with that
    /// account's old key under its own server URL - one Immich server sent another's credential,
    /// silently, by a save that validated and went through.
    /// </summary>
    [Test]
    public async Task SaveConfig_AccountAfterAnEmptyEntry_IsNeverSavedWithAnotherAccountsKey()
    {
        // Arrange
        WriteSettings("Settings.json", AccountsAroundAnEmptyEntryJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.Accounts[1].ApiKey = "key-y-retyped";

        // Act
        var response = await Put(client, config);
        var body = await response.Content.ReadAsStringAsync();
        var saved = JsonNode.Parse(StoredText(factory));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That((string?)saved?["Accounts"]?[1]?["ApiKey"], Is.EqualTo("key-y-retyped"));
            Assert.That((string?)saved?["Accounts"]?[2]?["ImmichServerUrl"], Is.EqualTo("http://server-z.example.com"));
            Assert.That((string?)saved?["Accounts"]?[2]?["ApiKey"], Is.EqualTo("key-z"),
                "server z keeps its own key, never the one stored for server y");
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
    [TestCase("Settings.json", SharedAccountsJson, TestName = "{m}({0})")]
    [TestCase("Settings.yml", SharedAccountsAsYaml, TestName = "{m}({0})")]
    public async Task SaveConfig_UneditedConfigurationSavedTwice_IsWrittenIdenticallyAndStaysSparse(
        string fileName, string settings)
    {
        // Arrange
        WriteSettings(fileName, settings);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // Act
        var first = await Put(client, await GetConfig(client));
        var afterFirst = StoredText(factory);

        var second = await Put(client, await GetConfig(client));
        var afterSecond = StoredText(factory);

        var saved = StoredDocument(factory);

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
    /// A current-schema file that names no accounts. The import only checks that a file binds, so it
    /// is stored as it stands, and it has to run as an ImmichFrame with nothing to show yet. Bound to a
    /// null account list instead, it failed every request the host served: the frame's services are
    /// built for the authentication handler, ahead of any controller, and building them dereferenced
    /// the null - so the editor's own read answered 500 too, and the one tool that could add the
    /// missing account was locked out.
    /// </summary>
    [TestCase("""{ "General": { "Interval": 45 } }""",
        TestName = "Config_ImportedSettingsWithNoAccountsKey_KeepTheEditorAndTheFrameAnswering")]
    [TestCase("""{ "General": { "Interval": 45 }, "Accounts": null }""",
        TestName = "Config_ImportedSettingsWithNullAccounts_KeepTheEditorAndTheFrameAnswering")]
    public async Task Config_ImportedSettingsWithoutAccounts_KeepTheEditorAndTheFrameAnswering(string settings)
    {
        WriteSettings("Settings.json", settings);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        Assume.That(StoredText(factory), Is.EqualTo(settings),
            "the file has to have been imported as it stands, or this is the unconfigured case instead");

        var read = await Send(client, HttpMethod.Get, ConfigUrl);
        var frame = await client.GetAsync("/api/Asset");

        Assert.Multiple(() =>
        {
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "the editor is the one tool that can add the missing account");
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "a frame with nothing to show is not a failed one");
        });

        var config = await GetConfig(client);
        var assets = JsonNode.Parse(await frame.Content.ReadAsStringAsync())?.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(config.Default.Accounts, Is.Empty);
            Assert.That(assets, Is.Empty);
        });
    }

    /// <summary>
    /// An account list holding an empty entry - JSON's null, or the bare '-' a YAML list is left with
    /// - imported as it stands, since the import only checks that the file binds. The entry bound to a
    /// null account. In the default configuration's list that failed every request the host served,
    /// building the frame's services for the authentication handler, so the editor's own read
    /// answered 500 with the frame; in a profile's own list it failed that profile's frame and the
    /// editor's read. The file is imported once, so correcting it changed nothing either.
    /// </summary>
    [TestCase("Settings.json", """{ "General": { "Interval": 45 }, "Accounts": [null] }""", null, 0,
        TestName = "Config_ImportedAccountListOfOneNull_KeepsTheEditorAndTheFrameAnswering")]
    [TestCase("Settings.json", """
        {
          "General": { "Interval": 45 },
          "Accounts": [{ "ImmichServerUrl": "http://mock-immich-server.com", "ApiKey": "base-api-key" }, null]
        }
        """, null, 1, TestName = "Config_ImportedAccountListEndingInNull_KeepsTheEditorAndTheFrameAnswering")]
    [TestCase("Settings.yml", "General:\n  Interval: 45\nAccounts:\n-\n", null, 0,
        TestName = "Config_ImportedYamlAccountListOfABareDash_KeepsTheEditorAndTheFrameAnswering")]
    [TestCase("Settings.json", """
        {
          "General": { "Interval": 45 },
          "Accounts": [{ "ImmichServerUrl": "http://mock-immich-server.com", "ApiKey": "base-api-key" }],
          "Profiles": { "kitchen": { "Accounts": [null] } }
        }
        """, "kitchen", 0, TestName = "Config_ImportedProfileAccountListOfOneNull_KeepsTheEditorAndTheFrameAnswering")]
    public async Task Config_ImportedAccountListWithAnEmptyEntry_KeepsTheEditorAndTheFrameAnswering(
        string fileName, string settings, string? profile, int accounts)
    {
        // A real account is asked for its slideshow, so its server has to answer: with no photos.
        WriteSettings(fileName, settings);
        using var factory = CreateFactory(
            immich: new Mock<HttpMessageHandler>().WithServerVersion().WithAssetStatistics(0).WithNoRandomAssets());
        var client = factory.CreateClient();

        Assume.That(StoredText(factory), Is.EqualTo(settings),
            "the file has to have been imported as it stands, or this is the unconfigured case instead");

        var read = await Send(client, HttpMethod.Get, ConfigUrl);
        var frame = await client.GetAsync("/api/Asset");
        var profileFrame = profile is null ? null : await client.GetAsync($"/api/Asset?profile={profile}");

        Assert.Multiple(() =>
        {
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "the editor is the one tool that can remove the empty entry");
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the frame runs on the accounts that are there");
            Assert.That(profileFrame?.StatusCode, profile is null ? Is.Null : Is.EqualTo(HttpStatusCode.OK),
                "and so does the profile's");
        });

        var config = await GetConfig(client);
        var entry = profile is null ? config.Default : config.Profiles.Single(candidate => candidate.Name == profile);

        Assert.That(entry.Accounts.Select(account => account?.ImmichServerUrl),
            Is.EqualTo(Enumerable.Repeat("http://mock-immich-server.com", accounts)),
            "the editor shows the real accounts, and nothing for the empty entry");
    }

    /// <summary>
    /// An empty entry is dropped rather than refused, so the startup log is where it shows: by
    /// position, against the configuration that declares it - a profile inheriting the default
    /// configuration's list is not blamed for that list's empty entry - and as a warning that leaves
    /// validation to run on. An entry that is already ignored does not make the stored settings
    /// invalid, so it has no place in the Critical line, and every profile is still validated - its
    /// key file read - around it.
    /// </summary>
    [Test]
    public void Config_ImportedEmptyAccountEntries_AreEachNamedWithoutStoppingValidation()
    {
        // Arrange
        var keyPath = Path.Combine(_directory, "api-key");
        File.WriteAllText(keyPath, "key-from-a-file\n");
        WriteSettings("Settings.json", EmptyAccountEntriesJson.Replace("API_KEY_PATH", keyPath));

        var logs = new LogSink();
        using var factory = CreateFactory(logs: logs);

        // Act - the first use of the catalog seeds it from the stored settings, and validates them
        var studio = factory.Services.GetRequiredService<IConfigCatalog>().Get("studio");

        var warnings = logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToList();
        var critical = logs.Entries.Where(entry => entry.Level == LogLevel.Critical).Select(entry => entry.Message).ToList();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(warnings, Has.Exactly(1).Matches<string>(message =>
                message.Contains("Accounts entry 2") && message.Contains("the default configuration")));
            Assert.That(warnings, Has.Exactly(1).Matches<string>(message =>
                message.Contains("Accounts entry 1") && message.Contains("'kitchen'")));
            Assert.That(warnings, Has.None.Contains("'living-room'"),
                "a profile inheriting the default configuration's list declares no entry of its own");

            Assert.That(studio.Accounts.Single().ApiKey, Is.EqualTo("key-from-a-file"),
                "a profile validated after the empty entries still has its key file read");
            Assert.That(critical, Has.None.Contains("not valid"),
                "an entry that is ignored does not make the stored settings invalid");
        });
    }

    /// <summary>
    /// Stored settings that do not validate are still run, because the editor that fixes them is
    /// served by this very process: the failure is logged as Critical and the frame goes on serving.
    /// The log has to name every configuration that fails rather than the first, and every account
    /// has to have been validated - which is also what reads an ApiKeyFile into its key. Validation
    /// used to stop at the first failure, so here every account after the default configuration's
    /// first one ran with an empty key, the profiles' included.
    /// </summary>
    [Test]
    public async Task Config_StoredSettingsThatDoNotValidate_AreLoggedAsCriticalAndServedAnyway()
    {
        // Arrange
        var secondKey = Path.Combine(_directory, "second-key");
        var kitchenKey = Path.Combine(_directory, "kitchen-key");
        File.WriteAllText(secondKey, "key-from-the-second-file\n");
        File.WriteAllText(kitchenKey, "key-from-the-kitchen-file\n");

        WriteSettings("Settings.json", $$"""
            {
              "General": { "Interval": 45 },
              "Accounts": [
                { "ImmichServerUrl": "http://mock-immich-server.com", "ApiKey": "base-api-key", "ImagesFromDays": 99999 },
                { "ImmichServerUrl": "http://mock-immich-server.com", "ApiKeyFile": {{JsonSerializer.Serialize(secondKey)}} }
              ],
              "Profiles": {
                "kitchen": {
                  "Accounts": [{ "ImmichServerUrl": "http://mock-immich-server.com", "ApiKeyFile": {{JsonSerializer.Serialize(kitchenKey)}} }]
                },
                "hallway": {
                  "Accounts": [{ "ImmichServerUrl": "http://mock-immich-server.com" }]
                }
              }
            }
            """);

        var logs = new LogSink();
        using var factory = CreateFactory(logs: logs);
        var client = factory.CreateClient();

        // Act - the first request seeds the catalog from the stored settings, and validates them
        var frame = await client.GetAsync("/api/Config");
        var editor = await Send(client, HttpMethod.Get, ConfigUrl);
        var catalog = factory.Services.GetRequiredService<IConfigCatalog>();

        var critical = logs.Entries.Where(entry => entry.Level == LogLevel.Critical).Select(entry => entry.Message).ToList();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(critical, Has.Exactly(1).Matches<string>(message =>
                    message.Contains("default configuration") && message.Contains("ImagesFromDays") &&
                    message.Contains("'hallway'") && message.Contains("Either ApiKey or ApiKeyFile")),
                "one Critical line, naming both configurations that fail and what is wrong with each");
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the frame keeps running");
            Assert.That(editor.StatusCode, Is.EqualTo(HttpStatusCode.OK), "and so does the editor that fixes it");

            Assert.That(catalog.Default.Accounts.Select(account => account.ApiKey),
                Is.EqualTo(new[] { "base-api-key", "key-from-the-second-file" }),
                "an account after the one that fails still has its key file read");
            Assert.That(catalog.Get("kitchen").Accounts.Single().ApiKey, Is.EqualTo("key-from-the-kitchen-file"),
                "and so does a profile validated after the configuration that fails");
        });
    }

    /// <summary>
    /// Stored settings that cannot even be bound - here a profile named after a path a later version
    /// reserved, which is what an upgrade can leave behind - hold no configuration that could run in
    /// part, so they are served as an instance nothing has configured: one Critical line saying why,
    /// and a frame with nothing to show. Nothing is behind the missing secret either - no account, no
    /// calendar, no weather key. Thrown instead, from the catalog's seed, the failure took every
    /// request with it, the editor's that could correct the name among them, and the same document
    /// fails the same way however often it is read.
    /// </summary>
    [Test]
    public async Task Config_StoredSettingsThatCannotBeBound_AreServedAsUnconfiguredWithACriticalLine()
    {
        // Arrange - stored past the checks a save makes, by a run before this one
        WriteSettings("Settings.json", SettingsJson);

        using (var earlier = CreateFactory())
        {
            var store = earlier.Services.GetRequiredService<SettingsService>();
            store.Save(SettingsJson.Replace("\"living-room\"", "\"admin\""), ConfigFormat.Json, store.Read().Version);
        }

        var logs = new LogSink();
        using var factory = CreateFactory(logs: logs);
        var client = factory.CreateClient();

        // Act - the first request seeds the catalog from the stored settings
        var frame = await client.GetAsync("/api/Asset");
        var editor = await Send(client, HttpMethod.Get, ConfigUrl);
        var served = factory.Services.GetRequiredService<IConfigCatalog>();
        var accounts = served.Default.Accounts.ToList();
        var profiles = served.ProfileNames;

        var critical = logs.Entries.Where(entry => entry.Level == LogLevel.Critical).Select(entry => entry.Message).ToList();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a frame with nothing to show is not a failed one");
            Assert.That(editor.StatusCode, Is.EqualTo(HttpStatusCode.OK), "and the editor that renames the profile answers");
            Assert.That(accounts, Is.Empty);
            Assert.That(profiles, Is.Empty);
            Assert.That(critical, Has.Exactly(1).Matches<string>(message => message.Contains("'admin' is a reserved name")),
                "the operator is told why nothing is configured");
        });
    }

    /// <summary>
    /// A calendar list spelled as null: <c>"Webcalendars": null</c>, YAML's bare <c>Webcalendars:</c>,
    /// or the same null in a v1 file, which the import carried into the current schema as it stood.
    /// The binder calls the setter with that null, over the empty list the settings class starts out
    /// with, and the calendar service enumerated it on every poll - so every <c>GET /api/Calendar</c>
    /// answered 500, for a frame whose settings only say it has no calendars.
    /// </summary>
    [TestCase("Settings.json", """
        {
          "General": { "Webcalendars": null },
          "Accounts": [{ "ImmichServerUrl": "http://mock-immich-server.com", "ApiKey": "base-api-key" }]
        }
        """, TestName = "Config_ImportedNullWebcalendars_KeepTheCalendarAnswering")]
    [TestCase("Settings.yml", """
        General:
          Webcalendars:
        Accounts:
        - ImmichServerUrl: http://mock-immich-server.com
          ApiKey: base-api-key
        """, TestName = "Config_ImportedBareYamlWebcalendars_KeepTheCalendarAnswering")]
    [TestCase("Settings.json", """
        {
          "ImmichServerUrl": "http://mock-immich-server.com",
          "ApiKey": "base-api-key",
          "Webcalendars": null
        }
        """, TestName = "Config_ImportedLegacySchemaNullWebcalendars_KeepTheCalendarAnswering")]
    public async Task Config_ImportedWebcalendarsSpelledAsNull_KeepTheCalendarAnswering(string fileName, string settings)
    {
        // Arrange
        WriteSettings(fileName, settings);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        Assume.That(factory.Services.GetRequiredService<SettingsService>().IsUnconfigured, Is.False,
            "the file has to have been imported, or this is the unconfigured case instead");

        // Act
        var calendar = await client.GetAsync("/api/Calendar");
        var webcalendars = factory.Services.GetRequiredService<IConfigCatalog>().Default.GeneralSettings.Webcalendars;

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(calendar.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "a frame with no calendars is not a failed one");
            Assert.That(webcalendars, Is.Not.Null.And.Empty, "bound as no calendars rather than as a null");
        });

        var appointments = JsonNode.Parse(await calendar.Content.ReadAsStringAsync())?.AsArray();

        Assert.That(appointments, Is.Empty);
    }

    /// <summary>
    /// The account's four lists spelled as null, in JSON and as YAML's bare keys. The binder calls each
    /// setter with that null, over the empty list the settings class starts out with, so an account
    /// that narrows nothing reached ImmichFrame.Core with lists its interface says are never null. Core
    /// guards each of these reads anyway; the settings still have to keep the interface's promise
    /// rather than lean on that.
    /// </summary>
    [TestCase("Settings.json", """
        {
          "General": { "Interval": 45 },
          "Accounts": [
            {
              "ImmichServerUrl": "http://mock-immich-server.com",
              "ApiKey": "base-api-key",
              "Albums": null,
              "ExcludedAlbums": null,
              "People": null,
              "Tags": null
            }
          ]
        }
        """, TestName = "Config_ImportedNullAccountLists_BindEmptyAndKeepTheFrameAnswering")]
    [TestCase("Settings.yml", """
        General:
          Interval: 45
        Accounts:
        - ImmichServerUrl: http://mock-immich-server.com
          ApiKey: base-api-key
          Albums:
          ExcludedAlbums:
          People:
          Tags:
        """, TestName = "Config_ImportedBareYamlAccountLists_BindEmptyAndKeepTheFrameAnswering")]
    public async Task Config_ImportedAccountListsSpelledAsNull_BindEmptyAndKeepTheFrameAnswering(
        string fileName, string settings)
    {
        // Arrange - the account is asked for its slideshow, so its server has to answer: with no photos.
        WriteSettings(fileName, settings);
        using var factory = CreateFactory(
            immich: new Mock<HttpMessageHandler>().WithServerVersion().WithAssetStatistics(0).WithNoRandomAssets());
        var client = factory.CreateClient();

        Assume.That(StoredText(factory), Is.EqualTo(settings),
            "the file has to have been imported as it stands, or this is the unconfigured case instead");

        // Act
        var frame = await client.GetAsync("/api/Asset");
        var account = factory.Services.GetRequiredService<IConfigCatalog>().Default.Accounts.Single();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(frame.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "the frame runs on the account as it would with the lists left out");
            Assert.That(account.Albums, Is.Not.Null.And.Empty);
            Assert.That(account.ExcludedAlbums, Is.Not.Null.And.Empty);
            Assert.That(account.People, Is.Not.Null.And.Empty);
            Assert.That(account.Tags, Is.Not.Null.And.Empty);
        });
    }

    /// <summary>
    /// The save's half. The editor always sends a list, but the endpoint can be sent a null for
    /// <c>General.Webcalendars</c>, and every declared setting but the three secrets is written as it
    /// arrives. That save bound, validated and swapped in like any other, and the calendar then
    /// answered 500 on every poll.
    /// </summary>
    [Test]
    public async Task SaveConfig_WebcalendarsDeclaredAsNull_KeepsTheCalendarAnswering()
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var config = await GetConfig(client);
        config.Default.General.Webcalendars = null;
        config = config with
        {
            Default = config.Default with
            {
                DeclaredKeys = [.. config.Default.DeclaredKeys, "General.Webcalendars"]
            }
        };

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        using var calendarRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Calendar");
        calendarRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FrameSecret);
        var calendar = await client.SendAsync(calendarRequest);

        var webcalendars = factory.Services.GetRequiredService<IConfigCatalog>().Default.GeneralSettings.Webcalendars;

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), problem);
            Assert.That(calendar.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "the saved configuration says no calendars, which is not a failed calendar");
            Assert.That(webcalendars, Is.Not.Null.And.Empty);
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
    /// an empty account list breaks outright. Both shapes have to be refused, and in words: the
    /// settings bind and validate without an account - no accounts is a state ImmichFrame can run in -
    /// so nothing after the editor would stop either from being saved. An absent Accounts key used to
    /// reach the loader as a null dereference instead, reported as "Object reference not set to an
    /// instance of an object."
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
    /// The request's half of the empty account entry. A save is not a settings file to be read
    /// leniently - the editor never sends an empty entry - so one is a malformed request, and is
    /// refused by name with the stored settings untouched. It used to reach the label check as a null
    /// dereference and answer 500.
    /// </summary>
    [TestCase(null, TestName = "SaveConfig_EmptyAccountInTheDefaultConfiguration_IsRefusedReadably")]
    [TestCase("kitchen", TestName = "SaveConfig_EmptyAccountInAProfile_IsRefusedReadably")]
    public async Task SaveConfig_EmptyAccountInTheRequest_IsRefusedReadably(string? profileName)
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);

        config = profileName is null
            ? config with { Default = config.Default with { Accounts = [.. config.Default.Accounts, null!] } }
            : config with
            {
                Profiles =
                [
                    .. config.Profiles.Select(profile => profile.Name == profileName
                        ? profile with { DeclaredKeys = [.. profile.DeclaredKeys, "Accounts"], Accounts = [null!] }
                        : profile)
                ]
            };

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), problem);
            Assert.That(problem, Does.Contain("empty"));
            Assert.That(problem, Does.Contain(profileName is null ? "the default configuration" : $"'{profileName}'"),
                "the message has to say which list to fix");
            Assert.That(problem, Does.Not.Contain("Object reference"));
            Assert.That(StoredText(factory), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// An empty entry in the request's profile list. Model binding accepts it, as it accepts an
    /// empty account. The editor never sends one, so it is a malformed request. The save used to hit
    /// it as a null dereference and answer 500, and now refuses it by name with the stored settings
    /// untouched.
    /// </summary>
    [Test]
    public async Task SaveConfig_EmptyProfileInTheRequest_IsRefusedReadably()
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);
        config = config with { Profiles = [.. config.Profiles, null!] };

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), problem);
            Assert.That(problem, Does.Contain("empty"));
            Assert.That(problem, Does.Not.Contain("Object reference"));
            Assert.That(StoredText(factory), Is.EqualTo(before));
        });
    }

    /// <summary>
    /// An empty entry in one configuration's DeclaredKeys, for the same reasons as an empty profile.
    /// Binding accepts it, the editor never sends it, and the save hit it as a null dereference and
    /// answered 500.
    /// </summary>
    [TestCase(null, TestName = "SaveConfig_EmptyDeclaredKeyInTheDefaultConfiguration_IsRefusedReadably")]
    [TestCase("kitchen", TestName = "SaveConfig_EmptyDeclaredKeyInAProfile_IsRefusedReadably")]
    public async Task SaveConfig_EmptyDeclaredKeyInTheRequest_IsRefusedReadably(string? profileName)
    {
        // Arrange
        WriteSettings("Settings.json", SettingsJson);
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var before = StoredText(factory);
        var config = await GetConfig(client);

        config = profileName is null
            ? config with { Default = config.Default with { DeclaredKeys = [.. config.Default.DeclaredKeys, null!] } }
            : config with
            {
                Profiles =
                [
                    .. config.Profiles.Select(profile => profile.Name == profileName
                        ? profile with { DeclaredKeys = [.. profile.DeclaredKeys, null!] }
                        : profile)
                ]
            };

        // Act
        var response = await Put(client, config);
        var problem = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), problem);
            Assert.That(problem, Does.Contain("empty"));
            Assert.That(problem, Does.Contain(profileName is null ? "the default configuration" : $"'{profileName}'"),
                "the message has to say which configuration to fix");
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

    /// <summary>
    /// A legacy environment setup is recognised by its variables being set, not by their values
    /// parsing: nothing reads the values any more. A boolean spelled "yes" used to make the loader
    /// give up on the environment altogether, so the administrator was told there was no
    /// configuration to import rather than that configuring through environment variables is no
    /// longer supported.
    /// </summary>
    [Test]
    public void FirstRun_LegacyEnvironmentWithAnUnparseableValue_IsStillRecognisedAsTheEnvironment()
    {
        string[] variables = ["ImmichServerUrl", "ApiKey", "ShowClock"];
        var restore = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("ImmichServerUrl", "http://mock-immich-server.com");
        Environment.SetEnvironmentVariable("ApiKey", "env-api-key");
        Environment.SetEnvironmentVariable("ShowClock", "yes");

        try
        {
            using var factory = CreateFactory();

            // What the first-run import branches on: Environment is the "no longer supported"
            // warning, a throw is "no configuration to import".
            var source = factory.Services.GetRequiredService<ConfigLoader>().DescribeSource(_directory);

            Assert.Multiple(() =>
            {
                Assert.That(source.Format, Is.EqualTo(ConfigFormat.Environment));
                Assert.That(factory.Services.GetRequiredService<SettingsService>().IsUnconfigured, Is.True,
                    "the environment still configures nothing");
            });
        }
        finally
        {
            foreach (var (name, value) in restore)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
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

    /// <summary>
    /// The API keys the default configuration is running with. Validation has already replaced each
    /// ApiKeyFile with what that file holds, so a file read on a save shows up here as a key.
    /// </summary>
    private static IEnumerable<string> RunningApiKeys(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IConfigCatalog>().Default.Accounts.Select(account => account.ApiKey);

    /// <summary>The stored row's version - the token the editor round-trips as its concurrency check.</summary>
    private static long StoredVersion(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<SettingsService>().Read().Version;

    /// <summary>The format the stored document is written in - a YAML import stays YAML.</summary>
    private static ConfigFormat StoredFormat(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<SettingsService>().Read().Format;

    /// <summary>
    /// The stored document as a JSON tree whichever format it is written in, for the tests that run
    /// against both. A YAML document's scalars all arrive as strings - YAML resolves no types until it
    /// is bound - so read keys and strings off this, never numbers or booleans.
    /// </summary>
    private static JsonNode? StoredDocument(WebApplicationFactory<Program> factory) =>
        StoredFormat(factory) == ConfigFormat.Yaml
            ? FromYaml(new DeserializerBuilder().Build().Deserialize<object?>(StoredText(factory)))
            : JsonNode.Parse(StoredText(factory));

    private static JsonNode? FromYaml(object? node) => node switch
    {
        IDictionary<object, object?> mapping => new JsonObject(
            mapping.Select(pair => KeyValuePair.Create((string)pair.Key, FromYaml(pair.Value)))),
        IList<object?> sequence => new JsonArray(sequence.Select(FromYaml).ToArray()),
        null => null,
        _ => JsonValue.Create(node.ToString())
    };

    private string SettingsPath(string name) => Path.Combine(_directory, name);

    private void WriteSettings(string name, string contents) => File.WriteAllText(SettingsPath(name), contents);

    /// <param name="immich">
    /// What the Immich servers answer. By default only the startup version check, which is all a test
    /// that never asks the frame for a photo needs.
    /// </param>
    /// <param name="logs">Where the host's log entries are kept, for a test that asserts on them.</param>
    private WebApplicationFactory<Program> CreateFactory(
        Mock<HttpMessageHandler>? immich = null, LogSink? logs = null)
    {
        var immichHandler = immich ?? new Mock<HttpMessageHandler>().WithServerVersion();
        var directory = _directory;

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(immichHandler);

                    if (logs is not null)
                    {
                        services.AddSingleton<ILoggerProvider>(logs);
                    }

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

    /// <summary>
    /// Every entry the host logs, for the tests that are about what an operator is told. Concurrent,
    /// because the startup version check logs from a thread of its own.
    /// </summary>
    private sealed class LogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IEnumerable<(LogLevel Level, string Message)> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new Logger(_entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(LogLevel Level, string Message)> _entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
