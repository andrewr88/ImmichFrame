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

    // A class rather than a record, so that two items with one key are still different objects.
    private sealed class Item(int key)
    {
        public int Key { get; } = key;
    }
}
