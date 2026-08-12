using System.Buffers;
using Microsoft.Win32.SafeHandles;
using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Generation.IO;

public sealed record GenerationGraphTileReaderOptions(long MaxCachedBytes);

public sealed record GenerationGraphTileHeaderReadResult(
    GraphTileHeader Header,
    long TileLength,
    int BytesRead);

public sealed class GenerationGraphTileReader : IAsyncDisposable
{
    private readonly GenerationGraphTileReaderOptions options;
    private readonly object sync = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> leastRecentlyUsed = [];
    private long cachedBytes;
    private long totalBytesRead;
    private int activeLeaseCount;
    private bool disposed;

    public GenerationGraphTileReader(GenerationGraphTileReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxCachedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The graph-tile cache budget must be positive.");
        }

        this.options = options;
    }

    public long CachedBytes => Interlocked.Read(ref cachedBytes);

    public int ActiveLeaseCount => Volatile.Read(ref activeLeaseCount);

    public long TotalBytesRead => Interlocked.Read(ref totalBytesRead);

    public async ValueTask<GenerationGraphTileHeaderReadResult> ReadHeaderAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = NormalizePath(path);
        ThrowIfDisposed();

        using SafeFileHandle handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        long tileLength = RandomAccess.GetLength(handle);
        if (tileLength < GraphTileHeader.HeaderSize)
        {
            throw new InvalidDataException(
                $"Graph tile '{fullPath}' is shorter than its {GraphTileHeader.HeaderSize}-byte header.");
        }

        byte[] headerBytes = GC.AllocateUninitializedArray<byte>(
            GraphTileHeader.HeaderSize);
        int bytesRead = await ReadExactlyAsync(
                handle,
                headerBytes,
                fileOffset: 0,
                cancellationToken)
            .ConfigureAwait(false);
        var header = GraphTileHeader.FromBytes(headerBytes);
        ValidateHeaderLength(fullPath, header, tileLength);
        return new GenerationGraphTileHeaderReadResult(
            header,
            tileLength,
            bytesRead);
    }

    public async ValueTask<GenerationGraphTileLease> AcquireAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = NormalizePath(path);
        lock (sync)
        {
            ThrowIfDisposed();
            if (cache.TryGetValue(fullPath, out CacheEntry? cached))
            {
                return CreateLease(cached);
            }
        }

        using SafeFileHandle handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        long fileLength = RandomAccess.GetLength(handle);
        if (fileLength < GraphTileHeader.HeaderSize || fileLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Graph tile '{fullPath}' has unsupported length {fileLength}.");
        }

        int tileLength = checked((int)fileLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(tileLength);
        try
        {
            await ReadExactlyAsync(
                    handle,
                    buffer.AsMemory(0, tileLength),
                    fileOffset: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var header = GraphTileHeader.FromBytes(
                buffer.AsSpan(0, GraphTileHeader.HeaderSize));
            ValidateHeaderLength(fullPath, header, tileLength);

            lock (sync)
            {
                ThrowIfDisposed();
                if (cache.TryGetValue(fullPath, out CacheEntry? racedEntry))
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = [];
                    return CreateLease(racedEntry);
                }

                var entry = new CacheEntry(fullPath, buffer, tileLength);
                buffer = [];
                if (entry.RetainedBytes <= options.MaxCachedBytes &&
                    MakeRoomFor(entry.RetainedBytes))
                {
                    entry.IsCached = true;
                    entry.LruNode = leastRecentlyUsed.AddFirst(fullPath);
                    cache.Add(fullPath, entry);
                    Interlocked.Add(ref cachedBytes, entry.RetainedBytes);
                }

                return CreateLease(entry);
            }
        }
        catch
        {
            if (buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            disposed = true;
            foreach (CacheEntry entry in cache.Values)
            {
                entry.IsCached = false;
                entry.LruNode = null;
                Interlocked.Add(ref cachedBytes, -entry.RetainedBytes);
                if (entry.ReferenceCount == 0)
                {
                    ReturnBuffer(entry);
                }
            }

            cache.Clear();
            leastRecentlyUsed.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private GenerationGraphTileLease CreateLease(CacheEntry entry)
    {
        checked
        {
            entry.ReferenceCount++;
        }

        Interlocked.Increment(ref activeLeaseCount);
        Touch(entry);
        return new GenerationGraphTileLease(
            entry.Path,
            entry.Buffer.AsMemory(0, entry.Length),
            () => Release(entry));
    }

    private bool MakeRoomFor(long requiredBytes)
    {
        while (CachedBytes > options.MaxCachedBytes - requiredBytes)
        {
            LinkedListNode<string>? candidateNode = leastRecentlyUsed.Last;
            while (candidateNode is not null &&
                   cache[candidateNode.Value].ReferenceCount != 0)
            {
                candidateNode = candidateNode.Previous;
            }

            if (candidateNode is null)
            {
                return false;
            }

            CacheEntry candidate = cache[candidateNode.Value];
            leastRecentlyUsed.Remove(candidateNode);
            cache.Remove(candidate.Path);
            candidate.IsCached = false;
            candidate.LruNode = null;
            Interlocked.Add(ref cachedBytes, -candidate.RetainedBytes);
            ReturnBuffer(candidate);
        }

        return true;
    }

    private void Release(CacheEntry entry)
    {
        lock (sync)
        {
            if (entry.ReferenceCount <= 0)
            {
                throw new InvalidOperationException(
                    "Graph-tile lease reference count became invalid.");
            }

            entry.ReferenceCount--;
            Interlocked.Decrement(ref activeLeaseCount);
            if (entry.ReferenceCount == 0 && !entry.IsCached)
            {
                ReturnBuffer(entry);
            }
        }
    }

    private void Touch(CacheEntry entry)
    {
        if (!entry.IsCached || entry.LruNode is null ||
            ReferenceEquals(entry.LruNode, leastRecentlyUsed.First))
        {
            return;
        }

        leastRecentlyUsed.Remove(entry.LruNode);
        entry.LruNode = leastRecentlyUsed.AddFirst(entry.Path);
    }

    private static void ReturnBuffer(CacheEntry entry)
    {
        if (entry.BufferReturned)
        {
            return;
        }

        entry.BufferReturned = true;
        ArrayPool<byte>.Shared.Return(entry.Buffer);
    }

    private async ValueTask<int> ReadExactlyAsync(
        SafeFileHandle handle,
        Memory<byte> destination,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await RandomAccess.ReadAsync(
                    handle,
                    destination[totalRead..],
                    fileOffset + totalRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Graph-tile read ended after {totalRead} of {destination.Length} bytes.");
            }

            totalRead = checked(totalRead + read);
            Interlocked.Add(ref totalBytesRead, read);
        }

        return totalRead;
    }

    private static void ValidateHeaderLength(
        string path,
        GraphTileHeader header,
        long tileLength)
    {
        if (header.EndOffset() != tileLength)
        {
            throw new InvalidDataException(
                $"Graph tile '{path}' declares {header.EndOffset()} bytes but contains {tileLength} bytes.");
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class CacheEntry
    {
        public CacheEntry(string path, byte[] buffer, int length)
        {
            Path = path;
            Buffer = buffer;
            Length = length;
        }

        public string Path { get; }

        public byte[] Buffer { get; }

        public int Length { get; }

        public long RetainedBytes => Buffer.LongLength;

        public int ReferenceCount { get; set; }

        public bool IsCached { get; set; }

        public bool BufferReturned { get; set; }

        public LinkedListNode<string>? LruNode { get; set; }
    }
}

public sealed class GenerationGraphTileLease : IAsyncDisposable
{
    private ReadOnlyMemory<byte> memory;
    private Action? release;

    internal GenerationGraphTileLease(
        string path,
        ReadOnlyMemory<byte> memory,
        Action release)
    {
        Path = path;
        this.memory = memory;
        this.release = release;
    }

    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return memory;
        }
    }

    public string Path { get; }

    public bool IsDisposed => Volatile.Read(ref release) is null;

    public ValueTask DisposeAsync()
    {
        Action? callback = Interlocked.Exchange(ref release, null);
        if (callback is not null)
        {
            memory = ReadOnlyMemory<byte>.Empty;
            callback();
        }

        return ValueTask.CompletedTask;
    }
}
