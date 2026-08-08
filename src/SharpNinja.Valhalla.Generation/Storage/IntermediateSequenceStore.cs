using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharpNinja.Valhalla.Generation.Storage;

public sealed record IntermediateSequenceStoreOptions(
    string WorkingDirectory,
    string StoreName,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

public sealed record IntermediateSequenceStoreState(
    IntermediateStorageMode ActiveStorageMode,
    long RecordCount,
    long CurrentMemoryBytes,
    long PeakMemoryBytes,
    long CurrentScratchBytes,
    long ScratchHighWaterMarkBytes,
    bool IsComplete);

public sealed record IntermediateSequenceSegmentReceipt(
    int SegmentOrdinal,
    string FileName,
    long RecordCount,
    long ByteLength,
    string Sha256);

public sealed record IntermediateSequenceManifest(
    int SchemaVersion,
    string StoreName,
    int RecordSize,
    long RecordCount,
    IntermediateStorageMode StorageMode,
    string ContentSha256,
    IReadOnlyList<IntermediateSequenceSegmentReceipt> Segments,
    string ManifestPath,
    string ManifestSha256);

public interface IIntermediateSequenceStore<T> : IDisposable
    where T : unmanaged
{
    IntermediateSequenceStoreState State { get; }

    void Append(T value);

    T Read(long index);

    ValueTask<IntermediateSequenceManifest> CompleteAsync(
        CancellationToken cancellationToken = default);
}

public sealed class IntermediateSequenceStore<T> : IIntermediateSequenceStore<T>
    where T : unmanaged
{
    private const int ManifestSchemaVersion = 1;
    private static readonly int RecordSize = Unsafe.SizeOf<T>();
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IntermediateSequenceStoreOptions options;
    private readonly string storeDirectory;
    private readonly int recordsPerSegment;
    private readonly List<Segment> segments = [];
    private BoundedMemoryBuffer? memoryRecords;
    private IntermediateStorageMode activeStorageMode;
    private long recordCount;
    private long currentMemoryBytes;
    private long peakMemoryBytes;
    private long currentScratchBytes;
    private long scratchHighWaterMarkBytes;
    private bool complete;
    private bool disposed;
    private IntermediateSequenceManifest? completedManifest;

    public IntermediateSequenceStore(IntermediateSequenceStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        this.options = options;
        storeDirectory = Path.GetFullPath(
            Path.Combine(options.WorkingDirectory, options.StoreName));
        Directory.CreateDirectory(storeDirectory);
        recordsPerSegment = Math.Max(1, options.SegmentSizeBytes / RecordSize);
        activeStorageMode = options.StorageMode == IntermediateStorageMode.MemoryMapped
            ? IntermediateStorageMode.MemoryMapped
            : IntermediateStorageMode.Memory;
        if (activeStorageMode == IntermediateStorageMode.Memory)
        {
            var maximumCapacity = checked((int)Math.Clamp(
                options.MemoryBudgetBytes / RecordSize,
                1,
                Math.Max(1, Array.MaxLength / RecordSize)));
            var initialCapacity = Math.Min(maximumCapacity, 1024);
            memoryRecords = new BoundedMemoryBuffer(
                initialCapacity,
                maximumCapacity);
        }
    }

    public IntermediateSequenceStoreState State
    {
        get
        {
            ThrowIfDisposed();
            return new IntermediateSequenceStoreState(
                activeStorageMode,
                recordCount,
                currentMemoryBytes,
                peakMemoryBytes,
                currentScratchBytes,
                scratchHighWaterMarkBytes,
                complete);
        }
    }

    public void Append(T value)
    {
        ThrowIfDisposed();
        if (complete)
        {
            throw new InvalidOperationException("A completed intermediate store is immutable.");
        }

        if (activeStorageMode == IntermediateStorageMode.Memory)
        {
            var requiredMemory = checked((recordCount + 1) * RecordSize);
            if (requiredMemory <= options.MemoryBudgetBytes)
            {
                memoryRecords!.Add(value);
                recordCount++;
                currentMemoryBytes = checked((long)memoryRecords.Capacity * RecordSize);
                peakMemoryBytes = Math.Max(peakMemoryBytes, currentMemoryBytes);
                return;
            }

            if (options.StorageMode == IntermediateStorageMode.Memory)
            {
                throw new ValhallaGenerationResourceLimitException(
                    $"Intermediate memory budget of {options.MemoryBudgetBytes} bytes would be exceeded.");
            }

            var requiredScratch = checked((recordCount + 1) * RecordSize);
            EnsureScratchAvailable(requiredScratch - currentScratchBytes);
            SpillMemoryToSegments();
        }

        AppendToSegment(value);
        recordCount++;
    }

    public T Read(long index)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, recordCount);

        if (activeStorageMode == IntermediateStorageMode.Memory)
        {
            return memoryRecords![checked((int)index)];
        }

        var segmentOrdinal = checked((int)(index / recordsPerSegment));
        var segmentIndex = checked((int)(index % recordsPerSegment));
        return segments[segmentOrdinal].Read(segmentIndex);
    }

    public async ValueTask<IntermediateSequenceManifest> CompleteAsync(
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
            ? HashMemoryRecords()
            : await HashSegmentsAsync(cancellationToken).ConfigureAwait(false);

        var payload = new IntermediateSequenceManifestPayload(
            ManifestSchemaVersion,
            options.StoreName,
            RecordSize,
            recordCount,
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
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
        foreach (var segment in segments)
        {
            segment.SealForMappedReads();
        }

        complete = true;
        completedManifest = new IntermediateSequenceManifest(
            ManifestSchemaVersion,
            options.StoreName,
            RecordSize,
            recordCount,
            activeStorageMode,
            contentSha256,
            receipts,
            manifestPath,
            manifestSha256);
        return completedManifest;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        memoryRecords?.Dispose();
        memoryRecords = null;
        foreach (var segment in segments)
        {
            segment.Dispose();
        }

        disposed = true;
    }

    private void SpillMemoryToSegments()
    {
        var records = memoryRecords!.AsSpan();
        foreach (var record in records)
        {
            AppendToSegment(record);
        }

        memoryRecords.Dispose();
        memoryRecords = null;
        currentMemoryBytes = 0;
        activeStorageMode = IntermediateStorageMode.MemoryMapped;
    }

    private void AppendToSegment(T value)
    {
        EnsureScratchAvailable(RecordSize);
        var segment = segments.Count == 0 ||
            segments[^1].RecordCount >= recordsPerSegment
            ? CreateSegment()
            : segments[^1];
        segment.Append(value);
        currentScratchBytes = checked(currentScratchBytes + RecordSize);
        scratchHighWaterMarkBytes = Math.Max(
            scratchHighWaterMarkBytes,
            currentScratchBytes);
    }

    private Segment CreateSegment()
    {
        var ordinal = segments.Count;
        var path = Path.Combine(
            storeDirectory,
            $"{ordinal:D8}.sequence");
        var segment = new Segment(ordinal, path, RecordSize);
        segments.Add(segment);
        return segment;
    }

    private void EnsureScratchAvailable(long additionalBytes)
    {
        if (additionalBytes < 0 ||
            additionalBytes > options.ScratchDiskBudgetBytes - currentScratchBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Intermediate scratch budget of {options.ScratchDiskBudgetBytes} bytes would be exceeded.");
        }
    }

    private (string Sha256, IReadOnlyList<IntermediateSequenceSegmentReceipt> Receipts)
        HashMemoryRecords()
    {
        var bytes = MemoryMarshal.AsBytes(memoryRecords!.AsSpan());
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        IReadOnlyList<IntermediateSequenceSegmentReceipt> receipts =
        [
            new(
                0,
                "memory",
                recordCount,
                bytes.Length,
                hash),
        ];
        return (hash, receipts);
    }

    private async ValueTask<(
        string Sha256,
        IReadOnlyList<IntermediateSequenceSegmentReceipt> Receipts)>
        HashSegmentsAsync(CancellationToken cancellationToken)
    {
        using var totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var receipts = new List<IntermediateSequenceSegmentReceipt>(segments.Count);
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

                    var bytes = buffer.AsSpan(0, read);
                    segmentHash.AppendData(bytes);
                    totalHash.AppendData(bytes);
                }

                receipts.Add(new IntermediateSequenceSegmentReceipt(
                    segment.Ordinal,
                    Path.GetFileName(segment.Path),
                    segment.RecordCount,
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

    private static void ValidateOptions(IntermediateSequenceStoreOptions options)
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

        if (options.MemoryBudgetBytes < RecordSize ||
            options.ScratchDiskBudgetBytes < RecordSize ||
            options.SegmentSizeBytes < RecordSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Memory, scratch, and segment bounds must hold at least one record.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class Segment : IDisposable
    {
        private readonly int recordSize;
        private readonly FileStream stream;
        private MemoryMappedFile? mappedFile;
        private MemoryMappedViewAccessor? view;
        private bool disposed;

        public Segment(int ordinal, string path, int recordSize)
        {
            Ordinal = ordinal;
            Path = path;
            this.recordSize = recordSize;
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

        public long RecordCount { get; private set; }

        public long ByteLength => checked(RecordCount * recordSize);

        public void Append(T value)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Span<T> record = stackalloc T[1];
            record[0] = value;
            stream.Write(MemoryMarshal.AsBytes(record));
            RecordCount++;
        }

        public T Read(int index)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                (long)index,
                RecordCount);
            var offset = checked((long)index * recordSize);
            if (view is not null)
            {
                view.Read(offset, out T value);
                return value;
            }

            Span<byte> bytes = stackalloc byte[recordSize];
            var read = RandomAccess.Read(stream.SafeFileHandle, bytes, offset);
            if (read != recordSize)
            {
                throw new EndOfStreamException(
                    "Intermediate sequence record is truncated.");
            }

            return MemoryMarshal.Read<T>(bytes);
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

    private sealed class BoundedMemoryBuffer : IDisposable
    {
        private readonly int initialCapacity;
        private readonly int maximumCapacity;
        private T[] buffer = [];
        private bool disposed;

        public BoundedMemoryBuffer(int initialCapacity, int maximumCapacity)
        {
            if (initialCapacity <= 0 || maximumCapacity < initialCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            }

            this.initialCapacity = initialCapacity;
            this.maximumCapacity = maximumCapacity;
        }

        public int Capacity => buffer.Length;

        public int Count { get; private set; }

        public void Add(T value)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureCapacity(checked(Count + 1));
            buffer[Count++] = value;
        }

        public ReadOnlySpan<T> AsSpan()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return buffer.AsSpan(0, Count);
        }

        public T this[int index]
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                return buffer[index];
            }
        }

        public void Dispose()
        {
            buffer = [];
            Count = 0;
            disposed = true;
        }

        private void EnsureCapacity(int required)
        {
            if (required <= buffer.Length)
            {
                return;
            }

            if (required > maximumCapacity)
            {
                throw new ValhallaGenerationResourceLimitException(
                    "The bounded intermediate memory capacity would be exceeded.");
            }

            var doubled = buffer.Length == 0
                ? initialCapacity
                : Math.Min(maximumCapacity, checked(buffer.Length * 2));
            var capacity = Math.Max(required, doubled);
            var replacement = new T[capacity];
            buffer.AsSpan(0, Count).CopyTo(replacement);
            buffer = replacement;
        }
    }

    private sealed record IntermediateSequenceManifestPayload(
        int SchemaVersion,
        string StoreName,
        int RecordSize,
        long RecordCount,
        IntermediateStorageMode StorageMode,
        string ContentSha256,
        IReadOnlyList<IntermediateSequenceSegmentReceipt> Segments);
}
