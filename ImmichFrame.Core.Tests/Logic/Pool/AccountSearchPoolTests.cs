using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Logic.Pool;

[TestFixture]
public class AccountSearchPoolTests
{
    // Turns a hang into a failure rather than a stalled run.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private Mock<IApiCache> _cache;
    private Mock<ImmichApi> _api;
    private Mock<IAccountSettings> _settings;
    private AccountSearchPool _pool;

    [SetUp]
    public void Setup()
    {
        _cache = new Mock<IApiCache>();
        _cache.Setup(c => c.GetOrAddAsync(It.IsAny<string>(), It.IsAny<Func<Task<ICollection<TagResponseDto>>>>()))
            .Returns<string, Func<Task<ICollection<TagResponseDto>>>>((_, factory) => factory());
        _cache.Setup(c => c.GetOrAddAsync(It.IsAny<string>(), It.IsAny<Func<Task<long>>>()))
            .Returns<string, Func<Task<long>>>((_, factory) => factory());
        _api = new Mock<ImmichApi>(null!, null!);
        _api.Setup(a => a.SearchRandomAsync(It.IsAny<RandomSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AssetResponseDto>());
        _api.Setup(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchStatisticsResponseDto { Total = 0 });
        _settings = new Mock<IAccountSettings>();
        _settings.SetupGet(s => s.ImmichServerUrl).Returns("https://photos.example");
        _settings.SetupGet(s => s.Albums).Returns(new List<Guid>());
        _settings.SetupGet(s => s.People).Returns(new List<Guid>());
        _settings.SetupGet(s => s.Tags).Returns(new List<string>());
        _settings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid>());
        _pool = new AccountSearchPool(_cache.Object, _api.Object, _settings.Object);
    }

    [Test]
    public async Task GetAssets_WithoutSources_SearchesTheLibrary()
    {
        // Act
        await _pool.GetAssets(5);

        // Assert - trashed photos are left out by a trashedAt that is null, sent as such
        VerifyRandom(dto =>
            dto.Size == 5 &&
            dto.WithExif == true &&
            dto.WithPeople == true &&
            dto.Filter!.Or == null &&
            dto.Filter.TrashedAt is NullableDateFilter { Eq: { IsSpecified: true, Value: null } } &&
            dto.Filter.AlbumIds == null &&
            dto.Filter.TakenAt == null &&
            dto.Filter.Rating == null &&
            dto.Filter.Visibility!.In.Contains(AssetVisibility.Timeline) &&
            !dto.Filter.Visibility.In.Contains(AssetVisibility.Archive) &&
            dto.Filter.Type!.In.Contains(AssetTypeEnum.IMAGE) &&
            !dto.Filter.Type.In.Contains(AssetTypeEnum.VIDEO));
        _api.Verify(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Settings bound from a file can leave any of the lists null rather than empty. None of them is
    /// then a source, and none of them sends an empty list, which Immich refuses.
    /// </summary>
    [Test]
    public async Task GetAssets_NullLists_SearchTheLibrary()
    {
        // Arrange
        _settings.SetupGet(s => s.Albums).Returns((List<Guid>)null!);
        _settings.SetupGet(s => s.People).Returns((List<Guid>)null!);
        _settings.SetupGet(s => s.Tags).Returns((List<string>)null!);
        _settings.SetupGet(s => s.ExcludedAlbums).Returns((List<Guid>)null!);

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto => dto.Filter!.Or == null && dto.Filter.AlbumIds == null);
        _api.Verify(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAssets_ShowVideos_AddsVideoType()
    {
        // Arrange
        _settings.SetupGet(s => s.ShowVideos).Returns(true);

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto =>
            dto.Filter!.Type!.In.Contains(AssetTypeEnum.IMAGE) &&
            dto.Filter.Type.In.Contains(AssetTypeEnum.VIDEO));
    }

    [Test]
    public async Task GetAssets_ShowArchived_AddsArchiveVisibility()
    {
        // Arrange
        _settings.SetupGet(s => s.ShowArchived).Returns(true);

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto =>
            dto.Filter!.Visibility!.In.Contains(AssetVisibility.Timeline) &&
            dto.Filter.Visibility.In.Contains(AssetVisibility.Archive));
    }

    [Test]
    public async Task GetAssets_Rating_SetsGte()
    {
        // Arrange
        _settings.SetupGet(s => s.Rating).Returns(3);

        // Act
        await _pool.GetAssets(1);

        // Assert - at least the rating, not exactly it
        VerifyRandom(dto => dto.Filter!.Rating!.Gte == 3 && dto.Filter.Rating.Eq == null);
    }

    // The date tests below expect inclusive ends - gte and lte - where upstream sends gt and lt: the
    // account's dates have always included the moment they name, and the memories still do.

    [Test]
    public async Task GetAssets_ImagesFromDays_SetsTakenAfter()
    {
        // Arrange
        _settings.SetupGet(s => s.ImagesFromDays).Returns(10);
        var expected = DateTime.Today.AddDays(-10);

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto =>
            dto.Filter!.TakenAt!.Gte == new DateTimeOffset(expected) &&
            dto.Filter.TakenAt.Gt == null &&
            dto.Filter.TakenAt.Lte == null);
    }

