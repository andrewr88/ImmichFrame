using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

/// <summary>
/// Everything an account shows from its library - its favourites, albums, people and tags, or all of
/// it when it narrows to none - in one search that Immich filters, draws at random from and counts.
/// Nothing is paged out of Immich to be filtered here, so a source is no slower to draw from for being
/// large, and an asset found under two sources is one asset rather than two.
/// </summary>
public class AccountSearchPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : IAssetPool
{
    // In the account's own cache, as everything an account caches is, under a name no other entry in
    // it could be taken for.
    private const string AssetCountCacheKey = $"{nameof(AccountSearchPool)}_AssetCount";

    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds();
        if (SearchFilters.HasConfiguredSource(accountSettings) && !SearchFilters.HasMatchingBranch(accountSettings, tagIds))
        {
            return 0;
        }

        // Without the request's token: the count is cached and shared with every request that asks
        // while it loads, so whichever request started the load must not be able to cancel it for the
        // rest.
        var filter = SearchFilters.ForAccount(accountSettings, tagIds);
        return await apiCache.GetOrAddAsync(AssetCountCacheKey, async () =>
        {
            var stats = await immichApi.SearchAssetStatisticsAsync(new StatisticsSearchDto
            {
                Filter = filter
            }, CancellationToken.None);
            return stats.Total;
        });
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds();
        if (SearchFilters.HasConfiguredSource(accountSettings) && !SearchFilters.HasMatchingBranch(accountSettings, tagIds))
        {
            return [];
        }

        // The draw is this request's own, so it goes with the request's token.
        return await immichApi.SearchRandomAsync(new RandomSearchDto
        {
            Size = requested,
            WithExif = true,
            WithPeople = true,
            Filter = SearchFilters.ForAccount(accountSettings, tagIds)
        }, ct);
    }

    /// <summary>
    /// The ids of the configured tags, looked up by their value - the tag's full path, matched as
    /// Immich spells it, case and all. Each value is looked up once, compared as the lookup compares
    /// it, so a tag listed twice is searched for once, and values differing only in case are still two
    /// different tags. A value naming no tag is left out.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveTagIds()
    {
        if (accountSettings.Tags is not { Count: > 0 } values)
        {
            return [];
        }

        // Loaded without a request's token, for the reason the count is.
        var allTags = await apiCache.GetOrAddAsync($"allTags_{accountSettings.ImmichServerUrl}",
            () => immichApi.GetAllTagsAsync(CancellationToken.None));
        var tagValueToTag = allTags.ToDictionary(t => t.Value);

        return values
            .Distinct(tagValueToTag.Comparer)
            .Where(tagValueToTag.ContainsKey)
            .Select(value => tagValueToTag[value].Id)
            .ToList();
    }
}
