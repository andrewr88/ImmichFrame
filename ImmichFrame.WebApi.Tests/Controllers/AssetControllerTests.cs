using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;
using ImmichFrame.Core.Api;
using ImmichFrame.WebApi.Controllers;
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

            // Account selection asks for the match count before the random pick.
            var statsJson = JsonSerializer.Serialize(new SearchStatisticsResponseDto { Total = 1 });
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/search/statistics")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(statsJson)
                });

            // Build the mock response from the generated DTOs so the test fails at
            // compile time (not at runtime) if the OpenAPI schema changes.
            var randomJson = JsonSerializer.Serialize(new[] { BuildAssetResponse(expectedAssetId) });
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/search/random")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage // Use a Func to return new instance each time
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(randomJson)
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

            // Assert - the very bytes the mock server sent, in both answers. Merely having content
            // would be met just as well by an image that never came from Immich.
            response.EnsureSuccessStatusCode();
            repeatResponse.EnsureSuccessStatusCode();
            var image = await response.Content.ReadFromJsonAsync<ImageResponse>();
            var repeatImage = await repeatResponse.Content.ReadFromJsonAsync<ImageResponse>();

            Assert.Multiple(() =>
            {
                Assert.That(image?.RandomImageBase64, Is.EqualTo(Convert.ToBase64String(mockImageData)));
                Assert.That(repeatImage?.RandomImageBase64, Is.EqualTo(Convert.ToBase64String(mockImageData)));
            });
        }

        /// <summary>
        /// An asset's type reaches the web client spelled the way the client compares it: "image",
        /// the camel-cased name its generated API client was built from, not the "IMAGE" Immich sends.
        /// The client keeps only the assets it recognises as an image or a video, so spelled any other
        /// way, every asset is dropped and the frame has nothing to show.
        /// </summary>
        [Test]
        public async Task GetAssets_AnImage_ReachesTheClientWithItsTypeSpelledAsTheClientComparesIt()
        {
            // Arrange
            var id = Guid.NewGuid();
            HoldAsset(BuildAssetResponse(id));
            var client = _factory.CreateClient();

            // Act
            var response = await client.GetAsync("/api/Asset");
            var assets = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That((string?)assets.Single()?["id"], Is.EqualTo(id.ToString()));
                Assert.That((string?)assets.Single()?["type"], Is.EqualTo("image"));
            });
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

        // The first bytes of an MP4's ftyp box, padded out to a kilobyte so that a range can begin well
        // inside it. Content only has to survive the trip, not play.
        private static readonly byte[] VideoBytes = [0x00, 0x00, 0x00, 0x20, .. new byte[1020]];

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
        /// The webhook tells an automation an asset went up on a frame, so it is notified once each time
        /// one does. A video goes up through a browser's &lt;video&gt;, which asks for a range from its
        /// very first request - Chromium and Firefox for bytes=0-, and WebKit for bytes=0-1 to probe it
        /// before asking for bytes=0-N - and then goes on asking range after range, every one of them a
        /// request to this action. So the request that puts an asset up is one that asks for no range,
        /// as an image is asked for, or for a single range from the first byte that is not that probe;
        /// any other range is a player asking for more of a video already up, and notifying for each
        /// would tell the automation the video was shown as many times as a piece of it was asked for.
        /// Each request under test is followed by a whole fetch, and told apart from it by client
        /// identifier, since both name the same asset.
        /// </summary>
        [TestCase(null, 1)]
        [TestCase("bytes=0-", 1)]
        [TestCase("bytes=0-999", 1)]
        [TestCase("bytes=0-1", 0)]
        [TestCase("bytes=500-", 0)]
        public async Task GetAsset_NotifiesTheWebhookOncePerShowingAndNeverForTheRestOfAVideo(string? range, int notified)
        {
            // Arrange
            var notifications = ReceiveWebhookNotifications();
            var videoAssetId = Guid.NewGuid();
            ServeVideo(videoAssetId);
            var client = _factory.CreateClient();
            await GetSlideshowContaining(client, videoAssetId);

            using var tested = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Asset/{videoAssetId}/Asset?assetType=video&clientIdentifier=Tested");
            if (range is not null)
                tested.Headers.TryAddWithoutValidation("Range", range);

            // Act - the request under test first, so that by the time the whole fetch's notification has
            // arrived, one sent for the request under test would have been sent before it.
            (await client.SendAsync(tested)).EnsureSuccessStatusCode();
            (await client.GetAsync($"/api/Asset/{videoAssetId}/Asset?assetType=video&clientIdentifier=Whole"))
                .EnsureSuccessStatusCode();

            // Sent and forgotten by the action, so waited for rather than assumed to have arrived.
            using (var waited = new CancellationTokenSource(WaitLimit))
            {
                while (!notifications.Any(notification => notification.ClientIdentifier == "Whole"))
                    await Task.Delay(10, waited.Token);
            }

            // Assert
            var asset = videoAssetId.ToString();
            Assert.That(notifications, Is.EqualTo(
                Enumerable.Repeat((ClientIdentifier: "Tested", AssetId: asset), notified)
                    .Append((ClientIdentifier: "Whole", AssetId: asset))));
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
        /// <para>
        /// The problem details say so in a sentence of the filter's own. The exception's message is
        /// written for the log, and the tracker's says nothing at all.
        /// </para>
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
            var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That((int?)problem?["status"], Is.EqualTo((int)HttpStatusCode.NotFound));
                Assert.That((string?)problem?["detail"], Is.EqualTo("That asset was not found on any of this frame's accounts."));
            });
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
        /// A video played back the way Immich plays one: whole, as the 206 it answers a range with - a
        /// range left open, or running past the end, being the rest of the video - or as a 416 for a
        /// range that starts past its end.
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
            if (range is { From: { } from })
            {
                var to = Math.Min(range.To ?? long.MaxValue, VideoBytes.Length - 1);
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
        /// <paramref name="asset"/> alone: counted as one, and drawn whenever the account draws.
        /// </summary>
        private void HoldAsset(AssetResponseDto asset)
        {
            var statistics = JsonSerializer.Serialize(new SearchStatisticsResponseDto { Total = 1 });
            var drawn = JsonSerializer.Serialize(new[] { asset });

            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/search/statistics")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(statistics)
                });

            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/search/random")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(drawn)
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
