using System.Collections;
using NUnit.Framework;
using Moq;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;

namespace ImmichFrame.Core.Tests.Logic.Pool;

[TestFixture]
public class CachingApiAssetsPoolTests
{
    private Mock<IApiCache> _mockApiCache;
    private TestableCachingApiAssetsPool _testPool;

    // Concrete implementation for testing the abstract class
    private class TestableCachingApiAssetsPool : CachingApiAssetsPool
    {
        public Func<Task<IEnumerable<AssetResponseDto>>>? LoadAssetsFunc { get; set; }

        public TestableCachingApiAssetsPool(IApiCache apiCache)
            : base(apiCache)
        {
        }

        protected override Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default)
        {
            return LoadAssetsFunc != null ? LoadAssetsFunc() : Task.FromResult(Enumerable.Empty<AssetResponseDto>());
        }
    }

    [SetUp]
    public void Setup()
    {
        _mockApiCache = new Mock<IApiCache>();
        _testPool = new TestableCachingApiAssetsPool(_mockApiCache.Object);

        // Default setup for ApiCache to execute the factory function
        _mockApiCache.Setup(c => c.GetOrAddAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()
            ))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>(async (key, factory) => await factory());
    }

    private List<AssetResponseDto> CreateSampleAssets()
    {
        return new List<AssetResponseDto>
        {
            new AssetResponseDto { Id = FixtureHelpers.GuidFor("1"), Type = AssetTypeEnum.IMAGE, IsArchived = false, ExifInfo = new ExifResponseDto { DateTimeOriginal = DateTime.Now.AddDays(-10), Rating = 5 } },
            new AssetResponseDto { Id = FixtureHelpers.GuidFor("2"), Type = AssetTypeEnum.VIDEO, IsArchived = false, ExifInfo = new ExifResponseDto { DateTimeOriginal = DateTime.Now.AddDays(-10) } }, // Video asset
            new AssetResponseDto { Id = FixtureHelpers.GuidFor("3"), Type = AssetTypeEnum.IMAGE, IsArchived = true, ExifInfo = new ExifResponseDto { DateTimeOriginal = DateTime.Now.AddDays(-5), Rating = 3 } }, // Archived asset
            new AssetResponseDto { Id = FixtureHelpers.GuidFor("4"), Type = AssetTypeEnum.IMAGE, IsArchived = false, ExifInfo = new ExifResponseDto { DateTimeOriginal = DateTime.Now.AddDays(-2), Rating = 5 } },
            new AssetResponseDto { Id = FixtureHelpers.GuidFor("5"), Type = AssetTypeEnum.IMAGE, IsArchived = false, ExifInfo = new ExifResponseDto { DateTimeOriginal = DateTime.Now.AddYears(-1), Rating = 1 } },
        };
    }

    [Test]
    public async Task GetAssetCount_ReturnsLoadedCount()
    {
        // Arrange
        var assets = CreateSampleAssets();
        _testPool.LoadAssetsFunc = () => Task.FromResult<IEnumerable<AssetResponseDto>>(assets);

        // Act
        var count = await _testPool.GetAssetCount();

        // Assert - filtering is up to the pool that loads them
        Assert.That(count, Is.EqualTo(assets.Count));
    }

    [Test]
    public async Task GetAssets_ReturnsRequestedNumberOfAssets()
    {
        // Arrange
        var assets = CreateSampleAssets();
        _testPool.LoadAssetsFunc = () => Task.FromResult<IEnumerable<AssetResponseDto>>(assets);

        // Act
        var result = (await _testPool.GetAssets(2)).ToList();

        // Assert
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result.Select(a => a.Id), Is.SubsetOf(assets.Select(a => a.Id)));
    }

    [Test]
    public async Task GetAssets_ReturnsAllAvailableIfLessThanRequested()
    {
        // Arrange
        var assets = CreateSampleAssets().Where(a => !a.IsArchived).ToList();
        _testPool.LoadAssetsFunc = () => Task.FromResult<IEnumerable<AssetResponseDto>>(assets);

        // Act
        var result = (await _testPool.GetAssets(5)).ToList();

        // Assert
        Assert.That(result.Count, Is.EqualTo(assets.Count));
    }

    /// <summary>
    /// An asset loaded twice is one asset: counted twice, it weighed the pool's share of a batch as two,
    /// and it could be handed out twice in one batch.
    /// </summary>
    [Test]
    public async Task GetAssets_AssetLoadedTwice_IsCountedAndReturnedOnce()
    {
        // Arrange
        var asset = CreateSampleAssets()[0];
        var sameAssetAgain = new AssetResponseDto { Id = asset.Id, Type = asset.Type };
        _testPool.LoadAssetsFunc = () => Task.FromResult<IEnumerable<AssetResponseDto>>([asset, sameAssetAgain]);

        // Act
        var count = await _testPool.GetAssetCount();
        var result = (await _testPool.GetAssets(2)).ToList();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(result.Select(a => a.Id), Is.EqualTo(new[] { asset.Id }));
        });
    }

    [Test]
    public async Task AllAssets_UsesCache_LoadAssetsCalledOnce()
    {
        // Arrange
        var assets = CreateSampleAssets();
        var loadAssetsCallCount = 0;
        _testPool.LoadAssetsFunc = () =>
        {
            loadAssetsCallCount++;
            return Task.FromResult<IEnumerable<AssetResponseDto>>(assets);
        };

        // Setup cache to really cache after the first call
        Dictionary<string, IEnumerable<AssetResponseDto>> cacheStore = new();
        _mockApiCache.Setup(c => c.GetOrAddAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()
            ))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>(async (key, factory) =>
            {
                if (!cacheStore.ContainsKey(key))
                {
                    cacheStore[key] = await factory();
                }

                return cacheStore[key];
            });

        // Act
        await _testPool.GetAssetCount(); // First call, should trigger LoadAssets
        await _testPool.GetAssetCount(); // Second call, should use cache
        await _testPool.GetAssets(1); // Third call, should use cache

        // Assert
        Assert.That(loadAssetsCallCount, Is.EqualTo(1), "LoadAssets should only be called once.");
    }

    /// <summary>
    /// What is cached is read by every request for a count or an asset, so it is read out of the load
    /// once, when it is cached. Cached as a lazy query, every request ran it over the load again.
    /// </summary>
    [Test]
    public async Task GetAssets_ManyRequests_ReadTheLoadedAssetsOnce()
    {
        // Arrange
        var loaded = new CountingEnumerable<AssetResponseDto>(CreateSampleAssets());
        using var cache = new ApiCache(TimeSpan.FromHours(1));
        var pool = new TestableCachingApiAssetsPool(cache)
        {
            LoadAssetsFunc = () => Task.FromResult<IEnumerable<AssetResponseDto>>(loaded)
        };

        // Act
        await pool.GetAssetCount();
        (await pool.GetAssets(2)).ToList();
        (await pool.GetAssets(2)).ToList();

        // Assert
        Assert.That(loaded.Enumerations, Is.EqualTo(1));
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
