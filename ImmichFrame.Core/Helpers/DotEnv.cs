
namespace ImmichFrame.Core.Helpers
{
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

                var index = line.IndexOf('=');

                if (index == -1)
                    continue;

                var variable = line.Substring(0, index);
                var value = line.Substring(index + 1);

                Environment.SetEnvironmentVariable(variable, value);
            }
        }
    }
}
