using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace ImmichFrame.Core.Helpers;

public class ApiCache : IApiCache, IDisposable
{
    private readonly Func<MemoryCacheEntryOptions> _cacheOptions;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    /// <summary>
    /// The loads still in flight, one per key, each a <c>Lazy&lt;Task&lt;T&gt;&gt;</c> for its key's
    /// value type. Kept apart from <see cref="_cache"/> so that no expiry applies to them: with a
    /// refresh interval of zero an entry lives for a millisecond, and a load sharing that lifetime
    /// would stop being shared long before it finished.
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _loads = new();

    public ApiCache(TimeSpan cacheDuration) : this(() => new MemoryCacheEntryOptions()
    {
        AbsoluteExpirationRelativeToNow = cacheDuration
    })
    {
    }

    public ApiCache(Func<MemoryCacheEntryOptions> entryOptions)
    {
        _cacheOptions = entryOptions;
    }

    /// <summary>
    /// The value cached under <paramref name="key"/>, or else the one <paramref name="factory"/>
    /// loads - once, however many callers ask for it while that load is in flight.
    /// <para>
    /// <c>MemoryCache.GetOrCreateAsync</c> does not do that: it holds a value only once there is
    /// one, so every caller that missed while a load was running started a load of its own. The
    /// asset pools load everything they draw from through here - albums, people and tags paged out
    /// of Immich, and the excluded albums - so at startup and at every expiry that was one full load
    /// per concurrent request. Callers that miss now join the load already in flight and share its
    /// outcome, failure included.
    /// </para>
    /// <para>
    /// Only the factory of the caller that started the load runs, so a factory that captures its
    /// request's <see cref="CancellationToken"/> lets that request cancel the load for everyone
    /// sharing it.
    /// </para>
    /// </summary>
    public virtual Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory)
    {
        if (_cache.Get(key) is Task<T> cached)
        {
            return cached;
        }

        // A Lazy rather than the task itself, because GetOrAdd may build a value it then throws
        // away, and building this one starts a load. Only the Lazy that won is ever run.
        Lazy<Task<T>>? load = null;
        load = new Lazy<Task<T>>(() => Load(key, factory, load!));

        return ((Lazy<Task<T>>)_loads.GetOrAdd(key, load)).Value;
    }

    private async Task<T> Load<T>(string key, Func<Task<T>> factory, Lazy<Task<T>> load)
    {
        try
        {
            // The load this caller missed may have finished, cached its value and left _loads
            // between that miss and this load being added in its place.
            if (_cache.Get(key) is Task<T> cached)
            {
                return await cached;
            }

            // The entry is made the way GetOrCreateAsync made it: options taken when the load
            // starts - DailyApiCache's midnight is worked out per entry - and the relative expiry
            // counted from when the entry is committed, once the load is done. A load that faults
            // or is cancelled never sets a value, and an entry disposed without one is not
            // committed, so the next caller loads again rather than being handed the failure.
            using var entry = _cache.CreateEntry(key);
            entry.SetOptions(_cacheOptions());

            var value = await factory();
            entry.Value = Task.FromResult(value);
            return value;
        }
        finally
        {
            // After the entry is committed, so a caller never finds neither the value nor the load,
            // and before the task completes, so nobody can join a load that has already failed. By
            // value as well as by key, so that this can only ever take out this load and never one
            // that replaced it.
            _loads.TryRemove(new KeyValuePair<string, object>(key, load));
        }
    }

    public void Dispose()
        => _cache.Dispose();
}