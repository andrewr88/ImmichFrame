using System.Text.Json;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Models;
using YamlDotNet.Serialization;

namespace ImmichFrame.WebApi.Helpers.Config;

public class ConfigLoader(ILogger<ConfigLoader> _logger)
{
    private static string FindConfigFile(string dir, params string[] fileNames)
    {
        if (!Directory.Exists(dir))
        {
            return Path.Combine(dir, fileNames[0]);
        }

        return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => fileNames.Any(name => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)))
            ?? Path.Combine(dir, fileNames[0]);
    }

    /// <summary>
    /// Which file the first-run import would read out of <paramref name="configPath"/>, and in what
    /// shape.
    /// <para>
    /// Answered by running the real load rather than by re-deriving the rules, because the answer is
    /// which of the fallbacks below actually fires: a <c>Settings.json</c> that only parses as v1 is
    /// indistinguishable from a current-schema one until it has been tried.
    /// </para>
    /// </summary>
    internal ConfigSource DescribeSource(string configPath)
    {
        // A settings file that will not parse leaves LoadCatalogRaw in one of two places: the
        // environment branch, if two environment variables happen to match a v1 property name, or the
        // bare failure at the end if they do not. Neither may be reported as where this installation is
        // configured from while that file is there: the import tells the operator what it found, and
        // "this installation is configured from environment variables" is a lie that hides a trailing
        // comma - and, on an installation whose settings file is perfectly real, a lie that hides the
        // file.
        var present = FindExistingConfigFile(configPath);

        ConfigSource source;
        try
        {
            source = LoadCatalogRaw(configPath).Source;
        }
        catch (ImmichFrameException ex) when (present is not null)
        {
            throw Unreadable(present, ex);
        }

        return source.Format == ConfigFormat.Environment && present is not null
            ? throw Unreadable(present, null)
            : source;
    }

    private static ConfigSaveRefusedException Unreadable(string path, Exception? cause)
    {
        var message =
            $"'{path}' is present but could not be read as a settings file, in either the current or the " +
            "old schema. Fix the file and reload the editor." +
            (cause is null ? " The startup log names the parse error." : $" ({cause.Message})");

        return cause is null ? new ConfigSaveRefusedException(message) : new ConfigSaveRefusedException(message, cause);
    }

    /// <summary>The settings file that is actually on disk, whether or not it can be read.</summary>
    private static string? FindExistingConfigFile(string configPath) =>
        new[] { FindConfigFile(configPath, "Settings.json"), FindConfigFile(configPath, "Settings.yml", "Settings.yaml") }
            .FirstOrDefault(File.Exists);

    private (ConfigCatalog Catalog, ConfigSource Source) LoadCatalogRaw(string configPath)
    {
        var jsonConfigPath = FindConfigFile(configPath, "Settings.json");
        if (File.Exists(jsonConfigPath))
        {
            try
            {
                return (LoadCatalogJson(jsonConfigPath), new ConfigSource(ConfigFormat.Json, jsonConfigPath, false));
            }
            catch (Exception e)
            {
                _logger.LogWarning("Failed to load config as current version JSON. ({errorMessage})", e.Message);
            }

            try
            {
                var v1 = LoadConfigJson<ServerSettingsV1>(jsonConfigPath);
                return (new ConfigCatalog(new ServerSettingsV1Adapter(v1)),
                    new ConfigSource(ConfigFormat.Json, jsonConfigPath, true));
            }
            catch (Exception e)
            {
                _logger.LogWarning("Failed to load config as old JSON. ({errorMessage})", e.Message);
            }
        }

        var ymlConfigPath = FindConfigFile(configPath, "Settings.yml", "Settings.yaml");
        if (File.Exists(ymlConfigPath))
        {
            try
            {
                return (LoadCatalogYaml(ymlConfigPath), new ConfigSource(ConfigFormat.Yaml, ymlConfigPath, false));
            }
            catch (Exception e)
            {
                _logger.LogWarning("Failed to load config as current version YAML. ({errorMessage})", e.Message);
            }

            try
            {
                var v1 = LoadConfigYaml<ServerSettingsV1>(ymlConfigPath);
                return (new ConfigCatalog(new ServerSettingsV1Adapter(v1)),
                    new ConfigSource(ConfigFormat.Yaml, ymlConfigPath, true));
            }
            catch (Exception e)
            {
                _logger.LogWarning("Failed to load config as old YAML. ({errorMessage})", e.Message);
            }
        }

        try
        {
            // Environment variables are flat, so they can only ever describe a single configuration.
            // Only their names are checked: SettingsService refuses to import them, so nothing reads
            // the values any more, and parsing them anyway let one bad value turn the "no longer
            // supported" warning into a parse error.
            var environment = Environment.GetEnvironmentVariables();
            var recognised = typeof(ServerSettingsV1).GetProperties().Count(property => environment.Contains(property.Name));
            if (recognised < 2)
            {
                throw new ImmichFrameException("No environment variables found");
            }

            return (new ConfigCatalog(new ServerSettingsV1Adapter(new ServerSettingsV1())),
                new ConfigSource(ConfigFormat.Environment, null, true));
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to load config as env vars ({errorMessage})", e.Message);
        }

        throw new ImmichFrameException("Failed to load configuration");
    }

    internal ConfigCatalog LoadCatalogJson(string configPath)
        => BuildCatalog(new JsonConfigDocument(ReadConfigFile(configPath)));

    internal ConfigCatalog LoadCatalogYaml(string configPath)
        => BuildCatalog(new YamlConfigDocument(ReadConfigFile(configPath)));

    private static string ReadConfigFile(string configPath)
    {
        if (!File.Exists(configPath)) throw new FileNotFoundException(configPath);

        return File.ReadAllText(configPath);
    }

    /// <summary>
    /// Reads a settings file that is already in memory. The admin editor uses it to bind and
    /// validate a rewritten file before that file reaches the disk, so that the same shape check and
    /// the same profile-name rules apply to a saved configuration as to a loaded one.
    /// </summary>
    internal static IConfigDocument CreateDocument(ConfigFormat format, string text) => format switch
    {
        ConfigFormat.Json => new JsonConfigDocument(text),
        ConfigFormat.Yaml => new YamlConfigDocument(text),
        _ => throw new ImmichFrameException($"There is no settings-file document for {format}.")
    };

    internal static ConfigCatalog BuildCatalog(IConfigDocument document)
    {
        // A settings file from before 'General'/'Accounts' existed binds to an empty current-version
        // config rather than failing, so check the shape explicitly and let the caller fall back to
        // the V1 reader instead of starting up with no accounts at all.
        if (!document.HasKey(ConfigKeys.General) && !document.HasKey(ConfigKeys.Accounts))
        {
            throw new SettingsNotValidException(
                $"The settings file contains neither a '{ConfigKeys.General}' nor an '{ConfigKeys.Accounts}' section.");
        }

        var profiles = document.ProfileNames
            .Select(name => KeyValuePair.Create(name, (IServerSettings)document.Bind<ServerSettings>(name)))
            .ToList();

        return new ConfigCatalog(document.Bind<ServerSettings>(null), profiles);
    }

    internal T LoadConfigJson<T>(string configPath) where T : IConfigSettable, new()
    {
        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<T>(json) ?? throw new FileLoadException("Failed to load config file", configPath);
            }

            throw new FileNotFoundException(configPath);
        }
        catch (Exception ex)
        {
            throw new SettingsNotValidException($"Problem with parsing the settings: {ex.Message}", ex);
        }
    }
    internal T LoadConfigYaml<T>(string configPath) where T : IConfigSettable, new()
    {
        try
        {
            if (File.Exists(configPath))
            {
                var yml = File.ReadAllText(configPath);
                var deserializer = new DeserializerBuilder()
                    .IgnoreUnmatchedProperties()
                    .Build();
                return deserializer.Deserialize<T>(yml) ?? throw new FileLoadException("Failed to load config file", configPath);
            }

            throw new FileNotFoundException(configPath);
        }
        catch (Exception ex)
        {
            throw new SettingsNotValidException($"Problem with parsing the settings: {ex.Message}", ex);
        }
    }
}
