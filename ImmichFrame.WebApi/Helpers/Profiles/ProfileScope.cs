namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// The one <see cref="ProfileServices"/> a request works against, resolved once when the request
/// first needs it.
/// <para>
/// This exists because <see cref="ProfileServices"/> is <see cref="IDisposable"/> and the request's
/// services are not its owner. The container disposes whatever a scoped factory hands it at the end
/// of the scope, so registering <see cref="ProfileServices"/> scoped would tear down a profile's
/// pools and caches when any one request on that profile finished, while every other request still
/// using them carried on against a disposed graph. This holder is deliberately not disposable, so
/// the container has nothing to dispose; <see cref="ProfileRegistry.Invalidate"/> stays the only
/// thing that ends a profile graph's life.
/// </para>
/// <para>
/// Resolving once per request also keeps a configuration swap landing mid-request from serving that
/// request its settings from the outgoing configuration and its logic from the incoming one.
/// </para>
/// </summary>
public sealed class ProfileScope(ProfileRegistry _registry, ICurrentProfile _profile)
{
    private ProfileServices? _services;

    public ProfileServices Services => _services ??= _registry.For(_profile.Name);
}
