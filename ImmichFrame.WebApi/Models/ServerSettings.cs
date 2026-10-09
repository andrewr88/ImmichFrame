using System.Globalization;
using System.Text.Json.Serialization;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers;
using ImmichFrame.WebApi.Helpers.Config;
using YamlDotNet.Serialization;

namespace ImmichFrame.WebApi.Models;

public class ServerSettings : IServerSettings, IConfigSettable
{
    [YamlMember(Alias = "General")]
    [JsonPropertyName("General")]
    public GeneralSettings? GeneralSettingsImpl { get; set; }

    /// <summary>
    /// Never null, and never holding a null, whatever the settings document says. A configuration with
    /// no accounts is one ImmichFrame runs - the slideshow has nothing to show, and the admin editor is
    /// there to add an account - but a null list is dereferenced on every request, the editor's own
    /// included.
    /// <para>
    /// Normalised in the setter because an initialiser alone only covers a document that leaves the
    /// key out. An explicit <c>"Accounts": null</c> - YAML's bare <c>Accounts:</c> too, and a profile
    /// overriding the inherited list with either - has the binder call the setter with null, which
    /// would overwrite it. One place here, rather than a null check at every reader.
    /// </para>
    /// <para>
    /// An empty entry in the list - <c>[null]</c> in JSON, a bare <c>-</c> in YAML - binds to a null
    /// account, and is dropped here for the same reason: building a configuration's services makes an
    /// account of every entry, so one null failed every request the configuration served. The
    /// accounts either side of it run as they would without it. Dropping it is not silent -
    /// <c>SettingsService</c> warns about each one at startup - and the admin editor never writes one
    /// back, so the next save removes it from the stored settings.
    /// </para>
    /// </summary>
    [YamlMember(Alias = "Accounts")]
    [JsonPropertyName("Accounts")]
    public IEnumerable<ServerAccountSettings> AccountsImpl
    {
        get => _accounts;
        set => _accounts = value?.Where(account => account is not null).ToList() ?? [];
    }

    private IEnumerable<ServerAccountSettings> _accounts = [];

    //Covariance not allowed on interface impls
    [JsonIgnore]
    [YamlIgnore]
    public IGeneralSettings GeneralSettings => GeneralSettingsImpl ?? new GeneralSettings();

    [JsonIgnore]
    [YamlIgnore]
    public IEnumerable<IAccountSettings> Accounts => AccountsImpl;

