using NUnit.Framework;
using Moq;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;

namespace ImmichFrame.Core.Tests.Logic.Pool;

[TestFixture]
public class AlbumAssetsPoolTests
{
    private Mock<IApiCache> _mockApiCache;
    private Mock<ImmichApi> _mockImmichApi;
    private Mock<IAccountSettings> _mockAccountSettings;
    private AlbumAssetsPool _albumAssetsPool;

    [SetUp]
    public void Setup()
    {
        _mockApiCache = new Mock<IApiCache>();

        _mockApiCache
            .Setup(m => m.GetOrAddAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>((_, factory) => factory());

        _mockImmichApi = new Mock<ImmichApi>("", null!);
        _mockAccountSettings = new Mock<IAccountSettings>();
        _albumAssetsPool = new AlbumAssetsPool(_mockApiCache.Object, _mockImmichApi.Object, _mockAccountSettings.Object);

        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid>());
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid>());
    }

    private AssetResponseDto CreateAsset(string id) => new AssetResponseDto { Id = FixtureHelpers.GuidFor(id), Type = AssetTypeEnum.IMAGE };
    // Total is deprecated in the Immich API and deliberately left unset, so paging has to follow each page's own item count
    private SearchResponseDto CreateSearchResult(List<AssetResponseDto> assets) =>
        new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = assets } };

    [Test]
    public async Task LoadAssets_ReturnsAssetsPresentIIncludedNotExcludedAlbums()
    {
        // Arrange
        var album1Id = Guid.NewGuid();
        var excludedAlbumId = Guid.NewGuid();

        var assetA = CreateAsset("A"); // In album1
        var assetB = CreateAsset("B"); // In album1 and excludedAlbum
        var assetC = CreateAsset("C"); // In excludedAlbum only
        var assetD = CreateAsset("D"); // In album1 only

        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { album1Id });
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { excludedAlbumId });

        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(album1Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = new List<AssetResponseDto> { assetA, assetB, assetD }, Total = 3 } });
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(excludedAlbumId)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = new List<AssetResponseDto> { assetB, assetC }, Total = 2 } });

        // Act
        var result = (await _albumAssetsPool.GetAssets(25)).ToList();

        // Assert
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("A")));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("D")));
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(album1Id)), It.IsAny<CancellationToken>()), Times.Once);
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(excludedAlbumId)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_CallsSearchAssetsAsync_WithAlbumId_AndPaginates()
    {
        // Arrange
        var albumId = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { albumId });

        var batchSize = 1000; // From AlbumAssetsPool.cs
        var assetsPage1 = Enumerable.Range(0, batchSize).Select(i => CreateAsset($"album_p1_{i}")).ToList();
        var assetsPage2 = Enumerable.Range(0, 50).Select(i => CreateAsset($"album_p2_{i}")).ToList();

        _mockImmichApi.SetupSequence(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(assetsPage1))  // Page 1, a full page indicates more might be available
            .ReturnsAsync(CreateSearchResult(assetsPage2)); // Page 2, a partial page indicates this is the last page

        // Act
        var count = await _albumAssetsPool.GetAssetCount();

        // Assert - with Exif, as the account's date and rating filters drop every asset without it
        Assert.That(count, Is.EqualTo(batchSize + 50));

        _mockImmichApi.Verify(api => api.SearchAssetsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(dto =>
                dto.AlbumIds.Contains(albumId) &&
                dto.Type == AssetTypeEnum.IMAGE &&
                dto.WithExif == true &&
                dto.WithPeople == true &&
                dto.Page == 1 && dto.Size == batchSize),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockImmichApi.Verify(api => api.SearchAssetsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(dto =>
                dto.AlbumIds.Contains(albumId) &&
                dto.Type == AssetTypeEnum.IMAGE &&
                dto.WithExif == true &&
                dto.WithPeople == true &&
                dto.Page == 2 && dto.Size == batchSize),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_ShowVideos_SearchesEveryType()
    {
        // Arrange
        var albumId = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { albumId });
        _mockAccountSettings.SetupGet(s => s.ShowVideos).Returns(true);

        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(new List<AssetResponseDto> { CreateAsset("A") }));

        // Act
        await _albumAssetsPool.GetAssetCount();

        // Assert
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(dto => dto.AlbumIds.Contains(albumId) && dto.Type == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_AssetInTwoAlbums_IsCountedAndReturnedOnce()
    {
        // Arrange
        var album1Id = Guid.NewGuid();
        var album2Id = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { album1Id, album2Id });

        // Each search answers with a copy of its own, as each response from Immich is read afresh
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(album1Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(new List<AssetResponseDto> { CreateAsset("A"), CreateAsset("B") })); // B is in both albums
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(album2Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(new List<AssetResponseDto> { CreateAsset("B"), CreateAsset("C") }));

        // Act
        var count = await _albumAssetsPool.GetAssetCount();
        var result = (await _albumAssetsPool.GetAssets(25)).ToList();

        // Assert - counted once for each album, B weighted the pool up and could be in one batch twice
        Assert.That(count, Is.EqualTo(3));
        Assert.That(result.Select(a => a.Id), Is.EquivalentTo(new[] { FixtureHelpers.GuidFor("A"), FixtureHelpers.GuidFor("B"), FixtureHelpers.GuidFor("C") }));
    }

    [Test]
    public async Task LoadAssets_NoIncludedAlbums_ReturnsEmpty()
    {
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid>());
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { Guid.NewGuid() });
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = new List<AssetResponseDto> { CreateAsset("excluded_only") }, Total = 1 } });


        var result = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task LoadAssets_NoExcludedAlbums_ReturnsAlbums()
    {
        var album1Id = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { album1Id });
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid>()); // Empty excluded

        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(album1Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = new List<AssetResponseDto> { CreateAsset("A") }, Total = 1 } });

        var result = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("A")));
    }

    [Test]
    public async Task LoadAssets_NullAlbums_ReturnsEmpty()
    {
        _mockAccountSettings.SetupGet(s => s.Albums).Returns((List<Guid>)null!);

        var result = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(result, Is.Empty);

        // the absence of an error, whereas before a null pointer exception would be thrown, indicates success.
    }

    [Test]
    public async Task LoadAssets_NullExcludedAlbums_Succeeds()
    {
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns((List<Guid>)null!);

        var result = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(result, Is.Empty);

        // the absence of an error, whereas before a null pointer exception would be thrown, indicates success.
    }
}
