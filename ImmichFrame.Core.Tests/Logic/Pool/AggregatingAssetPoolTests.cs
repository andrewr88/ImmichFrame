using NUnit.Framework;
using ImmichFrame.Core.Api; // For AssetResponseDto
using ImmichFrame.Core.Logic.Pool; // For AggregatingAssetPool

namespace ImmichFrame.Core.Tests.Logic.Pool
{
    [TestFixture]
    public class AggregatingAssetPoolTests
    {
        // The least an AggregatingAssetPool can be, so that what is tested is its own part: a batch
        // made of one GetNextAsset after another. Hands out the assets it is given in order, a null
        // standing for a moment when it had none to give.
        private class TestableAggregatingAssetPool(params AssetResponseDto?[] assets) : AggregatingAssetPool
        {
            private readonly Queue<AssetResponseDto?> _assets = new(assets);

            public int NextAssetRequests { get; private set; }

            public override Task<long> GetAssetCount(CancellationToken ct = default) => Task.FromResult((long)_assets.Count);

            protected override Task<AssetResponseDto?> GetNextAsset(CancellationToken ct)
            {
                NextAssetRequests++;
                return Task.FromResult(_assets.Count > 0 ? _assets.Dequeue() : null);
            }
        }

        private AssetResponseDto CreateAsset(string id) => new AssetResponseDto { Id = FixtureHelpers.GuidFor(id), OriginalPath = $"/path/{id}.jpg", Type = AssetTypeEnum.IMAGE, ExifInfo = new ExifResponseDto() };

        [Test]
        public async Task GetAssets_StopsAtTheFirstMissingAsset()
        {
            // Arrange - another asset is still to come after the gap
            var asset1 = CreateAsset("a1");
            var asset2 = CreateAsset("a2");
            var pool = new TestableAggregatingAssetPool(asset1, asset2, null, CreateAsset("a3"));

            // Act
            var result = (await pool.GetAssets(4, CancellationToken.None)).ToList();

            // Assert
            Assert.That(result, Is.EqualTo(new[] { asset1, asset2 }));
            Assert.That(pool.NextAssetRequests, Is.EqualTo(3), "Nothing should be asked for past the missing asset");
        }
    }
}
