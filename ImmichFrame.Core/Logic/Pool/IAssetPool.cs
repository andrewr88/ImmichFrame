using ImmichFrame.Core.Api;

namespace ImmichFrame.Core.Logic.Pool;

public interface IAssetPool
{
    Task<long> GetAssetCount(CancellationToken ct = default);
    Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default);

    protected static async Task<IEnumerable<AssetResponseDto>> WaitAssets(
        int requested,
        Func<CancellationToken, Task<AssetResponseDto?>> supplier,
        CancellationToken cancellationToken)
    {
        var itemsRead = new List<AssetResponseDto>(requested > 0 ? requested : 0);

        for (var i = 0; i < requested; i++)
        {
            var asset = await supplier(cancellationToken);

            if (asset != null)
            {
                itemsRead.Add(asset);
            }
            else
            {
                break;
            }
        }

        return itemsRead;
    }
}