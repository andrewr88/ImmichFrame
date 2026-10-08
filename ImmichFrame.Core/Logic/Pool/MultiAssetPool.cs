using ImmichFrame.Core.Api;

namespace ImmichFrame.Core.Logic.Pool;

public class MultiAssetPool(IEnumerable<IAssetPool> delegates) : IAssetPool
{
    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        var counts = delegates.Select(pool => pool.GetAssetCount(ct));
        return (await Task.WhenAll(counts)).Sum();
    }

    /// <summary>
    /// Up to <paramref name="requested"/> assets from across the delegates, each delegate's share
    /// drawn in proportion to its count, shuffled together so that the batch does not come out a
    /// delegate at a time - the frame shows a batch in order.
    /// <para>
    /// The shares are drawn without replacement. A delegate's count is what it holds rather than
    /// what it has left - CachingApiAssetsPool hands out from all of its cached assets on every
    /// call - so picking a delegate by count for each asset in turn, and asking it for one asset each
    /// time, drew with replacement: an album of 3 photos made a batch of 25 with 3 distinct, and the
    /// same photo was shown back to back. Each draw here takes one off its delegate's remaining
    /// count instead, so that no delegate is asked for more than it holds, and each is asked once.
    /// </para>
    /// </summary>
    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default)
    {
        var pools = delegates.ToList();
        var counts = await Task.WhenAll(pools.Select(pool => pool.GetAssetCount(ct)));
        var shares = Share(requested, counts);

        var batches = await Task.WhenAll(pools
            .Zip(shares, (pool, share) => (pool, share))
            .Where(poolAndShare => poolAndShare.share > 0)
            .Select(poolAndShare => poolAndShare.pool.GetAssets(poolAndShare.share, ct)));

        // An asset can be in more than one delegate - a favourite in a configured album - and would
        // otherwise be in the batch once for each.
        var assets = batches.SelectMany(batch => batch).DistinctBy(asset => asset.Id).ToArray();
        Random.Shared.Shuffle(assets);

        return assets;
    }

    /// <summary>
    /// How many of <paramref name="requested"/> assets each delegate is asked for: drawn one at a
    /// time, each delegate weighted by how many of its assets have not been allotted yet.
    /// </summary>
    private static int[] Share(int requested, long[] counts)
    {
        var shares = new int[counts.Length];
        var unallotted = counts.Sum();

        for (var drawn = 0; drawn < requested && unallotted > 0; drawn++, unallotted--)
        {
            // A delegate with nothing left to allot spans none of the draw, so it is never chosen.
            var draw = Random.Shared.NextInt64(unallotted);
            var chosen = 0;
            while (draw >= counts[chosen] - shares[chosen])
            {
                draw -= counts[chosen] - shares[chosen];
                chosen++;
            }

            shares[chosen]++;
        }

        return shares;
    }
}