// Faithful C# port of Valhalla baldr TurnLanes (valhalla/baldr/turnlanes.h) @ 3.7.0.
// Holds turn lane information at the end of a directed edge. Turn lane text is stored in the
// GraphTile text list and the offset is stored within this structure. The directed edge index
// within the tile is also stored so turn lanes can be found via the directed edge index.
//
// TILE-LAYOUT FIDELITY:
//   C++ struct (little-endian, packed read directly from the tile blob):
//     uint32_t edgeindex_ : 22;   // kMaxTileEdgeCount: 22 bits
//     uint32_t spare_     : 10;
//     uint32_t text_offset_;
//   sizeof(TurnLanes) == 8 bytes (verified by gtest kTurnLanesExpectedSize).
//   word0 (bytes 0..3): bits  0..21 edgeindex, bits 22..31 spare
//   word1 (bytes 4..7): text_offset (full 32 bits)
// We use [StructLayout(Sequential)] with two uint fields (Word0/TextOffset) and bit masks/shifts
// so a tile byte buffer parses identically to the C++ struct.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Turn-lane masks, delimiters and name/mask lookup tables ported from
/// <c>valhalla/baldr/turnlanes.h</c>.
/// </summary>
public static class TurnLaneConstants
{
    /// <summary>Lane delimiter ('|').</summary>
    public const char LaneDelimiter = '|';

    /// <summary>Turn lane delimiter within a single lane (';').</summary>
    public const char TurnLaneDelimiter = ';';

    /// <summary>Number of distinct turn lane types (bit positions).</summary>
    public const ushort TurnLaneTypeCount = 11;

    public const ushort TurnLaneEmpty = 0;
    public const ushort TurnLaneNone = 1 << 0;
    public const ushort TurnLaneThrough = 1 << 1;
    public const ushort TurnLaneSharpLeft = 1 << 2;
    public const ushort TurnLaneLeft = 1 << 3;
    public const ushort TurnLaneSlightLeft = 1 << 4;
    public const ushort TurnLaneSlightRight = 1 << 5;
    public const ushort TurnLaneRight = 1 << 6;
    public const ushort TurnLaneSharpRight = 1 << 7;
    public const ushort TurnLaneReverse = 1 << 8;
    public const ushort TurnLaneMergeToLeft = 1 << 9;
    public const ushort TurnLaneMergeToRight = 1 << 10;

    /// <summary>Mask -> human-readable name (mirrors kTurnLaneNames).</summary>
    public static readonly IReadOnlyDictionary<ushort, string> TurnLaneNames = new Dictionary<ushort, string>
    {
        [0] = "|",
        [TurnLaneNone] = "none",
        [TurnLaneThrough] = "through",
        [TurnLaneSharpLeft] = "sharp_left",
        [TurnLaneLeft] = "left",
        [TurnLaneSlightLeft] = "slight_left",
        [TurnLaneSlightRight] = "slight_right",
        [TurnLaneRight] = "right",
        [TurnLaneSharpRight] = "sharp_right",
        [TurnLaneReverse] = "reverse",
        [TurnLaneMergeToLeft] = "merge_to_left",
        [TurnLaneMergeToRight] = "merge_to_right",
    };

    /// <summary>Name -> mask (mirrors kTurnLaneMasks).</summary>
    public static readonly IReadOnlyDictionary<string, ushort> TurnLaneMasks = new Dictionary<string, ushort>(StringComparer.Ordinal)
    {
        ["|"] = TurnLaneEmpty,
        ["none"] = TurnLaneNone,
        ["through"] = TurnLaneThrough,
        ["sharp_left"] = TurnLaneSharpLeft,
        ["left"] = TurnLaneLeft,
        ["slight_left"] = TurnLaneSlightLeft,
        ["slight_right"] = TurnLaneSlightRight,
        ["right"] = TurnLaneRight,
        ["sharp_right"] = TurnLaneSharpRight,
        ["reverse"] = TurnLaneReverse,
        ["merge_to_left"] = TurnLaneMergeToLeft,
        ["merge_to_right"] = TurnLaneMergeToRight,
    };
}

