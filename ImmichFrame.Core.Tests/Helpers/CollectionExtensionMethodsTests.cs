using ImmichFrame.Core.Helpers;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Helpers;

[TestFixture]
public class CollectionExtensionMethodsTests
{
    [Test]
    public void WhereExcludes_KeysEachItemOnce_AndKeepsTheRestInOrderWithDuplicates()
    {
        // Arrange - every item is its own object, so an excluded item is matched by its key alone
        var source = new List<Item> { new(1), new(2), new(3), new(2), new(4), new(5), new(3) };
        var excluded = new List<Item> { new(3), new(5), new(9) };
        var keyed = 0;

        object Key(Item item)
        {
            keyed++;
            return item.Key;
        }

        // Act
        var result = source.WhereExcludes(excluded, Key);
        var notReadYet = keyed;
        var remaining = result.Select(item => item.Key).ToList();

        // Assert - counted rather than timed: scanning the excluded items for every item keyed
        // them all again each time, n·m keys rather than n + m
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Is.EqualTo(new[] { 1, 2, 2, 4 }));
            Assert.That(notReadYet, Is.Zero, "nothing is read until the result is enumerated");
            Assert.That(keyed, Is.EqualTo(source.Count + excluded.Count));
        });
    }

    [Test]
    public async Task ChooseOne_ChoosesEachSourceInProportionToItsCount()
    {
        // Arrange
        var counts = new Dictionary<string, long> { ["large"] = 1000, ["small"] = 10, ["empty"] = 0 };
        var chosen = counts.Keys.ToDictionary(source => source, _ => 0);

        // Act
        for (var i = 0; i < 10_000; i++)
        {
            var source = await counts.Keys.ChooseOne(source => Task.FromResult(counts[source]));
            chosen[source!]++;
        }

        // Assert - the small source holds 1% of the count, so about 100 of the 10,000 choices; a
        // choice that ignored the counts would make it a third or a half. The range is more than
        // 7 standard deviations wide on either side of 100.
        Assert.Multiple(() =>
        {
            Assert.That(chosen["small"], Is.InRange(25, 250));
            Assert.That(chosen["empty"], Is.Zero);
        });
    }

    /// <summary>
    /// A source holding nothing spans none of the draw, wherever it stands. Placed first, it is
    /// reached by a draw of 0 before anything has been taken off the draw - where comparing with
    /// &lt;= rather than &lt; would choose it one draw in five here.
    /// </summary>
    [Test]
    public async Task ChooseOne_EmptySourceFirst_IsNeverChosen()
    {
        // Arrange - in this order
        (string Name, long Count)[] sources = [("empty", 0), ("only", 5)];
        var chosen = new List<string>();

        // Act
        for (var i = 0; i < 1_000; i++)
        {
            chosen.Add((await sources.ChooseOne(source => Task.FromResult(source.Count))).Name);
        }

        // Assert
        Assert.That(chosen, Has.All.EqualTo("only"));
    }

    /// <summary>
    /// TotalAccountImagesSelectionStrategy reads what this returns three times - to count it, to
    /// record each asset with the tracker and to hand them out - so it is the same items each time.
    /// Taken lazily from a shuffle, every read shuffled again, and the tracker recorded other assets
    /// than the frame was sent.
    /// </summary>
    [Test]
    public void TakeProportional_OfAShuffle_IsTheSameItemsEveryTimeItIsRead()
    {
        // Arrange
        var items = Enumerable.Range(0, 100).ToList();

        // Act
        var taken = items.Shuffle().TakeProportional(0.5);
        var firstRead = taken.ToList();
        var secondRead = taken.ToList();

        // Assert
        Assert.That(secondRead, Is.EqualTo(firstRead));
    }

    /// <summary>
    /// Rounded up, so that an account given a sliver of a batch still shows something: the strategy
    /// scales each account's batch of 25 by its share, and a small account's 1% of it is one asset,
    /// not none.
    /// </summary>
    [TestCase(1, 0.5, 1)]
    [TestCase(25, 0.01, 1)]
    [TestCase(25, 0.5, 13)]
    public void TakeProportional_PartOfAnItem_IsRoundedUpToAWholeOne(int count, double proportion, int expected)
    {
        // Act
        var taken = Enumerable.Range(0, count).TakeProportional(proportion);

        // Assert
        Assert.That(taken.Count(), Is.EqualTo(expected));
    }

    [Test]
    public void Shuffle_PutsTheItemsInEveryOrderAlike()
    {
        // Arrange
        var items = new[] { 1, 2, 3 };
        var orders = new Dictionary<string, int>();

        // Act
        for (var i = 0; i < 60_000; i++)
        {
            var order = string.Join(",", items.Shuffle());
            orders[order] = orders.GetValueOrDefault(order) + 1;
        }

        // Assert - each of the 6 orders about 10,000 times, where 500 either way is more than 5
        // standard deviations. The usual slip, swapping each item with any rather than with one not
        // yet placed, still makes all 6 orders, but some 8,900 or 11,100 times each.
        Assert.Multiple(() =>
        {
            Assert.That(orders, Has.Count.EqualTo(6));
            Assert.That(orders.Values, Has.All.InRange(9_500, 10_500));
            Assert.That(items, Is.EqualTo(new[] { 1, 2, 3 }), "the source itself is left in its order");
        });
    }

    // A class rather than a record, so that two items with one key are still different objects.
    private sealed class Item(int key)
    {
        public int Key { get; } = key;
    }
}
