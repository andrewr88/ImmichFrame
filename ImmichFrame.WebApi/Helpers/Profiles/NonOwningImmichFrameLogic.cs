using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;

namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// Forwards every <see cref="IImmichFrameLogic"/> call to a profile's shared logic without owning it.
/// <para>
/// The wrapper looks pointless and is not. Microsoft.Extensions.DependencyInjection takes ownership
/// of any <see cref="IDisposable"/> a factory registration returns - it makes no difference that the
/// instance was built elsewhere and is shared - and disposes it when the scope ends. A profile's
/// logic is disposable (<see cref="ImmichFrame.Core.Logic.MultiImmichFrameLogicDelegate"/>, whose
/// Dispose walks the accounts tearing down their asset pools and API caches), so handing it straight
/// to the scoped registration made the first request on a profile destroy that profile's caches on
/// its way out, while the registry went on serving the same dead graph to every request after it.
/// </para>
/// <para>
/// So this is deliberately <b>not</b> <see cref="IDisposable"/>: there is nothing here for the
/// container to take ownership of. Do not unwrap it in <c>Program.cs</c>, and do not let it become
/// disposable - <see cref="ProfileRegistry.Invalidate"/> is the only thing that may end a profile
/// graph's life.
/// </para>
/// </summary>
public sealed class NonOwningImmichFrameLogic(IImmichFrameLogic _logic) : IImmichFrameLogic
{
    /// <summary>
    /// The shared instance the calls go to. Exposed so tests can assert which profile graph a
    /// request was pinned to; nothing in production should need to reach past the forwarder.
    /// </summary>
    internal IImmichFrameLogic Target => _logic;

    public Task<AssetResponseDto?> GetNextAsset() => _logic.GetNextAsset();

    public Task<IEnumerable<AssetResponseDto>> GetAssets() => _logic.GetAssets();

    public Task<AssetResponseDto> GetAssetInfoById(Guid assetId) => _logic.GetAssetInfoById(assetId);

    public Task<IEnumerable<AssetFaceResponseDto>> GetAssetFacesById(Guid assetId) => _logic.GetAssetFacesById(assetId);

    public Task<IEnumerable<AlbumResponseDto>> GetAlbumInfoById(Guid assetId) => _logic.GetAlbumInfoById(assetId);

    public Task<AssetResponse> GetAsset(Guid id, AssetTypeEnum? assetType = null, string? rangeHeader = null)
        => _logic.GetAsset(id, assetType, rangeHeader);

    public Task<long> GetTotalAssets() => _logic.GetTotalAssets();

    public Task SendWebhookNotification(IWebhookNotification notification) => _logic.SendWebhookNotification(notification);
}
