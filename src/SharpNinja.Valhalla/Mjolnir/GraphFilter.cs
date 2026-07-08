// Faithful C# port of Valhalla mjolnir graphfilter.cc + graphfilter.h @ 3.7.0.
// Sources:
//   F:/github/valhalla/src/mjolnir/graphfilter.cc
//   F:/github/valhalla/valhalla/mjolnir/graphfilter.h
//
// Optionally filters edges and nodes based on access. The pipeline (GraphFilter::Filter) is:
//   1. FilterTiles                      - drop edges that no enabled mode can use; rebuild each tile
//                                         (re-adding signs / turn lanes / access restrictions / lane
//                                         connectivity / edge info), record old->new node mapping,
//                                         and mark candidate aggregation nodes (mode_change flag).
//   2. UpdateEndNodes                   - remap each directed edge's end node to the new node id and
//                                         shift restriction / name-consistency masks for filtered edges.
//   3. AggregateTiles                   - validate which marked nodes can actually be aggregated, then
//                                         aggregate edges through those nodes (merge shape, length,
//                                         curvature) so degree-2 nodes left over from filtering vanish.
//   4. UpdateEndNodes                   - remap end nodes again after aggregation.
//   5. UpdateOpposingIndexAndTransitions- set opposing local edge indexes and edge transitions
//                                         (turn type / edge-to-left/right / stop impact).
//
// The tiles are read through the ported baldr GraphReader and rewritten with the ported
// GraphTileBuilder (the rebuild path + the deserialize/Update path). The written tiles are
// byte-compatible with the GraphTile reader.
//
// PORT-NOTE: the C++ boost::property_tree config is replaced by an explicit FilterConfig record
// (include_driving / include_bicycle / include_pedestrian + the GraphReader config). The
// SCOPED_TIMER / LOG_* diagnostics collapse to the returned stats. Tile removal (when all edges in a
// tile are filtered) deletes the .gph/.gph.gz file, matching the C++ ::remove.

using System;
using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Optionally filters edges and nodes based on access. Faithful port of the C++ <c>class GraphFilter</c>
/// plus the graphfilter.cc free functions.
/// </summary>
public static class GraphFilter
{
    /// <summary>Group wheelchair and pedestrian access together. Mirrors C++ <c>kAllPedestrianAccess</c>.</summary>
    public const uint AllPedestrianAccess = GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess;

    /// <summary>
    /// Configuration for <see cref="Filter"/>. Replaces the C++ <c>boost::property_tree::ptree</c>:
    /// the three mode toggles plus the tile directory the GraphReader reads from / writes to.
    /// </summary>
    public sealed class FilterConfig
    {
        /// <summary>Tile directory (the GraphReader tile_dir).</summary>
        public string TileDir { get; init; } = string.Empty;

        /// <summary>Include edge if driving (any vehicular) access in either direction. C++ <c>include_driving</c>.</summary>
        public bool IncludeDriving { get; init; } = true;

        /// <summary>Include edge if bicycle access in either direction. C++ <c>include_bicycle</c>.</summary>
        public bool IncludeBicycle { get; init; } = true;

        /// <summary>Include edge if pedestrian or wheelchair access in either direction. C++ <c>include_pedestrian</c>.</summary>
        public bool IncludePedestrian { get; init; } = true;
    }

    /// <summary>Counters mirroring the file-scope statics in graphfilter.cc.</summary>
    public sealed class FilterStats
    {
        /// <summary>Number of original edges.</summary>
        public uint OriginalEdges { get; set; }

        /// <summary>Number of original nodes.</summary>
        public uint OriginalNodes { get; set; }

        /// <summary>Number of filtered (removed) edges.</summary>
        public uint FilteredEdges { get; set; }

        /// <summary>Number of filtered (removed) nodes.</summary>
        public uint FilteredNodes { get; set; }

        /// <summary>Number of nodes marked as aggregation candidates.</summary>
        public uint CanAggregate { get; set; }

        /// <summary>Number of aggregated directed edges.</summary>
        public uint Aggregated { get; set; }
    }

    /// <summary>
    /// Optionally filter edges and nodes based on access. Faithful port of <c>GraphFilter::Filter</c>.
    /// </summary>
    /// <param name="config">Filter configuration (mode toggles + tile dir).</param>
    /// <returns>Filter statistics (also useful for tests).</returns>
    public static FilterStats Filter(FilterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var stats = new FilterStats();

        bool includeDriving = config.IncludeDriving;
        bool includeBicycle = config.IncludeBicycle;
        bool includePedestrian = config.IncludePedestrian;

        if (includeBicycle && includeDriving && includePedestrian)
        {
            // Nothing to filter!
            return stats;
        }

        // Map of old node Ids to new node Ids (after filtering).
        var oldToNew = new Dictionary<GraphId, GraphId>();

        // Map of updated local indexes at nodes where edges have been filtered.
        var updatedLocalIndexes = new Dictionary<GraphId, byte[]>();

        // Construct GraphReader.
        var reader = new GraphReader(new GraphReader.Config { TileDir = config.TileDir });

        // Filter edges (and nodes) by access.
        FilterTiles(reader, config.TileDir, oldToNew, updatedLocalIndexes,
            includeDriving, includeBicycle, includePedestrian, stats);

        // Update end nodes. Clear the GraphReader cache first.
        reader.Clear();
        UpdateEndNodes(reader, config.TileDir, oldToNew, updatedLocalIndexes);

        reader.Clear();
        oldToNew.Clear();
        AggregateTiles(reader, config.TileDir, oldToNew, stats);

        // Update end nodes. Clear the GraphReader cache first.
        reader.Clear();
        // Only update the indexes once.
        updatedLocalIndexes.Clear();
        UpdateEndNodes(reader, config.TileDir, oldToNew, updatedLocalIndexes);

        // Update Opposing Edge Index. Clear the GraphReader cache first.
        reader.Clear();
        UpdateOpposingIndexAndTransitions(reader, config.TileDir);

        return stats;
    }

