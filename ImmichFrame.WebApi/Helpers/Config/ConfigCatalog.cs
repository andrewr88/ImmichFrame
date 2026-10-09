using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.WebApi.Helpers.Config;

public partial class ConfigCatalog : IConfigCatalog
{
    /// <summary>
    /// Name a client can use to explicitly ask for <see cref="Default"/>. Reserved, so that a
    /// profile of that name can never shadow the default configuration.
    /// </summary>
    public const string DefaultProfileName = "default";

    // Profile names travel as a path segment on the web client and as a query parameter on the
    // API, so restrict them to characters that need no escaping in either. Anchored with \A and \z
    // rather than ^ and $, because $ also matches just before a trailing newline - 'api\n' would
    // otherwise pass as valid and then slip past the reserved-name check below.
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex ValidProfileName();

    // Paths the backend already serves, plus 'admin', which the web client keeps for its
    // configuration editor. A profile named after one of these could never be reached at /{profile}
    // in the browser, so reject it up front rather than let it fail mysteriously.
    private static readonly HashSet<string> ReservedProfileNames =
        new(StringComparer.OrdinalIgnoreCase) { "api", "static", "swagger", "admin", DefaultProfileName };

    private readonly Dictionary<string, IServerSettings> _profiles;

    public ConfigCatalog(IServerSettings defaultSettings,
        IEnumerable<KeyValuePair<string, IServerSettings>>? profiles = null)
    {
        Default = defaultSettings;
        _profiles = new Dictionary<string, IServerSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, settings) in profiles ?? [])
        {
            ValidateProfileName(name);

            if (!_profiles.TryAdd(name, settings))
            {
                throw new SettingsNotValidException(
                    $"There is more than one configuration profile named '{name}'. Profile names are case-insensitive.");
            }
        }
    }

    public IServerSettings Default { get; }

    public IReadOnlyCollection<string> ProfileNames => _profiles.Keys;

    public bool TryGet(string? name, [NotNullWhen(true)] out IServerSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(name) || DefaultProfileName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            settings = Default;
            return true;
        }

        return _profiles.TryGetValue(name, out settings);
    }

    public IServerSettings Get(string? name)
    {
        if (TryGet(name, out var settings)) return settings;

        throw ProfileNotFound(name);
    }

    /// <summary>
    /// The refusal for a profile nothing declares, in the same words whether the catalog or a settings
    /// document is the one asked for it.
    /// </summary>
    internal static ProfileNotFoundException ProfileNotFound(string? name) =>
        new($"No configuration profile named '{name}' is configured.");

    /// <summary>
    /// Validates the default configuration and every profile, so that a broken profile is reported
    /// at startup rather than the first time a client asks for it.
    /// <para>
    /// Every one of them, whatever the ones before it threw, for the reason each configuration
    /// validates every account: validating is also what reads an account's ApiKeyFile, and settings
    /// that fail are run anyway. Each failure is named after the configuration it belongs to - the
    /// default one included, so that a message joining several still says which is which - and all
    /// of them are thrown at once in an <see cref="AggregateSettingsNotValidException"/>.
    /// </para>
    /// </summary>
    public void Validate()
    {
        var failures = new List<Exception>();

        Validate("The default configuration", Default, failures);

        foreach (var (name, settings) in _profiles)
        {
            Validate($"Configuration profile '{name}'", settings, failures);
        }

        if (failures.Count > 0) throw new AggregateSettingsNotValidException(failures);
    }

    private static void Validate(string configuration, IServerSettings settings, List<Exception> failures)
    {
        try
        {
            settings.Validate();
        }
        catch (Exception ex)
        {
            failures.Add(new SettingsNotValidException($"{configuration} is not valid: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// The profile-name rules, exposed so that the admin editor rejects a bad name with the same
    /// message the loader would, rather than growing a second copy of the pattern and the reserved
    /// list that would drift from this one.
    /// </summary>
    internal static void ValidateProfileName(string name)
    {
        if (!ValidProfileName().IsMatch(name))
        {
            throw new SettingsNotValidException(
                $"'{name}' is not a valid configuration profile name. Use between 1 and 64 characters from A-Z, a-z, 0-9, '-' and '_'.");
        }

        if (ReservedProfileNames.Contains(name))
        {
            throw new SettingsNotValidException(
                $"'{name}' is a reserved name and cannot be used for a configuration profile.");
        }
    }
}
