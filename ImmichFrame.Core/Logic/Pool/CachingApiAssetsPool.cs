using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public abstract class CachingApiAssetsPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : IAssetPool
{
    private readonly Random _random = new();

    // Derived pools read these instead of their own constructor parameters, which would
    // otherwise be captured a second time on top of the copy held here (CS9107).
    protected IApiCache Cache { get; } = apiCache;
    protected ImmichApi Api { get; } = immichApi;
    protected IAccountSettings AccountSettings { get; } = accountSettings;

    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        return (await AllAssets()).Count();
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default)
    {
        return (await AllAssets()).OrderBy(_ => _random.Next()).Take(requested);
    }

    // Deliberately takes no CancellationToken: both loads are cached and served to every later
    // request, so they must not be cut short by whichever request happened to trigger them.
    private async Task<IEnumerable<AssetResponseDto>> AllAssets()
    {
        var excludedAlbumAssets = await Cache.GetOrAddAsync($"{GetType().FullName}_ExcludedAlbums", () => AssetHelper.GetExcludedAlbumAssets(Api, AccountSettings));

        return await Cache.GetOrAddAsync(GetType().FullName!, () => LoadAssets().ApplyAccountFilters(AccountSettings, excludedAlbumAssets));
    }

    protected abstract Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default);
}