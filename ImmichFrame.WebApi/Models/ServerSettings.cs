using System.Text.Json.Serialization;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers;
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

    public void Validate()
    {
        GeneralSettings.Validate();

        foreach (var account in Accounts)
        {
            account.ValidateAndInitialize();
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

    public int RefreshAlbumPeopleInterval { get; set; } = 12;
    public string? WeatherApiKey { get; set; } = string.Empty;
    public string? UnitSystem { get; set; } = "imperial";
    public string? WeatherLatLong { get; set; } = "40.7128,-74.0060";
    public string? Webhook { get; set; }
    public string? AuthenticationSecret { get; set; }

    public void Validate() { }
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

    public int? Rating { get; set; }

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
    }
}