    /// <summary>
    /// Validates the general settings and every account, each whatever the ones before it threw -
    /// validating an account is also what reads its ApiKeyFile into its key, so one that failed used
    /// to leave every account after it with none. Every failure is then thrown at once, in an
    /// <see cref="AggregateSettingsNotValidException"/>.
    /// </summary>
    public void Validate()
    {
        var failures = new List<Exception>();

        Collect(GeneralSettings.Validate);

        foreach (var account in Accounts)
        {
            Collect(account.ValidateAndInitialize);
        }

        if (failures.Count > 0) throw new AggregateSettingsNotValidException(failures);

        void Collect(Action validate)
        {
            try
            {
                validate();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
    }
}

public class GeneralSettings : IGeneralSettings, IConfigSettable
{
    public bool DownloadImages { get; set; } = false;
    public string Language { get; set; } = "en";
    public string? ImageLocationFormat { get; set; } = "City,State,Country";
    public string? PhotoDateFormat { get; set; } = "MM/dd/yyyy";
    public int Interval { get; set; } = 45;
    public double TransitionDuration { get; set; } = 1;
    public bool ShowClock { get; set; } = true;
    public string? ClockFormat { get; set; } = "hh:mm";
    public string? ClockDateFormat { get; set; } = "eee, MMM d";
    public bool ShowProgressBar { get; set; } = true;
    public bool ShowPhotoDate { get; set; } = true;
    public bool ShowImageDesc { get; set; } = true;
    public bool ShowPeopleDesc { get; set; } = true;
    public bool ShowTagsDesc { get; set; } = true;
    public bool ShowAlbumName { get; set; } = true;
    public bool ShowImageLocation { get; set; } = true;
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }
    public string Style { get; set; } = "none";
    public string? BaseFontSize { get; set; }
    public bool ShowWeatherDescription { get; set; } = true;
    public string? WeatherIconUrl { get; set; } = "https://openweathermap.org/img/wn/{IconId}.png";
    public bool ImageZoom { get; set; } = true;
    public bool ImagePan { get; set; } = false;
    public bool ImageFill { get; set; } = false;
    public bool PlayAudio { get; set; } = false;
    public string Layout { get; set; } = "splitview";
    public int RenewImagesDuration { get; set; } = 30;

    /// <summary>
    /// Never null, and normalised in the setter for the reason <see cref="ServerSettings.AccountsImpl"/>
    /// is: <c>"Webcalendars": null</c>, or YAML's bare <c>Webcalendars:</c>, has the binder call the
    /// setter with null over the initialiser. The calendar service enumerates this list on every poll,
    /// so a null here answered every <c>GET /api/Calendar</c> with a 500 - for a frame whose settings
    /// only say it has no calendars.
    /// <para>
    /// An empty entry - <c>[null]</c> in JSON, a bare <c>-</c> in YAML - is dropped here too. It binds
    /// as a null URL, which the service failed the whole poll on rather than that one calendar, and
    /// an entry that is only whitespace names no calendar either. Nothing is lost by dropping them,
    /// and the admin editor never writes one back.
    /// </para>
    /// </summary>
    public List<string> Webcalendars
    {
        get => _webcalendars;
        set => _webcalendars = value?.Where(url => !string.IsNullOrWhiteSpace(url)).ToList() ?? [];
    }

    private List<string> _webcalendars = [];

    /// <summary>
    /// How many hours an account's albums and people stay cached before they are fetched again. At 0,
    /// they are fetched again for every request. Bounded by <see cref="MaxRefreshAlbumPeopleInterval"/>.
    /// </summary>
    public int RefreshAlbumPeopleInterval { get; set; } = 12;

    /// <summary>
    /// The largest <see cref="RefreshAlbumPeopleInterval"/> allowed: ten years, in hours. That is far
    /// longer than any frame needs to cache its albums and people, and far below either failure
    /// point. <c>int.MaxValue</c> hours is more than a <see cref="TimeSpan"/> can hold. A hundred
    /// million hours fits in one, but is further ahead than the cache can set an expiry. Either value
    /// failed every request for the configuration's photos. The admin editor applies the same bound,
    /// in <c>admin-config.ts</c>.
    /// </summary>
    public const int MaxRefreshAlbumPeopleInterval = 87600;

    public string? WeatherApiKey { get; set; } = string.Empty;
    public string? UnitSystem { get; set; } = "imperial";
    public string? WeatherLatLong { get; set; } = "40.7128,-74.0060";
    public string? Webhook { get; set; }
    public string? AuthenticationSecret { get; set; }

    public void Validate()
    {
        // Trimmed for the same reason an account's API key is: a secret is often pasted with the line
        // break it was copied with, and a YAML block scalar keeps the line break it ends on. The
        // frame's handler trims the bearer token a frame sends, so a stored secret with whitespace
        // around it matched no token any frame could send, and every frame was refused. This runs
        // first, because stored settings that fail validation are run anyway, and they have to run
        // with the trimmed secret. The weather API key is pasted the same way, and OpenWeatherMap takes
        // it in its query string - whitespace and all - and refuses every request made with it.
        AuthenticationSecret = AuthenticationSecret?.Trim();
        WeatherApiKey = WeatherApiKey?.Trim();

        // A negative number is not a number of hours. The upper bound is explained on the constant.
        if (RefreshAlbumPeopleInterval is < 0 or > MaxRefreshAlbumPeopleInterval)
        {
            throw new SettingsNotValidException(
                $"RefreshAlbumPeopleInterval is {RefreshAlbumPeopleInterval}, and has to be a number of hours between 0, " +
                $"to fetch albums and people again for every request, and {MaxRefreshAlbumPeopleInterval}, which is ten years.");
        }
    }
}

public class ServerAccountSettings : IAccountSettings, IConfigSettable
{
    /// <summary>
    /// A human name for this account, purely so a person - and the configuration editor - can tell
    /// one from another. Nothing in ImmichFrame selects, logs or routes by it.
    /// <para>
    /// It exists because nothing else in the file identifies an account across a reload. An API key
    /// is never sent to the browser, and <see cref="ImmichServerUrl"/> is not an identity either:
    /// two accounts may legitimately be two users on one Immich server. Deliberately not on
    /// <see cref="IAccountSettings"/> - this is editor metadata that happens to live in the settings
    /// file, and nothing in ImmichFrame.Core would ever read it.
    /// </para>
    /// </summary>
    public string? Label { get; set; }

