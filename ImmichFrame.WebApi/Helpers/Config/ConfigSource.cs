namespace ImmichFrame.WebApi.Helpers.Config;

/// <summary>
/// How the configuration ImmichFrame is running was written down.
/// </summary>
public enum ConfigFormat
{
    Json,
    Yaml,

    /// <summary>
    /// Not a file at all: the flat environment-variable fallback. There is nothing to rewrite, so
    /// the admin editor refuses to save rather than inventing a file the next restart would ignore.
    /// </summary>
    Environment
}

/// <summary>
/// Where <see cref="ConfigLoader"/> found the configuration, and in what shape.
/// <para>
/// Reported rather than inferred by the caller because only the loader knows which of its fallbacks
/// actually fired: a <c>Settings.json</c> that fails to parse as the current schema and then loads
/// as v1 is a different thing to write back than one that parsed first time, and a directory with
/// no readable settings file at all is a third.
/// </para>
/// </summary>
/// <param name="Format">The format the file is written in, or <see cref="ConfigFormat.Environment"/>.</param>
/// <param name="Path">The settings file, or null when the configuration came from the environment.</param>
/// <param name="LegacySchema">
/// True when the configuration only loaded through <see cref="ServerSettingsV1Adapter"/>. Saving
/// such a file rewrites it in the current schema, which is not something to do without being asked.
/// </param>
public sealed record ConfigSource(ConfigFormat Format, string? Path, bool LegacySchema);

/// <summary>
/// The directory ImmichFrame loads its settings file from - <c>IMMICHFRAME_CONFIG_PATH</c>, or the
/// <c>Config</c> directory beside the binary.
/// <para>
/// A registered value rather than a static, so a test can point the admin editor at a temporary
/// directory instead of the one the host booted from.
/// </para>
/// </summary>
public sealed record ConfigLocation(string Directory);
