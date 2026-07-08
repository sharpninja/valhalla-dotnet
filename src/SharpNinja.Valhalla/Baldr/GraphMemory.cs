// Faithful C# port of Valhalla baldr graphmemory.h (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/baldr/graphmemory.h
//
// A holder struct for memory owned by the GraphTile. In C++ this exposes a raw
// (char* data, size_t size) view over a tile blob (mmap'd file, decompressed
// buffer, etc.). The C# port models the same "owned memory view" abstraction
// using a byte[] backing buffer plus an offset/length window so the bytes can be
// parsed identically to the C++ pointer arithmetic.

using System;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// A holder for memory owned by a graph tile. Mirrors the C++ abstract
/// <c>class GraphMemory { char* data; size_t size; }</c>: it provides a contiguous
/// byte view (the decompressed/loaded tile blob) that tile structures are parsed from.
/// </summary>
/// <remarks>
/// The C++ base class is abstract (protected ctor + virtual dtor) with concrete
/// subclasses for mmap and flat-buffer backing stores. The routing-relevant contract is
/// simply "a span of bytes with a known size", which this base type exposes via
/// <see cref="Data"/>, <see cref="Offset"/> and <see cref="Size"/>.
/// </remarks>
public abstract class GraphMemory
{
    /// <summary>Protected constructor mirroring the C++ <c>GraphMemory() = default;</c>.</summary>
    protected GraphMemory()
    {
        Data = Array.Empty<byte>();
        Offset = 0;
        Size = 0;
    }

    /// <summary>
    /// Constructs a memory holder over the supplied backing buffer.
    /// </summary>
    /// <param name="data">Backing byte buffer (the tile blob).</param>
    /// <param name="offset">Offset within <paramref name="data"/> where the tile begins.</param>
    /// <param name="size">Number of bytes belonging to this tile (C++ <c>size_t size</c>).</param>
    protected GraphMemory(byte[] data, int offset, long size)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Offset = offset;
        Size = size;
    }

    /// <summary>
    /// The backing buffer. Mirrors C++ <c>char* data</c> (combined with <see cref="Offset"/>
    /// this gives the equivalent of the raw pointer).
    /// </summary>
    public byte[] Data { get; protected set; }

    /// <summary>Offset within <see cref="Data"/> at which this tile's bytes start.</summary>
    public int Offset { get; protected set; }

    /// <summary>Number of bytes in the tile. Mirrors C++ <c>size_t size</c>.</summary>
    public long Size { get; protected set; }

    /// <summary>Convenience read-only span over the owned bytes.</summary>
    public ReadOnlySpan<byte> Span => Data.AsSpan(Offset, checked((int)Size));
}