/// <summary>
/// Holds turn lane information at the end of a directed edge. Faithful port of
/// <c>valhalla::baldr::TurnLanes</c>. Total on-disk size is 8 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TurnLanes : IComparable<TurnLanes>
{
    // Bit layout for Word0:
    //   bits 0..21  : edgeindex_ (22 bits)
    //   bits 22..31 : spare_     (10 bits)
    private const int EdgeIndexBits = 22;
    private const uint EdgeIndexMask = (1u << EdgeIndexBits) - 1u; // 0x003FFFFF

    private uint word0_;       // packed: edgeindex (22) + spare (10)
    private uint textOffset_;  // full 32-bit text offset

    /// <summary>
    /// Constructor given arguments.
    /// </summary>
    /// <param name="idx">Directed edge index to which this turn lane applies.</param>
    /// <param name="textOffset">Offset to text in the names/text table.</param>
    public TurnLanes(uint idx, uint textOffset)
    {
        word0_ = 0;
        textOffset_ = textOffset;
        EdgeIndex = idx; // packs into Word0, spare stays 0
    }

    /// <summary>
    /// Gets or sets the index of the directed edge this turn lane applies to (22-bit field).
    /// </summary>
    public uint EdgeIndex
    {
        readonly get => word0_ & EdgeIndexMask;
        set => word0_ = (word0_ & ~EdgeIndexMask) | (value & EdgeIndexMask);
    }

    /// <summary>
    /// Gets the offset into the GraphTile text list for the text associated with the turn lane.
    /// </summary>
    public readonly uint TextOffset => textOffset_;

    /// <summary>
    /// Convert a stored string into a list of turn lane masks. The pipe-separated string is split
    /// and each token parsed as an integer mask.
    /// </summary>
    /// <param name="str">Stored string to convert into a list of turn lane masks.</param>
    /// <returns>A list of turn lane masks.</returns>
    public static List<ushort> LaneMasks(string str)
    {
        // Convert the pipe separated string into lane masks. Mirrors std::getline split + stoi.
        var masks = new List<ushort>();
        foreach (string item in SplitOn(str, TurnLaneConstants.LaneDelimiter))
        {
            // C++ uses stoi(item) and pushes (uint16_t) of the result.
            masks.Add((ushort)int.Parse(item, NumberStyles.Integer, CultureInfo.InvariantCulture));
        }

        return masks;
    }

    /// <summary>
    /// Get a string with turn lanes: lanes are pipe delimited and turn lanes within a lane are
    /// ';' delimited. Faithful port of <c>turnlane_string</c>.
    /// </summary>
    /// <param name="lanemasks">List of lane masks.</param>
    /// <returns>A string depicting the turn lanes.</returns>
    public static string TurnLaneString(IReadOnlyList<ushort> lanemasks)
    {
        var turnlanes = new StringBuilder();
        if (lanemasks.Count == 0)
        {
            return turnlanes.ToString();
        }

        foreach (ushort m in lanemasks)
        {
            if (turnlanes.Length == 0 && m == 0)
            {
                turnlanes.Append(TurnLaneConstants.LaneDelimiter);
            }
            else
            {
                if (m > 0)
                {
                    var tl = new StringBuilder();
                    for (ushort i = 0; i < TurnLaneConstants.TurnLaneTypeCount; ++i)
                    {
                        if ((m & (1u << i)) != 0)
                        {
                            if (TurnLaneConstants.TurnLaneNames.TryGetValue((ushort)(1u << i), out string? str))
                            {
                                if (tl.Length != 0)
                                {
                                    tl.Append(TurnLaneConstants.TurnLaneDelimiter);
                                }

                                tl.Append(str);
                            }
                        }
                    }

                    turnlanes.Append(tl);
                }

                turnlanes.Append(TurnLaneConstants.LaneDelimiter);
            }
        }

        // C++ turnlanes.pop_back();
        turnlanes.Length -= 1;
        return turnlanes.ToString();
    }

    /// <summary>
    /// Get the pipe separated Valhalla turn lane string from the OSM turn lane string.
    /// Faithful port of <c>GetTurnLaneString</c>.
    /// </summary>
    /// <param name="osmstr">OSM turn lane string.</param>
    /// <returns>Pipe separated turn lane string (stored in Valhalla tiles).</returns>
    public static string GetTurnLaneString(string osmstr)
    {
        var tl = new StringBuilder();
        foreach (string item in SplitOn(osmstr, TurnLaneConstants.LaneDelimiter))
        {
            if (tl.Length != 0)
            {
                tl.Append(TurnLaneConstants.LaneDelimiter);
            }

            ushort lanemask = 0;
            foreach (string item2 in SplitOn(item, TurnLaneConstants.TurnLaneDelimiter))
            {
                if (TurnLaneConstants.TurnLaneMasks.TryGetValue(item2, out ushort mask))
                {
                    lanemask |= mask;
                }
            }

            // Append the numeric lane mask.
            tl.Append(lanemask.ToString(CultureInfo.InvariantCulture));
        }

        // Add an empty lane if the string ends with a delimiter. C++ uses osmstr.back().
        if (osmstr.Length > 0 && osmstr[^1] == TurnLaneConstants.LaneDelimiter)
        {
            tl.Append(TurnLaneConstants.LaneDelimiter);
            tl.Append('0');
        }

        return tl.ToString();
    }

    /// <summary>
    /// Comparison for use in sorting (mirrors C++ <c>operator&lt;</c>): sort by edge index.
    /// </summary>
    public readonly int CompareTo(TurnLanes other) => EdgeIndex.CompareTo(other.EdgeIndex);

    // Faithful reproduction of std::getline(ss, item, delim): splits on every delimiter
    // occurrence, including producing empty tokens for adjacent/leading delimiters. A trailing
    // delimiter does NOT produce a final empty token (std::getline returns false at EOF when the
    // last char was the delimiter and nothing follows). string.Split would add that trailing
    // empty token, so we implement the getline semantics explicitly.
    private static IEnumerable<string> SplitOn(string str, char delim)
    {
        int start = 0;
        for (int i = 0; i < str.Length; ++i)
        {
            if (str[i] == delim)
            {
                yield return str.Substring(start, i - start);
                start = i + 1;
            }
        }

        // Emit the final segment only if there is content after the last delimiter
        // (matches std::getline not yielding a trailing empty token).
        if (start < str.Length)
        {
            yield return str.Substring(start);
        }
    }
}
