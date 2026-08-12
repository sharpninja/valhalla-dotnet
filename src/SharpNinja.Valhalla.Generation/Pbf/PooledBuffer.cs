using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpNinja.Valhalla.Generation.Pbf;

internal sealed class PooledBuffer<T> : IDisposable
{
    private T[] buffer;
    private bool disposed;

    public PooledBuffer(int initialCapacity = 16)
    {
        if (initialCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        }

        buffer = ArrayPool<T>.Shared.Rent(initialCapacity);
    }

    public int Count { get; private set; }

    public void Add(T value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureCapacity(checked(Count + 1));
        buffer[Count++] = value;
    }

    public void AddRange(ReadOnlySpan<T> values)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureCapacity(checked(Count + values.Length));
        values.CopyTo(buffer.AsSpan(Count));
        Count += values.Length;
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            buffer.AsSpan(0, Count).Clear();
        }

        Count = 0;
    }

    public ReadOnlySpan<T> AsSpan()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return buffer.AsSpan(0, Count);
    }

    public Span<T> AsWritableSpan()
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
        if (disposed)
        {
            return;
        }

        ArrayPool<T>.Shared.Return(
            buffer,
            clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
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

        var newSize = Math.Max(required, checked(buffer.Length * 2));
        var replacement = ArrayPool<T>.Shared.Rent(newSize);
        buffer.AsSpan(0, Count).CopyTo(replacement);
        ArrayPool<T>.Shared.Return(
            buffer,
            clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        buffer = replacement;
    }
}
