using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.AccountSelection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Logic.AccountSelection;

[TestFixture]
public class BloomFilterAssetAccountTrackerTests
{
    // Far more than an account's filter is first sized for, however few assets the account counts.
    private const int HandedOut = 20_000;

    private BloomFilterAssetAccountTracker _tracker;
    private Mock<IAccountImmichFrameLogic> _account;

    // Seeded, so that the ids - and with them whatever a filter happens to claim - are the same on
    // every run.
    private Random _random;

    [SetUp]
    public void Setup()
    {
        _tracker = new BloomFilterAssetAccountTracker(NullLogger<BloomFilterAssetAccountTracker>.Instance);
        _random = new Random(1009);

        // Counting ten assets, as a memories-only account counts the day's memories.
        _account = new Mock<IAccountImmichFrameLogic>();
        _account.Setup(a => a.GetTotalAssets()).ReturnsAsync(10);
    }

    private Guid NextId()
    {
        var bytes = new byte[16];
        _random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private async Task<List<Guid>> HandOut(int count)
    {
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var id = NextId();
            await _tracker.RecordAssetLocation(_account.Object, id);
            ids.Add(id);
        }

        return ids;
    }

    // Whether the tracker takes the account for the asset's: it asks the account, rather than
    // finding none to ask.
    private async Task<bool> Claims(Guid assetId)
    {
        try
        {
            return await _tracker.ForAsset(assetId, _ => Task.FromResult(true));
        }
        catch (AssetNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// An account's filter is sized by the account's count, which is only an estimate of how many
    /// assets it will hand out: a memories-only account counts today's memories, and hands out a new
    /// day's every day. Sized once, the filter filled until it claimed every asset there is, so any
    /// request for an asset went to the account, and its failure was taken for a false positive.
    /// </summary>
    [Test]
    public async Task ForAsset_FarMoreAssetsHandedOutThanCounted_RarelyClaimsAnAssetNeverHandedOut()
    {
        // Arrange
        await HandOut(HandedOut);

        // Act
        var claimed = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (await Claims(NextId())) claimed++;
        }

        // Assert - where a full filter claims nearly every asset. The first is built for 1% and each
        // added one for half the rate of the one before, so together they stay under 2%: under 3%
        // allows for a sample of 1,000.
        Assert.That(claimed, Is.LessThan(30));
    }

    [Test]
    public async Task ForAsset_FarMoreAssetsHandedOutThanCounted_StillFindsEveryOne()
    {
        // Arrange - from the first, recorded before the filter had to grow, to the last
        var handedOut = await HandOut(HandedOut);

        // Act
        var missed = new List<Guid>();
        foreach (var id in handedOut)
        {
            if (!await Claims(id)) missed.Add(id);
        }

        // Assert
        Assert.That(missed, Is.Empty);
    }
}
