// Faithful C# port of Valhalla baldr AccessRestriction (accessrestriction.h + src/baldr/accessrestriction.cc) @ 3.7.0.
// Source: valhalla/baldr/accessrestriction.h, src/baldr/accessrestriction.cc
//
// Information held for each access restriction. Read directly from / written directly to tile
// blobs, so the bit layout MUST match the C++ exactly.
//
// EXACT LAYOUT (two consecutive uint64 words => 16 bytes total):
//   word 0 (packed bitfields, LSB first):
//     bits  0..21 (22 bits) : edgeindex_           (directed edge index; max kMaxTileEdgeCount)
//     bits 22..27 ( 6 bits) : type_                (AccessType)
//     bits 28..39 (12 bits) : modes_               (mode bit mask this restriction applies to)
//     bit  40     ( 1 bit)  : except_destination_  (local traffic exempt flag)
//     bits 41..63 (23 bits) : spare_
//   word 1:
//     uint64 value_                                 (value; meaning depends on type)
// Total size: 16 bytes (the C++ test asserts sizeof(AccessRestriction) == 16).
//
// PORT-NOTE: the C++ json(rapidjson::writer_wrapper_t&) method is omitted (json/rapidjson
//            serialization is an excluded module). All other members are ported faithfully.

using System;
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Information held for each access restriction. Faithful port of C++ <c>class AccessRestriction</c>.
/// </summary>
/// <remarks>
/// Tile-layout fidelity: laid out as two consecutive little-endian 64-bit words (16 bytes total),
/// matching the C++ struct exactly so a tile byte buffer parses identically. The first word is the
/// packed bitfield set (edgeindex/type/modes/except_destination/spare); the second word is the
/// raw value. See the file header for the full bit map.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct AccessRestriction : IComparable<AccessRestriction>, IEquatable<AccessRestriction>
{
    // word 0: packed bitfields
    private ulong _bits;

    // word 1: value
    private ulong _value;

    private const int EdgeIndexShift = 0;
    private const ulong EdgeIndexMask = 0x3FFFFF; // 22 bits
    private const int TypeShift = 22;
    private const ulong TypeMask = 0x3F; // 6 bits
    private const int ModesShift = 28;
    private const ulong ModesMask = 0xFFF; // 12 bits
    private const int ExceptDestShift = 40;
    private const ulong ExceptDestMask = 0x1; // 1 bit

    /// <summary>
    /// Constructor with arguments. Mirrors the C++ ctor; spare bits are initialized to 0.
    /// </summary>
    /// <param name="edgeindex">Directed edge index within the tile.</param>
    /// <param name="type">Access restriction type.</param>
    /// <param name="modes">Bit mask of affected modes.</param>
    /// <param name="value">Value for this restriction (meaning depends on <paramref name="type"/>).</param>
    /// <param name="exceptDestination">Whether local traffic is exempted from this restriction.</param>
    public AccessRestriction(uint edgeindex, AccessType type, uint modes, ulong value, bool exceptDestination)
    {
        _bits = 0;
        _value = value;
        SetField(EdgeIndexShift, EdgeIndexMask, edgeindex);
        SetField(TypeShift, TypeMask, (uint)type);
        SetField(ModesShift, ModesMask, modes);
        SetField(ExceptDestShift, ExceptDestMask, exceptDestination ? 1u : 0u);
    }

    private readonly uint GetField(int shift, ulong mask) => (uint)((_bits >> shift) & mask);

    private void SetField(int shift, ulong mask, ulong v)
        => _bits = (_bits & ~(mask << shift)) | ((v & mask) << shift);

    /// <summary>
    /// Gets the internal edge index to which this access restriction applies (within the tile).
    /// </summary>
    public readonly uint EdgeIndex() => GetField(EdgeIndexShift, EdgeIndexMask);

    /// <summary>Sets the directed edge index to which this access restriction applies.</summary>
    public void SetEdgeIndex(uint edgeindex) => SetField(EdgeIndexShift, EdgeIndexMask, edgeindex);

    /// <summary>Gets the type of the restriction. See <see cref="AccessType"/>.</summary>
    public readonly AccessType Type() => (AccessType)GetField(TypeShift, TypeMask);

    /// <summary>Gets the modes impacted by this access restriction (a bit mask of affected modes).</summary>
    public readonly uint Modes() => GetField(ModesShift, ModesMask);

    /// <summary>
    /// Gets the flag telling whether local traffic is exempted from this restriction.
    /// </summary>
    public readonly bool ExceptDestination() => GetField(ExceptDestShift, ExceptDestMask) != 0;

    /// <summary>Sets the exemption flag for local traffic.</summary>
    public void SetExceptDestination(bool exceptDestination)
        => SetField(ExceptDestShift, ExceptDestMask, exceptDestination ? 1u : 0u);

    /// <summary>Gets the value for this restriction.</summary>
    public readonly ulong Value() => _value;

    /// <summary>Sets the value for this restriction.</summary>
    public void SetValue(ulong v) => _value = v;

    /// <summary>
    /// operator&lt; - for sorting. Sort by edge index, then modes, then type, then value.
    /// Faithful port of C++ <c>AccessRestriction::operator&lt;</c>.
    /// </summary>
    public readonly int CompareTo(AccessRestriction other)
    {
        if (EdgeIndex() == other.EdgeIndex())
        {
            if (Modes() == other.Modes())
            {
                if (Type() == other.Type())
                {
                    return Value().CompareTo(other.Value());
                }

                return Type() < other.Type() ? -1 : 1;
            }

            return Modes() < other.Modes() ? -1 : 1;
        }

        return EdgeIndex() < other.EdgeIndex() ? -1 : 1;
    }

    /// <summary>Less than operator for sorting (mirrors C++ <c>operator&lt;</c>).</summary>
    public static bool operator <(AccessRestriction lhs, AccessRestriction rhs) => lhs.CompareTo(rhs) < 0;

    /// <summary>Greater than operator for sorting.</summary>
    public static bool operator >(AccessRestriction lhs, AccessRestriction rhs) => lhs.CompareTo(rhs) > 0;

    /// <summary>Less than or equal operator.</summary>
    public static bool operator <=(AccessRestriction lhs, AccessRestriction rhs) => lhs.CompareTo(rhs) <= 0;

    /// <summary>Greater than or equal operator.</summary>
    public static bool operator >=(AccessRestriction lhs, AccessRestriction rhs) => lhs.CompareTo(rhs) >= 0;

    /// <inheritdoc/>
    public readonly bool Equals(AccessRestriction other) => _bits == other._bits && _value == other._value;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is AccessRestriction other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(_bits, _value);

    /// <summary>Operator equality.</summary>
    public static bool operator ==(AccessRestriction lhs, AccessRestriction rhs) => lhs.Equals(rhs);

    /// <summary>Operator inequality.</summary>
    public static bool operator !=(AccessRestriction lhs, AccessRestriction rhs) => !lhs.Equals(rhs);
}