    [Test]
    public async Task GetAssets_ImagesFromDate_WinsOverFromDays()
    {
        // Arrange
        var fromDate = new DateTime(2020, 6, 1);
        _settings.SetupGet(s => s.ImagesFromDate).Returns(fromDate);
        _settings.SetupGet(s => s.ImagesFromDays).Returns(10);

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto => dto.Filter!.TakenAt!.Gte == new DateTimeOffset(fromDate));
    }

    [Test]
    public async Task GetAssets_ImagesUntilDate_SetsTakenBefore()
    {
        // Arrange
        var until = new DateTime(2024, 1, 1);
        _settings.SetupGet(s => s.ImagesUntilDate).Returns(until);

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto =>
            dto.Filter!.TakenAt!.Lte == new DateTimeOffset(until) &&
            dto.Filter.TakenAt.Lt == null &&
            dto.Filter.TakenAt.Gte == null);
    }

    [Test]
    public async Task GetAssets_ExcludedAlbums_SentAsNone()
    {
        // Arrange
        var excludedAlbumId = Guid.NewGuid();
        _settings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { excludedAlbumId });

        // Act
        await _pool.GetAssets(1);

        // Assert - left to Immich, rather than every excluded asset paged out of it to be dropped here
        VerifyRandom(dto => dto.Filter!.AlbumIds!.None.SequenceEqual(new[] { excludedAlbumId }) && dto.Filter.AlbumIds.Any == null);
        _api.Verify(a => a.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAssets_EmptyExcludedAlbums_OmitsAlbumFilter()
    {
        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto => dto.Filter!.AlbumIds == null);
    }

    [Test]
    public async Task GetAssets_NullExcludedAlbums_OmitsAlbumFilter()
    {
        // Arrange
        _settings.SetupGet(s => s.ExcludedAlbums).Returns((List<Guid>)null!);

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto => dto.Filter!.AlbumIds == null);
    }

    [Test]
    public async Task GetAssets_WithSources_OrsOnlyConfiguredBranches()
    {
        // Arrange
        var albumId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var excludedAlbumId = Guid.NewGuid();
        _settings.SetupGet(s => s.ShowFavorites).Returns(true);
        _settings.SetupGet(s => s.Albums).Returns(new List<Guid> { albumId });
        _settings.SetupGet(s => s.People).Returns(new List<Guid> { personId });
        _settings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { excludedAlbumId });

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto =>
            dto.Filter!.AlbumIds!.None.Contains(excludedAlbumId) &&
            dto.Filter.Or.Count == 3 &&
            dto.Filter.Or.Any(branch => branch.IsFavorite != null && branch.IsFavorite.Eq) &&
            dto.Filter.Or.Any(branch => branch.AlbumIds != null && branch.AlbumIds.Any.Contains(albumId) && branch.AlbumIds.None == null) &&
            dto.Filter.Or.Any(branch => branch.PersonIds != null && branch.PersonIds.Any.Contains(personId)));
    }

    /// <summary>
    /// Each source is a branch of its own, holding that source and nothing else. Two sources in one
    /// branch would show only what is in both - a favourite of the album rather than the favourites
    /// and the album - and an account's own settings in a branch would leave them off the others.
    /// </summary>
    [Test]
    public async Task GetAssets_EverySource_IsABranchOfItsOwn()
    {
        // Arrange
        var albums = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var people = new List<Guid> { Guid.NewGuid() };
        var tagId = Guid.NewGuid();
        _settings.SetupGet(s => s.ShowFavorites).Returns(true);
        _settings.SetupGet(s => s.Albums).Returns(albums);
        _settings.SetupGet(s => s.People).Returns(people);
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "Holiday" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto> { new() { Id = tagId, Value = "Holiday" } });
        var drawn = CaptureRandom();

        // Act
        await _pool.GetAssets(1);

        // Assert - each branch as it is sent, so a criterion added to one shows up here
        var branches = drawn().Filter.Or.Select(branch => JsonConvert.SerializeObject(branch));
        Assert.That(branches, Is.EquivalentTo(new[]
        {
            """{"isFavorite":{"eq":true}}""",
            $$$"""{"albumIds":{"any":["{{{albums[0]}}}","{{{albums[1]}}}"]}}""",
            $$$"""{"personIds":{"any":["{{{people[0]}}}"]}}""",
            $$$"""{"tagIds":{"any":["{{{tagId}}}"]}}"""
        }));
    }

    [Test]
    public async Task GetAssets_Tags_MatchFullPathCaseSensitively()
    {
        // Arrange
        var match = Guid.NewGuid();
        var wrongCase = Guid.NewGuid();
        var wrongParent = Guid.NewGuid();
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "Parent/Child" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>
            {
                new() { Id = match, Value = "Parent/Child" },
                new() { Id = wrongCase, Value = "parent/child" },
                new() { Id = wrongParent, Value = "Other/Child" }
            });

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto =>
            dto.Filter!.Or.Count == 1 &&
            dto.Filter.Or.Single().TagIds!.Any.Contains(match) &&
            !dto.Filter.Or.Single().TagIds.Any.Contains(wrongCase) &&
            !dto.Filter.Or.Single().TagIds.Any.Contains(wrongParent));
    }

    [Test]
    public async Task GetAssets_TagListedTwice_IsSearchedForOnce()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "Tag", "Tag" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto> { new() { Id = tagId, Value = "Tag" } });

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto => dto.Filter!.Or.Single().TagIds!.Any.SequenceEqual(new[] { tagId }));
    }

    [Test]
    public async Task GetAssets_TagsDifferingOnlyInCase_AreBothSearchedFor()
    {
        // Arrange
        var lower = Guid.NewGuid();
        var upper = Guid.NewGuid();
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "people", "People" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>
            {
                new() { Id = lower, Value = "people" },
                new() { Id = upper, Value = "People" }
            });

        // Act
        await _pool.GetAssets(1);

        // Assert - matched case-sensitively, these are two different tags rather than one listed twice
        VerifyRandom(dto => dto.Filter!.Or.Single().TagIds!.Any.SequenceEqual(new[] { lower, upper }));
    }

    [Test]
    public async Task GetAssets_UnmatchedTagsOnly_DoesNotSearch()
    {
        // Arrange
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "missing" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>());

        // Act
        var result = await _pool.GetAssets(5);

        // Assert - an account narrowed to nothing that exists shows nothing, not its whole library
        Assert.That(result, Is.Empty);
        _api.Verify(a => a.SearchRandomAsync(It.IsAny<RandomSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A tag that names nothing is no reason to show nothing when another source is configured - and
    /// it is no branch either: one with no tags in it is refused by Immich.
    /// </summary>
    [Test]
    public async Task GetAssets_UnmatchedTagBesideFavorites_SearchesTheFavoritesAlone()
    {
        // Arrange
        _settings.SetupGet(s => s.ShowFavorites).Returns(true);
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "missing" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>());

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto =>
            dto.Filter!.Or.Count == 1 &&
            dto.Filter.Or.Single().IsFavorite!.Eq &&
            dto.Filter.Or.Single().TagIds == null);
    }

    [Test]
    public async Task GetAssets_TagList_IsCachedUnderTheServersKey()
    {
        // Arrange
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "Tag" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto> { new() { Id = Guid.NewGuid(), Value = "Tag" } });

        // Act
        await _pool.GetAssets(1);

        // Assert
        _cache.Verify(c => c.GetOrAddAsync("allTags_https://photos.example", It.IsAny<Func<Task<ICollection<TagResponseDto>>>>()), Times.Once);
    }

    [Test]
    public async Task GetAssets_TagListAlreadyCached_IsNotFetchedAgain()
    {
        // Arrange - the cache already holds the server's tags, so it answers without running the load
        // it is handed
        var tagId = Guid.NewGuid();
        ICollection<TagResponseDto> allTags = [new TagResponseDto { Id = tagId, Value = "Tag" }];
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "Tag" });
        _cache.Setup(c => c.GetOrAddAsync(It.IsAny<string>(), It.IsAny<Func<Task<ICollection<TagResponseDto>>>>()))
            .ReturnsAsync(allTags);

        // Act
        await _pool.GetAssets(1);

        // Assert
        VerifyRandom(dto => dto.Filter!.Or.Single().TagIds!.Any.Contains(tagId));
        _api.Verify(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The tag list is cached and shared with every request, so it is loaded without the token of the
    /// request that happened to start it. The random draw is that request's own, and goes with it.
    /// </summary>
    [Test]
    public async Task GetAssets_RequestWithAToken_LoadsTheTagsWithoutItAndDrawsWithIt()
    {
        // Arrange
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "Tag" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto> { new() { Id = Guid.NewGuid(), Value = "Tag" } });
        using var request = new CancellationTokenSource();

        // Act
        await _pool.GetAssets(1, request.Token);

        // Assert
        _api.Verify(a => a.GetAllTagsAsync(CancellationToken.None), Times.Once);
        _api.Verify(a => a.SearchRandomAsync(It.IsAny<RandomSearchDto>(), request.Token), Times.Once);
    }

    [Test]
    public async Task GetAssetCount_UsesTheSameFilter()
    {
        // Arrange
        _settings.SetupGet(s => s.ShowFavorites).Returns(true);
        _api.Setup(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchStatisticsResponseDto { Total = 12 });

        // Act
        var count = await _pool.GetAssetCount();

        // Assert
        Assert.That(count, Is.EqualTo(12));
        _api.Verify(a => a.SearchAssetStatisticsAsync(It.Is<StatisticsSearchDto>(dto =>
            dto.Filter!.Or.Any(branch => branch.IsFavorite != null && branch.IsFavorite.Eq) &&
            dto.Filter.Visibility!.In.Contains(AssetVisibility.Timeline)
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// What an account counts weighs its share of every batch against the other accounts, so it has
    /// to be what it draws from: counted with a looser filter, an account narrowed to a few photos
    /// was asked for many more than it had.
    /// </summary>
    [Test]
    public async Task GetAssetCount_EverySettingInUse_CountsWithTheFilterItDrawsWith()
    {
        // Arrange
        _settings.SetupGet(s => s.ShowFavorites).Returns(true);
        _settings.SetupGet(s => s.ShowArchived).Returns(true);
        _settings.SetupGet(s => s.ShowVideos).Returns(true);
        _settings.SetupGet(s => s.Albums).Returns(new List<Guid> { Guid.NewGuid() });
        _settings.SetupGet(s => s.People).Returns(new List<Guid> { Guid.NewGuid() });
        _settings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { Guid.NewGuid() });
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "Tag" });
        _settings.SetupGet(s => s.ImagesFromDate).Returns(new DateTime(2020, 1, 1));
        _settings.SetupGet(s => s.ImagesUntilDate).Returns(new DateTime(2021, 1, 1));
        _settings.SetupGet(s => s.Rating).Returns(4);
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto> { new() { Id = Guid.NewGuid(), Value = "Tag" } });
        SearchFilter? counted = null;
        _api.Setup(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()))
            .Callback<StatisticsSearchDto, CancellationToken>((dto, _) => counted = dto.Filter)
            .ReturnsAsync(new SearchStatisticsResponseDto { Total = 1 });
        var drawn = CaptureRandom();

        // Act
        await _pool.GetAssetCount();
        await _pool.GetAssets(1);

        // Assert - compared as they are sent
        Assert.That(JsonConvert.SerializeObject(counted), Is.EqualTo(JsonConvert.SerializeObject(drawn().Filter)));
    }

    [Test]
    public async Task GetAssetCount_UnmatchedTagsOnly_IsZeroWithoutStatisticsCall()
    {
        // Arrange
        _settings.SetupGet(s => s.Tags).Returns(new List<string> { "missing" });
        _api.Setup(a => a.GetAllTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagResponseDto>());

        // Act
        var count = await _pool.GetAssetCount();

        // Assert
        Assert.That(count, Is.EqualTo(0));
        _api.Verify(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAssetCount_SameFilter_IsServedFromCache()
    {
        // Arrange
        using var cache = new ApiCache(TimeSpan.FromHours(1));
        var pool = new AccountSearchPool(cache, _api.Object, _settings.Object);
        _api.Setup(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchStatisticsResponseDto { Total = 7 });

        // Act & Assert
        Assert.That(await pool.GetAssetCount(), Is.EqualTo(7));
        Assert.That(await pool.GetAssetCount(), Is.EqualTo(7));

        _api.Verify(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The account's cache is shared by everything the account caches, so the count is kept under a
    /// name of its own rather than one as plain as "stats".
    /// </summary>
    [Test]
    public async Task GetAssetCount_IsCachedUnderItsOwnKey()
    {
        // Act
        await _pool.GetAssetCount();

        // Assert
        _cache.Verify(c => c.GetOrAddAsync("AccountSearchPool_AssetCount", It.IsAny<Func<Task<long>>>()), Times.Once);
    }

    /// <summary>
    /// The count is loaded once and shared with every request that asks while the load is running,
    /// so the request that happened to start it cannot take it down for the rest by giving up.
    /// </summary>
    [Test]
    public async Task GetAssetCount_RequestThatStartedTheLoadGivesUp_RequestSharingItStillGetsTheCount()
    {
        // Arrange - the search answers when the test says so, unless its token gives up first
        var answer = new TaskCompletionSource<SearchStatisticsResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.Setup(a => a.SearchAssetStatisticsAsync(It.IsAny<StatisticsSearchDto>(), It.IsAny<CancellationToken>()))
            .Returns((StatisticsSearchDto _, CancellationToken token) => answer.Task.WaitAsync(token));
        using var cache = new ApiCache(TimeSpan.FromHours(1));
        var pool = new AccountSearchPool(cache, _api.Object, _settings.Object);
        using var starter = new CancellationTokenSource();

        // Act - one request starts the load, another joins it, and the first gives up before it is done
        _ = pool.GetAssetCount(starter.Token);
        var sharing = pool.GetAssetCount();
        await starter.CancelAsync();
        answer.SetResult(new SearchStatisticsResponseDto { Total = 7 });

        // Assert
        Assert.That(await sharing.WaitAsync(WaitLimit), Is.EqualTo(7));
    }

    // The random search last sent, for a test to read once it has drawn.
    private Func<RandomSearchDto> CaptureRandom()
    {
        RandomSearchDto? drawn = null;
        _api.Setup(a => a.SearchRandomAsync(It.IsAny<RandomSearchDto>(), It.IsAny<CancellationToken>()))
            .Callback<RandomSearchDto, CancellationToken>((dto, _) => drawn = dto)
            .ReturnsAsync(new List<AssetResponseDto>());
        return () => drawn ?? throw new AssertionException("nothing was drawn");
    }

    private void VerifyRandom(Func<RandomSearchDto, bool> match) =>
        _api.Verify(a => a.SearchRandomAsync(It.Is<RandomSearchDto>(dto => match(dto)), It.IsAny<CancellationToken>()), Times.Once);
}
