// Faithful C# port of the GraphTileBuilder surface used by the mjolnir graph filter that is not
// already covered by the build / full-deserialize path in GraphTileBuilder.cs:
//   - the deserialized-tile read accessors node(idx) / directededge(idx) / directededges(idx) /
//     admininfo(idx) (served from the deserialized builder lists),
//   - OpposingEdgeInfoDiffers / CopyLaneConnectivityFromTile,
//   - Update(nodes, directededges) which replaces the node + directed-edge sections of a
//     deserialized tile and re-stores it to disk.
// Sources:
//   F:/github/valhalla/src/mjolnir/graphtilebuilder.cc  (Update, node, directededge, directededges,
//                                                        admininfo, CopyLaneConnectivityFromTile,
//                                                        OpposingEdgeInfoDiffers)
//
// PORT-NOTE: this builds on the full-deserialize ctor GraphTileBuilder(GraphTile) in
// GraphTileBuilder.cs. After deserialization the C++ node(idx)/directededge(idx) read from the
// read-in tile arrays (nodes_ / directededges_), which are byte-identical to the builder lists the
// deserialize ctor populates; we therefore serve them from the builder lists. The C++ Update() is a
// fast path that copies the unchanged tile tail byte-for-byte; here we replace the node + directed
// edge builder lists and re-serialize via StoreTileData(tileDir), which produces the byte-identical
// tile (the deserialize ctor + StoreTileData round-trip is lossless), so the result is the same
// bytes the C++ Update would write.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Graph-filter additions to <see cref="GraphTileBuilder"/>: the deserialized-tile read accessors,
/// opposing-edge / lane-connectivity helpers, and the in-place tile update path.
/// </summary>
public sealed partial class GraphTileBuilder
{
    /// <summary>
    /// Gets the node at the given index from the deserialized builder list. Faithful port of
    /// <c>node(size_t)</c>.
    /// </summary>
    public NodeInfo Node(int idx)
    {
        if (idx < _nodesBuilder.Count)
        {
            return _nodesBuilder[idx];
        }

        throw new InvalidOperationException("GraphTileBuilder NodeInfo index out of bounds");
    }

    /// <summary>
    /// Gets the directed edge at the given index from the deserialized builder list. Faithful port of
    /// <c>directededge(size_t)</c>.
    /// </summary>
    public DirectedEdge Directededge(int idx)
    {
        if (idx < _directedEdgesBuilder.Count)
        {
            return _directedEdgesBuilder[idx];
        }

        throw new InvalidOperationException("GraphTile DirectedEdge id out of bounds");
    }

    /// <summary>
    /// Gets the directed edge at the given index (the C++ <c>directededges(size_t)</c> returns a
    /// pointer into the builder list so callers can index following edges; the C# port returns the
    /// edge value at the index from the same builder list).
    /// </summary>
    public DirectedEdge DirectededgesPtr(int idx)
    {
        if (idx < _directedEdgesBuilder.Count)
        {
            return _directedEdgesBuilder[idx];
        }

        throw new InvalidOperationException("GraphTile DirectedEdge id out of bounds");
    }

    /// <summary>
    /// Gets the admin info at the given index, resolving the country / state names from the
    /// deserialized text list. Faithful port of the tile <c>admininfo(size_t)</c> the builder inherits.
    /// </summary>
    public AdminInfo AdminInfo(int idx)
    {
        if (idx >= _adminsBuilder.Count)
        {
            throw new InvalidOperationException("GraphTileBuilder AdminInfo index out of bounds");
        }

        Admin admin = _adminsBuilder[idx];
        return new AdminInfo(
            GetText(admin.CountryOffset),
            GetText(admin.StateOffset),
            admin.CountryIsoCode(),
            admin.StateIsoCode());
    }

    /// <summary>
    /// Copies lane connectivity from an existing tile and updates the target edge indices to match
    /// the current builder's directed edge count. Faithful port of <c>CopyLaneConnectivityFromTile</c>.
    /// </summary>
    public void CopyLaneConnectivityFromTile(GraphTile tile, uint edgeId)
    {
        IReadOnlyList<LaneConnectivity> span = tile.GetLaneConnectivity(edgeId);
        if (span.Count == 0)
        {
            // LOG_ERROR("Base edge should have lane connectivity, but none found");
        }

        var laneConnectivity = new List<LaneConnectivity>(span.Count);
        foreach (LaneConnectivity lc in span)
        {
            LaneConnectivity updated = lc;
            updated.SetTo((uint)DirectedEdges.Count);
            laneConnectivity.Add(updated);
        }

        AddLaneConnectivity(laneConnectivity);
    }

    /// <summary>
    /// Is there an opposing edge with a matching edgeinfo offset? The end node of the directed edge
    /// must be in the same tile as the directed edge. Faithful port of <c>OpposingEdgeInfoDiffers</c>.
    /// Returns true if the opposing edge info differs (no matching offset found).
    /// </summary>
    public bool OpposingEdgeInfoDiffers(GraphTile tile, DirectedEdge edge)
    {
        if (edge.EndNode.TileValue() == tile.Header().Graphid().TileValue())
        {
            // Get the nodeinfo at the end of the edge. Iterate through the directed edges and return
            // false if a matching edgeinfo offset is found.
            NodeInfo nodeinfo = tile.Node((int)edge.EndNode.Id());
            uint edgeIndex = nodeinfo.EdgeIndex;
            for (uint i = 0; i < nodeinfo.EdgeCount; i++)
            {
                DirectedEdge de = tile.DirectedEdge((int)(edgeIndex + i));
                if (de.EdgeInfoOffset == edge.EdgeInfoOffset)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Updates a deserialized tile with new nodes and directed edges (counts must match the existing
    /// tile) and re-stores it to disk. Faithful port of <c>Update</c>.
    /// </summary>
    /// <param name="tileDir">Tile directory to write to.</param>
    /// <param name="nodes">Updated list of nodes (same count as the tile).</param>
    /// <param name="directededges">Updated list of directed edges (same count as the tile).</param>
    public void Update(
        string tileDir,
        IReadOnlyList<NodeInfo> nodes,
        IReadOnlyList<DirectedEdge> directededges)
    {
        if (nodes.Count != _nodesBuilder.Count)
        {
            throw new InvalidOperationException("GraphTileBuilder.Update - node count has changed");
        }

        if (directededges.Count != _directedEdgesBuilder.Count)
        {
            throw new InvalidOperationException("GraphTileBuilder.Update - directed edge count has changed");
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            _nodesBuilder[i] = nodes[i];
        }

        for (int i = 0; i < directededges.Count; i++)
        {
            _directedEdgesBuilder[i] = directededges[i];
        }

        StoreTileData(tileDir);
    }

    // Resolves a text-list offset to its string (deserialized text list). Mirrors GraphTile.GetName.
    private string GetText(uint offset)
    {
        foreach (KeyValuePair<string, uint> kv in _textOffsetMap)
        {
            if (kv.Value == offset)
            {
                return kv.Key;
            }
        }

        return string.Empty;
    }
}
