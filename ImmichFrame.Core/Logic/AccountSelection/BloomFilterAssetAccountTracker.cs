using System.Collections.Concurrent;
using BloomFilter;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Logic.AccountSelection;

public class BloomFilterAssetAccountTracker(ILogger<BloomFilterAssetAccountTracker> _logger) : IAssetAccountTracker
{
    /// <summary>
    /// The fewest assets an account's filter is first built for. An account can hand out assets while
    /// counting none - its count is cached, and can date from before its first photo - and no filter
    /// can be built for none at all. Starting from a thousand rather than from one costs about a
    /// kilobyte, and spares an account that counts only a handful the run of small filters it would
    /// otherwise add on its way up.
    /// </summary>
    private const long MinimumExpectedAssets = 1000;

    // Lazy<Task<...>> so concurrent RecordAssetLocation calls for the same account build
    // exactly one filter (GetOrAdd alone doesn't guarantee single execution of the factory).
    private readonly ConcurrentDictionary<IAccountImmichFrameLogic, Lazy<Task<ScalableFilter>>> logicToFilter = new();

    public async ValueTask<bool> RecordAssetLocation(IAccountImmichFrameLogic account, Guid assetId)
    {
        var lazyFilter = logicToFilter.GetOrAdd(account,
            acc => new Lazy<Task<ScalableFilter>>(() => NewFilter(acc)));
        ScalableFilter filter;
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
        return filter.Add(assetId.ToString());
    }

    private static async Task<ScalableFilter> NewFilter(IImmichFrameLogic account)
    {
        return new ScalableFilter(Math.Max(await account.GetTotalAssets(), MinimumExpectedAssets));
    }

    public Task<T> ForAsset<T>(Guid assetId, Func<IAccountImmichFrameLogic, Task<T>> f, CancellationToken ct = default)
        => ForAsset(assetId, f, _ => true, ct);

    public async Task<T> ForAsset<T>(Guid assetId, Func<IAccountImmichFrameLogic, Task<T>> f, Func<T, bool> accept, CancellationToken ct = default)
    {
        // An answer turned down, kept in case no account has one that is not.
        (bool Given, T Answer) declined = default;

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
                T answer;
                try
                {
                    answer = await f(entry.Key);
                }
                catch (Exception e) when (e is not ApiException { StatusCode: 416 }
                    && !(e is OperationCanceledException && ct.IsCancellationRequested))
                {
                    _logger.LogWarning(e, "Failed to locate asset {assetId} in {entry.Key}. Must be false positive, trying next account.", assetId, entry.Key);
                    continue;
                }

                if (accept(answer)) return answer;

                declined = (true, answer);
            }
        }

        if (declined.Given) return declined.Answer;

        _logger.LogError("Failed to locate account for asset {assetId}", assetId);
        throw new AssetNotFoundException();
    }

    /// <summary>
    /// One account's record of the assets it has handed out: a scalable Bloom filter.
    /// <para>
    /// A Bloom filter is built for a number of entries, and past that number it fills until it claims
    /// to hold everything - which sent each request for any asset to the account, and its failure to
    /// serve one it never had was taken for a false positive. An account's count is only an estimate
    /// of how many assets it will hand out - a memories-only account counts today's memories, and
    /// hands out a new day's every day - so a filter is never filled past what it was built for:
    /// once full, another twice its size is added beside it, and an asset is looked for in all of them.
    /// </para>
    /// <para>
    /// Each added filter is built for half the false-positive rate of the one before it, so that
    /// however many are added, the chance of one of them claiming an asset never recorded stays under
    /// twice the first's: 1% + 0.5% + 0.25% + ... &lt; 2%.
    /// </para>
    /// </summary>
    private sealed class ScalableFilter(long expectedAssets)
    {
        private const double FirstErrorRate = 0.01;

        private readonly object _gate = new();

        // Replaced rather than added to, so that Contains reads every layer without taking the lock.
        private volatile Layer[] _layers = [new Layer(expectedAssets, FirstErrorRate)];

        public bool Contains(string assetId) => _layers.Any(layer => layer.Filter.Contains(assetId));

        public bool Add(string assetId)
        {
            lock (_gate)
            {
                // Already recorded - or claimed by a false positive, which finds it just the same - so
                // not counted again towards what the newest layer was built for.
                if (Contains(assetId)) return false;

                var newest = _layers[^1];
                newest.Filter.Add(assetId);

                if (++newest.Count >= newest.Capacity)
                    _layers = [.. _layers, new Layer(newest.Capacity * 2, newest.ErrorRate / 2)];

                return true;
            }
        }

        private sealed class Layer(long capacity, double errorRate)
        {
            public IBloomFilter Filter { get; } = FilterBuilder.Build(capacity, errorRate);
            public long Capacity => capacity;
            public double ErrorRate => errorRate;

            // Read and written under ScalableFilter's lock only.
            public long Count;
        }
    }
}