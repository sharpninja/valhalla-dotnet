// Faithful C# port of Valhalla baldr LaneConnectivity
// (valhalla/baldr/laneconnectivity.h + src/baldr/laneconnectivity.cc) @ 3.7.0.
// Stores lane connectivity between two edges, plus a compact lane-mask helper type.
//
// TILE-LAYOUT FIDELITY:
//   C++ LaneConnectivityLanes:
//     uint64_t value_;   // single 64-bit value representing the lane mask -> 8 bytes
//   C++ LaneConnectivity (little-endian, packed read directly from the tile blob):
//     uint64_t to_   : 22;   // destination edge index
//     uint64_t from_ : 42;   // source way id
//     LaneConnectivityLanes to_lanes_;    // 8 bytes
//     LaneConnectivityLanes from_lanes_;  // 8 bytes
//   sizeof(LaneConnectivity) == 24 bytes (verified by gtest LaneConnectivity.SizeOf).
//   word0..7  : to_ (bits 0..21) + from_ (bits 22..63), one 64-bit word
//   word8..15 : to_lanes_.value_
//   word16..23: from_lanes_.value_

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Compact lane-mask storage. Faithful port of <c>valhalla::baldr::LaneConnectivityLanes</c>.
/// A single 64-bit value packs up to <see cref="MaxLanesPerConnection"/> nibbles (4 bits each).
/// Example string form: <c>1|2|3|4|4</c>. On-disk size is 8 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LaneConnectivityLanes
{
    /// <summary>Number of bits used to store a single lane value.</summary>
    public const int MaxLanesPerConnectionBits = 4;

    /// <summary>Maximum value (and count) of lanes per connection: (1 &lt;&lt; 4) - 1 == 15.</summary>
    public const int MaxLanesPerConnection = (1 << MaxLanesPerConnectionBits) - 1;

    private ulong value_; // single 64-bit value representing the lane mask

    /// <summary>
    /// Constructor from a string representation of the lane mask (pipe separated, e.g. "1|2|3").
    /// </summary>
    /// <param name="lanes">String representation of the lane mask.</param>
    /// <exception cref="ArgumentException">Thrown if a token is not a valid integer.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if a lane or index is out of bounds.</exception>
    public LaneConnectivityLanes(string lanes)
    {
        value_ = 0;

        // C++ uses boost::split on '|' which (unlike std::getline) DOES yield empty tokens for
        // leading/trailing/adjacent delimiters. So "|1|" splits to {"", "1", ""}.
        string[] tokens = lanes.Split(SharpNinja.Valhalla.Baldr.LaneConnectivity.LaneSplitChar);
        byte n = 1;
        foreach (string t in tokens)
        {
            // midgard::to_int throws (ArgumentException) on empty / non-numeric tokens.
            SetLane(n++, (byte)Util.ToInt(t));
        }
    }

    /// <summary>
    /// Get the text representation of the lane mask (pipe separated, omitting zero lanes).
    /// Faithful port of <c>to_string</c>.
    /// </summary>
    /// <returns>Text representation of the lane mask.</returns>
    public readonly string ToTextString()
    {
        var result = new StringBuilder();
        for (int i = 1; i <= MaxLanesPerConnection; ++i)
        {
            byte lane = GetLane((byte)i);
            if (lane != 0)
            {
                if (result.Length != 0)
                {
                    result.Append('|');
                }

                result.Append(lane);
            }
        }

        return result.ToString();
    }

    private void SetLane(byte n, byte lane)
    {
        if (n == 0 || n > MaxLanesPerConnection || lane > MaxLanesPerConnection)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "lane or index out of bounds");
        }

        value_ |= (ulong)lane << ((n - 1) * MaxLanesPerConnectionBits);
    }

    private readonly byte GetLane(byte n)
    {
        if (n == 0 || n > MaxLanesPerConnection)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "index out of bounds");
        }

        return (byte)((value_ >> ((n - 1) * MaxLanesPerConnectionBits)) & MaxLanesPerConnection);
    }
}

/// <summary>
/// Stores lane connectivity between two edges. Faithful port of
/// <c>valhalla::baldr::LaneConnectivity</c>. On-disk size is 24 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LaneConnectivity : IComparable<LaneConnectivity>
{
    /// <summary>The '|' character used to split lane strings (mirrors boost::is_any_of("|")).</summary>
    internal const char LaneSplitChar = '|';

    // Bit layout for the first 64-bit word:
    //   bits 0..21  : to_   (22 bits, destination edge index)
    //   bits 22..63 : from_ (42 bits, source way id)
    private const int ToBits = 22;
    private const ulong ToMask = (1UL << ToBits) - 1UL;        // 0x3FFFFF
    private const ulong FromLimit = 1UL << 42;                 // 1 << 42

    private ulong packed_;                    // to_ (22) + from_ (42)
    private LaneConnectivityLanes toLanes_;   // 8 bytes
    private LaneConnectivityLanes fromLanes_; // 8 bytes

    /// <summary>
    /// Constructor with arguments.
    /// </summary>
    /// <param name="idx">Directed (destination) edge index.</param>
    /// <param name="from">From segment / way id.</param>
    /// <param name="toLanes">List of lanes on the <c>to</c> edge (string form).</param>
    /// <param name="fromLanes">List of lanes on the <c>from</c> edge (string form).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="from"/> exceeds 42 bits.</exception>
    public LaneConnectivity(uint idx, ulong from, string toLanes, string fromLanes)
    {
        packed_ = 0;
        toLanes_ = new LaneConnectivityLanes(toLanes);
        fromLanes_ = new LaneConnectivityLanes(fromLanes);

        SetToInternal(idx);
        SetFromInternal(from);

        if (from >= FromLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(from), "from way_id is too large");
        }
    }

    /// <summary>
    /// Get the index of the directed edge this lane connection applies to (22-bit field).
    /// </summary>
    public readonly uint To => (uint)(packed_ & ToMask);

    /// <summary>
    /// Set the directed edge index to which this lane connection applies.
    /// </summary>
    /// <param name="idx">Edge index.</param>
    public void SetTo(uint idx) => SetToInternal(idx);

    /// <summary>
    /// Get the OSM id of the incoming way of this lane connection (42-bit field).
    /// </summary>
    public readonly ulong From => packed_ >> ToBits;

    /// <summary>
    /// Get the text representation of lanes in the incoming (from) way.
    /// </summary>
    public readonly string FromLanes => fromLanes_.ToTextString();

    /// <summary>
    /// Get the text representation of lanes in the current (to) way.
    /// </summary>
    public readonly string ToLanes => toLanes_.ToTextString();

    /// <summary>
    /// Comparison for sorting (mirrors C++ <c>operator&lt;</c>): sort by <c>to</c> id.
    /// </summary>
    public readonly int CompareTo(LaneConnectivity other) => To.CompareTo(other.To);

    private void SetToInternal(uint idx) => packed_ = (packed_ & ~ToMask) | (idx & ToMask);

    private void SetFromInternal(ulong from) => packed_ = (packed_ & ToMask) | (from << ToBits);
}
