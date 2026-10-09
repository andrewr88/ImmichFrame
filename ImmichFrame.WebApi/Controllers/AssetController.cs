using System.Globalization;
using System.Text.RegularExpressions;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;
using ImmichFrame.WebApi.Helpers;
using ImmichFrame.WebApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ImmichFrame.WebApi.Controllers
{
    public class ImageResponse
    {
        public required string RandomImageBase64 { get; set; }
        public required string ThumbHashImageBase64 { get; set; }
        public required string PhotoDate { get; set; }
        public required string ImageLocation { get; set; }
    }

    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    // On the class rather than on the actions that look an asset up by id, so the same exception
    // answers the same way from every action here.
    [AssetNotFoundFilter]
    public partial class AssetController : ControllerBase
    {
        private readonly ILogger<AssetController> _logger;
        private readonly IImmichFrameLogic _logic;
        private readonly IGeneralSettings _settings;

        // Every placeholder ImageLocationFormat may contain, matched in a single pass over the
        // format so that a substituted city, state or country is never itself searched for
        // placeholders.
        [GeneratedRegex("City|State|Country")]
        private static partial Regex LocationPlaceholder();

        /// <summary>
        /// What an asset may be served as: image and video types, each of which a browser shows as
        /// media rather than opening as a document that can run script. Anything else Immich declares
        /// - text/html and image/svg+xml among them - is served as application/octet-stream.
        /// </summary>
        private static readonly string[] AssetContentTypes =
        [
            "image/jpeg", "image/png", "image/webp", "image/gif", "image/avif", "image/heic", "image/heif",
            "video/mp4", "video/quicktime", "video/webm"
        ];

        public AssetController(ILogger<AssetController> logger, IImmichFrameLogic logic, IGeneralSettings settings)
        {
            _logger = logger;
            _logic = logic;
            _settings = settings;
        }

        // 'profile' exists on the actions below only so Swashbuckle emits it as a query
        // parameter and the generated client can send it. It is deliberately never read
        // here: DI has already resolved the profile from the query string before an
        // action body runs, and a second resolution path could silently diverge.
        [HttpGet(Name = "GetAssets")]
        public async Task<List<AssetResponseDto>> GetAssets(string clientIdentifier = "", string profile = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Assets requested by '{sanitizedClientIdentifier}'", sanitizedClientIdentifier);
            return (await _logic.GetAssets()).ToList();
        }

        [HttpGet("{id}/AssetInfo", Name = "GetAssetInfo")]
        public async Task<AssetResponseDto> GetAssetInfo(Guid id, string clientIdentifier = "", string profile = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("AssetInfo '{id}' requested by '{sanitizedClientIdentifier}'", id, sanitizedClientIdentifier);

            return await _logic.GetAssetInfoById(id);
        }

        [HttpGet("{id}/AssetFaces", Name = "GetAssetFaces")]
        public async Task<IEnumerable<AssetFaceResponseDto>> GetAssetFaces(Guid id, string clientIdentifier = "", string profile = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("AssetFaces '{id}' requested by '{sanitizedClientIdentifier}'", id, sanitizedClientIdentifier);

            return await _logic.GetAssetFacesById(id);
        }

        [HttpGet("{id}/AlbumInfo", Name = "GetAlbumInfo")]
        public async Task<List<AlbumResponseDto>> GetAlbumInfo(Guid id, string clientIdentifier = "", string profile = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("AlbumInfo '{id}' requested by '{sanitizedClientIdentifier}'", id, sanitizedClientIdentifier);

            return (await _logic.GetAlbumInfoById(id)).ToList();
        }

        [Obsolete("Use GetAsset instead.")]
        [HttpGet("{id}/Image", Name = "GetImage")]
        [Produces("image/jpeg", "image/webp")]
        [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetImage(Guid id, string clientIdentifier = "", string profile = "")
        {
            return await GetAsset(id, clientIdentifier, profile, assetType: AssetTypeEnum.IMAGE);
        }

        [HttpGet("{id}/Asset", Name = "GetAsset")]
        [Produces("image/jpeg", "image/webp", "video/mp4", "video/quicktime")]
        [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
        public async Task<IActionResult> GetAsset(Guid id, string clientIdentifier = "", string profile = "", AssetTypeEnum? assetType = null)
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Asset '{id}' requested by '{sanitizedClientIdentifier}' (type hint: {assetType})", id, sanitizedClientIdentifier, assetType);

            var rangeHeader = Request.Headers.Range.FirstOrDefault();

            AssetResponse asset;
            try
            {
                asset = await _logic.GetAsset(id, assetType, rangeHeader, HttpContext.RequestAborted);
            }
            catch (ApiException ex) when (ex.StatusCode == 416)
            {
                return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // The client hung up while Immich was still answering, and there is nobody to answer.
                return new EmptyResult();
            }

            if (StartsAShowing(rangeHeader))
            {
                // Not the value logged above: the payload keeps the form existing automations match on.
                var notification = new AssetRequestedNotification(id, clientIdentifier.ToLegacyWebhookClientIdentifier());
                _ = _logic.SendWebhookNotification(notification);
            }

            // Never the upstream's own Content-Type unchecked, and never without nosniff. This response
            // is served from the origin the frame and the admin editor are served from, so a
            // compromised or hostile Immich server answering with an HTML page - or an SVG, which is an
            // image to an <img> and a document to a browser that opens it - would be running script on
            // that origin for whoever opened the asset's URL. The bytes are still Immich's, but the
            // label is ours, the browser is told not to second-guess it, and the sandbox denies a
            // response opened as a page all the same both script and the origin. Both paths below
            // serve it.
            var contentType = UpstreamContentType.Allowlisted(asset.ContentType, AssetContentTypes, "application/octet-stream");
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.ContentSecurityPolicy = "sandbox";

            Response.Headers.AcceptRanges = "bytes";

            if (asset.IsPartial && !string.IsNullOrEmpty(asset.ContentRange))
            {
                Response.Headers.ContentRange = asset.ContentRange;
                Response.StatusCode = 206;
                Response.ContentType = contentType;

                if (asset.FileStream is { CanSeek: true } && asset.FileStream.Length > 0)
                    Response.ContentLength = asset.FileStream.Length;
                else if (asset.ContentLength.HasValue)
                    Response.ContentLength = asset.ContentLength;

                // With the request's token. Kestrel goes on accepting writes to a response whose client
                // has hung up, throwing them away without an error, so without it the copy went on
                // pulling the range from Immich for nobody - for a browser's bytes=0-, the rest of the
                // video, every time a frame moved on before one had finished downloading. File() below
                // stops the same way for a whole asset.
                using (asset.Owner)
                {
                    try
                    {
                        await asset.FileStream.CopyToAsync(Response.Body, HttpContext.RequestAborted);
                    }
                    catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        // Nobody is left to answer, which is no error of this action's.
                    }
                }
                return new EmptyResult();
            }

            if (asset.Owner != null)
                HttpContext.Response.RegisterForDispose(asset.Owner);

            return File(asset.FileStream, contentType, asset.FileName, enableRangeProcessing: true);
        }

        [HttpGet("RandomImageAndInfo", Name = "GetRandomImageAndInfo")]
        [Produces("application/json")]
        public async Task<ImageResponse> GetRandomImageAndInfo(string clientIdentifier = "", string profile = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Random image requested by '{sanitizedClientIdentifier}'", sanitizedClientIdentifier);

            AssetResponseDto? randomAsset = null;
            const int maxAttempts = 10;
            for (int i = 0; i < maxAttempts; i++)
            {
                var candidate = await _logic.GetNextAsset();
                if (candidate == null) break;
                if (candidate.Type == AssetTypeEnum.IMAGE)
                {
                    randomAsset = candidate;
                    break;
                }
            }

            if (randomAsset == null)
                throw new AssetNotFoundException("No image asset was found");

            var asset = await _logic.GetAsset(randomAsset.Id, AssetTypeEnum.IMAGE);

            // Not the value logged above: the payload keeps the form existing automations match on.
            var notification = new AssetRequestedNotification(randomAsset.Id, clientIdentifier.ToLegacyWebhookClientIdentifier());
            _ = _logic.SendWebhookNotification(notification);

            // The image is copied out whole, so nothing reads the asset after this and releasing it
            // is up to this action. Owner is null for an image, but the stream still needs closing:
            // with DownloadImages on it is a FileStream in the image cache, and would hold its
            // handle until the finalizer got to it.
            string randomImageBase64;
            using (asset.Owner)
            await using (asset.FileStream)
            using (var memoryStream = new MemoryStream())
            {
                await asset.FileStream.CopyToAsync(memoryStream);
                randomImageBase64 = Convert.ToBase64String(memoryStream.ToArray());
            }

            // Immich's schema allows an asset without a thumbhash, and ThumbhashImage is null for
            // one. A missing placeholder is no reason to fail the image it belongs to.
            string thumbHashBase64 = string.Empty;
            if (randomAsset.ThumbhashImage is { } thumbhashImage)
            {
                byte[] byteArray = new byte[thumbhashImage.Length];
                thumbhashImage.Read(byteArray, 0, byteArray.Length);
                thumbHashBase64 = Convert.ToBase64String(byteArray);
            }

            CultureInfo cultureInfo = PhotoDateCulture(_settings.Language);
            // PhotoDateFormat is null when the setting is left empty. The fallback is the web
            // client's (asset-info.svelte) rather than the "MM/dd/yyyy" an absent key gets, so one
            // configuration dates a photo the same way on every client.
            string photoDateFormat = (_settings.PhotoDateFormat ?? "dd.MM.yyyy").Replace("''", "\\'");
            string photoDate = randomAsset.LocalDateTime.ToString(photoDateFormat, cultureInfo) ?? string.Empty;

            var locationFormat = _settings.ImageLocationFormat ?? "City,State,Country";
            var imageLocation = LocationPlaceholder().Replace(locationFormat, placeholder => placeholder.Value switch
            {
                "City" => randomAsset.ExifInfo?.City,
                "State" => randomAsset.ExifInfo?.State,
                _ => randomAsset.ExifInfo?.Country
            } ?? string.Empty);
            imageLocation = string.Join(",", imageLocation.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)));

            return new ImageResponse
            {
                RandomImageBase64 = randomImageBase64,
                ThumbHashImageBase64 = thumbHashBase64,
                PhotoDate = photoDate,
                ImageLocation = imageLocation
            };
        }

        /// <summary>
        /// Whether a request for an asset is the one that puts it up on a frame - the request the
        /// webhook is notified for - rather than one more piece of a video already playing.
        /// <para>
        /// No Range at all is the whole asset, which is how an image is asked for. A video never is: a
        /// browser's &lt;video&gt; asks for a range from its very first request - Chromium and Firefox
        /// for bytes=0-, and WebKit for bytes=0-1 as a probe before bytes=0-N - and goes on from there
        /// one range at a time. So a single range from the first byte puts a video up too, unless it
        /// is that probe, which is never asked for alone; any other range is a seek, or the next
        /// stretch of a video already up, and notifying for those would tell an automation the video
        /// was shown as many times as a piece of it was asked for. A video shown again, or a frame
        /// reloaded, starts from the first byte once more - a showing of its own, as it should be.
        /// </para>
        /// <para>
        /// Compared as the parsed range rather than as text, which can spell one range more than one way.
        /// </para>
        /// </summary>
        private static bool StartsAShowing(string? rangeHeader)
        {
            if (string.IsNullOrEmpty(rangeHeader)) return true;

            return RangeHeaderValue.TryParse(rangeHeader, out var range)
                && range.Ranges.Count == 1
                && range.Ranges.Single() is { From: 0, To: not 1 };
        }

        /// <summary>
        /// The culture a photo date is formatted in. <c>Language</c> is free text that nothing
        /// validates - "en US" names no culture, and an empty YAML key binds it to null - so a
        /// value no culture answers to costs the date its localisation rather than failing the
        /// response.
        /// </summary>
        private static CultureInfo PhotoDateCulture(string? language)
        {
            if (language is null) return CultureInfo.InvariantCulture;

            try
            {
                return new CultureInfo(language);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.InvariantCulture;
            }
        }
    }
}
