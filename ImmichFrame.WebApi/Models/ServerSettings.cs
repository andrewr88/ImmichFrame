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
    public List<string> Webcalendars { get; set; } = new();
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

    public int? ImagesFromDays { get; set; }
    public DateTime? ImagesFromDate { get; set; }
    public DateTime? ImagesUntilDate { get; set; }
    public List<Guid> Albums { get; set; } = new();
    public List<Guid> ExcludedAlbums { get; set; } = new();
    public List<Guid> People { get; set; } = new();
    public List<string> Tags { get; set; } = new();
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
    }
}
