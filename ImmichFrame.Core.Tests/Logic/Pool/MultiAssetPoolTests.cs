using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using NUnit.Framework.Constraints;

namespace ImmichFrame.Core.Tests.Logic.Pool
{
    [TestFixture]
    public class MultiAssetPoolTests
    {
        private Mock<IAssetPool> _mockPool1;
        private Mock<IAssetPool> _mockPool2;
        private Mock<IAssetPool> _mockPool3;
        private MultiAssetPool _multiPool = null!;

        [SetUp]
        public void Setup()
        {
            _mockPool1 = new Mock<IAssetPool>();
            _mockPool2 = new Mock<IAssetPool>();
            _mockPool3 = new Mock<IAssetPool>();
        }

        private AssetResponseDto CreateAsset(string id) => new AssetResponseDto { Id = FixtureHelpers.GuidFor(id), OriginalPath = $"/path/{id}.jpg", Type = AssetTypeEnum.IMAGE, ExifInfo = new ExifResponseDto() };

        // Sets a pool up the way CachingApiAssetsPool behaves: its count is what it holds rather than
        // what it has left, so it does not drop as assets are handed out, and every call hands out
        // up to the number asked for from all it holds, never one asset twice.
        private static void SetUpPool(Mock<IAssetPool> pool, long count, params AssetResponseDto[] assets)
        {
            pool.Setup(p => p.GetAssetCount(It.IsAny<CancellationToken>())).ReturnsAsync(count);
            pool.Setup(p => p.GetAssets(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int requested, CancellationToken _) => assets.Take(requested).ToList());
        }

        [Test]
        public async Task GetAssetCount_NoPools_ReturnsZero()
        {
            _multiPool = new MultiAssetPool(new List<IAssetPool>());
            Assert.That(await _multiPool.GetAssetCount(CancellationToken.None), Is.EqualTo(0));
        }

