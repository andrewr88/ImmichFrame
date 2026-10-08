namespace ImmichFrame.Core.Helpers;

public static class CollectionExtensionMethods
{
    private static readonly Random _random = new();
    
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

        var randomIndex = _random.NextInt64(totalCount);

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
    
    public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> source) => source.OrderBy(_ => _random.Next());
}