    public string ImmichServerUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string? ApiKeyFile { get; set; } = null;
    public bool ShowMemories { get; set; } = false;
    public bool ShowFavorites { get; set; } = false;
    public bool ShowArchived { get; set; } = false;
    public bool ShowVideos { get; set; } = false;

    /// <summary>
    /// How many days back from today the account's photos may be from. Taken off today's midnight on
    /// every request for them, so 0 is today's photos alone; bounded by <see cref="MaxImagesFromDays"/>.
    /// </summary>
    public int? ImagesFromDays { get; set; }

    /// <summary>
    /// The most <see cref="ImagesFromDays"/> may be: a century. A cut-off that far back already
    /// reaches past nearly every photo a library holds - <see cref="ImagesFromDate"/> is there for one
    /// further back, and leaving the setting out is no limit at all - and it is far inside what
    /// <see cref="DateTime"/> can count back from any date the server's clock could show. A number it
    /// cannot, <c>int.MaxValue</c> among them, threw on every request for the account's photos. The
    /// admin editor holds the same bound, in <c>admin-config.ts</c>.
    /// </summary>
    public const int MaxImagesFromDays = 36500;

    public DateTime? ImagesFromDate { get; set; }
    public DateTime? ImagesUntilDate { get; set; }

    /// <summary>
    /// Never null, and normalised in the setter for the reason <see cref="ServerSettings.AccountsImpl"/>
    /// is: an explicit null - <c>"Albums": null</c>, YAML's bare <c>Albums:</c> - has the binder call the
    /// setter with it over the initialiser. ImmichFrame.Core reads the account's four lists through
    /// <see cref="IAccountSettings"/>, which says they are never null; it guards them anyway, since it
    /// takes any implementation, but these settings keep that promise rather than lean on the guards.
    /// </summary>
    public List<Guid> Albums
    {
        get => _albums;
        set => _albums = value ?? [];
    }

    private List<Guid> _albums = [];

    /// <inheritdoc cref="Albums"/>
    public List<Guid> ExcludedAlbums
    {
        get => _excludedAlbums;
        set => _excludedAlbums = value ?? [];
    }

    private List<Guid> _excludedAlbums = [];

    /// <inheritdoc cref="Albums"/>
    public List<Guid> People
    {
        get => _people;
        set => _people = value ?? [];
    }

    private List<Guid> _people = [];

    /// <summary>
    /// Never null, for the reason <see cref="Albums"/> is not, and never holding an empty entry
    /// either. A null in JSON or a bare <c>-</c> in YAML binds as a null tag, and <c>TagAssetsPool</c>
    /// looks every tag up as a dictionary key, so one failed every request for the account's photos;
    /// an entry that is only whitespace names no tag either. The three lists of ids cannot hold such
    /// an entry: both binders refuse an empty one where a Guid belongs, so the file never loads.
    /// </summary>
    public List<string> Tags
    {
        get => _tags;
        set => _tags = value?.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList() ?? [];
    }

    private List<string> _tags = [];

    /// <summary>
    /// The exact star rating of the photos the account shows, between <see cref="MinRating"/> and
    /// <see cref="MaxRating"/>. Leave it out to show photos of any rating.
    /// </summary>
    public int? Rating { get; set; }

