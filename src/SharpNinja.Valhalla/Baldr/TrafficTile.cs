// Faithful C# port of Valhalla baldr live-traffic tile layout @ 3.7.0.
// Source: valhalla/baldr/traffictile.h
//
// A live-traffic tile on disk is laid out as:
//   TrafficTileHeader (32 bytes)
//   n x TrafficSpeed  (n x 8 bytes)
//
// CRITICAL FIDELITY: TrafficSpeed is a 64-bit bit-packed struct read directly from the
// tile blob. The C++ declares it as a uint64_t bitfield; on the little-endian platforms
// Valhalla targets (and as relied upon by the on-disk format), the first declared field
// occupies the least-significant bits. This port reproduces that exact packing by storing
// the 64 bits in a single ulong and exposing each field via shift/mask accessors in the
// same bit order. A tile byte buffer therefore parses identically.
//
// Bit layout of TrafficSpeed (LSB -> MSB), total 64 bits = 8 bytes:
//   [ 0.. 6]  overall_encoded_speed : 7
//   [ 7..13]  encoded_speed1        : 7
//   [14..20]  encoded_speed2        : 7
//   [21..27]  encoded_speed3        : 7
//   [28..35]  breakpoint1           : 8
//   [36..43]  breakpoint2           : 8
//   [44..49]  congestion1           : 6
//   [50..55]  congestion2           : 6
//   [56..61]  congestion3           : 6
//   [62]      has_incidents         : 1
//   [63]      spare                 : 1
//
// PORT-NOTE: the C++ class TrafficTile wraps an mmap'd GraphMemory blob with const
// volatile pointers (modifiable by another process). This port reads the tile from a
// byte buffer (TrafficTile.FromBytes / ctor taking ReadOnlyMemory<byte>). The volatile
// concurrent-mutation semantics are not reproduced (C# routing path reads a snapshot
// buffer), but the byte layout, field widths, struct sizes, and accessor algorithms are
// identical.

using System;
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Live-traffic tile-format constants ported from <c>valhalla/baldr/traffictile.h</c>.
/// </summary>
public static class TrafficTileConstants
{
    /// <summary>
    /// The version of the traffic tile format. C++ <c>TRAFFIC_TILE_VERSION =
    /// VALHALLA_VERSION_MAJOR</c>; pinned to the Valhalla 3.7.0 major version (3).
    /// </summary>
    public const byte TrafficTileVersion = 3;

    /// <summary>
    /// Raw bitfield value (not KPH) signalling an unknown live speed: max value of a 7-bit
    /// number = 127. C++ <c>UNKNOWN_TRAFFIC_SPEED_RAW</c>.
    /// </summary>
    public const uint UnknownTrafficSpeedRaw = (1u << 7) - 1;

    /// <summary>
    /// Maximum encodable traffic speed in KPH (2 KPH resolution). C++
    /// <c>MAX_TRAFFIC_SPEED_KPH = (UNKNOWN_TRAFFIC_SPEED_RAW - 1) &lt;&lt; 1</c> = 252.
    /// </summary>
    public const uint MaxTrafficSpeedKph = (UnknownTrafficSpeedRaw - 1) << 1;

    /// <summary>
    /// KPH value signifying an unknown traffic speed. C++ <c>UNKNOWN_TRAFFIC_SPEED_KPH =
    /// UNKNOWN_TRAFFIC_SPEED_RAW &lt;&lt; 1</c> = 254.
    /// </summary>
    public const uint UnknownTrafficSpeedKph = UnknownTrafficSpeedRaw << 1;

    /// <summary>Unknown congestion value. C++ <c>UNKNOWN_CONGESTION_VAL</c>.</summary>
    public const byte UnknownCongestionVal = 0;

    /// <summary>Max congestion value. C++ <c>MAX_CONGESTION_VAL</c> = 63.</summary>
    public const byte MaxCongestionVal = 63;
}

/// <summary>
/// Bit-packed live speed record for a single directed edge. Faithful port of the C++
/// <c>TrafficSpeed</c> bitfield struct. Exactly 8 bytes (one <see cref="ulong"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
public struct TrafficSpeed : IEquatable<TrafficSpeed>
{
    // The full 64-bit packed value, identical to the on-disk uint64_t.
    private ulong _bits;

