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
                asset = await _logic.GetAsset(id, assetType, rangeHeader);
            }
            catch (ApiException ex) when (ex.StatusCode == 416)
            {
                return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            if (string.IsNullOrEmpty(rangeHeader))
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

                using (asset.Owner)
                {
                    await asset.FileStream.CopyToAsync(Response.Body);
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
