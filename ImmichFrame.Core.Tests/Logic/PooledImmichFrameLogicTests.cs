using System.Net;
using System.Security.Cryptography;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Models;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Logic;

[TestFixture]
public class PooledImmichFrameLogicTests
{
    // Turns a hang into a failure rather than a stalled run.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static readonly byte[] Image = RandomNumberGenerator.GetBytes(64 * 1024);

    private readonly Guid _assetId = Guid.NewGuid();
    private string _cacheDirectory;
    private Mock<IGeneralSettings> _generalSettings;
    private Mock<HttpMessageHandler> _immich;
    private PooledImmichFrameLogic _sut;

    [SetUp]
    public void Setup()
    {
        // A cache of its own for every test, and one that does not exist yet, as on a fresh install.
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"ImmichFrame.Core.Tests.{Guid.NewGuid():N}");

        _generalSettings = new Mock<IGeneralSettings>();
        _generalSettings.SetupGet(s => s.DownloadImages).Returns(true);
        _generalSettings.SetupGet(s => s.RenewImagesDuration).Returns(30);

        var accountSettings = new Mock<IAccountSettings>();
        accountSettings.SetupGet(s => s.ImmichServerUrl).Returns("http://immich.test");
        accountSettings.SetupGet(s => s.ApiKey).Returns("api-key");

