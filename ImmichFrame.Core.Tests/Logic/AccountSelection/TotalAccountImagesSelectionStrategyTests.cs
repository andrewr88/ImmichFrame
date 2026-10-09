using Moq;
using NUnit.Framework;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.AccountSelection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichFrame.Core.Tests.Logic.AccountSelection
{
    [TestFixture]
    public class TotalAccountImagesSelectionStrategyTests
    {
        private Mock<IAssetAccountTracker> _mockTracker;

        [SetUp]
        public void Setup()
        {
            _mockTracker = new Mock<IAssetAccountTracker>();
            _mockTracker
                .Setup(t => t.RecordAssetLocation(It.IsAny<IAccountImmichFrameLogic>(), It.IsAny<Guid>()))
                .ReturnsAsync(true);
        }

        private TotalAccountImagesSelectionStrategy CreateStrategy(params IAccountImmichFrameLogic[] accounts)
            => new(NullLogger<TotalAccountImagesSelectionStrategy>.Instance, _mockTracker.Object, accounts.ToList());

        private static Mock<IAccountImmichFrameLogic> CreateAccount(long totalAssets, IEnumerable<AssetResponseDto> assets)
        {
            var mock = new Mock<IAccountImmichFrameLogic>();
            mock.Setup(a => a.GetTotalAssets()).ReturnsAsync(totalAssets);
            mock.Setup(a => a.GetAssets()).ReturnsAsync(assets);
            return mock;
        }

        private static AssetResponseDto CreateAsset() => new()
        {
            Id = Guid.NewGuid(),
            OriginalPath = "/path/asset.jpg",
            Type = AssetTypeEnum.IMAGE,
        };

        [Test]
        public async Task GetAssets_NoAccounts_ReturnsEmpty()
        {
            var strategy = CreateStrategy();

            var assets = await strategy.GetAssets();

            Assert.That(assets, Is.Empty);
        }

        [Test]
        public async Task GetAssets_AllAccountsEmpty_ReturnsEmptyWithoutNaN()
        {
            var account1 = CreateAccount(0, Enumerable.Empty<AssetResponseDto>());
            var account2 = CreateAccount(0, Enumerable.Empty<AssetResponseDto>());
            var strategy = CreateStrategy(account1.Object, account2.Object);

            var assets = await strategy.GetAssets();

            Assert.That(assets, Is.Empty);
        }

        [Test]
        public async Task GetAssets_UnevenAccounts_LargestAccountKeepsAllAssets()
        {
            var largeAssets = Enumerable.Range(0, 10).Select(_ => CreateAsset()).ToList();
            var smallAssets = Enumerable.Range(0, 10).Select(_ => CreateAsset()).ToList();
            var largeAccount = CreateAccount(1000, largeAssets);
            var smallAccount = CreateAccount(100, smallAssets);
            var strategy = CreateStrategy(largeAccount.Object, smallAccount.Object);

            var result = (await strategy.GetAssets()).ToList();

            var fromLarge = result.Count(t => ReferenceEquals(t.Item1, largeAccount.Object));
            var fromSmall = result.Count(t => ReferenceEquals(t.Item1, smallAccount.Object));
            Assert.Multiple(() =>
            {
                // The account with the most assets keeps its full batch, smaller ones are scaled.
                Assert.That(fromLarge, Is.EqualTo(10));
                Assert.That(fromSmall, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task GetAssets_RecordsEveryReturnedAssetWithTracker()
        {
            var assets = Enumerable.Range(0, 5).Select(_ => CreateAsset()).ToList();
            var account = CreateAccount(5, assets);
            var strategy = CreateStrategy(account.Object);

            var result = (await strategy.GetAssets()).ToList();

            Assert.That(result, Has.Count.EqualTo(5));
            _mockTracker.Verify(
                t => t.RecordAssetLocation(account.Object, It.IsAny<Guid>()),
                Times.Exactly(5));
        }

        /// <summary>
        /// The tracker is how a later request for an asset finds the account holding it, so what it
        /// records is what is handed out. A smaller account's share is a random part of its assets,
        /// read once to record it and once more to return it: drawn afresh for each read, the tracker
        /// recorded one part and the frame was sent another, whose assets no account was found for.
        /// </summary>
        [Test]
        public async Task GetAssets_SmallerAccountsShare_IsWhatTheTrackerRecords()
        {
            // Arrange - the small account holds half the large one's count, so half its assets are taken
            var largeAccount = CreateAccount(1000, Enumerable.Range(0, 40).Select(_ => CreateAsset()).ToList());
            var smallAccount = CreateAccount(500, Enumerable.Range(0, 40).Select(_ => CreateAsset()).ToList());
            var recorded = new List<(IAccountImmichFrameLogic, Guid)>();
            _mockTracker
                .Setup(t => t.RecordAssetLocation(It.IsAny<IAccountImmichFrameLogic>(), It.IsAny<Guid>()))
                .Callback((IAccountImmichFrameLogic account, Guid assetId) => recorded.Add((account, assetId)))
                .ReturnsAsync(true);
            var strategy = CreateStrategy(largeAccount.Object, smallAccount.Object);

            // Act
            var returned = (await strategy.GetAssets()).Select(t => (t.Item1, t.Item2.Id)).ToList();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(returned.Count(t => ReferenceEquals(t.Item1, smallAccount.Object)), Is.EqualTo(20),
                    "the small account's share is a part of its assets");
                Assert.That(recorded, Is.EquivalentTo(returned));
            });
        }

        [Test]
        public async Task GetNextAsset_ReturnsAssetAndRecordsLocation()
        {
            var asset = CreateAsset();
            var account = CreateAccount(1, new[] { asset });
            account.Setup(a => a.GetNextAsset()).ReturnsAsync(asset);
            var strategy = CreateStrategy(account.Object);

            var result = await strategy.GetNextAsset();

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.Item2.Id, Is.EqualTo(asset.Id));
            _mockTracker.Verify(t => t.RecordAssetLocation(account.Object, asset.Id), Times.Once);
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task GetNextAsset_AllAccountsEmpty_ReturnsNull(int accountCount)
        {
            // An empty library or a filter matching nothing reports zero assets, which leaves
            // the count-weighted pick with no account to choose.
            var accounts = Enumerable.Range(0, accountCount)
                .Select(_ => CreateAccount(0, Enumerable.Empty<AssetResponseDto>()).Object)
                .ToArray();
            var strategy = CreateStrategy(accounts);

            var result = await strategy.GetNextAsset();

            Assert.That(result, Is.Null);
        }
    }
}
