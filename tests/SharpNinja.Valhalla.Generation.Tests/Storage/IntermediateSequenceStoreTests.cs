using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Generation.Storage;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Storage;

public sealed class IntermediateSequenceStoreTests
{
    [Fact]
    public async Task MemoryAndMappedModes_PreserveIdenticalStableOrder()
    {
        var records = CreateRecords();
        var memory = await RoundTripAsync(
            IntermediateStorageMode.Memory,
            records,
            TestContext.Current.CancellationToken);
        var mapped = await RoundTripAsync(
            IntermediateStorageMode.MemoryMapped,
            records,
            TestContext.Current.CancellationToken);

        Assert.Equal(records, memory.Records);
        Assert.Equal(records, mapped.Records);
        Assert.Equal(memory.Manifest.ContentSha256, mapped.Manifest.ContentSha256);
        Assert.Equal(memory.Manifest.RecordCount, mapped.Manifest.RecordCount);
    }

    [Theory]
    [InlineData(IntermediateStorageMode.Memory)]
    [InlineData(IntermediateStorageMode.MemoryMapped)]
    public async Task BatchedRead_CrossesSegmentsWithoutChangingStableOrder(
        IntermediateStorageMode mode)
    {
        TestRecord[] records = CreateRecords();
        string directory = CreateTempDirectory();
        try
        {
            using var store = new IntermediateSequenceStore<TestRecord>(
                new IntermediateSequenceStoreOptions(
                    directory,
                    "batched-read",
                    mode,
                    MemoryBudgetBytes: 1024,
                    ScratchDiskBudgetBytes: 4096,
                    SegmentSizeBytes: 32));
            foreach (TestRecord record in records)
            {
                store.Append(record);
            }

            await store.CompleteAsync(TestContext.Current.CancellationToken);
            TestRecord sentinel = new(-1, -1, -1);
            TestRecord[] destination = Enumerable.Repeat(sentinel, 6).ToArray();

            store.ReadRange(1, destination, 1, 4);

            Assert.Equal(sentinel, destination[0]);
            Assert.Equal(records[1..5], destination[1..5]);
            Assert.Equal(sentinel, destination[5]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BatchedRead_PackedRecordsMatchScalarMappedReads()
    {
        PackedRecord[] records = Enumerable.Range(0, 5000)
            .Select(index => new PackedRecord(
                index % 3,
                index * 17L,
                index,
                checked((ulong)(index * 31L)),
                index + 0.125,
                -index - 0.875,
                index * 43L,
                index % 997))
            .ToArray();
        string directory = CreateTempDirectory();
        try
        {
            using var store = new IntermediateSequenceStore<PackedRecord>(
                new IntermediateSequenceStoreOptions(
                    directory,
                    "packed-batched-read",
                    IntermediateStorageMode.MemoryMapped,
                    MemoryBudgetBytes: 1024,
                    ScratchDiskBudgetBytes: 4 * 1024 * 1024,
                    SegmentSizeBytes: 64 * 1024));
            foreach (PackedRecord record in records)
            {
                store.Append(record);
            }

            await store.CompleteAsync(TestContext.Current.CancellationToken);
            PackedRecord[] scalar = Enumerable.Range(0, records.Length)
                .Select(index => store.Read(index))
                .ToArray();
            var batched = new PackedRecord[records.Length];

            store.ReadRange(0, batched, 0, batched.Length);

            Assert.Equal(records, scalar);
            Assert.Equal(records, batched);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutoMode_SpillsBeforeMemoryBudgetIsExceeded()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var store = new IntermediateSequenceStore<TestRecord>(
                new IntermediateSequenceStoreOptions(
                    directory,
                    "auto-spill",
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: 32,
                    ScratchDiskBudgetBytes: 1024,
                    SegmentSizeBytes: 64));

            foreach (var record in CreateRecords().Take(3))
            {
                store.Append(record);
            }

            var manifest = await store.CompleteAsync(TestContext.Current.CancellationToken);

            Assert.Equal(IntermediateStorageMode.MemoryMapped, store.State.ActiveStorageMode);
            Assert.True(store.State.PeakMemoryBytes <= 32);
            Assert.Equal(0, store.State.CurrentMemoryBytes);
            Assert.Equal(48, store.State.CurrentScratchBytes);
            Assert.Equal(3, manifest.RecordCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScratchBudgetExhaustion_DoesNotPartiallyAppendRecord()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var store = new IntermediateSequenceStore<TestRecord>(
                new IntermediateSequenceStoreOptions(
                    directory,
                    "scratch-limit",
                    IntermediateStorageMode.MemoryMapped,
                    MemoryBudgetBytes: 32,
                    ScratchDiskBudgetBytes: 32,
                    SegmentSizeBytes: 64));

            store.Append(new TestRecord(1, 0, 10));
            store.Append(new TestRecord(1, 1, 20));

            Assert.Throws<ValhallaGenerationResourceLimitException>(
                () => store.Append(new TestRecord(2, 2, 30)));
            Assert.Equal(2, store.State.RecordCount);
            Assert.Equal(32, store.State.CurrentScratchBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedManifest_IsIntegrityStampedAndDeterministic()
    {
        var records = CreateRecords();
        var first = await RoundTripAsync(
            IntermediateStorageMode.MemoryMapped,
            records,
            TestContext.Current.CancellationToken);
        var second = await RoundTripAsync(
            IntermediateStorageMode.MemoryMapped,
            records,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Manifest.ContentSha256, second.Manifest.ContentSha256);
        Assert.Equal(
            first.Manifest.Segments.Select(segment => segment.Sha256),
            second.Manifest.Segments.Select(segment => segment.Sha256));
        Assert.Equal(64, first.Manifest.ManifestSha256.Length);
    }

    private static async Task<(TestRecord[] Records, IntermediateSequenceManifest Manifest)> RoundTripAsync(
        IntermediateStorageMode mode,
        TestRecord[] records,
        CancellationToken cancellationToken)
    {
        var directory = CreateTempDirectory();
        try
        {
            using var store = new IntermediateSequenceStore<TestRecord>(
                new IntermediateSequenceStoreOptions(
                    directory,
                    "stable-order",
                    mode,
                    MemoryBudgetBytes: 1024,
                    ScratchDiskBudgetBytes: 4096,
                    SegmentSizeBytes: 64));
            foreach (var record in records)
            {
                store.Append(record);
            }

            var manifest = await store.CompleteAsync(cancellationToken);
            var actual = Enumerable.Range(0, records.Length)
                .Select(index => store.Read(index))
                .ToArray();
            Assert.True(File.Exists(manifest.ManifestPath));
            Assert.Equal(
                manifest.ManifestSha256,
                Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        await File.ReadAllBytesAsync(manifest.ManifestPath, cancellationToken))));

            return (actual, manifest);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TestRecord[] CreateRecords() =>
    [
        new(8, 0, 100),
        new(3, 1, 200),
        new(8, 2, 300),
        new(1, 3, 400),
        new(3, 4, 500),
    ];

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-sequence-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct PackedRecord(
        int FileOrdinal,
        long BlockOrdinal,
        int EntityOrdinal,
        ulong Id,
        double Latitude,
        double Longitude,
        long PayloadOffset,
        int PayloadLength);

    private readonly record struct TestRecord(long Key, int InputOrdinal, int Value);
}