    // ------------------------------------------------------------------
    // get_new_mask / CanAggregate / get_hierarchy_rc (anonymous-namespace helpers)
    // ------------------------------------------------------------------

    /// <summary>
    /// For each bit set in <paramref name="oldMask"/>, update a bit in the new mask using
    /// <paramref name="newLocalIndexes"/>. Faithful port of <c>get_new_mask</c>.
    /// </summary>
    private static byte GetNewMask(byte oldMask, IReadOnlyList<byte> newLocalIndexes)
    {
        int n = Math.Min(8, newLocalIndexes.Count);
        byte newMask = 0;
        for (int i = 0; i < n; ++i)
        {
            if ((oldMask & (1 << i)) != 0 && newLocalIndexes[i] != 255)
            {
                // Replace bit set in the old mask with one from new_local_indexes.
                byte index = newLocalIndexes[i];
                newMask |= (byte)(1 << index);
            }
        }

        return newMask;
    }

    /// <summary>Can this edge be aggregated (no special restriction / signal attributes)? Faithful port of <c>CanAggregate</c>.</summary>
    private static bool CanAggregate(DirectedEdge de)
    {
        if (de.StartRestriction != 0 || de.PartOfComplexRestriction || de.EndRestriction != 0 ||
            de.Restrictions != 0 || de.TrafficSignal || de.AccessRestriction != 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Links and ferry-connecting edges hijack the road class for hierarchy purposes; respect that
    /// while aggregating. Faithful port of <c>get_hierarchy_rc</c>.
    /// </summary>
    private static RoadClass GetHierarchyRc(DirectedEdge de)
        => de.IsShortcut ? (RoadClass)de.Shortcut : de.Classification;

    // ------------------------------------------------------------------
    // FilterTiles
    // ------------------------------------------------------------------

    private static void FilterTiles(
        GraphReader reader,
        string tileDir,
        Dictionary<GraphId, GraphId> oldToNew,
        Dictionary<GraphId, byte[]> updatedLocalIndexes,
        bool includeDriving,
        bool includeBicycle,
        bool includePedestrian,
        FilterStats stats)
    {
        // lambda to check if an edge should be included.
        bool IncludeEdge(DirectedEdge edge)
        {
            bool bicycleAccess =
                (edge.ForwardAccess & GraphConstants.BicycleAccess) != 0 ||
                (edge.ReverseAccess & GraphConstants.BicycleAccess) != 0;
            bool pedestrianAccess =
                (edge.ForwardAccess & AllPedestrianAccess) != 0 ||
                (edge.ReverseAccess & AllPedestrianAccess) != 0;
            bool drivingAccess =
                (edge.ForwardAccess & GraphConstants.VehicularAccess) != 0 ||
                (edge.ReverseAccess & GraphConstants.VehicularAccess) != 0;
            return (drivingAccess && includeDriving) || (bicycleAccess && includeBicycle) ||
                   (pedestrianAccess && includePedestrian);
        }

        // Iterate through all tiles in the local level.
        HashSet<GraphId> localTiles = reader.GetTileSet(TileHierarchy.Levels()[^1].Level);
        foreach (GraphId tileId in localTiles)
        {
            // Get the graph tile. Read from this tile to create the new tile.
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            // Create a new tilebuilder - copy header information from the existing tile.
            var tilebuilder = NewRebuildBuilder(tileId, tile);
            stats.OriginalNodes += tile.Header().Nodecount();
            stats.OriginalEdges += tile.Header().Directededgecount();

            var nodeid = new GraphId(tileId.Tileid(), tileId.Level(), 0);
            for (uint i = 0; i < tile.Header().Nodecount(); ++i, nodeid = Increment(nodeid))
            {
                bool diffNames = false;
                bool diffTile = false;
                bool edgeFiltered = false;
                // Count of edges added for this node.
                uint edgeCount = 0;

                // Current edge index for the first edge from this node.
                uint edgeIndex = (uint)tilebuilder.DirectedEdges.Count;

                byte newEdgeCount = 0;
                const byte removedIndex = 255;
                var newLocalIndexes = new List<byte>();

                // Iterate through directed edges outbound from this node.
                var wayid = new List<ulong>();
                var classification = new List<RoadClass>();
                var endnode = new List<GraphId>();
                NodeInfo nodeinfo = tile.Node((int)nodeid.Id());
                var headings = new List<uint>();
                var traversabilities = new List<Traversability>();
                var edgeid = new GraphId(nodeid.Tileid(), nodeid.Level(), nodeinfo.EdgeIndex);
                for (uint j = 0; j < nodeinfo.EdgeCount; ++j, edgeid = Increment(edgeid))
                {
                    // Check if the directed edge should be included.
                    DirectedEdge directededge = tile.DirectedEdge(edgeid);
                    if (!IncludeEdge(directededge))
                    {
                        ++stats.FilteredEdges;
                        edgeFiltered = true;
                        newLocalIndexes.Add(removedIndex);
                        continue;
                    }

                    newLocalIndexes.Add(newEdgeCount);
                    newEdgeCount++;

                    // Copy the directed edge information.
                    DirectedEdge newedge = directededge;

                    // Set opposing edge indexes to 0 (gets set in graph validator).
                    newedge.SetOppIndex(0);

                    // Update the local edge index.
                    newedge.SetLocalEdgeIdx(j);

                    // Add heading and traversability to a temporary list so we can update nodeinfo
                    // headings to account for removed edges.
                    if (j < NodeInfo.MaxLocalEdgeIndex)
                    {
                        headings.Add(nodeinfo.Heading(j));
                        traversabilities.Add(nodeinfo.LocalDriveability(j));
                    }
                    else if (headings.Count < NodeInfo.MaxLocalEdgeIndex)
                    {
                        // Heading is not stored for the node. Compute it from the shape.
                        var shape = new List<PointLL>(tile.EdgeInfo(directededge).Shape());
                        if (!directededge.Forward)
                        {
                            shape.Reverse();
                        }

                        uint heading = (uint)Math.Round(
                            PointLL.HeadingAlongPolyline(
                                shape,
                                GraphConstants.GetOffsetForHeading(GetHierarchyRc(directededge), directededge.Use)),
                            MidpointRounding.AwayFromZero);
                        headings.Add(heading);

                        // Set traversability for autos.
                        Traversability traversability;
                        if ((directededge.ForwardAccess & GraphConstants.AutoAccess) != 0)
                        {
                            traversability = (directededge.ReverseAccess & GraphConstants.AutoAccess) != 0
                                ? Traversability.Both
                                : Traversability.Forward;
                        }
                        else
                        {
                            traversability = (directededge.ReverseAccess & GraphConstants.AutoAccess) != 0
                                ? Traversability.Backward
                                : Traversability.None;
                        }

                        traversabilities.Add(traversability);
                    }

                    // Get signs from the base directed edge.
                    if (directededge.Sign)
                    {
                        List<SignInfo> signs = tile.GetSigns(edgeid.Id());
                        tilebuilder.AddSigns((uint)tilebuilder.DirectedEdges.Count, signs);
                    }

                    // Get turn lanes from the base directed edge.
                    if (directededge.TurnLanes)
                    {
                        uint offset = tile.TurnLanesOffset(edgeid.Id());
                        tilebuilder.AddTurnLanes((uint)tilebuilder.DirectedEdges.Count, tile.GetName(offset));
                    }

                    // Get access restrictions from the base directed edge. Add these to the list of
                    // access restrictions in the new tile, updating the edge index to be the current
                    // directed edge id.
                    if (directededge.AccessRestriction != 0)
                    {
                        (IReadOnlyList<AccessRestriction> restrictions, _) = tile.GetAccessRestrictions(edgeid.Id());
                        foreach (AccessRestriction res in restrictions)
                        {
                            tilebuilder.AddAccessRestriction(new AccessRestriction(
                                (uint)tilebuilder.DirectedEdges.Count, res.Type(), res.Modes(), res.Value(),
                                res.ExceptDestination()));
                        }
                    }

                    // Copy lane connectivity.
                    if (directededge.LaneConnectivity)
                    {
                        tilebuilder.CopyLaneConnectivityFromTile(tile, edgeid.Id());
                    }

                    // Names can be different in the forward and backward direction.
                    diffNames = tilebuilder.OpposingEdgeInfoDiffers(tile, directededge);

                    // Get edge info, shape, and names from the old tile and add to the new. Cannot use
                    // edge info offset since edges in arterial/highway hierarchy can cross base tiles;
                    // use a hash based on the encoded shape plus way Id.
                    EdgeInfo edgeinfo = tile.EdgeInfo(directededge);
                    string encodedShape = edgeinfo.EncodedShape();
                    uint w = Hash(encodedShape + edgeinfo.WayId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    uint edgeInfoOffset = tilebuilder.AddEdgeInfo(
                        w, nodeid, directededge.EndNode, edgeinfo.WayId, edgeinfo.MeanElevation,
                        edgeinfo.BikeNetwork, edgeinfo.SpeedLimit, DecodeShape(encodedShape),
                        edgeinfo.GetNames(), edgeinfo.GetTaggedValues(), edgeinfo.GetLinguisticTaggedValues(),
                        edgeinfo.GetTypes(), out _, diffNames);
                    newedge.SetEdgeInfoOffset(edgeInfoOffset);
                    wayid.Add(edgeinfo.WayId);
                    classification.Add(GetHierarchyRc(directededge));
                    endnode.Add(directededge.EndNode);

                    if (directededge.EndNode.TileValue() != tile.Header().Graphid().TileValue())
                    {
                        diffTile = true;
                    }

                    // Add directed edge.
                    tilebuilder.DirectedEdges.Add(newedge);
                    ++edgeCount;
                }

                // Add the node to the tilebuilder unless no edges remain.
                if (edgeCount > 0)
                {
                    // Add a node builder to the tile. Update the edge count and edge index.
                    var newNode = new GraphId(nodeid.Tileid(), nodeid.Level(), (uint)tilebuilder.Nodes.Count);
                    NodeInfo node = nodeinfo;
                    node.SetEdgeCount(edgeCount);
                    node.SetLocalEdgeCount(edgeCount);
                    node.SetEdgeIndex(edgeIndex);
                    AdminInfo admin = tile.AdminInfo((int)nodeinfo.AdminIndex);
                    node.SetAdminIndex((ushort)tilebuilder.AddAdmin(
                        admin.CountryText, admin.StateText, admin.CountryIso, admin.StateIso));

                    // Update headings to account for removed edges.
                    for (int h = 0; h < headings.Count; ++h)
                    {
                        node.SetHeading((uint)h, headings[h]);
                        node.SetLocalDriveability((uint)h, traversabilities[h]);
                    }

                    // Get named signs from the base node.
                    if (nodeinfo.NamedIntersection)
                    {
                        List<SignInfo> signs = tile.GetSigns(nodeid.Id(), true);
                        node.SetNamedIntersection(true);
                        tilebuilder.Nodes.Add(node);
                        tilebuilder.AddSigns((uint)(tilebuilder.Nodes.Count - 1), signs);
                    }
                    else
                    {
                        tilebuilder.Nodes.Add(node);
                    }

                    // Associate the old node to the new node.
                    oldToNew[nodeid] = newNode;

                    // If any edges from this node have been filtered, add new_local_indexes to the map.
                    if (edgeFiltered)
                    {
                        updatedLocalIndexes[nodeid] = newLocalIndexes.ToArray();
                    }

                    // Check if edges at this node can be aggregated. Only 2 edges, same way Id (so that
                    // edge attributes match), don't end at the same node (no loops), no traffic signal,
                    // no signs exist at the node (named_intersection), no different names, and the end
                    // node of the edges are not in a different tile.
                    if (edgeFiltered && edgeCount == 2 && wayid[0] == wayid[1] &&
                        classification[0] == classification[1] && endnode[0] != endnode[1] &&
                        !nodeinfo.TrafficSignal && !nodeinfo.NamedIntersection && !diffNames && !diffTile)
                    {
                        // One more check on intersection and node type. Similar to shortcuts.
                        bool aggregate =
                            nodeinfo.Intersection != IntersectionType.Fork &&
                            nodeinfo.Type != NodeType.Gate && nodeinfo.Type != NodeType.TollBooth &&
                            nodeinfo.Type != NodeType.TollGantry && nodeinfo.Type != NodeType.Bollard &&
                            nodeinfo.Type != NodeType.SumpBuster && nodeinfo.Type != NodeType.BorderControl;

                        if (aggregate)
                        {
                            // Temporarily used to check aggregating edges from this node.
                            NodeInfo last = tilebuilder.Nodes[^1];
                            last.SetModeChange(true);
                            tilebuilder.Nodes[^1] = last;
                            ++stats.CanAggregate;
                        }
                    }
                }
                else
                {
                    ++stats.FilteredNodes;
                }
            }

            // Store the updated tile data (or remove the tile if all edges were filtered).
            if (tilebuilder.Nodes.Count > 0)
            {
                tilebuilder.StoreTileData(tileDir);
            }
            else
            {
                RemoveTile(tileDir, tileId);
            }

            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }
    }

    // ------------------------------------------------------------------
    // Aggregation expansion (recursive helpers)
    // ------------------------------------------------------------------

    private static bool ExpandFromNodeInner(
        GraphReader reader,
        List<PointLL> shape,
        ref GraphId en,
        GraphId fromNode,
        HashSet<string> isos,
        bool forward,
        HashSet<GraphId> visitedNodes,
        ref ulong wayId,
        GraphTile prevTile,
        GraphId prevNode,
        GraphId currentNode,
        NodeInfo nodeInfo,
        RoadClass rc,
        bool validate,
        FilterStats stats)
    {
        for (uint j = 0; j < nodeInfo.EdgeCount; ++j)
        {
            var edgeId = new GraphId(prevTile.Id().Tileid(), prevTile.Id().Level(), nodeInfo.EdgeIndex + j);
            DirectedEdge de = prevTile.DirectedEdge(edgeId);
            EdgeInfo edgeInfo = prevTile.EdgeInfo(de);

            GraphTile tile = prevTile;
            if (tile.Id() != de.EndNode.TileBase())
            {
                GraphTile? t = reader.GetGraphTile(de.EndNode);
                if (t is null)
                {
                    continue;
                }

                tile = t;
            }

            NodeInfo enInfo = tile.Node((int)de.EndNode.Id());

            // Check the direction, if we looped back, or are we done.
            if (de.EndNode != prevNode && de.Forward == forward &&
                (de.EndNode != fromNode || (de.EndNode == fromNode && visitedNodes.Count > 1)))
            {
                if (edgeInfo.WayId == wayId &&
                    (enInfo.ModeChange || (nodeInfo.ModeChange && !enInfo.ModeChange)))
                {
                    // If this edge has special attributes, then we can't aggregate.
                    if (!CanAggregate(de) || GetHierarchyRc(de) != rc)
                    {
                        wayId = 0;
                        return false;
                    }

                    if (validate)
                    {
                        if (isos.Count >= 1)
                        {
                            isos.Add(tile.Admin((int)enInfo.AdminIndex).CountryIsoCode());
                        }
                    }
                    else
                    {
                        var edgeShape = Encoded.Decode7(edgeInfo.EncodedShape());
                        if (!de.Forward)
                        {
                            edgeShape.Reverse();
                        }

                        // Append shape. Skip the first point since it should equal the last of the
                        // prior edge.
                        for (int s = 1; s < edgeShape.Count; ++s)
                        {
                            shape.Add(edgeShape[s]);
                        }
                    }

                    // Found a node that does not have aggregation marked (using mode_change flag);
                    // we are done.
                    if (nodeInfo.ModeChange && !enInfo.ModeChange)
                    {
                        en = de.EndNode;
                        stats.Aggregated++;
                        return true;
                    }

                    stats.Aggregated++;

                    if (!visitedNodes.Contains(de.EndNode))
                    {
                        visitedNodes.Add(de.EndNode);

                        // Expand with the same way_id.
                        bool found = ExpandFromNode(reader, shape, ref en, fromNode, isos, forward,
                            visitedNodes, ref wayId, tile, currentNode, de.EndNode, rc, validate, stats);
                        if (found)
                        {
                            return true;
                        }

                        visitedNodes.Remove(de.EndNode);
                    }
                }
            }
        }

        return false;
    }

    private static bool ExpandFromNode(
        GraphReader reader,
        List<PointLL> shape,
        ref GraphId en,
        GraphId fromNode,
        HashSet<string> isos,
        bool forward,
        HashSet<GraphId> visitedNodes,
        ref ulong wayId,
        GraphTile prevTile,
        GraphId prevNode,
        GraphId currentNode,
        RoadClass rc,
        bool validate,
        FilterStats stats)
    {
        GraphTile tile = prevTile;
        if (tile.Id() != currentNode.TileBase())
        {
            GraphTile? t = reader.GetGraphTile(currentNode);
            if (t is null)
            {
                return false;
            }

            tile = t;
        }

        NodeInfo nodeInfo = tile.Node((int)currentNode.Id());
        // Expand from the current node.
        return ExpandFromNodeInner(reader, shape, ref en, fromNode, isos, forward, visitedNodes, ref wayId,
            tile, prevNode, currentNode, nodeInfo, rc, validate, stats);
    }

    private static bool Aggregate(
        ref GraphId startNode,
        GraphReader reader,
        List<PointLL> shape,
        ref GraphId en,
        GraphId fromNode,
        ref ulong wayId,
        HashSet<string> isos,
        RoadClass rc,
        bool forward,
        bool validate,
        FilterStats stats)
    {
        GraphTile? tile = reader.GetGraphTile(startNode);
        if (tile is null)
        {
            return false;
        }

        var visitedNodes = new HashSet<GraphId> { startNode };
        return ExpandFromNode(reader, shape, ref en, fromNode, isos, forward, visitedNodes, ref wayId,
            tile, GraphId.Invalid, startNode, rc, validate, stats);
    }

    private static void GetAggregatedData(
        GraphReader reader,
        List<PointLL> shape,
        ref GraphId en,
        GraphId fromNode,
        GraphTile tile,
        DirectedEdge directededge,
        FilterStats stats)
    {
        var isos = new HashSet<string>();
        bool isForward = directededge.Forward;
        GraphId id = directededge.EndNode;
        if (!isForward)
        {
            shape.Reverse();
        }

        // Walk in the correct direction.
        ulong wayid = tile.EdgeInfo(directededge).WayId;
        if (Aggregate(ref id, reader, shape, ref en, fromNode, ref wayid, isos,
                GetHierarchyRc(directededge), isForward, false, stats))
        {
            stats.Aggregated++; // count the current edge
            // Flip the shape back for storing in edgeinfo.
            if (!isForward)
            {
                shape.Reverse();
            }
        }
    }

    // If we cross into another country we can't aggregate the edges as access can differ; bollards or
    // gates could also block access. We also handle islands created by tossing pedestrian edges.
    private static void ValidateData(
        GraphReader reader,
        List<PointLL> shape,
        ref GraphId en,
        HashSet<GraphId> processedNodes,
        HashSet<ulong> noAggWays,
        GraphId fromNode,
        GraphTile tile,
        DirectedEdge directededge,
        FilterStats stats)
    {
        // Get the tile at the end node. Skip if the node is in another tile (mode_change is not set
        // for end nodes that are in different tiles).
        if (directededge.EndNode.TileValue() == tile.Header().Graphid().TileValue())
        {
            EdgeInfo edgeinfo = tile.EdgeInfo(directededge);
            NodeInfo enInfo = tile.Node((int)directededge.EndNode.Id());
            NodeInfo snInfo = tile.Node((int)fromNode.Id());

            if (enInfo.ModeChange)
            {
                // If this edge has special attributes, then we can't aggregate.
                if (!CanAggregate(directededge))
                {
                    processedNodes.Add(directededge.EndNode);
                    noAggWays.Add(edgeinfo.WayId);
                    return;
                }

                var isos = new HashSet<string>();
                bool isForward = directededge.Forward;
                GraphId id = directededge.EndNode;

                isos.Add(tile.Admin((int)snInfo.AdminIndex).CountryIsoCode()); // start node
                isos.Add(tile.Admin((int)enInfo.AdminIndex).CountryIsoCode()); // end node

                if (isos.Count > 1)
                {
                    // already in a diff country
                    processedNodes.Add(directededge.EndNode);
                    return;
                }

                // Walk in the correct direction.
                ulong wayid = edgeinfo.WayId;
                if (!Aggregate(ref id, reader, shape, ref en, fromNode, ref wayid, isos,
                        GetHierarchyRc(directededge), isForward, true, stats))
                {
                    if (wayid == 0)
                    {
                        // This edge has special attributes, we can't aggregate.
                        noAggWays.Add(edgeinfo.WayId);
                    }

                    processedNodes.Add(directededge.EndNode); // turn off so that we don't fail
                }
                else if (isos.Count > 1)
                {
                    // in diff country
                    processedNodes.Add(directededge.EndNode);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // AggregateTiles
    // ------------------------------------------------------------------

    private static void AggregateTiles(
        GraphReader reader,
        string tileDir,
        Dictionary<GraphId, GraphId> oldToNew,
        FilterStats stats)
    {
        // Iterate through all tiles in the local level (validation pass).
        HashSet<GraphId> localTiles = reader.GetTileSet(TileHierarchy.Levels()[^1].Level);
        foreach (GraphId tileId in localTiles)
        {
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            var processedNodes = new HashSet<GraphId>();
            var noAggWays = new HashSet<ulong>();

            var nodeid = new GraphId(tileId.Tileid(), tileId.Level(), 0);
            for (uint i = 0; i < tile.Header().Nodecount(); ++i, nodeid = Increment(nodeid))
            {
                NodeInfo nodeinfo = tile.Node((int)i);
                uint idx = nodeinfo.EdgeIndex;
                for (uint j = 0; j < nodeinfo.EdgeCount; j++, idx++)
                {
                    DirectedEdge directededge = tile.DirectedEdge((int)idx);
                    if (!processedNodes.Contains(nodeid))
                    {
                        GraphId en = directededge.EndNode;
                        var shape = new List<PointLL>();
                        // Check if we can aggregate the edges at this node.
                        ValidateData(reader, shape, ref en, processedNodes, noAggWays, nodeid, tile,
                            directededge, stats);
                    }
                }
            }

            // Now loop again double checking the ways.
            nodeid = new GraphId(tileId.Tileid(), tileId.Level(), 0);
            for (uint i = 0; i < tile.Header().Nodecount(); ++i, nodeid = Increment(nodeid))
            {
                NodeInfo nodeinfo = tile.Node((int)i);
                uint idx = nodeinfo.EdgeIndex;
                for (uint j = 0; j < nodeinfo.EdgeCount; j++, idx++)
                {
                    DirectedEdge directededge = tile.DirectedEdge((int)idx);
                    if (noAggWays.Contains(tile.EdgeInfo(directededge).WayId))
                    {
                        processedNodes.Add(directededge.EndNode);
                    }
                }
            }

            // Create a new tile builder (deserialize) and turn off mode_change for non-aggregatable
            // nodes, then write the updated nodes + (unchanged) edges.
            var tilebuilder = new GraphTileBuilder(tile);
            var nodes = new List<NodeInfo>((int)tile.Header().Nodecount());
            var directededges = new List<DirectedEdge>((int)tile.Header().Directededgecount());

            // Copy edges (they do not change).
            for (uint e = 0; e < tile.Header().Directededgecount(); ++e)
            {
                directededges.Add(tile.DirectedEdge((int)e));
            }

            nodeid = new GraphId(tileId.Tileid(), tileId.Level(), 0);
            for (uint i = 0; i < tile.Header().Nodecount(); ++i, nodeid = Increment(nodeid))
            {
                NodeInfo nodeinfo = tilebuilder.Node((int)i);
                bool found = processedNodes.Contains(nodeid);

                // We can not aggregate at this node. Turn off the mode change (aggregation) bit.
                if (found)
                {
                    nodeinfo.SetModeChange(false);
                }

                nodes.Add(nodeinfo);
            }

            tilebuilder.Update(tileDir, nodes, directededges);

            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }

        // Aggregating edges.
        reader.Clear();
        localTiles = reader.GetTileSet(TileHierarchy.Levels()[^1].Level);
        foreach (GraphId tileId in localTiles)
        {
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            var tilebuilder = NewRebuildBuilder(tileId, tile);

            var nodeid = new GraphId(tileId.Tileid(), tileId.Level(), 0);
            for (uint i = 0; i < tile.Header().Nodecount(); ++i, nodeid = Increment(nodeid))
            {
                bool diffNames = false;

                // Count of edges added for this node.
                uint edgeCount = 0;

                // Current edge index for the first edge from this node.
                uint edgeIndex = (uint)tilebuilder.DirectedEdges.Count;

                NodeInfo nodeinfo = tile.Node((int)nodeid.Id());

                // Nodes marked with mode_change = true are tossed.
                if (nodeinfo.ModeChange)
                {
                    continue;
                }

                var edgeid = new GraphId(nodeid.Tileid(), nodeid.Level(), nodeinfo.EdgeIndex);
                for (uint j = 0; j < nodeinfo.EdgeCount; ++j, edgeid = Increment(edgeid))
                {
                    DirectedEdge directededge = tile.DirectedEdge(edgeid);

                    // Copy the directed edge information.
                    DirectedEdge newedge = directededge;

                    // Set opposing edge indexes to 0 (gets set in graph validator).
                    newedge.SetOppIndex(0);

                    // Update the local edge index.
                    newedge.SetLocalEdgeIdx(j);

                    // Get signs from the base directed edge.
                    if (directededge.Sign)
                    {
                        List<SignInfo> signs = tile.GetSigns(edgeid.Id());
                        tilebuilder.AddSigns((uint)tilebuilder.DirectedEdges.Count, signs);
                    }

                    // Get turn lanes from the base directed edge.
                    if (directededge.TurnLanes)
                    {
                        uint offset = tile.TurnLanesOffset(edgeid.Id());
                        tilebuilder.AddTurnLanes((uint)tilebuilder.DirectedEdges.Count, tile.GetName(offset));
                    }

                    // Get access restrictions from the base directed edge.
                    if (directededge.AccessRestriction != 0)
                    {
                        (IReadOnlyList<AccessRestriction> restrictions, _) = tile.GetAccessRestrictions(edgeid.Id());
                        foreach (AccessRestriction res in restrictions)
                        {
                            tilebuilder.AddAccessRestriction(new AccessRestriction(
                                (uint)tilebuilder.DirectedEdges.Count, res.Type(), res.Modes(), res.Value(),
                                res.ExceptDestination()));
                        }
                    }

                    // Copy lane connectivity.
                    if (directededge.LaneConnectivity)
                    {
                        tilebuilder.CopyLaneConnectivityFromTile(tile, edgeid.Id());
                    }

                    // Names can be different in the forward and backward direction.
                    diffNames = tilebuilder.OpposingEdgeInfoDiffers(tile, directededge);

                    EdgeInfo edgeinfo = tile.EdgeInfo(directededge);
                    string encodedShape = edgeinfo.EncodedShape();
                    var shape = Encoded.Decode7(encodedShape);

                    // Aggregate if the end node is marked and in the same tile.
                    bool aggregated = false;
                    GraphId en = directededge.EndNode;

                    if (en.TileValue() == tileId.TileValue())
                    {
                        if (tile.Node((int)en.Id()).ModeChange)
                        {
                            GetAggregatedData(reader, shape, ref en, nodeid, tile, directededge, stats);
                            newedge.SetEndNode(en);
                            aggregated = true;
                        }
                    }

                    encodedShape = Encoded.Encode7(shape);
                    uint w = Hash(encodedShape + edgeinfo.WayId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    uint edgeInfoOffset = tilebuilder.AddEdgeInfo(
                        w, nodeid, en, edgeinfo.WayId, edgeinfo.MeanElevation, edgeinfo.BikeNetwork,
                        edgeinfo.SpeedLimit, shape, edgeinfo.GetNames(), edgeinfo.GetTaggedValues(),
                        edgeinfo.GetLinguisticTaggedValues(), edgeinfo.GetTypes(), out _, diffNames);
                    newedge.SetEdgeInfoOffset(edgeInfoOffset);

                    // Update length and curvature if the edge was aggregated.
                    if (aggregated)
                    {
                        newedge.SetLength((uint)PointLlPolyline2.Length(shape));
                        newedge.SetCurvature(GraphBuilder.ComputeCurvature(shape));
                    }

                    // Add directed edge.
                    tilebuilder.DirectedEdges.Add(newedge);
                    ++edgeCount;
                }

                // Add the node to the tilebuilder unless no edges remain.
                if (edgeCount > 0)
                {
                    var newNode = new GraphId(nodeid.Tileid(), nodeid.Level(), (uint)tilebuilder.Nodes.Count);
                    NodeInfo node = nodeinfo;
                    node.SetEdgeCount(edgeCount);
                    node.SetLocalEdgeCount(edgeCount);
                    node.SetEdgeIndex(edgeIndex);
                    AdminInfo admin = tile.AdminInfo((int)nodeinfo.AdminIndex);
                    node.SetAdminIndex((ushort)tilebuilder.AddAdmin(
                        admin.CountryText, admin.StateText, admin.CountryIso, admin.StateIso));

                    // Get named signs from the base node.
                    if (nodeinfo.NamedIntersection)
                    {
                        List<SignInfo> signs = tile.GetSigns(nodeid.Id(), true);
                        node.SetNamedIntersection(true);
                        tilebuilder.Nodes.Add(node);
                        tilebuilder.AddSigns((uint)(tilebuilder.Nodes.Count - 1), signs);
                    }
                    else
                    {
                        tilebuilder.Nodes.Add(node);
                    }

                    // Associate the old node to the new node.
                    oldToNew[nodeid] = newNode;
                }
            }

            // Store the updated tile data (or remove the tile if all edges are filtered).
            if (tilebuilder.Nodes.Count > 0)
            {
                tilebuilder.StoreTileData(tileDir);
            }
            else
            {
                RemoveTile(tileDir, tileId);
            }

            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }
    }

    // ------------------------------------------------------------------
    // UpdateEndNodes
    // ------------------------------------------------------------------

    private static void UpdateEndNodes(
        GraphReader reader,
        string tileDir,
        Dictionary<GraphId, GraphId> oldToNew,
        Dictionary<GraphId, byte[]> updatedLocalIndexes)
    {
        HashSet<GraphId> localTiles = reader.GetTileSet(TileHierarchy.Levels()[^1].Level);
        foreach (GraphId tileId in localTiles)
        {
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            var tilebuilder = new GraphTileBuilder(tile);

            // Copy nodes (they do not change).
            var nodes = new List<NodeInfo>((int)tile.Header().Nodecount());
            for (uint i = 0; i < tile.Header().Nodecount(); ++i)
            {
                nodes.Add(tile.Node((int)i));
            }

            // Iterate through all directed edges - update end nodes.
            var directededges = new List<DirectedEdge>((int)tile.Header().Directededgecount());
            for (uint j = 0; j < tile.Header().Directededgecount(); ++j)
            {
                DirectedEdge edge = tile.DirectedEdge((int)j);

                // Check if the end node has updated local indexes (any edges filtered).
                byte newRestrictions = (byte)edge.Restrictions;
                byte newNameConsistency = edge.NameConsistency;
                if (updatedLocalIndexes.TryGetValue(edge.EndNode, out byte[]? indexes))
                {
                    byte oldMask = (byte)edge.Restrictions;
                    if (oldMask != 0)
                    {
                        newRestrictions = GetNewMask(oldMask, indexes);
                    }

                    oldMask = edge.NameConsistency;
                    if (oldMask != 0)
                    {
                        newNameConsistency = GetNewMask(oldMask, indexes);
                    }
                }

                // Find the end node in the old_to_new mapping.
                GraphId endNode = GraphId.Invalid;
                if (oldToNew.TryGetValue(edge.EndNode, out GraphId mapped))
                {
                    endNode = mapped;
                }

                // Copy the edge and update the end node.
                DirectedEdge newEdge = edge;
                newEdge.SetEndNode(endNode);

                // Update name consistency and restrictions.
                newEdge.SetRestrictions(newRestrictions);
                newEdge.SetNameConsistency(newNameConsistency);
                directededges.Add(newEdge);
            }

            // Update the tile with new directededges.
            tilebuilder.Update(tileDir, nodes, directededges);

            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }
    }

    // ------------------------------------------------------------------
    // UpdateOpposingIndexAndTransitions
    // ------------------------------------------------------------------

    private static void UpdateOpposingIndexAndTransitions(GraphReader reader, string tileDir)
    {
        var stats = new EnhancerStats();
        HashSet<GraphId> localTiles = reader.GetTileSet(TileHierarchy.Levels()[^1].Level);
        foreach (GraphId tileId in localTiles)
        {
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            var tilebuilder = new GraphTileBuilder(tile);

            // Copy nodes (they do not change).
            var nodes = new List<NodeInfo>((int)tile.Header().Nodecount());
            for (uint i = 0; i < tile.Header().Nodecount(); ++i)
            {
                nodes.Add(tile.Node((int)i));
            }

            // Iterate through all directed edges - update opposing index + transitions.
            var directededges = new List<DirectedEdge>((int)tile.Header().Directededgecount());

            // The full set of (unchanged) directed edges from the builder, used as the "edges" array
            // for the transition logic (indexed by edge_index + j).
            var allEdges = new List<DirectedEdge>((int)tile.Header().Directededgecount());
            for (uint e = 0; e < tile.Header().Directededgecount(); ++e)
            {
                allEdges.Add(tilebuilder.DirectededgesPtr((int)e));
            }

            var nodeid = new GraphId(tileId.Tileid(), tileId.Level(), 0);
            for (uint i = 0; i < tile.Header().Nodecount(); ++i, nodeid = Increment(nodeid))
            {
                NodeInfo nodeinfo = tile.Node((int)nodeid.Id());
                var edgeid = new GraphId(nodeid.Tileid(), nodeid.Level(), nodeinfo.EdgeIndex);

                // edges = tilebuilder.directededges(nodeinfo.edge_index()): the edges at this node.
                var edges = new List<DirectedEdge>((int)nodeinfo.EdgeCount);
                for (uint e = 0; e < nodeinfo.EdgeCount; ++e)
                {
                    edges.Add(allEdges[(int)(nodeinfo.EdgeIndex + e)]);
                }

                for (uint j = 0; j < nodeinfo.EdgeCount; ++j, edgeid = Increment(edgeid))
                {
                    DirectedEdge edge = tile.DirectedEdge(edgeid);

                    // Copy the edge and update the opposing index / transitions.
                    DirectedEdge newEdge = edge;

                    // Get the tile at the end node.
                    GraphTile endnodetile;
                    if (tile.Id() == edge.EndNode.TileBase())
                    {
                        endnodetile = tile;
                    }
                    else
                    {
                        GraphTile? t = reader.GetGraphTile(edge.EndNode);
                        if (t is null)
                        {
                            directededges.Add(newEdge);
                            continue;
                        }

                        endnodetile = t;
                    }

                    // Set the opposing index on the local level.
                    newEdge.SetOppLocalIdx(MjolnirUtil.GetOpposingEdgeIndex(endnodetile, nodeid, tile, edge));

                    // Set edge transitions.
                    if (j < GraphConstants.NumberOfEdgeTransitions)
                    {
                        uint ntrans = nodeinfo.LocalEdgeCount;
                        MjolnirUtil.ProcessEdgeTransitions(j, ref newEdge, edges, ntrans, nodeinfo, stats);
                    }

                    directededges.Add(newEdge);
                }
            }

            // Update the tile with new directededges.
            tilebuilder.Update(tileDir, nodes, directededges);

            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    // Creates a fresh tile builder for the rebuild path, copying the source tile header fields that
    // FilterTiles / AggregateTiles preserve (base lat/lon, dataset id, checksum, creation date). The
    // C++ non-deserialize ctor (with an existing tile on disk) copies the whole header before resetting
    // the counts in StoreTileData.
    private static GraphTileBuilder NewRebuildBuilder(GraphId tileId, GraphTile source)
    {
        var builder = new GraphTileBuilder(tileId);
        builder.HeaderBuilder.SetBaseLl(source.Header().BaseLl());
        builder.HeaderBuilder.SetDatasetId(source.Header().DatasetId());
        builder.HeaderBuilder.SetChecksum(source.Header().Checksum());
        builder.HeaderBuilder.SetDateCreated(source.Header().DateCreated());
        return builder;
    }

    // std::hash<std::string> analogue used to key shared edge info. Any stable hash works here: the
    // value is only used as the edgeindex component of the (edgeindex, nodea, nodeb) tuple that shares
    // edge info between the two directed edges of an edge (faithful to the C++ which hashes the encoded
    // shape + way id for the same purpose).
    private static uint Hash(string s)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in s)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }
    }

    // Decode a 7-digit-precision encoded polyline to a point list (the AddEdgeInfo overload here
    // takes a point list; FilterTiles passes the decoded shape, AggregateTiles passes a list directly).
    private static List<PointLL> DecodeShape(string encodedShape) => Encoded.Decode7(encodedShape);

    // Increment a GraphId's id field by one (the C++ ++GraphId increments the id within the tile).
    private static GraphId Increment(GraphId id) => new GraphId(id.Tileid(), id.Level(), id.Id() + 1);

    // Remove the tile file from disk (uncompressed or gzipped). Faithful port of the ::remove call.
    private static void RemoveTile(string tileDir, GraphId tileId)
    {
        string fileLocation = Path.Combine(tileDir, GraphTile.FileSuffix(tileId));
        if (File.Exists(fileLocation))
        {
            File.Delete(fileLocation);
        }

        string gz = fileLocation + ".gz";
        if (File.Exists(gz))
        {
            File.Delete(gz);
        }
    }
}
