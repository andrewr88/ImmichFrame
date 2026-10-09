using NUnit.Framework;
using Moq;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace ImmichFrame.Core.Tests.Logic.Pool;

[TestFixture]
public class MemoryAssetsPoolTests
{
    private Mock<ImmichApi> _mockImmichApi;
    private Mock<IAccountSettings> _mockAccountSettings;
    private MemoryAssetsPool _memoryAssetsPool;

    [SetUp]
    public void Setup()
    {
        _mockImmichApi = new Mock<ImmichApi>(null!, null!); // Base constructor requires ILogger, IHttpClientFactory, IOptions, pass null
        _mockAccountSettings = new Mock<IAccountSettings>();

        _memoryAssetsPool = new MemoryAssetsPool(_mockImmichApi.Object, _mockAccountSettings.Object);
    }

    private List<AssetResponseDto> CreateSampleAssets(int count, bool withExif, int yearCreated, AssetTypeEnum assetType)
    {
        var assets = new List<AssetResponseDto>();
        for (int i = 0; i < count; i++)
        {
            var extension = assetType == AssetTypeEnum.IMAGE ? "jpg" : "mp4";
            var asset = new AssetResponseDto
            {
                Id = Guid.NewGuid(),
                OriginalPath = $"/path/to/image{i}.{extension}",
                Type = assetType,
                ExifInfo = withExif ? new ExifResponseDto { DateTimeOriginal = new DateTime(yearCreated, 1, 1) } : null,
                People = new List<PersonResponseDto>()
            };
            assets.Add(asset);
        }
        return assets;
    }

    private List<MemoryResponseDto> CreateSampleImageMemories(int memoryCount, int assetsPerMemory, bool withExifInAssets, int memoryYear)
    {
        return CreateSampleMemories(memoryCount, assetsPerMemory, withExifInAssets, memoryYear, AssetTypeEnum.IMAGE);
    }

    private List<MemoryResponseDto> CreateSampleVideoMemories(int memoryCount, int assetsPerMemory, bool withExifInAssets, int memoryYear)
    {
        return CreateSampleMemories(memoryCount, assetsPerMemory, withExifInAssets, memoryYear, AssetTypeEnum.VIDEO);
    }
    private List<MemoryResponseDto> CreateSampleMemories(int memoryCount, int assetsPerMemory, bool withExifInAssets, int memoryYear, AssetTypeEnum assetType)
    {
        var memories = new List<MemoryResponseDto>();
        for (int i = 0; i < memoryCount; i++)
        {
            var memory = new MemoryResponseDto
            {
                Id = Guid.NewGuid(),
                Assets = CreateSampleAssets(assetsPerMemory, withExifInAssets, memoryYear, assetType),
                Data = new OnThisDayDto { Year = memoryYear }
            };
            memories.Add(memory);
        }
        return memories;
    }

    [Test]
    public async Task LoadAssets_CallsSearchMemoriesAsync()
    {
        // Arrange
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryResponseDto>());

