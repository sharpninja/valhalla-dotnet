using SharpNinja.Valhalla.Traffic.Routing;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class BoundedAsyncCacheTests
{
    [Fact]
    public async Task ClearAndInvalidateChurn_RemovesBackingFifoNodesBelowCapacity()
    {
        using var cache = new BoundedAsyncCache<int, int>(
            capacity: 32,
            maximumConcurrentBuilds: 4);

        for (var cycle = 0; cycle < 250; cycle++)
        {
            int firstKey = cycle * 4;
            for (var offset = 0; offset < 4; offset++)
            {
                int key = firstKey + offset;
                Assert.Equal(
                    key,
                    await cache.GetOrAddAsync(
                        key,
                        _ => Task.FromResult(key),
                        TestContext.Current.CancellationToken));
            }

            cache.RemoveWhere(key => key == firstKey || key == firstKey + 1);
            Assert.Equal(2, cache.Count);
            Assert.Equal(cache.Count, cache.TrackedStorageCount);

            cache.Clear();
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.TrackedStorageCount);
        }

        Assert.Equal(1_000, cache.DisposedEntryResourceCount);
    }

    [Fact]
    public async Task CapacityEviction_IsStrictFifoAndDoesNotPromoteCacheHits()
    {
        using var cache = new BoundedAsyncCache<int, int>(
            capacity: 2,
            maximumConcurrentBuilds: 2);
        var builds = new int[4];

        Task<int> Build(int key)
        {
            builds[key]++;
            return Task.FromResult(key);
        }

        Assert.Equal(1, await cache.GetOrAddAsync(1, _ => Build(1), TestContext.Current.CancellationToken));
        Assert.Equal(2, await cache.GetOrAddAsync(2, _ => Build(2), TestContext.Current.CancellationToken));
        Assert.Equal(1, await cache.GetOrAddAsync(1, _ => Build(1), TestContext.Current.CancellationToken));
        Assert.Equal(3, await cache.GetOrAddAsync(3, _ => Build(3), TestContext.Current.CancellationToken));
        Assert.Equal(2, await cache.GetOrAddAsync(2, _ => Build(2), TestContext.Current.CancellationToken));
        Assert.Equal(1, await cache.GetOrAddAsync(1, _ => Build(1), TestContext.Current.CancellationToken));

        Assert.Equal(2, builds[1]);
        Assert.Equal(1, builds[2]);
        Assert.Equal(1, builds[3]);
        Assert.Equal(2, cache.Count);
        Assert.Equal(cache.Count, cache.TrackedStorageCount);
    }

    [Fact]
    public async Task CapacityBackpressure_DoesNotEvictEntryWithActiveLease()
    {
        using var cache = new BoundedAsyncCache<int, int>(
            capacity: 1,
            maximumConcurrentBuilds: 1);
        using BoundedAsyncCache<int, int>.Lease first = await cache.AcquireAsync(
            1,
            _ => Task.FromResult(1),
            TestContext.Current.CancellationToken);

        Task<int> second = cache.GetOrAddAsync(
            2,
            _ => Task.FromResult(2),
            TestContext.Current.CancellationToken);

        for (var iteration = 0; iteration < 8; iteration++)
        {
            await Task.Yield();
        }

        Assert.False(second.IsCompleted);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.TrackedStorageCount);

        first.Dispose();

        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.TrackedStorageCount);
    }

    [Fact]
    public async Task ActiveOldestEntry_DoesNotBlockEvictionOfOldestCompletedEntry()
    {
        using var cache = new BoundedAsyncCache<int, int>(
            capacity: 2,
            maximumConcurrentBuilds: 2);
        using BoundedAsyncCache<int, int>.Lease activeOldest = await cache.AcquireAsync(
            1,
            _ => Task.FromResult(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            2,
            await cache.GetOrAddAsync(
                2,
                _ => Task.FromResult(2),
                TestContext.Current.CancellationToken));

        Task<int> admitted = cache.GetOrAddAsync(
            3,
            _ => Task.FromResult(3),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            3,
            await admitted.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        Assert.Equal(2, cache.Count);
        Assert.Equal(2, cache.TrackedStorageCount);
        Assert.Equal(1, cache.DisposedEntryResourceCount);
        Assert.Equal(1, activeOldest.Value);

        activeOldest.Dispose();
    }

    [Fact]
    public async Task BuildAdmission_BoundsConcurrentFactories()
    {
        using var cache = new BoundedAsyncCache<int, int>(
            capacity: 2,
            maximumConcurrentBuilds: 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeBuilds = 0;
        var maximumActiveBuilds = 0;

        async Task<int> BuildAsync(int key, CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref activeBuilds);
            InterlockedExtensions.Max(ref maximumActiveBuilds, active);
            try
            {
                if (key == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondStarted.TrySetResult();
                }

                return key;
            }
            finally
            {
                Interlocked.Decrement(ref activeBuilds);
            }
        }

        Task<int> first = cache.GetOrAddAsync(
            1,
            token => BuildAsync(1, token),
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task<int> second = cache.GetOrAddAsync(
            2,
            token => BuildAsync(2, token),
            TestContext.Current.CancellationToken);

        for (var iteration = 0; iteration < 8; iteration++)
        {
            await Task.Yield();
        }

        Assert.False(secondStarted.Task.IsCompleted);
        releaseFirst.TrySetResult();

        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(secondStarted.Task.IsCompleted);
        Assert.Equal(1, maximumActiveBuilds);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelSharedBuildOrPoisonCachedValue()
    {
        using var cache = new BoundedAsyncCache<int, int>(
            capacity: 2,
            maximumConcurrentBuilds: 1);
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        async Task<int> BuildAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref buildCount);
            buildStarted.TrySetResult();
            await releaseBuild.Task.WaitAsync(cancellationToken);
            return 42;
        }

        Task<int> primary = cache.GetOrAddAsync(
            1,
            BuildAsync,
            TestContext.Current.CancellationToken);
        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var waiterCancellation = new CancellationTokenSource();
        Task<int> cancelledWaiter = cache.GetOrAddAsync(
            1,
            _ => throw new InvalidOperationException("Shared factory must be reused."),
            waiterCancellation.Token);
        waiterCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await cancelledWaiter);
        Assert.False(primary.IsCompleted);

        releaseBuild.TrySetResult();
        Assert.Equal(42, await primary.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(
            42,
            await cache.GetOrAddAsync(
                1,
                _ => throw new InvalidOperationException("Completed value must be reused."),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, buildCount);
    }

    [Fact]
    public async Task InvalidateActiveEntry_CancelsWorkRemovesStorageAndAllowsRetry()
    {
        using var cache = new BoundedAsyncCache<int, int>(
            capacity: 2,
            maximumConcurrentBuilds: 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        async Task<int> BuildAsync(CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return 84;
        }

        Task<int> first = cache.GetOrAddAsync(
            7,
            BuildAsync,
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cache.RemoveWhere(key => key == 7);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await first);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TrackedStorageCount);

        Assert.Equal(
            84,
            await cache.GetOrAddAsync(
                7,
                BuildAsync,
                TestContext.Current.CancellationToken));
        Assert.Equal(2, attempts);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.TrackedStorageCount);
    }

    [Fact]
    public async Task FaultedBuild_IsRemovedAndRetrySucceeds()
    {
        using var cache = new BoundedAsyncCache<int, int>(
            capacity: 1,
            maximumConcurrentBuilds: 1);
        var attempts = 0;

        Task<int> BuildAsync(CancellationToken _)
        {
            int attempt = Interlocked.Increment(ref attempts);
            return attempt == 1
                ? Task.FromException<int>(new InvalidOperationException("fixture fault"))
                : Task.FromResult(21);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await cache.GetOrAddAsync(
                1,
                BuildAsync,
                TestContext.Current.CancellationToken));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TrackedStorageCount);
        Assert.Equal(1, cache.DisposedEntryResourceCount);

        Assert.Equal(
            21,
            await cache.GetOrAddAsync(
                1,
                BuildAsync,
                TestContext.Current.CancellationToken));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Dispose_ThrowingCancellationCallback_DoesNotEscapeOrBlockTeardown()
    {
        var cache = new BoundedAsyncCache<int, int>(
            capacity: 1,
            maximumConcurrentBuilds: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> BuildAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static () => throw new InvalidOperationException("hostile cancellation callback"));
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        }

        Task<int> work = cache.GetOrAddAsync(
            1,
            BuildAsync,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Exception? disposeException = null;
        try
        {
            cache.Dispose();
        }
        catch (Exception exception)
        {
            disposeException = exception;
        }

        Assert.Null(disposeException);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await work);
        await WaitUntilAsync(() => cache.InfrastructureDisposed);

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TrackedStorageCount);
        Assert.Equal(1, cache.DisposedEntryResourceCount);
    }

    [Fact]
    public async Task Dispose_WithHeldLease_DefersInfrastructureDisposalUntilLeaseRelease()
    {
        var cache = new BoundedAsyncCache<int, int>(
            capacity: 1,
            maximumConcurrentBuilds: 1);
        BoundedAsyncCache<int, int>.Lease lease = await cache.AcquireAsync(
            1,
            _ => Task.FromResult(1),
            TestContext.Current.CancellationToken);

        cache.Dispose();

        Assert.False(cache.InfrastructureDisposed);
        Assert.Equal(1, cache.Count);

        lease.Dispose();
        await WaitUntilAsync(() => cache.InfrastructureDisposed);

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TrackedStorageCount);
        Assert.Equal(1, cache.DisposedEntryResourceCount);
    }

    [Fact]
    public async Task Dispose_ReleasesAdmissionWaiterAndCancelsActiveWorkWithoutDeadlock()
    {
        var cache = new BoundedAsyncCache<int, int>(
            capacity: 2,
            maximumConcurrentBuilds: 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFactoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> FirstBuildAsync(CancellationToken cancellationToken)
        {
            firstStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        }

        Task<int> first = cache.GetOrAddAsync(
            1,
            FirstBuildAsync,
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task<int> second = cache.GetOrAddAsync(
            2,
            _ =>
            {
                secondFactoryEntered.TrySetResult();
                return Task.FromResult(2);
            },
            TestContext.Current.CancellationToken);

        for (var iteration = 0; iteration < 8; iteration++)
        {
            await Task.Yield();
        }

        Assert.False(secondFactoryEntered.Task.IsCompleted);
        cache.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await second);
        Assert.False(secondFactoryEntered.Task.IsCompleted);
        await WaitUntilAsync(() => cache.InfrastructureDisposed);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TrackedStorageCount);
        Assert.Equal(2, cache.DisposedEntryResourceCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(1),
                TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int observed = Volatile.Read(ref target);
            while (observed < value)
            {
                int exchanged = Interlocked.CompareExchange(ref target, value, observed);
                if (exchanged == observed)
                {
                    return;
                }

                observed = exchanged;
            }
        }
    }
}
