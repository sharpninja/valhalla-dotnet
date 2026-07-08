// Faithful C# port of Valhalla baldr ConditionalSpeedLimit (conditional_speed_limit.h) @ 3.7.0.
// Source: valhalla/baldr/conditional_speed_limit.h
//
// A C++ union of a TimeDomain and a bit-packed { padding_:54, speed_:8, spare_:2 }
// struct, all sharing the same 8 bytes. Read directly from the tile blob.
//
// EXACT BIT LAYOUT (single uint64, LSB first):
//   bits  0..53 (54 bits) : padding_  (overlaps the meaningful TimeDomain bits)
//   bits 54..61 ( 8 bits) : speed_    (speed limit in KPH)
//   bits 62..63 ( 2 bits) : spare_
// Total size: 8 bytes (static_assert sizeof == 8 in C++).

using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// A combination of a <see cref="Baldr.TimeDomain"/> condition and a speed limit in a single 8-byte
/// word. Faithful port of C++ <c>union ConditionalSpeedLimit</c>.
/// </summary>
/// <remarks>
/// Tile-layout fidelity: a single 8-byte little-endian word. The <see cref="TimeDomain"/> view and
/// the <see cref="Speed"/> view share the same storage (the speed occupies bits 54-61, which the
/// TimeDomain treats as part of its spare region). See file header for the bit map.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct ConditionalSpeedLimit
{
    [FieldOffset(0)]
    private ulong _value;

    private const int SpeedShift = 54;
    private const ulong SpeedMask = 0xFF; // 8 bits

    /// <summary>Constructs from a raw 8-byte little-endian word (as read from a tile).</summary>
    public ConditionalSpeedLimit(ulong value)
    {
        _value = value;
    }

    /// <summary>The raw 8-byte word.</summary>
    public readonly ulong Value => _value;

    /// <summary>The <see cref="Baldr.TimeDomain"/> view of this word (the <c>td_</c> union member).</summary>
    public TimeDomain TimeDomain
    {
        readonly get => new TimeDomain(_value);
        set => _value = value.TdValue;
    }

    /// <summary>Speed limit in KPH (bits 54-61).</summary>
    public byte Speed
    {
        readonly get => (byte)((_value >> SpeedShift) & SpeedMask);
        set => _value = (_value & ~(SpeedMask << SpeedShift)) | (((ulong)value & SpeedMask) << SpeedShift);
    }
}