    // ---- Bit positions / widths (LSB-first, matching GCC/Clang little-endian packing) ----
    private const int OverallSpeedShift = 0;
    private const int Speed1Shift = 7;
    private const int Speed2Shift = 14;
    private const int Speed3Shift = 21;
    private const int Breakpoint1Shift = 28;
    private const int Breakpoint2Shift = 36;
    private const int Congestion1Shift = 44;
    private const int Congestion2Shift = 50;
    private const int Congestion3Shift = 56;
    private const int HasIncidentsShift = 62;
    private const int SpareShift = 63;

    private const ulong Mask7 = 0x7F;
    private const ulong Mask8 = 0xFF;
    private const ulong Mask6 = 0x3F;
    private const ulong Mask1 = 0x1;

    /// <summary>Construct from the raw packed 64-bit value (e.g. read from a tile blob).</summary>
    public TrafficSpeed(ulong rawBits) => _bits = rawBits;

    /// <summary>
    /// Field-wise constructor. Faithful port of the C++ parameterized constructor; values are
    /// truncated to their bit widths exactly as C++ bitfield assignment would (wraparound).
    /// </summary>
    public TrafficSpeed(
        uint overallEncodedSpeed,
        uint s1,
        uint s2,
        uint s3,
        uint b1,
        uint b2,
        uint c1,
        uint c2,
        uint c3,
        bool incidents)
    {
        _bits = 0;
        OverallEncodedSpeed = overallEncodedSpeed;
        EncodedSpeed1 = s1;
        EncodedSpeed2 = s2;
        EncodedSpeed3 = s3;
        Breakpoint1 = b1;
        Breakpoint2 = b2;
        Congestion1 = c1;
        Congestion2 = c2;
        Congestion3 = c3;
        HasIncidents = incidents;
        Spare = 0;
    }

    /// <summary>The raw packed 64-bit value.</summary>
    public ulong RawBits
    {
        get => _bits;
        set => _bits = value;
    }

    private uint Get(int shift, ulong mask) => (uint)((_bits >> shift) & mask);

    private void Set(int shift, ulong mask, uint value)
        => _bits = (_bits & ~(mask << shift)) | (((ulong)value & mask) << shift);

    /// <summary>0-255 KPH in 2 KPH resolution (7 bits). Access overall speed via <see cref="GetOverallSpeed"/>.</summary>
    public uint OverallEncodedSpeed
    {
        get => Get(OverallSpeedShift, Mask7);
        set => Set(OverallSpeedShift, Mask7, value);
    }

    /// <summary>Subsegment-0 encoded speed (7 bits).</summary>
    public uint EncodedSpeed1
    {
        get => Get(Speed1Shift, Mask7);
        set => Set(Speed1Shift, Mask7, value);
    }

    /// <summary>Subsegment-1 encoded speed (7 bits).</summary>
    public uint EncodedSpeed2
    {
        get => Get(Speed2Shift, Mask7);
        set => Set(Speed2Shift, Mask7, value);
    }

    /// <summary>Subsegment-2 encoded speed (7 bits).</summary>
    public uint EncodedSpeed3
    {
        get => Get(Speed3Shift, Mask7);
        set => Set(Speed3Shift, Mask7, value);
    }

    /// <summary>Breakpoint 1 (8 bits): position = length * (breakpoint1 / 255).</summary>
    public uint Breakpoint1
    {
        get => Get(Breakpoint1Shift, Mask8);
        set => Set(Breakpoint1Shift, Mask8, value);
    }

    /// <summary>Breakpoint 2 (8 bits): position = length * (breakpoint2 / 255).</summary>
    public uint Breakpoint2
    {
        get => Get(Breakpoint2Shift, Mask8);
        set => Set(Breakpoint2Shift, Mask8, value);
    }

    /// <summary>Congestion 1 (6 bits): 0 (unknown) or 1..63 (no congestion -> max congestion).</summary>
    public uint Congestion1
    {
        get => Get(Congestion1Shift, Mask6);
        set => Set(Congestion1Shift, Mask6, value);
    }

    /// <summary>Congestion 2 (6 bits).</summary>
    public uint Congestion2
    {
        get => Get(Congestion2Shift, Mask6);
        set => Set(Congestion2Shift, Mask6, value);
    }

