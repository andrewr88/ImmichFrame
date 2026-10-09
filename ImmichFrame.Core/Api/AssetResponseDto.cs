using System.Text.Json.Serialization;
using ImmichFrame.Core.Helpers;
using ThumbHashes;

namespace ImmichFrame.Core.Api
{
    public partial class AssetResponseDto
    {
        [JsonIgnore]
        public Stream? ThumbhashImage => GetThumbHashStream();
        
        public string? ImmichServerUrl { get; set; }

        private Stream? GetThumbHashStream()
        {
            if (this.Thumbhash == null)
                return null;

            // A thumbhash that cannot be decoded costs the asset its placeholder, which a frame shows
            // the image without, rather than costing the frame the image. Each failure is one the
            // input causes: base64 that is not base64 (FormatException); fewer than the five bytes
            // ThumbHash 2.1.1 checks for (ArgumentOutOfRangeException); or five and more, but fewer
            // than the hash's own header says it has - which ThumbHash does not check, and reads past
            // the end of (IndexOutOfRangeException).
            try
            {
                var hash = Convert.FromBase64String(this.Thumbhash);
                var thumbhash = new ThumbHash(hash);
                return ImageHelper.SaveDataUrlToStream(thumbhash.ToDataUrl());
            }
            catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException)
            {
                return null;
            }
        }
    }
}
