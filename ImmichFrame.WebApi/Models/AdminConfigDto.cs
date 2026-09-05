using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.WebApi.Models;

/// <summary>
/// How the admin API says "this value is a secret the browser is not being given".
/// <para>
/// A save must never be able to blank a secret the editor never received, so a value that is absent
/// or still carries <see cref="Placeholder"/> means "keep whatever is on disk". Clearing a secret is
/// therefore a deliberate act with its own value: the empty string.
/// </para>
/// </summary>
public static class AdminSecret
{
    /// <summary>What a masked field carries back if the editor sends its form fields verbatim.</summary>
    public const string Placeholder = "********";

    /// <summary>Whether <paramref name="value"/> asks for the stored secret to be left alone.</summary>
    public static bool KeepsStored(string? value) =>
        value is null || string.Equals(value, Placeholder, StringComparison.Ordinal);
}

/// <summary>
/// The whole editable configuration: the default configuration and every profile, as the admin
/// editor reads it and as it sends it back.
/// </summary>
/// <param name="Version">
/// A hash of the settings file as it was read. A <c>PUT</c> carrying a stale one is refused, so two
/// administrators - or one administrator and a hand edit - cannot silently overwrite each other.
/// </param>
/// <param name="Source">Where the configuration lives, and whether it can be written at all.</param>
/// <param name="Default">The default configuration, which every profile inherits from.</param>
/// <param name="Profiles">The named profiles, in the order the file declares them.</param>
public record AdminConfigDto(
    string Version,
    AdminConfigSourceDto Source,
    AdminConfigEntryDto Default,
    IReadOnlyList<AdminConfigEntryDto> Profiles);

/// <summary>
/// What the editor needs to know before it offers a save button.
/// </summary>
/// <param name="Format">"json", "yaml", or "environment" when there is no file.</param>
/// <param name="Path">The settings file, or null when the configuration came from the environment.</param>
/// <param name="LegacySchema">
/// The file only loaded through the v1 reader. Saving rewrites it in the current schema, so a
/// <c>PUT</c> has to set <see cref="AdminConfigUpdateDto.ConvertLegacySchema"/> to say so on purpose.
/// </param>
/// <param name="Editable">False when there is no file to write.</param>
/// <param name="NotEditableReason">Why not, in words an operator can act on.</param>
public record AdminConfigSourceDto(
    string Format,
    string? Path,
    bool LegacySchema,
    bool Editable,
    string? NotEditableReason);

/// <summary>
/// One configuration - the default one or a single profile - as values plus provenance.
/// </summary>
/// <param name="Name">The profile name, or <c>default</c> for the default configuration.</param>
/// <param name="DeclaredKeys">
/// The keys this configuration <em>declares</em>, as <c>General.ShowClock</c> and <c>Accounts</c>
/// paths. Everything else in <paramref name="General"/> and <paramref name="Accounts"/> is inherited
/// from the default configuration, or is the setting's built-in default.
/// <para>
/// This is the field that keeps profiles sparse. A save writes exactly these keys and nothing else,
/// so round-tripping a profile unchanged leaves it overriding exactly what it overrode before -
/// where writing back the merged result would turn every inherited value into an override and
/// quietly cut the profile off from later edits to the default configuration.
/// </para>
/// </summary>
/// <param name="General">The merged, effective general settings, with secrets masked.</param>
/// <param name="Accounts">The merged, effective accounts, with API keys masked.</param>
public record AdminConfigEntryDto(
    string Name,
    IReadOnlyList<string> DeclaredKeys,
    AdminGeneralSettingsDto General,
    IReadOnlyList<AdminAccountSettingsDto> Accounts);

/// <summary>
/// A save of the whole configuration. Profiles are replaced wholesale: a name that is new is
/// created, and a profile the request leaves out is deleted.
/// </summary>
/// <param name="Version">The <see cref="AdminConfigDto.Version"/> this edit started from.</param>
/// <param name="ConvertLegacySchema">
/// Consent to rewrite a v1 settings file in the current schema. Required, because doing it silently
/// would rewrite a file the administrator never asked to convert.
/// </param>
public record AdminConfigUpdateDto(
    string Version,
    AdminConfigEntryDto Default,
    IReadOnlyList<AdminConfigEntryDto> Profiles,
    bool ConvertLegacySchema = false);

