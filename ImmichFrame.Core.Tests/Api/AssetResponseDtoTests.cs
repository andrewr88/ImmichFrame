using ImmichFrame.Core.Api;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Api;

[TestFixture]
public class AssetResponseDtoTests
{
    [Test]
    public void ThumbhashImage_Thumbhash_IsTheDecodedPlaceholder()
    {
        // Arrange - a real thumbhash, base64 as Immich sends it
        var asset = new AssetResponseDto { Thumbhash = "I0cMCQS94XmImZeXmYd3d3g=" };

        // Act
        using var image = asset.ThumbhashImage;

        // Assert
        Assert.That(image, Is.Not.Null.And.Property(nameof(Stream.Length)).GreaterThan(0));
    }

    /// <summary>
    /// A thumbhash that cannot be decoded costs the asset its placeholder, which the image is shown
    /// without, rather than the request for the image: the exception reached the controller, and
    /// answered the frame with a 500.
    /// </summary>
    [TestCase("not base64!!")]  // FormatException, from the base64
    [TestCase("")]              // ArgumentOutOfRangeException: no bytes, where a thumbhash has five at least
    [TestCase("AAAA")]          // ArgumentOutOfRangeException: three bytes
    [TestCase("AAAAAAAA")]      // IndexOutOfRangeException: six bytes, but fewer than its own header says it has
    public void ThumbhashImage_ThumbhashThatDoesNotDecode_IsNull(string thumbhash)
    {
        // Arrange
        var asset = new AssetResponseDto { Thumbhash = thumbhash };

        // Act / Assert
        Assert.That(asset.ThumbhashImage, Is.Null);
    }
}
