// Faithful C# port of Valhalla's memory-mapped sequence<T>.
// Source: valhalla/midgard/sequence.h (mem_map<T> + sequence<T>).
//
// The C++ sequence<T> requires POD ("trivially copyable") element types and stores
// them contiguously in a file that is memory-mapped for reading; new elements are
// appended through a write buffer and periodically flushed (and re-mapped). This is a
// faithful, readable port:
//   - element type is constrained to `unmanaged` (the C# analogue of POD)
//   - the file is memory-mapped via System.IO.MemoryMappedFiles and read through a Span
//   - push_back / flush / sort / find / find_first_of / transform / enumerate / front /
//     back / indexer / iterator semantics mirror the original
//
// OMITTED from sequence.h (not part of the engine-needed reader subset, or platform glue
// handled by the BCL): the `tar` archive reader, the POSIX/Win32 `mmap`/`munmap` shims,
// and `posix_madvise` advice hints (madvise is advisory and ignored on Windows anyway).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// A readable, memory-mapped, append-capable sequence of fixed-size <typeparamref name="T"/>
/// values backed by a file. Faithful port of <c>valhalla::midgard::sequence&lt;T&gt;</c>.
/// </summary>
/// <typeparam name="T">An unmanaged (POD/blittable) element type.</typeparam>
public sealed class Sequence<T> : IDisposable, IEnumerable<T>
    where T : unmanaged
{
    /// <summary>Sentinel "not found" index, mirroring C++ <c>sequence::npos</c>.</summary>
    public const long Npos = -1;

    private static readonly int ElementSize = Marshal.SizeOf<T>();

    private readonly string _fileName;
    private readonly List<T> _writeBuffer;
    private readonly int _writeBufferCapacity;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private long _mappedCount;
    private bool _disposed;

    /// <summary>
    /// Opens (or creates) a sequence backed by the file at <paramref name="fileName"/>.
    /// Mirrors the C++ constructor: when <paramref name="create"/> is true the file is
    /// truncated, otherwise the existing contents are opened for append.
    /// </summary>
    /// <param name="fileName">Backing file path.</param>
    /// <param name="create">If true, truncate/create; if false, open existing and append.</param>
    /// <param name="writeBufferSize">
    /// Number of elements buffered before an automatic flush (default mirrors the C++
    /// <c>1024*1024*32 / sizeof(T)</c> heuristic).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the existing file size is not a whole multiple of <c>sizeof(T)</c>.
    /// </exception>
    public Sequence(string fileName, bool create = false, long writeBufferSize = -1)
    {
        _fileName = fileName;
        _writeBufferCapacity = (int)(writeBufferSize >= 0
            ? writeBufferSize
            : (1024L * 1024L * 32L) / ElementSize);
        if (_writeBufferCapacity <= 0)
        {
            _writeBufferCapacity = 1;
        }

        _writeBuffer = new List<T>(_writeBufferCapacity);

        if (create)
        {
            // truncate / create
            using (File.Create(fileName))
            {
            }
        }
        else if (!File.Exists(fileName))
        {
            // ate mode on a nonexistent file: behave like an empty new file
            using (File.Create(fileName))
            {
            }
        }

        long end = new FileInfo(fileName).Length;
        long elementCount = end / ElementSize;
        if (end != elementCount * ElementSize)
        {
            throw new InvalidOperationException(
                $"sequence: {fileName} has an incorrect size for type");
        }

        // memory map the file for reading
        Map(elementCount);
    }

    /// <summary>How many elements have been written so far (mapped + buffered).</summary>
    public long Count => _mappedCount + _writeBuffer.Count;

    /// <summary>Backing file path.</summary>
    public string Name => _fileName;

    /// <summary>Adds an element to the sequence. Faithful port of <c>push_back</c>.</summary>
    public void PushBack(T obj)
    {
        _writeBuffer.Add(obj);

        // push it to the file once the buffer is full
        if (_writeBuffer.Count == _writeBufferCapacity)
        {
            Flush();
        }
    }

    /// <summary>
    /// Forces writing whatever is in the write buffer to the file and re-maps.
    /// Faithful port of <c>flush</c>.
    /// </summary>
    public void Flush()
    {
        if (_writeBuffer.Count == 0)
        {
            return;
        }

        long newCount = _mappedCount + _writeBuffer.Count;

        UnmapView();

        using (var fs = new FileStream(
                   _fileName,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            Span<T> span = CollectionsMarshal.AsSpan(_writeBuffer);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            fs.Write(bytes);
            fs.Flush(true);
        }

        _writeBuffer.Clear();
        Map(newCount);
    }

    /// <summary>Reads the element at <paramref name="index"/> (mapped or buffered).</summary>
    public T this[long index]
    {
        get => Read(index);
        set => Write(index, value);
    }

    /// <summary>Reads the first element. Faithful port of <c>front</c>.</summary>
    public T Front() => Read(0);

    /// <summary>Reads the last element. Faithful port of <c>back</c> (flushes first).</summary>
    public T Back()
    {
        Flush();
        return Read(_mappedCount - 1);
    }

    /// <summary>
    /// Finds the first matching object by scanning O(n). Assumes nothing about order.
    /// Faithful port of <c>find_first_of</c>; returns <see cref="Npos"/> if not found.
    /// </summary>
    /// <param name="target">Target value passed to the predicate.</param>
    /// <param name="predicate">Equality-style predicate (target, candidate) =&gt; bool.</param>
    /// <param name="startIndex">Index to start scanning from.</param>
    public long FindFirstOf(T target, Func<T, T, bool> predicate, long startIndex = 0)
    {
        Flush();
        while (startIndex < _mappedCount)
        {
            T candidate = Read(startIndex);
            if (predicate(target, candidate))
            {
                return startIndex;
            }

            ++startIndex;
        }

        return Npos;
    }

    /// <summary>
    /// Binary search for an object. Assumes the file was written in sorted order.
    /// Faithful port of <c>find</c>; returns <see cref="Count"/> (the end index) if not found.
    /// </summary>
    /// <param name="target">Target value.</param>
    /// <param name="comparer">Strict-weak-ordering "less than" comparer (a, b) =&gt; a &lt; b.</param>
    /// <returns>Index of the found element, or the end index (Count) if not present.</returns>
    public long Find(T target, Func<T, T, bool> comparer)
    {
        Flush();
        if (_mappedCount == 0)
        {
            return Count;
        }

        // std::lower_bound: first index whose element is not < target
        long lo = 0;
        long hi = _mappedCount;
        while (lo < hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (comparer(Read(mid), target))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (lo == _mappedCount)
        {
            return Count;
        }

        T found = Read(lo);

        // equality when !(target < found) && !(found < target)
        if (!(comparer(target, found) || comparer(found, target)))
        {
            return lo;
        }

        return Count;
    }

    /// <summary>
    /// Sorts the file based on the comparer, in place. Faithful port of <c>sort</c>: small
    /// inputs are sorted directly; larger inputs use an external merge sort over buffer-sized
    /// sub-ranges, exactly mirroring the C++ strategy.
    /// </summary>
    /// <param name="comparer">Strict-weak-ordering "less than" comparer (a, b) =&gt; a &lt; b.</param>
    /// <param name="bufferSize">Sub-range length (default mirrors the C++ 512 MiB heuristic).</param>
    public void Sort(Func<T, T, bool> comparer, long bufferSize = -1)
    {
        Flush();
        if (_mappedCount == 0)
        {
            return;
        }

        if (bufferSize < 0)
        {
            bufferSize = (1024L * 1024L * 512L) / ElementSize;
        }

        if (bufferSize <= 0)
        {
            bufferSize = 1;
        }

        var comparison = new Comparison<T>((a, b) => comparer(a, b) ? -1 : (comparer(b, a) ? 1 : 0));

        // If there wont be any merging, take the simple approach: sort the mapped region in place.
        if (bufferSize > _mappedCount)
        {
            T[] all = ReadAll();
            Array.Sort(all, comparison);
            WriteAll(all);
            return;
        }

        // External merge sort: sort sub-ranges in place, then k-way merge via a priority queue.
        string tmpPath = TempPath();
        using (var output = new Sequence<T>(tmpPath, create: true))
        {
            // Sort the subsections in place within the mapped file.
            for (long i = 0; i < _mappedCount; i += bufferSize)
            {
                long end = Math.Min(_mappedCount, i + bufferSize);
                int len = (int)(end - i);
                T[] chunk = ReadRange(i, len);
                Array.Sort(chunk, comparison);
                WriteRange(i, chunk);
            }

            // Min-priority queue keyed by element using the comparer; payload is the source index.
            var pq = new PriorityQueue<long, T>(new ComparerAdapter(comparer));
            for (long i = 0; i < _mappedCount; i += bufferSize)
            {
                pq.Enqueue(i, Read(i));
            }

            // Perform the merge.
            while (pq.Count > 0)
            {
                long idx = pq.Dequeue();
                output.PushBack(Read(idx));
                long newIdx = idx + 1;
                if (newIdx % bufferSize != 0 && newIdx < _mappedCount)
                {
                    pq.Enqueue(newIdx, Read(newIdx));
                }
            }

            output.Flush();
        }

        // Swap in the temp file.
        UnmapView();
        _mmf?.Dispose();
        _mmf = null;
        _accessor = null;

        File.Delete(_fileName);
        File.Move(tmpPath, _fileName);

        long reloadedCount = new FileInfo(_fileName).Length / ElementSize;
        Map(reloadedCount);
    }

    /// <summary>
    /// Applies a (potentially mutating) transform to every element and writes it back.
    /// Faithful port of <c>transform</c>.
    /// </summary>
    public void Transform(Func<T, T> transformer)
    {
        Flush();
        for (long i = 0; i < _mappedCount; ++i)
        {
            Write(i, transformer(Read(i)));
        }
    }

    /// <summary>
    /// Applies a read-only operation to every element. Faithful port of <c>enumerate</c>.
    /// </summary>
    public void Enumerate(Action<T> action)
    {
        Flush();
        for (long i = 0; i < _mappedCount; ++i)
        {
            action(Read(i));
        }
    }

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator()
    {
        long count = Count;
        for (long i = 0; i < count; ++i)
        {
            yield return Read(i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // finish writing whatever it was to file (mirrors the C++ destructor's flush())
        Flush();
        UnmapView();
        _mmf?.Dispose();
        _mmf = null;
        _disposed = true;
    }

    private T Read(long index)
    {
        // If index is beyond the end of the mmap buffer, then access the write buffer.
        if (index >= _mappedCount)
        {
            return _writeBuffer[(int)(index - _mappedCount)];
        }

        if (_accessor is null)
        {
            throw new InvalidOperationException("sequence: file is not mapped");
        }

        _accessor.Read(index * ElementSize, out T value);
        return value;
    }

    private void Write(long index, T value)
    {
        if (index >= _mappedCount)
        {
            _writeBuffer[(int)(index - _mappedCount)] = value;
            return;
        }

        if (_accessor is null)
        {
            throw new InvalidOperationException("sequence: file is not mapped");
        }

        _accessor.Write(index * ElementSize, ref value);
    }

    private T[] ReadAll() => ReadRange(0, (int)_mappedCount);

    private T[] ReadRange(long start, int length)
    {
        var result = new T[length];
        for (int j = 0; j < length; ++j)
        {
            result[j] = Read(start + j);
        }

        return result;
    }

    private void WriteAll(T[] values) => WriteRange(0, values);

    private void WriteRange(long start, T[] values)
    {
        for (int j = 0; j < values.Length; ++j)
        {
            Write(start + j, values[j]);
        }
    }

    private void Map(long count)
    {
        UnmapView();
        _mmf?.Dispose();
        _mmf = null;
        _accessor = null;
        _mappedCount = count;

        if (count <= 0)
        {
            return;
        }

        // MemoryMappedFile cannot map a zero-length region; count > 0 guarantees a size.
        _mmf = MemoryMappedFile.CreateFromFile(
            new FileStream(_fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite),
            mapName: null,
            capacity: count * ElementSize,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false);
        _accessor = _mmf.CreateViewAccessor(0, count * ElementSize, MemoryMappedFileAccess.ReadWrite);
    }

    private void UnmapView()
    {
        _accessor?.Flush();
        _accessor?.Dispose();
        _accessor = null;
    }

    private string TempPath()
    {
        string? dir = Path.GetDirectoryName(_fileName);
        string name = Path.GetFileName(_fileName) + ".tmp";
        return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
    }

    // Adapts a "less than" predicate to an IComparer<T> for the merge priority queue.
    private sealed class ComparerAdapter : IComparer<T>
    {
        private readonly Func<T, T, bool> _lessThan;

        public ComparerAdapter(Func<T, T, bool> lessThan) => _lessThan = lessThan;

        public int Compare(T x, T y)
        {
            if (_lessThan(x, y))
            {
                return -1;
            }

            if (_lessThan(y, x))
            {
                return 1;
            }

            return 0;
        }
    }
}
