// Faithful C# port of Valhalla mjolnir shortcutbuilder.cc + shortcutbuilder.h @ 3.7.0.
// Sources:
//   F:/github/valhalla/src/mjolnir/shortcutbuilder.cc
//   F:/github/valhalla/valhalla/mjolnir/shortcutbuilder.h
//
// Builds shortcut edges. Shortcut edges are possible through nodes that only connect to 2 edges on
// the hierarchy level and have compatible attributes. Shortcut edges are inserted before regular
// edges. The pipeline (ShortcutBuilder::Build) iterates the hierarchy levels from the second-highest
// down (skipping the lowest/local level), and for each level FormShortcuts:
//   - reads each tile, creates a GraphTileBuilder for the new tile (copying node transitions),
//   - for each node, AddShortcutEdges forms shortcut edges starting at non-contractible nodes by
//     contracting through CanContract-eligible nodes (matching attributes, no signs/roundabouts/
//     restrictions, same ISO, etc.), merging shape + duration + density + access restrictions, then
//   - copies the remaining regular edges (re-adding signs / turn lanes / access restrictions / lane
//     connectivity / edge info), marking superseded edges with the shortcut index.
//
// EXCLUDED (out of scope: transit/bss/elevation): transit-connection / egress / platform / bss /
// construction edges never appear in the auto/truck graph; the use()-skip branches are preserved
// structurally. Elevation is not added to shortcuts (mean elevation 0, as in C++).
//
// PORT-NOTE: tile->GetSpeed(directededge, ...) in C++ derives the directed-edge index from pointer
// arithmetic (de - tile->directededge(0)); the C# GraphTile.GetSpeed takes that index explicitly, so
// we pass edgeid.Id(). OSRMCarTurnDuration / GetTurnDegree / compute_curvature / decode7 / length are
// the ported sif/midgard/mjolnir helpers. The GraphReader / GraphTileBuilder are the ported baldr
// reader + mjolnir write side, so the tiles written are byte-compatible with the GraphTile reader.
// The boost::property_tree config is replaced by the GraphReader.Config.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Builds shortcut edges. Faithful port of the C++ <c>class ShortcutBuilder</c> plus the
/// shortcutbuilder.cc free functions.
/// </summary>
public static class ShortcutBuilder
{
    /// <summary>Statistics returned by <see cref="Build"/> (mirrors the build_stats shortcut counters).</summary>
    public sealed class ShortcutStats
    {
        /// <summary>Number of shortcut edges created.</summary>
        public uint ShortcutCount { get; set; }

        /// <summary>Number of base edges superseded by shortcuts.</summary>
        public uint EdgeCount { get; set; }

        /// <summary>Number of nodes that exceeded the max shortcuts-from-node limit.</summary>
        public uint ExceededMaxCount { get; set; }
    }

    // Holds the most-restrictive non-conditional access restrictions accumulated along a shortcut.
    // Faithful port of the anonymous-namespace struct ShortcutAccessRestriction.
    private sealed class ShortcutAccessRestriction
    {
        public readonly Dictionary<AccessType, AccessRestriction> AllRestrictions = new();

        // important to set the edge's attribute.
        public ulong Modes;

        public ShortcutAccessRestriction(IReadOnlyList<AccessRestriction> restrictions)
        {
            foreach (AccessRestriction res in restrictions)
            {
                Modes |= res.Modes();
                AllRestrictions.TryAdd(res.Type(), res);
            }
        }

        // Updates non-conditional restrictions if their value is lower than the current value.
        public void UpdateNonconditional(IEnumerable<AccessRestriction> otherRestrictions)
        {
            foreach (AccessRestriction newAr in otherRestrictions)
            {
                // Update the modes for the edge attribute regardless of conditional-ness.
                if (newAr.Type() == AccessType.TimedAllowed || newAr.Type() == AccessType.TimedDenied ||
                    newAr.Type() == AccessType.DestinationAllowed)
                {
                    continue;
                }

                Modes |= newAr.Modes();
                if (!AllRestrictions.TryGetValue(newAr.Type(), out AccessRestriction existing))
                {
                    AllRestrictions[newAr.Type()] = newAr;
                }
                else if (newAr.Value() < existing.Value())
                {
                    AllRestrictions[newAr.Type()] = newAr;
                }
            }
        }
    }

