using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;
using ImmichFrame.WebApi.Controllers;
using ImmichFrame.WebApi.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers;

/// <summary>
/// <see cref="AssetController.GetRandomImageAndInfo"/> called directly against a fake
/// <see cref="IImmichFrameLogic"/>, for what the action itself does with the asset it is handed:
/// how it releases it, and how it renders the settings and metadata that come with it. The round
/// trip through a real host is <see cref="AssetControllerTests"/>' business.
/// </summary>
[TestFixture]
public class AssetControllerRandomImageAndInfoTests
{
    // A real thumbhash, so the placeholder is decoded the way one sent by Immich would be.
    private const string Thumbhash = "I0cMCQS94XmImZeXmYd3d3g=";

    private static readonly byte[] ImageBytes = [0xFF, 0xD8, 0xFF, 0xE0]; // Minimal JPEG

    private Mock<IImmichFrameLogic> _logic;
    private GeneralSettings _settings;
    private AssetResponseDto _asset;

    [SetUp]
    public void Setup()
    {
        _asset = new AssetResponseDto
        {
            Id = Guid.NewGuid(),
            Type = AssetTypeEnum.IMAGE,
            Thumbhash = Thumbhash,
            LocalDateTime = DateTimeOffset.Parse("2023-10-26T10:00:00Z"),
        };

        _settings = new GeneralSettings
        {
            Language = "en",
            PhotoDateFormat = "MM/dd/yyyy",
            ImageLocationFormat = "City,State,Country",
        };

        _logic = new Mock<IImmichFrameLogic>();
        _logic.Setup(logic => logic.GetNextAsset()).ReturnsAsync(_asset);
        _logic.Setup(logic => logic.GetAsset(_asset.Id, AssetTypeEnum.IMAGE, It.IsAny<string?>()))
            .ReturnsAsync(() => Image(new MemoryStream(ImageBytes, writable: false)));
        _logic.Setup(logic => logic.SendWebhookNotification(It.IsAny<IWebhookNotification>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// The image is copied into the response whole, so nothing reads the asset once the action
    /// returns and the action is the one that has to release it. With <c>DownloadImages</c> on,
    /// the stream is a <see cref="FileStream"/> in the image cache: left to the finalizer it holds
    /// a file handle per request - and when it is the stream the cache wrote the image through,
    /// an exclusive lock that fails the next request for that image too.
    /// </summary>
    [Test]
    public async Task GetRandomImageAndInfo_DisposesTheAssetStreamAndItsOwner()
    {
        // Arrange
        var stream = new TrackingStream(ImageBytes);
        var owner = new TrackingDisposable();
        _logic.Setup(logic => logic.GetAsset(_asset.Id, AssetTypeEnum.IMAGE, It.IsAny<string?>()))
            .ReturnsAsync(Image(stream, owner));

        // Act
        var response = await CreateController().GetRandomImageAndInfo();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.RandomImageBase64, Is.EqualTo(Convert.ToBase64String(ImageBytes)),
                "the image is read out before anything is released");
            Assert.That(stream.Disposed, Is.True, "the asset's stream");
            Assert.That(owner.Disposed, Is.True, "and whatever owns it");
        });
    }

    /// <summary>
    /// Each placeholder in <c>ImageLocationFormat</c> is replaced once, and what replaced it is
    /// never searched for placeholders itself. Substituting them one after another turned
    /// Statesboro into "Georgiasboro" as soon as State came round, because the city had already
    /// been written into the string being searched.
    /// </summary>
    [TestCase("Springfield", "Illinois", "USA", "Springfield,Illinois,USA")]
    [TestCase("Statesboro", "Georgia", "United States", "Statesboro,Georgia,United States")]
    [TestCase("Country Club Hills", "Illinois", "USA", "Country Club Hills,Illinois,USA")]
    public async Task GetRandomImageAndInfo_ImageLocation_SubstitutesEachPlaceholderExactlyOnce(
        string city, string state, string country, string expected)
    {
        // Arrange
        _asset.ExifInfo = new ExifResponseDto { City = city, State = state, Country = country };

        // Act
        var response = await CreateController().GetRandomImageAndInfo();

        // Assert
        Assert.That(response.ImageLocation, Is.EqualTo(expected));
    }

    /// <summary>
    /// Inputs nothing validates, each of which used to fail the whole response with a 500 rather
    /// than cost it one detail: Immich sends no thumbhash for some assets, and
    /// <c>PhotoDateFormat</c> and <c>Language</c> are free text in the settings - null, too, where
    /// a YAML key is left empty.
    /// </summary>
    [TestCase(null, "MM/dd/yyyy", "en", "10/26/2023",
        TestName = "GetRandomImageAndInfo_AssetWithoutAThumbhash_ReturnsTheImageWithoutAPlaceholder")]
    [TestCase(Thumbhash, null, "en", "26.10.2023",
        TestName = "GetRandomImageAndInfo_NoPhotoDateFormat_FormatsTheDateAsTheWebClientDoes")]
    [TestCase(Thumbhash, "MM/dd/yyyy", "en US", "10/26/2023",
        TestName = "GetRandomImageAndInfo_LanguageNamingNoCulture_FormatsTheDateInvariantly")]
    [TestCase(Thumbhash, "MM/dd/yyyy", null, "10/26/2023",
        TestName = "GetRandomImageAndInfo_NoLanguage_FormatsTheDateInvariantly")]
    public async Task GetRandomImageAndInfo_UnusableInput_StillReturnsTheImage(
        string? thumbhash, string? photoDateFormat, string? language, string expectedPhotoDate)
    {
        // Arrange - the generated client and the settings both declare these non-nullable, but
        // null is what actually arrives.
        _asset.Thumbhash = thumbhash!;
        _settings.PhotoDateFormat = photoDateFormat;
        _settings.Language = language!;

        // Act
        var response = await CreateController().GetRandomImageAndInfo();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.RandomImageBase64, Is.EqualTo(Convert.ToBase64String(ImageBytes)));
            Assert.That(response.PhotoDate, Is.EqualTo(expectedPhotoDate));
            Assert.That(response.ThumbHashImageBase64, thumbhash is null ? Is.Empty : Is.Not.Empty);
        });
    }

    /// <summary>
    /// The client identifier a webhook receives is a contract with whatever automation reads it, so it
    /// stays what it has always been - punctuation stripped, 'Frame_Kitchen' arriving as
    /// 'FrameKitchen' - now that the log's sanitiser no longer strips punctuation.
    /// </summary>
    [Test]
    public async Task GetRandomImageAndInfo_WebhookNotification_CarriesTheClientIdentifierAsItAlwaysHas()
    {
        // Arrange
        IWebhookNotification? sent = null;
        _logic.Setup(logic => logic.SendWebhookNotification(It.IsAny<IWebhookNotification>()))
            .Callback<IWebhookNotification>(notification => sent = notification)
            .Returns(Task.CompletedTask);

        // Act
        await CreateController().GetRandomImageAndInfo("Frame_Kitchen");

        // Assert
        Assert.That((sent as AssetRequestedNotification)?.ClientIdentifier, Is.EqualTo("FrameKitchen"));
    }

    private AssetController CreateController() =>
        new(NullLogger<AssetController>.Instance, _logic.Object, _settings);

    private static AssetResponse Image(Stream stream, IDisposable? owner = null) => new()
    {
        FileName = "image.jpeg",
        ContentType = "image/jpeg",
        FileStream = stream,
        Owner = owner
    };

    private sealed class TrackingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
