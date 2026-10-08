using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.AccountSelection;

public interface IAssetAccountTracker
{
    ValueTask<bool> RecordAssetLocation(IAccountImmichFrameLogic account, Guid assetId);
    Task<T> ForAsset<T>(Guid assetId, Func<IAccountImmichFrameLogic, Task<T>> f);
}