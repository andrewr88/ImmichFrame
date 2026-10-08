using System.Collections;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Helpers;

[TestFixture]
public class AssetExtensionMethodsTests
{
    private Mock<IAccountSettings> _accountSettings = null!;

    [SetUp]
    public void Setup()
    {
        // Every filter but the excluded albums is off, or passes everything below.
        _accountSettings = new Mock<IAccountSettings>();
        _accountSettings.SetupGet(s => s.ShowVideos).Returns(true);
        _accountSettings.SetupGet(s => s.ShowArchived).Returns(true);
    }

    /// <summary>
    /// <c>CachingApiAssetsPool</c> caches what this returns and enumerates it on every request for a
    /// count or an asset. A lazy query ran every filter again each time, the excluded-album one
    /// scanning every excluded asset for every asset; counting the reads of each input shows both
    /// without timing anything.
    /// </summary>
    [Test]
    public void ApplyAccountFilters_ExcludedAlbumAssets_AreLeftOutOfAResultThatReadsEachInputOnce()
    {
        // Arrange - the excluded album's assets are separate objects, as a search for them returns
        var assets = Enumerable.Range(0, 100)
            .Select(i => new AssetResponseDto { Id = Guid.NewGuid(), Type = AssetTypeEnum.IMAGE })
            .ToList();
        var excludedIds = assets.Where((_, i) => i % 4 == 0).Select(asset => asset.Id).ToList();
        var loaded = new CountingEnumerable<AssetResponseDto>(assets);
        var excluded = new CountingEnumerable<AssetResponseDto>(
            excludedIds.Select(id => new AssetResponseDto { Id = id, Type = AssetTypeEnum.IMAGE }).ToList());

        // Act
        var filtered = loaded.ApplyAccountFilters(_accountSettings.Object, excluded);
        var firstRead = filtered.ToList();
        var secondRead = filtered.ToList();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(firstRead, Is.EqualTo(assets.Where(asset => !excludedIds.Contains(asset.Id))));
            Assert.That(secondRead, Is.EqualTo(firstRead));
            Assert.That(loaded.Enumerations, Is.EqualTo(1), "the loaded assets were filtered again");
            Assert.That(excluded.Enumerations, Is.EqualTo(1), "the excluded assets were scanned per asset");
        });
    }

    private sealed class CountingEnumerable<T>(IEnumerable<T> items) : IEnumerable<T>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
