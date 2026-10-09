using System.Net;
using ImmichFrame.Core.Api;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Api;

[TestFixture]
public class ImmichApiTests
{
    private Mock<HttpMessageHandler> _immich;
    private List<Uri> _requested;

    [SetUp]
    public void Setup()
    {
        _requested = [];

        // One answer for both endpoints: a server version is valid JSON for the one, and any 200 is a
        // playable body for the other.
        _immich = new Mock<HttpMessageHandler>();
        _immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => _requested.Add(request.RequestUri!))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "major": 1, "minor": 131, "patch": 0, "prerelease": null }""")
            });
    }

    // A trailing slash is an ordinary way to write a server URL, and "//api/..." is a 404 from Immich
    // itself - only a reverse proxy that merges slashes, as nginx does by default, hides it.
    [TestCase("https://immich.example.com", "/api/server/version")]
    [TestCase("https://immich.example.com/", "/api/server/version")]
    [TestCase("https://example.com/immich/", "/immich/api/server/version")]
    public async Task GetServerVersionAsync_AnyServerUrl_RequestsTheApiWithSingleSlashes(string serverUrl, string expectedPath)
    {
        // Arrange
        using var client = new HttpClient(_immich.Object);
        var api = new ImmichApi(serverUrl, client);

        // Act
        await api.GetServerVersionAsync();

        // Assert
        Assert.That(_requested.Single().AbsolutePath, Is.EqualTo(expectedPath));
    }

    [TestCase("https://immich.example.com", "/api/assets/{0}/video/playback")]
    [TestCase("https://immich.example.com/", "/api/assets/{0}/video/playback")]
    [TestCase("https://example.com/immich/", "/immich/api/assets/{0}/video/playback")]
    public async Task PlayAssetVideoWithRangeAsync_AnyServerUrl_RequestsTheApiWithSingleSlashes(string serverUrl, string expectedPath)
    {
        // Arrange
        var id = Guid.NewGuid();
        using var client = new HttpClient(_immich.Object);
        var api = new ImmichApi(serverUrl, client);

        // Act
        using var video = await api.PlayAssetVideoWithRangeAsync(id, "bytes=0-");

        // Assert
        Assert.That(_requested.Single().AbsolutePath, Is.EqualTo(string.Format(expectedPath, id)));
    }

    [Test]
    public void PlayAssetVideoWithRangeAsync_ErrorStatus_ThrowsAndDisposesTheResponse()
    {
        // Arrange
        var body = new TrackedBody();
        ImmichAnswers(HttpStatusCode.InternalServerError, body);
        using var client = new HttpClient(_immich.Object);
        var api = new ImmichApi("https://immich.example.com", client);

        // Act
        var thrown = Assert.ThrowsAsync<ApiException>(() => api.PlayAssetVideoWithRangeAsync(Guid.NewGuid(), "bytes=0-"));

        // Assert - no FileResponse is handed back to dispose the response, so it is let go of here
        Assert.Multiple(() =>
        {
            Assert.That(thrown.StatusCode, Is.EqualTo(500));
            Assert.That(body.Disposed, Is.True);
        });
    }

    /// <summary>
    /// A body can fail as it is read - the connection dropped partway through it. Whatever the
    /// status, nothing is handed back to dispose the response then either, and the response was
    /// left holding its connection until the garbage collector came for it.
    /// </summary>
    [TestCase(HttpStatusCode.OK)]
    [TestCase(HttpStatusCode.PartialContent)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public void PlayAssetVideoWithRangeAsync_BodyCannotBeRead_DisposesTheResponse(HttpStatusCode status)
    {
        // Arrange
        var body = new TrackedBody(readFailure: new HttpRequestException("The response ended prematurely."));
        ImmichAnswers(status, body);
        using var client = new HttpClient(_immich.Object);
        var api = new ImmichApi("https://immich.example.com", client);

        // Act
        Assert.ThrowsAsync<HttpRequestException>(() => api.PlayAssetVideoWithRangeAsync(Guid.NewGuid(), "bytes=0-"));

        // Assert
        Assert.That(body.Disposed, Is.True);
    }

    private void ImmichAnswers(HttpStatusCode status, HttpContent body) =>
        _immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status) { Content = body });

    /// <summary>
    /// A response body that records being disposed, which disposing the response it came in does,
    /// and that can fail as it is read.
    /// </summary>
    private sealed class TrackedBody(Exception? readFailure = null) : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            readFailure is null ? stream.WriteAsync("Internal Server Error"u8.ToArray()).AsTask() : Task.FromException(readFailure);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
