using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Logic.AccountSelection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Logic;

[TestFixture]
public class MultiImmichFrameLogicDelegateTests
{
    private readonly Guid _assetId = Guid.NewGuid();
    private Mock<IAccountImmichFrameLogic>[] _accounts;
    private MultiImmichFrameLogicDelegate _sut;

    [SetUp]
    public async Task Setup()
    {
        _accounts = [CreateAccount("http://immich-one"), CreateAccount("http://immich-two")];
        var accounts = _accounts.Select(a => a.Object).ToList();

        var serverSettings = new Mock<IServerSettings>();
        serverSettings.SetupGet(s => s.Accounts).Returns(accounts.Select(a => a.AccountSettings).ToList());

        // The real tracker and strategy: the fallback to the next account lives in the tracker, and
        // only these Task-returning lookups show whether it holds.
        var tracker = new BloomFilterAssetAccountTracker(NullLogger<BloomFilterAssetAccountTracker>.Instance);
        _sut = new MultiImmichFrameLogicDelegate(
            serverSettings.Object,
            settings => accounts.Single(a => a.AccountSettings == settings),
            NullLogger<MultiImmichFrameLogicDelegate>.Instance,
            logics => new TotalAccountImagesSelectionStrategy(
                NullLogger<TotalAccountImagesSelectionStrategy>.Instance, tracker, logics));

        // Both filters claim the asset, as they do for a Bloom false positive or an asset both
        // accounts can see.
        foreach (var account in accounts)
            await tracker.RecordAssetLocation(account, _assetId);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
    }

    private static Mock<IAccountImmichFrameLogic> CreateAccount(string serverUrl)
    {
        var settings = new Mock<IAccountSettings>();
        settings.SetupGet(s => s.ImmichServerUrl).Returns(serverUrl);

        var account = new Mock<IAccountImmichFrameLogic>();
        account.SetupGet(a => a.AccountSettings).Returns(settings.Object);
        account.Setup(a => a.GetTotalAssets()).ReturnsAsync(10);
        return account;
    }

    private static ApiException ImmichError(int status) =>
        new($"Unexpected status code ({status}).", status, string.Empty, new Dictionary<string, IEnumerable<string>>(), null!);

    [Test]
    public async Task GetAssetInfoById_FirstAccountFailsAsynchronously_ServedByNextAccount()
    {
        // Arrange - the tracker's enumeration order is not fixed, so whichever account is asked
        // first fails, and it fails the way a real lookup does: inside the Task it returns.
        var calls = 0;
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAssetInfoById(_assetId)).Returns(async () =>
            {
                await Task.Yield();
                if (Interlocked.Increment(ref calls) == 1) throw new AssetNotFoundException();
                return new AssetResponseDto { Id = _assetId };
            });
        }

        // Act
        var asset = await _sut.GetAssetInfoById(_assetId);

        // Assert
        Assert.That(asset.Id, Is.EqualTo(_assetId));
    }

    [Test]
    public async Task GetAlbumInfoById_FirstAccountThrowsSynchronously_ServedByNextAccount()
    {
        // Arrange - the other way an account can fail: throwing before it hands back a Task at all.
        IEnumerable<AlbumResponseDto> albums = [new AlbumResponseDto()];
        var calls = 0;
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAlbumInfoById(_assetId)).Returns(() =>
                Interlocked.Increment(ref calls) == 1
                    ? throw new HttpRequestException("Connection refused")
                    : Task.FromResult(albums));
        }

        // Act
        var result = await _sut.GetAlbumInfoById(_assetId);

        // Assert
        Assert.That(result, Is.SameAs(albums));
    }

    [Test]
    public void GetAssetFacesById_EveryAccountFails_ThrowsAssetNotFound()
    {
        // Arrange
        foreach (var account in _accounts)
            account.Setup(a => a.GetAssetFacesById(_assetId)).ThrowsAsync(ImmichError(400));

        // Act / Assert
        Assert.That(async () => await _sut.GetAssetFacesById(_assetId), Throws.TypeOf<AssetNotFoundException>());
        foreach (var account in _accounts)
            account.Verify(a => a.GetAssetFacesById(_assetId), Times.Once);
    }

    [Test]
    public void GetAsset_RangeNotSatisfiable_ReachesCallerUnchanged()
    {
        // Arrange - a 416 means the account holds the asset and the range lies outside it. It is
        // no false positive, and AssetController answers exactly this exception with a 416, so it
        // must not come back as AssetNotFoundException.
        const string pastTheEnd = "bytes=999999999-";
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAsset(_assetId, AssetTypeEnum.VIDEO, pastTheEnd))
                .ThrowsAsync(ImmichError(416));
        }

        // Act
        var thrown = Assert.ThrowsAsync<ApiException>(() => _sut.GetAsset(_assetId, AssetTypeEnum.VIDEO, pastTheEnd));

        // Assert
        Assert.That(thrown!.StatusCode, Is.EqualTo(416));
    }
}
