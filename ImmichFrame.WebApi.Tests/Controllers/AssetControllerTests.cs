using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
        private GeneralSettings _generalSettings;

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

            // Held by the fixture for the same reason: a test can give the frame a webhook to notify.
            _generalSettings = new GeneralSettings
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

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.UseMockHandler(_mockHttpMessageHandler);

                        var serverSettings = new ServerSettings
                        {
                            GeneralSettingsImpl = _generalSettings,
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
        /// A range that starts past the end of the video, as a browser asks for one when the length it
        /// holds is stale. Immich answers 416, and so does the frame - not a 500, and not the 404 of an
        /// account lookup that took Immich's 416 for a false positive and went on to the next account.
        /// </summary>
        [Test]
        public async Task GetAsset_RangeImmichCannotSatisfy_IsRangeNotSatisfiable()
        {
            // Arrange
            var videoAssetId = Guid.NewGuid();
            var forwardedRanges = ServeVideo(videoAssetId);
            var client = _factory.CreateClient();
            await GetSlideshowContaining(client, videoAssetId);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Asset/{videoAssetId}/Asset?assetType=video");
            request.Headers.Range = new RangeHeaderValue(VideoBytes.Length, VideoBytes.Length + 9);

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(forwardedRanges, Is.EqualTo(new[] { $"bytes={VideoBytes.Length}-{VideoBytes.Length + 9}" }),
                    "Immich has to be the one that refused the range");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestedRangeNotSatisfiable));
            });
        }

        /// <summary>
        /// A player streams a video range after range, every one of them a request to this action, so
        /// notifying the webhook for each would tell an automation the video was shown as many times as
        /// a piece of it was asked for. A whole fetch notifies it once, and a ranged one never does.
        /// Told apart by client identifier, since both name the same asset.
        /// </summary>
        [Test]
        public async Task GetAsset_NotifiesTheWebhookOnceForAWholeFetchAndNeverForARange()
        {
            // Arrange
            var notifications = ReceiveWebhookNotifications();
            var videoAssetId = Guid.NewGuid();
            ServeVideo(videoAssetId);
            var client = _factory.CreateClient();
            await GetSlideshowContaining(client, videoAssetId);

            using var ranged = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Asset/{videoAssetId}/Asset?assetType=video&clientIdentifier=Ranged");
            ranged.Headers.Range = new RangeHeaderValue(0, 1);

            // Act - the range first, so that by the time the whole fetch's notification has arrived,
            // one sent for the range would have been sent before it.
            (await client.SendAsync(ranged)).EnsureSuccessStatusCode();
            (await client.GetAsync($"/api/Asset/{videoAssetId}/Asset?assetType=video&clientIdentifier=Whole"))
                .EnsureSuccessStatusCode();

            // Sent and forgotten by the action, so waited for rather than assumed to have arrived.
            using (var waited = new CancellationTokenSource(WaitLimit))
            {
                while (!notifications.Any(notification => notification.ClientIdentifier == "Whole"))
                    await Task.Delay(10, waited.Token);
            }

            // Assert
            Assert.That(notifications, Is.EqualTo(new[] { (ClientIdentifier: "Whole", AssetId: videoAssetId.ToString()) }));
        }

        /// <summary>
        /// A compromised or hostile Immich server answering a request that asked for no range with a
        /// 206, a Content-Range and an HTML page. Passed on as it stood, that was a partial response
        /// with no attachment disposition, labelled text/html, from the origin the frame and the admin
        /// editor are served from - script on that origin for whoever opened the asset's URL. A part
        /// nobody asked for is served as the whole, and a type no asset can be is served as bytes,
        /// with the browser told not to sniff them and the response sandboxed if opened as a page.
        /// </summary>
        [Test]
        public async Task GetAsset_WholeRequestAnsweredWithAPartialHtmlPage_IsServedNeitherPartialNorAsHtml()
        {
            // Arrange
            var videoAssetId = Guid.NewGuid();
            ServeVideo(videoAssetId, playback: _ => PartialHtmlPage());
            var client = _factory.CreateClient();
            await GetSlideshowContaining(client, videoAssetId);

            // Act
            var response = await client.GetAsync($"/api/Asset/{videoAssetId}/Asset?assetType=video");

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "no range was asked for");
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
                Assert.That(Header(response, "X-Content-Type-Options"), Is.EqualTo("nosniff"));
                Assert.That(Header(response, "Content-Security-Policy"), Is.EqualTo("sandbox"));
            });
        }

        /// <summary>
        /// The same page in answer to a range the browser did ask for. The 206 is genuine this time and
        /// is passed on as one, but the label is still not Immich's to choose.
        /// </summary>
        [Test]
        public async Task GetAsset_RangeAnsweredWithAnHtmlPage_IsServedPartialButNotAsHtml()
        {
            // Arrange
            var videoAssetId = Guid.NewGuid();
            ServeVideo(videoAssetId, playback: _ => PartialHtmlPage());
            var client = _factory.CreateClient();
            await GetSlideshowContaining(client, videoAssetId);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Asset/{videoAssetId}/Asset?assetType=video");
            request.Headers.Range = new RangeHeaderValue(0, 1);

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
                Assert.That(Header(response, "X-Content-Type-Options"), Is.EqualTo("nosniff"));
                Assert.That(Header(response, "Content-Security-Policy"), Is.EqualTo("sandbox"));
            });
        }

        /// <summary>
        /// An image keeps the type Immich declares for it only when that is a type a browser shows as
        /// an image and nothing more. SVG is not one: it is an image to an &lt;img&gt; and a document,
        /// script and all, to a browser that opens it. The declared type's case and parameters do not
        /// change what it is, to a browser or here.
        /// </summary>
        [TestCase("image/jpeg", "image/jpeg")]
        [TestCase("IMAGE/PNG; charset=binary", "image/png")]
        [TestCase("image/svg+xml", "application/octet-stream")]
        [TestCase("text/html", "application/octet-stream")]
        public async Task GetAsset_Image_IsServedAsAnImageTypeOrAsBytes(string declared, string served)
        {
            // Arrange
            var imageAssetId = Guid.NewGuid();
            ServeImage(imageAssetId, declared);
            var client = _factory.CreateClient();
            await GetSlideshowContaining(client, imageAssetId);

            // Act
            var response = await client.GetAsync($"/api/Asset/{imageAssetId}/Asset?assetType=image");
            var body = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(served));
                Assert.That(Header(response, "X-Content-Type-Options"), Is.EqualTo("nosniff"));
                Assert.That(Header(response, "Content-Security-Policy"), Is.EqualTo("sandbox"));
                Assert.That(body, Is.EqualTo(ImageBytes), "relabelled, not withheld");
            });
        }

        /// <summary>
        /// An asset id no account is found for is a 404 from every action that looks one up, where it
        /// was a 500. Which account holds an asset is looked up among the assets the accounts have
        /// handed out, and that lookup starts empty - after a restart, or a configuration save - while
        /// a frame still holds the ids of the slideshow it was shown before. A host that has served no
        /// slideshow is that case exactly, and a deterministic one: with nothing handed out there is no
        /// Bloom filter to ask, so none can answer a false positive.
        /// </summary>
        [TestCase("AssetInfo")]
        [TestCase("AssetFaces")]
        [TestCase("AlbumInfo")]
        [TestCase("Asset")]
        public async Task GetById_AssetNoAccountIsFoundFor_IsNotFound(string action)
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.GetAsync($"/api/Asset/{Guid.NewGuid()}/{action}");

            // Assert
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        // Turns a hang into a failure rather than a stalled run.
        private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

        private const string WebhookUrl = "http://webhook.example.com/frame";

        // Not an image at all, and labelled text/html: what a hostile server serves in place of one.
        private static readonly byte[] HtmlPage = Encoding.UTF8.GetBytes("<script>alert(document.domain)</script>");

        // The first bytes of a JPEG. As with the video, content only has to survive the trip.
        private static readonly byte[] ImageBytes = [0xFF, 0xD8, 0xFF, 0xE0];

        /// <summary>
        /// Has the mocked Immich hold one video among the account's favourites, which is what this
        /// fixture's account searches, and play it back - by default through <see cref="Playback"/>,
        /// otherwise as <paramref name="playback"/> answers. Returns the Range header of every playback
        /// request that reached it.
        /// </summary>
        private List<string?> ServeVideo(Guid id, Func<HttpRequestMessage, HttpResponseMessage>? playback = null)
        {
            // An account's videos are left out of its slideshow unless it shows them.
            _accountSettings.ShowVideos = true;
            HoldAsset(BuildAssetResponse(id, AssetTypeEnum.VIDEO, duration: 5000));

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
                    return (playback ?? Playback)(request);
                });

            return forwardedRanges;
        }

        /// <summary>
        /// A video played back the way Immich plays one: whole, as the 206 it answers a range with, or
        /// as a 416 for a range that starts past its end.
        /// </summary>
        private static HttpResponseMessage Playback(HttpRequestMessage request)
        {
            var range = request.Headers.Range?.Ranges.Single();

            if (range is { From: { } start } && start >= VideoBytes.Length)
            {
                var refused = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                refused.Content.Headers.ContentRange = new ContentRangeHeaderValue(VideoBytes.Length);
                return refused;
            }

            HttpResponseMessage response;
            if (range is { From: { } from, To: { } to })
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
        }

        /// <summary>
        /// A 206 with a Content-Range and an HTML page in it, whatever was asked for.
        /// </summary>
        private static HttpResponseMessage PartialHtmlPage()
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(HtmlPage) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, HtmlPage.Length - 1, HtmlPage.Length);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return response;
        }

        /// <summary>
        /// Has the mocked Immich hold one image among the account's favourites and serve its preview
        /// declared as <paramref name="contentType"/>, which is written as it is given, unvalidated.
        /// </summary>
        private void ServeImage(Guid id, string contentType)
        {
            HoldAsset(BuildAssetResponse(id));

            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains($"/assets/{id}/thumbnail")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageBytes) };
                    response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                    return response;
                });
        }

        /// <summary>
        /// Has the mocked Immich answer this fixture's account's search - its favourites - with
        /// <paramref name="asset"/> alone.
        /// </summary>
        private void HoldAsset(AssetResponseDto asset)
        {
            var searchResponse = JsonSerializer.Serialize(new SearchResponseDto
            {
                Assets = new SearchAssetResponseDto
                {
                    Count = 1,
                    Total = 1,
                    Items = { asset },
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
        }

        /// <summary>
        /// Gives the frame a webhook and returns every notification it is sent, in the order they
        /// arrive. The webhook's client goes out on the mocked handler like every other client the host
        /// creates, so the notifications are read off it here.
        /// </summary>
        private ConcurrentQueue<(string? ClientIdentifier, string? AssetId)> ReceiveWebhookNotifications()
        {
            _generalSettings.Webhook = WebhookUrl;
            var notifications = new ConcurrentQueue<(string? ClientIdentifier, string? AssetId)>();

            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString() == WebhookUrl),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage request, CancellationToken _) =>
                {
                    var notification = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                    notifications.Enqueue(((string?)notification?["ClientIdentifier"], (string?)notification?["RequestedAssetId"]));

                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                });

            return notifications;
        }

        private static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

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

            Assert.That(served, Does.Contain(id.ToString()), "the slideshow has to have served the asset first");
        }
    }
}
