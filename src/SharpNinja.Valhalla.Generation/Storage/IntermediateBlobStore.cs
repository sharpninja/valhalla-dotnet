using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharpNinja.Valhalla.Generation.Storage;

public sealed record IntermediateBlobStoreOptions(
    string WorkingDirectory,
    string StoreName,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024,
    int ReadPageSizeBytes = 64 * 1024,
    int MaxCachedPages = 8);

public readonly record struct IntermediateBlobReference(long Offset, int Length);

public sealed record IntermediateBlobStoreState(
    IntermediateStorageMode ActiveStorageMode,
    long BlobCount,
    long ByteLength,
    long CurrentMemoryBytes,
    long PeakMemoryBytes,
    long CurrentScratchBytes,
    long ScratchHighWaterMarkBytes,
    int CachedPageCount,
    long PeakCachedPageBytes,
    bool IsComplete);

public sealed record IntermediateBlobSegmentReceipt(
    int SegmentOrdinal,
    string FileName,
    long ByteLength,
    string Sha256);

public sealed record IntermediateBlobManifest(
    int SchemaVersion,
    string StoreName,
    long BlobCount,
    long ByteLength,
    IntermediateStorageMode StorageMode,
    string ContentSha256,
    IReadOnlyList<IntermediateBlobSegmentReceipt> Segments,
    string ManifestPath,
    string ManifestSha256);

public interface IIntermediateBlobStore : IDisposable
{
    IntermediateBlobStoreState State { get; }

    IntermediateBlobReference Append(ReadOnlySpan<byte> value);

    byte[] Read(IntermediateBlobReference reference);

    ValueTask<IntermediateBlobManifest> CompleteAsync(
        CancellationToken cancellationToken = default);
}

public sealed class IntermediateBlobStore : IIntermediateBlobStore
{
    private const int ManifestSchemaVersion = 1;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IntermediateBlobStoreOptions options;
    private readonly string storeDirectory;
    private readonly long maximumMemoryBytes;
    private readonly List<DiskSegment> segments = [];
    private readonly BoundedPageCache pageCache;
    private byte[] memory = [];
    private IntermediateStorageMode activeStorageMode;
    private long blobCount;
    private long byteLength;
    private long currentMemoryBytes;
    private long peakMemoryBytes;
    private long currentScratchBytes;
    private long scratchHighWaterMarkBytes;
    private bool complete;
    private bool disposed;
    private IntermediateBlobManifest? completedManifest;

    public IntermediateBlobStore(IntermediateBlobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        this.options = options;
        maximumMemoryBytes = Math.Min(options.MemoryBudgetBytes, Array.MaxLength);
        storeDirectory = Path.GetFullPath(
            Path.Combine(options.WorkingDirectory, options.StoreName));
        if (Directory.Exists(storeDirectory) &&
            Directory.EnumerateFileSystemEntries(storeDirectory).Any())
        {
            throw new InvalidOperationException(
                $"Intermediate blob store directory '{storeDirectory}' is not empty.");
        }

        Directory.CreateDirectory(storeDirectory);
        activeStorageMode = options.StorageMode == IntermediateStorageMode.MemoryMapped
            ? IntermediateStorageMode.MemoryMapped
            : IntermediateStorageMode.Memory;
        var budgetPageLimit = Math.Max(
            1L,
            options.MemoryBudgetBytes / options.ReadPageSizeBytes);
        var cachePageLimit = checked((int)Math.Min(
            options.MaxCachedPages,
            budgetPageLimit));
        pageCache = new BoundedPageCache(cachePageLimit);
    }

    public IntermediateBlobStoreState State
    {
        get
        {
            ThrowIfDisposed();
            return new IntermediateBlobStoreState(
                activeStorageMode,
                blobCount,
                byteLength,
                currentMemoryBytes,
                peakMemoryBytes,
                currentScratchBytes,
                scratchHighWaterMarkBytes,
                pageCache.Count,
                pageCache.PeakBytes,
                complete);
        }
    }

