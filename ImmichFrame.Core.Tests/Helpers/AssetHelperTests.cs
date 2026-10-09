using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Helpers;

[TestFixture]
public class AssetHelperTests
{
    /// <summary>
    /// An excluded album is searched a page of a thousand assets at a time, and a full page means
    /// there may be more: every page is read until one comes back short, or every asset past the
    /// first thousand would be shown after all.
    /// </summary>
    [Test]
    public async Task GetExcludedAlbumAssets_AlbumLargerThanAPage_ReadsEveryPage()
    {
        // Arrange - a full first page, and five more on the second
        var albumId = Guid.NewGuid();
        var albumAssets = Enumerable.Range(0, 1005).Select(_ => new AssetResponseDto { Id = Guid.NewGuid() }).ToList();
        var accountSettings = new Mock<IAccountSettings>();
        accountSettings.SetupGet(s => s.ExcludedAlbums).Returns([albumId]);
        var immichApi = new Mock<ImmichApi>(null!, null!);
        immichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, MetadataSearchDto search, CancellationToken _) => PageOf(albumAssets, search));

        // Act
        var excluded = await AssetHelper.GetExcludedAlbumAssets(immichApi.Object, accountSettings.Object);

        // Assert
        Assert.That(excluded.Select(asset => asset.Id), Is.EquivalentTo(albumAssets.Select(asset => asset.Id)));
        immichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.Is<MetadataSearchDto>(search => search.Page == 2 && search.AlbumIds.Contains(albumId)), It.IsAny<CancellationToken>()), Times.Once);
        immichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// Each excluded album is paged from its own first page. Carried on from an album larger than a
    /// page, the count would ask the next album for a page past its end, and every asset in it would
    /// be shown after all.
    /// </summary>
    [Test]
    public async Task GetExcludedAlbumAssets_SeveralAlbums_PagesEachFromItsFirstPage()
    {
        // Arrange - a first album that runs to a second page, and a second that fits in one
        var firstAlbumId = Guid.NewGuid();
        var secondAlbumId = Guid.NewGuid();
        var firstAlbumAssets = Enumerable.Range(0, 1005).Select(_ => new AssetResponseDto { Id = Guid.NewGuid() }).ToList();
        var secondAlbumAssets = Enumerable.Range(0, 3).Select(_ => new AssetResponseDto { Id = Guid.NewGuid() }).ToList();
        var accountSettings = new Mock<IAccountSettings>();
        accountSettings.SetupGet(s => s.ExcludedAlbums).Returns([firstAlbumId, secondAlbumId]);
        var immichApi = new Mock<ImmichApi>(null!, null!);
        immichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.Is<MetadataSearchDto>(search => search.AlbumIds.Contains(firstAlbumId)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, MetadataSearchDto search, CancellationToken _) => PageOf(firstAlbumAssets, search));
        immichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.Is<MetadataSearchDto>(search => search.AlbumIds.Contains(secondAlbumId)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, MetadataSearchDto search, CancellationToken _) => PageOf(secondAlbumAssets, search));

        // Act
        var excluded = await AssetHelper.GetExcludedAlbumAssets(immichApi.Object, accountSettings.Object);

        // Assert
        Assert.That(excluded.Select(asset => asset.Id), Is.EquivalentTo(firstAlbumAssets.Concat(secondAlbumAssets).Select(asset => asset.Id)));
        immichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.Is<MetadataSearchDto>(search => search.Page == 1 && search.AlbumIds.Contains(secondAlbumId)), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The page of the album's assets a search asks for, as Immich answers it.
    private static SearchResponseDto PageOf(List<AssetResponseDto> albumAssets, MetadataSearchDto search)
    {
        var size = search.Size!.Value;
        var items = albumAssets.Skip((int)(search.Page!.Value - 1) * size).Take(size).ToList();

        return new SearchResponseDto
        {
            Assets = new SearchAssetResponseDto { Items = items, Count = items.Count, Total = albumAssets.Count }
        };
    }
}
