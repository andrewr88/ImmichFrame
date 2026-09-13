using System.Collections.Concurrent;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.WebApi.Helpers.Config;

namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// Hands out the <see cref="ProfileServices"/> for a configuration profile, building each one on
/// first use and caching it until the configuration it was built from is replaced.
/// <para>
/// Built lazily rather than eagerly at startup: a catalog with a dozen profiles would otherwise
/// spin up a dozen sets of asset pools and HTTP clients before serving a single request, most of
/// which may never be asked for.
/// </para>
/// </summary>
public sealed class ProfileRegistry(IConfigCatalog _catalog, IServiceProvider _root)
{
    private readonly ConcurrentDictionary<string, Lazy<ProfileServices>> _byProfile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The profile's services as they stand now, unleased: a configuration swap may retire and tear
    /// down the returned graph while the caller still holds it, and a caller that has taken no lease
    /// has no claim on it surviving one. That is the right bargain for a caller that only wants to
    /// look at a profile; anything that goes on using its graph - a request does, for its whole life
    /// - takes a <see cref="Lease"/> instead.
    /// </summary>
    /// <param name="profileName">
    /// Profile to resolve; null, empty or <see cref="ConfigCatalog.DefaultProfileName"/> resolve to
    /// the default configuration.
    /// </param>
    /// <exception cref="ProfileNotFoundException">No profile of that name is configured.</exception>
    public ProfileServices For(string? profileName)
    {
        // Resolved through the catalog first so an unknown name fails here, rather than caching a
        // half-built entry under a name that does not exist.
        _catalog.Get(profileName);

        var key = KeyFor(profileName);

        // Lazy inside GetOrAdd: GetOrAdd alone can run its factory more than once under contention,
        // and building a ProfileServices twice would mean two sets of pools and caches.
        //
        // The settings are read inside the factory rather than captured above, so a swap racing
        // this call cannot leave a stale entry behind: the entry is in the dictionary before the
        // factory runs, so either Invalidate() removes it, or it is built after the catalog was
        // replaced and therefore from the new settings.
        return _byProfile.GetOrAdd(key, _ => new Lazy<ProfileServices>(
            () => new ProfileServices(_root, _catalog.Get(profileName)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// The profile's services with a lease taken on them, which keeps that graph alive - working,
    /// not merely referenced - until the lease is given back, even if a configuration swap retires
    /// it in between. <see cref="ProfileScope"/> is the caller: one lease per request, released when
    /// the request's scope ends.
    /// </summary>
    /// <param name="profileName">As <see cref="For"/>.</param>
    /// <exception cref="ProfileNotFoundException">No profile of that name is configured.</exception>
    public ProfileServices Lease(string? profileName)
    {
        while (true)
        {
            var services = For(profileName);

            // Retired between being handed out and the lease being taken, so going round again
            // builds from the catalog that is current now rather than leasing a graph nothing will
            // ever serve again. That this terminates rests on retirement always following eviction:
            // Invalidate removes an entry before it retires it, and the orphan check below only ever
            // retires a graph that is already out of the dictionary. A graph retired while still
            // reachable under its key would be handed straight back here, forever.
            if (!services.TryAcquire()) continue;

            // An entry evicted while its Lazy was still building leaves the graph it was building in
            // no dictionary at all, so Invalidate never sees it and nothing would ever tear it down.
            // Retiring it here makes it die with the request that built it.
            if (!IsCached(KeyFor(profileName), services)) services.Retire();

            return services;
        }
    }

    /// <summary>
    /// Drops every cached <see cref="ProfileServices"/>, so the next <see cref="For"/> or
    /// <see cref="Lease"/> rebuilds from whatever the catalog now says. Called when the catalog is
    /// replaced - see <see cref="Config.SwappableConfigCatalog.Swap"/>, which is the only thing that
    /// should.
    /// <para>
    /// Dropped is not destroyed. Each graph is retired rather than torn down, so one a request is
    /// still inside outlives the swap and is torn down when that request releases its lease; what
    /// retiring guarantees immediately is that the graph is never handed out again.
    /// </para>
    /// </summary>
    public void Invalidate()
    {
        // Swapped out entry by entry rather than cleared in one go, so each dropped graph can be
        // retired. MultiImmichFrameLogicDelegate and PooledImmichFrameLogic became IDisposable
        // upstream - the asset pools hold API caches that do not go away on their own - so dropping
        // the reference and leaving it to the collector is no longer enough.
        foreach (var key in _byProfile.Keys.ToList())
        {
            if (!_byProfile.TryRemove(key, out var services)) continue;

            // Only a Lazy that actually ran built anything to retire. Asking an unevaluated one for
            // its Value here would construct a whole profile graph purely in order to tear it down;
            // the one it is building is caught by Lease instead, which finds it evicted. A Lazy whose
            // factory threw reports no value either, so a profile that failed to build is skipped
            // here rather than rethrowing its exception at whoever is swapping the configuration.
            if (!services.IsValueCreated) continue;

            // Unguarded deliberately: a graph objecting to being torn down must not fail the swap,
            // and that guard now lives with the teardown itself, which the request holding the last
            // lease can reach just as easily as this can.
            services.Value.Retire();
        }
    }

    /// <summary>
    /// Whether <paramref name="services"/> is still what the key resolves to. Identity only, and
    /// only against an entry that already has a value: forcing an unevaluated <see cref="Lazy{T}"/>
    /// here would build a second whole profile graph purely in order to compare it.
    /// </summary>
    private bool IsCached(string key, ProfileServices services) =>
        _byProfile.TryGetValue(key, out var entry) && entry.IsValueCreated && ReferenceEquals(entry.Value, services);

    private static string KeyFor(string? profileName) =>
        string.IsNullOrWhiteSpace(profileName) ? ConfigCatalog.DefaultProfileName : profileName;
}