    public IntermediateBlobReference Append(ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        if (complete)
        {
            throw new InvalidOperationException("A completed intermediate blob store is immutable.");
        }

        var reference = new IntermediateBlobReference(byteLength, value.Length);
        if (value.IsEmpty)
        {
            blobCount++;
            return reference;
        }

        var requiredLength = checked(byteLength + value.Length);
        if (activeStorageMode == IntermediateStorageMode.Memory)
        {
            if (requiredLength <= maximumMemoryBytes)
            {
                EnsureMemoryCapacity(checked((int)requiredLength));
                value.CopyTo(memory.AsSpan(checked((int)byteLength)));
                byteLength = requiredLength;
                blobCount++;
                currentMemoryBytes = byteLength;
                peakMemoryBytes = Math.Max(peakMemoryBytes, currentMemoryBytes);
                return reference;
            }

            if (options.StorageMode == IntermediateStorageMode.Memory)
            {
                throw new ValhallaGenerationResourceLimitException(
                    $"Intermediate blob memory budget of {options.MemoryBudgetBytes} bytes would be exceeded.");
            }

            EnsureScratchAvailable(requiredLength - currentScratchBytes);
            SpillMemoryToSegments();
        }
        else
        {
            EnsureScratchAvailable(value.Length);
        }

        AppendToSegments(value);
        byteLength = requiredLength;
        blobCount++;
        return reference;
    }

    public byte[] Read(IntermediateBlobReference reference)
    {
        ThrowIfDisposed();
        ValidateReference(reference);
        if (reference.Length == 0)
        {
            return [];
        }

        var result = GC.AllocateUninitializedArray<byte>(reference.Length);
        if (activeStorageMode == IntermediateStorageMode.Memory)
        {
            memory.AsSpan(
                    checked((int)reference.Offset),
                    reference.Length)
                .CopyTo(result);
            return result;
        }

        if (!complete)
        {
            ReadDirect(reference.Offset, result);
            return result;
        }

        ReadThroughCache(reference.Offset, result);
        return result;
    }

