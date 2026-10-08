using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Helpers
{
    public static class AssetExtensionMethods
    {
        public static bool IsSupportedAsset(this AssetResponseDto asset)
        {
            return asset.Type == AssetTypeEnum.IMAGE || asset.Type == AssetTypeEnum.VIDEO;
        }

        public static async Task<IEnumerable<AssetResponseDto>> ApplyAccountFilters(this Task<IEnumerable<AssetResponseDto>> unfilteredAssets, IAccountSettings accountSettings, IEnumerable<AssetResponseDto> excludedAlbumAssets)
        {
            return ApplyAccountFilters(await unfilteredAssets, accountSettings, excludedAlbumAssets);
        }

        public static IEnumerable<AssetResponseDto> ApplyAccountFilters(this IEnumerable<AssetResponseDto> unfilteredAssets, IAccountSettings accountSettings, IEnumerable<AssetResponseDto> excludedAlbumAssets)
        {
            // Display supported media types
            var assets = unfilteredAssets.Where(asset => asset.IsSupportedAsset());

            if (!accountSettings.ShowVideos)
                assets = assets.Where(x => x.Type == AssetTypeEnum.IMAGE);

            if (!accountSettings.ShowArchived)
                assets = assets.Where(x => x.IsArchived == false);

            var takenBefore = accountSettings.ImagesUntilDate;
            if (takenBefore.HasValue)
            {
                assets = assets.Where(x => x.ExifInfo?.DateTimeOriginal != null && x.ExifInfo.DateTimeOriginal <= takenBefore);
            }

            var takenAfter = accountSettings.ImagesFromDate ?? (accountSettings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-accountSettings.ImagesFromDays.Value) : null);
            if (takenAfter.HasValue)
            {
                assets = assets.Where(x => x.ExifInfo?.DateTimeOriginal != null && x.ExifInfo.DateTimeOriginal >= takenAfter);
            }

            if (accountSettings.Rating is int rating)
            {
                assets = assets.Where(x => x.ExifInfo?.Rating == rating);
            }

            // Looked up in a set of ids made once, rather than by scanning every excluded asset for
            // every asset: 20,000 assets against 5,000 excluded ones was up to a hundred million
            // comparisons, boxing both ids for each. And materialised before it is returned, because
            // CachingApiAssetsPool caches the result and enumerates it on every request - a lazy
            // query ran every filter above again each time.
            var excludedIds = excludedAlbumAssets.Select(asset => asset.Id).ToHashSet();

            return assets.Where(asset => !excludedIds.Contains(asset.Id)).ToList();
        }
    }
}
