using SharpNinja.Valhalla.Generation.Storage;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Storage;

public sealed class ExternalSequenceSortTests
{
    [Fact]
    public async Task SpilledInput_SortsStablyWithinMemoryBudget()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var input = new IntermediateSequenceStore<TestRecord>(
                new IntermediateSequenceStoreOptions(
                    directory,
                    "input",
                    IntermediateStorageMode.MemoryMapped,
                    MemoryBudgetBytes: 64,
                    ScratchDiskBudgetBytes: 4096,
                    SegmentSizeBytes: 64));
            var records = new[]
            {
                new TestRecord(8, 0, 100),
                new TestRecord(3, 1, 200),
                new TestRecord(8, 2, 300),
                new TestRecord(1, 3, 400),
                new TestRecord(3, 4, 500),
                new TestRecord(1, 5, 600),
                new TestRecord(8, 6, 700),
            };
            foreach (var record in records)
            {
                input.Append(record);
            }

            await input.CompleteAsync(TestContext.Current.CancellationToken);
            const long sortMemoryBudget = 48;
            using var result = await ExternalSequenceSorter.SortAsync(
                input,
                new IntermediateSequenceStoreOptions(
                    directory,
                    "output",
                    IntermediateStorageMode.MemoryMapped,
                    MemoryBudgetBytes: 64,
                    ScratchDiskBudgetBytes: 4096,
                    SegmentSizeBytes: 64),
                new ExternalSequenceSortOptions(
                    directory,
                    "stable-sort",
                    sortMemoryBudget,
                    ScratchDiskBudgetBytes: 4096,
                    MaxMergeFanIn: 4),
                static (left, right) => left.Key.CompareTo(right.Key),
                TestContext.Current.CancellationToken);

            var actual = Enumerable.Range(0, records.Length)
                .Select(index => result.Output.Read(index))
                .ToArray();
            var expected = records
                .OrderBy(static record => record.Key)
                .ToArray();

            Assert.Equal(expected, actual);
            Assert.True(result.Receipt.InitialRunCount > 1);
            Assert.InRange(result.Receipt.PeakMemoryBytes, 1, sortMemoryBudget);
            Assert.InRange(result.Receipt.ScratchHighWaterMarkBytes, 1, 4096);
            Assert.Equal(records.Length, result.Receipt.OutputManifest.RecordCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScratchBudgetExhaustion_FailsBeforeOutputPublication()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var input = new IntermediateSequenceStore<TestRecord>(
                new IntermediateSequenceStoreOptions(
                    directory,
                    "input",
                    IntermediateStorageMode.Memory,
                    MemoryBudgetBytes: 4096,
                    ScratchDiskBudgetBytes: 4096,
                    SegmentSizeBytes: 64));
            for (var index = 0; index < 16; index++)
            {
                input.Append(new TestRecord(16 - index, index, index));
            }

            await input.CompleteAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                async () => await ExternalSequenceSorter.SortAsync(
                    input,
                    new IntermediateSequenceStoreOptions(
                        directory,
                        "output",
                        IntermediateStorageMode.MemoryMapped,
                        MemoryBudgetBytes: 64,
                        ScratchDiskBudgetBytes: 4096,
                        SegmentSizeBytes: 64),
                    new ExternalSequenceSortOptions(
                        directory,
                        "budget-sort",
                        MemoryBudgetBytes: 48,
                        ScratchDiskBudgetBytes: 32,
                        MaxMergeFanIn: 4),
                    static (left, right) => left.Key.CompareTo(right.Key),
                    TestContext.Current.CancellationToken));

            Assert.False(
                File.Exists(Path.Combine(directory, "output", "manifest.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ComparisonFailure_RemovesEveryIntermediateRunAndDoesNotPublishOutput()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var input = new IntermediateSequenceStore<TestRecord>(
                new IntermediateSequenceStoreOptions(
                    directory,
                    "input",
                    IntermediateStorageMode.Memory,
                    MemoryBudgetBytes: 4096,
                    ScratchDiskBudgetBytes: 4096,
                    SegmentSizeBytes: 64));
            for (var index = 0; index < 8; index++)
            {
                input.Append(new TestRecord(8 - index, index, index));
            }

            await input.CompleteAsync(TestContext.Current.CancellationToken);
            var comparisonCount = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await ExternalSequenceSorter.SortAsync(
                    input,
                    new IntermediateSequenceStoreOptions(
                        directory,
                        "output",
                        IntermediateStorageMode.MemoryMapped,
                        MemoryBudgetBytes: 64,
                        ScratchDiskBudgetBytes: 4096,
                        SegmentSizeBytes: 64),
                    new ExternalSequenceSortOptions(
                        directory,
                        "failed-sort",
                        MemoryBudgetBytes: 24,
                        ScratchDiskBudgetBytes: 4096,
                        MaxMergeFanIn: 2),
                    (left, right) =>
                    {
                        if (Interlocked.Increment(ref comparisonCount) == 2)
                        {
                            throw new InvalidOperationException("Injected comparison failure.");
                        }

                        return left.Key.CompareTo(right.Key);
                    },
                    TestContext.Current.CancellationToken));

            Assert.Empty(
                Directory.EnumerateFiles(
                    Path.Combine(directory, "failed-sort"),
                    "*.run",
                    SearchOption.TopDirectoryOnly));
            Assert.False(
                File.Exists(Path.Combine(directory, "output", "manifest.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-sort-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private readonly record struct TestRecord(long Key, int InputOrdinal, int Value);
}
