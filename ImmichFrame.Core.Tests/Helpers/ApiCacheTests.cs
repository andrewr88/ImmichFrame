using ImmichFrame.Core.Helpers;
using Microsoft.Extensions.Caching.Memory;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Helpers;

/// <summary>
/// <c>ApiCache</c> stands in front of every listing an asset pool pages out of Immich - albums,
/// people, tags, excluded albums - so how often a factory runs matters as much as what it returns.
/// <c>MemoryCache.GetOrCreateAsync</c> on its own only holds a value once there is one, so every
/// request that missed while a load was in flight started a load of its own: at startup, at every
/// expiry, and on every request when the refresh interval is zero.
/// </summary>
[TestFixture]
public class ApiCacheTests
{
    // Turns a hang into a failure rather than a stalled run.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    // Comfortably past a 1 ms time to live, which is what a refresh interval of zero caches for.
    private static readonly TimeSpan PastShortTtl = TimeSpan.FromMilliseconds(50);

    private ApiCache _cache = null!;

    [SetUp]
    public void Setup() => _cache = new ApiCache(TimeSpan.FromMinutes(5));

    [TearDown]
    public void TearDown() => _cache.Dispose();

    [Test]
    public async Task GetOrAddAsync_ConcurrentMisses_ShareOneLoad()
    {
        // Arrange - the load stalls until the test lets it finish, so every caller misses while it
        // is still in flight
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;

        async Task<object> Load()
        {
            Interlocked.Increment(ref loads);
            await finish.Task;
            return new object();
        }

        // Act
        var callers = Enumerable.Range(0, 5).Select(_ => _cache.GetOrAddAsync("key", Load)).ToList();
        finish.SetResult();
        var values = await Task.WhenAll(callers).WaitAsync(WaitLimit);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(loads, Is.EqualTo(1));
            Assert.That(values, Has.All.SameAs(values[0]));
        });
    }

    [Test]
    public async Task GetOrAddAsync_LoadOutlastsTimeToLive_LaterCallerStillJoinsIt()
    {
        // Arrange - a refresh interval of zero caches for 1 ms, far less than any page through Immich
        // takes. Expiry is for a value; a load still in flight is shared however long it runs.
        using var cache = new ApiCache(TimeSpan.FromMilliseconds(1));
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;

        async Task<object> Load()
        {
            Interlocked.Increment(ref loads);
            await finish.Task;
            return new object();
        }

        // Act
        var first = cache.GetOrAddAsync("key", Load);
        await Task.Delay(PastShortTtl);
        var second = cache.GetOrAddAsync("key", Load);
        finish.SetResult();
        var values = await Task.WhenAll(first, second).WaitAsync(WaitLimit);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(loads, Is.EqualTo(1));
            Assert.That(values[1], Is.SameAs(values[0]));
        });
    }

    [Test]
    public async Task GetOrAddAsync_FaultedLoad_IsNotCached()
    {
        // Act
        Assert.ThrowsAsync<HttpRequestException>(() => _cache.GetOrAddAsync("key",
            () => Task.FromException<int>(new HttpRequestException("Immich is unreachable"))));
        var retried = await _cache.GetOrAddAsync("key", () => Task.FromResult(42));

        // Assert - the next caller loads again rather than being handed the failure
        Assert.That(retried, Is.EqualTo(42));
    }

    [Test]
    public async Task GetOrAddAsync_CancelledLoad_IsNotCached()
    {
        // Act
        Assert.CatchAsync<OperationCanceledException>(() => _cache.GetOrAddAsync("key",
            () => Task.FromCanceled<int>(new CancellationToken(canceled: true))));
        var retried = await _cache.GetOrAddAsync("key", () => Task.FromResult(42));

        // Assert - the next caller loads again rather than being handed the cancellation
        Assert.That(retried, Is.EqualTo(42));
    }

    [Test]
    public async Task GetOrAddAsync_LoadFailsWhileOthersWaitOnIt_AllSeeTheFailureAndTheNextCallerLoadsAgain()
    {
        // Arrange - the first load fails once the callers waiting on it have joined; any later one
        // succeeds
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;

        async Task<int> Load()
        {
            if (Interlocked.Increment(ref loads) > 1) return 42;

            await finish.Task;
            throw new HttpRequestException("Immich is unreachable");
        }

        // Act
        var waiting = Enumerable.Range(0, 3).Select(_ => _cache.GetOrAddAsync("key", Load)).ToList();
        finish.SetResult();
        Assert.ThrowsAsync<HttpRequestException>(() => Task.WhenAll(waiting).WaitAsync(WaitLimit));
        var next = await _cache.GetOrAddAsync("key", Load).WaitAsync(WaitLimit);

        // Assert - one load, whose failure every caller waiting on it shares, and nothing of it left
        // behind for the caller after them
        Assert.Multiple(() =>
        {
            Assert.That(waiting, Has.All.Matches<Task<int>>(t => t.IsFaulted && t.Exception!.InnerException is HttpRequestException));
            Assert.That(next, Is.EqualTo(42));
            Assert.That(loads, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task GetOrAddAsync_WithinTimeToLive_ServesTheCachedValue()
    {
        // Arrange
        var loads = 0;
        Task<object> Load()
        {
            loads++;
            return Task.FromResult(new object());
        }

        // Act
        var first = await _cache.GetOrAddAsync("key", Load);
        var second = await _cache.GetOrAddAsync("key", Load);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(loads, Is.EqualTo(1));
            Assert.That(second, Is.SameAs(first));
        });
    }

    [Test]
    public async Task GetOrAddAsync_AfterTimeToLive_LoadsAgain()
    {
        // Arrange
        using var cache = new ApiCache(TimeSpan.FromMilliseconds(1));
        var loads = 0;
        Task<int> Load() => Task.FromResult(++loads);

        // Act
        await cache.GetOrAddAsync("key", Load);
        await Task.Delay(PastShortTtl);
        var reloaded = await cache.GetOrAddAsync("key", Load);

        // Assert
        Assert.That(reloaded, Is.EqualTo(2));
    }

    [Test]
    public async Task GetOrAddAsync_EntryOptions_AreTakenForEachNewEntry()
    {
        // Arrange - an absolute expiry, as DailyApiCache sets one: the first entry lapses at once, as
        // one made just before midnight does, and the next is good for a day. Options taken once and
        // reused would give the second entry the first one's expiry, already past.
        var entries = 0;
        using var cache = new ApiCache(() => new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.Now + (++entries == 1 ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromDays(1))
        });
        var loads = 0;
        Task<int> Load() => Task.FromResult(++loads);

        // Act
        await cache.GetOrAddAsync("key", Load);
        await Task.Delay(PastShortTtl);
        await cache.GetOrAddAsync("key", Load);
        var cached = await cache.GetOrAddAsync("key", Load);

        // Assert - loaded again once the first entry lapsed, and the second entry then held
        Assert.That(cached, Is.EqualTo(2));
    }
}
