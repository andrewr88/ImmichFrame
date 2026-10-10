using System.Net;
using System.Security.Cryptography;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Logic.AccountSelection;
using ImmichFrame.Core.Models;
using ImmichFrame.Core.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

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
    private Mock<IAccountSettings> _accountSettings;
    private Mock<HttpMessageHandler> _immich;
    private Mock<IHttpClientFactory> _httpClientFactory;
    private ListLogger<PooledImmichFrameLogic> _logger;
    private PooledImmichFrameLogic _sut;

    [SetUp]
    public async Task Setup()
    {
        // A cache of its own for every test, and one that does not exist yet, as on a fresh install.
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"ImmichFrame.Core.Tests.{Guid.NewGuid():N}");

        _generalSettings = new Mock<IGeneralSettings>();
        _generalSettings.SetupGet(s => s.DownloadImages).Returns(true);
        _generalSettings.SetupGet(s => s.RenewImagesDuration).Returns(30);

        _accountSettings = new Mock<IAccountSettings>();
        _accountSettings.SetupGet(s => s.ImmichServerUrl).Returns("http://immich.test");
        _accountSettings.SetupGet(s => s.ApiKey).Returns("api-key");

        // Immich is faked below the generated client, so the tests see the same response objects,
        // and the same need to dispose them, as a real server produces.
        _immich = new Mock<HttpMessageHandler>();
        _httpClientFactory = new Mock<IHttpClientFactory>();
        _httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_immich.Object));

        _logger = new ListLogger<PooledImmichFrameLogic>();
        _sut = await CreateLogic();
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        if (!Directory.Exists(_cacheDirectory)) return;

        // A read-only case may have taken the write bit off the directory itself.
        SetMode(_cacheDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_cacheDirectory, recursive: true);
    }

    // An account's logic over the test's image cache, unless given another. Returned once the sweep
    // it starts is over, so that no test has a sweep running through the files it arranges.
    private async Task<PooledImmichFrameLogic> CreateLogic(string? cacheDirectory = null)
    {
        var logic = new PooledImmichFrameLogic(_accountSettings.Object, _generalSettings.Object,
            _httpClientFactory.Object, _logger, cacheDirectory ?? _cacheDirectory);
        await logic.ImageCacheSweep.WaitAsync(WaitLimit);
        return logic;
    }

    private List<string> Warnings() =>
        _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();

    // A file in the test's cache, written as long ago as given. On Linux, which keeps no creation
    // time to set, setting one sets the last write time, which then stands in for it.
    private string CacheFile(string name, TimeSpan age)
    {
        var path = Path.Combine(_cacheDirectory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow - age);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    // Guarded rather than called directly: the file-mode APIs are unsupported on Windows, and an
    // unguarded call site is a CA1416 warning in an otherwise clean build. The tests that turn on what
    // the mode actually is carry [Platform("Unix")] as well.
    private static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(path, mode);
    }

    private void ImmichServes(Func<HttpResponseMessage> response) =>
        _immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

    private void VerifyImmichRequests(Times times) =>
        _immich.Protected().Verify("SendAsync", times, ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

    // Answers the requests to one of Immich's endpoints, named by the end of its path, with a JSON body.
    private void ImmichAnswers(string endpoint, string json) =>
        _immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith(endpoint)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

    private void VerifyImmichRequests(string endpoint, Times times) =>
        _immich.Protected().Verify("SendAsync", times,
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith(endpoint)),
            ItExpr.IsAny<CancellationToken>());

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
    /// A cache the frame cannot write to is no reason to fail the image it was to keep a copy of. In
    /// the shipped image the cache belonged beside the binary, in a directory only root may write to,
    /// so it could never be created and every image failed.
    /// </summary>
    [Test]
    // Windows has no mode bits for SetMode to take the write bit off with.
    [Platform("Unix")]
    public async Task GetAsset_CacheDirectoryCannotBeCreated_ServesTheImageFromImmich()
    {
        // Root writes through a directory's mode, so as root the cache is created all the same.
        Assume.That(Environment.IsPrivilegedProcess, Is.False, "running as root, whom a directory's mode does not stop");

        // Arrange - the cache belongs in a directory the frame may only read, as /app is in the image
        Directory.CreateDirectory(_cacheDirectory);
        SetMode(_cacheDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var cacheDirectory = Path.Combine(_cacheDirectory, "ImageCache");
        using var logic = await CreateLogic(cacheDirectory);
        ImmichServes(() => ImageResponse(new UpstreamBody(Image)));

        // Act
        var first = await ReadAll(await logic.GetAsset(_assetId, AssetTypeEnum.IMAGE));
        var second = await ReadAll(await logic.GetAsset(_assetId, AssetTypeEnum.IMAGE));

        // Assert - served both times, from one request to Immich each, and the operator told which
        // directory to fix once rather than for every image
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(Image));
            Assert.That(second, Is.EqualTo(Image));
            Assert.That(Warnings(), Has.Count.EqualTo(1));
            Assert.That(Warnings(), Has.All.Contains(cacheDirectory));
        });
        VerifyImmichRequests(Times.Exactly(2));
    }

    /// <summary>
    /// What the frame made of that: the tracker takes an account failing to serve an asset for a
    /// Bloom false positive, so every image came back as not found.
    /// </summary>
    [Test]
    [Platform("Unix")]
    public async Task GetAsset_ThroughTheTrackerWithACacheThatCannotBeCreated_ServesTheImage()
    {
        Assume.That(Environment.IsPrivilegedProcess, Is.False, "running as root, whom a directory's mode does not stop");

        // Arrange
        Directory.CreateDirectory(_cacheDirectory);
        SetMode(_cacheDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var logic = await CreateLogic(Path.Combine(_cacheDirectory, "ImageCache"));
        ImmichServes(() => ImageResponse(new UpstreamBody(Image)));
        // The tracker sizes the account's filter by its count: for an account narrowed to nothing,
        // this search
        ImmichAnswers("/search/statistics", """{"total":1}""");
        var tracker = new BloomFilterAssetAccountTracker(NullLogger<BloomFilterAssetAccountTracker>.Instance);
        await tracker.RecordAssetLocation(logic, _assetId);

        // Act
        var asset = await tracker.ForAsset(_assetId, account => account.GetAsset(_assetId, AssetTypeEnum.IMAGE));

        // Assert
        Assert.That(await ReadAll(asset), Is.EqualTo(Image));
    }

    /// <summary>
    /// The cache can fail once the image has been read out of Immich's response, too - a disk that
    /// fills part way, a name the download cannot be moved in under - and a response cannot be read
    /// twice. Immich is asked again and that answer served as it arrives, with nothing left behind.
    /// </summary>
    [Test]
    public async Task GetAsset_CacheFailsOnceTheImageIsDownloaded_ServesItFromASecondRequest()
    {
        // Arrange - a directory squats on the image's name in the cache, so the finished download
        // cannot be moved in under it: a failure after the whole response is read, even for root
        var squatter = Path.Combine(_cacheDirectory, $"{_assetId}.jpeg");
        Directory.CreateDirectory(squatter);
        ImmichServes(() => ImageResponse(new UpstreamBody(Image)));

        // Act
        var served = await ReadAll(await _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(served, Is.EqualTo(Image));
            Assert.That(Directory.GetFileSystemEntries(_cacheDirectory), Is.EqualTo(new[] { squatter }),
                "the download was left behind");
        });
        VerifyImmichRequests(Times.Exactly(2));
    }

    /// <summary>
    /// An expired image was deleted only when the same image was asked for again, which for a frame
    /// long moved on is never, and a crash part way through a download left its file behind for good.
    /// The sweep takes both - but no image that can still be served, nor a download under way.
    /// </summary>
    [Test]
    public void SweepImageCache_TakesExpiredImagesAndAbandonedDownloads_LeavesTheRest()
    {
        // Arrange - images are renewed after 30 days
        Directory.CreateDirectory(_cacheDirectory);
        CacheFile($"{Guid.NewGuid()}.jpeg", age: TimeSpan.FromDays(31));
        CacheFile($"{Guid.NewGuid()}.{Guid.NewGuid():N}.tmp", age: TimeSpan.FromDays(1));
        var fresh = CacheFile($"{Guid.NewGuid()}.webp", age: TimeSpan.FromDays(29));
        var underWay = CacheFile($"{Guid.NewGuid()}.{Guid.NewGuid():N}.tmp", age: TimeSpan.Zero);

        // Act
        _sut.SweepImageCache();

        // Assert
        Assert.That(Directory.GetFiles(_cacheDirectory), Is.EquivalentTo(new[] { fresh, underWay }));
    }

    [Test]
    public async Task Constructor_DownloadImagesOn_SweepsTheImageCache()
    {
        // Arrange
        Directory.CreateDirectory(_cacheDirectory);
        var expired = CacheFile($"{Guid.NewGuid()}.jpeg", age: TimeSpan.FromDays(31));

        // Act - returned once the sweep the logic starts is over
        using var logic = await CreateLogic();

        // Assert
        Assert.That(File.Exists(expired), Is.False);
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
    /// Stored settings are run even when they fail validation, so a RefreshAlbumPeopleInterval far
    /// beyond the bound a save is held to still reaches the account. More hours than a TimeSpan holds
    /// failed the account as it was built; more than the cache can set an expiry for failed every
    /// request for its photos, when the first load was committed. Both are cached for the longest
    /// interval a save allows instead - cached, as so long an interval means, rather than not at all.
    /// </summary>
    [TestCase(100_000_000, TestName = "GetAssets_RefreshIntervalPastTheLatestExpiryTheCacheCanSet_ServesAndCaches")]
    [TestCase(int.MaxValue, TestName = "GetAssets_RefreshIntervalLongerThanATimeSpanHolds_ServesAndCaches")]
    public async Task GetAssets_RefreshIntervalTooLongToCacheFor_ServesAndCaches(int hours)
    {
        // Arrange - an account with nothing narrowed, so its count is a cached statistics search
        _generalSettings.SetupGet(s => s.RefreshAlbumPeopleInterval).Returns(hours);
        ImmichAnswers("/search/random", "[]");
        ImmichAnswers("/search/statistics", """{"total":7}""");

        // Act - the counts further apart than the millisecond an interval of 0 caches for, so the
        // second is only the cache's when the interval was kept long
        using var logic = await CreateLogic();
        var assets = await logic.GetAssets();
        var firstCount = await logic.GetTotalAssets();
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        long[] counts = [firstCount, await logic.GetTotalAssets()];

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(assets, Is.Empty);
            Assert.That(counts, Is.EqualTo(new long[] { 7, 7 }));
        });
        VerifyImmichRequests("/search/statistics", Times.Once());
    }

    // The one tag ImmichFindsNothing's Immich has.
    private static readonly Guid HolidayTagId = Guid.NewGuid();

    // Answers every search with nothing found - and the tags with Holiday alone - and keeps each request
    // as its method, path and body: read as it is sent, since the client disposes a request once it is
    // answered.
    private List<string> ImmichFindsNothing()
    {
        const string noAssets = """
            {"albums":{"count":0,"facets":[],"items":[],"total":0},
             "assets":{"count":0,"facets":[],"items":[],"nextCursor":null,"nextPage":null,"total":0}}
            """;
        var tags = $$"""
            [{"id":"{{HolidayTagId}}","name":"Holiday","value":"Holiday",
              "createdAt":"2024-01-01T00:00:00Z","updatedAt":"2024-01-01T00:00:00Z"}]
            """;
        var requests = new List<string>();
        _immich.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage request, CancellationToken cancellationToken) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (requests) requests.Add($"{request.Method} {path} {body}");

                var json = path.EndsWith("/search/metadata") ? noAssets
                    : path.EndsWith("/search/statistics") ? """{"total":0}"""
                    : path.EndsWith("/tags") ? tags
                    : "[]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            });
        return requests;
    }

    /// <summary>
    /// An account showing one kind of asset searches for that kind alone: the one branch of the
    /// search's "or", as it goes to Immich. With no "or", it would draw at random from its whole
    /// library; with another branch, or one Immich does not accept, it would show something else, or
    /// nothing at all.
    /// </summary>
    [TestCase("Favorites")]
    [TestCase("Albums")]
    [TestCase("People")]
    [TestCase("Tags")]
    public async Task GetAssets_AccountShowingOneKindOnly_SearchesForThatKindAlone(string kind)
    {
        // Arrange - the account's pool is built along with its logic
        var id = Guid.NewGuid();
        string branch;
        switch (kind)
        {
            case "Favorites":
                _accountSettings.SetupGet(s => s.ShowFavorites).Returns(true);
                branch = """{"isFavorite":{"eq":true}}""";
                break;
            case "Albums":
                _accountSettings.SetupGet(s => s.Albums).Returns([id]);
                branch = $$$"""{"albumIds":{"any":["{{{id}}}"]}}""";
                break;
            case "People":
                _accountSettings.SetupGet(s => s.People).Returns([id]);
                branch = $$$"""{"personIds":{"any":["{{{id}}}"]}}""";
                break;
            default:
                _accountSettings.SetupGet(s => s.Tags).Returns(["Holiday"]);
                branch = $$$"""{"tagIds":{"any":["{{{HolidayTagId}}}"]}}""";
                break;
        }

        var requests = ImmichFindsNothing();
        using var logic = await CreateLogic();

        // Act
        await logic.GetAssets();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(requests, Has.Some.Matches<string>(r => r.StartsWith("POST /api/search/random") && r.Contains($"\"or\":[{branch}]")));
            Assert.That(requests, Has.None.Contains("/memories"));
        });
    }

    /// <summary>
    /// An account showing nothing but its memories draws on them alone. Built with the library search
    /// beside them, it would show its whole library as well.
    /// </summary>
    [Test]
    public async Task GetAssets_AccountShowingMemoriesOnly_DoesNotSearchTheLibrary()
    {
        // Arrange
        _accountSettings.SetupGet(s => s.ShowMemories).Returns(true);
        var requests = ImmichFindsNothing();
        using var logic = await CreateLogic();

        // Act
        await logic.GetAssets();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(requests, Has.Some.StartsWith("GET /api/memories"));
            Assert.That(requests, Has.None.Contains("/search/"));
        });
    }

    /// <summary>
    /// An account showing its memories and another kind of asset draws on both, each weighed by its
    /// count. Built with the memories alone, it would never show the other kind.
    /// </summary>
    [Test]
    public async Task GetAssets_AccountShowingMemoriesAndFavorites_CountsBoth()
    {
        // Arrange
        _accountSettings.SetupGet(s => s.ShowMemories).Returns(true);
        _accountSettings.SetupGet(s => s.ShowFavorites).Returns(true);
        var requests = ImmichFindsNothing();
        using var logic = await CreateLogic();

        // Act
        await logic.GetAssets();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(requests, Has.Some.StartsWith("GET /api/memories"));
            Assert.That(requests, Has.Some.Matches<string>(r =>
                r.StartsWith("POST /api/search/statistics") && r.Contains("""
                    "or":[{"isFavorite":{"eq":true}}]
                    """)));
        });
    }

    /// <summary>
    /// Trashed photos are left out by asking for the ones whose trashedAt is null, and that null has to
    /// reach Immich. The client generated from Immich's spec leaves a null out of the request, which
    /// sends a filter with nothing in it and leaves the trash in the slideshow.
    /// </summary>
    [Test]
    public async Task GetAssets_LibrarySearch_AsksImmichForWhatIsNotInTheTrash()
    {
        // Arrange - an account narrowed to nothing
        var requests = ImmichFindsNothing();
        using var logic = await CreateLogic();

        // Act
        await logic.GetAssets();

        // Assert
        Assert.That(requests, Has.Some.Matches<string>(r =>
            r.StartsWith("POST /api/search/random") && r.Contains("""
                "trashedAt":{"eq":null}
                """)));
    }

    /// <summary>
    /// Only images and videos are shown. Anything else is not found, rather than streamed from the
    /// endpoint that plays videos.
    /// </summary>
    [TestCase(AssetTypeEnum.AUDIO)]
    [TestCase(AssetTypeEnum.OTHER)]
    public void GetAsset_NeitherImageNorVideo_NotFoundWithoutAskingImmich(AssetTypeEnum assetType)
    {
        // Act / Assert
        Assert.That(async () => await _sut.GetAsset(_assetId, assetType), Throws.TypeOf<AssetNotFoundException>());
        VerifyImmichRequests(Times.Never());
    }

    /// <summary>
    /// The cache is read only while DownloadImages is on: an image cached while it was, however
    /// fresh, is not served once it has been turned off.
    /// </summary>
    [Test]
    public async Task GetAsset_CachedImageWithDownloadImagesOff_ServedFromImmich()
    {
        // Arrange
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllBytesAsync(Path.Combine(_cacheDirectory, $"{_assetId}.jpeg"), [1, 2, 3]);
        _generalSettings.SetupGet(s => s.DownloadImages).Returns(false);
        ImmichServes(() => ImageResponse(new UpstreamBody(Image)));

        // Act
        var served = await ReadAll(await _sut.GetAsset(_assetId, AssetTypeEnum.IMAGE));

        // Assert
        Assert.That(served, Is.EqualTo(Image));
        VerifyImmichRequests(Times.Once());
    }

    // A video as Immich streams it, with the headers given.
    private static HttpResponseMessage VideoResponse(long? contentLength = null, string? contentType = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[1234])) };
        if (contentLength.HasValue)
            response.Content.Headers.ContentLength = contentLength;
        if (contentType != null)
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return response;
    }

    // AssetController answers a range of a video with the length Immich gave it, which the stream the
    // range arrives on cannot tell.
    [Test]
    public async Task GetAsset_VideoOfKnownLength_PassesTheLengthOn()
    {
        // Arrange
        ImmichServes(() => VideoResponse(contentLength: 1234, contentType: "video/quicktime"));

        // Act
        var asset = await _sut.GetAsset(_assetId, AssetTypeEnum.VIDEO);
        using (asset.Owner)
        await using (asset.FileStream)
        {
            // Assert
            Assert.That(asset.ContentLength, Is.EqualTo(1234));
        }
    }

    [Test]
    public async Task GetAsset_VideoWithoutAContentType_ServedAsMp4()
    {
        // Arrange
        ImmichServes(() => VideoResponse());

        // Act
        var asset = await _sut.GetAsset(_assetId, AssetTypeEnum.VIDEO);
        using (asset.Owner)
        await using (asset.FileStream)
        {
            // Assert
            Assert.That(asset.ContentType, Is.EqualTo("video/mp4"));
        }
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
