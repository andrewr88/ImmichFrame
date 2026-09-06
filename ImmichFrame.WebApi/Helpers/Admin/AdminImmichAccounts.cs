using System.Diagnostics.CodeAnalysis;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;

namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>An Immich account a picker request resolved to, ready to be talked to.</summary>
/// <param name="ServerUrl">The base URL, with any trailing slash removed.</param>
/// <param name="ApiKey">The key to authenticate with. Never leaves the server.</param>
/// <param name="Host">
/// The host out of <paramref name="ServerUrl"/>, parsed rather than substringed. It is the one part
/// of an administrator's input that is safe to put in a message back to them: <see cref="Uri"/> has
/// already established that it is a host and nothing else.
/// </param>
public record ResolvedImmichAccount(string ServerUrl, string ApiKey, string Host);

/// <summary>
/// Turns a picker request into an <see cref="ImmichApi"/> pointed at one Immich account - either one
/// already in the configuration, named by the opaque handle the configuration read issued, or one
/// the administrator is still typing.
/// <para>
/// The saved half deliberately reuses <see cref="AdminConfigService.Read"/> rather than growing a
/// second way to identify a stored account. That read is what issues the handles in the first place;
/// a second scheme would be a second thing to keep in step with the file format, and getting it
/// wrong points one Immich server's URL at another's credential, which is the bug the handle exists
/// to prevent.
/// </para>
/// <para>
/// But the read and the catalog are two different things, and the code below must not assume they
/// agree. The read is the settings file <em>as it is on disk right now</em>; the catalog is what
/// this process is <em>running</em>. They are bound from the same document by the same code and so
/// normally line up index for index - but a hand edit since startup, or a save from another
/// administrator, moves one and not the other, and the version token cannot catch it because the
/// version describes the file the read just made. So the position is used to look the account up
/// and then the resolved server URL is checked back against the one the read reported. Without that
/// check a reordered file would have the picker list host X's albums while the editor showed host
/// Y - and the administrator would then save X's album GUIDs onto Y, producing a configuration that
/// loads, validates and selects nothing.
/// </para>
/// <para>
/// Inline credentials are used for the one request that carries them and are never written down.
/// Accepting an arbitrary URL from an authenticated administrator is a server-side fetch the
/// administrator asked for, so it is bounded rather than refused: the scheme must be HTTP or HTTPS,
/// and <see cref="HttpClientName"/> carries the timeout, the redirect refusal and the response-size
/// cap that keep one typo from turning ImmichFrame into a probe of its own network.
/// </para>
/// </summary>
public sealed class AdminImmichAccounts(
    AdminConfigService _config,
    IConfigCatalog _catalog,
    IHttpClientFactory _httpClientFactory)
{
    /// <summary>
    /// The named <c>HttpClient</c> every picker request goes out on. Configured in <c>Program.cs</c>
    /// and deliberately not <c>ImmichApiAccountClient</c>: the frame's own client talks only to
    /// servers named in the settings file, while this one talks to whatever an administrator types.
    /// </summary>
    public const string HttpClientName = "AdminImmichPickerClient";

    /// <summary>How long one call to an Immich server may take before it counts as unreachable.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The primary handler behind <see cref="HttpClientName"/>, refusing to follow redirects.
    /// <para>
    /// Following one would let the host the administrator named hand the request - and the
    /// <c>X-API-KEY</c> header on it - to a host they did not. Named here rather than written inline
    /// in <c>Program.cs</c> so a test can assert the setting: the test host replaces every primary
    /// handler with a mock (<c>ImmichApiMock.UseMockHandler</c>), so there is no way to observe the
    /// real one refusing a real 302 from inside the application's own object graph.
    /// </para>
    /// </summary>
    public static HttpClientHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>
    /// The most of an Immich response ImmichFrame will read. Generous next to an album list or a
    /// face thumbnail, and small enough that a URL pointed at something that is not Immich cannot
    /// stream this process out of memory.
    /// </summary>
    public const long MaxResponseBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Resolves <paramref name="request"/> to one account, or explains why it could not be. The
    /// explanation is written for the administrator reading it in the editor.
    /// </summary>
    public bool TryResolve(
        AdminImmichAccountRefDto request,
        [NotNullWhen(true)] out ResolvedImmichAccount? account,
        [NotNullWhen(false)] out string? error)
    {
        account = null;

        var hasHandle = !string.IsNullOrWhiteSpace(request.AccountId);
        var hasInline = !string.IsNullOrWhiteSpace(request.ServerUrl) || !string.IsNullOrWhiteSpace(request.ApiKey);

        if (hasHandle && hasInline)
        {
            // Refused rather than resolved by precedence: an editor that sends both is confused about
            // which account it is asking about, and quietly picking one would answer confidently with
            // the wrong server's albums.
            error = "Name either a saved account or a server URL and API key, not both.";
            return false;
        }

        if (hasHandle)
        {
            return TryResolveSaved(request, out account, out error);
        }

        if (hasInline)
        {
            return TryResolveInline(request, out account, out error);
        }

        error = "No Immich account was named. Choose a saved account, or supply a server URL and an API key.";
        return false;
    }

    /// <summary>
    /// A fresh <see cref="ImmichApi"/> for <paramref name="account"/>. Not cached: config editing is
    /// rare, and the client comes from <c>IHttpClientFactory</c>, which already pools the connection.
    /// </summary>
    public ImmichApi Connect(ResolvedImmichAccount account)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        httpClient.UseApiKey(account.ApiKey);

        return new ImmichApi(account.ServerUrl, httpClient);
    }

    private bool TryResolveSaved(
        AdminImmichAccountRefDto request,
        [NotNullWhen(true)] out ResolvedImmichAccount? account,
        [NotNullWhen(false)] out string? error)
    {
        account = null;

        AdminConfigDto config;
        try
        {
            config = _config.Read();
        }
        catch (ImmichFrameException ex)
        {
            error = $"The configuration could not be read: {ex.Message}";
            return false;
        }

        if (string.IsNullOrEmpty(request.Version))
        {
            // Required, not optional. The staleness check below is the only thing standing between a
            // handle and a configuration that has moved since it was issued, and a request that omits
            // the version skips it entirely - so an omission has to be a refusal rather than a waiver.
            error = "The request did not say which version of the configuration the account came from. Reload the page and try again.";
            return false;
        }

        if (!string.Equals(request.Version, config.Version, StringComparison.Ordinal))
        {
            // Checked before the handle, because the handle is derived from the version: a stale one
            // simply matches nothing, and "no such account" would send an administrator looking for a
            // deleted account instead of reloading the page.
            error = "The configuration changed since the editor loaded it. Reload the page and try again.";
            return false;
        }

        var profile = string.IsNullOrWhiteSpace(request.Profile) ? ConfigCatalog.DefaultProfileName : request.Profile;

        var entry = string.Equals(profile, ConfigCatalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase)
            ? config.Default
            : config.Profiles.FirstOrDefault(p => string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            error = $"There is no configuration profile named '{profile}'.";
            return false;
        }

        var index = entry.Accounts
            .Select((stored, position) => (stored, position))
            .Where(candidate => string.Equals(candidate.stored.Id, request.AccountId, StringComparison.Ordinal))
            .Select(candidate => (int?)candidate.position)
            .FirstOrDefault();

        if (index is null)
        {
            error = "That account is not part of the saved configuration. Save it first, or type its server URL and API key.";
            return false;
        }

        // The values come from the catalog rather than from the read: the read masks every secret,
        // and the catalog's accounts have been through ValidateAndInitialize, so an account whose key
        // lives in an ApiKeyFile has the key itself here rather than a path.
        //
        // Each of the three ways that can fail gets its own message. TryGet rather than Get, because
        // a profile the file declares and the running process has never seen is a fact about the
        // installation, not a 500 - and calling it "no usable API key" would send an administrator
        // hunting for a key that is sitting right there in the file.
        // (ConfigCatalog.DefaultProfileName resolves to the default configuration in TryGet.)
        if (!_catalog.TryGet(entry.Name, out var settings))
        {
            error = $"Configuration profile '{entry.Name}' is in the settings file but not in the running configuration. " +
                    "Restart ImmichFrame to pick it up, then try again.";
            return false;
        }

        var saved = settings.Accounts.ElementAtOrDefault(index.Value);
        var declared = entry.Accounts[index.Value].ImmichServerUrl;

        // The position came from the file and is being applied to the running configuration, so it is
        // only meaningful while the two still describe the same accounts. The server URL is the part
        // of the account the read does not mask, which makes it exactly the cross-check available:
        // if it does not match, this handle no longer names the account the editor is showing, and
        // answering with some other server's albums would have the administrator save those GUIDs
        // onto an account that has never seen them.
        if (saved is null || !string.Equals(declared, saved.ImmichServerUrl, StringComparison.Ordinal))
        {
            error = "The running configuration no longer matches the settings file, so that account could not be " +
                    "identified safely. Restart ImmichFrame, then try again.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(saved.ApiKey))
        {
            error = "That account has no usable API key in the running configuration.";
            return false;
        }

        return TryDescribe(saved.ImmichServerUrl, saved.ApiKey, StoredKeyRemedy, out account, out error);
    }

    /// <summary>
    /// What to tell an administrator whose <em>stored</em> key cannot be sent. Reachable, though not
    /// at startup: <c>ImmichServerVersionChecker</c> would have refused to boot a process holding
    /// such an account, so getting here means the key arrived in a configuration saved through the
    /// editor and swapped in since. The remedy is therefore the file, not the form in front of them.
    /// </summary>
    private const string StoredKeyRemedy =
        "Fix it in the settings file - or in the file its ApiKeyFile names - and save the configuration again.";

    /// <inheritdoc cref="StoredKeyRemedy"/>
    /// <remarks>The same problem in a key being typed, where the fix is simply to paste it again.</remarks>
    private const string TypedKeyRemedy = "Copy it again from Immich.";

    private static bool TryResolveInline(
        AdminImmichAccountRefDto request,
        [NotNullWhen(true)] out ResolvedImmichAccount? account,
        [NotNullWhen(false)] out string? error)
    {
        account = null;

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            error = "An API key is required to read albums, people and tags from that server.";
            return false;
        }

        return TryDescribe(request.ServerUrl, request.ApiKey, TypedKeyRemedy, out account, out error);
    }

    private static bool TryDescribe(
        string? serverUrl,
        string? apiKey,
        string unusableKeyRemedy,
        [NotNullWhen(true)] out ResolvedImmichAccount? account,
        [NotNullWhen(false)] out string? error)
    {
        account = null;

        // Absolute and HTTP(S) only. ImmichApi appends "/api" to whatever it is given, so a file: or
        // a relative URL would not fail as a bad address so much as become a different kind of
        // request altogether.
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "The Immich server URL must be an absolute http:// or https:// address.";
            return false;
        }

        // Trimmed for the same reason profile names are (0f0be55): an API key is copied out of a
        // terminal or read from a file, and it arrives carrying the newline that came with it. The
        // key becomes an X-API-KEY header, and HttpHeaders.Add throws FormatException on a control
        // character - outside every catch in the controller, so a pasted trailing newline would be a
        // 500 on exactly the first-run path this endpoint exists to serve.
        var key = apiKey?.Trim() ?? string.Empty;

        // char.IsControl is deliberately stricter than the header rules, which only refuse below
        // 0x20 and 0x7F. Refusing a little more turns an unusable key into a message rather than an
        // exception, and no Immich API key contains one of these.
        //
        // The remedy comes from the caller, because the two callers are answering different people:
        // one is looking at a key they are typing, the other at one that is already in the settings
        // file, and telling the second to copy it again from Immich is advice about a form they are
        // not on.
        if (key.Length == 0 || key.Any(char.IsControl))
        {
            error = $"That API key is empty, or contains a character an HTTP header cannot carry. {unusableKeyRemedy}";
            return false;
        }

        account = new ResolvedImmichAccount(serverUrl!.Trim().TrimEnd('/'), key, uri.Host);
        error = null;
        return true;
    }
}
