namespace ImmichFrame.Core.Helpers;

public static class CollectionExtensionMethods
{
    public static IEnumerable<T> TakeProportional<T>(this IEnumerable<T> enumerable, double proportion)
    {
        if (proportion <= 0) return [];

        var list = enumerable.ToList();
        var itemsToTake = (int)Math.Ceiling(list.Count * proportion);
        return list.Take(itemsToTake);
    }

    public static IEnumerable<T> WhereExcludes<T>(this IEnumerable<T> source, IEnumerable<T> excluded)
        => WhereExcludes(source, excluded, t => t!);

    /// <summary>
    /// The items of <paramref name="source"/>, in order and duplicates kept, whose key under
    /// <paramref name="comparator"/> is not the key of any item of <paramref name="excluded"/>.
    /// <para>
    /// The excluded keys are hashed once per enumeration rather than scanned once per item, which cost
    /// n·m key comparisons. Still deferred, as the scan was: nothing is read until the result is
    /// enumerated, and each enumeration reads <paramref name="excluded"/> as it is then.
    /// </para>
    /// </summary>
    public static IEnumerable<T> WhereExcludes<T>(this IEnumerable<T> source, IEnumerable<T> excluded, Func<T, object> comparator)
    {
        var excludedKeys = excluded.Select(comparator).ToHashSet();

        foreach (var item in source)
        {
            if (!excludedKeys.Contains(comparator(item)))
            {
                yield return item;
            }
        }
    }

    public static async Task<T?> ChooseOne<T>(this IEnumerable<T> sources, Func<T, Task<long>> probabilitySelector)
    {
        var sourcesAndCounts = await Task.WhenAll(
            sources.Select(async source => (Source: source, Count: await probabilitySelector(source)))
                .ToList());

        var totalCount = sourcesAndCounts.Sum(source => source.Count);

        // Random.Shared rather than a Random of this class's own: concurrent requests choose at
        // once, and a Random is not safe to share between threads.
        var randomIndex = Random.Shared.NextInt64(totalCount);

        foreach (var sourceAndCount in sourcesAndCounts)
        {
            if (randomIndex < sourceAndCount.Count)
            {
                return sourceAndCount.Source;
            }

            randomIndex -= sourceAndCount.Count;
        }

        return default;
    }

    /// <summary>
    /// The items of <paramref name="source"/> in a random order, by a Fisher-Yates shuffle that
    /// makes one swap per item read: taking k of n items costs k swaps, where ordering by random
    /// keys sorted all n. Deferred, as that sort was - <paramref name="source"/> is read, and
    /// shuffled afresh, each time the result is enumerated - and a copy is shuffled rather than
    /// <paramref name="source"/> itself, which can be a list every request reads.
    /// </summary>
    public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> source)
    {
        var items = source.ToArray();

        for (var i = 0; i < items.Length; i++)
        {
            var pick = Random.Shared.Next(i, items.Length);
            (items[i], items[pick]) = (items[pick], items[i]);

            yield return items[i];
        }
    }
}