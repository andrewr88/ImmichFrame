namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// The one <see cref="ProfileServices"/> a request works against, leased once when the request
/// first needs it and given back when the request ends.
/// <para>
/// Being <see cref="IDisposable"/> is the mechanism here rather than the hazard. Registered scoped,
/// the container disposes this holder at the end of the request, which is exactly when the request
/// stops using the profile's graph; until then the lease keeps that graph alive, so a configuration
/// swap landing mid-request stops it being handed out to anything new without tearing it down under
/// the requests already inside it - which used to fail with <see cref="ObjectDisposedException"/>
/// out of the account's API cache on the request's next call.
/// </para>
/// <para>
/// What must not be disposable is what this hands out. The container takes ownership of whatever a
/// scoped factory returns, so the graph's shared members go out through non-owning wrappers - see
/// <see cref="NonOwningImmichFrameLogic"/> - and <see cref="ProfileServices"/> offers no Dispose()
/// at all: retiring it is the registry's to decide, and tearing it down is the last lease's.
/// </para>
/// <para>
/// Resolving once per request also keeps a configuration swap landing mid-request from serving that
/// request its settings from the outgoing configuration and its logic from the incoming one.
/// </para>
/// </summary>
public sealed class ProfileScope(ProfileRegistry _registry, ICurrentProfile _profile) : IDisposable
{
    // Nothing promises a scope is used from one thread - a request that fans out resolves from the
    // same scope on several - and "lease it unless it is already leased" has to be one decision
    // rather than a read and a write. Two callers that both found no lease would both take one, and
    // the one whose assignment lost would never be given back: a retired graph pinned, with nothing
    // left that could release it, which is the leak the lease exists to prevent.
    private readonly object _gate = new();
    private ProfileServices? _services;
    private bool _disposed;

    public ProfileServices Services
    {
        get
        {
            lock (_gate)
            {
                // A resolve after the scope ended would take a lease that nothing will ever release,
                // because the release already happened. The container does not do this - it refuses
                // to resolve from a disposed scope at all - but this is a public property on a public
                // type, and that is a container implementation detail to be quietly relying on.
                ObjectDisposedException.ThrowIf(_disposed, this);

                // The lease is taken under the lock, which is also what holds one request to one
                // graph: a swap landing while this is being built cannot let a second caller through
                // to lease the replacement.
                return _services ??= _registry.Lease(_profile.Name);
            }
        }
    }

    public void Dispose()
    {
        ProfileServices? leased;

        lock (_gate)
        {
            // Cleared and marked under the lock, so the lease is handed to exactly one caller: a
            // second Dispose() releases nothing, and over-releasing would tear down a graph other
            // requests are still holding.
            leased = _services;
            _services = null;
            _disposed = true;
        }

        // Outside the lock, deliberately: releasing the last lease on a retired graph tears that
        // whole graph down - asset pools, API caches and all - and that is not work to do while
        // holding a lock.
        leased?.Release();
    }
}
