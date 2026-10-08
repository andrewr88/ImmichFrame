using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public class PersonAssetsPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : CachingApiAssetsPool(apiCache, immichApi, accountSettings)
{
    protected override async Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default)
    {
        var personAssets = new List<AssetResponseDto>();

        var people = AccountSettings.People;
        if (people == null)
        {
            return personAssets;
        }

        foreach (var personId in people)
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
                    PersonIds = [personId],
                    WithExif = true,
                    WithPeople = true
                };

                if (!AccountSettings.ShowVideos)
                {
                    metadataBody.Type = AssetTypeEnum.IMAGE;
                }

                var personInfo = await Api.SearchAssetsAsync(null, null, metadataBody, ct);

                itemsInPage = personInfo.Assets.Items.Count;

                personAssets.AddRange(personInfo.Assets.Items);
                page++;
            } while (itemsInPage == batchSize);
        }

        return personAssets;
    }
}