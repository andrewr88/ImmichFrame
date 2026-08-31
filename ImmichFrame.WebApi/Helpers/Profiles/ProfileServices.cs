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
}