    /// <summary>
    /// The range of <see cref="Rating"/>: 1 to 5 stars. Immich v3, the oldest ImmichFrame supports, no
    /// longer rates a photo -1 or 0, and its search refuses either. Photos are matched on the exact rating, so a number outside the range matched no photo, and the
    /// frame showed nothing without saying why. The admin editor applies the same bound, in
    /// <c>admin-config.ts</c>.
    /// </summary>
    public const int MinRating = 1;

    /// <inheritdoc cref="MinRating"/>
    public const int MaxRating = 5;

    public void ValidateAndInitialize()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyFile))
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                throw new SettingsNotValidException("Cannot specify both ApiKey and ApiKeyFile. Please provide only one.");
            }
            ApiKey = File.ReadAllText(ApiKeyFile).Trim();
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new SettingsNotValidException("Either ApiKey or ApiKeyFile must be provided.");
        }

        // Trimmed as a key file's contents are: a key written into the settings arrives with the line
        // break it was copied with just as readily, and a YAML block scalar keeps the one it ends on.
        // The key becomes an X-API-KEY header, and HttpHeaders.Add throws FormatException on a line
        // break - in the constructor of the account's PooledImmichFrameLogic, inside ProfileRegistry's
        // Lazy, which caches the exception: every request for the profile would fail until the
        // configuration was replaced or the process restarted.
        ApiKey = ApiKey.Trim();

        // What trimming leaves is refused here, as a message, rather than left to be that exception.
        // char.IsControl as AdminImmichAccounts applies it: a little stricter than the header rules,
        // and no Immich API key contains one. The key itself is not quoted - this message is logged,
        // and shown in the admin editor.
        if (ApiKey.Any(char.IsControl))
        {
            throw new SettingsNotValidException(
                "An account's API key contains a character an HTTP header cannot carry, such as a line break. " +
                "Copy the key again from Immich into the settings - or into the file the account's ApiKeyFile names.");
        }

        // After the key, so that an account refused here has still been given the one its file holds:
        // stored settings that fail validation are run regardless, and a number of days past the bound
        // but short of what DateTime refuses is one that account goes on serving photos with. A
        // negative number is refused with the rest - it moves the cut-off into the future, where no
        // photo is, and the frame shows nothing without saying why.
        if (ImagesFromDays is < 0 or > MaxImagesFromDays)
        {
            throw new SettingsNotValidException(
                $"An account's ImagesFromDays is {ImagesFromDays}, and has to be a number of days back from today " +
                $"between 0, for today's photos alone, and {MaxImagesFromDays}. Use ImagesFromDate for a cut-off " +
                "further back than that, or leave ImagesFromDays out for no limit.");
        }

        if (Rating is < MinRating or > MaxRating)
        {
            throw new SettingsNotValidException(
                $"An account's Rating is {Rating}, and has to be a rating Immich gives a photo: between {MinRating} " +
                $"and {MaxRating} stars. Leave Rating out to show photos of any rating.");
        }

        // The account shows the photos taken from ImagesFromDate up to ImagesUntilDate, so a from date
        // later than the until date leaves no photo between them. ImagesFromDate takes precedence over
        // ImagesFromDays when both are set, so this order is refused whatever ImagesFromDays says.
        if (ImagesFromDate is { } fromDate && ImagesUntilDate is { } untilDate && fromDate > untilDate)
        {
            throw new SettingsNotValidException(
                $"An account's ImagesFromDate, {Written(fromDate)}, is later than its ImagesUntilDate, " +
                $"{Written(untilDate)}, so no photo can fall between them. Swap the two dates, or leave one of them out.");
        }
    }

    /// <summary>
    /// A date as a message spells it: the day alone, unless it has a time of day as well. The time
    /// matters when both dates fall on the same day, which would otherwise read as one date being
    /// later than itself.
    /// </summary>
    private static string Written(DateTime date) => date.ToString(
        date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
