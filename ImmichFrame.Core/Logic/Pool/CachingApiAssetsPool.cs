using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public abstract class CachingApiAssetsPool(IApiCache apiCache) : IAssetPool
{
    // For whatever else a derived pool loads alongside its assets, to be kept for as long as they are.
    protected IApiCache Cache { get; } = apiCache;

    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        return (await AllAssets()).Count();
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default)
    {
        return (await AllAssets()).Shuffle().Take(requested);
    }

    // Deliberately takes no CancellationToken: the load is cached and served to every later request,
    // so it must not be cut short by whichever request happened to trigger it.
    private Task<IEnumerable<AssetResponseDto>> AllAssets()
        => Cache.GetOrAddAsync(GetType().FullName!, LoadDistinctAssets);

    // Deduplicated here, once for every pool, rather than in each LoadAssets: an asset that comes back
    // twice was counted - and could be handed out in one batch - once for each time. Materialised,
    // because what is cached here is read on every request, and a lazy query ran again each time.
    private async Task<IEnumerable<AssetResponseDto>> LoadDistinctAssets()
        => (await LoadAssets()).DistinctBy(asset => asset.Id).ToList();

    protected abstract Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default);
}