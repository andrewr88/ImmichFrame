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

        var key = string.IsNullOrWhiteSpace(profileName) ? ConfigCatalog.DefaultProfileName : profileName;

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
    /// Drops every cached <see cref="ProfileServices"/>, so the next <see cref="For"/> rebuilds
    /// from whatever the catalog now says. Called when the catalog is replaced - see
    /// <see cref="Config.SwappableConfigCatalog.Swap"/>, which is the only thing that should.
    /// </summary>
    public void Invalidate()
    {
        // Swapped out entry by entry rather than cleared in one go, so each dropped graph can be
        // disposed. MultiImmichFrameLogicDelegate and PooledImmichFrameLogic became IDisposable
        // upstream - the asset pools hold API caches that do not go away on their own - so dropping
        // the reference and leaving it to the collector is no longer enough.
        foreach (var key in _byProfile.Keys.ToList())
        {
            if (!_byProfile.TryRemove(key, out var services)) continue;

            // Only a Lazy that actually ran built anything to dispose. Asking an unevaluated one for
            // its Value here would construct a whole profile graph purely in order to tear it down.
            if (!services.IsValueCreated) continue;

            try
            {
                services.Value.Dispose();
            }
            catch (Exception)
            {
                // A profile that failed to build has nothing to release, and a swap must not fail
                // because an outgoing graph objected to being torn down.
            }
        }
    }
}
