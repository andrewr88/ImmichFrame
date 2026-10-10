using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.WebApi.Helpers.Config;

namespace ImmichFrame.WebApi.Helpers
{
    public record AccountCheckResult(bool Success, string Message, string? Version);

    public static class ImmichServerVersionChecker
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The minimum Immich server version supported by this version of ImmichFrame. The Immich client
        /// is generated from this version's API, and cannot read an older server's answer to a metadata
        /// search: it lacks the <c>nextCursor</c> that v3.2.0 added and the client requires.
        /// </summary>
        public const int MinimumSupportedMajorVersion = 3;
        public const int MinimumSupportedMinorVersion = 2;
        public const int MinimumSupportedPatchVersion = 0;

        public static readonly Version MinimumSupportedVersion = new(
            MinimumSupportedMajorVersion,
            MinimumSupportedMinorVersion,
            MinimumSupportedPatchVersion);

        /// <summary>
        /// Checks a single account: is the Immich server reachable and running a supported version?
        /// </summary>
        public static Task<AccountCheckResult> CheckAccount(IAccountSettings account, IHttpClientFactory httpClientFactory) =>
            CheckAccount(account, httpClientFactory, RequestTimeout);

        /// <summary>
        /// <see cref="CheckAccount(IAccountSettings, IHttpClientFactory)"/> with the server given
        /// <paramref name="timeout"/> to answer instead of <see cref="RequestTimeout"/>. Taken apart so
        /// that a server that never answers can be checked against a timeout short enough to wait out.
        /// </summary>
        internal static async Task<AccountCheckResult> CheckAccount(IAccountSettings account, IHttpClientFactory httpClientFactory, TimeSpan timeout)
        {
            try
            {
                // The frame's own client, whose registration is what keeps the key below out of the
                // request log.
                var httpClient = httpClientFactory.CreateClient(PooledImmichFrameLogic.HttpClientName);
                httpClient.UseApiKey(account.ApiKey);
                var immichApi = new ImmichApi(account.ImmichServerUrl, httpClient);

                using var cts = new CancellationTokenSource(timeout);
                var version = await immichApi.GetServerVersionAsync(cts.Token);
                var versionString = $"{version.Major}.{version.Minor}.{version.Patch}";

                if (!IsSupported(version))
                {
                    return new AccountCheckResult(false,
                        $"Immich server {account.ImmichServerUrl} is running v{versionString}, but this version of ImmichFrame requires Immich v{MinimumSupportedVersion} or newer. Please update your Immich server.",
                        versionString);
                }

                return new AccountCheckResult(true,
                    $"Immich server {account.ImmichServerUrl} is running v{versionString}", versionString);
            }
            catch (Exception ex)
            {
                return new AccountCheckResult(false,
                    $"Could not determine Immich server version for {account.ImmichServerUrl}: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Checks and logs the version of every Immich server configured by any profile.
        /// </summary>
        /// <returns>
        /// <c>true</c> only if every configured Immich server was reachable and reported a version of
        /// v<see cref="MinimumSupportedVersion"/> or newer.
        /// </returns>
        public static async Task<bool> CheckServerVersions(IServiceProvider services, ILogger logger)
        {
            IEnumerable<IAccountSettings> accounts;
            try
            {
                // Resolved from the catalog rather than IServerSettings: that is scoped now, and
                // there is no request scope at startup. It also means every profile's servers get
                // checked, not just the default configuration's.
                accounts = AllAccounts(services.GetRequiredService<IConfigCatalog>());
            }
            catch (Exception ex)
            {
                logger.LogCritical("Could not check Immich server versions, config could not be loaded: {Message}", ex.Message);
                return false;
            }

            var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

            // In parallel: one unreachable server must not add its timeout to all the others
            var results = await Task.WhenAll(accounts.Select(account => CheckAccount(account, httpClientFactory)));

            foreach (var result in results)
            {
                if (result.Success)
                {
                    logger.LogInformation("{Message}", result.Message);
                }
                else
                {
                    logger.LogCritical("{Message}", result.Message);
                }
            }

            return results.All(r => r.Success);
        }

        /// <summary>
        /// Every account across the default configuration and all profiles, deduplicated - profiles
        /// commonly share accounts, and the same server does not need checking twice. Materialized
        /// so a configuration problem surfaces at the call site rather than mid-iteration.
        /// </summary>
        private static IEnumerable<IAccountSettings> AllAccounts(IConfigCatalog catalog) =>
            catalog.ProfileNames
                .Select(catalog.Get)
                .Prepend(catalog.Default)
                .SelectMany(settings => settings.Accounts)
                .DistinctBy(account => (account.ImmichServerUrl, account.ApiKey))
                .ToList();

        /// <summary>
        /// Whether <paramref name="version"/> is <see cref="MinimumSupportedVersion"/> or newer. A
        /// pre-release comes before the release it leads up to, so a pre-release of the minimum itself
        /// is older than the minimum and may lack what ImmichFrame needs from it. A pre-release of
        /// any later version is still newer than the minimum.
        /// </summary>
        private static bool IsSupported(ServerVersionResponseDto version)
        {
            var numeric = new Version((int)version.Major, (int)version.Minor, (int)version.Patch);
            if (numeric == MinimumSupportedVersion && version.Prerelease != null)
                return false;

            return numeric >= MinimumSupportedVersion;
        }
    }
}
