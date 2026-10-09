
namespace ImmichFrame.Core.Helpers
{
    /// <summary>
    /// Loads <c>docker/.env</c> into the environment for <c>dotnet run</c>, read as docker compose
    /// reads the same file for the container, so that the two set the same variables. As far as a
    /// line goes that holds a name and a value: escapes, and values over several lines, are not read
    /// as compose reads them.
    /// </summary>
    public static class DotEnv
    {
        public static void Load(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            foreach (var line in File.ReadAllLines(filePath))
            {
                // docker compose reads this same file and skips its comments, so this has to as well.
                // Split on '=' like any other line, "# IMMICHFRAME_CONFIG_PATH=/app/Config" set a
                // variable named "# IMMICHFRAME_CONFIG_PATH".
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                // "export NAME=value", as a file a shell sources as well is written, is NAME=value to
                // compose.
                if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                    trimmed = trimmed["export ".Length..];

                var index = trimmed.IndexOf('=');

                if (index == -1)
                    continue;

                var variable = trimmed[..index].Trim();

                // "=value" names nothing, and a variable with no name cannot be set: it threw, and
                // took the host down as it started.
                if (variable.Length == 0)
                    continue;

                Environment.SetEnvironmentVariable(variable, Value(trimmed[(index + 1)..]));
            }
        }

        /// <summary>
        /// A value as compose reads it: without the whitespace around it, and then either what one
        /// pair of quotes holds, as written, or - unquoted - what comes before a comment, which a
        /// space and a '#' begin. A '#' with no space before it is part of the value, as it can be
        /// of a URL or a password.
        /// </summary>
        private static string Value(string value)
        {
            value = value.Trim();

            if (value.Length > 0 && value[0] is '"' or '\'')
            {
                // Whatever follows the closing quote - a comment, say - is not part of the value.
                var closing = value.IndexOf(value[0], 1);
                if (closing != -1)
                    return value[1..closing];
            }

            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            return comment == -1 ? value : value[..comment].TrimEnd();
        }
    }
}
