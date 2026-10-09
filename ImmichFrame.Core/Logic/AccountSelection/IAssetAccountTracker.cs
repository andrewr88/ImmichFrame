using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.AccountSelection;

public interface IAssetAccountTracker
{
    ValueTask<bool> RecordAssetLocation(IAccountImmichFrameLogic account, Guid assetId);
    Task<T> ForAsset<T>(Guid assetId, Func<IAccountImmichFrameLogic, Task<T>> f, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="ForAsset{T}(Guid, Func{IAccountImmichFrameLogic, Task{T}}, CancellationToken)"/>,
    /// for a lookup that an account not holding the asset answers rather than fails: an answer
    /// <paramref name="accept"/> turns down sends the lookup on to the next account, as a failure
    /// would, and is the answer only if no account gives one it takes.
    /// </summary>
    Task<T> ForAsset<T>(Guid assetId, Func<IAccountImmichFrameLogic, Task<T>> f, Func<T, bool> accept, CancellationToken ct = default);
}