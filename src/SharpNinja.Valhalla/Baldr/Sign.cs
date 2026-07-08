// Faithful C# port of Valhalla baldr Sign (sign.h) @ 3.7.0.
// Source: valhalla/baldr/sign.h
// This is a self-contained engine port: it intentionally does NOT reuse other
// TruckMate types. Field widths, bit-packing order, and on-disk struct size are
// reproduced exactly so a tile byte buffer parses identically to the C++ engine.
//
// EXACT BIT LAYOUT (must match the on-disk tile blob):
//   Word 0 (uint32, little-endian):
//     bits  0..21 (22 bits) : index_           (directed edge or node index)
//     bits 22..29 ( 8 bits) : type_            (Sign::Type)
//     bit  30      ( 1 bit) : route_num_type_
//     bit  31      ( 1 bit) : tagged_
//   Word 1 (uint32):
//     bits  0..31 (32 bits) : text_offset_
// Total struct size: 8 bytes.

using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Holds a generic sign with type and text. Text is stored in the GraphTile
/// text list and the offset is stored within the sign. The directed edge index
/// within the tile is also stored so that signs can be found via the directed
/// edge or node index.
/// </summary>
/// <remarks>
/// Tile-layout fidelity: this struct is bit-packed and read directly from the
/// on-disk tile blob. Size is exactly 8 bytes. See file header for the bit map.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Sign
{
    /// <summary>
    /// Sign type. Backing storage is 8 bits (see <c>kLinguistic = 255</c>).
    /// </summary>
    public enum Type : byte
    {
        ExitNumber = 0,
        ExitBranch = 1,
        ExitToward = 2,
        ExitName = 3,
        GuideBranch = 4,
        GuideToward = 5,
        JunctionName = 6,
        GuidanceViewJunction = 7,
        GuidanceViewSignboard = 8,
        TollName = 9,
        Linguistic = 255,
    }

    // Bit masks/shifts matching the C++ bitfields in word 0.
    private const int IndexBits = 22;
    private const int TypeBits = 8;
    private const uint IndexMask = (1u << IndexBits) - 1u; // 0x003FFFFF
    private const uint TypeMask = (1u << TypeBits) - 1u;   // 0x000000FF
    private const int TypeShift = IndexBits;               // 22
    private const int RouteNumTypeShift = IndexBits + TypeBits;       // 30
    private const int TaggedShift = IndexBits + TypeBits + 1;         // 31

    // Word 0: packed index_:22, type_:8, route_num_type_:1, tagged_:1.
    private uint _word0;

    // Word 1: text_offset_ (full 32 bits).
    private uint _textOffset;

    /// <summary>
    /// Constructor given arguments.
    /// </summary>
    /// <param name="idx">Directed edge or node index to which this sign applies.</param>
    /// <param name="type">Sign type.</param>
    /// <param name="rnType">
    /// Boolean indicating whether this sign indicates a route number or the guidance view type.
    /// </param>
    /// <param name="tagged">Whether the sign text is tagged.</param>
    /// <param name="textOffset">Offset to text in the names/text table.</param>
    public Sign(uint idx, Sign.Type type, bool rnType, bool tagged, uint textOffset)
    {
        _word0 = (idx & IndexMask)
                 | (((uint)type & TypeMask) << TypeShift)
                 | ((rnType ? 1u : 0u) << RouteNumTypeShift)
                 | ((tagged ? 1u : 0u) << TaggedShift);
        _textOffset = textOffset;
    }

    /// <summary>
    /// Gets or sets the index of the directed edge or node this sign applies to
    /// (within the same tile as the sign information). 22-bit field.
    /// </summary>
    public uint Index
    {
        readonly get => _word0 & IndexMask;
        set => _word0 = (_word0 & ~IndexMask) | (value & IndexMask);
    }

    /// <summary>Gets the sign type.</summary>
    public readonly Sign.Type GetSignType() => (Sign.Type)((_word0 >> TypeShift) & TypeMask);

    /// <summary>
    /// Does this sign record indicate a route number, phoneme for a node, or the guidance view
    /// type. Returns true if the sign record is a route number, phoneme for a node, or - for a
    /// guidance view sign - true indicates a base image and false an overlay image.
    /// </summary>
    public readonly bool IsRouteNumType() => ((_word0 >> RouteNumTypeShift) & 1u) != 0u;

    /// <summary>Is the sign text tagged.</summary>
    public readonly bool Tagged() => ((_word0 >> TaggedShift) & 1u) != 0u;

    /// <summary>
    /// Gets the offset into the GraphTile text list for the text associated with the sign.
    /// </summary>
    public readonly uint TextOffset => _textOffset;

    /// <summary>
    /// operator&lt; - for sorting. Sort by edge or node index and then by type.
    /// </summary>
    public readonly int CompareTo(Sign other)
    {
        if (Index == other.Index)
        {
            return GetSignType().CompareTo(other.GetSignType());
        }

        return Index.CompareTo(other.Index);
    }

    /// <summary>Less-than comparison mirroring the C++ <c>operator&lt;</c>.</summary>
    public static bool operator <(Sign a, Sign b) => a.CompareTo(b) < 0;

    /// <summary>Greater-than comparison.</summary>
    public static bool operator >(Sign a, Sign b) => a.CompareTo(b) > 0;
}
