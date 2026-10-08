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
