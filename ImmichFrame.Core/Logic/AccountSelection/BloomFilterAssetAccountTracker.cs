using System.Collections.Concurrent;
using BloomFilter;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Logic.AccountSelection;

public class BloomFilterAssetAccountTracker(ILogger<BloomFilterAssetAccountTracker> _logger) : IAssetAccountTracker
{
    // Lazy<Task<...>> so concurrent RecordAssetLocation calls for the same account build
    // exactly one filter (GetOrAdd alone doesn't guarantee single execution of the factory).
    private readonly ConcurrentDictionary<IAccountImmichFrameLogic, Lazy<Task<IBloomFilter>>> logicToFilter = new();

    public async ValueTask<bool> RecordAssetLocation(IAccountImmichFrameLogic account, Guid assetId)
    {
        var lazyFilter = logicToFilter.GetOrAdd(account,
            acc => new Lazy<Task<IBloomFilter>>(() => NewFilter(acc)));
        IBloomFilter filter;
        try
        {
            filter = await lazyFilter.Value;
        }
        catch
        {
            // A faulted Lazy would otherwise be cached forever; evict it (only if it is
            // still this exact entry) so the next call rebuilds the filter.
            logicToFilter.TryRemove(KeyValuePair.Create(account, lazyFilter));
            throw;
        }
        return await filter.AddAsync(assetId.ToString());
    }

    private static async Task<IBloomFilter> NewFilter(IImmichFrameLogic account)
    {
        return FilterBuilder.Build(await account.GetTotalAssets());
    }

    public async Task<T> ForAsset<T>(Guid assetId, Func<IAccountImmichFrameLogic, Task<T>> f, CancellationToken ct = default)
    {
        foreach (var entry in logicToFilter)
        {
            var filterTask = entry.Value.Value;
            // A filter still being built cannot have recorded this asset yet.
            if (!filterTask.IsCompletedSuccessfully) continue;

            if (filterTask.Result.Contains(assetId.ToString()))
            {
                // Awaited here rather than handed back: an account's failure arrives inside the
                // Task, so only awaiting it lets the lookup move on to the next account. A 416 is
                // let through: that account does hold the asset and is saying the requested range
                // lies outside it, which is no false positive - and AssetController.GetAsset
                // answers exactly that exception with a 416 of its own. So is the caller's own
                // cancellation: once it has given up there is nobody left to look any further for,
                // and AssetController.GetAsset answers that with nothing at all.
                try
                {
                    return await f(entry.Key);
                }
                catch (Exception e) when (e is not ApiException { StatusCode: 416 }
                    && !(e is OperationCanceledException && ct.IsCancellationRequested))
                {
                    _logger.LogWarning(e, "Failed to locate asset {assetId} in {entry.Key}. Must be false positive, trying next account.", assetId, entry.Key);
                }
            }
        }

        _logger.LogError("Failed to locate account for asset {assetId}", assetId);
        throw new AssetNotFoundException();
    }
}