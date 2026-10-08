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
public class PersonAssetsPoolTests // Renamed from PeopleAssetsPoolTests to match class name
{
    private Mock<IApiCache> _mockApiCache;
    private Mock<ImmichApi> _mockImmichApi;
    private Mock<IAccountSettings> _mockAccountSettings;
    private TestablePersonAssetsPool _personAssetsPool;

    private class TestablePersonAssetsPool : PersonAssetsPool
    {
        public TestablePersonAssetsPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings)
            : base(apiCache, immichApi, accountSettings) { }

        public Task<IEnumerable<AssetResponseDto>> TestLoadAssets(CancellationToken ct = default)
        {
            return base.LoadAssets(ct);
        }
    }

    [SetUp]
    public void Setup()
    {
        _mockApiCache = new Mock<IApiCache>();
        _mockImmichApi = new Mock<ImmichApi>(null!, null!);
        _mockAccountSettings = new Mock<IAccountSettings>();
        _personAssetsPool = new TestablePersonAssetsPool(_mockApiCache.Object, _mockImmichApi.Object, _mockAccountSettings.Object);

        _mockAccountSettings.SetupGet(s => s.People).Returns(new List<Guid>());
    }

    private AssetResponseDto CreateAsset(string id, AssetTypeEnum type = AssetTypeEnum.IMAGE) => new AssetResponseDto { Id = FixtureHelpers.GuidFor(id), Type = type };
    // Total is deprecated in the Immich API and deliberately left unset, so paging has to follow each page's own item count
    private SearchResponseDto CreateSearchResult(List<AssetResponseDto> assets) =>
        new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = assets } };

    [Test]
    public async Task LoadAssets_CallsSearchAssetsForEachPerson_AndPaginates()
    {
        // Arrange
        var person1Id = Guid.NewGuid();
        var person2Id = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.People).Returns(new List<Guid> { person1Id, person2Id });

        var batchSize = 1000; // From PersonAssetsPool.cs
        var p1AssetsPage1 = Enumerable.Range(0, batchSize).Select(i => CreateAsset($"p1_p1_{i}")).ToList();
        var p1AssetsPage2 = Enumerable.Range(0, 30).Select(i => CreateAsset($"p1_p2_{i}")).ToList();
        var p2AssetsPage1 = Enumerable.Range(0, 20).Select(i => CreateAsset($"p2_p1_{i}")).ToList();

        var type = AssetTypeEnum.IMAGE;

        // Person 1 - Page 1
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person1Id) && d.Page == 1 && d.Type == type), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(p1AssetsPage1));
        // Person 1 - Page 2
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person1Id) && d.Page == 2 && d.Type == type), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(p1AssetsPage2));
        // Person 2 - Page 1
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person2Id) && d.Page == 1 && d.Type == type), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(p2AssetsPage1));

        // Act
        var result = (await _personAssetsPool.TestLoadAssets()).ToList();

        // Assert
        Assert.That(result.Count, Is.EqualTo(batchSize + 30 + 20));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("p1_p1_0")));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("p1_p2_29")));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("p2_p1_19")));

        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person1Id) && d.Page == 1 && d.Type == type), It.IsAny<CancellationToken>()), Times.Once);
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person1Id) && d.Page == 2 && d.Type == type), It.IsAny<CancellationToken>()), Times.Once);
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person2Id) && d.Page == 1 && d.Type == type), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_CallsSearchAssetsAsync_WithPersonId_AndPaginates()
    {
        // Arrange
        var personId = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.People).Returns(new List<Guid> { personId });

        var batchSize = 1000; // From PersonAssetsPool.cs
        var assetsPage1 = Enumerable.Range(0, batchSize).Select(i => CreateAsset($"person_p1_{i}")).ToList();
        var assetsPage2 = Enumerable.Range(0, 50).Select(i => CreateAsset($"person_p2_{i}")).ToList();

        _mockImmichApi.SetupSequence(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(assetsPage1))  // Page 1, a full page indicates more might be available
            .ReturnsAsync(CreateSearchResult(assetsPage2)); // Page 2, a partial page indicates this is the last page

        // Act
        var result = (await _personAssetsPool.TestLoadAssets()).ToList();

        // Assert - with Exif, as the account's date and rating filters drop every asset without it
        Assert.That(result.Count, Is.EqualTo(batchSize + 50));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("person_p1_0")));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("person_p2_49")));

        _mockImmichApi.Verify(api => api.SearchAssetsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(dto =>
                dto.PersonIds.Contains(personId) &&
                dto.Type == AssetTypeEnum.IMAGE &&
                dto.WithExif == true &&
                dto.WithPeople == true &&
                dto.Page == 1 && dto.Size == batchSize),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockImmichApi.Verify(api => api.SearchAssetsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(dto =>
                dto.PersonIds.Contains(personId) &&
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
        var personId = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.People).Returns(new List<Guid> { personId });
        _mockAccountSettings.SetupGet(s => s.ShowVideos).Returns(true);

        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(new List<AssetResponseDto> { CreateAsset("video", AssetTypeEnum.VIDEO) }));

        // Act
        await _personAssetsPool.TestLoadAssets();

        // Assert
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(dto => dto.PersonIds.Contains(personId) && dto.Type == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_AssetWithTwoPeople_IsCountedAndReturnedOnce()
    {
        // Arrange - through the pool's cache rather than LoadAssets alone, as GetAssetCount and GetAssets read it
        _mockApiCache.Setup(c => c.GetOrAddAsync(It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>((_, factory) => factory());

        var person1Id = Guid.NewGuid();
        var person2Id = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.People).Returns(new List<Guid> { person1Id, person2Id });

        // Each search answers with a copy of its own, as each response from Immich is read afresh
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person1Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(new List<AssetResponseDto> { CreateAsset("p1_only"), CreateAsset("together") }));
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person2Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(new List<AssetResponseDto> { CreateAsset("together"), CreateAsset("p2_only") }));

        // Act
        var count = await _personAssetsPool.GetAssetCount();
        var result = (await _personAssetsPool.GetAssets(25)).ToList();

        // Assert - a photo of both people is one photo
        Assert.That(count, Is.EqualTo(3));
        Assert.That(result.Select(a => a.Id), Is.EquivalentTo(new[] { FixtureHelpers.GuidFor("p1_only"), FixtureHelpers.GuidFor("together"), FixtureHelpers.GuidFor("p2_only") }));
    }

    [Test]
    public async Task LoadAssets_NoPeopleConfigured_ReturnsEmpty()
    {
        _mockAccountSettings.SetupGet(s => s.People).Returns(new List<Guid>());

        var result = (await _personAssetsPool.TestLoadAssets()).ToList();
        Assert.That(result, Is.Empty);
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task LoadAssets_NullPeople_ReturnsEmpty()
    {
        _mockAccountSettings.SetupGet(s => s.People).Returns((List<Guid>)null!);

        var result = (await _personAssetsPool.TestLoadAssets()).ToList();
        Assert.That(result, Is.Empty);
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task LoadAssets_PersonHasNoAssets_DoesNotAffectOthers()
    {
        var person1Id = Guid.NewGuid(); // Has assets
        var person2Id = Guid.NewGuid(); // No assets
        _mockAccountSettings.SetupGet(s => s.People).Returns(new List<Guid> { person1Id, person2Id });

        var type = AssetTypeEnum.IMAGE;

        var p1Assets = Enumerable.Range(0, 10).Select(i => CreateAsset($"p1_{i}")).ToList();
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person1Id) && d.Type == type), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(p1Assets));
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.PersonIds.Contains(person2Id) && d.Type == type), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSearchResult(new List<AssetResponseDto>()));

        var result = (await _personAssetsPool.TestLoadAssets()).ToList();
        Assert.That(result.Count, Is.EqualTo(10));
        var expectedIds = p1Assets.Select(a => a.Id);
        Assert.That(result.All(a => expectedIds.Contains(a.Id)));
    }
}
