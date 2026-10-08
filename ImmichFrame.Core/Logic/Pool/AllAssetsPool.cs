using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public class AllAssetsPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : IAssetPool
{
    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        // Counted by the search GetAssets draws from rather than from the library's own statistics,
        // which ignore the account's dates, rating and visibility: TotalAccountImagesSelectionStrategy
        // scales each account's batch against the largest count, so a 50,000-photo library showing
        // its last 7 days cut an account showing a 500-photo album to about one photo a batch.
        // Excluded albums cannot be put to the search, so their assets are still counted - an
        // over-count, as the random search draws them too and only drops them afterwards.
        var stats = await apiCache.GetOrAddAsync(nameof(AllAssetsPool),
            () => immichApi.SearchAssetStatisticsAsync(WithAccountFilters(new StatisticsSearchDto()), ct));

        return stats.Total;
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default)
    {
        var searchDto = WithAccountFilters(new RandomSearchDto
        {
            Size = requested,
            WithExif = true,
            WithPeople = true
        });

        var assets = await immichApi.SearchRandomAsync(searchDto, ct);
        var excludedAlbumAssets = await apiCache.GetOrAddAsync(
            $"{nameof(AllAssetsPool)}_ExcludedAlbums",
            () => AssetHelper.GetExcludedAlbumAssets(immichApi, accountSettings, ct));

        return assets.ApplyAccountFilters(accountSettings, excludedAlbumAssets);
    }

    // The one place the account's settings become search filters, for the count and the random
    // search alike, so that the two cannot drift apart.
    private T WithAccountFilters<T>(T search) where T : IAssetSearchFilters
    {
        if (!accountSettings.ShowVideos)
        {
            search.Type = AssetTypeEnum.IMAGE;
        }

        if (accountSettings.ShowArchived)
        {
            search.Visibility = AssetVisibility.Archive;
        }
        else
        {
            search.Visibility = AssetVisibility.Timeline;
        }

        search.TakenBefore = accountSettings.ImagesUntilDate;
        var takenAfter = accountSettings.ImagesFromDate ?? (accountSettings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-accountSettings.ImagesFromDays.Value) : null);

        if (takenAfter.HasValue)
        {
            search.TakenAfter = takenAfter;
        }

        if (accountSettings.Rating is int rating)
        {
            search.Rating = rating;
        }

        return search;
    }
}