    /// <summary>Congestion 3 (6 bits).</summary>
    public uint Congestion3
    {
        get => Get(Congestion3Shift, Mask6);
        set => Set(Congestion3Shift, Mask6, value);
    }

    /// <summary>Whether incidents exist on this edge in the corresponding incident tile (1 bit).</summary>
    public bool HasIncidents
    {
        get => Get(HasIncidentsShift, Mask1) != 0;
        set => Set(HasIncidentsShift, Mask1, value ? 1u : 0u);
    }

    /// <summary>Spare bit (1 bit).</summary>
    public uint Spare
    {
        get => Get(SpareShift, Mask1);
        set => Set(SpareShift, Mask1, value);
    }

    /// <summary>
    /// True when the speed record carries a valid speed. Faithful port of
    /// <c>speed_valid()</c>.
    /// </summary>
    public bool SpeedValid()
        => Breakpoint1 != 0 && OverallEncodedSpeed != TrafficTileConstants.UnknownTrafficSpeedRaw;

    /// <summary>
    /// True when the whole edge is closed (valid speed of 0). Faithful port of
    /// <c>closed()</c>.
    /// </summary>
    public bool Closed()
        => Breakpoint1 != 0 && OverallEncodedSpeed == 0;

    /// <summary>
    /// True when the given subsegment is closed. Faithful port of <c>closed(subsegment)</c>.
    /// </summary>
    /// <param name="subsegment">Subsegment index (0, 1 or 2).</param>
    /// <exception cref="InvalidOperationException">Thrown for an out-of-range subsegment.</exception>
    public bool Closed(int subsegment)
    {
        if (!SpeedValid())
        {
            return false;
        }

        return subsegment switch
        {
            0 => EncodedSpeed1 == 0 || Congestion1 == TrafficTileConstants.MaxCongestionVal,
            1 => Breakpoint1 < 255 && (EncodedSpeed2 == 0 || Congestion2 == TrafficTileConstants.MaxCongestionVal),
            2 => Breakpoint2 < 255 && (EncodedSpeed3 == 0 || Congestion3 == TrafficTileConstants.MaxCongestionVal),
            _ => throw new InvalidOperationException("Bad subsegment"),
        };
    }

    /// <summary>Overall speed in KPH across the edge. Faithful port of <c>get_overall_speed()</c>.</summary>
    public byte GetOverallSpeed() => (byte)(OverallEncodedSpeed << 1);

    /// <summary>
    /// Speed in KPH for a subsegment, or <see cref="TrafficTileConstants.UnknownTrafficSpeedKph"/>
    /// when unknown. Faithful port of <c>get_speed(subsegment)</c>.
    /// </summary>
    /// <param name="subsegment">Subsegment index (0, 1 or 2).</param>
    /// <exception cref="InvalidOperationException">Thrown for an out-of-range subsegment.</exception>
    public byte GetSpeed(int subsegment)
    {
        if (!SpeedValid())
        {
            return (byte)TrafficTileConstants.UnknownTrafficSpeedKph;
        }

        return subsegment switch
        {
            0 => (byte)(EncodedSpeed1 << 1),
            1 => (byte)(EncodedSpeed2 << 1),
            2 => (byte)(EncodedSpeed3 << 1),
            _ => throw new InvalidOperationException("Bad subsegment"),
        };
    }

    /// <summary>The invalid / unknown speed sentinel. Faithful port of the C++ <c>INVALID_SPEED</c>.</summary>
    public static TrafficSpeed Invalid => new(
        TrafficTileConstants.UnknownTrafficSpeedRaw,
        TrafficTileConstants.UnknownTrafficSpeedRaw,
        TrafficTileConstants.UnknownTrafficSpeedRaw,
        TrafficTileConstants.UnknownTrafficSpeedRaw,
        0u, 0u, 0u, 0u, 0u, false);

    public bool Equals(TrafficSpeed other) => _bits == other._bits;

    public override bool Equals(object? obj) => obj is TrafficSpeed other && Equals(other);

    public override int GetHashCode() => _bits.GetHashCode();

    public static bool operator ==(TrafficSpeed left, TrafficSpeed right) => left.Equals(right);

    public static bool operator !=(TrafficSpeed left, TrafficSpeed right) => !left.Equals(right);
}

