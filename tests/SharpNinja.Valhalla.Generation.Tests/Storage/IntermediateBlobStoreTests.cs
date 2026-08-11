using SharpNinja.Valhalla.Generation.Storage;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Storage;

public sealed class IntermediateBlobStoreTests
{
    [Fact]
    public async Task MemoryAndMappedModes_PreserveIdenticalOffsetReferences()
    {
        var payloads = new byte[][]
        {
            [1, 2, 3],
            [],
            Enumerable.Range(0, 29).Select(static value => (byte)value).ToArray(),
            [255, 254, 253, 252],
        };

        var memory = await BuildAsync(IntermediateStorageMode.Memory, payloads);
        var mapped = await BuildAsync(IntermediateStorageMode.MemoryMapped, payloads);

        Assert.Equal(memory.References, mapped.References);
        Assert.Equal(payloads, memory.Payloads);
        Assert.Equal(payloads, mapped.Payloads);
        Assert.Equal(memory.Manifest.ContentSha256, mapped.Manifest.ContentSha256);
    }

    [Theory]
    [InlineData(IntermediateStorageMode.Memory)]
    [InlineData(IntermediateStorageMode.MemoryMapped)]
    public async Task RangeRead_CrossesBlobAndSegmentBoundaries(
        IntermediateStorageMode mode)
    {
        byte[][] payloads = Enumerable.Range(0, 12)
            .Select(index => Enumerable
                .Range(0, 11 + index)
                .Select(value => checked((byte)(index + value)))
                .ToArray())
            .ToArray();
        string directory = CreateTempDirectory();
        try
        {
            using var store = new IntermediateBlobStore(
                CreateOptions(
                    directory,
                    mode,
                    memoryBudgetBytes: 1024,
                    scratchBudgetBytes: 4096,
                    segmentSizeBytes: 32));
            IntermediateBlobReference[] references = payloads
                .Select(payload => store.Append(payload))
                .ToArray();
            await store.CompleteAsync(TestContext.Current.CancellationToken);
            long offset = references[1].Offset;
            long end = references[^2].Offset + references[^2].Length;
            byte[] destination = GC.AllocateUninitializedArray<byte>(checked((int)(end - offset)));

            store.ReadRange(offset, destination);

            Assert.Equal(payloads[1..^1].SelectMany(static value => value), destination);
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
            using var store = new IntermediateBlobStore(
                CreateOptions(
                    directory,
                    IntermediateStorageMode.Auto,
                    memoryBudgetBytes: 12,
                    scratchBudgetBytes: 64));

            var first = store.Append(new byte[8]);
            var second = store.Append(new byte[8]);
            var manifest = await store.CompleteAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, first.Offset);
            Assert.Equal(8, second.Offset);
            Assert.Equal(IntermediateStorageMode.MemoryMapped, store.State.ActiveStorageMode);
            Assert.Equal(0, store.State.CurrentMemoryBytes);
            Assert.Equal(8, store.State.PeakMemoryBytes);
            Assert.Equal(16, store.State.ScratchHighWaterMarkBytes);
            Assert.Equal(IntermediateStorageMode.MemoryMapped, manifest.StorageMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MappedReads_KeepPageCacheWithinConfiguredLimit()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var store = new IntermediateBlobStore(
                CreateOptions(
                    directory,
                    IntermediateStorageMode.MemoryMapped,
                    memoryBudgetBytes: 16,
                    scratchBudgetBytes: 256,
                    segmentSizeBytes: 32,
                    readPageSizeBytes: 8,
                    maxCachedPages: 2));

            var references = Enumerable.Range(0, 8)
                .Select(index => store.Append(
                    Enumerable.Repeat((byte)index, 8).ToArray()))
                .ToArray();
            await store.CompleteAsync(TestContext.Current.CancellationToken);

            foreach (var reference in references.Reverse())
            {
                _ = store.Read(reference);
                Assert.InRange(store.State.CachedPageCount, 0, 2);
            }

            Assert.InRange(store.State.PeakCachedPageBytes, 0, 16);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScratchBudgetExhaustion_DoesNotPartiallyAppendBlob()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var store = new IntermediateBlobStore(
                CreateOptions(
                    directory,
                    IntermediateStorageMode.MemoryMapped,
                    memoryBudgetBytes: 8,
                    scratchBudgetBytes: 4));

            var accepted = store.Append([1, 2, 3, 4]);
            var before = store.State;
            Assert.Throws<ValhallaGenerationResourceLimitException>(
                () => store.Append([5]));

            Assert.Equal(new IntermediateBlobReference(0, 4), accepted);
            Assert.Equal(before, store.State);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, store.Read(accepted));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedManifest_IsIntegrityStampedAndDeterministic()
    {
        var payloads = new byte[][]
        {
            [10, 20],
            [30, 40, 50],
        };

        var first = await BuildAsync(IntermediateStorageMode.MemoryMapped, payloads);
        var second = await BuildAsync(IntermediateStorageMode.MemoryMapped, payloads);

        Assert.Equal(first.Manifest.ContentSha256, second.Manifest.ContentSha256);
        Assert.Equal(
            first.Manifest.Segments.Select(static receipt => receipt.Sha256),
            second.Manifest.Segments.Select(static receipt => receipt.Sha256));
        Assert.Equal(first.Manifest.ManifestSha256, first.ManifestFileSha256);
    }

    private static async Task<(
        IntermediateBlobReference[] References,
        byte[][] Payloads,
        IntermediateBlobManifest Manifest,
        string ManifestFileSha256)> BuildAsync(
        IntermediateStorageMode mode,
        byte[][] payloads)
    {
        var directory = CreateTempDirectory();
        var store = new IntermediateBlobStore(
            CreateOptions(
                directory,
                mode,
                memoryBudgetBytes: 1024,
                scratchBudgetBytes: 4096));
        try
        {
            var references = payloads.Select(payload => store.Append(payload)).ToArray();
            var manifest = await store.CompleteAsync(TestContext.Current.CancellationToken);
            var actual = references.Select(store.Read).ToArray();
            var manifestFileSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(
                        manifest.ManifestPath,
                        TestContext.Current.CancellationToken)));
            return (references, actual, manifest, manifestFileSha256);
        }
        finally
        {
            store.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IntermediateBlobStoreOptions CreateOptions(
        string workingDirectory,
        IntermediateStorageMode mode,
        long memoryBudgetBytes,
        long scratchBudgetBytes,
        int segmentSizeBytes = 32,
        int readPageSizeBytes = 8,
        int maxCachedPages = 2) =>
        new(
            workingDirectory,
            "blobs",
            mode,
            memoryBudgetBytes,
            scratchBudgetBytes,
            segmentSizeBytes,
            readPageSizeBytes,
            maxCachedPages);

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-blob-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
