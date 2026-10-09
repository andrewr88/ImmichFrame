using System.Net.Http.Headers;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using ImmichFrame.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Logic;

public class PooledImmichFrameLogic : IAccountImmichFrameLogic, IDisposable
{
    /// <summary>
    /// The named <c>HttpClient</c> the frame talks to its Immich servers on, here and in the startup
    /// version check. Configured in <c>Program.cs</c> so that the factory's request logging - every
    /// request header, once LOG_LEVEL is Trace - writes the <c>X-API-KEY</c> each request carries as
    /// <c>*</c>. Redacted rather than silenced: the request line and its timing are what an operator
    /// chasing a slow or failing server reads, and neither holds the key.
    /// </summary>
    public const string HttpClientName = "ImmichApiAccountClient";

    private readonly IGeneralSettings _generalSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PooledImmichFrameLogic> _logger;
    private readonly IApiCache _apiCache;
    private readonly IAssetPool _pool;
    private readonly ImmichApi _immichApi;

    // The only names an image is ever cached under: GetImageAsset saves a WebP preview as .webp
    // and anything else as .jpeg.
    private static readonly string[] CachedImageExtensions = ["jpeg", "webp"];

    /// <summary>
    /// How long a download's file can go unwritten before <see cref="SweepImageCache"/> takes it for
    /// one a crash cut short. A download under way writes to its file throughout, and the cache is
    /// shared - by every account, and through a configuration swap with the accounts on their way out
    /// - so a sweep cannot simply take every download it finds.
    /// </summary>
    private static readonly TimeSpan AbandonedDownloadAge = TimeSpan.FromHours(1);

    // Set once the image cache has failed; see ImageCacheFailed. An int, for Interlocked.
    private int _imageCacheFailed;