    // Simple structure to hold the 2 pairs of directed edges at a node. First edge in the pair is
    // incoming and second is outgoing. Faithful port of the anonymous-namespace struct EdgePairs.
    private struct EdgePairs
    {
        public (GraphId First, GraphId Second) Edge1;
        public (GraphId First, GraphId Second) Edge2;
    }

    /// <summary>
    /// Build shortcuts. Shortcut edges are possible through nodes that only connect to 2 edges on the
    /// hierarchy level and have compatible attributes. Shortcut edges are inserted before regular
    /// edges. Faithful port of <c>ShortcutBuilder::Build</c>.
    /// </summary>
    /// <param name="config">GraphReader configuration (tile directory + cache knobs).</param>
    /// <returns>Aggregate shortcut statistics across all levels.</returns>
    public static ShortcutStats Build(GraphReader.Config config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var reader = new GraphReader(config);
        var stats = new ShortcutStats();

        // Iterate the hierarchy levels from the second-highest down (skip the lowest/local level).
        // C++: tile_level = levels().rbegin(); ++tile_level; ... rend().
        IReadOnlyList<TileLevel> levels = TileHierarchy.Levels();
        for (int li = levels.Count - 2; li >= 0; --li)
        {
            (uint scCount, uint edgeCount, uint exceededMax) = FormShortcuts(reader, levels[li]);
            stats.ShortcutCount += scCount;
            stats.EdgeCount += edgeCount;
            stats.ExceededMaxCount += exceededMax;
        }

        return stats;
    }

