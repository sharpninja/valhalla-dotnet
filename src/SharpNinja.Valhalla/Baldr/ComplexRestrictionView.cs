// Faithful C# port of valhalla::baldr::ComplexRestrictionView (graphtile.h) @ 3.7.0.
// Source: F:/github/valhalla/valhalla/baldr/graphtile.h (lines 46-129)
//
// A lazy, forward-iterable view over a contiguous block of serialized ComplexRestriction
// records within a tile. It walks the variable-length records (each is the fixed 24-byte
// struct followed by via_count GraphIds) and yields only those whose to_graphid (forward) or
// from_graphid (reverse) matches the requested id AND whose modes overlap the requested modes.
//
// TILE-LAYOUT FIDELITY: the records are read directly out of a byte buffer (the tile's complex
// restriction section). Each ComplexRestriction struct is 24 bytes (LSB-first bit packing,
// verified elsewhere); the iterator advances by cr.SizeOf() = 24 + 8*via_count, exactly as the
// C++ iterator does with reinterpret_cast<const ComplexRestriction*>(data_ + offset_).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Lazy forward-iterable view over a tile's serialized <see cref="ComplexRestriction"/> records,
/// filtered by graph id (to-id in forward order, from-id in reverse order) and overlapping modes.
/// Faithful port of C++ <c>class ComplexRestrictionView</c>.
/// </summary>
/// <remarks>
/// The C++ view reads the records straight out of the memory-mapped tile via pointer arithmetic.
/// Here the records live in a byte buffer (<paramref name="data"/> region) and each
/// <see cref="ComplexRestriction"/> is read with <see cref="MemoryMarshal"/>; the iterator advances
/// by <see cref="ComplexRestriction.SizeOf"/> exactly as the engine advances by <c>cr-&gt;SizeOf()</c>.
/// </remarks>
public readonly struct ComplexRestrictionView : IEnumerable<ComplexRestriction>
{
    private readonly byte[] _data;
    private readonly int _offset;
    private readonly long _size;
    private readonly GraphId _id;
    private readonly ulong _modes;
    private readonly bool _forward;

    /// <summary>Constructs an empty view (mirrors the C++ default-constructed view).</summary>
    public ComplexRestrictionView()
    {
        _data = Array.Empty<byte>();
        _offset = 0;
        _size = 0;
        _id = default;
        _modes = 0;
        _forward = false;
    }

    /// <summary>
    /// Constructs a view over the complex restriction byte block. Faithful port of the C++ ctor
    /// <c>ComplexRestrictionView(const char* data, size_t size, GraphId id, uint64_t modes, bool forward)</c>.
    /// </summary>
    /// <param name="data">Backing buffer containing the complex restriction section.</param>
    /// <param name="offset">Offset within <paramref name="data"/> where the section begins.</param>
    /// <param name="size">Size in bytes of the section.</param>
    /// <param name="id">Graph id to match (to-id in forward order, from-id in reverse order).</param>
    /// <param name="modes">Access mode mask; a restriction matches if it shares any mode bit.</param>
    /// <param name="forward">Whether to match the to-id (true) or the from-id (false).</param>
    public ComplexRestrictionView(byte[] data, int offset, long size, GraphId id, ulong modes, bool forward)
    {
        _data = data ?? Array.Empty<byte>();
        _offset = offset;
        _size = size;
        _id = id;
        _modes = modes;
        _forward = forward;
    }

    /// <summary>Returns true if the view yields no restrictions (mirrors <c>view_interface::empty()</c>).</summary>
    public bool Empty()
    {
        Enumerator e = GetEnumerator();
        return !e.MoveNext();
    }

    /// <summary>
    /// Returns the first matching restriction (mirrors <c>view_interface::front()</c>). Throws if
    /// the view is empty.
    /// </summary>
    public ComplexRestriction Front()
    {
        Enumerator e = GetEnumerator();
        if (!e.MoveNext())
        {
            throw new InvalidOperationException("ComplexRestrictionView is empty");
        }

        return e.Current;
    }

    /// <summary>
    /// Enumerates each matching restriction together with the via <see cref="GraphId"/>s that follow
    /// it on disk. The ported <see cref="ComplexRestriction"/> is the fixed 24-byte head only; the C++
    /// <c>WalkVias</c> reads the vias straight off the bytes after the struct, which this surfaces so
    /// callers (e.g. the bidirectional A* bridging-restriction check) can drive
    /// <see cref="ComplexRestriction.WalkVias"/>.
    /// </summary>
    public IEnumerable<(ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias)> WithVias()
    {
        Enumerator e = GetEnumerator();
        while (e.MoveNext())
        {
            yield return (e.Current, e.CurrentVias());
        }
    }

    /// <summary>Forward iterator over matching restrictions. Faithful port of the C++ nested iterator.</summary>
    public Enumerator GetEnumerator() => new(_data, _offset, _size, _id, _modes, _forward);

    IEnumerator<ComplexRestriction> IEnumerable<ComplexRestriction>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Forward iterator that advances over variable-length records, skipping any whose id/modes do
    /// not match. Faithful port of the C++ <c>ComplexRestrictionView::iterator</c>.
    /// </summary>
    public struct Enumerator : IEnumerator<ComplexRestriction>
    {
        private readonly byte[] _data;
        private readonly int _baseOffset;
        private readonly long _size;
        private readonly GraphId _id;
        private readonly ulong _modes;
        private readonly bool _forward;
        private long _pos;       // relative offset within the section (matches C++ offset_)
        private bool _started;

        internal Enumerator(byte[] data, int baseOffset, long size, GraphId id, ulong modes, bool forward)
        {
            _data = data;
            _baseOffset = baseOffset;
            _size = size;
            _id = id;
            _modes = modes;
            _forward = forward;
            _pos = 0;
            _started = false;
        }

        /// <summary>The current restriction.</summary>
        public ComplexRestriction Current => ReadAt(_pos);

        object IEnumerator.Current => Current;

        /// <summary>
        /// Reads the via <see cref="GraphId"/>s that immediately follow the current restriction's
        /// fixed-size struct on disk. The C++ <c>WalkVias</c> reads these in place via pointer
        /// arithmetic (<c>this + 1</c>); here they are read out of the backing buffer so callers can
        /// pass them to <see cref="ComplexRestriction.WalkVias"/>.
        /// </summary>
        /// <returns>The ordered list of via edge ids for the current restriction (empty if none).</returns>
        public readonly IReadOnlyList<GraphId> CurrentVias()
        {
            ComplexRestriction cr = ReadAt(_pos);
            int viaCount = cr.ViaCount();
            if (viaCount == 0)
            {
                return Array.Empty<GraphId>();
            }

            var vias = new GraphId[viaCount];
            int viaStart = _baseOffset + checked((int)_pos) + ComplexRestriction.SizeOfStruct;
            for (int i = 0; i < viaCount; i++)
            {
                ReadOnlySpan<byte> span = _data.AsSpan(viaStart + (i * ComplexRestriction.SizeOfGraphId), ComplexRestriction.SizeOfGraphId);
                vias[i] = new GraphId(MemoryMarshal.Read<ulong>(span));
            }

            return vias;
        }

        /// <inheritdoc/>
        public bool MoveNext()
        {
            if (!_started)
            {
                _started = true;
            }
            else
            {
                // ++it: advance past the current record (struct + vias) then seek the next match.
                ComplexRestriction cr = ReadAt(_pos);
                _pos += cr.SizeOf();
            }

            return AdvanceToNext();
        }

        // Advance offset_ to the next record matching id and modes (C++ advance_to_next()).
        private bool AdvanceToNext()
        {
            while (_pos < _size)
            {
                ComplexRestriction cr = ReadAt(_pos);
                GraphId candidate = _forward ? cr.ToGraphId() : cr.FromGraphId();
                if (candidate == _id && (cr.Modes() & _modes) != 0)
                {
                    return true;
                }

                _pos += cr.SizeOf();
            }

            return false;
        }

        private readonly ComplexRestriction ReadAt(long rel)
        {
            int start = _baseOffset + checked((int)rel);
            ReadOnlySpan<byte> span = _data.AsSpan(start, ComplexRestriction.SizeOfStruct);
            return MemoryMarshal.Read<ComplexRestriction>(span);
        }

        /// <inheritdoc/>
        public void Reset()
        {
            _pos = 0;
            _started = false;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}