    public async ValueTask<IntermediateBlobManifest> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (completedManifest is not null)
        {
            return completedManifest;
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var segment in segments)
        {
            await segment.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var (contentSha256, receipts) = activeStorageMode == IntermediateStorageMode.Memory
            ? HashMemory()
            : await HashSegmentsAsync(cancellationToken).ConfigureAwait(false);
        var payload = new IntermediateBlobManifestPayload(
            ManifestSchemaVersion,
            options.StoreName,
            blobCount,
            byteLength,
            activeStorageMode,
            contentSha256,
            receipts);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            ManifestJsonOptions);
        var manifestPath = Path.Combine(storeDirectory, "manifest.json");
        var temporaryPath = manifestPath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, manifestPath, overwrite: true);
        foreach (var segment in segments)
        {
            segment.SealForMappedReads();
        }

        complete = true;
        completedManifest = new IntermediateBlobManifest(
            ManifestSchemaVersion,
            options.StoreName,
            blobCount,
            byteLength,
            activeStorageMode,
            contentSha256,
            receipts,
            manifestPath,
            Convert.ToHexString(SHA256.HashData(manifestBytes)));
        return completedManifest;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        pageCache.Dispose();
        foreach (var segment in segments)
        {
            segment.Dispose();
        }

        memory = [];
        disposed = true;
    }

    private void EnsureMemoryCapacity(int requiredLength)
    {
        if (memory.Length >= requiredLength)
        {
            return;
        }

        var doubled = memory.Length == 0
            ? requiredLength
            : Math.Max(requiredLength, checked((long)memory.Length * 2));
        var capacity = checked((int)Math.Min(maximumMemoryBytes, doubled));
        Array.Resize(ref memory, capacity);
    }

    private void SpillMemoryToSegments()
    {
        if (byteLength > 0)
        {
            AppendToSegments(memory.AsSpan(0, checked((int)byteLength)));
        }

        memory = [];
        currentMemoryBytes = 0;
        activeStorageMode = IntermediateStorageMode.MemoryMapped;
    }

    private void AppendToSegments(ReadOnlySpan<byte> value)
    {
        var remaining = value;
        while (!remaining.IsEmpty)
        {
            var segment = segments.Count == 0 ||
                segments[^1].ByteLength >= options.SegmentSizeBytes
                ? CreateSegment()
                : segments[^1];
            var writable = checked((int)Math.Min(
                remaining.Length,
                options.SegmentSizeBytes - segment.ByteLength));
            segment.Append(remaining[..writable]);
            remaining = remaining[writable..];
            currentScratchBytes = checked(currentScratchBytes + writable);
            scratchHighWaterMarkBytes = Math.Max(
                scratchHighWaterMarkBytes,
                currentScratchBytes);
        }
    }

    private DiskSegment CreateSegment()
    {
        var ordinal = segments.Count;
        var segment = new DiskSegment(
            ordinal,
            Path.Combine(storeDirectory, $"{ordinal:D8}.blob"));
        segments.Add(segment);
        return segment;
    }

    private void ReadDirect(long offset, Span<byte> destination)
    {
        var remaining = destination;
        var currentOffset = offset;
        while (!remaining.IsEmpty)
        {
            var segmentOrdinal = checked((int)(currentOffset / options.SegmentSizeBytes));
            var segmentOffset = currentOffset % options.SegmentSizeBytes;
            var readable = checked((int)Math.Min(
                remaining.Length,
                segments[segmentOrdinal].ByteLength - segmentOffset));
            segments[segmentOrdinal].Read(segmentOffset, remaining[..readable]);
            currentOffset += readable;
            remaining = remaining[readable..];
        }
    }

    private void ReadThroughCache(long offset, Span<byte> destination)
    {
        var remaining = destination;
        var currentOffset = offset;
        while (!remaining.IsEmpty)
        {
            var segmentOrdinal = checked((int)(currentOffset / options.SegmentSizeBytes));
            var segmentOffset = currentOffset % options.SegmentSizeBytes;
            var pageOrdinal = segmentOffset / options.ReadPageSizeBytes;
            var pageOffset = checked((int)(segmentOffset % options.ReadPageSizeBytes));
            var key = new PageKey(segmentOrdinal, pageOrdinal);
            var page = pageCache.GetOrAdd(
                key,
                () => segments[segmentOrdinal].ReadPage(
                    checked(pageOrdinal * options.ReadPageSizeBytes),
                    options.ReadPageSizeBytes));
            var readable = Math.Min(remaining.Length, page.Length - pageOffset);
            if (readable <= 0)
            {
                throw new EndOfStreamException("Intermediate blob page is truncated.");
            }

            page.AsSpan(pageOffset, readable).CopyTo(remaining);
            currentOffset += readable;
            remaining = remaining[readable..];
        }
    }

    private void ValidateReference(IntermediateBlobReference reference)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reference.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(reference.Length);
        if (reference.Offset > byteLength ||
            reference.Length > byteLength - reference.Offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reference),
                "The blob reference is outside the stored byte range.");
        }
    }

    private void EnsureScratchAvailable(long additionalBytes)
    {
        if (additionalBytes < 0 ||
            additionalBytes > options.ScratchDiskBudgetBytes - currentScratchBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Intermediate blob scratch budget of {options.ScratchDiskBudgetBytes} bytes would be exceeded.");
        }
    }

    private (string Sha256, IReadOnlyList<IntermediateBlobSegmentReceipt> Receipts)
        HashMemory()
    {
        var bytes = memory.AsSpan(0, checked((int)byteLength));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        IReadOnlyList<IntermediateBlobSegmentReceipt> receipts =
        [
            new(0, "memory", byteLength, hash),
        ];
        return (hash, receipts);
    }

    private async ValueTask<(
        string Sha256,
        IReadOnlyList<IntermediateBlobSegmentReceipt> Receipts)>
        HashSegmentsAsync(CancellationToken cancellationToken)
    {
        using var totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var receipts = new List<IntermediateBlobSegmentReceipt>(segments.Count);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var segmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var input = new FileStream(
                    segment.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    segmentHash.AppendData(buffer.AsSpan(0, read));
                    totalHash.AppendData(buffer.AsSpan(0, read));
                }

                receipts.Add(new IntermediateBlobSegmentReceipt(
                    segment.Ordinal,
                    Path.GetFileName(segment.Path),
                    segment.ByteLength,
                    Convert.ToHexString(segmentHash.GetHashAndReset())));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (
            Convert.ToHexString(totalHash.GetHashAndReset()),
            receipts);
    }

    private static void ValidateOptions(IntermediateBlobStoreOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StoreName);
        if (!string.Equals(
                options.StoreName,
                Path.GetFileName(options.StoreName),
                StringComparison.Ordinal) ||
            options.StoreName is "." or "..")
        {
            throw new ArgumentException(
                "The store name must be a single safe path segment.",
                nameof(options));
        }

        if (options.MemoryBudgetBytes <= 0 ||
            options.ScratchDiskBudgetBytes <= 0 ||
            options.SegmentSizeBytes <= 0 ||
            options.ReadPageSizeBytes <= 0 ||
            options.MaxCachedPages <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Memory, scratch, segment, page, and cache bounds must be positive.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private readonly record struct PageKey(int SegmentOrdinal, long PageOrdinal);

    private sealed class BoundedPageCache : IDisposable
    {
        private readonly int maximumPages;
        private readonly Dictionary<PageKey, CacheEntry> entries = [];
        private readonly LinkedList<PageKey> recency = [];
        private long currentBytes;

        public BoundedPageCache(int maximumPages)
        {
            this.maximumPages = maximumPages;
        }

        public int Count => entries.Count;

        public long PeakBytes { get; private set; }

        public byte[] GetOrAdd(PageKey key, Func<byte[]> factory)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                recency.Remove(existing.Node);
                recency.AddFirst(existing.Node);
                return existing.Bytes;
            }

            var bytes = factory();
            while (entries.Count >= maximumPages)
            {
                var expiredNode = recency.Last!;
                var expired = entries[expiredNode.Value];
                currentBytes -= expired.Bytes.Length;
                entries.Remove(expiredNode.Value);
                recency.RemoveLast();
            }

            var node = recency.AddFirst(key);
            entries.Add(key, new CacheEntry(bytes, node));
            currentBytes += bytes.Length;
            PeakBytes = Math.Max(PeakBytes, currentBytes);
            return bytes;
        }

        public void Dispose()
        {
            entries.Clear();
            recency.Clear();
            currentBytes = 0;
        }

        private sealed record CacheEntry(
            byte[] Bytes,
            LinkedListNode<PageKey> Node);
    }

    private sealed class DiskSegment : IDisposable
    {
        private readonly FileStream stream;
        private MemoryMappedFile? mappedFile;
        private MemoryMappedViewAccessor? view;
        private bool disposed;

        public DiskSegment(int ordinal, string path)
        {
            Ordinal = ordinal;
            Path = path;
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess | FileOptions.WriteThrough);
        }

        public int Ordinal { get; }

        public string Path { get; }

        public long ByteLength { get; private set; }

        public void Append(ReadOnlySpan<byte> value)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stream.Write(value);
            ByteLength = checked(ByteLength + value.Length);
        }

        public void Read(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var read = RandomAccess.Read(stream.SafeFileHandle, destination, offset);
            if (read != destination.Length)
            {
                throw new EndOfStreamException("Intermediate blob segment is truncated.");
            }
        }

        public byte[] ReadPage(long offset, int requestedLength)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var length = checked((int)Math.Min(requestedLength, ByteLength - offset));
            var result = GC.AllocateUninitializedArray<byte>(length);
            var read = view is null
                ? RandomAccess.Read(stream.SafeFileHandle, result, offset)
                : view.ReadArray(offset, result, 0, length);
            if (read != length)
            {
                throw new EndOfStreamException("Intermediate blob page is truncated.");
            }

            return result;
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        public void SealForMappedReads()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (ByteLength == 0 || view is not null)
            {
                return;
            }

            mappedFile = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: ByteLength,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);
            view = mappedFile.CreateViewAccessor(
                0,
                ByteLength,
                MemoryMappedFileAccess.Read);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            view?.Dispose();
            mappedFile?.Dispose();
            stream.Dispose();
            disposed = true;
        }
    }

    private sealed record IntermediateBlobManifestPayload(
        int SchemaVersion,
        string StoreName,
        long BlobCount,
        long ByteLength,
        IntermediateStorageMode StorageMode,
        string ContentSha256,
        IReadOnlyList<IntermediateBlobSegmentReceipt> Segments);
}
