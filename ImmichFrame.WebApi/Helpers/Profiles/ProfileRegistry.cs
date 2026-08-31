using System.Collections.Concurrent;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.WebApi.Helpers.Config;

namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// Hands out the <see cref="ProfileServices"/> for a configuration profile, building each one on
/// first use and caching it for the life of the process.
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
        var settings = _catalog.Get(profileName);

        var key = string.IsNullOrWhiteSpace(profileName) ? ConfigCatalog.DefaultProfileName : profileName;

        // Lazy inside GetOrAdd: GetOrAdd alone can run its factory more than once under contention,
        // and building a ProfileServices twice would mean two sets of pools and caches.
        return _byProfile.GetOrAdd(key, _ => new Lazy<ProfileServices>(
            () => new ProfileServices(_root, settings),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
