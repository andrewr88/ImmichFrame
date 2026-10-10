using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace ImmichFrame.Core.Logic.Pool;

public class MemoryAssetsPool(ImmichApi immichApi, IAccountSettings accountSettings) : CachingApiAssetsPool(new DailyApiCache())
{
    private const string ExcludedAlbumsCacheKey = "ExcludedAlbums";

    // The most a search hands back at once.
    private const int SearchPageSize = 1000;

    protected override async Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default)
    {
        var searchDate = DateTimeOffset.Now;
        var memories = await immichApi.SearchMemoriesAsync(searchDate, null, null, null, null, null, null, null, null, ct);

        var memoryAssets = new List<AssetResponseDto>();
        foreach (var memory in memories)
        {
            var assets = memory.Assets.ToList();
            var yearsAgo = searchDate.Year - memory.Data.Year;

            if (!accountSettings.ShowVideos)
            {
                assets = assets.Where(a => a.Type == AssetTypeEnum.IMAGE).ToList();
            }

            foreach (var asset in assets)
            {
                if (asset.ExifInfo == null)
                {
                    var assetInfo = await immichApi.GetAssetInfoAsync(asset.Id, null, null, ct);
                    asset.ExifInfo = assetInfo.ExifInfo;
                    asset.People = assetInfo.People;
                }

                // ExifInfo is optional in the Immich API, so the full asset info can lack it too
                asset.ExifInfo ??= new ExifResponseDto();
                asset.ExifInfo.Description = $"{yearsAgo} {(yearsAgo == 1 ? "year" : "years")} ago";
            }

            memoryAssets.AddRange(assets);
        }

        return await ShownByAccount(memoryAssets);
    }

    /// <summary>
    /// The memories the account's settings let through. The rest of the account's photos are filtered
    /// by Immich's search, but memories come from elsewhere, so they are filtered here instead - to the
    /// same settings, read the same way: both ends of the date range inclusive, and a rating met by
    /// any photo rated at least that. Anything but an image or a video is never shown.
    /// </summary>
    private async Task<List<AssetResponseDto>> ShownByAccount(List<AssetResponseDto> assets)
    {
        var takenAfter = accountSettings.ImagesFromDate
            ?? (accountSettings.ImagesFromDays is int days ? DateTime.Today.AddDays(-days) : null);
        var takenBefore = accountSettings.ImagesUntilDate;
        var rating = accountSettings.Rating;

        var shown = assets.Where(asset =>
                (asset.Type == AssetTypeEnum.IMAGE || (asset.Type == AssetTypeEnum.VIDEO && accountSettings.ShowVideos))
                && (accountSettings.ShowArchived || !asset.IsArchived)
                && (takenAfter == null || asset.ExifInfo?.DateTimeOriginal >= takenAfter)
                && (takenBefore == null || asset.ExifInfo?.DateTimeOriginal <= takenBefore)
                && (rating == null || asset.ExifInfo?.Rating >= rating))
            .ToList();

        // On a day with no memories to show, the excluded albums are not searched at all.
        if (shown.Count == 0 || accountSettings.ExcludedAlbums is not { Count: > 0 } excludedAlbums)
        {
            return shown;
        }

        // Without a request's token: this is part of the memories' own load, which is cached and shared
        // with every request. Kept as long as the memories are, until the day is out.
        var excludedIds = await Cache.GetOrAddAsync(ExcludedAlbumsCacheKey, () => LoadAssetIds(excludedAlbums));
        return shown.Where(asset => !excludedIds.Contains(asset.Id)).ToList();
    }

    /// <summary>
    /// The ids of every asset in <paramref name="albums"/>, in one search paged through by its cursor.
    /// Looked for in each visibility the account shows a memory in, so that what is excluded does not
    /// rest on which of them Immich would search when it is not told.
    /// </summary>
    private async Task<HashSet<Guid>> LoadAssetIds(List<Guid> albums)
    {
        var ids = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await immichApi.SearchAssetsAsync(null, null, new MetadataSearchDto
            {
                Size = SearchPageSize,
                Cursor = cursor,
                Filter = new SearchFilter
                {
                    AlbumIds = new IdsFilter { Any = albums },
                    Visibility = SearchFilters.Visibility(accountSettings)
                }
            }, CancellationToken.None);

            ids.UnionWith(page.Assets.Items.Select(asset => asset.Id));
            cursor = page.Assets.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));

        return ids;
    }
}

class DailyApiCache : ApiCache
{
    public DailyApiCache() : base(() => new MemoryCacheEntryOptions
    {
        AbsoluteExpiration = ExpiryFor(DateTimeOffset.Now)
    }
    )
    {
    }

    /// <summary>
    /// The midnight that ends <paramref name="now"/>'s day, after which the memories loaded on it are
    /// no longer the ones "on this day". In local time, as <c>DateTimeOffset.Now</c> is: the date is
    /// read off <paramref name="now"/>'s clock and its midnight taken at the machine's own offset.
    /// Taken apart from the options so that it can be checked against fixed times.
    /// </summary>
    internal static DateTimeOffset ExpiryFor(DateTimeOffset now) => now.Date.AddDays(1);
}