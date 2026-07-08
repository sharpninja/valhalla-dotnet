// Faithful C# port of Valhalla thor EdgeStatus (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/thor/edgestatus.h
//
// Class to define / lookup the status and index of an edge in the edge label list during shortest
// path algorithms. Status info is stored per-tile in arrays sized to the tile's directed-edge
// count, so the path algorithms can get a reference to the first edge status and iterate it over
// sequential edges (reducing map lookups).
//
// PORT-NOTE: the C++ stores EdgeStatusInfo* arrays in an unordered_map keyed by
// (tile_value | SHIFT_path_id(path_id)) and manually new[]/delete[]s them. The C# port keeps a
// Dictionary<uint, EdgeStatusInfo[]>; arrays are GC-managed so the destructor/clear() machinery
// reduces to clearing the dictionary. EdgeStatusInfo is a value type (struct) packing index_:28 and
// set_:4, matching the C++ bitfield. GetPtr returns the backing array plus index so callers can
// mutate in place (the C# analogue of returning EdgeStatusInfo*).

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

// graph_tile_ptr alias to read like the C++ signatures.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;

namespace SharpNinja.Valhalla.Thor;

/// <summary>Edge label status. Faithful port of <c>enum class EdgeSet : uint8_t</c>.</summary>
public enum EdgeSet : byte
{
    /// <summary>
    /// Unreached - not yet encountered in search OR encountered but reset due to a complex
    /// restriction (see valhalla issue 2103).
    /// </summary>
    UnreachedOrReset = 0,

    /// <summary>Permanent - shortest path to this edge has been found.</summary>
    Permanent = 1,

    /// <summary>
    /// Temporary - edge has been encountered but there could still be a shorter path to it. This
    /// edge will be "adjacent" to an edge that is permanently labeled.
    /// </summary>
    Temporary = 2,

    /// <summary>Skipped - edge has been encountered but was thrown out of consideration.</summary>
    Skipped = 3,
}

/// <summary>
/// Stores the edge label status and its index in the EdgeLabels list. Faithful port of the C++
/// <c>struct EdgeStatusInfo</c> (index_:28, set_:4).
/// </summary>
public struct EdgeStatusInfo
{
    // index_:28, set_:4 packed into a single uint to match the C++ bitfield size/semantics.
    private uint _packed;

    /// <summary>Default constructor (index 0, set kUnreachedOrReset).</summary>
    public EdgeStatusInfo()
    {
        _packed = 0;
    }

    /// <summary>Constructor with values. Faithful port of <c>EdgeStatusInfo(set, index)</c>.</summary>
    public EdgeStatusInfo(EdgeSet set, uint index)
    {
        _packed = (index & 0x0FFFFFFFu) | (((uint)set & 0xFu) << 28);
    }

    /// <summary>Gets the index into the edge label list. Faithful port of <c>index()</c>.</summary>
    public readonly uint Index() => _packed & 0x0FFFFFFFu;

    /// <summary>Gets the edge set. Faithful port of <c>set()</c>.</summary>
    public readonly EdgeSet Set() => (EdgeSet)((_packed >> 28) & 0xFu);

    // Internal mutator used by EdgeStatus.Update (matches the C++ p->second[id].set_ = set).
    internal void SetSet(EdgeSet set) => _packed = (_packed & 0x0FFFFFFFu) | (((uint)set & 0xFu) << 28);
}

/// <summary>
/// Class to define / lookup the status and index of an edge in the edge label list during shortest
/// path algorithms. Faithful port of <c>valhalla::thor::EdgeStatus</c>.
/// </summary>
public sealed class EdgeStatus
{
    // Keys are the tile Ids (level + tile id, i.e. GraphId.TileValue()) optionally or'd with the
    // shifted path id; values are arrays of EdgeStatusInfo sized to the tile's directed-edge count.
    private readonly Dictionary<uint, EdgeStatusInfo[]> _edgestatus = new();

    // Handy macro port: shift the 7-bit path id so it can be or'd with the tile/level id.
    private static uint ShiftPathId(byte pathId) => (uint)pathId << 25;

    /// <summary>Clears the EdgeStatusInfo arrays and the edge status map. Faithful port of <c>clear()</c>.</summary>
    public void Clear() => _edgestatus.Clear();