    // Test if 2 edges have matching attributes such that they should be considered for combining into
    // a shortcut edge. Faithful port of EdgesMatch.
    private static bool EdgesMatch(GraphTile tile, GraphId edge1Id, DirectedEdge edge1, GraphId edge2Id, DirectedEdge edge2)
    {
        // Check if edges end at same node.
        if (edge1.EndNode == edge2.EndNode)
        {
            return false;
        }

        // Make sure access matches. Need to consider opposite direction for one of the edges since both
        // edges are outbound from the node.
        if (edge1.ForwardAccess != edge2.ReverseAccess || edge1.ReverseAccess != edge2.ForwardAccess)
        {
            return false;
        }

        // Neither directed edge can have exit signs or be a roundabout.
        if (edge1.Sign || edge2.Sign || edge1.Roundabout || edge2.Roundabout)
        {
            return false;
        }

        // Neither edge can be part of a complex turn restriction.
        if (edge1.StartRestriction != 0 || edge1.EndRestriction != 0 ||
            edge2.StartRestriction != 0 || edge2.EndRestriction != 0)
        {
            return false;
        }

        // classification, link, use, and attributes must also match. We don't consider bridge and
        // tunnel here.
        if (edge1.Classification != edge2.Classification || edge1.Link != edge2.Link ||
            edge1.Use != edge2.Use || edge1.Toll != edge2.Toll ||
            edge1.DestOnly != edge2.DestOnly || edge1.DestOnlyHgv != edge2.DestOnlyHgv ||
            edge1.Unpaved != edge2.Unpaved || edge1.Surface != edge2.Surface ||
            edge1.Roundabout != edge2.Roundabout)
        {
            return false;
        }

        // if there's conditional access restrictions, they must match; others we can safely contract
        // over.
        if (edge1.AccessRestriction != 0 || edge2.AccessRestriction != 0)
        {
            // Filter to keep only conditional restrictions.
            static bool ConditionalFilter(AccessRestriction r)
                => r.Type() == AccessType.DestinationAllowed || r.Type() == AccessType.TimedAllowed ||
                   r.Type() == AccessType.TimedDenied;

            var res1 = new List<AccessRestriction>();
            foreach (AccessRestriction r in tile.GetAccessRestrictions(edge1Id.Id(), GraphConstants.VehicularAccess))
            {
                if (ConditionalFilter(r))
                {
                    res1.Add(r);
                }
            }

            var res2 = new List<AccessRestriction>();
            foreach (AccessRestriction r in tile.GetAccessRestrictions(edge2Id.Id(), GraphConstants.VehicularAccess))
            {
                if (ConditionalFilter(r))
                {
                    res2.Add(r);
                }
            }

            if (res1.Count != res2.Count)
            {
                return false;
            }

            for (int i = 0; i < res1.Count; i++)
            {
                if (res1[i].Type() != res2[i].Type() || res1[i].Modes() != res2[i].Modes() ||
                    res1[i].Value() != res2[i].Value())
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Get the GraphId of the opposing edge. Faithful port of GetOpposingEdge.
    private static GraphId GetOpposingEdge(GraphId node, DirectedEdge edge, GraphReader reader, ulong wayid)
    {
        // Get the tile at the end node.
        GraphTile tile = reader.GetGraphTile(edge.EndNode)!;
        NodeInfo nodeinfo = tile.Node((int)edge.EndNode.Id());

        // Get the directed edges and return when the end node matches the specified node and length
        // matches.
        var edgeid = new GraphId(edge.EndNode.Tileid(), edge.EndNode.Level(), nodeinfo.EdgeIndex);
        for (uint i = 0, n = nodeinfo.EdgeCount; i < n; i++, edgeid = Increment(edgeid))
        {
            DirectedEdge directededge = tile.DirectedEdge(edgeid);
            if (directededge.Use == Use.TransitConnection ||
                directededge.Use == Use.EgressConnection ||
                directededge.Use == Use.PlatformConnection ||
                directededge.Use == Use.Construction)
            {
                continue;
            }

            if (directededge.EndNode == node && directededge.Classification == edge.Classification &&
                directededge.Length == edge.Length &&
                ((directededge.Link && edge.Link) || (directededge.Use == edge.Use)) &&
                wayid == tile.EdgeInfo(directededge).WayId)
            {
                return edgeid;
            }
        }

        // LOG_ERROR("Opposing directed edge not found ...");
        return new GraphId(0, 0, 0);
    }

    // Get the ISO country code at the end node. Faithful port of EndNodeIso.
    private static string EndNodeIso(DirectedEdge edge, GraphReader reader)
    {
        GraphTile tile = reader.GetGraphTile(edge.EndNode)!;
        NodeInfo nodeinfo = tile.Node((int)edge.EndNode.Id());
        return tile.AdminInfo((int)nodeinfo.AdminIndex).CountryIso;
    }

    // Test if the node is eligible to be contracted (part of a shortcut). Faithful port of CanContract.
    private static bool CanContract(GraphReader reader, GraphTile tile, GraphId node, ref EdgePairs edgepairs)
    {
        NodeInfo nodeinfo = tile.Node(node);
        if (!nodeinfo.CanContract())
        {
            return false;
        }

        // Do not create a shortcut across a node that has any upward transitions.
        if (nodeinfo.TransitionCount > 0)
        {
            foreach (NodeTransition trans in tile.GetNodeTransitions(node))
            {
                if (trans.Up())
                {
                    return false;
                }
            }
        }

        // Get list of valid edges, excluding transit connection edges. Also skip shortcut edges.
        var edges = new List<GraphId>();
        var edgeid = new GraphId(node.Tileid(), node.Level(), nodeinfo.EdgeIndex);
        for (uint i = 0, n = nodeinfo.EdgeCount; i < n; i++, edgeid = Increment(edgeid))
        {
            DirectedEdge directededge = tile.DirectedEdge(edgeid);
            if (directededge.CanFormShortcut())
            {
                edges.Add(edgeid);
            }
        }

        // Must have only 2 edges at this level.
        if (edges.Count != 2)
        {
            return false;
        }

        // Get the directed edges - these are the outbound edges from the node.
        DirectedEdge edge1 = tile.DirectedEdge(edges[0]);
        DirectedEdge edge2 = tile.DirectedEdge(edges[1]);

        if (!EdgesMatch(tile, edges[0], edge1, edges[1], edge2))
        {
            return false;
        }

        // Get the opposing directed edges - these are the inbound edges to the node.
        ulong wayid1 = tile.EdgeInfo(edge1).WayId;
        ulong wayid2 = tile.EdgeInfo(edge2).WayId;
        GraphId oppedge1 = GetOpposingEdge(node, edge1, reader, wayid1);
        GraphId oppedge2 = GetOpposingEdge(node, edge2, reader, wayid2);
        DirectedEdge oppdiredge1 = reader.GetGraphTile(oppedge1)!.DirectedEdge(oppedge1);
        DirectedEdge oppdiredge2 = reader.GetGraphTile(oppedge2)!.DirectedEdge(oppedge2);

        // If either opposing directed edge has exit signs return false.
        if (oppdiredge1.Sign || oppdiredge2.Sign)
        {
            return false;
        }

        // Do not allow a shortcut on a ramp crossing at a traffic signal or where more than 3 edges
        // meet.
        if (edge1.Link && edge2.Link && (nodeinfo.TrafficSignal || nodeinfo.EdgeCount > 3))
        {
            return false;
        }

        // Cannot have turn restriction from either inbound edge to the other outbound edge.
        if (((oppdiredge1.Restrictions & (1u << (int)edge2.LocalEdgeIdx)) != 0) ||
            ((oppdiredge2.Restrictions & (1u << (int)edge1.LocalEdgeIdx)) != 0))
        {
            return false;
        }

        // ISO country codes at the end nodes must equal this node.
        string iso = tile.AdminInfo((int)nodeinfo.AdminIndex).CountryIso;
        string e1Iso = EndNodeIso(edge1, reader);
        string e2Iso = EndNodeIso(edge2, reader);
        if (e1Iso != iso || e2Iso != iso)
        {
            return false;
        }

        // Simple check for a possible maneuver where the continuation is a turn and there are other
        // edges at the node (forward intersecting edge or a 'T' intersection).
        if (nodeinfo.LocalEdgeCount > 2)
        {
            // Find number of drivable edges.
            uint drivable = 0;
            for (uint i = 0; i < nodeinfo.LocalEdgeCount; i++)
            {
                if (nodeinfo.LocalDriveability(i) != Traversability.None)
                {
                    drivable++;
                }
            }

            if (drivable > 2)
            {
                uint heading1 = (nodeinfo.Heading(edge1.LocalEdgeIdx) + 180) % 360;
                uint turnDegree = Util.GetTurnDegree(heading1, nodeinfo.Heading(edge2.LocalEdgeIdx));
                if (turnDegree > 60 && turnDegree < 300)
                {
                    return false;
                }
            }
        }

        // Store the pairs of base edges entering and exiting this node.
        edgepairs.Edge1 = (oppedge1, edges[1]);
        edgepairs.Edge2 = (oppedge2, edges[0]);
        return true;
    }

    // Connect 2 edges shape and update the next end node in the new level. Faithful port of
    // ConnectEdges.
    private static void ConnectEdges(
        GraphReader reader,
        GraphId startnode,
        GraphId edgeid,
        List<PointLL> shape,
        ref GraphId endnode,
        ref uint oppLocalIdx,
        ref uint restrictions,
        ref float averageDensity,
        ref float totalDuration,
        ref float totalTruckDuration,
        ShortcutAccessRestriction accessRestrictions,
        ref bool hasBridge,
        ref bool hasTunnel)
    {
        // Get the tile and directed edge.
        GraphTile tile = reader.GetGraphTile(startnode)!;
        DirectedEdge directededge = tile.DirectedEdge(edgeid);

        // Add edge and turn duration for car.
        NodeInfo nodeinfo = tile.Node(startnode);
        float turnDuration = DynamicCost.OSRMCarTurnDuration(directededge, nodeinfo, oppLocalIdx);
        totalDuration += turnDuration;
        uint speed = tile.GetSpeed(directededge, edgeid.Id(), GraphConstants.NoFlowMask);
        float edgeDuration = directededge.Length / (float)(speed * Constants.KphToMetersPerSec);
        totalDuration += edgeDuration;

        // Add edge and turn duration for truck.
        totalTruckDuration += turnDuration;
        uint truckSpeed = tile.GetSpeed(directededge, edgeid.Id(), GraphConstants.NoFlowMask, GraphConstants.InvalidSecondsOfWeek, true);
        float edgeDurationTruck = directededge.Length / (float)(truckSpeed * Constants.KphToMetersPerSec);
        totalTruckDuration += edgeDurationTruck;

        // Copy the restrictions and opposing local index. Want to set the shortcut edge's restrictions
        // and opp_local_idx to the last directed edge in the chain.
        oppLocalIdx = directededge.OppLocalIdx;
        restrictions = directededge.Restrictions;

        // Get the shape for this edge. Reverse if directed edge is not forward.
        string encoded = tile.EdgeInfo(directededge).EncodedShape();
        List<PointLL> edgeshape = Encoded.Decode7(encoded);
        if (!directededge.Forward)
        {
            edgeshape.Reverse();
        }

        // Append shape to the shortcut's shape. Skip first point since it should equal the last of the
        // prior edge.
        for (int k = 1; k < edgeshape.Count; k++)
        {
            shape.Add(edgeshape[k]);
        }

        // Add to the weighted average.
        averageDensity += directededge.Length * directededge.Density;

        // Preserve the most restrictive access restrictions.
        (IReadOnlyList<AccessRestriction> edgeRestrictions, _) = tile.GetAccessRestrictions(edgeid.Id());
        accessRestrictions.UpdateNonconditional(edgeRestrictions);

        // Update the end node.
        endnode = directededge.EndNode;

        // Update has_bridge / has_tunnel flags.
        hasBridge |= directededge.Bridge;
        hasTunnel |= directededge.Tunnel;
    }

    // Check if the edge is entering a contracted node. Faithful port of IsEnteringEdgeOfContractedNode.
    private static bool IsEnteringEdgeOfContractedNode(GraphReader reader, GraphId nodeid, GraphId edge)
    {
        var edgepairs = default(EdgePairs);
        GraphTile tile = reader.GetGraphTile(nodeid)!;
        bool c = CanContract(reader, tile, nodeid, ref edgepairs);
        return c && (edgepairs.Edge1.First == edge || edgepairs.Edge2.First == edge);
    }

    // Add shortcut edges (if they should exist) from the specified node. Faithful port of
    // AddShortcutEdges. Returns (shortcut_count, total_edge_count).
    private static (uint ShortcutCount, uint TotalEdgeCount) AddShortcutEdges(
        GraphReader reader,
        GraphTile tile,
        GraphTileBuilder tilebuilder,
        GraphId startNode,
        uint edgeIndex,
        uint edgeCount,
        Dictionary<uint, uint> shortcuts)
    {
        // Shortcut edges have to start at a node that is not contracted - return if this node can be
        // contracted.
        var startPairs = default(EdgePairs);
        if (CanContract(reader, tile, startNode, ref startPairs))
        {
            return (0u, 0u);
        }

        // Check if this is the last edge in a shortcut (if the endnode cannot be contracted).
        bool LastEdge(GraphTile t, GraphId endnode, ref EdgePairs ep) => !CanContract(reader, t, endnode, ref ep);

        // Iterate through directed edges of the base node.
        uint shortcut = 0;
        uint shortcutCount = 0;
        uint totalEdgeCount = 0;
        var edgeId = new GraphId(startNode.Tileid(), startNode.Level(), edgeIndex);
        for (uint i = 0; i < edgeCount; i++, edgeId = Increment(edgeId))
        {
            // Skip transit connection edges.
            DirectedEdge directededge = tile.DirectedEdge(edgeId);
            if (!directededge.CanFormShortcut())
            {
                continue;
            }

            // Get the end node and check if the edge is set as a matching, entering edge of the
            // contracted node.
            GraphId endNode = directededge.EndNode;
            if (IsEnteringEdgeOfContractedNode(reader, endNode, edgeId))
            {
                totalEdgeCount++;

                // Form a shortcut edge.
                DirectedEdge newedge = directededge;

                // For computing weighted density and total turn duration along the shortcut.
                uint edgeLength = newedge.Length;
                float averageDensity = edgeLength * newedge.Density;
                uint speed = tile.GetSpeed(directededge, edgeId.Id(), GraphConstants.NoFlowMask);
                float totalDuration = edgeLength / (float)(speed * Constants.KphToMetersPerSec);
                uint truckSpeed = Math.Min(
                    tile.GetSpeed(directededge, edgeId.Id(), GraphConstants.NoFlowMask, GraphConstants.InvalidSecondsOfWeek, true),
                    directededge.TruckSpeed != 0 ? directededge.TruckSpeed : GraphConstants.MaxAssumedTruckSpeed);
                float totalTruckDuration = edgeLength / (float)(truckSpeed * Constants.KphToMetersPerSec);

                // Get the shape for this edge. If this initial directed edge is not forward - reverse
                // the shape so the edge info stored is forward for the first added edge info.
                EdgeInfo edgeinfo = tile.EdgeInfo(directededge);
                List<PointLL> shape = Encoded.Decode7(edgeinfo.EncodedShape());
                if (!directededge.Forward)
                {
                    shape.Reverse();
                }

                // store all access_restrictions of the base edge: non-conditional ones will be updated
                // while contracting, conditional ones break contraction and are safe to copy.
                (IReadOnlyList<AccessRestriction> restrictionsView, _) = tile.GetAccessRestrictions(edgeId.Id());
                var accessRestrictions = new ShortcutAccessRestriction(restrictionsView);

                // Connect edges to the shortcut while the end node is marked as contracted.
                uint rst = 0;
                uint oppLocalIdx = directededge.OppLocalIdx;
                GraphId nextEdgeId = edgeId;
                bool hasBridge = directededge.Bridge;
                bool hasTunnel = directededge.Tunnel;
                while (true)
                {
                    var edgepairs = default(EdgePairs);
                    GraphTile endTile = reader.GetGraphTile(endNode)!;
                    if (LastEdge(endTile, endNode, ref edgepairs))
                    {
                        break;
                    }

                    // Edge should match one of the 2 first (inbound) edges in the pair. Choose the
                    // matching outgoing (second) edge.
                    if (edgepairs.Edge1.First == nextEdgeId)
                    {
                        nextEdgeId = edgepairs.Edge1.Second;
                    }
                    else if (edgepairs.Edge2.First == nextEdgeId)
                    {
                        nextEdgeId = edgepairs.Edge2.Second;
                    }
                    else
                    {
                        // Break out of loop. This case can happen when a shortcut edge enters another
                        // shortcut edge (but is not drivable in reverse direction from the node).
                        // LOG_ERROR("Edge not found in edge pairs.");
                        break;
                    }

                    // Connect the matching outbound directed edge (updates the next end node in the new
                    // level). Keep track of the last restriction on the connected shortcut.
                    ConnectEdges(reader, endNode, nextEdgeId, shape, ref endNode, ref oppLocalIdx, ref rst,
                        ref averageDensity, ref totalDuration, ref totalTruckDuration, accessRestrictions,
                        ref hasBridge, ref hasTunnel);
                    totalEdgeCount++;
                }

                // Get the length from the shape. This prevents roundoff issues when forming elevation.
                uint length = (uint)PointLlPolyline2.Length(shape);

                // Add the edge info. Use length and number of shape points to match an edge in case
                // multiple shortcut edges exist between the 2 nodes. Shortcuts use way Id = 0. No need
                // for names etc, shortcuts aren't used in guidance.
                uint idx = (length & 0xfffff) | (((uint)shape.Count & 0xfff) << 20);
                uint edgeInfoOffset = tilebuilder.AddEdgeInfo(
                    idx, startNode, endNode, 0, 0, edgeinfo.BikeNetwork, edgeinfo.SpeedLimit, shape,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 0, out _, false);

                newedge.SetEdgeInfoOffset(edgeInfoOffset);

                // Set the forward flag on this directed edge (a new edge was added, so forward).
                newedge.SetForward(true);

                // Shortcut edge has the opp_local_idx and restrictions of the last directed edge in the
                // shortcut chain.
                newedge.SetOppLocalIdx(oppLocalIdx);
                newedge.SetRestrictions(rst);

                // add new access restrictions if any and set the mask on the edge.
                if (accessRestrictions.AllRestrictions.Count > 0)
                {
                    newedge.SetAccessRestriction((uint)accessRestrictions.Modes);
                    foreach (KeyValuePair<AccessType, AccessRestriction> res in accessRestrictions.AllRestrictions)
                    {
                        tilebuilder.AddAccessRestriction(new AccessRestriction(
                            (uint)tilebuilder.DirectedEdges.Count, res.Value.Type(), res.Value.Modes(),
                            res.Value.Value(), res.Value.ExceptDestination()));
                    }
                }

                // Update the length, curvature, and end node.
                newedge.SetLength(length);
                newedge.SetCurvature(GraphBuilder.ComputeCurvature(shape));
                newedge.SetEndNode(endNode);

                // Set the default weighted grade for the edge. No edge elevation is added.
                newedge.SetWeightedGrade(6);

                // Sanity check - should never see a shortcut with signs.
                // if (newedge.Sign) LOG_ERROR("Shortcut edge with exit signs");

                // Get turn lanes from the base directed edge. Add them if this is the last edge,
                // otherwise set the turnlanes flag to false.
                var endEdgePairs = default(EdgePairs);
                if (directededge.TurnLanes &&
                    LastEdge(reader.GetGraphTile(directededge.EndNode)!, directededge.EndNode, ref endEdgePairs))
                {
                    uint offset = tile.TurnLanesOffset(edgeId.Id());
                    tilebuilder.AddTurnLanes((uint)tilebuilder.DirectedEdges.Count, tile.GetName(offset));
                    newedge.SetTurnLanes(true);
                }
                else
                {
                    newedge.SetTurnLanes(false);
                }

                // For now just drop lane connectivity for shortcuts.
                if (newedge.LaneConnectivity)
                {
                    newedge.SetLaneConnectivity(false);
                }

                // Compute the weighted edge density.
                newedge.SetDensity((uint)(averageDensity / length));

                // Update speed to the one that takes turn durations into account.
                uint newSpeed = (uint)Math.Round(length / totalDuration * Constants.MetersPerSecToKph);
                newedge.SetSpeed(newSpeed);

                uint newTruckSpeed = (uint)Math.Round(length / totalTruckDuration * Constants.MetersPerSecToKph);
                newedge.SetTruckSpeed(newTruckSpeed);

                // Add shortcut edge. Add to the shortcut map (associates the base edge index to the
                // shortcut index). Remove superseded mask that may have been copied from base edge.
                shortcuts[i] = shortcut + 1;
                newedge.SetShortcut(shortcut + 1);
                newedge.SetSuperseded(0);

                // Make sure shortcut edge is not marked as internal edge.
                newedge.SetInternal(false);

                // Set bridge / tunnel flags.
                newedge.SetBridge(hasBridge);
                newedge.SetTunnel(hasTunnel);

                // Add new directed edge to tile builder.
                tilebuilder.DirectedEdges.Add(newedge);
                shortcutCount++;
                shortcut++;
            }
        }

        // Log if the max number of shortcuts from a node is exceeded (not serious; see NOTE in C++).
        return (shortcutCount, totalEdgeCount);
    }

    // Form shortcuts for tiles in this level. Faithful port of FormShortcuts.
    // Returns {shortcut_count, total_edge_count, exceeded_max_count}.
    private static (uint ShortcutCount, uint TotalEdgeCount, uint ExceededMaxCount) FormShortcuts(
        GraphReader reader,
        TileLevel level)
    {
        reader.Clear();
        uint shortcutCount = 0;
        uint totalEdgeCount = 0;
        uint exceededMaxCount = 0;
        uint ntiles = level.Tiles.TileCount();
        uint tileLevel = level.Level;
        for (uint tileid = 0; tileid < ntiles; tileid++)
        {
            // Get the graph tile. Skip if no tile exists (common case).
            GraphTile? tile = reader.GetGraphTile(new GraphId(tileid, tileLevel, 0));
            if (tile is null)
            {
                continue;
            }

            // Create GraphTileBuilder for the new tile.
            var newTile = new GraphId(tileid, tileLevel, 0);
            var tilebuilder = new GraphTileBuilder(newTile);
            tilebuilder.HeaderBuilder.SetBaseLl(tile.Header().BaseLl());
            tilebuilder.HeaderBuilder.SetDatasetId(tile.Header().DatasetId());
            tilebuilder.HeaderBuilder.SetChecksum(tile.Header().Checksum());

            // Since the old tile is not serialized we must copy any data not dependent on edge Id into
            // the new builders (e.g., node transitions).
            uint transitionCount = tile.Header().Transitioncount();
            for (uint t = 0; t < transitionCount; ++t)
            {
                tilebuilder.Transitions.Add(tile.Transition(t));
            }

            // Iterate through the nodes in the tile.
            var nodeId = new GraphId(tileid, tileLevel, 0);
            uint nodecount = tile.Header().Nodecount();
            for (uint n = 0; n < nodecount; n++, nodeId = Increment(nodeId))
            {
                // Get the node info, copy node index and count from old tile.
                NodeInfo nodeinfo = tile.Node(nodeId);
                uint oldEdgeIndex = nodeinfo.EdgeIndex;
                uint oldEdgeCount = nodeinfo.EdgeCount;

                // Update node information.
                AdminInfo admin = tile.AdminInfo((int)nodeinfo.AdminIndex);
                nodeinfo.SetEdgeIndex((uint)tilebuilder.DirectedEdges.Count);
                nodeinfo.SetAdminIndex((ushort)tilebuilder.AddAdmin(
                    admin.CountryText, admin.StateText, admin.CountryIso, admin.StateIso));

                // Current edge count.
                int edgeCount = tilebuilder.DirectedEdges.Count;

                // Add shortcut edges first.
                var shortcuts = new Dictionary<uint, uint>();
                (uint scCount, uint teCount) = AddShortcutEdges(reader, tile, tilebuilder, nodeId,
                    oldEdgeIndex, oldEdgeCount, shortcuts);
                shortcutCount += scCount;
                totalEdgeCount += teCount;
                if (scCount > GraphConstants.MaxShortcutsFromNode)
                {
                    ++exceededMaxCount;
                }

                // Copy the rest of the directed edges from this node.
                var edgeid = new GraphId(tileid, tileLevel, oldEdgeIndex);
                for (uint i = 0; i < oldEdgeCount; i++, edgeid = Increment(edgeid))
                {
                    // Copy the directed edge information and update end node, edge data offset.
                    DirectedEdge directededge = tile.DirectedEdge(edgeid);
                    DirectedEdge newedge = directededge;

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

                    // Get access restrictions from the base directed edge. Update the edge index.
                    if (directededge.AccessRestriction != 0)
                    {
                        (IReadOnlyList<AccessRestriction> restrictions, _) = tile.GetAccessRestrictions(edgeid.Id());
                        foreach (AccessRestriction res in restrictions)
                        {
                            tilebuilder.AddAccessRestriction(new AccessRestriction(
                                (uint)tilebuilder.DirectedEdges.Count, res.Type(), res.Modes(),
                                res.Value(), res.ExceptDestination()));
                        }
                    }

                    // Copy lane connectivity.
                    if (directededge.LaneConnectivity)
                    {
                        tilebuilder.CopyLaneConnectivityFromTile(tile, edgeid.Id());
                    }

                    // Names can be different in the forward and backward direction.
                    bool diffNames = tilebuilder.OpposingEdgeInfoDiffers(tile, directededge);

                    // Get edge info, shape, and names from the old tile and add to the new. Use prior
                    // edgeinfo offset as the key to differentiate edges with the same end nodes.
                    EdgeInfo edgeinfo = tile.EdgeInfo(directededge);
                    uint edgeInfoOffset = tilebuilder.AddEdgeInfo(
                        (uint)directededge.EdgeInfoOffset, nodeId, directededge.EndNode, edgeinfo.WayId,
                        edgeinfo.MeanElevation, edgeinfo.BikeNetwork, edgeinfo.SpeedLimit,
                        Encoded.Decode7(edgeinfo.EncodedShape()), edgeinfo.GetNames(),
                        edgeinfo.GetTaggedValues(), edgeinfo.GetLinguisticTaggedValues(),
                        edgeinfo.GetTypes(), out _, diffNames);

                    newedge.SetEdgeInfoOffset(edgeInfoOffset);

                    // Set the superseded mask - the shortcut mask that supersedes this edge (outbound
                    // from the node). Do not set (keep 0) if max number of shortcuts from a node is
                    // exceeded.
                    uint supersededIdx = shortcuts.TryGetValue(i, out uint s) ? s : 0;
                    if (supersededIdx <= GraphConstants.MaxShortcutsFromNode)
                    {
                        newedge.SetSuperseded(supersededIdx);
                    }

                    // Add directed edge.
                    tilebuilder.DirectedEdges.Add(newedge);
                }

                // Set the edge count for the new node.
                nodeinfo.SetEdgeCount((uint)(tilebuilder.DirectedEdges.Count - edgeCount));

                // Get named signs from the base node.
                if (nodeinfo.NamedIntersection)
                {
                    List<SignInfo> signs = tile.GetSigns(n, true);
                    tilebuilder.AddSigns((uint)tilebuilder.Nodes.Count, signs);
                }

                tilebuilder.Nodes.Add(nodeinfo);
            }

            // Store the new tile.
            tilebuilder.StoreTileData(reader.TileDir());

            // Check if we need to clear the tile cache.
            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }

        return (shortcutCount, totalEdgeCount, exceededMaxCount);
    }

    // Increment a GraphId's id field by one (the C++ ++GraphId increments the id within the tile).
    private static GraphId Increment(GraphId id) => new GraphId(id.Tileid(), id.Level(), id.Id() + 1);
}
