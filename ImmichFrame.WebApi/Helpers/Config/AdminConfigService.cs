using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.WebApi.Models;
using YamlDotNet.Serialization;

namespace ImmichFrame.WebApi.Helpers.Config;

/// <summary>
/// Reads and rewrites the settings file on behalf of the admin editor.
/// <para>
/// A save goes bind, validate, write, swap, in that order and never another: the rewritten file is
/// parsed and validated in memory first, so a configuration that would not load is refused before
/// the file on disk is touched, and there is no state in which the editor leaves a broken settings
/// file behind. Only once the new file is in place is
/// <see cref="SwappableConfigCatalog.Swap"/> called, so a failed write cannot leave the running
/// process serving a configuration that is not what the file says.
/// </para>
/// <para>
/// Serialised on one lock. Saves are rare, the file is small, and the alternative - two
/// administrators reading, editing and writing concurrently - is exactly the interleaving the
/// version token exists to catch. Holding the lock across the read that produces a version token is
/// what makes that token meaningful.
/// </para>
/// </summary>
public sealed class AdminConfigService(
    ConfigLoader _loader,
    ConfigLocation _location,
    SwappableConfigCatalog _catalog,
    ILogger<AdminConfigService> _logger)
{
    private const string GeneralKey = "General";
    private const string AccountsKey = "Accounts";
    private const string ProfilesKey = "Profiles";

    /// <summary>The settings each of the two settings classes accepts, keyed the way a file spells them.</summary>
    private static readonly IReadOnlyDictionary<string, PropertyInfo> GeneralProperties =
        WritableProperties(typeof(GeneralSettings));

    private static readonly IReadOnlyDictionary<string, PropertyInfo> AccountProperties =
        WritableProperties(typeof(ServerAccountSettings));

    /// <summary>
    /// The values that are masked on a read and only written when the administrator actually typed
    /// one. Named here rather than at each use so that adding a secret to the settings cannot mask
    /// it in one direction and leak it in the other.
    /// </summary>
    private static readonly HashSet<string> GeneralSecrets = new(StringComparer.Ordinal)
    {
        nameof(GeneralSettings.WeatherApiKey),
        nameof(GeneralSettings.Webhook),
        nameof(GeneralSettings.AuthenticationSecret)
    };

    /// <summary>
    /// How many previous versions of the settings file are kept beside it. Bounded on purpose: a
    /// save that turns out wrong is usually noticed a save or two later, and an unbounded pile of
    /// backups in the configuration directory is its own operational problem.
    /// </summary>
    private const int BackupsKept = 5;

    /// <summary>
    /// The relaxed encoder, because this file is meant to be hand-editable. The default escapes
    /// everything outside ASCII, so one save through the editor would turn non-English album names
    /// and tags into runs of \uXXXX - readable to the parser, and to nobody else.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOutput = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _saveLock = new();

    public AdminConfigDto Read()
    {
        lock (_saveLock)
        {
            return ReadCurrent();
        }
    }

    /// <summary>
    /// Writes <paramref name="request"/> to the settings file and applies it without a restart.
    /// </summary>
    /// <exception cref="ConfigSaveRefusedException">
    /// Nothing was written: the version token is stale, the configuration came from the environment,
    /// the file is in the v1 schema and the request did not consent to converting it, or the
    /// directory cannot be written to.
    /// </exception>
    /// <exception cref="SettingsNotValidException">
    /// Nothing was written: the configuration in the request does not load.
    /// </exception>
    public AdminConfigDto Save(AdminConfigUpdateDto request)
    {
        lock (_saveLock)
        {
            var source = _loader.DescribeSource(_location.Directory);

            if (source.Format == ConfigFormat.Environment || source.Path is null)
            {
                throw new ConfigSaveRefusedException(
                    "This installation is configured from environment variables, so there is no settings file to edit. " +
                    "Create a Settings.json or Settings.yml in the configuration directory and restart ImmichFrame to use the editor.");
            }

            var current = File.ReadAllText(source.Path);

            if (!string.Equals(VersionOf(current), request.Version, StringComparison.Ordinal))
            {
                throw new ConfigSaveRefusedException(
                    $"'{source.Path}' has changed since it was loaded into the editor. Reload the configuration and apply your changes again.");
            }

            if (source.LegacySchema && !request.ConvertLegacySchema)
            {
                throw new ConfigSaveRefusedException(
                    $"'{source.Path}' is written in the old settings schema. Saving rewrites it in the current one, " +
                    "which is not reversible from here, so confirm the conversion before saving.");
            }

            var rewritten = Render(source.Format, BuildDocument(request, StoredSecrets(source, current)));

            // Bind and validate before the file is touched. Validation is local - it reads ApiKeyFile
            // from disk and checks that each account has a key - so a broken configuration is refused
            // here, with the file on disk still the one that was loaded.
            //
            // Validating after rendering also keeps ValidateAndInitialize's side effect out of the
            // file: it copies ApiKeyFile's contents into ApiKey, and serialising the settings objects
            // instead of the text above would write the key itself into a file that only ever named
            // the path to it.
            var catalog = Validated(source.Format, rewritten);

            WriteAtomically(source.Path, rewritten);

            // Last, and only on a completed write: everything reading IConfigCatalog now sees the
            // configuration that is on disk, and the per-profile services built from the old one are
            // dropped so the next request rebuilds them.
            _catalog.Swap(catalog);

            _logger.LogInformation("Configuration saved to '{configPath}' and applied without a restart", source.Path);

            return ReadCurrent();
        }
    }

    /// <summary>
    /// Binds and validates a rewritten file, with every way it can be wrong reported as one
    /// exception. <c>ServerSettings.Validate</c> lets <c>ValidateAndInitialize</c>'s own
    /// <see cref="InvalidOperationException"/> straight through, so without this a missing API key -
    /// the likeliest mistake there is - would reach the client as a 500 rather than as the sentence
    /// that says which account is wrong.
    /// </summary>
    private static ConfigCatalog Validated(ConfigFormat format, string text)
    {
        try
        {
            var catalog = ConfigLoader.BuildCatalog(ConfigLoader.CreateDocument(format, text));
            catalog.Validate();

            return catalog;
        }
        catch (SettingsNotValidException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SettingsNotValidException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Where a secret the browser was never given is read back from, per configuration being
    /// written.
    /// <para>
    /// Normally that is the file itself, and deliberately only the part of it the configuration in
    /// question <em>declares</em>: falling back to the merged value would write a secret a profile
    /// inherits into that profile as an override of its own.
    /// </para>
    /// <para>
    /// A v1 file has no declared-versus-inherited distinction to preserve - it is one flat
    /// configuration with no profiles - so its secrets come from the running settings instead.
    /// Without that, converting a file to the current schema would blank the API key it is being
    /// converted around.
    /// </para>
    /// </summary>
    private Func<string?, JsonObject?> StoredSecrets(ConfigSource source, string current)
    {
        if (source.LegacySchema)
        {
            var settings = _catalog.Default;
            var secrets = new JsonObject
            {
                [GeneralKey] = new JsonObject
                {
                    [nameof(GeneralSettings.WeatherApiKey)] = settings.GeneralSettings.WeatherApiKey,
                    [nameof(GeneralSettings.Webhook)] = settings.GeneralSettings.Webhook,
                    [nameof(GeneralSettings.AuthenticationSecret)] = settings.GeneralSettings.AuthenticationSecret
                },
                [AccountsKey] = new JsonArray(settings.Accounts
                    .Select(account => (JsonNode)new JsonObject
                    {
                        [nameof(ServerAccountSettings.ApiKey)] = account.ApiKey
                    })
                    .ToArray())
            };

            return profileName => profileName is null ? secrets : null;
        }

        var document = ConfigLoader.CreateDocument(source.Format, current);

        return profileName =>
        {
            try
            {
                return document.DeclaredOverrides(profileName);
            }
            catch (ProfileNotFoundException)
            {
                // A profile the request is creating declares nothing yet.
                return null;
            }
        };
    }

    private AdminConfigDto ReadCurrent()
    {
        var source = _loader.DescribeSource(_location.Directory);

        if (source.Format == ConfigFormat.Environment || source.Path is null)
        {
            return NotFromAFile(source, string.Empty,
                "This installation is configured from environment variables. There is no settings file to edit.");
        }

        var text = File.ReadAllText(source.Path);

        if (source.LegacySchema)
        {
            // A v1 file is flat: it has no profiles and nothing in it is inherited, so every setting
            // it can express counts as declared. Saving it therefore writes the lot, which is what
            // converting to the current schema means.
            return NotFromAFile(source, VersionOf(text), null);
        }

        var document = ConfigLoader.CreateDocument(source.Format, text);
        var version = VersionOf(text);

        var profiles = document.ProfileNames
            .Select(name => Entry(version, name, document.Bind<ServerSettings>(name), document.DeclaredOverrides(name)))
            .ToList();

        return new AdminConfigDto(
            version,
            new AdminConfigSourceDto(FormatName(source.Format), source.Path, false, true, null),
            Entry(version, ConfigCatalog.DefaultProfileName,
                document.Bind<ServerSettings>(null), document.DeclaredOverrides(null)),
            profiles);
    }

    /// <summary>
    /// The view for a configuration that has no current-schema document behind it - one loaded from
    /// the environment, or through the v1 fallback. The values come from the running catalog, and
    /// every key counts as declared, since neither shape has anything to inherit from.
    /// </summary>
    private AdminConfigDto NotFromAFile(ConfigSource source, string version, string? notEditableReason)
    {
        var settings = _catalog.Default;

        // Every account is declared here, so every one has a stored key the save can put back.
        var accounts = settings.Accounts
            .Select((account, index) => new AdminAccountSettingsDto(account)
            {
                Id = AccountId(version, ConfigCatalog.DefaultProfileName, index)
            })
            .ToList();

        var entry = new AdminConfigEntryDto(
            ConfigCatalog.DefaultProfileName,
            [.. GeneralProperties.Values.Select(property => $"{GeneralKey}.{property.Name}"), AccountsKey],
            new AdminGeneralSettingsDto(settings.GeneralSettings),
            accounts);

        return new AdminConfigDto(
            version,
            new AdminConfigSourceDto(FormatName(source.Format), source.Path, source.LegacySchema,
                notEditableReason is null, notEditableReason),
            entry,
            []);
    }

    private static AdminConfigEntryDto Entry(string version, string name, ServerSettings settings, JsonObject declared)
    {
        // A declared Accounts list replaces the inherited one outright, so where the list is declared
        // the bound accounts are exactly the declared ones, in order. Where it is not, the accounts
        // shown are inherited and get no handle: there is no stored key of this configuration's own
        // for a later save to put back.
        var stored = declared[AccountsKey] as JsonArray;

        var accounts = settings.Accounts
            .Select((account, index) => new AdminAccountSettingsDto(account)
            {
                Id = index < (stored?.Count ?? 0) ? AccountId(version, name, index) : null
            })
            .ToList();

        return new AdminConfigEntryDto(name, DeclaredKeys(declared),
            new AdminGeneralSettingsDto(settings.GeneralSettings), accounts);
    }

    /// <summary>
    /// The handle that ties an account in the editor back to the account it was read from.
    /// <para>
    /// Derived rather than stored, so it costs nothing in the settings file: the version token
    /// already guarantees the file has not changed between the read that issued a handle and the save
    /// that presents it, so the stored account's position is a stable identity for exactly that long -
    /// and a handle from an older read fails the version check before it is ever looked up.
    /// </para>
    /// <para>
    /// It is not an integrity control, and must not be read as one. Every input is public - the
    /// version travels in the same response and the index is the account's position in it - so any
    /// caller can compute any handle. Nor would unforgeable handles buy anything: an administrator
    /// who echoes a legitimately issued handle against a server URL of their choosing gets that
    /// account's stored key written there, and is anyway free to type the key out. What the handle
    /// prevents is <em>accidental</em> misattribution - a delete or a reorder silently moving one
    /// server's credential onto another - which is the mistake an editor actually makes. The hash is
    /// there to keep the wire format opaque enough that nobody reimplements positional matching by
    /// reading an index off it.
    /// </para>
    /// </summary>
    private static string AccountId(string version, string profileName, int index) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{version}\n{profileName.ToLowerInvariant()}\n{index}")))[..16]
            .ToLowerInvariant();

    /// <summary>
    /// Flattens a declared subtree into the dotted keys the editor works in. Keys the current schema
    /// does not model are left out, and are therefore dropped when the file is written - the same
    /// accepted loss as the comments this editor does not preserve.
    /// </summary>
    private static List<string> DeclaredKeys(JsonObject declared)
    {
        var keys = new List<string>();

        foreach (var (key, value) in declared)
        {
            if (string.Equals(key, GeneralKey, StringComparison.OrdinalIgnoreCase) && value is JsonObject general)
            {
                keys.AddRange(general
                    .Where(setting => GeneralProperties.ContainsKey(setting.Key))
                    // Spelled as the settings class spells it, so a differently-cased file still
                    // hands the editor keys it can send back.
                    .Select(setting => $"{GeneralKey}.{GeneralProperties[setting.Key].Name}"));
            }
            else if (string.Equals(key, AccountsKey, StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(AccountsKey);
            }
        }

        return keys;
    }

    private static Dictionary<string, object?> BuildDocument(
        AdminConfigUpdateDto request, Func<string?, JsonObject?> stored)
    {
        var root = BuildEntry(request.Default, stored(null), request.Version, null);

        // The default configuration is the one thing with nothing above it to inherit from, so an
        // absent or empty account list here is not a sparse override - it is an ImmichFrame that
        // cannot show a single photo. Refused by name, because ServerSettings.Validate would
        // otherwise dereference a null Accounts and report the whole thing as 'Object reference not
        // set to an instance of an object.'
        if (root.GetValueOrDefault(AccountsKey) is not List<Dictionary<string, object?>> { Count: > 0 })
        {
            throw new SettingsNotValidException(
                "The default configuration must declare at least one Immich account. It is what every " +
                "configuration profile inherits, and ImmichFrame cannot serve an image without one.");
        }

        var profiles = new Dictionary<string, object?>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in request.Profiles ?? [])
        {
            var name = profile.Name ?? string.Empty;

            // The loader's own rules, so a name the editor accepts is one the next restart accepts.
            ConfigCatalog.ValidateProfileName(name);

            if (!seen.Add(name))
            {
                throw new SettingsNotValidException(
                    $"There is more than one configuration profile named '{name}'. Profile names are case-insensitive.");
            }

            profiles[name] = BuildEntry(profile, stored(name), request.Version, name);
        }

        if (profiles.Count > 0)
        {
            root[ProfilesKey] = profiles;
        }

        return root;
    }

    /// <summary>
    /// Turns one configuration into the subtree the file should declare for it: exactly the keys the
    /// request names, and nothing else. This is where a profile stays sparse.
    /// </summary>
    private static Dictionary<string, object?> BuildEntry(
        AdminConfigEntryDto entry, JsonObject? declared, string version, string? profileName)
    {
        var storedGeneral = declared?[GeneralKey] as JsonObject;
        var storedAccounts = declared?[AccountsKey] as JsonArray;

        Dictionary<string, object?>? general = null;
        List<Dictionary<string, object?>>? accounts = null;

        foreach (var key in entry.DeclaredKeys ?? [])
        {
            if (key.StartsWith($"{GeneralKey}.", StringComparison.OrdinalIgnoreCase))
            {
                var name = key[(GeneralKey.Length + 1)..];

                if (!GeneralProperties.TryGetValue(name, out var property))
                {
                    throw new SettingsNotValidException($"'{key}' is not a setting ImmichFrame knows about.");
                }

                var value = GeneralValue(property.Name, entry.General, storedGeneral);

                if (GeneralSecrets.Contains(property.Name) && string.IsNullOrWhiteSpace(value as string))
                {
                    // "No secret here", however the request spelled it. The empty string is never
                    // what gets written: it is not "unset" to anything that reads it back, and for
                    // AuthenticationSecret it is an outright lockout, since the only bearer token
                    // equal to "" is the one no client sends.
                    //
                    // Where it is written differs by one level. On the default configuration null
                    // and absent say the same thing, so the key is simply left out. A profile has a
                    // third state the default does not - an explicit null overrides an inherited
                    // secret with none, which is a documented capability and how a frame on a
                    // trusted network skips the prompt - so dropping the key there would silently
                    // hand the profile the default's secret back instead.
                    if (profileName is null) continue;

                    value = null;
                }

                general ??= [];
                general[property.Name] = value;
            }
            else if (string.Equals(key, AccountsKey, StringComparison.OrdinalIgnoreCase))
            {
                accounts = Accounts(entry.Accounts, storedAccounts, version, profileName);
            }
            else
            {
                throw new SettingsNotValidException($"'{key}' is not a setting ImmichFrame knows about.");
            }
        }

        // Assembled in the order the settings files use rather than the order the keys arrived in,
        // so a save cannot shuffle the file about.
        var result = new Dictionary<string, object?>();
        if (general is not null) result[GeneralKey] = general;
        if (accounts is not null) result[AccountsKey] = accounts;

        return result;
    }

    private static object? GeneralValue(string name, AdminGeneralSettingsDto? general, JsonObject? stored)
    {
        var value = ReadProperty(general, name);

        if (!GeneralSecrets.Contains(name)) return value;

        // Kept only when this configuration already declares it. Falling back to the merged value
        // would write an inherited secret into a profile as an override of its own.
        return AdminSecret.KeepsStored(value as string) ? stored?[name]?.GetValue<string>() : value;
    }

    /// <summary>
    /// Builds the account list a configuration declares, putting each masked API key back on the
    /// account it was read from - or refusing the save when it cannot say which account that is.
    /// <para>
    /// There is deliberately no positional fallback. Deleting the first of two accounts shifts every
    /// account after it, so matching by position would write the deleted account's key under the
    /// surviving account's server URL and start sending one Immich server another server's
    /// credential - silently, since the result validates, swaps in and refreshes the version token
    /// like any successful save. Refusing and asking for the key again is the only safe answer.
    /// </para>
    /// </summary>
    private static List<Dictionary<string, object?>> Accounts(
        IReadOnlyList<AdminAccountSettingsDto>? accounts, JsonArray? stored, string version, string? profileName)
    {
        var storedById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (var index = 0; index < (stored?.Count ?? 0); index++)
        {
            if (stored![index] is JsonObject account)
            {
                storedById[AccountId(version, profileName ?? ConfigCatalog.DefaultProfileName, index)] = account;
            }
        }

        var where = profileName is null ? "the default configuration" : $"configuration profile '{profileName}'";
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var defaults = new ServerAccountSettings();
        var result = new List<Dictionary<string, object?>>();

        foreach (var account in accounts ?? [])
        {
            var ambiguous = false;
            JsonObject? declared = null;

            if (!string.IsNullOrEmpty(account.Id) && storedById.TryGetValue(account.Id, out var match))
            {
                // One handle, one account: two entries claiming the same stored account is exactly the
                // ambiguity that must not be resolved by guessing.
                if (claimed.Add(account.Id)) declared = match;
                else ambiguous = true;
            }

            var apiKey = account.ApiKey;

            if (AdminSecret.KeepsStored(apiKey))
            {
                if (!string.IsNullOrWhiteSpace(account.ApiKeyFile))
                {
                    // An account naming an ApiKeyFile has no ApiKey of its own: the key is read from
                    // that path at load time. Normalised to null rather than left alone, because the
                    // editor sends the masking placeholder back in the field like any other form
                    // value - writing it verbatim would put both an ApiKey and an ApiKeyFile in the
                    // file, which the loader refuses outright, and every ApiKeyFile installation
                    // would find its configuration unsaveable.
                    apiKey = null;
                }
                else if (stored is null)
                {
                    throw new SettingsNotValidException(
                        $"Saving this makes {where} declare its own account list instead of inheriting one, so " +
                        $"it cannot keep an API key it never had of its own. Enter the API key for " +
                        $"'{Label(account)}' and save again.");
                }
                else if (declared is null)
                {
                    throw new SettingsNotValidException(
                        $"The API key for '{Label(account)}' in {where} could not be matched to a stored account" +
                        (ambiguous ? ", because another account in this save already claimed the same one" : "") +
                        ". Enter that account's API key again and save; ImmichFrame will not guess which stored " +
                        "key belongs to it.");
                }
                else
                {
                    apiKey = declared[nameof(ServerAccountSettings.ApiKey)]?.GetValue<string>();
                }
            }

            result.Add(Account(account, declared, apiKey, defaults));
        }

        return result;
    }

    /// <summary>
    /// One account, written as sparsely as the rest of the file.
    /// <para>
    /// A setting is written when the account differs from the built-in defaults or when the file
    /// already spelled it out, and left out otherwise. Materialising all fifteen would freeze today's
    /// defaults into the file, so a later change to one of them would stop reaching any installation
    /// that had ever used the editor - the same silent drift that keeping profiles sparse exists to
    /// prevent, one level further down.
    /// </para>
    /// </summary>
    private static Dictionary<string, object?> Account(
        AdminAccountSettingsDto account, JsonObject? declared, string? apiKey, ServerAccountSettings defaults)
    {
        var entry = new Dictionary<string, object?>();

        foreach (var property in AccountProperties.Values)
        {
            var value = string.Equals(property.Name, nameof(ServerAccountSettings.ApiKey), StringComparison.Ordinal)
                ? apiKey
                : ReadProperty(account, property.Name);

            // A setting the request left out is left out of the file too, rather than written as a
            // null the loader would have to interpret.
            if (value is null) continue;

            if (!Declares(declared, property.Name) && IsDefault(defaults, property, value)) continue;

            entry[property.Name] = value;
        }

        return entry;
    }

    private static string Label(AdminAccountSettingsDto account) =>
        string.IsNullOrWhiteSpace(account.ImmichServerUrl) ? "(no server URL)" : account.ImmichServerUrl;

    /// <summary>Whether the stored account spelled this setting out, however it cased it.</summary>
    private static bool Declares(JsonObject? declared, string name) =>
        declared is not null &&
        declared.Any(setting => string.Equals(setting.Key, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsDefault(object defaults, PropertyInfo property, object value)
    {
        var fallback = property.GetValue(defaults);

        // Lists compare by contents: an empty Albums list is the default however it was constructed,
        // and reference equality would call every one of them a deliberate override.
        if (value is System.Collections.IEnumerable left and not string &&
            fallback is System.Collections.IEnumerable right and not string)
        {
            return left.Cast<object?>().SequenceEqual(right.Cast<object?>());
        }

        return Equals(value, fallback);
    }

    private static object? ReadProperty(object? source, string name) =>
        source is null ? null : source.GetType().GetProperty(name)?.GetValue(source);

    /// <summary>
    /// Renders the document in the format it was loaded in - a YAML installation stays YAML and a
    /// JSON one stays JSON, because the loader picks by filename and a changed format would leave
    /// the old file behind to win the next restart.
    /// </summary>
    private static string Render(ConfigFormat format, Dictionary<string, object?> root) => format switch
    {
        ConfigFormat.Json => JsonSerializer.Serialize(root, JsonOutput),
        ConfigFormat.Yaml => new SerializerBuilder().Build().Serialize(root),
        _ => throw new ConfigSaveRefusedException($"There is no settings file to write for {format}.")
    };

    /// <summary>
    /// Backs the old file up, writes the new one beside it, and renames it over the top, so a reader
    /// sees either the whole old file or the whole new one and never a half-written one.
    /// </summary>
    private void WriteAtomically(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path) ?? _location.Directory;
        var fileName = Path.GetFileName(path);
        var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            // The temporary file first: on a read-only mount this is what fails, and it fails before
            // a backup file has been left lying around.
            File.WriteAllText(temporary, contents);

            // The rename below hands the live file the temporary file's permissions, and a new file
            // is created at 0666 minus the umask. Without this, an operator who tightened the mode on
            // a file holding every Immich API key and the frame's AuthenticationSecret would silently
            // get it widened back on the first save from the editor. File.Copy already carries the
            // mode across to the backup.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            }

            var backup = Path.Combine(directory,
                $"{fileName}.{DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)}.bak");
            File.Copy(path, backup, overwrite: true);

            File.Move(temporary, path, overwrite: true);

            _logger.LogInformation("Previous configuration kept at '{backupPath}'", backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temporary);

            throw new ConfigSaveRefusedException(
                $"'{directory}' could not be written to, so the configuration was not saved. " +
                "ImmichFrame runs as UID 1000 and needs write access to the configuration directory; " +
                $"a read-only mount cannot be edited from here. ({ex.Message})", ex);
        }

        // Outside the block above, and after it: the rename has happened, so a failure while tidying
        // up old backups must not be reported as a configuration that was not saved.
        PruneBackups(directory, fileName);
    }

    /// <summary>
    /// Drops all but the <see cref="BackupsKept"/> most recent backups. Ordered by name, which the
    /// sortable UTC timestamp in it makes the same as ordering by age.
    /// <para>
    /// Failure here is logged and swallowed: the new file is already in place, and turning a problem
    /// with housekeeping into a failed save the administrator has to repeat - and which would then
    /// fail the version check, because the file did change - would be worse than leaving a stale
    /// backup behind.
    /// </para>
    /// </summary>
    private void PruneBackups(string directory, string fileName)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(directory, $"{fileName}.*.bak")
                         .OrderByDescending(backup => backup, StringComparer.Ordinal)
                         .Skip(BackupsKept)
                         .ToList())
            {
                File.Delete(stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not prune old configuration backups in '{configDirectory}' ({errorMessage})",
                directory, ex.Message);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the temporary settings file '{temporaryPath}' ({errorMessage})",
                path, ex.Message);
        }
    }

    /// <summary>
    /// The token that decides whether an edit is still current. A hash of the file's contents rather
    /// than its timestamp: a hand edit and a save from a second browser tab both change it, and a
    /// file restored byte for byte deliberately does not.
    /// </summary>
    private static string VersionOf(string text) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string FormatName(ConfigFormat format) => format.ToString().ToLowerInvariant();

    private static Dictionary<string, PropertyInfo> WritableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
}
