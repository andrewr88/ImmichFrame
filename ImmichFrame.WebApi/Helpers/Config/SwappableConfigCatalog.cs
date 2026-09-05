using System.Diagnostics.CodeAnalysis;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Profiles;

namespace ImmichFrame.WebApi.Helpers.Config;

/// <summary>
/// The <see cref="IConfigCatalog"/> everything else injects: a thin forward to whichever catalog is
/// current, so the configuration behind it can be replaced without rebuilding the service provider.
/// <para>
/// The seed is only run the first time a catalog is actually needed, so registering this never
/// touches the settings file on its own - a test that supplies its own catalog still starts without
/// reading from disk.
/// </para>
/// <para>
/// One type rather than a holder plus a separate swapper: replacing the catalog without dropping
/// the caches built from the old one is a silent correctness bug, so the field is private and
/// <see cref="Swap"/> is the only way to write it. <see cref="ProfileRegistry"/> arrives as a
/// factory rather than an instance because the registry injects this catalog - resolving it here
/// would close the constructor cycle.
/// </para>
/// </summary>
public sealed class SwappableConfigCatalog(Func<IConfigCatalog> _seed, Func<ProfileRegistry> _registry) : IConfigCatalog
{
    private Lazy<IConfigCatalog> _current = new(_seed, LazyThreadSafetyMode.ExecutionAndPublication);

    private IConfigCatalog Current => Volatile.Read(ref _current).Value;

    /// <summary>
    /// Serves <paramref name="replacement"/> from here on: every consumer of
    /// <see cref="IConfigCatalog"/> sees it, and the per-profile services built from the outgoing
    /// catalog are dropped so the next request rebuilds them.
    /// <para>
    /// A request already in flight keeps the <see cref="ProfileServices"/> pinned to its scope and
    /// finishes against the configuration it started with, which is what makes the swap safe to do
    /// under load: nothing in that graph is <see cref="IDisposable"/> and its <c>HttpClient</c>s
    /// come from <c>IHttpClientFactory</c>, so a superseded graph is simply collected once the last
    /// request holding it completes. A request that has not yet resolved its graph gets the
    /// replacement instead, so the two halves of one request are never mixed.
    /// </para>
    /// <para>
    /// The one rough edge: a request whose profile the replacement no longer declares, admitted by
    /// <see cref="UnknownProfileMiddleware"/> before the swap and resolving its services after it,
    /// fails with a <see cref="Core.Exceptions.ProfileNotFoundException"/> thrown from dependency
    /// injection, well outside MVC's exception filters. <see cref="ProfileNotFoundMiddleware"/> - added
    /// with the configuration editor, the first caller of this method - catches it in the pipeline and
    /// answers 404, which is what that request deserves. Mapping it belongs there rather than here.
    /// </para>
    /// </summary>
    public void Swap(IConfigCatalog replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        // Already-created rather than seeded, so the replacement is never built twice and the swap
        // publishes it in one write: a concurrent reader sees either the old catalog or the new one.
        Volatile.Write(ref _current, new Lazy<IConfigCatalog>(replacement));

        // Ordered: the catalog first, because ProfileRegistry.For reads its settings inside the
        // Lazy it has already inserted. Invalidating first would let an entry inserted before this
        // write, but evaluated after it, survive the clear holding the outgoing configuration.
        _registry().Invalidate();
    }

    public IServerSettings Default => Current.Default;

    public IReadOnlyCollection<string> ProfileNames => Current.ProfileNames;

    public bool TryGet(string? name, [NotNullWhen(true)] out IServerSettings? settings) =>
        Current.TryGet(name, out settings);

    public IServerSettings Get(string? name) => Current.Get(name);
}