/// <summary>
/// Per-tile live-traffic header. Faithful port of the C++ <c>TrafficTileHeader</c>; exactly
/// 32 bytes (4 * sizeof(uint64_t)).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
public struct TrafficTileHeader
{
    /// <summary>Tile id (8 bytes). C++ <c>tile_id</c>.</summary>
    public ulong TileId;

    /// <summary>Last update, seconds since epoch (8 bytes). C++ <c>last_update</c>.</summary>
    public ulong LastUpdate;

    /// <summary>Directed-edge count (4 bytes). C++ <c>directed_edge_count</c>.</summary>
    public uint DirectedEdgeCount;

    /// <summary>Traffic tile format version (4 bytes). C++ <c>traffic_tile_version</c>.</summary>
    public uint TrafficTileVersion;

    /// <summary>Spare (4 bytes). C++ <c>spare2</c>.</summary>
    public uint Spare2;

    /// <summary>Spare (4 bytes). C++ <c>spare3</c>.</summary>
    public uint Spare3;
}

/// <summary>
/// A tile of live-traffic data. Faithful port of the C++ <c>TrafficTile</c> class.
/// </summary>
/// <remarks>
/// PORT-NOTE: replaces the C++ mmap'd <c>GraphMemory</c> + volatile pointers with a managed
/// byte buffer. The on-disk layout (24-byte header note in the C++ comment is stale; the
/// header is actually 32 bytes per the static_assert) is preserved: header at offset 0,
/// then a packed array of 8-byte TrafficSpeed entries.
/// </remarks>
public sealed class TrafficTile
{
    /// <summary>Byte size of <see cref="TrafficTileHeader"/> (32).</summary>
    public const int HeaderSize = 32;

    /// <summary>Byte size of one <see cref="TrafficSpeed"/> entry (8).</summary>
    public const int SpeedSize = 8;

    private readonly ReadOnlyMemory<byte> _memory;
    private readonly bool _valid;

    /// <summary>
    /// Construct from a tile byte buffer. A null/empty buffer yields an invalid tile (mirrors
    /// the C++ <c>TrafficTile(nullptr)</c> path), which never segfaults.
    /// </summary>
    public TrafficTile(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
        _valid = !memory.IsEmpty;
    }

    /// <summary>Invalid / empty tile. Mirrors C++ <c>TrafficTile(nullptr)</c>.</summary>
    public TrafficTile()
        : this(ReadOnlyMemory<byte>.Empty)
    {
    }

    /// <summary>True if this tile wraps a valid buffer. Faithful port of <c>operator()</c>.</summary>
    public bool IsValid => _valid;

    /// <summary>
    /// Read the tile header. Returns null when the tile is invalid (C++ <c>header == nullptr</c>).
    /// </summary>
    public TrafficTileHeader? Header
    {
        get
        {
            if (!_valid || _memory.Length < HeaderSize)
            {
                return null;
            }

            return MemoryMarshal.Read<TrafficTileHeader>(_memory.Span.Slice(0, HeaderSize));
        }
    }

    /// <summary>
    /// Returns the <see cref="TrafficSpeed"/> for a directed-edge offset. Faithful port of
    /// <c>trafficspeed(directed_edge_offset)</c>: returns the invalid sentinel when the tile
    /// is missing or the version mismatches, and throws when the offset is out of bounds.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the offset exceeds the edge count.</exception>
    public TrafficSpeed TrafficSpeed(uint directedEdgeOffset)
    {
        TrafficTileHeader? header = Header;
        if (header is null || header.Value.TrafficTileVersion != TrafficTileConstants.TrafficTileVersion)
        {
            return Baldr.TrafficSpeed.Invalid;
        }

        if (directedEdgeOffset >= header.Value.DirectedEdgeCount)
        {
            throw new InvalidOperationException(
                "TrafficSpeed requested for edgeid beyond bounds of tile (offset: " +
                directedEdgeOffset + ", edge count: " + header.Value.DirectedEdgeCount);
        }

        int start = HeaderSize + ((int)directedEdgeOffset * SpeedSize);
        ulong bits = MemoryMarshal.Read<ulong>(_memory.Span.Slice(start, SpeedSize));
        return new TrafficSpeed(bits);
    }
}
