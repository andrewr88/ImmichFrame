using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace ImmichFrame.Core.Logic.Pool;

public class MemoryAssetsPool(ImmichApi immichApi, IAccountSettings accountSettings) : CachingApiAssetsPool(new DailyApiCache(), immichApi, accountSettings)
{
    protected override async Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default)
    {
        var searchDate = DateTimeOffset.Now;
        var memories = await Api.SearchMemoriesAsync(searchDate, null, null, null, null, null, null, null, null, ct);

        var memoryAssets = new List<AssetResponseDto>();
        foreach (var memory in memories)
        {
            var assets = memory.Assets.ToList();
            var yearsAgo = searchDate.Year - memory.Data.Year;

            if (!AccountSettings.ShowVideos)
            {
                assets = assets.Where(a => a.Type == AssetTypeEnum.IMAGE).ToList();
            }

            foreach (var asset in assets)
            {
                if (asset.ExifInfo == null)
                {
                    var assetInfo = await Api.GetAssetInfoAsync(asset.Id, null, null, ct);
                    asset.ExifInfo = assetInfo.ExifInfo;
                    asset.People = assetInfo.People;
                }

                // ExifInfo is optional in the Immich API, so the full asset info can lack it too
                asset.ExifInfo ??= new ExifResponseDto();
                asset.ExifInfo.Description = $"{yearsAgo} {(yearsAgo == 1 ? "year" : "years")} ago";
            }

            memoryAssets.AddRange(assets);
        }

        return memoryAssets;
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