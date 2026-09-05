using System.Collections;
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
            return Path.Combine(dir, fileNames.First());
        }

        return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => fileNames.Any(name => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)))
            ?? Path.Combine(dir, fileNames.First());
    }

    /// <summary>
    /// Loads every configuration ImmichFrame should serve: the default one, plus any profile
    /// declared under <c>Profiles</c> in the settings file.
    /// </summary>
    public IConfigCatalog LoadCatalog(string configPath)
    {
        var (catalog, _) = LoadCatalogRaw(configPath);
        catalog.Validate();
        return catalog;
    }

    public IServerSettings LoadConfig(string configPath) => LoadCatalog(configPath).Default;

    /// <summary>
    /// Which file a restart would load out of <paramref name="configPath"/>, and in what shape.
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
        // bare failure at the end if they do not. LoadCatalog goes on starting the application from
        // whichever it reaches - long-standing behaviour, and it stays - but the configuration editor
        // must not repeat it. "This installation is configured from environment variables" is a lie
        // that hides a trailing comma, and on an installation whose settings file is perfectly real it
        // is a lie that hides the file.
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
            var v1 = LoadConfigFromDictionary<ServerSettingsV1>(Environment.GetEnvironmentVariables());
            return (new ConfigCatalog(new ServerSettingsV1Adapter(v1)),
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
        if (!document.HasKey("General") && !document.HasKey("Accounts"))
        {
            throw new SettingsNotValidException("The settings file contains neither a 'General' nor an 'Accounts' section.");
        }

        var profiles = document.ProfileNames
            .Select(name => KeyValuePair.Create(name, (IServerSettings)document.Bind<ServerSettings>(name)))
            .ToList();

        return new ConfigCatalog(document.Bind<ServerSettings>(null), profiles);
    }

    internal T LoadConfigFromDictionary<T>(IDictionary env) where T : IConfigSettable, new()
    {
        var config = new T();
        var propertiesSet = 0;

        foreach (var key in env.Keys)
        {
            if (key == null) continue;

            var propertyInfo = typeof(T).GetProperty(key.ToString() ?? string.Empty);

            if (propertyInfo != null)
            {
                config.SetValue(propertyInfo, env[key]?.ToString() ?? string.Empty);
                propertiesSet++;
            }
        }

        if (propertiesSet < 2)
        {
            throw new ImmichFrameException("No environment variables found");
        }

        return config;
    }

    internal T LoadConfigJson<T>(string configPath) where T : IConfigSettable, new()
    {
        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var doc = JsonDocument.Parse(json);
                return doc.Deserialize<T>() ?? throw new FileLoadException("Failed to load config file", configPath);
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