/// <summary>
/// <see cref="GeneralSettings"/> as the admin surface sees it: every value, minus the three secrets,
/// which are reported as present or absent instead of being handed to the browser.
/// <para>
/// Nullable throughout, because this type is also the request shape. A save writes only the keys
/// named in <see cref="AdminConfigEntryDto.DeclaredKeys"/>, so a property left null on a key nobody
/// declares is simply never read.
/// </para>
/// </summary>
public class AdminGeneralSettingsDto
{
    public AdminGeneralSettingsDto()
    {
    }

    public AdminGeneralSettingsDto(IGeneralSettings settings)
    {
        Interval = settings.Interval;
        TransitionDuration = settings.TransitionDuration;
        DownloadImages = settings.DownloadImages;
        RenewImagesDuration = settings.RenewImagesDuration;
        ShowClock = settings.ShowClock;
        ClockFormat = settings.ClockFormat;
        ClockDateFormat = settings.ClockDateFormat;
        ShowPhotoDate = settings.ShowPhotoDate;
        ShowProgressBar = settings.ShowProgressBar;
        PhotoDateFormat = settings.PhotoDateFormat;
        ShowImageDesc = settings.ShowImageDesc;
        ShowPeopleDesc = settings.ShowPeopleDesc;
        ShowTagsDesc = settings.ShowTagsDesc;
        ShowAlbumName = settings.ShowAlbumName;
        ShowImageLocation = settings.ShowImageLocation;
        ImageLocationFormat = settings.ImageLocationFormat;
        PrimaryColor = settings.PrimaryColor;
        SecondaryColor = settings.SecondaryColor;
        Style = settings.Style;
        BaseFontSize = settings.BaseFontSize;
        ShowWeatherDescription = settings.ShowWeatherDescription;
        WeatherIconUrl = settings.WeatherIconUrl;
        ImageZoom = settings.ImageZoom;
        ImagePan = settings.ImagePan;
        ImageFill = settings.ImageFill;
        PlayAudio = settings.PlayAudio;
        Layout = settings.Layout;
        Language = settings.Language;
        Webcalendars = settings.Webcalendars;
        RefreshAlbumPeopleInterval = settings.RefreshAlbumPeopleInterval;
        WeatherLatLong = settings.WeatherLatLong;
        UnitSystem = settings.UnitSystem;

        HasWeatherApiKey = !string.IsNullOrEmpty(settings.WeatherApiKey);
        HasWebhook = !string.IsNullOrEmpty(settings.Webhook);
        HasAuthenticationSecret = !string.IsNullOrEmpty(settings.AuthenticationSecret);
    }

    public int? Interval { get; set; }
    public double? TransitionDuration { get; set; }
    public bool? DownloadImages { get; set; }
    public int? RenewImagesDuration { get; set; }
    public bool? ShowClock { get; set; }
    public string? ClockFormat { get; set; }
    public string? ClockDateFormat { get; set; }
    public bool? ShowPhotoDate { get; set; }
    public bool? ShowProgressBar { get; set; }
    public string? PhotoDateFormat { get; set; }
    public bool? ShowImageDesc { get; set; }
    public bool? ShowPeopleDesc { get; set; }
    public bool? ShowTagsDesc { get; set; }
    public bool? ShowAlbumName { get; set; }
    public bool? ShowImageLocation { get; set; }
    public string? ImageLocationFormat { get; set; }
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }
    public string? Style { get; set; }
    public string? BaseFontSize { get; set; }
    public bool? ShowWeatherDescription { get; set; }
    public string? WeatherIconUrl { get; set; }
    public bool? ImageZoom { get; set; }
    public bool? ImagePan { get; set; }
    public bool? ImageFill { get; set; }
    public bool? PlayAudio { get; set; }
    public string? Layout { get; set; }
    public string? Language { get; set; }
    public List<string>? Webcalendars { get; set; }
    public int? RefreshAlbumPeopleInterval { get; set; }
    public string? WeatherLatLong { get; set; }
    public string? UnitSystem { get; set; }

    /// <summary>Never populated on a read. On a save: absent or <see cref="AdminSecret.Placeholder"/>
    /// keeps the stored key, the empty string clears it, anything else replaces it.</summary>
    public string? WeatherApiKey { get; set; }

    /// <inheritdoc cref="WeatherApiKey"/>
    public string? Webhook { get; set; }

    /// <inheritdoc cref="WeatherApiKey"/>
    public string? AuthenticationSecret { get; set; }

    public bool HasWeatherApiKey { get; set; }
    public bool HasWebhook { get; set; }
    public bool HasAuthenticationSecret { get; set; }
}

