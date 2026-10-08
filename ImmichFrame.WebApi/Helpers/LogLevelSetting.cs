namespace ImmichFrame.WebApi.Helpers;

/// <summary>
/// The minimum log level named by the <c>LOG_LEVEL</c> environment variable.
/// <para>
/// A value that names no level keeps <c>Information</c>. Leaving that to <c>Enum.TryParse</c> did
/// not: on failure it hands back <c>default(LogLevel)</c>, which is <c>Trace</c>, so a near miss such
/// as <c>verbose</c> or <c>warn</c> switched on the noisiest logging there is. And it accepts any
/// number without checking it against the enum, so <c>42</c> became a level above every message
/// and silenced the log altogether.
/// </para>
/// </summary>
public static class LogLevelSetting
{
    /// <summary>
    /// The level <paramref name="value"/> names, ignoring case, or <c>Information</c> when it names
    /// none. <paramref name="rejected"/> tells the two apart, so the caller can say the value was
    /// ignored; an unset or blank variable is not a mistake and is not rejected.
    /// </summary>
    public static LogLevel Parse(string? value, out bool rejected)
    {
        rejected = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return LogLevel.Information;
        }

        if (Enum.TryParse(value, ignoreCase: true, out LogLevel level) && Enum.IsDefined(level))
        {
            return level;
        }

        rejected = true;
        return LogLevel.Information;
    }
}