        [Test]
        public async Task GetAssetCount_OnePool_ReturnsCorrectCount()
        {
            _mockPool1.Setup(p => p.GetAssetCount(It.IsAny<CancellationToken>())).ReturnsAsync(5L);
            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object });
            Assert.That(await _multiPool.GetAssetCount(CancellationToken.None), Is.EqualTo(5L));
        }

        [Test]
        public async Task GetAssetCount_MultiplePools_ReturnsSumOfCounts()
        {
            _mockPool1.Setup(p => p.GetAssetCount(It.IsAny<CancellationToken>())).ReturnsAsync(5L);
            _mockPool2.Setup(p => p.GetAssetCount(It.IsAny<CancellationToken>())).ReturnsAsync(10L);
            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object });
            Assert.That(await _multiPool.GetAssetCount(CancellationToken.None), Is.EqualTo(15L));
        }

        [Test]
        public async Task GetAssets_RequestZeroAssets_ReturnsEmptyCollection()
        {
            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object });
            var result = await _multiPool.GetAssets(0, CancellationToken.None);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public async Task GetAssets_TotalLessThanRequested_ReturnsAllAvailableAssets()
        {
            var asset1 = CreateAsset("a1");
            var asset2 = CreateAsset("a2");
            SetUpPool(_mockPool1, 2, asset1, asset2);

            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object });

            var result = (await _multiPool.GetAssets(5, CancellationToken.None)).ToList();
            Assert.That(result, Is.EquivalentTo(new[] { asset1, asset2 }));

            // Asked once, and for no more than it holds
            _mockPool1.Verify(p => p.GetAssets(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once());
            _mockPool1.Verify(p => p.GetAssets(2, It.IsAny<CancellationToken>()), Times.Once());
        }

        [Test]
        public async Task GetAssets_TotalMoreThanRequested_AggregatesAssets()
        {
            var pool1Assets = new[] { CreateAsset("p1a1"), CreateAsset("p1a2") };
            var pool2Assets = new[] { CreateAsset("p2a1"), CreateAsset("p2a2") };
            SetUpPool(_mockPool1, 2, pool1Assets);
            SetUpPool(_mockPool2, 2, pool2Assets);

            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object });

            // Neither pool holds 3, so both have to give some
            var result = (await _multiPool.GetAssets(3, CancellationToken.None)).ToList();
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result, Is.Unique);
            Assert.That(result.Intersect(pool1Assets), Is.Not.Empty);
            Assert.That(result.Intersect(pool2Assets), Is.Not.Empty);
        }

        [Test]
        public async Task GetAssets_PoolReturnsFewerAssetsThanCountSuggests_HandlesGracefully()
        {
            var p1a1 = CreateAsset("p1a1");
            var p1a2 = CreateAsset("p1a2");
            var p2a1 = CreateAsset("p2a1");

            // Pool1 reports 5 assets, but only has 2 to hand out
            SetUpPool(_mockPool1, 5, p1a1, p1a2);
            SetUpPool(_mockPool2, 1, p2a1);

            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object });

            // All 6 the counts suggest, so each pool is asked for everything it reports
            var result = (await _multiPool.GetAssets(6, CancellationToken.None)).ToList();

            Assert.That(result, Is.EquivalentTo(new[] { p1a1, p1a2, p2a1 }));
        }

        [Test]
        public async Task GetAssets_DifferentAssetCounts_AsksEachPoolOnceForItsShare()
        {
            var pool1Assets = new[] { CreateAsset("p1a1"), CreateAsset("p1a2"), CreateAsset("p1a3") };
            var p2a1 = CreateAsset("p2a1");
            SetUpPool(_mockPool1, 3, pool1Assets);
            SetUpPool(_mockPool2, 1, p2a1);

            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object });

            var result = (await _multiPool.GetAssets(4, CancellationToken.None)).ToList();

            Assert.That(result, Is.EquivalentTo(pool1Assets.Append(p2a1)));
            _mockPool1.Verify(p => p.GetAssets(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once());
            _mockPool1.Verify(p => p.GetAssets(3, It.IsAny<CancellationToken>()), Times.Once());
            _mockPool2.Verify(p => p.GetAssets(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once());
            _mockPool2.Verify(p => p.GetAssets(1, It.IsAny<CancellationToken>()), Times.Once());
        }

        [Test]
        public async Task GetAssets_PoolWithZeroCount_IsNotCalledForAssets()
        {
            var asset1 = CreateAsset("p1a1");
            SetUpPool(_mockPool1, 1, asset1);
            SetUpPool(_mockPool2, 0);
            SetUpPool(_mockPool3, 0);

            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object, _mockPool3.Object });

            // More than pool1 holds, so that the empty pools would be asked if they could be
            var result = (await _multiPool.GetAssets(5, CancellationToken.None)).ToList();

            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.First(), Is.SameAs(asset1));
            _mockPool2.Verify(p => p.GetAssets(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
            _mockPool3.Verify(p => p.GetAssets(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [Test]
        public async Task GetAssets_NoAssetsInAnyPool_ReturnsEmpty()
        {
            SetUpPool(_mockPool1, 0);
            SetUpPool(_mockPool2, 0);
            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object });

            var result = await _multiPool.GetAssets(1, CancellationToken.None);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public async Task GetAssets_AlbumOfThreePhotos_ReturnsEachPhotoOnce()
        {
            // Arrange - the pool PooledImmichFrameLogic builds for an account showing one album, over a
            // real cache: the album pool hands out from the same cached assets on every call, so its
            // count is what it holds rather than what it has left.
            var albumId = Guid.NewGuid();
            var album = new List<AssetResponseDto> { CreateAsset("a1"), CreateAsset("a2"), CreateAsset("a3") };

            var accountSettings = new Mock<IAccountSettings>();
            accountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { albumId });

            var immichApi = new Mock<ImmichApi>("", null!);
            immichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(albumId)), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = album } });

            using var apiCache = new ApiCache(TimeSpan.FromMinutes(5));
            _multiPool = new MultiAssetPool(new List<IAssetPool> { new AlbumAssetsPool(apiCache, immichApi.Object, accountSettings.Object) });

            // Act - the batch the frame asks for
            var result = (await _multiPool.GetAssets(25, CancellationToken.None)).ToList();

            // Assert - each photo once, rather than 25 drawn from the 3 and shown back to back
            Assert.That(result.Select(a => a.Id), Is.EquivalentTo(album.Select(a => a.Id)));
        }

        [Test]
        public async Task GetAssets_PoolsOf1000And10_ShareEachBatchByCount()
        {
            // Arrange
            var large = Enumerable.Range(0, 1000).Select(i => CreateAsset($"large{i}")).ToArray();
            var small = Enumerable.Range(0, 10).Select(i => CreateAsset($"small{i}")).ToArray();
            SetUpPool(_mockPool1, large.Length, large);
            SetUpPool(_mockPool2, small.Length, small);

            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object });

            // Act - 400 batches of 25, so 10,000 assets in all
            var batches = new List<List<AssetResponseDto>>();
            for (var i = 0; i < 400; i++)
            {
                batches.Add((await _multiPool.GetAssets(25, CancellationToken.None)).ToList());
            }

            // Assert - the small pool holds 1% of the assets, so about 100 of the 10,000 are its own.
            // Picking either pool alike would take about half of every batch from it, up to the 10 it
            // holds - some 4,000 in all. The range is more than 7 standard deviations wide on either
            // side of 100.
            var smallIds = small.Select(a => a.Id).ToHashSet();
            var fromSmall = batches.Sum(batch => batch.Count(a => smallIds.Contains(a.Id)));
            Assert.Multiple(() =>
            {
                Assert.That(batches.Select(batch => batch.Count), Has.All.EqualTo(25));
                Assert.That(fromSmall, Is.InRange(25, 250));
            });
        }

        [Test]
        public async Task GetAssets_AssetInTwoPools_ReturnsItOnce()
        {
            // Arrange - a favourite that is also in a configured album, found by each pool's own search
            var shared = CreateAsset("shared");
            SetUpPool(_mockPool1, 1, shared);
            SetUpPool(_mockPool2, 1, CreateAsset("shared"));

            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object });

            // Act
            var result = (await _multiPool.GetAssets(2, CancellationToken.None)).ToList();

            // Assert
            Assert.That(result.Select(a => a.Id), Is.EqualTo(new[] { shared.Id }));
        }

        [Test]
        public async Task GetAssets_ShufflesThePoolsTogether()
        {
            // Arrange
            var pool1Assets = Enumerable.Range(0, 20).Select(i => CreateAsset($"p1a{i}")).ToArray();
            var pool2Assets = Enumerable.Range(0, 20).Select(i => CreateAsset($"p2a{i}")).ToArray();
            SetUpPool(_mockPool1, pool1Assets.Length, pool1Assets);
            SetUpPool(_mockPool2, pool2Assets.Length, pool2Assets);

            _multiPool = new MultiAssetPool(new List<IAssetPool> { _mockPool1.Object, _mockPool2.Object });

            // Act - every asset of both
            var result = (await _multiPool.GetAssets(40, CancellationToken.None)).ToList();

            // Assert - the frame shows a batch in order, so a batch that came out a pool at a time was
            // shown a pool at a time. A shuffle puts either pool's 20 first once in 69 billion.
            var firstHalf = result.Take(20).ToList();
            Assert.That(firstHalf, Is.Not.SubsetOf(pool1Assets).And.Not.SubsetOf(pool2Assets));
        }
    }
}