        // Act
        // Access protected method via reflection for testing, or make it internal/public if design allows
        // For now, we assume LoadAssets is implicitly called by a public method of CachingApiAssetsPool (e.g. GetAsset)
        // Let's simulate this by calling a method that would trigger LoadAssets if cache is empty.
        // Since LoadAssets is protected, we'll test its effects via GetAsset.
        // We need to ensure the cache is empty or expired for LoadAssets to be called.
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None); // This should trigger LoadAssets

        // Assert
        _mockImmichApi.Verify(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_SearchesTheMemoriesOfToday()
    {
        // Arrange
        DateTimeOffset? searchDate = null;
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset?, bool?, bool?, MemorySearchOrder?, long?, MemoryType?, CancellationToken>((date, _, _, _, _, _, _) => searchDate = date)
            .ReturnsAsync(new List<MemoryResponseDto>());

        // Act
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None);

        // Assert - Immich works out which memories are "on this day" from the date it is given
        Assert.That(searchDate, Is.EqualTo(DateTimeOffset.Now).Within(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task LoadAssets_VideosNotShown_DoesNotFetchAssetInfoForAVideo()
    {
        // Arrange - a video without ExifInfo, which would be fetched only for the account's filters
        // to drop the video afterwards
        var memories = CreateSampleVideoMemories(1, 1, false, DateTime.Now.Year - 2);

        _mockAccountSettings.Setup(x => x.ShowVideos).Returns(false);
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);
        _mockImmichApi.Setup(x => x.GetAssetInfoAsync(It.IsAny<Guid>(), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetResponseDto { ExifInfo = new ExifResponseDto(), People = new List<PersonResponseDto>() });

        // Act
        var result = await _memoryAssetsPool.GetAssets(1, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty);
        _mockImmichApi.Verify(x => x.GetAssetInfoAsync(It.IsAny<Guid>(), null, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task LoadAssets_FetchesAssetInfo_WhenExifInfoIsNull()
    {
        // Arrange
        var memoryYear = DateTime.Now.Year - 2;
        var memories = CreateSampleImageMemories(1, 1, false, memoryYear); // Asset without ExifInfo
        var assetId = memories[0].Assets.First().Id;

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);
        _mockImmichApi.Setup(x => x.GetAssetInfoAsync(assetId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetResponseDto { Id = assetId, ExifInfo = new ExifResponseDto { DateTimeOriginal = new DateTime(memoryYear, 1, 1) }, People = new List<PersonResponseDto>() });

        // Act
        var resultAsset = (await _memoryAssetsPool.GetAssets(1, CancellationToken.None)).First(); // Triggers LoadAssets

        // Assert
        _mockImmichApi.Verify(x => x.GetAssetInfoAsync(assetId, null, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(resultAsset.ExifInfo, Is.Not.Null);
        Assert.That(resultAsset.ExifInfo.Description, Is.EqualTo("2 years ago"));
    }

    [Test]
    public async Task LoadAssets_SetsDescription_WhenAssetInfoHasNoExifInfo()
    {
        // Arrange
        var memoryYear = DateTime.Now.Year - 2;
        var memories = CreateSampleImageMemories(1, 1, false, memoryYear); // Asset without ExifInfo
        var assetId = memories[0].Assets.First().Id;

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);
        // ExifInfo is optional in the Immich API, so the full asset info can lack it too
        _mockImmichApi.Setup(x => x.GetAssetInfoAsync(assetId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetResponseDto { Id = assetId, ExifInfo = null });

        // Act
        var resultAsset = (await _memoryAssetsPool.GetAssets(1, CancellationToken.None)).First(); // Triggers LoadAssets

        // Assert
        Assert.That(resultAsset.ExifInfo, Is.Not.Null);
        Assert.That(resultAsset.ExifInfo.Description, Is.EqualTo("2 years ago"));
    }

    [Test]
    public async Task LoadAssets_DoesNotFetchAssetInfo_WhenExifInfoIsPresent()
    {
        // Arrange
        var memoryYear = DateTime.Now.Year - 1;
        var memories = CreateSampleImageMemories(1, 1, true, memoryYear); // Asset with ExifInfo
        var assetId = memories[0].Assets.First().Id;

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        // Act
        var resultAsset = (await _memoryAssetsPool.GetAssets(1, CancellationToken.None)).First(); // Triggers LoadAssets

        // Assert
        _mockImmichApi.Verify(x => x.GetAssetInfoAsync(It.IsAny<Guid>(), null, null, It.IsAny<CancellationToken>()), Times.Never);
        Assert.That(resultAsset.ExifInfo, Is.Not.Null);
        Assert.That(resultAsset.ExifInfo.Description, Is.EqualTo("1 year ago"));
    }

    [Test]
    public async Task LoadAssets_CorrectlyFormatsDescription_YearsAgo()
    {
        // Arrange
        var currentYear = DateTime.Now.Year;
        var testCases = new[]
        {
            new { year = currentYear - 1, expectedDesc = "1 year ago" },
            new { year = currentYear - 5, expectedDesc = "5 years ago" },
            new { year = currentYear, expectedDesc = "0 years ago" } // Or "This year" depending on desired logic, current is "0 years ago"
        };

        foreach (var tc in testCases)
        {
            var memories = CreateSampleImageMemories(1, 1, true, tc.year);
            memories[0].Assets.First().ExifInfo.DateTimeOriginal = new DateTime(tc.year, 1, 1); // Ensure Exif has the year

            _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(memories);

            _memoryAssetsPool = new MemoryAssetsPool(_mockImmichApi.Object, _mockAccountSettings.Object);


            // Act
            var resultAsset = (await _memoryAssetsPool.GetAssets(1, CancellationToken.None)).First(); // Triggers LoadAssets

            // Assert
            Assert.That(resultAsset.ExifInfo, Is.Not.Null);
            Assert.That(resultAsset.ExifInfo.Description, Is.EqualTo(tc.expectedDesc), $"Failed for year {tc.year}");
        }
    }

    [Test]
    public async Task LoadAssets_AggregatesAssetsFromMultipleMemories()
    {
        var imageMemoriesCount = 2;
        var videoMemoriesCount = 2;
        var assetsPerMemory = 2;

        // Arrange
        var memoryYear = DateTime.Now.Year - 3;
        var memories = CreateSampleImageMemories(imageMemoriesCount, assetsPerMemory, true, memoryYear); // 2 memories, 2 assets each
        memories.AddRange(CreateSampleVideoMemories(videoMemoriesCount, assetsPerMemory, true, memoryYear)); // 2 video memories, 2 assets each

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories).Verifiable(Times.Once);

        // Act
        // We will rely on the fact that the factory in GetFromCacheAsync is called, and it returns the list.
        // The count can be indirectly verified if we could access the pool's internal list after LoadAssets.

        // Let's refine the test to ensure LoadAssets returns the correct number of assets.
        // We need a way to inspect the result of LoadAssets directly.
        // We can make LoadAssets internal and use InternalsVisibleTo, or use reflection.
        // Or, we can rely on the setup of GetFromCacheAsync to capture the factory's result.
        var loadedAssets = await _memoryAssetsPool.GetAssets(memories.Count * assetsPerMemory, CancellationToken.None); // Trigger load

        // Assert
        Assert.That(loadedAssets, Is.Not.Null);
        Assert.That(loadedAssets.All(x => x.Type == AssetTypeEnum.IMAGE), Is.True);
        Assert.That(loadedAssets.Count(), Is.EqualTo(4)); // 2 memories * 2 assets
        _mockImmichApi.VerifyAll();

    }

    [Test]
    public async Task LoadAssets_AggregatesAssetsFromMultipleMemories_WithVideo()
    {
        var imageMemoriesCount = 2;
        var videoMemoriesCount = 2;
        var assetsPerMemory = 2;

        // Arrange
        var memoryYear = DateTime.Now.Year - 3;
        var memories = CreateSampleImageMemories(imageMemoriesCount, assetsPerMemory, true, memoryYear); // 2 memories, 2 assets each
        memories.AddRange(CreateSampleVideoMemories(videoMemoriesCount, assetsPerMemory, true, memoryYear)); // 2 video memories, 2 assets each

        _mockAccountSettings.Setup(x => x.ShowVideos).Returns(true);
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories).Verifiable(Times.Once);

        // Act
        // We will rely on the fact that the factory in GetFromCacheAsync is called, and it returns the list.
        // The count can be indirectly verified if we could access the pool's internal list after LoadAssets.

        // Let's refine the test to ensure LoadAssets returns the correct number of assets.
        // We need a way to inspect the result of LoadAssets directly.
        // We can make LoadAssets internal and use InternalsVisibleTo, or use reflection.
        // Or, we can rely on the setup of GetFromCacheAsync to capture the factory's result.
        var loadedAssets = await _memoryAssetsPool.GetAssets(memories.Count * assetsPerMemory, CancellationToken.None); // Trigger load

        // Assert
        Assert.That(loadedAssets, Is.Not.Null);
        Assert.That(loadedAssets.Count(), Is.EqualTo(8)); // 4 memories * 2 assets
        _mockImmichApi.VerifyAll();

    }

    [TestCase("12:00:00", TestName = "ExpiryFor_MidDay_IsTheMidnightEndingThatDay")]
    [TestCase("23:59:59.9999999", TestName = "ExpiryFor_JustBeforeMidnight_IsThatMidnight")]
    [TestCase("00:00:00", TestName = "ExpiryFor_ExactlyMidnight_IsAFullDayLater")]
    public void ExpiryFor_TimeOfDay_IsTheMidnightEndingThatDay(TimeSpan timeOfDay)
    {
        // Arrange - a local time, as DateTimeOffset.Now gives one. The memories Immich finds are the
        // ones "on this day", so they are good until the day is out: not past already, not for a
        // moment, and not for a year
        var now = new DateTimeOffset(new DateTime(2026, 6, 10) + timeOfDay);

        // Act
        var expiry = DailyApiCache.ExpiryFor(now);

        // Assert - local midnight between the 10th and the 11th
        Assert.That(expiry, Is.EqualTo(new DateTimeOffset(new DateTime(2026, 6, 11))));
    }

    [Test]
    public async Task GetAssets_AgainTheSameDay_DoesNotSearchTheMemoriesAgain()
    {
        // Arrange - through the pool's own DailyApiCache, so that it is the expiry it actually sets
        // that is tested. A run that straddles midnight would rightly search twice.
        Assume.That(DateTime.Now.TimeOfDay, Is.LessThan(new TimeSpan(23, 59, 50)),
            "too close to midnight, when the day's memories rightly expire");
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleImageMemories(1, 1, true, DateTime.Now.Year - 1));

        // Act - the second request comes long after an entry made for a millisecond would have lapsed
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None);
        await Task.Delay(50);
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None);

        // Assert - the day's memories were held rather than searched for on every request
        _mockImmichApi.Verify(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
