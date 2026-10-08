using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public class TagAssetsPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : CachingApiAssetsPool(apiCache, immichApi, accountSettings)
{
    protected override async Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default)
    {
        var tagAssets = new List<AssetResponseDto>();

        if (AccountSettings.Tags == null)
        {
            return tagAssets;
        }

        var allTags = await Cache.GetOrAddAsync($"allTags_{AccountSettings.ImmichServerUrl}",
            () => Api.GetAllTagsAsync(ct));
        var tagValueToTag = allTags.ToDictionary(t => t.Value);

        // Find the tags for the configured tag values
        var tags = new List<TagResponseDto>();
        foreach (var tagValue in AccountSettings.Tags)
        {
            if (tagValueToTag.TryGetValue(tagValue, out var tag))
            {
                tags.Add(tag);
            }
        }

        // Finding an asset already met under another tag is a lookup rather than a scan of every
        // asset so far; tagAssets keeps them in the order they were first found.
        var assetsById = new Dictionary<Guid, AssetResponseDto>();
        foreach (var tag in tags)
        {
            int page = 1;
            const int batchSize = 1000;
            int itemsInPage;
            do
            {
                var metadataBody = new MetadataSearchDto
                {
                    Page = page,
                    Size = batchSize,
                    TagIds = [tag.Id],
                    WithExif = true,
                    WithPeople = true
                };

                if (!AccountSettings.ShowVideos)
                {
                    metadataBody.Type = AssetTypeEnum.IMAGE;
                }

                var tagInfo = await Api.SearchAssetsAsync(null, null, metadataBody, ct);

                itemsInPage = tagInfo.Assets.Items.Count;

                // Attach the tag that matched this search
                foreach (var asset in tagInfo.Assets.Items)
                {
                    if (assetsById.TryGetValue(asset.Id, out var seen))
                    {
                        seen.Tags.Add(tag);
                        continue;
                    }

                    // SearchAssetsAsync does not support a `WithTags`
                    // parameter, so simply set the one that was configured
                    asset.Tags = new List<TagResponseDto> { tag };

                    assetsById.Add(asset.Id, asset);
                    tagAssets.Add(asset);
                }

                page++;
            } while (itemsInPage == batchSize);
        }

        return tagAssets;
    }
}
