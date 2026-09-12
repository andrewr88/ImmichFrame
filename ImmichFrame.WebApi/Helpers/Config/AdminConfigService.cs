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
    Services.SettingsService _store,
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
            var settings = _store.Read();
            var source = new ConfigSource(settings.Format, null, false);

            // Checked here as well as inside the store: this lock is what makes the token meaningful
            // within the process, and refusing before anything is rendered keeps the message about
            // the edit rather than about the write.
            if (!string.Equals(VersionOf(settings), request.Version, StringComparison.Ordinal))
            {
                throw new ConfigSaveRefusedException(
                    "The settings have changed since they were loaded into the editor. Reload the configuration and apply your changes again.");
            }

            var stored = StoredSecrets(source, settings.Text, request.Version);
            var rewritten = Render(settings.Format, BuildDocument(request, stored));

            // Bind and validate before the file is touched. Validation is local - it reads ApiKeyFile
            // from disk and checks that each account has a key - so a broken configuration is refused
            // here, with the file on disk still the one that was loaded.
            //
            // Validating after rendering also keeps ValidateAndInitialize's side effect out of the
            // file: it copies ApiKeyFile's contents into ApiKey, and serialising the settings objects
            // instead of the text above would write the key itself into a file that only ever named
            // the path to it.
            var catalog = Validated(settings.Format, rewritten);

            _store.Save(rewritten, settings.Format, settings.Version);

            // Last, and only on a completed write: everything reading IConfigCatalog now sees the
            // stored configuration, and the per-profile services built from the old one are dropped
            // so the next request rebuilds them.
            _catalog.Swap(catalog);

            _logger.LogInformation("Configuration saved and applied without a restart");

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
    /// One stored account, remembered along with the name of the configuration that declared it.
    /// <para>
    /// The owner is kept because a handle answers two questions with different scopes. <em>Which
    /// stored API key is this?</em> any entry in the document may answer, which is what lets a
    /// profile be given an account it never declared without the key being retyped. <em>What did
    /// this entry already spell out?</em> only the entry's own stored account may answer - an
    /// adopted account is new here, and answering that one across entries would make the adopting
    /// profile write out every setting the entry it came from spelled out, sparse no longer.
    /// </para>
    /// </summary>
    private readonly record struct StoredAccount(string Owner, JsonObject Account);

    /// <summary>
    /// The configuration as it stands on disk, in the two shapes a save reads secrets back out of.
    /// </summary>
    /// <param name="DeclaredByName">
    /// What each configuration <em>declares</em> itself, under the name a handle spells it with -
    /// <see cref="ConfigCatalog.DefaultProfileName"/> for the default configuration. A profile the
    /// request is creating is simply absent.
    /// </param>
    /// <param name="AccountsById">
    /// Every stored account in the document, the default configuration's and every profile's, under
    /// the handle the read issued for it. Built once per save and shared by every entry: the handle
    /// names the account it was read from, not the entry being written.
    /// </param>
    private sealed record StoredConfiguration(
        IReadOnlyDictionary<string, JsonObject> DeclaredByName,
        IReadOnlyDictionary<string, StoredAccount> AccountsById)
    {
        /// <summary>
        /// What one configuration declares - the default one for a null <paramref name="profileName"/>,
        /// otherwise that profile - or null where it declares nothing yet.
        /// </summary>
        public JsonObject? Declared(string? profileName) =>
            DeclaredByName.GetValueOrDefault(profileName ?? ConfigCatalog.DefaultProfileName);
    }

    /// <summary>
    /// Where a secret the browser was never given is read back from.
    /// <para>
    /// For everything but an account's API key that is the file itself, and deliberately only the
    /// part of it the configuration in question <em>declares</em>: falling back to the merged value
    /// would write a secret a profile inherits into that profile as an override of its own.
    /// </para>
    /// <para>
    /// API keys are gathered from every entry at once instead, because an account handle names the
    /// entry and position it was read from - so resolving one against the whole document still
    /// cannot land on an account the editor did not point at, and an administrator can hand a
    /// profile an account the default configuration declares without retyping its credentials.
    /// Entries the request is about to delete or rename are in there too: an account of theirs may
    /// be the one moving elsewhere in this very save.
    /// </para>
    /// <para>
    /// A v1 file has no declared-versus-inherited distinction to preserve - it is one flat
    /// configuration with no profiles - so its secrets come from the running settings instead, and
    /// its accounts are the default configuration's alone. Without that, converting a file to the
    /// current schema would blank the API key it is being converted around.
    /// </para>
    /// </summary>
    private StoredConfiguration StoredSecrets(ConfigSource source, string current, string version)
    {
        var declaredByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        if (source.LegacySchema)
        {
            var settings = _catalog.Default;

            declaredByName[ConfigCatalog.DefaultProfileName] = new JsonObject
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

            return new StoredConfiguration(declaredByName, AccountsById(version, declaredByName));
        }

        var document = ConfigLoader.CreateDocument(source.Format, current);

        declaredByName[ConfigCatalog.DefaultProfileName] = document.DeclaredOverrides(null);

        foreach (var name in document.ProfileNames)
        {
            // One name, one entry: a handle's key lookup and an entry's own declared subtree are
            // both resolved out of this dictionary by entry name, so a second entry answering to a
            // name already in it would quietly source the first one's secrets - the default
            // configuration's three general secrets and its accounts' stored keys among them - from
            // somewhere else in the file. No file that reaches this branch can do that today: the
            // loader refuses both collisions, and one that trips either is read as v1 instead. The
            // refusal is repeated here because that guarantee lives two files away, and nothing on
            // this side would notice it being relaxed.
            if (!declaredByName.TryAdd(name, document.DeclaredOverrides(name)))
            {
                throw new SettingsNotValidException(
                    ConfigCatalog.DefaultProfileName.Equals(name, StringComparison.OrdinalIgnoreCase)
                        ? $"'{name}' is a reserved name and cannot be used for a configuration profile. " +
                          "It is how the default configuration itself is spelled, and a profile of that name would shadow it."
                        : $"There is more than one configuration profile named '{name}'. Profile names are case-insensitive.");
            }
        }

        return new StoredConfiguration(declaredByName, AccountsById(version, declaredByName));
    }

    /// <summary>
    /// Indexes every stored account in the document by the handle the read issued for it.
    /// <para>
    /// Keyed exactly as <see cref="Entry"/> keys it, entry name included, so a handle the editor
    /// echoes back lands on the account it was read from and on no other. Two entries therefore
    /// cannot collide, and nothing here is reached by guessing - an entry the handle does not name
    /// is not a candidate for it.
    /// </para>
    /// </summary>
    private static Dictionary<string, StoredAccount> AccountsById(
        string version, IReadOnlyDictionary<string, JsonObject> declaredByName)
    {
        var byId = new Dictionary<string, StoredAccount>(StringComparer.Ordinal);

        foreach (var (name, declared) in declaredByName)
        {
            // An entry declaring no account list of its own contributes none: the accounts it shows
            // are another entry's, and they are already indexed under that entry's handles.
            if (declared[AccountsKey] is not JsonArray stored) continue;

            for (var index = 0; index < stored.Count; index++)
            {
                if (stored[index] is JsonObject account)
                {
                    byId[AccountId(version, name, index)] = new StoredAccount(name, account);
                }
            }
        }

        return byId;
    }

    private AdminConfigDto ReadCurrent()
    {
        var settings = _store.Read();
        var source = new ConfigSource(settings.Format, null, false);
        var version = VersionOf(settings);

        // Nothing has ever configured this instance, so there is no document to project. The editor
        // gets the empty default configuration to fill in rather than an error.
        if (_store.IsUnconfigured)
        {
            return NotFromAFile(source, version, null);
        }

        var document = ConfigLoader.CreateDocument(settings.Format, settings.Text);

        var profiles = document.ProfileNames
            .Select(name => Entry(version, name, document.Bind<ServerSettings>(name), document.DeclaredOverrides(name)))
            .ToList();

        return new AdminConfigDto(
            version,
            new AdminConfigSourceDto(FormatName(source.Format), null, false, true, null),
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
        //
        // The label has to be read off the concrete settings, because it is not on IAccountSettings
        // and must not be put there - and this branch only has the interface. An environment
        // configuration and a v1 file both arrive as adapters over their own schema, neither of
        // which can express a label, so reporting none for them is the honest answer rather than a
        // gap: the cast fails exactly when there is nothing to read.
        var accounts = settings.Accounts
            .Select((account, index) => new AdminAccountSettingsDto(account)
            {
                Id = AccountId(version, ConfigCatalog.DefaultProfileName, index),
                Label = account is ServerAccountSettings stored ? NormalizedLabel(stored.Label) : null
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

        // AccountsImpl rather than Accounts: the label is not on IAccountSettings and must not be
        // put there, and this is the one path that already knows the concrete type.
        var accounts = settings.AccountsImpl
            .Select((account, index) => new AdminAccountSettingsDto(account)
            {
                Id = index < (stored?.Count ?? 0) ? AccountId(version, name, index) : null,
                Label = NormalizedLabel(account.Label)
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
        AdminConfigUpdateDto request, StoredConfiguration stored)
    {
        var root = BuildEntry(request.Default, stored.Declared(null), stored.AccountsById, null);

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

            profiles[name] = BuildEntry(profile, stored.Declared(name), stored.AccountsById, name);
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
        AdminConfigEntryDto entry, JsonObject? declared,
        IReadOnlyDictionary<string, StoredAccount> storedById, string? profileName)
    {
        var storedGeneral = declared?[GeneralKey] as JsonObject;

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
                accounts = Accounts(entry.Accounts, storedById, profileName);
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
    /// A handle resolves against every entry in the document, not just this one, so an administrator
    /// can give a profile an account the default configuration - or another profile - declares
    /// without retyping its credentials. Two entries naming the same stored account is not an
    /// ambiguity: each writes its own copy of that key into its own list, which is the point. Two
    /// accounts <em>within one list</em> naming it still is, and is still refused.
    /// </para>
    /// <para>
    /// There is deliberately no positional fallback. Deleting the first of two accounts shifts every
    /// account after it, so matching by position would write the deleted account's key under the
    /// surviving account's server URL and start sending one Immich server another server's
    /// credential - silently, since the result validates, swaps in and refreshes the version token
    /// like any successful save. Refusing and asking for the key again is the only safe answer.
    /// </para>
    /// <para>
    /// Two accounts <em>in this list</em> carrying one label are refused for the same reason. A label
    /// is what says "these two entries mean one account", so a list holding it twice would show two
    /// sets of credentials as a single account, and editing that one row would write one account's
    /// server URL or key over the other's. Across entries the opposite holds - a shared label is how
    /// a profile says it means the default configuration's account - so there is no check there.
    /// </para>
    /// </summary>
    private static List<Dictionary<string, object?>> Accounts(
        IReadOnlyList<AdminAccountSettingsDto>? accounts,
        IReadOnlyDictionary<string, StoredAccount> storedById,
        string? profileName)
    {
        var entryName = profileName ?? ConfigCatalog.DefaultProfileName;
        var where = profileName is null ? "the default configuration" : $"configuration profile '{profileName}'";
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var labelled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaults = new ServerAccountSettings();
        var result = new List<Dictionary<string, object?>>();

        foreach (var account in accounts ?? [])
        {
            var label = NormalizedLabel(account.Label);

            // Case-insensitively and after trimming, because "Mum" and "mum " would read as one
            // account to anyone looking at the editor, whatever the file says.
            if (label is not null && !labelled.Add(label))
            {
                throw new SettingsNotValidException(
                    $"Two accounts in {where} are labelled '{label}'. A label is what tells one account " +
                    "from another here, so two accounts sharing one would be edited as if they were a " +
                    "single account - writing one account's server URL or API key over the other's. " +
                    "Labels are compared without regard to case or surrounding spaces; rename one of them.");
            }

            var ambiguous = false;

            // Two questions, deliberately two variables: which stored key this account is, and what
            // this entry had already spelled out about it. Only the first may be answered from
            // another entry's account.
            JsonObject? stored = null;
            JsonObject? declared = null;

            if (!string.IsNullOrEmpty(account.Id) && storedById.TryGetValue(account.Id, out var match))
            {
                // One handle, one account in this list: a second account naming it is the ambiguity
                // that must not be resolved by guessing.
                if (claimed.Add(account.Id))
                {
                    stored = match.Account;

                    // Matched by name rather than by reference: the request may spell this entry's
                    // name with different casing than the file does, which is exactly the difference
                    // AccountId already folds away.
                    if (string.Equals(match.Owner, entryName, StringComparison.OrdinalIgnoreCase))
                    {
                        declared = match.Account;
                    }
                }
                else
                {
                    ambiguous = true;
                }
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
                else if (ambiguous)
                {
                    throw new SettingsNotValidException(
                        $"Two accounts in {where} name the same stored account, so ImmichFrame cannot tell " +
                        $"which of them keeps its API key. Enter the API key for '{Describe(account)}' and save again.");
                }
                else if (stored is null)
                {
                    throw new SettingsNotValidException(
                        $"The API key for '{Describe(account)}' in {where} could not be matched to an account " +
                        "stored anywhere in the settings file, so there is no key to keep: either the account " +
                        "is new, or the stored account it came from is gone. Enter its API key and save again; " +
                        "ImmichFrame will not guess which stored key belongs to it.");
                }
                else
                {
                    apiKey = stored[nameof(ServerAccountSettings.ApiKey)]?.GetValue<string>();
                }
            }

            result.Add(Account(account, declared, apiKey, label, defaults));
        }

        return result;
    }

    /// <summary>
    /// One account, written as sparsely as the rest of the file.
    /// <para>
    /// A setting is written when the account differs from the built-in defaults or when the file
    /// already spelled it out, and left out otherwise. Materialising all sixteen would freeze today's
    /// defaults into the file, so a later change to one of them would stop reaching any installation
    /// that had ever used the editor - the same silent drift that keeping profiles sparse exists to
    /// prevent, one level further down.
    /// </para>
    /// </summary>
    private static Dictionary<string, object?> Account(
        AdminAccountSettingsDto account, JsonObject? declared, string? apiKey, string? label,
        ServerAccountSettings defaults)
    {
        var entry = new Dictionary<string, object?>();

        foreach (var property in AccountProperties.Values)
        {
            // Two settings the request does not supply verbatim: the API key has already been
            // resolved against what is stored, and the label has been normalised, so that a label of
            // spaces leaves no key behind rather than writing one the next read would report as
            // absent.
            var value = property.Name switch
            {
                nameof(ServerAccountSettings.ApiKey) => apiKey,
                nameof(ServerAccountSettings.Label) => label,
                _ => ReadProperty(account, property.Name)
            };

            // A setting the request left out is left out of the file too, rather than written as a
            // null the loader would have to interpret.
            if (value is null) continue;

            if (!Declares(declared, property.Name) && IsDefault(defaults, property, value)) continue;

            entry[property.Name] = value;
        }

        return entry;
    }

    /// <summary>
    /// How a refusal names the account it is about. The server URL rather than
    /// <see cref="AdminAccountSettingsDto.Label"/>, because an account can be saved without a label
    /// and one of these messages is what an administrator gets when something is already wrong.
    /// </summary>
    private static string Describe(AdminAccountSettingsDto account) =>
        string.IsNullOrWhiteSpace(account.ImmichServerUrl) ? "(no server URL)" : account.ImmichServerUrl;

    /// <summary>
    /// A label as everything here compares and stores it. Absent, empty and whitespace-only are one
    /// state - this account has no label - so they all normalise to null, and a stray space either
    /// side of a typed name is not a second account.
    /// </summary>
    private static string? NormalizedLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();

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
    /// Renders the document in the format it was stored in - a configuration imported from YAML
    /// stays YAML and one imported from JSON stays JSON. Not a cosmetic choice: YAML's
    /// representation model carries no resolved scalar type, so rewriting a YAML document as JSON
    /// would have to guess whether a quoted '10' was a number or a string.
    /// </summary>
    private static string Render(ConfigFormat format, Dictionary<string, object?> root) => format switch
    {
        ConfigFormat.Json => JsonSerializer.Serialize(root, JsonOutput),
        ConfigFormat.Yaml => new SerializerBuilder().Build().Serialize(root),
        _ => throw new ConfigSaveRefusedException($"There is no settings document to write for {format}.")
    };


    /// <summary>
    /// The token that decides whether an edit is still current: the stored row's version, which the
    /// store increments on every save and enforces again as an EF concurrency token. Rendered as a
    /// string because it travels through the DTOs and into the account handles below, which are
    /// text.
    /// <para>
    /// This replaced a hash of the settings file's contents when the database became the source of
    /// truth. A counter is the better token here for the reason the hash was chosen there: a hash
    /// answers "are these the same bytes", which was the question while a human could edit the file
    /// underneath the editor, and the row can only change by being saved.
    /// </para>
    /// </summary>
    private static string VersionOf(Services.StoredSettings settings) =>
        settings.Version.ToString(CultureInfo.InvariantCulture);

    private static string FormatName(ConfigFormat format) => format.ToString().ToLowerInvariant();

    private static Dictionary<string, PropertyInfo> WritableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
}
