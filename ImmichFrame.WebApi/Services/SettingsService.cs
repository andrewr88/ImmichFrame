using System.Text.Json;
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
    /// </summary>
    public IConfigCatalog LoadCatalog()
    {
        var stored = Read();

        // An unconfigured instance has no document to bind. An empty catalog rather than a throw:
        // the slideshow reports that nothing is configured and the admin editor is reachable to fix
        // it, which is the whole point of starting without a configuration.
        if (IsUnconfigured)
        {
            return new ConfigCatalog(new Models.ServerSettings
            {
                GeneralSettingsImpl = new Models.GeneralSettings(),
                AccountsImpl = []
            });
        }

        var catalog = ConfigLoader.BuildCatalog(ConfigLoader.CreateDocument(stored.Format, stored.Text));

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
    /// </summary>
    private static string ConvertLegacy(string text, ConfigFormat format)
    {
        var v1 = format == ConfigFormat.Yaml
            ? new DeserializerBuilder().IgnoreUnmatchedProperties().Build().Deserialize<ServerSettingsV1>(text)
            : JsonSerializer.Deserialize<ServerSettingsV1>(text)
              ?? throw new SettingsNotValidException("The settings file contained no settings.");

        var legacy = new ServerSettingsV1Adapter(v1);

        var general = new Models.GeneralSettings();
        CopyMatching(legacy.GeneralSettings, general);

        var accounts = legacy.Accounts.Select(account =>
        {
            var target = new Models.ServerAccountSettings();
            CopyMatching(account, target);
            return target;
        }).ToList();

        return JsonSerializer.Serialize(
            new Models.ServerSettings { GeneralSettingsImpl = general, AccountsImpl = accounts },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
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

    private static ConfigFormat ParseFormat(string? stored) =>
        Enum.TryParse<ConfigFormat>(stored, ignoreCase: true, out var format) ? format : ConfigFormat.Json;
}
