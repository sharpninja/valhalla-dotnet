using System.Buffers.Binary;
using SharpNinja.Valhalla.Generation.Differential;
using SharpNinja.Valhalla.Generation.Parallel;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Parallel;

public sealed class ParallelSequenceSortTests
{
    [Fact]
    public void PsrsSort_MatchesStableScalarReference()
    {
        var source = Enumerable.Range(0, 4096)
            .Select(index => new SortRecord((index * 37) % 29, index, index * 11))
            .Reverse()
            .ToArray();
        var expected = source
            .OrderBy(static record => record.Key)
            .ToArray();

        foreach (var degree in SupportedDegrees())
        {
            var actual = source.ToArray();
            var receipt = ParallelSequenceSorter.Sort(
                actual,
                new ParallelSequenceSortOptions(
                    degree,
                    MemoryBudgetBytes: 4 * 1024 * 1024),
                static (left, right) => left.Key.CompareTo(right.Key),
                TestContext.Current.CancellationToken);

            Assert.Equal(expected, actual);
            Assert.InRange(receipt.PartitionCount, 1, degree);
            Assert.InRange(receipt.MaxObservedConcurrency, 1, degree);
            Assert.InRange(receipt.PeakMemoryBytes, 1, 4 * 1024 * 1024);
        }
    }

    private static int[] SupportedDegrees() => [1, 2, 4, 8, 16, 32];

    private readonly record struct SortRecord(int Key, int InputOrdinal, int Value);
}

public sealed class GenerationConcurrencyTests
{
    [Fact]
    public async Task WorkScheduling_RespectsDegreeAndMemoryLimits()
    {
        var scheduler = CreateScheduler(
            maxDegreeOfParallelism: 8,
            memoryBudgetBytes: 16,
            queueCapacity: 3);
        var active = 0;
        var maximumActive = 0;
        var inputs = Enumerable.Range(0, 64).ToArray();

        var result = await scheduler.MapAsync(
            inputs,
            static _ => 4,
            async (value, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                ObserveMaximum(ref maximumActive, current);
                try
                {
                    await Task.Delay(2, cancellationToken);
                    return value * value;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(inputs.Select(static value => value * value), result.Results);
        Assert.InRange(result.Receipt.MaxObservedConcurrency, 1, 4);
        Assert.InRange(result.Receipt.PeakReservedMemoryBytes, 1, 16);
        Assert.Equal(3, result.Receipt.QueueCapacity);
    }

    [Fact]
    public async Task TileConstruction_UsesFrozenGlobalIndexes()
    {
        var index = new GenerationGlobalIndex<int, string>();
        for (var value = 0; value < 32; value++)
        {
            index.Add(value, $"node-{value}");
        }

        index.Freeze();
        var scheduler = CreateScheduler(8, 64, 4);
        var executor = new DeterministicGenerationStageExecutor(scheduler);
        var items = Enumerable.Range(0, 32).ToArray();

        var result = await executor.ExecuteAsync(
            index,
            items,
            static _ => 2,
            (item, _) =>
            {
                Assert.True(index.IsFrozen);
                Assert.True(index.TryGetValue(item, out var value));
                Assert.Equal($"node-{item}", value);
                Assert.Throws<InvalidOperationException>(
                    () => index.Add(item + 1000, "late mutation"));
                return ValueTask.FromResult(item * 2);
            },
            static (_, _) => 2,
            static (item, discovery, _, _) =>
                ValueTask.FromResult(item + discovery),
            TestContext.Current.CancellationToken);

        Assert.Equal(items.Select(static item => item * 3), result.Results);
    }

    [Fact]
    public async Task CrossTileStages_NeverPublishPartiallyDiscoveredState()
    {
        var index = new GenerationGlobalIndex<int, int>();
        index.Add(1, 1);
        index.Freeze();
        var scheduler = CreateScheduler(16, 128, 4);
        var executor = new DeterministicGenerationStageExecutor(scheduler);
        var items = Enumerable.Range(0, 128).ToArray();
        var discovered = 0;
        var writes = 0;

        var result = await executor.ExecuteAsync(
            index,
            items,
            static _ => 1,
            async (item, _) =>
            {
                await Task.Yield();
                Interlocked.Increment(ref discovered);
                return item + 1000;
            },
            static (_, _) => 1,
            (item, discovery, allDiscoveries, _) =>
            {
                Assert.Equal(items.Length, Volatile.Read(ref discovered));
                Assert.Equal(items.Length, allDiscoveries.Count);
                Interlocked.Increment(ref writes);
                return ValueTask.FromResult(item + discovery);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(items.Length, writes);
        Assert.Equal(
            items.Select(static item => (item * 2) + 1000),
            result.Results);
    }

    private static DeterministicGenerationScheduler CreateScheduler(
        int maxDegreeOfParallelism,
        long memoryBudgetBytes,
        int queueCapacity) =>
        new(
            new GenerationParallelExecutionOptions(
                maxDegreeOfParallelism,
                memoryBudgetBytes,
                queueCapacity));

    private static void ObserveMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (candidate <= observed ||
                Interlocked.CompareExchange(ref target, candidate, observed) == observed)
            {
                return;
            }
        }
    }
}

public sealed class DeterministicGenerationTests
{
    [Fact]
    public async Task ParallelBuild_ProducesStableTreeHash()
    {
        string? expectedHash = null;
        var hasher = new GenerationOutputTreeHasher();
        foreach (var degree in new[] { 1, 2, 4, 8, 16, 32 })
        {
            for (var run = 0; run < 5; run++)
            {
                var directory = CreateTempDirectory();
                try
                {
                    var index = new GenerationGlobalIndex<int, int>();
                    var items = Enumerable.Range(0, 64).Reverse().ToArray();
                    foreach (var item in items)
                    {
                        index.Add(item, item * 17);
                    }

                    index.Freeze();
                    var scheduler = new DeterministicGenerationScheduler(
                        new GenerationParallelExecutionOptions(
                            degree,
                            MemoryBudgetBytes: 256,
                            QueueCapacity: 5));
                    var executor = new DeterministicGenerationStageExecutor(scheduler);
                    var result = await executor.ExecuteAsync(
                        index,
                        items,
                        static _ => 2,
                        async (item, _) =>
                        {
                            await Task.Yield();
                            return item * 31;
                        },
                        static (_, _) => 2,
                        static (item, discovery, _, _) =>
                        {
                            var bytes = new byte[12];
                            BinaryPrimitives.WriteInt32LittleEndian(bytes, item);
                            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), discovery);
                            BinaryPrimitives.WriteInt32LittleEndian(
                                bytes.AsSpan(8),
                                item ^ discovery);
                            return ValueTask.FromResult(new TileOutput(item, bytes));
                        },
                        TestContext.Current.CancellationToken);

                    foreach (var output in result.Results.OrderBy(static value => value.TileId))
                    {
                        await File.WriteAllBytesAsync(
                            Path.Combine(directory, $"{output.TileId:D8}.gph"),
                            output.Bytes,
                            TestContext.Current.CancellationToken);
                    }

                    var hash = await hasher.ComputeSha256Async(
                        directory,
                        TestContext.Current.CancellationToken);
                    expectedHash ??= hash;
                    Assert.Equal(expectedHash, hash);
                }
                finally
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-parallel-determinism",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed record TileOutput(int TileId, byte[] Bytes);
}
