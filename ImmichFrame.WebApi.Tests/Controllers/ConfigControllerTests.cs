using System.Net.Http;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers
{
    [TestFixture]
    public class ConfigControllerTests
    {
        private WebApplicationFactory<Program> _factory;

        [SetUp]
        public void Setup()
        {
            // Every value but the flags differs from every other and from its own default, so that a
            // setting sent as another's value, or as a constant, reads wrong. The flags have two values
            // to go round, so they alternate down the order ClientSettingsDto declares them in instead:
            // a flag read from the line next to it is the slip that shows.
            var generalSettings = new GeneralSettings
            {
                Interval = 33,
                TransitionDuration = 2.5,
                DownloadImages = true,
                RenewImagesDuration = 77,
                ShowClock = false,
                ClockFormat = "HH:mm:ss",
                ClockDateFormat = "yyyy-MM-dd",
                ShowPhotoDate = true,
                ShowProgressBar = false,
                PhotoDateFormat = "dd.MM.yyyy",
                ShowImageDesc = true,
                ShowPeopleDesc = false,
                ShowTagsDesc = true,
                ShowAlbumName = false,
                ShowImageLocation = true,
                ImageLocationFormat = "City",
                PrimaryColor = "#112233",
                SecondaryColor = "#445566",
                Style = "blur",
                BaseFontSize = "18px",
                ShowWeatherDescription = false,
                WeatherIconUrl = "https://example.com/{IconId}.png",
                ImageZoom = true,
                ImagePan = false,
                ImageFill = true,
                PlayAudio = false,
                Layout = "single",
                Language = "de",
                // Server-only values that must never surface in the client config:
                WeatherApiKey = "secret-weather-key",
                WeatherLatLong = "1.23,4.56",
                UnitSystem = "metric",
                Webhook = "https://webhook.example.com/secret-hook",
                AuthenticationSecret = "secret-auth-token",
                Webcalendars = new List<string> { "https://calendar.example.com/secret.ics" },
                RefreshAlbumPeopleInterval = 8,
            };

            var serverSettings = new ServerSettings
            {
                GeneralSettingsImpl = generalSettings,
                AccountsImpl = new List<ServerAccountSettings>
                {
                    new()
                    {
                        ImmichServerUrl = "http://mock-immich-server.com",
                        ApiKey = "secret-api-key",
                    }
                }
            };

            var versionHandler = new Mock<HttpMessageHandler>().WithServerVersion();

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.UseMockHandler(versionHandler);

                        // Seeding the catalog rather than IServerSettings directly: the per-profile
                        // services are built from the catalog, and IClientSettings resolves down
                        // through it.
                        services.AddSingleton<IConfigCatalog>(new ConfigCatalog(serverSettings));
                    });
                });
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
        }

        /// <summary>
        /// What a frame is sent, written out value by value. Not built from <c>ClientSettingsDto</c>:
        /// an expectation produced by the mapping under test agrees with whatever that mapping does, so
        /// a setting sent as another's value - or as a constant - passed.
        /// </summary>
        [Test]
        public async Task GetConfig_ReturnsSerializedClientSettings()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/api/Config");

            response.EnsureSuccessStatusCode();
            var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync());

            var expected = JsonNode.Parse("""
                {
                  "interval": 33,
                  "transitionDuration": 2.5,
                  "downloadImages": true,
                  "renewImagesDuration": 77,
                  "showClock": false,
                  "clockFormat": "HH:mm:ss",
                  "clockDateFormat": "yyyy-MM-dd",
                  "showPhotoDate": true,
                  "showProgressBar": false,
                  "photoDateFormat": "dd.MM.yyyy",
                  "showImageDesc": true,
                  "showPeopleDesc": false,
                  "showTagsDesc": true,
                  "showAlbumName": false,
                  "showImageLocation": true,
                  "imageLocationFormat": "City",
                  "primaryColor": "#112233",
                  "secondaryColor": "#445566",
                  "style": "blur",
                  "baseFontSize": "18px",
                  "showWeatherDescription": false,
                  "weatherIconUrl": "https://example.com/{IconId}.png",
                  "imageZoom": true,
                  "imagePan": false,
                  "imageFill": true,
                  "playAudio": false,
                  "layout": "single",
                  "language": "de"
                }
                """);

            Assert.That(JsonNode.DeepEquals(actual, expected), Is.True,
                () => $"Expected: {expected?.ToJsonString()}\nActual: {actual?.ToJsonString()}");
        }

        [Test]
        public async Task GetConfig_ContainsNoSecrets()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/api/Config");

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Not.Contain("secret-auth-token"));
                Assert.That(json, Does.Not.Contain("secret-weather-key"));
                Assert.That(json, Does.Not.Contain("secret-api-key"));
                Assert.That(json, Does.Not.Contain("secret-hook"));
                Assert.That(json, Does.Not.Contain("secret.ics"));
                Assert.That(json, Does.Not.Contain("authenticationSecret"));
                Assert.That(json, Does.Not.Contain("weatherApiKey"));
                Assert.That(json, Does.Not.Contain("apiKey"));
                Assert.That(json, Does.Not.Contain("webhook"));
                Assert.That(json, Does.Not.Contain("webcalendars"));
            });
        }
    }
}
