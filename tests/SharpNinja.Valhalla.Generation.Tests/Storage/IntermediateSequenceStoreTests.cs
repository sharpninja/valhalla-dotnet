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

    private readonly record struct TestRecord(long Key, int InputOrdinal, int Value);
}
