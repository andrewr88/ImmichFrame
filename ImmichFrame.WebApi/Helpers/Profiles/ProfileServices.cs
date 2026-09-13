using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Logic.AccountSelection;

namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// The long-lived half of one configuration profile's object graph.
/// <para>
/// These are the expensive pieces - HTTP clients, asset pools, API caches, bloom filters - so
/// exactly one set exists per profile for the lifetime of the process. Anything a request needs
/// per-request reaches them through <see cref="ProfileRegistry"/> rather than rebuilding them.
/// </para>
/// <para>
/// It owns its own lifetime instead of being <see cref="IDisposable"/>, and deliberately offers no
/// Dispose(): the graph is shared by every request on the profile, so no holder of one may end it.
/// A request takes a lease through <see cref="ProfileRegistry.Lease"/> and gives it back when its
/// scope ends, while a configuration swap <see cref="Retire"/>s the graph - which stops it being
/// handed out at once, and tears it down when the last lease is released rather than under the
/// requests still inside it.
/// </para>
/// </summary>
public sealed class ProfileServices
{
    public ProfileServices(IServiceProvider root, IServerSettings settings)
    {
        Settings = settings;
        GeneralSettings = settings.GeneralSettings;

        WeatherService = ActivatorUtilities.CreateInstance<OpenWeatherMapService>(root, GeneralSettings);
        CalendarService = ActivatorUtilities.CreateInstance<IcalCalendarService>(root, GeneralSettings);

        // One tracker per profile, deliberately. BloomFilterAssetAccountTracker.ForAsset scans
        // every filter it holds, so a shared instance would let an asset request made against one
        // profile be served by an account belonging to another.
        var tracker = ActivatorUtilities.CreateInstance<BloomFilterAssetAccountTracker>(root);

        var logicFactory = (Func<IAccountSettings, IAccountImmichFrameLogic>)(account =>
            ActivatorUtilities.CreateInstance<PooledImmichFrameLogic>(root, account, GeneralSettings));

        var strategyFactory = (Func<IList<IAccountImmichFrameLogic>, IAccountSelectionStrategy>)(accounts =>
            ActivatorUtilities.CreateInstance<TotalAccountImagesSelectionStrategy>(root, tracker, accounts));

        Logic = ActivatorUtilities.CreateInstance<MultiImmichFrameLogicDelegate>(
            root, settings, logicFactory, strategyFactory);
    }

    public IServerSettings Settings { get; }
    public IGeneralSettings GeneralSettings { get; }
    public IImmichFrameLogic Logic { get; }
    public IWeatherService WeatherService { get; }
    public ICalendarService CalendarService { get; }

    // Leases are taken and given back once per request, so this is nowhere near a hot path: a lock
    // is the right amount of machinery, and it is what makes "retired, and the last lease has gone"
    // one decision rather than two reads that can race. The teardown it decides on always runs
    // outside the lock - tearing a whole object graph down while holding one invites a deadlock.
    private readonly object _gate = new();
    private int _leases;
    private bool _retired;

    /// <summary>
    /// Takes a lease, or reports that there is none to take because a configuration swap has already
    /// retired this graph - in which case the caller wants the graph that replaced it, not this one.
    /// </summary>
    internal bool TryAcquire()
    {
        lock (_gate)
        {
            if (_retired) return false;

            _leases++;
            return true;
        }
    }

    /// <summary>
    /// Gives a lease back. The last one to go tears the graph down if it was retired in the
    /// meantime, which is how a request that outlived a configuration swap ends.
    /// </summary>
    internal void Release()
    {
        bool teardown;

        lock (_gate)
        {
            // Loud, because the alternative is silent: a lease given back twice would drive the
            // count below zero, and no later release could ever bring it back to the zero the
            // teardown below waits for. The graph would stay pinned with nothing left to free it.
            if (_leases == 0) throw new InvalidOperationException("Released a profile lease that was not held.");

            teardown = --_leases == 0 && _retired;
        }

        if (teardown) TearDown();
    }

    /// <summary>
    /// Stops this graph being handed out, and tears it down as soon as nothing holds it - now if it
    /// is unleased, otherwise at the last <see cref="Release"/>. Called by
    /// <see cref="ProfileRegistry"/> when a configuration swap drops this profile's services, never
    /// by a request: these are shared by every request on the profile, so the end of any one
    /// request's scope must not end them.
    /// </summary>
    internal void Retire()
    {
        bool teardown;

        lock (_gate)
        {
            // Only the transition into retirement tears anything down, so being retired twice is
            // harmless - a swap and the orphan check in ProfileRegistry.Lease can both reach the
            // same graph.
            teardown = !_retired && _leases == 0;
            _retired = true;
        }

        if (teardown) TearDown();
    }

    /// <summary>
    /// Tears down the profile's asset pools and their API caches. Private, and reached only through
    /// <see cref="Retire"/> and <see cref="Release"/>: <see cref="Logic"/> is handed to every request
    /// on the profile - wrapped in a <see cref="NonOwningImmichFrameLogic"/>, because the container
    /// captures any disposable a scoped factory returns - and none of them may decide when it ends.
    /// </summary>
    private void TearDown()
    {
        try
        {
            (Logic as IDisposable)?.Dispose();
        }
        catch (Exception)
        {
            // Guarded here rather than at either caller, because the same teardown reaches this from
            // both and neither can do anything about it: a configuration swap must not fail because
            // an outgoing graph objected to being torn down, and nor must the end of the request
            // scope that happened to be holding the last lease on one.
        }
    }
}