/// <summary>
/// <see cref="ServerAccountSettings"/> as the admin surface sees it, with the API key masked.
/// </summary>
public class AdminAccountSettingsDto
{
    public AdminAccountSettingsDto()
    {
    }

    public AdminAccountSettingsDto(IAccountSettings settings)
    {
        ImmichServerUrl = settings.ImmichServerUrl;
        ApiKeyFile = settings.ApiKeyFile;
        ShowMemories = settings.ShowMemories;
        ShowFavorites = settings.ShowFavorites;
        ShowArchived = settings.ShowArchived;
        ShowVideos = settings.ShowVideos;
        ImagesFromDays = settings.ImagesFromDays;
        ImagesFromDate = settings.ImagesFromDate;
        ImagesUntilDate = settings.ImagesUntilDate;
        Albums = settings.Albums;
        ExcludedAlbums = settings.ExcludedAlbums;
        People = settings.People;
        Tags = settings.Tags;
        Rating = settings.Rating;

        // Answered from the settings as the file spells them, which is the honest answer but not the
        // obvious one: these are bound without ValidateAndInitialize, so an account whose key lives in
        // an ApiKeyFile has an empty ApiKey here. Reporting that as "no key" would show an account
        // that works as one that is not configured, and invite an administrator to type a key it
        // cannot accept alongside the file.
        ApiKeyFromFile = !string.IsNullOrWhiteSpace(settings.ApiKeyFile);
        HasApiKey = ApiKeyFromFile || !string.IsNullOrEmpty(settings.ApiKey);
    }

    /// <summary>
    /// Which stored account this one is, so that a save can put a masked API key back where it came
    /// from. Opaque, issued by the read, and to be echoed back unchanged.
    /// <para>
    /// Position is deliberately not an identity. A declared <c>Accounts</c> list replaces the
    /// inherited one outright, so deleting the first of two accounts shifts every account that
    /// follows it - and matching a masked key by position would then write the deleted account's key
    /// under the surviving account's server URL, sending one Immich server another server's
    /// credential. Nor is <see cref="ImmichServerUrl"/> an identity: two accounts may legitimately be
    /// two users on the same Immich server.
    /// </para>
    /// <para>
    /// Null on an account a profile is inheriting rather than declaring, and on one the editor has
    /// just added: neither has a stored key to keep, so both have to be given one outright.
    /// </para>
    /// </summary>
    public string? Id { get; set; }

    public string? ImmichServerUrl { get; set; }

    /// <summary>
    /// Never populated on a read. On a save: absent or <see cref="AdminSecret.Placeholder"/> keeps the
    /// stored key named by <see cref="Id"/>, and anything else replaces it.
    /// <para>
    /// Unlike the three secrets on <see cref="AdminGeneralSettingsDto"/>, the empty string does not
    /// clear this one so much as attempt to: an account with neither a key nor an
    /// <see cref="ApiKeyFile"/> cannot reach Immich, so the save is then refused by validation. Remove
    /// the account, or point it at a key file, rather than emptying this.
    /// </para>
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Whether this account has an API key at all, by either means.</summary>
    public bool HasApiKey { get; set; }

    /// <summary>
    /// Whether that key comes from <see cref="ApiKeyFile"/> rather than being stored in the settings
    /// file. Such an account has no key of its own to type, keep or clear here: it is read from that
    /// path at load time, and the loader refuses a file naming both.
    /// </summary>
    public bool ApiKeyFromFile { get; set; }

    public string? ApiKeyFile { get; set; }
    public bool? ShowMemories { get; set; }
    public bool? ShowFavorites { get; set; }
    public bool? ShowArchived { get; set; }
    public bool? ShowVideos { get; set; }
    public int? ImagesFromDays { get; set; }
    public DateTime? ImagesFromDate { get; set; }
    public DateTime? ImagesUntilDate { get; set; }
    public List<Guid>? Albums { get; set; }
    public List<Guid>? ExcludedAlbums { get; set; }
    public List<Guid>? People { get; set; }
    public List<string>? Tags { get; set; }
    public int? Rating { get; set; }
}
