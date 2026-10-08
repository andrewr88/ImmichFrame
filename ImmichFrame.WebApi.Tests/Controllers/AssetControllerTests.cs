using System;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;
using ImmichFrame.Core.Api;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using ImmichFrame.Core.Interfaces; // Added this back
using System.Text.Json;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers
{
    [TestFixture]
    public class AssetControllerTests
    {
        private WebApplicationFactory<Program> _factory;
        private Mock<HttpMessageHandler> _mockHttpMessageHandler;
        private ServerAccountSettings _accountSettings;

        [SetUp]
        public void Setup()
        {
            _mockHttpMessageHandler = new Mock<HttpMessageHandler>().WithServerVersion();

            // Held by the fixture rather than built with the host, so that a test can change what the
            // account shows before its first request builds the profile's services from it.
            _accountSettings = new ServerAccountSettings
            {
                ImmichServerUrl = "http://mock-immich-server.com",
                ApiKey = "test-api-key",
                ShowMemories = false,
                ShowFavorites = true,
                ShowArchived = false,
                Albums = new List<Guid>(),
                ExcludedAlbums = new List<Guid>(),
                People = new List<Guid>()
            };

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.UseMockHandler(_mockHttpMessageHandler);

                        // Directly instantiate and register settings objects
                        var generalSettings = new GeneralSettings
                        {
                            ShowWeatherDescription = false, // Assuming this corresponds to ShowWeather
                            WeatherIconUrl = "https://openweathermap.org/img/wn/{IconId}.png",
                            ShowClock = true,
                            ClockFormat = "HH:mm",
                            ClockDateFormat = "eee, MMM d",
                            Language = "en",
                            PhotoDateFormat = "MM/dd/yyyy", // Crucial for the NRE
                            ImageLocationFormat = "City,State,Country",
                            DownloadImages = false,
                            RenewImagesDuration = 30,
                            // Ensure all non-nullable string properties that might be used have defaults if not set here
                            PrimaryColor = "#FFFFFF", // Example default
                            SecondaryColor = "#000000", // Example default
                            Style = "none",
                            BaseFontSize = "16px",
                            WeatherApiKey = "",
                            UnitSystem = "imperial",
                            WeatherLatLong = "0,0"
                        };

                        var serverSettings = new ServerSettings
                        {
                            GeneralSettingsImpl = generalSettings,
                            AccountsImpl = new List<ServerAccountSettings> { _accountSettings }
                        };

                        // Seeding the catalog rather than IServerSettings directly: the per-profile
                        // services are built from the catalog, so settings registered around it
                        // would not reach the asset logic.
                        services.AddSingleton<IConfigCatalog>(new ConfigCatalog(serverSettings));
                    });
                });
        }

        // Removed OneTimeSetup that created Settings.json

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
        }

        [Test]
        public async Task GetRandomImage_ReturnsImageFromMockServer()
        {
            // Arrange
            var expectedAssetId = Guid.NewGuid();

            // Build the mock response from the generated DTOs so the test fails at
            // compile time (not at runtime) if the OpenAPI schema changes.
            var searchResponse = new SearchResponseDto
            {
                Assets = new SearchAssetResponseDto
                {
                    Count = 1,
                    Total = 1,
                    Items = { BuildAssetResponse(expectedAssetId) },
                    NextPage = null,
                },
                // Albums is already initialised to an empty SearchAlbumResponseDto.
            };

            var jsonResponse = JsonSerializer.Serialize(searchResponse);

            // Setup for SearchAssetsAsync
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/search/metadata")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage // Use a Func to return new instance each time
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(jsonResponse)
                });

            // Setup for ViewAssetAsync (thumbnail)
            var mockImageData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // Minimal JPEG
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/thumbnail")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new ByteArrayContent(mockImageData)
                });

            var client = _factory.CreateClient();

            // Act
            var response = await client.GetAsync("/api/Asset/RandomImageAndInfo");

            // A second request on the same profile, deliberately: the profile's pools and API caches
            // are shared by every request on it, so anything that ties their lifetime to one
            // request's DI scope serves the first request and fails every one after it.
            var repeatResponse = await client.GetAsync("/api/Asset/RandomImageAndInfo");

            // Assert
            response.EnsureSuccessStatusCode();
            repeatResponse.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            // For now, we'll just check if the response is not empty.
            // A more robust check would be to deserialize the response and check the asset ID.
            Assert.That(content, Is.Not.Empty);
        }

        // Builds a valid AssetResponseDto from the generated client types. Only the
        // fields required by the schema are set; pass type/duration to reuse this for
        // video assets. Changes to the OpenAPI schema surface here as compile errors.
        private static AssetResponseDto BuildAssetResponse(
            Guid id,
            AssetTypeEnum type = AssetTypeEnum.IMAGE,
            int? duration = null)
        {
            var timestamp = DateTimeOffset.Parse("2023-10-26T10:00:00Z");
            return new AssetResponseDto
            {
                Id = id,
                OwnerId = Guid.NewGuid(),
                Type = type,
                OriginalPath = "/path/to/image.jpg",
                OriginalFileName = "image.jpg",
                Checksum = "testchecksum",
                Thumbhash = "I0cMCQS94XmImZeXmYd3d3g=",
                Visibility = AssetVisibility.Timeline,
                Duration = duration,
                Width = 1920,
                Height = 1080,
                IsFavorite = true,
                HasMetadata = true,
                FileCreatedAt = timestamp,
                FileModifiedAt = timestamp,
                LocalDateTime = timestamp,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
            };
        }

        // The first bytes of an MP4's ftyp box. Content only has to survive the trip, not play.
        private static readonly byte[] VideoBytes = [0x00, 0x00, 0x00, 0x20];

        /// <summary>
        /// A video asked for the way the web client's player asks for one: by id, with its type named
        /// so that no asset info is fetched first. It has to have been in a slideshow the profile served
        /// - which account holds an asset is looked up among the assets the accounts have handed out,
        /// and an id never handed out is one no account is found for - so the slideshow comes first.
        /// </summary>
        [Test]
        public async Task GetAsset_VideoAsset_ReturnsVideoStream()
        {
            // Arrange
            var videoAssetId = Guid.NewGuid();
            ServeVideo(videoAssetId);
            var client = _factory.CreateClient();
            await GetSlideshowContaining(client, videoAssetId);

            // Act
            var response = await client.GetAsync($"/api/Asset/{videoAssetId}/Asset?assetType=video");
            var body = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("video/quicktime"),
                    "Immich's content type, rather than the video/mp4 assumed when it sends none");
                Assert.That(body, Is.EqualTo(VideoBytes));
            });
        }

        /// <summary>
        /// The same video asked for a byte range, as a browser asks while it plays one. The range has to
        /// reach Immich, and Immich's partial answer come back as it stands: ASP.NET can serve a range
        /// out of a stream it can seek on its own, which a test's in-memory response is, so the status
        /// alone would pass with the range never forwarded. The Content-Range tells them apart - Immich
        /// counts the whole video, where a range served out of its two-byte answer would count two.
        /// </summary>
        [Test]
        public async Task GetAsset_VideoAssetWithARange_ReturnsImmichsPartialContent()
        {
            // Arrange
            var videoAssetId = Guid.NewGuid();
            var forwardedRanges = ServeVideo(videoAssetId);
            var client = _factory.CreateClient();
            await GetSlideshowContaining(client, videoAssetId);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Asset/{videoAssetId}/Asset?assetType=video");
            request.Headers.Range = new RangeHeaderValue(0, 1);

            // Act
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(forwardedRanges, Is.EqualTo(new[] { "bytes=0-1" }), "the range Immich was asked for");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                Assert.That(response.Content.Headers.ContentRange?.ToString(), Is.EqualTo($"bytes 0-1/{VideoBytes.Length}"));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("video/quicktime"));
                Assert.That(body, Is.EqualTo(VideoBytes[..2]));
            });
        }

        /// <summary>
        /// Has the mocked Immich hold one video among the account's favourites, which is what this
        /// fixture's account searches, and play it back - whole, or as the 206 Immich answers a range
        /// with. Returns the Range header of every playback request that reached it.
        /// </summary>
        private List<string?> ServeVideo(Guid id)
        {
            // An account's videos are left out of its slideshow unless it shows them.
            _accountSettings.ShowVideos = true;

            var searchResponse = JsonSerializer.Serialize(new SearchResponseDto
            {
                Assets = new SearchAssetResponseDto
                {
                    Count = 1,
                    Total = 1,
                    Items = { BuildAssetResponse(id, AssetTypeEnum.VIDEO, duration: 5000) },
                    NextPage = null,
                },
            });

            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/search/metadata")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(searchResponse)
                });

            var forwardedRanges = new List<string?>();

            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains($"/assets/{id}/video/playback")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
                {
                    forwardedRanges.Add(request.Headers.Range?.ToString());

                    HttpResponseMessage response;
                    if (request.Headers.Range?.Ranges.Single() is { From: { } from, To: { } to })
                    {
                        response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                        {
                            Content = new ByteArrayContent(VideoBytes[(int)from..((int)to + 1)])
                        };
                        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, VideoBytes.Length);
                    }
                    else
                    {
                        response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(VideoBytes) };
                    }

                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/quicktime");
                    return response;
                });

            return forwardedRanges;
        }

        /// <summary>
        /// Asks for the slideshow as a frame does, which is what records the account each asset in it
        /// came from.
        /// </summary>
        private static async Task GetSlideshowContaining(HttpClient client, Guid id)
        {
            var response = await client.GetAsync("/api/Asset");
            response.EnsureSuccessStatusCode();

            var served = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
                .Select(asset => (string?)asset?["id"]);

            Assert.That(served, Does.Contain(id.ToString()), "the slideshow has to have served the video first");
        }
    }
}