        // Immich is faked below the generated client, so the tests see the same response objects,
        // and the same need to dispose them, as a real server produces.
        _immich = new Mock<HttpMessageHandler>();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_immich.Object));

        _sut = new PooledImmichFrameLogic(accountSettings.Object, _generalSettings.Object, httpClientFactory.Object)
        {
            DownloadLocation = _cacheDirectory
        };
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        if (Directory.Exists(_cacheDirectory))
            Directory.Delete(_cacheDirectory, recursive: true);
    }

    private void ImmichServes(Func<HttpResponseMessage> response) =>
        _immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

    private void VerifyImmichRequests(Times times) =>
        _immich.Protected().Verify("SendAsync", times, ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

    private static HttpResponseMessage ImageResponse(Stream body, string contentType = "image/jpeg")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return response;
    }

    private static async Task<byte[]> ReadAll(AssetResponse asset)
    {
        await using var stream = asset.FileStream;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Test]
    public async Task GetAsset_DownloadFailsPartWay_NothingCachedAndNextRequestDownloadsAgain()
    {
        // Arrange - the first download drops 100 bytes in. A FileStream still holds that much in its
        // buffer, so a file written under the image's own name is left empty.
        var dropped = new UpstreamBody(Image, failAfter: 100);
        _immich.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(ImageResponse(dropped))
            .ReturnsAsync(ImageResponse(new UpstreamBody(Image)));

        // Act
        Assert.That(async () => await _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE), Throws.InstanceOf<IOException>());
        var cachedAfterFailure = Directory.GetFiles(_cacheDirectory);
        var retried = await ReadAll(await _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE));

        // Assert - nothing is left for a later request, or a restart, to serve as the image; the
        // failed response is let go of rather than keeping its connection; the retry asks Immich.
        Assert.Multiple(() =>
        {
            Assert.That(cachedAfterFailure, Is.Empty);
            Assert.That(dropped.IsDisposed, Is.True);
            Assert.That(retried, Is.EqualTo(Image));
        });
        VerifyImmichRequests(Times.Exactly(2));
    }

    [Test]
    public async Task GetAsset_SecondRequestWhileFirstStillDownloading_BothServeTheImage()
    {
        // Arrange - every download stalls once it has begun, until the test lets them finish, so
        // the second request arrives while the first is still writing the image into the cache.
        var begun = new SemaphoreSlim(0);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ImmichServes(() => ImageResponse(new UpstreamBody(Image, onFirstRead: async () =>
        {
            begun.Release();
            await finish.Task;
        })));

        // Act
        var first = _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE);
        await begun.WaitAsync().WaitAsync(WaitLimit);
        var second = _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE);
        // A second request that fails outright never begins a download to wait for.
        await Task.WhenAny(second, begun.WaitAsync()).WaitAsync(WaitLimit);
        finish.SetResult();
        var assets = await Task.WhenAll(first, second).WaitAsync(WaitLimit);

        // Assert - each request downloaded the image itself, alongside the other
        var images = await Task.WhenAll(assets.Select(ReadAll));
        Assert.That(images, Has.All.EqualTo(Image));
        VerifyImmichRequests(Times.Exactly(2));
    }

    [Test]
    public async Task GetAsset_ImageDownloadedIntoCache_ReleasesImmichResponse()
    {
        // Arrange
        var body = new UpstreamBody(Image);
        ImmichServes(() => ImageResponse(body));

        // Act
        var asset = await _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE);
        await asset.FileStream.DisposeAsync();

        // Assert - the image is served from the cache, so nothing reads the response after the
        // copy, and nothing else is in a position to dispose it.
        Assert.That(body.IsDisposed, Is.True);
    }

    [TestCase("image/webp; charset=x")]
    [TestCase("IMAGE/WEBP")]
    [SetCulture("tr-TR")]
    public async Task GetAsset_WebpImage_CachedAndServedAsWebp(string contentType)
    {
        // Arrange - a media type matches case-insensitively and whatever its parameters, and the
        // culture is Turkish, where "I" lower-cases to a dotless "ı".
        ImmichServes(() => ImageResponse(new UpstreamBody(Image), contentType));

        // Act
        var downloaded = await _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE);
        await downloaded.FileStream.DisposeAsync();
        var cached = await _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE);
        var cachedImage = await ReadAll(cached);

        // Assert - and the second request is answered from the cache
        Assert.Multiple(() =>
        {
            Assert.That(downloaded.FileName, Is.EqualTo($"{_assetId}.webp"));
            Assert.That(cached.FileName, Is.EqualTo($"{_assetId}.webp"));
            Assert.That(cached.ContentType, Is.EqualTo("image/webp"));
            Assert.That(cachedImage, Is.EqualTo(Image));
        });
        VerifyImmichRequests(Times.Once());
    }

    [Test]
    public async Task GetAsset_CachedImageExpired_ReplacedByFreshDownload()
    {
        // Arrange - with a renewal period of zero days, every cached image has expired
        _generalSettings.SetupGet(s => s.RenewImagesDuration).Returns(0);
        var cachedFile = Path.Combine(_cacheDirectory, $"{_assetId}.jpeg");
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllBytesAsync(cachedFile, [1, 2, 3]);
        ImmichServes(() => ImageResponse(new UpstreamBody(Image)));

        // Act
        var served = await ReadAll(await _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(served, Is.EqualTo(Image));
            Assert.That(File.ReadAllBytes(cachedFile), Is.EqualTo(Image));
        });
    }

    /// <summary>
    /// A frame that hangs up while Immich is still to answer - on a video it has already moved on
    /// from, say - takes its request to Immich with it, rather than leaving that to run its course
    /// for an answer nobody will read. Each request GetAsset can make: the image, the whole video, a
    /// range of it, and the asset's info when its type is not given.
    /// </summary>
    [TestCase(AssetTypeEnum.IMAGE, null)]
    [TestCase(AssetTypeEnum.VIDEO, null)]
    [TestCase(AssetTypeEnum.VIDEO, "bytes=0-")]
    [TestCase(null, null)]
    public async Task GetAsset_CallerGivesUpWhileImmichIsAnswering_CancelsTheRequestToImmich(
        AssetTypeEnum? assetType, string? range)
    {
        // Arrange - Immich answers nothing until the request to it is cancelled
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken cancellationToken) =>
            {
                asked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var caller = new CancellationTokenSource();

        // Act
        var asset = _sut.GetAsset(_assetId, assetType, range, caller.Token);
        await asked.Task.WaitAsync(WaitLimit);
        await caller.CancelAsync();

        // Assert
        Assert.That(async () => await asset.WaitAsync(WaitLimit), Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>
    /// An image body as Immich streams it, which can drop its connection after
    /// <paramref name="failAfter"/> bytes and run <paramref name="onFirstRead"/> before it hands out
    /// any. Records whether it was disposed: until it is, the response it belongs to is not
    /// released.
    /// </summary>
    private sealed class UpstreamBody(byte[] image, int failAfter = int.MaxValue, Func<Task>? onFirstRead = null) : Stream
    {
        private int _position;
        private bool _begun;

        public bool IsDisposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_begun)
            {
                _begun = true;
                if (onFirstRead != null)
                    await onFirstRead();
            }

            return Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= failAfter)
                throw new IOException("The connection to Immich was reset.");

            var count = Math.Min(buffer.Length, Math.Min(image.Length, failAfter) - _position);
            image.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