    /// <summary>
    /// Set the status of a directed edge given its GraphId. Faithful port of
    /// <c>Set(edgeid, set, index, tile, path_id)</c>.
    /// </summary>
    /// <param name="edgeid">GraphId of the directed edge to set.</param>
    /// <param name="set">Label set for this directed edge.</param>
    /// <param name="index">Index of the edge label.</param>
    /// <param name="tile">Graph tile of the directed edge.</param>
    /// <param name="pathId">Identifies which path the edge status belongs to (0..127).</param>
    public void Set(GraphId edgeid, EdgeSet set, uint index, GraphTilePtr tile, byte pathId = 0)
    {
        if (pathId > GraphConstants.MaxMultiPathId)
        {
            throw new ArgumentOutOfRangeException(nameof(pathId));
        }

        uint key = edgeid.TileValue() | ShiftPathId(pathId);
        if (!_edgestatus.TryGetValue(key, out EdgeStatusInfo[]? arr))
        {
            arr = new EdgeStatusInfo[tile.Header().Directededgecount()];
            _edgestatus[key] = arr;
        }

        arr[edgeid.Id()] = new EdgeStatusInfo(set, index);
    }

    /// <summary>
    /// Update the status (set) of a directed edge given its GraphId. Assumes the edge id has already
    /// been encountered. Faithful port of <c>Update(edgeid, set, path_id)</c>.
    /// </summary>
    /// <param name="edgeid">GraphId of the directed edge to update.</param>
    /// <param name="set">Label set for this directed edge.</param>
    /// <param name="pathId">Identifies which path the edge status belongs to (0..127).</param>
    public void Update(GraphId edgeid, EdgeSet set, byte pathId = 0)
    {
        if (pathId > GraphConstants.MaxMultiPathId)
        {
            throw new ArgumentOutOfRangeException(nameof(pathId));
        }

        uint key = edgeid.TileValue() | ShiftPathId(pathId);
        if (_edgestatus.TryGetValue(key, out EdgeStatusInfo[]? arr))
        {
            arr[edgeid.Id()].SetSet(set);
        }
        else
        {
            throw new InvalidOperationException("EdgeStatus Update on edge not previously set");
        }
    }

    /// <summary>
    /// Get the status info of a directed edge given its GraphId. Faithful port of
    /// <c>Get(edgeid, path_id)</c>.
    /// </summary>
    /// <param name="edgeid">GraphId of the directed edge.</param>
    /// <param name="pathId">Identifies which path the edge status belongs to (0..127).</param>
    /// <returns>Returns edge status info (default/unreached if the tile is not in the map).</returns>
    public EdgeStatusInfo Get(GraphId edgeid, byte pathId = 0)
    {
        if (pathId > GraphConstants.MaxMultiPathId)
        {
            throw new ArgumentOutOfRangeException(nameof(pathId));
        }

        uint key = edgeid.TileValue() | ShiftPathId(pathId);
        return _edgestatus.TryGetValue(key, out EdgeStatusInfo[]? arr)
            ? arr[edgeid.Id()]
            : new EdgeStatusInfo();
    }

    /// <summary>
    /// Get a reference (backing array + index) to the edge status info of a directed edge. Since
    /// directed edges are stored sequentially from a node this reduces the number of lookups by
    /// edgeid. Faithful port of <c>GetPtr(edgeid, tile, path_id)</c> (the returned tuple is the C#
    /// analogue of the returned <c>EdgeStatusInfo*</c>; mutate via <c>arr[index]</c>).
    /// </summary>
    /// <param name="edgeid">GraphId of the directed edge.</param>
    /// <param name="tile">Graph tile of the directed edge.</param>
    /// <param name="pathId">Identifies which path the edge status belongs to (0..127).</param>
    /// <returns>The backing array and the index of this edge within it.</returns>
    public (EdgeStatusInfo[] Array, int Index) GetPtr(GraphId edgeid, GraphTilePtr tile, byte pathId = 0)
    {
        if (pathId > GraphConstants.MaxMultiPathId)
        {
            throw new ArgumentOutOfRangeException(nameof(pathId));
        }

        uint key = edgeid.TileValue() | ShiftPathId(pathId);
        if (!_edgestatus.TryGetValue(key, out EdgeStatusInfo[]? arr))
        {
            arr = new EdgeStatusInfo[tile.Header().Directededgecount()];
            _edgestatus[key] = arr;
        }

        return (arr, (int)edgeid.Id());
    }
}
