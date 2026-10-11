using NUnit.Framework;
using Moq;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using Newtonsoft.Json.Linq;
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
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryResponseDto>());

        // Act
        // Access protected method via reflection for testing, or make it internal/public if design allows
        // For now, we assume LoadAssets is implicitly called by a public method of CachingApiAssetsPool (e.g. GetAsset)
        // Let's simulate this by calling a method that would trigger LoadAssets if cache is empty.
        // Since LoadAssets is protected, we'll test its effects via GetAsset.
        // We need to ensure the cache is empty or expired for LoadAssets to be called.
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None); // This should trigger LoadAssets

        // Assert
        _mockImmichApi.Verify(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_SearchesTheMemoriesOfToday()
    {
        // Arrange
        DateTimeOffset? searchDate = null;
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset?, Guid?, bool?, bool?, bool?, MemorySearchOrder?, long?, long?, MemoryType?, CancellationToken>((date, _, _, _, _, _, _, _, _, _) => searchDate = date)
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
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
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

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
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

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
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

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
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

            _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
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

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
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
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
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
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleImageMemories(1, 1, true, DateTime.Now.Year - 1));

        // Act - the second request comes long after an entry made for a millisecond would have lapsed
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None);
        await Task.Delay(50);
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None);

        // Assert - the day's memories were held rather than searched for on every request
        _mockImmichApi.Verify(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The rest of an account's photos are filtered by Immich's search, which its memories do not come
    // from, so the pool holds them to the account's settings itself. Each test below turns one setting
    // on, and checks that it leaves out what it should and keeps the rest.

    [Test]
    public async Task GetAssets_MemoryNeitherImageNorVideo_IsLeftOut()
    {
        // Arrange - videos are shown, so it is the kind of asset that is refused, not that it is a video
        _mockAccountSettings.SetupGet(s => s.ShowVideos).Returns(true);
        var image = Remembered();
        var video = Remembered(AssetTypeEnum.VIDEO);
        ImmichRemembers(image, video, Remembered(AssetTypeEnum.AUDIO), Remembered(AssetTypeEnum.OTHER));

        // Act
        var shown = await AllShown();

        // Assert
        Assert.That(shown, Is.EquivalentTo(new[] { image, video }));
    }

    [TestCase(false, TestName = "GetAssets_ArchivedMemoryWithShowArchivedOff_IsLeftOut")]
    [TestCase(true, TestName = "GetAssets_ArchivedMemoryWithShowArchivedOn_IsShown")]
    public async Task GetAssets_ArchivedMemory_IsShownOnlyWithShowArchived(bool showArchived)
    {
        // Arrange
        _mockAccountSettings.SetupGet(s => s.ShowArchived).Returns(showArchived);
        var inTimeline = Remembered();
        var archived = Remembered(archived: true);
        ImmichRemembers(inTimeline, archived);

        // Act
        var shown = await AllShown();

        // Assert
        Assert.That(shown, Is.EquivalentTo(showArchived ? new[] { inTimeline, archived } : new[] { inTimeline }));
    }

    /// <summary>
    /// Both ends of the date range are inclusive, as they are for the search: a memory taken at the very
    /// moment ImagesFromDate or ImagesUntilDate names is shown. One taken a tick outside it is not,
    /// which shows the filter ran.
    /// </summary>
    [TestCase(nameof(IAccountSettings.ImagesFromDate), -1, TestName = "GetAssets_MemoryTakenAtImagesFromDate_IsShown")]
    [TestCase(nameof(IAccountSettings.ImagesUntilDate), 1, TestName = "GetAssets_MemoryTakenAtImagesUntilDate_IsShown")]
    public async Task GetAssets_MemoryTakenAtEitherEndOfTheRange_IsShown(string end, int ticksOutside)
    {
        // Arrange
        var boundary = new DateTime(2024, 6, 1, 12, 0, 0);
        if (end == nameof(IAccountSettings.ImagesFromDate))
            _mockAccountSettings.SetupGet(s => s.ImagesFromDate).Returns(boundary);
        else
            _mockAccountSettings.SetupGet(s => s.ImagesUntilDate).Returns(boundary);

        var atTheEnd = Remembered(taken: boundary);
        ImmichRemembers(atTheEnd, Remembered(taken: boundary.AddTicks(ticksOutside)));

        // Act
        var shown = await AllShown();

        // Assert
        Assert.That(shown, Is.EqualTo(new[] { atTheEnd }));
    }

    [Test]
    public async Task GetAssets_MemoryTakenBeforeImagesFromDays_IsLeftOut()
    {
        // Arrange
        _mockAccountSettings.SetupGet(s => s.ImagesFromDays).Returns(7);
        var recent = Remembered(taken: DateTime.Today.AddDays(-6));
        ImmichRemembers(recent, Remembered(taken: DateTime.Today.AddDays(-8)));

        // Act
        var shown = await AllShown();

        // Assert
        Assert.That(shown, Is.EqualTo(new[] { recent }));
    }

    [Test]
    public async Task GetAssets_Rating_ShowsMemoriesRatedAtLeastThat()
    {
        // Arrange
        _mockAccountSettings.SetupGet(s => s.Rating).Returns(3);
        var ratedThree = Remembered(rating: 3);
        var ratedFive = Remembered(rating: 5);
        ImmichRemembers(Remembered(rating: null), Remembered(rating: 2), ratedThree, ratedFive);

        // Act
        var shown = await AllShown();

        // Assert - read as the search reads it: at least the rating, which no unrated memory meets
        Assert.That(shown, Is.EquivalentTo(new[] { ratedThree, ratedFive }));
    }

    [Test]
    public async Task GetAssets_MemoryInAnExcludedAlbum_IsLeftOut()
    {
        // Arrange
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns([Guid.NewGuid()]);
        var kept = Remembered();
        var excluded = Remembered();
        ImmichRemembers(kept, excluded);
        ExcludedAlbumsHold(excluded.Id);

        // Act
        var shown = await AllShown();

        // Assert
        Assert.That(shown, Is.EqualTo(new[] { kept }));
    }

    /// <summary>
    /// The excluded albums are searched for through the search's filter and paged through by its
    /// cursor - not by the page number and album ids Immich deprecated in 3.2.0 - in each visibility
    /// the account shows a memory in, so an archived memory in an excluded album is found as well.
    /// </summary>
    [Test]
    public async Task GetAssets_ExcludedAlbumsLargerThanAPage_AreSearchedPageByPageByCursor()
    {
        // Arrange
        var excludedAlbums = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(excludedAlbums);
        _mockAccountSettings.SetupGet(s => s.ShowArchived).Returns(true);
        var onFirstPage = Remembered(archived: true);
        var onSecondPage = Remembered();
        var kept = Remembered();
        ImmichRemembers(onFirstPage, onSecondPage, kept);

        var searches = new List<MetadataSearchDto>();
        _mockImmichApi.Setup(x => x.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, MetadataSearchDto, CancellationToken>((_, _, search, _) => searches.Add(search))
            .ReturnsAsync((string _, string _, MetadataSearchDto search, CancellationToken _) =>
                search.Cursor == null ? SearchPage("page-2", onFirstPage.Id) : SearchPage(null, onSecondPage.Id));

        // Act
        var shown = await AllShown();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(shown, Is.EqualTo(new[] { kept }));
            Assert.That(searches.Select(search => search.Cursor), Is.EqualTo(new[] { null, "page-2" }));
            Assert.That(searches.Select(search => search.Size), Has.All.EqualTo(1000));
            Assert.That(searches.Select(search => search.Filter.AlbumIds.Any), Has.All.EquivalentTo(excludedAlbums));
            Assert.That(searches.Select(search => search.Filter.Visibility.In),
                Has.All.EquivalentTo(new[] { AssetVisibility.Timeline, AssetVisibility.Archive }));
            Assert.That(searches.Select(SentFields), Has.All.SubsetOf(new[] { "filter", "cursor", "size" }),
                "sent with a field other than the filter, the cursor and the page size");
        });
    }

    /// <summary>
    /// A server that hands back the very cursor it was sent would have the excluded albums paged
    /// through forever, and every frame request waits on that load. It ends at the repeat instead,
    /// with the ids both pages held.
    /// </summary>
    [Test]
    public async Task GetAssets_ExcludedAlbumsSearchRepeatsACursor_EndsWithTheIdsFound()
    {
        // Arrange
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns([Guid.NewGuid()]);
        var onFirstPage = Remembered();
        var onRepeatingPage = Remembered();
        var kept = Remembered();
        ImmichRemembers(onFirstPage, onRepeatingPage, kept);

        // Repeats the cursor only so often, so that a load that pages on past the repeat fails this
        // test rather than hanging it
        var searches = new List<MetadataSearchDto>();
        _mockImmichApi.Setup(x => x.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, MetadataSearchDto, CancellationToken>((_, _, search, _) => searches.Add(search))
            .ReturnsAsync((string _, string _, MetadataSearchDto search, CancellationToken _) =>
                search.Cursor == null ? SearchPage("page-2", onFirstPage.Id)
                : searches.Count < 10 ? SearchPage("page-2", onRepeatingPage.Id)
                : SearchPage(null));

        // Act
        var shown = await AllShown();

        // Assert - what both pages held is excluded, and nothing was searched for past the repeat
        Assert.Multiple(() =>
        {
            Assert.That(shown, Is.EqualTo(new[] { kept }));
            Assert.That(searches.Select(search => search.Cursor), Is.EqualTo(new[] { null, "page-2" }));
        });
    }

    [TestCase(false, TestName = "GetAssets_NullExcludedAlbums_DoesNotSearchForThem")]
    [TestCase(true, TestName = "GetAssets_EmptyExcludedAlbums_DoesNotSearchForThem")]
    public async Task GetAssets_NoExcludedAlbums_DoesNotSearchForThem(bool emptyList)
    {
        // Arrange
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(emptyList ? new List<Guid>() : null!);
        var memory = Remembered();
        ImmichRemembers(memory);

        // Act
        var shown = await AllShown();

        // Assert
        Assert.That(shown, Is.EqualTo(new[] { memory }));
        _mockImmichApi.Verify(x => x.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAssets_AgainTheSameDay_DoesNotSearchTheExcludedAlbumsAgain()
    {
        // Arrange - as for the memories themselves, a run that straddles midnight would rightly search twice
        Assume.That(DateTime.Now.TimeOfDay, Is.LessThan(new TimeSpan(23, 59, 50)),
            "too close to midnight, when the day's memories rightly expire");
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns([Guid.NewGuid()]);
        ImmichRemembers(Remembered());
        ExcludedAlbumsHold();

        // Act
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None);
        await Task.Delay(50);
        await _memoryAssetsPool.GetAssets(1, CancellationToken.None);

        // Assert - searched once for the day, and without a request's token, being shared by every request
        _mockImmichApi.Verify(x => x.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), CancellationToken.None), Times.Once);
    }

    // A memory asset with its info already at hand, taken on the given date or else on a fixed one.
    private static AssetResponseDto Remembered(AssetTypeEnum type = AssetTypeEnum.IMAGE, DateTime? taken = null, int? rating = null, bool archived = false) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        IsArchived = archived,
        ExifInfo = new ExifResponseDto { DateTimeOriginal = taken ?? new DateTime(2020, 1, 1), Rating = rating },
        People = new List<PersonResponseDto>()
    };

    // Has Immich find one memory today, holding exactly these assets.
    private void ImmichRemembers(params AssetResponseDto[] assets) =>
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTimeOffset>(), null, null, null, null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryResponseDto>
            {
                new() { Id = Guid.NewGuid(), Assets = assets.ToList(), Data = new OnThisDayDto { Year = DateTime.Now.Year - 2 } }
            });

    // Has Immich find exactly these assets in the excluded albums, on a single page.
    private void ExcludedAlbumsHold(params Guid[] assetIds) =>
        _mockImmichApi.Setup(x => x.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchPage(null, assetIds));

    private static SearchResponseDto SearchPage(string? nextCursor, params Guid[] assetIds) => new()
    {
        Assets = new SearchAssetResponseDto
        {
            Items = assetIds.Select(id => new AssetResponseDto { Id = id }).ToList(),
            NextCursor = nextCursor
        }
    };

    // Every memory the pool shows, asking for more than any test holds.
    private async Task<List<AssetResponseDto>> AllShown() =>
        (await _memoryAssetsPool.GetAssets(100, CancellationToken.None)).ToList();

    // The fields a search is sent with: a field left null is left out of the request.
    private static IEnumerable<string> SentFields(MetadataSearchDto search) =>
        JObject.FromObject(search).Properties().Select(property => property.Name);
}
