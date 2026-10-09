using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.WebApi.Database;
using ImmichFrame.WebApi.Helpers;
using ImmichFrame.WebApi.Helpers.Config;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;

namespace ImmichFrame.WebApi.Services;

/// <summary>
/// The settings document as it is stored, in the three parts a caller needs to edit it: the text
/// itself, the format that text is written in, and the version token a save has to present.
/// </summary>
/// <param name="Text">
/// The whole document, <c>Profiles</c> included. Stored as text rather than as a bound
/// <c>ServerSettings</c> on purpose: a profile declares only the keys it overrides, and binding it
/// to an object would materialise every inherited value into the profile, so the next save would
/// write out a configuration that no longer inherits anything.
/// </param>
public sealed record StoredSettings(string Text, ConfigFormat Format, long Version);

/// <summary>
/// Owns the stored configuration. The SQLite database in the configuration directory is the source
/// of truth; an existing <c>Settings.json</c>/<c>Settings.yml</c> is imported into it once, on the
/// first run that finds no row, and ignored from then on.
/// <para>
/// Deliberately a store of one document rather than of one <c>ServerSettings</c>: this repository
/// serves a catalog - a default configuration plus any number of named profiles - and the profile
/// overlay only survives a round trip while the document stays text. <see cref="LoadCatalog"/> is
/// what turns that text into the catalog everything else injects.
/// </para>
/// </summary>
public class SettingsService(
    IDbContextFactory<SettingsDbContext> _dbFactory,
    ConfigLoader _configLoader,
    ConfigLocation _location,
    ILogger<SettingsService> _logger)
{
    /// <summary>SQLITE_CANTOPEN: the database file could not be opened, almost always a permission problem.</summary>
    private const int SqliteCantOpen = 14;

    /// <summary>The row the settings document lives in. There is exactly one.</summary>
    private const int DocumentId = 1;

    /// <summary>
    /// An instance with no stored document and no file to import: nothing has ever configured it.
    /// The admin editor uses this to tell a fresh install from one whose configuration is simply
    /// empty.
    /// </summary>
    public bool IsUnconfigured { get; private set; }

    /// <summary>
    /// Migrates the database and, on the first run only, imports an existing settings file into it.
    /// Runs before the catalog is first seeded, so <see cref="LoadCatalog"/> always reads a database
    /// that is up to date.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_location.Directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw ConfigPathNotWritable(ex);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        try
        {
            await db.Database.MigrateAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCantOpen)
        {
            throw ConfigPathNotWritable(ex);
        }

        if (await db.SettingsDocuments.FindAsync(DocumentId) is { } row)
        {
            LogIgnoredFileConfig();
            _logger.LogInformation("Loaded settings from the database (last updated {updatedAt:u})", row.UpdatedAtUtc);
            return;
        }

        await ImportOrBootstrap(db);
    }

    /// <summary>
    /// The stored configuration, bound and validated into the catalog everything else injects. This
    /// is the seed <see cref="SwappableConfigCatalog"/> runs on first use.
    /// <para>
    /// Nothing the stored document says makes this throw. Settings that do not validate are run as
    /// they are, and settings that cannot even be bound are run as no configuration at all, each with
    /// a Critical line: a seed that throws fails every request it is run for, the admin editor's own
    /// included. What still throws is the database failing to answer, which may pass - and the
    /// catalog runs the seed again on the next access.
    /// </para>
    /// </summary>
    public IConfigCatalog LoadCatalog()
    {
        var stored = Read();

        // An unconfigured instance has no document to bind. An empty catalog rather than a throw:
        // the slideshow reports that nothing is configured and the admin editor is reachable to fix
        // it, which is the whole point of starting without a configuration.
        if (IsUnconfigured) return Unconfigured();

        IConfigDocument document;
        ConfigCatalog catalog;

        try
        {
            document = ConfigLoader.CreateDocument(stored.Format, stored.Text);
            catalog = ConfigLoader.BuildCatalog(document);
        }
        catch (Exception ex)
        {
            // Stored settings that no longer bind - a profile named after a path a later version
            // reserved, say - hold no configuration that could run even in part, unlike settings
            // that only fail validation. So they are served as an instance nothing has configured:
            // no account, so no photo, and no calendar, weather key or webhook either - which is
            // also why the AuthenticationSecret they lose guards nothing. Thrown, the failure was
            // the same on every request, since the same document fails the same way however often
            // it is read.
            _logger.LogCritical(
                "Stored settings could not be loaded ({message}). ImmichFrame is running as if nothing were configured - correct them in the admin editor at /admin.",
                ex.Message);

            return Unconfigured();
        }

        WarnAboutEmptyAccountEntries(document);

        try
        {
            catalog.Validate();
        }
        catch (Exception ex)
        {
            // Stored settings that no longer validate still have to start: the editor that fixes
            // them is served by this very process. Logged loudly rather than thrown.
            _logger.LogCritical(
                "Stored settings are not valid: {message}. ImmichFrame is running with them anyway - fix them in the admin editor at /admin.",
                ex.Message);
        }

        return catalog;
    }

    /// <summary>The catalog of an instance nothing has configured: the defaults, and no account.</summary>
    private static ConfigCatalog Unconfigured() => new(new Models.ServerSettings
    {
        GeneralSettingsImpl = new Models.GeneralSettings(),
        AccountsImpl = []
    });

    /// <summary>
    /// Warns about every empty entry - a null in JSON, a bare <c>-</c> in YAML - in an account list the
    /// stored document declares. <c>ServerSettings</c> drops each one, so the frame runs on the
    /// accounts around it; this is what keeps that from happening silently.
    /// <para>
    /// A warning rather than a validation failure, because the entry is already ignored: the frame
    /// runs on the accounts either side of it exactly as it would without it, so holding one does not
    /// make the stored settings invalid, and the Critical line stays for whatever actually does.
    /// </para>
    /// <para>
    /// Read off what each configuration declares rather than off the bound settings, because a
    /// profile that declares no account list binds the default configuration's: the bound lists would
    /// blame every such profile for the default configuration's empty entries.
    /// </para>
    /// </summary>
    private void WarnAboutEmptyAccountEntries(IConfigDocument document)
    {
        string?[] configurations = [null, .. document.ProfileNames];

        foreach (var name in configurations)
        {
            JsonObject declared;
            try
            {
                declared = document.DeclaredOverrides(name);
            }
            catch (SettingsNotValidException)
            {
                // A YAML document using anchors, which the declared view refuses to project. The
                // admin editor says so itself; here it only means there is nothing to look at.
                continue;
            }

            if (declared[ConfigKeys.Accounts] is not JsonArray accounts) continue;

            for (var index = 0; index < accounts.Count; index++)
            {
                if (accounts[index] is not null) continue;

                _logger.LogWarning(
                    "Accounts entry {position} in {configuration} is empty, so it is ignored. Saving the configuration in the admin editor at /admin removes it.",
                    index + 1,
                    name is null ? "the default configuration" : $"configuration profile '{name}'");
            }
        }
    }

    /// <summary>The stored document, for the admin editor to read and write back.</summary>
    public StoredSettings Read()
    {
        using var db = _dbFactory.CreateDbContext();
        var row = db.SettingsDocuments.Find(DocumentId);

        return row is null
            ? new StoredSettings("{}", ConfigFormat.Json, 0)
            : new StoredSettings(row.Json, ParseFormat(row.Format), row.Version);
    }

    /// <summary>
    /// Replaces the stored document. <paramref name="expectedVersion"/> is the version the caller
    /// read; a mismatch means somebody else saved in between and the write is refused rather than
    /// silently overwriting them.
    /// </summary>
    /// <exception cref="ConfigSaveRefusedException">The stored document has moved on.</exception>
    public long Save(string text, ConfigFormat format, long expectedVersion)
    {
        using var db = _dbFactory.CreateDbContext();
        var row = db.SettingsDocuments.Find(DocumentId);

        if ((row?.Version ?? 0) != expectedVersion)
        {
            throw new ConfigSaveRefusedException(
                "The settings have changed since they were loaded into the editor. Reload the configuration and apply your changes again.");
        }

        if (row is null)
        {
            row = new SettingsDocument { Id = DocumentId };
            db.SettingsDocuments.Add(row);
        }

        row.Json = text;
        row.Format = FormatName(format);
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.Version++;

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The EF concurrency token caught a writer this method's own check did not - two
            // processes against one database file, rather than two requests in one process.
            throw new ConfigSaveRefusedException(
                "The settings were changed by another process while this save was in flight. Reload the configuration and apply your changes again.",
                ex);
        }

        IsUnconfigured = false;
        return row.Version;
    }

    private async Task ImportOrBootstrap(SettingsDbContext db)
    {
        ConfigSource source;
        try
        {
            source = _configLoader.DescribeSource(_location.Directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "No configuration to import ({message}). Starting unconfigured - use the admin editor at /admin to set ImmichFrame up.",
                ex.Message);
            IsUnconfigured = true;
            return;
        }

        if (source.Format == ConfigFormat.Environment || source.Path is null)
        {
            _logger.LogWarning(
                "Configuration from environment variables is no longer supported and was not imported. Starting unconfigured - use the admin editor at /admin, or mount a Settings.json to import.");
            IsUnconfigured = true;
            return;
        }

        var text = await File.ReadAllTextAsync(source.Path);
        var format = source.Format;

        if (source.LegacySchema)
        {
            // A v1 file is converted on the way in rather than stored as it stands. Everything that
            // reads the document afterwards - the catalog, the editor - speaks the current schema
            // only, and a stored v1 blob would have to be recognised and converted at every one of
            // those call sites instead of here, once. The file on disk is not touched.
            text = ConvertLegacy(text, source.Format);
            format = ConfigFormat.Json;
            _logger.LogInformation("'{path}' is written in the old settings schema and was converted on import.", source.Path);
        }

        db.SettingsDocuments.Add(new SettingsDocument
        {
            Id = DocumentId,
            Json = text,
            Format = FormatName(format),
            UpdatedAtUtc = DateTime.UtcNow,
            ImportedFrom = Path.GetFileName(source.Path),
            Version = 1
        });
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Imported '{path}' into the settings database. The database is now the source of truth; further changes to that file are ignored.",
            source.Path);
        _logger.LogWarning(
            "DEPRECATED: configuring ImmichFrame through '{path}' will be removed in a future version. The import above runs once - manage your settings in the admin editor at /admin from now on.",
            source.Path);
    }

    /// <summary>
    /// Rewrites a v1 settings file as a current-schema JSON document.
    /// <para>
    /// Copied property by property off the adapter's interfaces rather than by serialising the
    /// adapter itself: <c>ServerSettingsV1Adapter</c> exposes computed properties with no setters
    /// and a shape the current binder does not read, so a straight serialisation would produce a
    /// document that binds back to defaults. The interface property names are the schema, and the
    /// current settings classes implement the same interfaces, so a name match is the mapping.
    /// </para>
    /// <para>
    /// Written as sparsely as the admin editor writes an account: a setting is stored only where it
    /// differs from today's built-in default. Storing every one recorded each default as a choice the
    /// file had made - the empty weather key among them - so no later change to a default could reach
    /// an installation that started from a v1 file. The frame runs on the same values either way,
    /// since a setting left out binds to the very default it was equal to.
    /// </para>
    /// </summary>
    private static string ConvertLegacy(string text, ConfigFormat format)
    {
        // Either reader answers a document holding nothing - an empty YAML file, JSON's bare null -
        // with null, and the adapter below dereferences it.
        var v1 = (format == ConfigFormat.Yaml
                     ? new DeserializerBuilder().IgnoreUnmatchedProperties().Build().Deserialize<ServerSettingsV1>(text)
                     : JsonSerializer.Deserialize<ServerSettingsV1>(text))
                 ?? throw new SettingsNotValidException("The settings file contained no settings.");

        var legacy = new ServerSettingsV1Adapter(v1);

        var general = new Models.GeneralSettings();
        CopyMatching(legacy.GeneralSettings, general);

        var accounts = legacy.Accounts.Select(account =>
        {
            var target = new Models.ServerAccountSettings();
            CopyMatching(account, target);
            return NotDefault(target, new Models.ServerAccountSettings());
        }).ToList();

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                [ConfigKeys.General] = NotDefault(general, new Models.GeneralSettings()),
                [ConfigKeys.Accounts] = accounts
            },
            JsonOutput);
    }

    /// <summary>
    /// The settings on <paramref name="settings"/> that differ from <paramref name="defaults"/>, by
    /// name - compared as <see cref="AdminConfigService.IsDefault"/> compares them for a save, so an
    /// import and a save agree on what is left to a default.
    /// </summary>
    private static Dictionary<string, object?> NotDefault(object settings, object defaults)
    {
        var differing = new Dictionary<string, object?>();

        foreach (var property in settings.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var value = property.GetValue(settings);

            // A null is a default only where the default is null as well. A v1 file's explicit
            // "ClockFormat": null left out would bind to "hh:mm" instead, so it is stored as the
            // null it is.
            var isDefault = value is null
                ? property.GetValue(defaults) is null
                : AdminConfigService.IsDefault(defaults, property, value);

            if (!isDefault) differing[property.Name] = value;
        }

        return differing;
    }

    /// <summary>Copies every readable property of <paramref name="from"/> onto the same-named writable property of <paramref name="to"/>.</summary>
    private static void CopyMatching(object from, object to)
    {
        foreach (var source in from.GetType().GetProperties().Where(p => p.CanRead))
        {
            var target = to.GetType().GetProperty(source.Name);
            if (target is null || !target.CanWrite || !target.PropertyType.IsAssignableFrom(source.PropertyType)) continue;

            target.SetValue(to, source.GetValue(from));
        }
    }

    private ImmichFrameException ConfigPathNotWritable(Exception inner)
    {
        var message = $"Cannot open the settings database in '{_location.Directory}'. " +
            "The configuration directory has to exist and be writable by the user ImmichFrame runs as " +
            "(uid 1000 in the official Docker image). For a bind mount, fix it on the host with " +
            "'chown -R 1000:1000 /path/to/config'; a read-only mount does not work.";
        _logger.LogError(inner, "{message}", message);
        return new ImmichFrameException(message, inner);
    }

    private void LogIgnoredFileConfig()
    {
        var ignored = new[] { "Settings.json", "Settings.yml", "Settings.yaml" }
            .Where(name => File.Exists(Path.Combine(_location.Directory, name)))
            .ToList();

        if (ignored.Count > 0)
        {
            _logger.LogInformation(
                "{files} exist but are ignored: the settings were already imported into the database, which is the source of truth. Change settings in the admin editor at /admin.",
                string.Join(", ", ignored));
        }
    }

    internal static string FormatName(ConfigFormat format) => format.ToString().ToLowerInvariant();

    /// <summary>
    /// How every JSON settings document is written - converted from a v1 file here, or saved by the
    /// admin editor. The relaxed encoder, because the document is meant to stay readable to a person:
    /// the default escapes everything outside ASCII, so one save through the editor would turn
    /// non-English album names and tags into runs of \uXXXX - readable to the parser, and to nobody
    /// else.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOutput = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static ConfigFormat ParseFormat(string? stored) =>
        Enum.TryParse<ConfigFormat>(stored, ignoreCase: true, out var format) ? format : ConfigFormat.Json;
}
