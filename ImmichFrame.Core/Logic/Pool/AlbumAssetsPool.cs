using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public class AlbumAssetsPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : CachingApiAssetsPool(apiCache, immichApi, accountSettings)
{
    protected override async Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default)
    {
        var albumAssets = new List<AssetResponseDto>();

        var albums = AccountSettings.Albums;
        if (albums != null)
        {
            foreach (var albumId in albums)
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
                        AlbumIds = [albumId],
                        WithExif = true,
                        WithPeople = true,
                    };

                    if (!AccountSettings.ShowVideos)
                    {
                        metadataBody.Type = AssetTypeEnum.IMAGE;
                    }

                    var searchResponse = await Api.SearchAssetsAsync(null, null, metadataBody, ct);

                    itemsInPage = searchResponse.Assets.Items.Count;

                    albumAssets.AddRange(searchResponse.Assets.Items);
                    page++;
                } while (itemsInPage == batchSize);
            }
        }

        return albumAssets;
    }
}