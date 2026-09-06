using ImmichFrame.Core.Api;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers;

/// <summary>
/// Reads album, person and tag lists off an Immich server so that the configuration editor can offer
/// names where the settings file stores identifiers - "Holidays 2024" rather than a GUID an
/// administrator has to go and copy out of Immich's own URL bar.
/// <para>
/// <strong>The three list endpoints are POST, and that is not a REST slip.</strong> A request may
/// carry an inline API key for an account that has not been saved yet, and a key in a URL is a key in
/// the access log, in every proxy log along the way, and in the browser's history. A body keeps it out
/// of all three. The one GET here, the person thumbnail, is a GET precisely because it cannot carry
/// credentials: an <c>&lt;img src&gt;</c> cannot POST, so it takes a saved-account handle and nothing
/// else.
/// </para>
/// <para>
/// Every action carries the <c>AdminOnly</c> policy - authentication against the admin cookie
/// <em>plus</em> the allowlist. Nothing here may copy <c>AdminSessionController.Logout</c>'s
/// scheme-only shape, which authenticates without consulting the allowlist; these endpoints hand out
/// the contents of an Immich library and will make an outbound request to a URL of the caller's
/// choosing. <see cref="AdminEndpointGuard"/> refuses to start a host that gets this wrong.
/// </para>
/// <para>
/// Nothing is cached. Configuration editing is rare, a stale album list is worse than a slow one, and
/// the frame's <c>IApiCache</c> is keyed for a running slideshow rather than for a form.
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/immich")]
[Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
public class AdminImmichController(
    ILogger<AdminImmichController> _logger,
    AdminImmichAccounts _accounts) : ControllerBase
{
    /// <summary>People per request. Immich caps this parameter at 1000 and defaults it to 500.</summary>
    private const int PeoplePageSize = 500;

    /// <summary>
    /// The most people one request will read. A library with more than this is real, and the response
    /// says it was cut rather than pretending the rest do not exist.
    /// </summary>
    private const int MaxPeople = 5000;

    /// <summary>
    /// The most pages one request will ask for, as a second cap on top of <see cref="MaxPeople"/>.
    /// <para>
    /// Two caps, because a count of people is not on its own a bound. A server answering
    /// <c>{"hasNextPage": true, "people": []}</c> never raises the count, so the accumulated cap is
    /// never reached and the loop asks for page after page forever - unbounded outbound requests at
    /// a host the administrator named. The per-request timeout does not help: it bounds each call,
    /// not how many are made.
    /// </para>
    /// </summary>
    private const int MaxPeoplePages = MaxPeople / PeoplePageSize + 1;

    /// <summary>Every album on the account, in whatever order Immich returns them.</summary>
    [HttpPost("albums", Name = "GetAdminImmichAlbums")]
    [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
    public async Task<ActionResult<IReadOnlyList<AdminImmichAlbumDto>>> GetAlbums(
        [FromBody] AdminImmichAccountRefDto request, CancellationToken cancellationToken)
    {
        if (!TryConnect(request, "albums", out var api, out var host, out var refusal))
        {
            return refusal;
        }

        try
        {
            var albums = await api.GetAllAlbumsAsync(null, null, null, null, null, cancellationToken);

            return albums
                .Select(album => new AdminImmichAlbumDto(album.Id, album.AlbumName, album.AssetCount))
                .ToList();
        }
        catch (Exception ex) when (IsUpstreamFailure(ex, cancellationToken))
        {
            return Upstream(ex, host, "albums");
        }
    }

    /// <summary>
    /// Every named and unnamed person on the account, paged through to completion or to
    /// <see cref="MaxPeople"/>, whichever comes first.
    /// <para>
    /// Hidden people are left out: Immich hides a face cluster precisely so it stops being offered,
    /// and a picker that offered them anyway would undo that.
    /// </para>
    /// </summary>
    [HttpPost("people", Name = "GetAdminImmichPeople")]
    [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
    public async Task<ActionResult<AdminImmichPeopleDto>> GetPeople(
        [FromBody] AdminImmichAccountRefDto request, CancellationToken cancellationToken)
    {
        if (!TryConnect(request, "people", out var api, out var host, out var refusal))
        {
            return refusal;
        }

        try
        {
            var people = new List<AdminImmichPersonDto>();
            var truncated = false;
            long total = 0;

            for (long page = 1; ; page++)
            {
                // Checked every time round rather than only inside the HTTP call: a caller who has
                // gone away should not leave this loop working through a large library on their
                // behalf, and the request token is the only thing that says so.
                cancellationToken.ThrowIfCancellationRequested();

                var response = await api.GetAllPeopleAsync(
                    null, null, page, PeoplePageSize, withHidden: false, cancellationToken);

                total = response.Total;
                people.AddRange(response.People.Select(person => new AdminImmichPersonDto(person.Id, person.Name)));

                if (response.People.Count == 0)
                {
                    // A page with nobody on it ends the read whatever the server claims comes next.
                    // Reported as truncated when it did claim more, because then the read stopped
                    // short of what the server said it had, and saying otherwise would be the same
                    // lie the cap exists to avoid.
                    truncated = response.HasNextPage == true;
                    break;
                }

                if (response.HasNextPage != true)
                {
                    break;
                }

                if (people.Count >= MaxPeople || page >= MaxPeoplePages)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
            {
                _logger.LogWarning(
                    "The people list from the Immich server at '{sanitizedHost}' was cut short at {count} of {total}.",
                    host.SanitizeString(), people.Count, total);
            }

            return new AdminImmichPeopleDto(people, total, truncated);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex, cancellationToken))
        {
            return Upstream(ex, host, "people");
        }
    }

    /// <summary>
    /// Every tag on the account. See <see cref="AdminImmichTagDto"/> for why the editor has to submit
    /// <c>value</c> and not <c>id</c> or <c>name</c>.
    /// </summary>
    [HttpPost("tags", Name = "GetAdminImmichTags")]
    [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
    public async Task<ActionResult<IReadOnlyList<AdminImmichTagDto>>> GetTags(
        [FromBody] AdminImmichAccountRefDto request, CancellationToken cancellationToken)
    {
        if (!TryConnect(request, "tags", out var api, out var host, out var refusal))
        {
            return refusal;
        }

        try
        {
            var tags = await api.GetAllTagsAsync(cancellationToken);

            return tags
                .Select(tag => new AdminImmichTagDto(tag.Id, tag.Value, tag.Name))
                .ToList();
        }
        catch (Exception ex) when (IsUpstreamFailure(ex, cancellationToken))
        {
            return Upstream(ex, host, "tags");
        }
    }

    /// <summary>
    /// One person's face thumbnail, streamed from Immich.
    /// <para>
    /// Saved accounts only, and that is a deliberate stopping point. The consumer of this is an
    /// <c>&lt;img src&gt;</c>, which cannot POST, so serving a thumbnail for an account being typed
    /// would mean either putting its API key in a URL - the thing the list endpoints are POSTs to
    /// avoid - or keeping a server-side cache of credentials the administrator never asked us to
    /// store. Neither is worth a face. During first-run setup the picker shows names without faces,
    /// and since most Immich people have no name at all, task 005 has to render an unnamed person
    /// sensibly whether or not there is a thumbnail beside it.
    /// </para>
    /// </summary>
    [HttpGet("people/{id}/thumbnail", Name = "GetAdminImmichPersonThumbnail")]
    [Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]
    // Documentation only, and worth being explicit about: ProducesAttribute rewrites an ObjectResult
    // and leaves a FileStreamResult alone, so it constrains what Swagger says and nothing else. The
    // content type actually sent is decided by ImageContentType below.
    [Produces("image/jpeg", "image/png", "image/webp")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPersonThumbnail(
        Guid id,
        [FromQuery] string? accountId,
        [FromQuery] string? version,
        [FromQuery] string? profile,
        CancellationToken cancellationToken)
    {
        // Built here rather than bound from the query as a whole, so that serverUrl and apiKey have
        // no way of arriving on this endpoint even if a caller puts them on the query string.
        var request = new AdminImmichAccountRefDto(Profile: profile, AccountId: accountId, Version: version);

        if (!TryConnect(request, "person thumbnail", out var api, out var host, out var refusal))
        {
            return refusal;
        }

        try
        {
            var thumbnail = await api.GetPersonThumbnailAsync(id, cancellationToken);

            HttpContext.Response.RegisterForDispose(thumbnail);

            // Never the upstream's own Content-Type, and never without nosniff. This response is
            // served from the origin that hosts the admin editor and holds its session cookie, so a
            // compromised or hostile Immich server answering text/html with a <script> in it would
            // be executing script on that origin. The bytes still come from there - there is no way
            // to show a thumbnail otherwise - but they are labelled by us and the browser is told
            // not to second-guess the label.
            Response.Headers["X-Content-Type-Options"] = "nosniff";

            return File(thumbnail.Stream, ImageContentType(thumbnail.Headers));
        }
        catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return Problem("That person is not on the Immich server any more.",
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex, cancellationToken))
        {
            return Upstream(ex, host, "person thumbnail");
        }
    }

    /// <summary>
    /// Resolves the account and opens a client for it, or produces the refusal to return. A request
    /// that names no account it can resolve never reaches the network.
    /// </summary>
    private bool TryConnect(
        AdminImmichAccountRefDto request,
        string operation,
        out ImmichApi api,
        out string host,
        out ObjectResult refusal)
    {
        if (!_accounts.TryResolve(request, out var account, out var error))
        {
            _logger.LogWarning("Refused an Immich {operation} request: {sanitizedReason}",
                operation, error.SanitizeString());

            api = null!;
            host = string.Empty;
            refusal = Problem(error, statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        _logger.LogDebug("Reading {operation} from the Immich server at '{sanitizedHost}'",
            operation, account.Host.SanitizeString());

        api = _accounts.Connect(account);
        host = account.Host;
        refusal = null!;
        return true;
    }

    /// <summary>
    /// What a person thumbnail is served as: the upstream's own type when it is one of the image
    /// types Immich actually produces, and JPEG otherwise.
    /// <para>
    /// An allowlist rather than a sanitiser, because the failure mode is not a malformed header but
    /// a plausible one - <c>text/html</c> is a perfectly valid Content-Type, and it is the one that
    /// turns this endpoint into script execution on the admin origin.
    /// </para>
    /// </summary>
    private static string ImageContentType(IReadOnlyDictionary<string, IEnumerable<string>> headers)
    {
        var declared = headers.TryGetValue("Content-Type", out var values) ? values.FirstOrDefault() : null;

        // Compared on the media type alone: "image/jpeg; charset=utf-8" is still a JPEG, and it is
        // the part before the parameters that decides how a browser treats the body.
        var mediaType = declared?.Split(';')[0].Trim();

        return AllowedImageTypes.FirstOrDefault(
            allowed => string.Equals(allowed, mediaType, StringComparison.OrdinalIgnoreCase)) ?? "image/jpeg";
    }

    private static readonly string[] AllowedImageTypes = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>
    /// The failures that are an ordinary part of typing a URL or a key into a form, as opposed to a
    /// bug. Everything else keeps propagating.
    /// <para>
    /// A cancellation is only ours to answer when it was not the caller hanging up: when
    /// <paramref name="cancellationToken"/> is the one that fired, the browser has gone and there is
    /// nobody left to read a message.
    /// </para>
    /// </summary>
    private static bool IsUpstreamFailure(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            ApiException => true,
            HttpRequestException => true,
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false
        };

    /// <summary>
    /// Maps an upstream failure onto a message the administrator can act on, and a status that says
    /// whose problem it is: 400 when Immich rejected the credential the request supplied, 502 when
    /// the server could not be reached or did not behave like Immich.
    /// <para>
    /// The target's own response body is never part of the answer. It is arbitrary bytes from a host
    /// this administrator named but does not necessarily control, and rendering it in the editor
    /// would make one of them the other's output.
    /// </para>
    /// </summary>
    private ObjectResult Upstream(Exception exception, string host, string operation)
    {
        switch (exception)
        {
            case ApiException { StatusCode: StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden }:
                _logger.LogWarning("The Immich server at '{sanitizedHost}' rejected the API key reading {operation}.",
                    host.SanitizeString(), operation);

                return Problem(
                    $"The Immich server at {host} rejected that API key. Check the key and try again.",
                    statusCode: StatusCodes.Status400BadRequest);

            case ApiException api:
                _logger.LogWarning("The Immich server at '{sanitizedHost}' answered {status} reading {operation}.",
                    host.SanitizeString(), api.StatusCode, operation);

                return Problem(
                    $"The Immich server at {host} answered with status {api.StatusCode}. Check that the URL points at an Immich server.",
                    statusCode: StatusCodes.Status502BadGateway);

            case ResponseTooLargeException tooLarge:
                _logger.LogWarning(
                    "The Immich server at '{sanitizedHost}' sent more than {limit} bytes reading {operation}.",
                    host.SanitizeString(), tooLarge.Limit, operation);

                // Its own message, not the one below: the server was reached and it answered. Saying
                // "could not reach" would send an administrator to check DNS for a host that is up.
                return Problem(
                    $"The Immich server at {host} sent more data than ImmichFrame will read ({tooLarge.Limit} bytes). " +
                    "Check that the URL points at an Immich server.",
                    statusCode: StatusCodes.Status502BadGateway);

            default:
                // Sanitized like every other client-influenced string: a transport exception message
                // routinely embeds the host, which came from the administrator's own form.
                _logger.LogWarning("Could not reach the Immich server at '{sanitizedHost}' reading {operation}: {sanitizedReason}",
                    host.SanitizeString(), operation, exception.Message.SanitizeString());

                return Problem(
                    $"Could not reach the Immich server at {host}. Check the URL, and that this installation can reach it.",
                    statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