    public PooledImmichFrameLogic(IAccountSettings accountSettings, IGeneralSettings generalSettings,
        IHttpClientFactory httpClientFactory, ILogger<PooledImmichFrameLogic> logger)
        : this(accountSettings, generalSettings, httpClientFactory, logger,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache"))
    {
    }

    // The image cache's directory is a parameter only so that tests can give each run one of its own,
    // and a constructor's rather than an initializer's because the sweep started here reads it.
    internal PooledImmichFrameLogic(IAccountSettings accountSettings, IGeneralSettings generalSettings,
        IHttpClientFactory httpClientFactory, ILogger<PooledImmichFrameLogic> logger, string downloadLocation)
    {
        _generalSettings = generalSettings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        DownloadLocation = downloadLocation;

        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        AccountSettings = accountSettings;

        httpClient.UseApiKey(accountSettings.ApiKey);
        _immichApi = new ImmichApi(accountSettings.ImmichServerUrl, httpClient);

        _apiCache = new ApiCache(RefreshInterval(generalSettings.RefreshAlbumPeopleInterval));
        _pool = BuildPool(accountSettings);

        // In the background, since nothing needs to wait for it: an image the sweep has yet to reach
        // is judged by the same rule when it is asked for.
        if (generalSettings.DownloadImages)
            ImageCacheSweep = Task.Run(SweepImageCache);
    }

    internal string DownloadLocation { get; }

    // The sweep the constructor started, for tests to wait on.
    internal Task ImageCacheSweep { get; } = Task.CompletedTask;

    /// <summary>
    /// The longest the account's albums and people are cached, in hours: ten years, the bound a save
    /// holds RefreshAlbumPeopleInterval to (<c>ServerSettings.MaxRefreshAlbumPeopleInterval</c> in
    /// ImmichFrame.WebApi, which this project cannot reference). Repeated here because stored
    /// settings that fail validation are run all the same, so a longer interval still arrives - and
    /// more hours than a TimeSpan holds failed the account as it was built, and more than the cache
    /// can set an expiry for failed every request for its photos.
    /// </summary>
    private const int MaxRefreshIntervalHours = 87600;

    private static TimeSpan RefreshInterval(int hours)
        => hours > 0 ? TimeSpan.FromHours(Math.Min(hours, MaxRefreshIntervalHours)) : TimeSpan.FromMilliseconds(1);

    public IAccountSettings AccountSettings { get; }

    private IAssetPool BuildPool(IAccountSettings accountSettings)
    {
        var hasAlbums = accountSettings.Albums?.Count > 0;
        var hasPeople = accountSettings.People?.Count > 0;
        var hasTags = accountSettings.Tags?.Count > 0;

        if (!accountSettings.ShowFavorites && !accountSettings.ShowMemories && !hasAlbums && !hasPeople && !hasTags)
        {
            return new AllAssetsPool(_apiCache, _immichApi, accountSettings);
        }

        var pools = new List<IAssetPool>();

        if (accountSettings.ShowFavorites)
            pools.Add(new FavoriteAssetsPool(_apiCache, _immichApi, accountSettings));

        if (accountSettings.ShowMemories)
            pools.Add(new MemoryAssetsPool(_immichApi, accountSettings));

        if (hasAlbums)
            pools.Add(new AlbumAssetsPool(_apiCache, _immichApi, accountSettings));

        if (hasPeople)
            pools.Add(new PersonAssetsPool(_apiCache, _immichApi, accountSettings));

        if (hasTags)
            pools.Add(new TagAssetsPool(_apiCache, _immichApi, accountSettings));

        return new MultiAssetPool(pools);
    }

    public async Task<AssetResponseDto?> GetNextAsset()
    {
        return (await _pool.GetAssets(1)).FirstOrDefault();
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets()
    {
        return await _pool.GetAssets(25);
    }

    public async Task<AssetResponseDto> GetAssetInfoById(Guid assetId) => await _immichApi.GetAssetInfoAsync(assetId, null, null);

    public async Task<IEnumerable<AssetFaceResponseDto>> GetAssetFacesById(Guid assetId) => await _immichApi.GetFacesAsync(assetId);

    public async Task<IEnumerable<AlbumResponseDto>> GetAlbumInfoById(Guid assetId) => await _immichApi.GetAllAlbumsAsync(assetId, null, null, null, null);

    public async Task<long> GetTotalAssets() => await _pool.GetAssetCount();

    public async Task<AssetResponse> GetAsset(Guid id, AssetTypeEnum? assetType = null, string? rangeHeader = null, CancellationToken ct = default)
    {
        if (!assetType.HasValue)
        {
            var assetInfo = await _immichApi.GetAssetInfoAsync(id, null, null, ct);
            if (assetInfo == null)
                throw new AssetNotFoundException($"Assetinfo for asset '{id}' was not found!");
            assetType = assetInfo.Type;
        }

        if (assetType == AssetTypeEnum.IMAGE)
        {
            var (fileName, contentType, fileStream) = await GetImageAsset(id, ct);
            return new AssetResponse
            {
                FileName = fileName,
                ContentType = contentType,
                FileStream = fileStream,
                ContentRange = null,
                IsPartial = false,
                Owner = null,
                ContentLength = null
            };
        }

        if (assetType == AssetTypeEnum.VIDEO)
        {
            return await GetVideoAsset(id, rangeHeader, ct);
        }

        throw new AssetNotFoundException($"Asset {id} is not a supported media type ({assetType}).");
    }
    private async Task<(string fileName, string ContentType, Stream fileStream)> GetImageAsset(Guid id, CancellationToken ct)
    {
        if (CachingImages)
        {
            if (OpenCachedImage(id) is { } cached)
                return cached;

            if (await DownloadToCache(id, ct) is { } downloaded)
                return downloaded;
        }

        // Not caching - or the cache has just failed, perhaps after reading the image out of the
        // response it was given, which cannot be read twice. So Immich is asked here afresh.
        var (response, fileName, contentType) = await RequestImage(id, ct);
        return (fileName, contentType, response.Stream);
    }

    /// <summary>
    /// Asks Immich for an image's preview, and names it by its media type: a WebP preview .webp, and
    /// anything else .jpeg.
    /// </summary>
    private async Task<(FileResponse response, string fileName, string ContentType)> RequestImage(Guid id, CancellationToken ct)
    {
        var data = await _immichApi.ViewAssetAsync(null, id, string.Empty, AssetMediaSize.Preview, null, ct);

        if (data == null)
            throw new AssetNotFoundException($"Asset {id} was not found!");

        var contentType = "";
        if (data.Headers.ContainsKey("Content-Type"))
        {
            contentType = data.Headers["Content-Type"].FirstOrDefault() ?? "";
        }

        // Compared as a media type - ignoring case and parameters - and without ToLower, which in a
        // Turkish culture turns "IMAGE" into "ımage".
        var isWebp = MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            && string.Equals(mediaType.MediaType, "image/webp", StringComparison.OrdinalIgnoreCase);
        var ext = isWebp ? "webp" : "jpeg";

        return (data, $"{id}.{ext}", contentType);
    }

    private bool CachingImages => _generalSettings.DownloadImages && Volatile.Read(ref _imageCacheFailed) == 0;

    /// <summary>
    /// Stops caching this account's images, and says so - once. A cache is never worth failing an
    /// image for, and one the frame cannot write to - a directory it may not create, a disk that is
    /// full - fails every image alike, so after its first failure it is not tried again: each image
    /// would be another warning, and those it failed part way through a second download. They are
    /// served straight from Immich instead, as with DownloadImages off, until the account is built
    /// afresh.
    /// </summary>
    private void ImageCacheFailed(Exception e)
    {
        if (Interlocked.Exchange(ref _imageCacheFailed, 1) == 0)
        {
            _logger.LogWarning(e, "Cannot cache images in '{downloadLocation}', so {account} serves them straight from Immich until ImmichFrame restarts or its settings change",
                DownloadLocation, this);
        }
    }

    /// <summary>
    /// The cached copy of an image, unless there is none or it has expired. Only the names
    /// <see cref="GetImageAsset"/> caches an image under are tried, rather than listing a directory
    /// every account shares.
    /// </summary>
    private (string fileName, string ContentType, Stream fileStream)? OpenCachedImage(Guid id)
    {
        foreach (var ext in CachedImageExtensions)
        {
            var fileName = $"{id}.{ext}";
            var filePath = Path.Combine(DownloadLocation, fileName);

            FileStream fs;
            try
            {
                fs = OpenCachedFile(filePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Never cached, or expired and deleted by a concurrent request a moment ago - or not
                // to be read, which is no reason to fail the image either: it is downloaded as though
                // it never had been, and writing it is what finds out whether the cache can be used.
                continue;
            }

            // Judged by the file that was opened, not by its name: a concurrent download can move a
            // fresh copy in under that name at any time.
            if (!Expired(File.GetCreationTimeUtc(fs.SafeFileHandle)))
                return (fileName, $"image/{ext}", fs);

            // Deleting by name can also take a fresh copy a concurrent download has just moved in.
            // That costs the next request a download - not the one serving that copy, which keeps
            // its handle.
            fs.Dispose();
            TryDelete(filePath);
        }

        return null;
    }

    // One rule for the requests and the sweep alike.
    private bool Expired(DateTime createdUtc) =>
        _generalSettings.RenewImagesDuration <= (DateTime.UtcNow - createdUtc).Days;

    /// <summary>
    /// Downloads an image into the cache and returns the cached copy, open for reading - or nothing
    /// when the cache fails, for the image to be served straight from Immich instead. The download
    /// goes to a file of its own and is moved in under the image's name only once it is complete, so
    /// the cache never holds part of an image - not after a failed download, and not after a crash -
    /// and requests downloading the same image at once do not collide.
    /// </summary>
    private async Task<(string fileName, string ContentType, Stream fileStream)?> DownloadToCache(Guid id, CancellationToken ct)
    {
        var tempPath = Path.Combine(DownloadLocation, $"{id}.{Guid.NewGuid():N}.tmp");
        FileStream? cached = null;

        // Whether it is Immich being waited on rather than the cache. Immich failing leaves no image
        // to serve, and fails the request as it always has; only the cache's failures are fallen
        // back from.
        var awaitingImmich = false;
        try
        {
            // Before Immich is asked for the image, so that the likeliest failure - a cache the frame
            // may not write to - is found out with nothing yet downloaded.
            Directory.CreateDirectory(DownloadLocation);

            string fileName, contentType;
            await using (var download = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                awaitingImmich = true;
                FileResponse response;
                (response, fileName, contentType) = await RequestImage(id, ct);

                // The image is served from the cache, so the response is done with once the copy has
                // finished or failed, and nothing else is in a position to dispose it.
                using (response)
                {
                    // By hand rather than by CopyToAsync, which cannot say which side of it failed.
                    var buffer = new byte[81920];
                    while (true)
                    {
                        awaitingImmich = true;
                        var read = await response.Stream.ReadAsync(buffer, ct);
                        awaitingImmich = false;
                        if (read == 0) break;

                        await download.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }

                // Through to the disk before the image's name goes on it: renamed while its contents
                // were still only in memory, a power cut could leave that name on an empty file, to
                // be served as the image until it expired.
                download.Flush(flushToDisk: true);
            }

            // Opened before the move rather than after it: once under the image's name, the file can
            // be replaced or expired by a concurrent request at any time, but this handle stays on
            // the image just downloaded.
            cached = OpenCachedFile(tempPath);
            File.Move(tempPath, Path.Combine(DownloadLocation, fileName), overwrite: true);
            return (fileName, contentType, cached);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && !awaitingImmich)
        {
            cached?.Dispose();
            TryDelete(tempPath);
            ImageCacheFailed(e);
            return null;
        }
        catch
        {
            cached?.Dispose();
            TryDelete(tempPath);
            throw;
        }
    }

    // Sharing delete lets a concurrent request replace or expire a cached image while it is being
    // served; the reader keeps the image it opened.
    private static FileStream OpenCachedFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next sweep, rather than failing an image that can be served all the same.
        }
    }

    /// <summary>
    /// Deletes what the cache holds but will never serve: images that have expired, which were only
    /// deleted when the same image was asked for again - by a frame long moved on, never - and the
    /// files of downloads a crash cut short, which nothing deleted at all. Run in the background as
    /// the account is built, so it must not throw: nothing awaits it to see.
    /// </summary>
    internal void SweepImageCache()
    {
        try
        {
            var directory = new DirectoryInfo(DownloadLocation);
            if (!directory.Exists) return;

            foreach (var file in directory.EnumerateFiles())
            {
                var stale = file.Extension == ".tmp"
                    ? DateTime.UtcNow - file.LastWriteTimeUtc > AbandonedDownloadAge
                    : CachedImageExtensions.Contains(file.Extension.TrimStart('.')) && Expired(file.CreationTimeUtc);

                if (stale) TryDelete(file.FullName);
            }
        }
        catch (Exception e)
        {
            // Most likely a cache the frame cannot read - which the first image it downloads reports,
            // along with what matters about it: that nothing is being cached.
            _logger.LogDebug(e, "Could not sweep the image cache in '{downloadLocation}'", DownloadLocation);
        }
    }

    private async Task<AssetResponse> GetVideoAsset(Guid id, string? rangeHeader, CancellationToken cancellationToken)
    {
        var videoResponse = string.IsNullOrEmpty(rangeHeader)
            ? await _immichApi.PlayAssetVideoAsync(id, null, null, cancellationToken)
            : await _immichApi.PlayAssetVideoWithRangeAsync(id, rangeHeader, cancellationToken);

        var contentType = videoResponse.Headers.TryGetValue("Content-Type", out var ct)
            ? ct.FirstOrDefault() ?? "video/mp4"
            : "video/mp4";

        var contentRange = videoResponse.Headers.TryGetValue("Content-Range", out var cr)
            ? cr.FirstOrDefault()
            : null;

        long? contentLength = videoResponse.Headers.TryGetValue("Content-Length", out var cl)
            && long.TryParse(cl.FirstOrDefault(), out var clValue) ? clValue : null;

        return new AssetResponse
        {
            FileName = $"{id}.mp4",
            ContentType = contentType,
            FileStream = videoResponse.Stream,
            ContentRange = contentRange,
            // Partial only when a range was asked for. AssetController passes a partial response on as
            // it stands - its status, its Content-Range, none of the attachment disposition a whole one
            // gets - so taking the server's word for it let a server answering a whole request with a
            // 206 decide how that response was served. A part nobody asked for is served as the whole.
            IsPartial = videoResponse.StatusCode == 206 && !string.IsNullOrEmpty(rangeHeader),
            Owner = videoResponse,
            ContentLength = contentLength
        };
    }

    // Nothing calls this in production: MultiImmichFrameLogicDelegate sends a profile's webhook itself
    // rather than asking an account to. It goes through the same helper all the same, so a failure
    // here is logged rather than escaping unobserved.
    public async Task SendWebhookNotification(IWebhookNotification notification) =>
        await WebhookHelper.SendWebhookNotification(notification, _generalSettings.Webhook,
            _httpClientFactory.CreateClient(WebhookHelper.HttpClientName), _logger);

    public override string ToString() => $"Account Pool [{_immichApi.BaseUrl}]";

    public void Dispose() => (_apiCache as IDisposable)?.Dispose();
}
