using System.Net.Http.Headers;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using ImmichFrame.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly IApiCache _apiCache;
    private readonly IAssetPool _pool;
    private readonly ImmichApi _immichApi;

    // The only names an image is ever cached under: GetImageAsset saves a WebP preview as .webp
    // and anything else as .jpeg.
    private static readonly string[] CachedImageExtensions = ["jpeg", "webp"];

    // Settable only so that tests can give each run an image cache of its own.
    internal string DownloadLocation { get; init; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");

    public PooledImmichFrameLogic(IAccountSettings accountSettings, IGeneralSettings generalSettings, IHttpClientFactory httpClientFactory)
    {
        _generalSettings = generalSettings;
        _httpClientFactory = httpClientFactory;

        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        AccountSettings = accountSettings;

        httpClient.UseApiKey(accountSettings.ApiKey);
        _immichApi = new ImmichApi(accountSettings.ImmichServerUrl, httpClient);

        _apiCache = new ApiCache(RefreshInterval(generalSettings.RefreshAlbumPeopleInterval));
        _pool = BuildPool(accountSettings);
    }

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
        if (_generalSettings.DownloadImages && OpenCachedImage(id) is { } cached)
        {
            return cached;
        }

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
        var fileName = $"{id}.{ext}";

        if (_generalSettings.DownloadImages)
        {
            // The image is served from the cache, so the response is done with once the copy has
            // finished or failed, and nothing else is in a position to dispose it.
            using (data)
            {
                return (fileName, contentType, await DownloadToCache(data.Stream, fileName, ct));
            }
        }

        return (fileName, contentType, data.Stream);
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
            catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Never cached, or expired and deleted by a concurrent request a moment ago.
                continue;
            }

            // Judged by the file that was opened, not by its name: a concurrent download can move a
            // fresh copy in under that name at any time.
            if (_generalSettings.RenewImagesDuration > (DateTime.UtcNow - File.GetCreationTimeUtc(fs.SafeFileHandle)).Days)
                return (fileName, $"image/{ext}", fs);

            // Deleting by name can also take a fresh copy a concurrent download has just moved in.
            // That costs the next request a download - not the one serving that copy, which keeps
            // its handle.
            fs.Dispose();
            File.Delete(filePath);
        }

        return null;
    }

    /// <summary>
    /// Writes a downloaded image into the cache and returns the cached copy, open for reading. The
    /// download goes to a file of its own and is moved in under <paramref name="fileName"/> only
    /// once it is complete, so the cache never holds part of an image - not after a failed download,
    /// and not after a crash - and requests downloading the same image at once do not collide.
    /// </summary>
    private async Task<Stream> DownloadToCache(Stream image, string fileName, CancellationToken ct)
    {
        Directory.CreateDirectory(DownloadLocation);
        var tempPath = Path.Combine(DownloadLocation, $"{fileName}.{Guid.NewGuid():N}.tmp");

        FileStream? cached = null;
        try
        {
            await using (var download = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                await image.CopyToAsync(download, ct);
            }

            // Opened before the move rather than after it: once under the image's name, the file can
            // be replaced or expired by a concurrent request at any time, but this handle stays on
            // the image just downloaded.
            cached = OpenCachedFile(tempPath);
            File.Move(tempPath, Path.Combine(DownloadLocation, fileName), overwrite: true);
            return cached;
        }
        catch
        {
            cached?.Dispose();
            File.Delete(tempPath);
            throw;
        }
    }

    // Sharing delete lets a concurrent request replace or expire a cached image while it is being
    // served; the reader keeps the image it opened.
    private static FileStream OpenCachedFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

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
    // rather than asking an account to. It goes through the same helper all the same, without a logger
    // since this class has none, so a failure here is dropped rather than escaping unobserved.
    public async Task SendWebhookNotification(IWebhookNotification notification) =>
        await WebhookHelper.SendWebhookNotification(notification, _generalSettings.Webhook,
            _httpClientFactory.CreateClient(WebhookHelper.HttpClientName), NullLogger.Instance);

    public override string ToString() => $"Account Pool [{_immichApi.BaseUrl}]";

    public void Dispose() => (_apiCache as IDisposable)?.Dispose();
}
