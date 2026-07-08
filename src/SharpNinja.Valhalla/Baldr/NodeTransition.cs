// Faithful C# port of Valhalla baldr NodeTransition (nodetransition.h) @ 3.7.0.
// Source: valhalla/baldr/nodetransition.h
// Self-contained engine port: field widths, bit-packing order, and on-disk struct
// size are reproduced exactly so a tile byte buffer parses identically to the C++ engine.
//
// EXACT BIT LAYOUT (single little-endian uint64 word, must match the on-disk tile blob):
//   bits  0..45 (46 bits) : endnode_  (GraphId value of the end node)
//   bit  46      ( 1 bit) : up_       (true = transition up to a higher level)
//   bits 47..63 (17 bits) : spare_
// Total struct size: 8 bytes.

using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Records a transition between a node on the current tile and a node at the same position
/// on a different hierarchy level. Stores the <see cref="GraphId"/> of the end node as well as a
/// flag indicating whether the transition is upwards (true) or downwards (false).
/// </summary>
/// <remarks>
/// Tile-layout fidelity: this struct is bit-packed and read directly from the on-disk tile blob.
/// Size is exactly 8 bytes. See the file header for the bit map.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NodeTransition
{
    // Bit widths/masks/shifts matching the C++ bitfields in the single 64-bit word.
    private const int EndNodeBits = 46;
    private const ulong EndNodeMask = (1UL << EndNodeBits) - 1UL; // 0x00003FFFFFFFFFFF
    private const int UpShift = EndNodeBits;                      // 46
    private const ulong UpMask = 1UL << UpShift;                  // 0x0000400000000000

    // Single packed 64-bit word: endnode_:46, up_:1, spare_:17.
    private ulong _word;

    /// <summary>
    /// Constructor given arguments. Mirrors the C++ <c>NodeTransition(const GraphId&amp; node, const bool up)</c>
    /// initializer list (<c>endnode_(node.value), up_(up), spare_(0)</c>).
    /// </summary>
    /// <param name="node">End node of the transition.</param>
    /// <param name="up">
    /// True if the transition is up to a higher level, false if the transition is down to a lower level.
    /// </param>
    public NodeTransition(GraphId node, bool up)
    {
        _word = (node.Value & EndNodeMask) | (up ? UpMask : 0UL);
    }

    /// <summary>
    /// Default-equivalent factory producing a transition with an invalid end node (matches the C++
    /// default constructor: <c>endnode_(kInvalidGraphId), up_(0), spare_(0)</c>).
    /// </summary>
    /// <remarks>
    /// <c>kInvalidGraphId == 0x3fffffffffff</c> exactly fills the 46-bit <c>endnode_</c> field, so the
    /// resulting packed word is <c>0x00003FFFFFFFFFFF</c> with the up bit and spare cleared.
    /// </remarks>
    public static NodeTransition Default => new NodeTransition { _word = GraphId.InvalidGraphId & EndNodeMask };

    /// <summary>Gets the id of the corresponding node on another hierarchy level.</summary>
    public readonly GraphId EndNode() => new GraphId(_word & EndNodeMask);

    /// <summary>
    /// Is the transition up to a higher level.
    /// </summary>
    /// <returns>
    /// True if the transition is up to a higher level, false if the transition is down to a lower level.
    /// </returns>
    public readonly bool Up() => (_word & UpMask) != 0UL;
}
