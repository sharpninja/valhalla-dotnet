// Faithful C# port of Valhalla mjolnir restrictionbuilder.h + src/mjolnir/restrictionbuilder.cc
// @ 3.8.3 commit a60c7cb (the upstream implementation is unchanged from 3.7.0).
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/restrictionbuilder.h
//   F:/github/valhalla/src/mjolnir/restrictionbuilder.cc
//
// RestrictionBuilder reads the simple+complex turn restrictions parsed from OSM (two sorted
// sequences keyed by the "from" way id: complex_restrictions_from and complex_restrictions_to) and
// writes complex restrictions into the baldr tiles. For each directed edge that is marked as the
// start (or end) of a restriction, it walks the chain of vias (depth-first, following way ids
// through directed edges + node transitions, possibly across tiles/levels) to turn the OSM way ids
// into a sequence of edge GraphIds, then stores a forward complex restriction (in the "to" edge's
// tile) and a reverse complex restriction (in the "from"/walked tile), handling the special
// only_* (and only_probable) restriction types by expanding to the disallowed sibling edges.
//
// Multi-via complex restrictions ARE reproduced (the depth-first GetGraphIds expansion + the
// only-restriction sibling expansion that emits one restriction per disallowed branch).
//
// PORT-NOTE (consistent with the established mjolnir front-end + GraphBuilder port): the C++ runs a
// thread pool over a randomized tile queue with a shared GraphReader + mutex, spilling the from/to
// restrictions to mmapped midgard::sequence temp files. This on-device port runs single-threaded
// over the tile set (deterministic order) and takes the from/to restrictions as in-memory sorted
// lists; the std::random_device shuffle, std::promise/std::thread fan-out, the mutex, and the
// SCOPED_TIMER/build_stats/logging are dropped. Every restriction-walking algorithm (GetGraphIds,
// ExpandFromNode[Inner], GetOpposingEdge, IsEdgeAllowed, CreateComplexRestriction, the forward /
// reverse / only-restriction sibling expansion, the dedup via the per-tile temp multimaps, and
// HandleOnlyRestrictionProperties) is preserved EXACTLY.
//
// EXCLUDED: transit (the transit-connection/egress/platform use checks are kept since they gate
// edge walking, but no transit edges are produced by the auto/truck build).

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Class used to add complex turn restrictions to the graph tiles. Faithful port of the C++
/// <c>class RestrictionBuilder</c> + the restrictionbuilder.cc free functions.
/// </summary>
public static class RestrictionBuilder
{
    /// <summary>Maximum number of vias per restriction. Mirrors C++ <c>kMaxViasPerRestriction</c>.</summary>
    public const int MaxViasPerRestriction = ComplexRestriction.MaxViasPerRestriction;

    // (way_id, graph_id) pair accumulated during the depth-first way->edge resolution.
    private readonly struct EdgeId
    {
        public EdgeId(ulong wayId, GraphId graphId)
        {
            WayId = wayId;
            GraphId = graphId;
        }

        public ulong WayId { get; }

        public GraphId GraphId { get; }
    }

    /// <summary>
    /// Per-thread (here, per-run) result accumulated during the build: the forward/reverse counts,
    /// the complex restrictions that need to be written into another tile (for only_* restrictions
    /// where the "to" edge is in a different tile), and the set of edges that are part of a
    /// restriction but live in another tile. Faithful port of the C++ <c>struct Result</c>.
    /// </summary>
    public sealed class Result
    {
        /// <summary>Number of forward complex restrictions added (in-tile).</summary>
        public uint ForwardRestrictionsCount { get; set; }

        /// <summary>Number of reverse complex restrictions added.</summary>
        public uint ReverseRestrictionsCount { get; set; }

        /// <summary>Complex restrictions whose "to" edge is in a different tile (written afterwards).</summary>
        public List<ComplexRestrictionBuilder> Restrictions { get; } = new();

        /// <summary>Edges that are part of an only_* restriction but live in another tile.</summary>
        public HashSet<GraphId> PartOfRestriction { get; } = new();
    }

    // ------------------------------------------------------------------
    // GetOpposingEdge / IsEdgeAllowed (anonymous-namespace helpers)
    // ------------------------------------------------------------------

    // Faithful port of the anonymous GetOpposingEdge: find the opposing directed edge of `edge`
    // (which starts at `node` and ends at edge.endnode()), matching classification, length, link/use
    // and way id. Returns an invalid GraphId if not found.
    private static GraphId GetOpposingEdge(GraphReader reader, GraphTile tile, GraphId node, DirectedEdge edge)
    {
        GraphId endNode = edge.EndNode;
        GraphTile? endNodeTile = tile;
        if (endNodeTile.Id() != endNode.TileBase())
        {
            endNodeTile = reader.GetGraphTile(endNode);
        }

        NodeInfo nodeinfo = endNodeTile!.Node(endNode);
        ulong wayId = tile.EdgeInfo(edge).WayId;

        // Get the directed edges and return when the end node matches the specified node and length.
        var oppId = new GraphId(endNode.Tileid(), endNode.Level(), nodeinfo.EdgeIndex);
        uint n = nodeinfo.EdgeCount;
        for (uint i = 0; i < n; i++, oppId += 1)
        {
            DirectedEdge oppEdge = endNodeTile.DirectedEdge((int)(nodeinfo.EdgeIndex + i));
            if (oppEdge.Use == Use.TransitConnection || oppEdge.Use == Use.EgressConnection ||
                oppEdge.Use == Use.PlatformConnection)
            {
                continue;
            }

            if (oppEdge.EndNode == node && oppEdge.Classification == edge.Classification &&
                oppEdge.Length == edge.Length &&
                ((oppEdge.Link && edge.Link) || (oppEdge.Use == edge.Use)) &&
                wayId == endNodeTile.EdgeInfo(oppEdge).WayId)
            {
                return oppId;
            }
        }

        return GraphId.Invalid;
    }

    // Faithful port of IsEdgeAllowed.
    private static bool IsEdgeAllowed(DirectedEdge de, uint access, bool forward)
    {
        bool accessible = ((forward ? de.ForwardAccess : de.ReverseAccess) & access) != 0;
        return accessible &&
               !(de.IsTransitLine || de.IsShortcut || de.Use == Use.TransitConnection ||
                 de.Use == Use.EgressConnection || de.Use == Use.PlatformConnection);
    }

    // ------------------------------------------------------------------
    // ExpandFromNode / GetGraphIds (the depth-first way -> edge resolver)
    // ------------------------------------------------------------------

    // Faithful port of ExpandFromNodeInner.
    private static bool ExpandFromNodeInner(
        GraphReader reader,
        uint access,
        bool forward,
        ref GraphId lastNode,
        HashSet<GraphId> visitedNodes,
        List<EdgeId> edgeIds,
        IReadOnlyList<ulong> wayIds,
        int wayIdIndex,
        GraphTile tile,
        GraphId prevNode,
        GraphId currentNode,
        NodeInfo nodeInfo)
    {
        ulong wayId = wayIds[wayIdIndex];

        for (uint j = 0; j < nodeInfo.EdgeCount; ++j)
        {
            var edgeId = new GraphId(tile.Id().Tileid(), tile.Id().Level(), nodeInfo.EdgeIndex + j);
            DirectedEdge de = tile.DirectedEdge(edgeId);

            if (de.EndNode != prevNode && IsEdgeAllowed(de, access, forward))
            {
                EdgeInfo edgeInfo = tile.EdgeInfo(de);
                if (edgeInfo.WayId == wayId)
                {
                    edgeIds.Add(new EdgeId(wayId, edgeId));

                    // Expand with the next way_id.
                    bool found = ExpandFromNode(reader, access, forward, ref lastNode, visitedNodes,
                        edgeIds, wayIds, wayIdIndex + 1, tile, currentNode, de.EndNode);
                    if (found)
                    {
                        return true;
                    }

                    if (!visitedNodes.Contains(de.EndNode))
                    {
                        visitedNodes.Add(de.EndNode);

                        // Expand with the same way_id.
                        found = ExpandFromNode(reader, access, forward, ref lastNode, visitedNodes,
                            edgeIds, wayIds, wayIdIndex, tile, currentNode, de.EndNode);
                        if (found)
                        {
                            return true;
                        }

                        visitedNodes.Remove(de.EndNode);
                    }

                    edgeIds.RemoveAt(edgeIds.Count - 1);
                }
            }
        }

        return false;
    }

    // Faithful port of ExpandFromNode (depth-first-search over directed edges + transition nodes).
    private static bool ExpandFromNode(
        GraphReader reader,
        uint access,
        bool forward,
        ref GraphId lastNode,
        HashSet<GraphId> visitedNodes,
        List<EdgeId> edgeIds,
        IReadOnlyList<ulong> wayIds,
        int wayIdIndex,
        GraphTile prevTile,
        GraphId prevNode,
        GraphId currentNode)
    {
        if (wayIdIndex == wayIds.Count)
        {
            // Assign the last node to use it for the reverse search later.
            lastNode = currentNode;
            return true;
        }

        GraphTile? tile = prevTile;
        if (tile.Id() != currentNode.TileBase())
        {
            tile = reader.GetGraphTile(currentNode);
        }

        NodeInfo nodeInfo = tile!.Node(currentNode);

        // Expand from the current node.
        bool found = ExpandFromNodeInner(reader, access, forward, ref lastNode, visitedNodes, edgeIds,
            wayIds, wayIdIndex, tile, prevNode, currentNode, nodeInfo);
        if (found)
        {
            return true;
        }

        // Expand from the transition nodes.
        for (uint k = 0; k < nodeInfo.TransitionCount; ++k)
        {
            NodeTransition trans = tile.Transition(nodeInfo.TransitionIndex + k);

            GraphTile? transTile = tile;
            if (transTile.Id() != trans.EndNode().TileBase())
            {
                transTile = reader.GetGraphTile(trans.EndNode());
            }

            found = ExpandFromNodeInner(reader, access, forward, ref lastNode, visitedNodes, edgeIds,
                wayIds, wayIdIndex, transTile!, prevNode, trans.EndNode(),
                transTile!.Node(trans.EndNode()));
            if (found)
            {
                return true;
            }
        }

        return false;
    }

    // Faithful port of GetGraphIds: depth-first resolve the list of way ids into edge GraphIds,
    // dropping the duplicated way_ids in the prefix (so [1,1,1,2,54] => [1,2,54]).
    private static List<GraphId> GetGraphIds(
        ref GraphId startNode,
        GraphReader reader,
        IReadOnlyList<ulong> wayIds,
        uint access,
        bool forward)
    {
        GraphTile? tile = reader.GetGraphTile(startNode);

        var visitedNodes = new HashSet<GraphId> { startNode };
        var edgeIds = new List<EdgeId>();
        ExpandFromNode(reader, access, forward, ref startNode, visitedNodes, edgeIds, wayIds, 0, tile!,
            GraphId.Invalid, startNode);
        if (edgeIds.Count == 0)
        {
            return new List<GraphId>();
        }

        // Ignore duplicated way_ids in the prefix so [1, 1, 1, 2, 54] => [1, 2, 54].
        // C++ does: it = find_if(begin+1, end, way_id != front.way_id); --it; then collect [it, end).
        int it = edgeIds.Count; // one-past-end if not found
        for (int i = 1; i < edgeIds.Count; i++)
        {
            if (edgeIds[i].WayId != edgeIds[0].WayId)
            {
                it = i;
                break;
            }
        }

        --it;

        var res = new List<GraphId>(edgeIds.Count - it);
        for (; it < edgeIds.Count; ++it)
        {
            res.Add(edgeIds[it].GraphId);
        }

        return res;
    }

    // ------------------------------------------------------------------
    // CreateComplexRestriction
    // ------------------------------------------------------------------

    // Faithful port of CreateComplexRestriction.
    private static ComplexRestrictionBuilder CreateComplexRestriction(
        OSMRestriction restriction,
        GraphId from,
        GraphId to,
        List<GraphId> vias)
    {
        var complexRestriction = new ComplexRestrictionBuilder();
        complexRestriction.SetFromId(from);
        complexRestriction.SetViaList(vias);
        complexRestriction.SetToId(to);
        complexRestriction.SetType(restriction.TypeValue());
        complexRestriction.SetModes((ushort)restriction.Modes());
        complexRestriction.SetProbability(restriction.Probability());

        var td = new TimeDomain(restriction.TimeDomain());
        if (td.TdValue != 0)
        {
            complexRestriction.SetBeginDayDow(td.BeginDayDow);
            complexRestriction.SetBeginHrs(td.BeginHrs);
            complexRestriction.SetBeginMins(td.BeginMins);
            complexRestriction.SetBeginMonth(td.BeginMonth);
            complexRestriction.SetBeginWeek(td.BeginWeek);
            complexRestriction.SetDow(td.Dow);
            complexRestriction.SetDt(true);
            complexRestriction.SetDtType(td.Type != 0);
            complexRestriction.SetEndDayDow(td.EndDayDow);
            complexRestriction.SetEndHrs(td.EndHrs);
            complexRestriction.SetEndMins(td.EndMins);
            complexRestriction.SetEndMonth(td.EndMonth);
            complexRestriction.SetEndWeek(td.EndWeek);
        }

        return complexRestriction;
    }

    private static bool IsOnlyRestriction(RestrictionType type)
        => (type >= RestrictionType.OnlyRightTurn && type <= RestrictionType.OnlyStraightOn) ||
           type == RestrictionType.OnlyProbable;

    // ------------------------------------------------------------------
    // Sorted-sequence lower-bound helper (replaces sequence<OSMRestriction>::find on a "from" key).
    // ------------------------------------------------------------------

    // Returns the index of the first restriction whose from() is NOT less than `fromWayId`
    // (std::lower_bound by from()). The list MUST be sorted by from() (then to/vias/... per
    // OSMRestriction::operator<).
    private static int LowerBoundByFrom(IReadOnlyList<OSMRestriction> restrictions, ulong fromWayId)
    {
        int lo = 0;
        int hi = restrictions.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (restrictions[mid].From() < fromWayId)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // ------------------------------------------------------------------
    // build (per-tile worker)
    // ------------------------------------------------------------------

    // Faithful port of the anonymous build() function: process every directed edge in the tile,
    // walk restrictions, and add forward/reverse complex restrictions to the tile builder. Returns
    // the per-run statistics (only_* cross-tile restrictions + part-of-restriction edges).
    private static void Build(
        IReadOnlyList<OSMRestriction> complexRestrictionsFrom,
        IReadOnlyList<OSMRestriction> complexRestrictionsTo,
        GraphReader reader,
        IReadOnlyCollection<GraphId> tileQueue,
        Result stats)
    {
        foreach (GraphId tileId in tileQueue)
        {
            // Get a readable tile; skip empty tiles.
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            // Tile builder - deserialize the existing tile.
            var tilebuilder = new GraphTileBuilder(tile);

            var forwardTmpCr = new Dictionary<GraphId, List<ComplexRestrictionBuilder>>();
            var reverseTmpCr = new Dictionary<GraphId, List<ComplexRestrictionBuilder>>();

            uint forwardCount = 0;
            uint reverseCount = 0;

            for (uint i = 0; i < tilebuilder.Header().Nodecount(); i++)
            {
                NodeInfo nodeinfo = tilebuilder.NodeBuilder((int)i);

                for (uint j = 0; j < nodeinfo.EdgeCount; j++)
                {
                    int directedEdgeIndex = (int)(nodeinfo.EdgeIndex + j);
                    DirectedEdge directededge = tilebuilder.DirectedEdgeBuilder(directedEdgeIndex);

                    if (directededge.IsTransitLine || directededge.IsShortcut ||
                        directededge.Use == Use.TransitConnection ||
                        directededge.Use == Use.EgressConnection ||
                        directededge.Use == Use.PlatformConnection)
                    {
                        continue;
                    }

                    EdgeInfo edgeInfo = tilebuilder.EdgeInfoFor(directededge);

                    // Starting with the "from" wayid. If this edge's endnode has the via, save it as
                    // the "from" and walk the vias to the "to" wayid (may transition hierarchy levels).
                    if (directededge.StartRestriction != 0)
                    {
                        ProcessStartRestriction(reader, complexRestrictionsFrom, tileId, tilebuilder,
                            stats, reverseTmpCr, ref reverseCount, directededge, edgeInfo.WayId);
                    }

                    if (directededge.EndRestriction != 0)
                    {
                        ProcessEndRestriction(reader, complexRestrictionsFrom, complexRestrictionsTo,
                            tileId, tilebuilder, stats, forwardTmpCr, ref forwardCount, directededge,
                            edgeInfo.WayId);
                    }
                }
            }

            stats.ForwardRestrictionsCount += forwardCount;
            stats.ReverseRestrictionsCount += reverseCount;

            // Write the new file.
            tilebuilder.StoreTileData(reader.TileDir());

            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }
    }

    // The "from" (forward search / reverse store) branch of build(). Faithful port of the
    // directededge.start_restriction() block.
    private static void ProcessStartRestriction(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexRestrictionsFrom,
        GraphId tileId,
        GraphTileBuilder tilebuilder,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>> reverseTmpCr,
        ref uint reverseCount,
        DirectedEdge directededge,
        ulong fromWayId)
    {
        int resIt = LowerBoundByFrom(complexRestrictionsFrom, fromWayId);
        while (resIt < complexRestrictionsFrom.Count && complexRestrictionsFrom[resIt].From() == fromWayId)
        {
            OSMRestriction restriction = complexRestrictionsFrom[resIt];
            GraphId currentNode = directededge.EndNode;

            var resWayIds = new List<ulong> { restriction.From() };

            List<ulong> vias = restriction.Vias();
            foreach (ulong v in vias)
            {
                resWayIds.Add(v);
            }

            // if via = restriction.to then don't add to the res_way_ids vector. This happens when we
            // have a restriction:<type> with a via as a node in the OSM data.
            if (vias.Count == 1 && vias[0] != restriction.To())
            {
                resWayIds.Add(restriction.To());
            }
            else if (vias.Count > 1)
            {
                resWayIds.Add(restriction.To());
            }

            // Walk in the forward direction.
            List<GraphId> tmpIdsFwd = GetGraphIds(ref currentNode, reader, resWayIds, restriction.Modes(), true);

            // Now walk in the reverse direction as this is really what needs to be stored in this tile.
            if (tmpIdsFwd.Count != 0)
            {
                resWayIds.Reverse();
                List<GraphId> tmpIds = GetGraphIds(ref currentNode, reader, resWayIds, restriction.Modes(), false);

                if (tmpIds.Count > 1 && tmpIds[^1].TileBase() == tileId)
                {
                    if (IsOnlyRestriction(restriction.TypeValue()))
                    {
                        ExpandOnlyReverseRestrictions(reader, tileId, tilebuilder, stats, reverseTmpCr,
                            ref reverseCount, restriction, tmpIds);
                    }
                    else
                    {
                        AddReverseRestriction(tilebuilder, tileId, stats, reverseTmpCr, ref reverseCount,
                            restriction, tmpIds);
                    }
                }
            }

            ++resIt;
        }
    }

    // The only_* sibling expansion for the reverse-store branch. Faithful port of the inner while
    // loop in build() that walks forward from the front edge's siblings.
    private static void ExpandOnlyReverseRestrictions(
        GraphReader reader,
        GraphId tileId,
        GraphTileBuilder tilebuilder,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>> reverseTmpCr,
        ref uint reverseCount,
        OSMRestriction restriction,
        List<GraphId> tmpIds)
    {
        while (tmpIds.Count > 1)
        {
            GraphId lastEdgeId = tmpIds[0];
            GraphTile? lastTile = reader.GetGraphTile(tileId);
            if (lastTile!.Id() != lastEdgeId.TileBase())
            {
                lastTile = reader.GetGraphTile(lastEdgeId);
            }

            DirectedEdge lastDe = lastTile!.DirectedEdge(lastEdgeId);
            GraphId endNode = lastDe.EndNode;
            GraphTile? endNodeTile = lastTile;
            if (endNodeTile.Id() != endNode.TileBase())
            {
                endNodeTile = reader.GetGraphTile(endNode);
            }

            NodeInfo endNodeInfo = endNodeTile!.Node(endNode);
            for (uint k = 0; k < endNodeInfo.EdgeCount; ++k)
            {
                var nextEdgeId = new GraphId(endNodeTile.Id().Tileid(), endNodeTile.Id().Level(),
                    endNodeInfo.EdgeIndex + k);
                DirectedEdge de = endNodeTile.DirectedEdge(nextEdgeId);
                GraphId oppId = GetOpposingEdge(reader, endNodeTile, endNode, de);
                if (oppId != lastEdgeId && IsEdgeAllowed(de, restriction.Modes(), true))
                {
                    tmpIds[0] = oppId;
                    AddReverseRestriction(tilebuilder, tileId, stats, reverseTmpCr, ref reverseCount,
                        restriction, tmpIds);
                }
            }

            foreach (NodeTransition trans in endNodeTile.GetNodeTransitions(endNode))
            {
                GraphId toNode = trans.EndNode();
                GraphTile? toTile = reader.GetGraphTile(toNode);
                NodeInfo toNodeInfo = toTile!.Node(toNode);
                var nextEdgeId = new GraphId(toTile.Id().Tileid(), toTile.Id().Level(), toNodeInfo.EdgeIndex);
                for (uint k = 0; k < toNodeInfo.EdgeCount; ++k, nextEdgeId += 1)
                {
                    DirectedEdge de = toTile.DirectedEdge(nextEdgeId);
                    GraphId oppId = GetOpposingEdge(reader, toTile, toNode, de);
                    if (oppId != lastEdgeId && IsEdgeAllowed(de, restriction.Modes(), true))
                    {
                        tmpIds[0] = oppId;
                        AddReverseRestriction(tilebuilder, tileId, stats, reverseTmpCr, ref reverseCount,
                            restriction, tmpIds);
                    }
                }
            }

            tmpIds.RemoveAt(0);
        }
    }

    // Faithful port of the AddReverseRestriction lambda.
    private static void AddReverseRestriction(
        GraphTileBuilder tilebuilder,
        GraphId tileId,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>> reverseTmpCr,
        ref uint reverseCount,
        OSMRestriction restriction,
        List<GraphId> tmpIds)
    {
        // vias = tmp_ids[1 .. end-1].
        var vias = new List<GraphId>();
        for (int v = 1; v < tmpIds.Count - 1; v++)
        {
            vias.Add(tmpIds[v]);
        }

        if (vias.Count > MaxViasPerRestriction)
        {
            return;
        }

        // Flip the vias because we walk backwards from the search direction.
        vias.Reverse();
        GraphId from = tmpIds[^1];
        GraphId to = tmpIds[0];

        if (IsOnlyRestriction(restriction.TypeValue()))
        {
            if (to.TileBase() == tileId)
            {
                DirectedEdge edge = tilebuilder.DirectedEdgeBuilder((int)to.Id());
                edge.SetComplexRestriction(true);
                tilebuilder.SetDirectedEdgeBuilder((int)to.Id(), edge);
            }
            else
            {
                stats.PartOfRestriction.Add(to);
            }
        }

        ComplexRestrictionBuilder complexRestriction = CreateComplexRestriction(restriction, from, to, vias);

        // Determine if we need to add this complex restriction or not (no dups).
        bool bfound = false;
        if (reverseTmpCr.TryGetValue(to, out List<ComplexRestrictionBuilder>? existing))
        {
            foreach (ComplexRestrictionBuilder r in existing)
            {
                if (complexRestriction.Equals(r))
                {
                    bfound = true;
                    break;
                }
            }
        }

        if (!bfound)
        {
            if (existing is null)
            {
                existing = new List<ComplexRestrictionBuilder>();
                reverseTmpCr[to] = existing;
            }

            existing.Add(complexRestriction);
            tilebuilder.AddReverseComplexRestriction(complexRestriction);
            reverseCount++;
        }
    }

    // The "to" (reverse search / forward store) branch of build(). Faithful port of the
    // directededge.end_restriction() block.
    private static void ProcessEndRestriction(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexRestrictionsFrom,
        IReadOnlyList<OSMRestriction> complexRestrictionsTo,
        GraphId tileId,
        GraphTileBuilder tilebuilder,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>> forwardTmpCr,
        ref uint forwardCount,
        DirectedEdge directededge,
        ulong fromWayId)
    {
        int resToIt = LowerBoundByFrom(complexRestrictionsTo, fromWayId);
        while (resToIt < complexRestrictionsTo.Count && complexRestrictionsTo[resToIt].From() == fromWayId)
        {
            OSMRestriction restrictionTo = complexRestrictionsTo[resToIt];

            int resIt = LowerBoundByFrom(complexRestrictionsFrom, restrictionTo.To());
            while (resIt < complexRestrictionsFrom.Count &&
                   complexRestrictionsFrom[resIt].From() == restrictionTo.To())
            {
                OSMRestriction restriction = complexRestrictionsFrom[resIt];
                GraphId currentNode = directededge.EndNode;

                var resWayIds = new List<ulong> { restriction.To() };

                List<ulong> vias = restriction.Vias();
                var tempVias = new List<ulong>(vias);
                tempVias.Reverse();

                // if via = restriction.to then don't add (restriction:<type> with a via as a node).
                if (vias.Count > 1 || (vias.Count == 1 && vias[0] != restriction.To()))
                {
                    foreach (ulong v in tempVias)
                    {
                        resWayIds.Add(v);
                    }
                }

                resWayIds.Add(restriction.From());

                // Walk in the forward direction (reverse in relation to the restriction).
                List<GraphId> tmpIdsRev = GetGraphIds(ref currentNode, reader, resWayIds, restriction.Modes(), false);

                // Now walk in the reverse direction (forward in relation to the restriction) as this
                // is really what needs to be stored in this tile.
                if (tmpIdsRev.Count != 0)
                {
                    resWayIds.Reverse();
                    List<GraphId> tmpIds = GetGraphIds(ref currentNode, reader, resWayIds, restriction.Modes(), true);

                    if (tmpIds.Count > 1 && tmpIds[^1].TileBase() == tileId)
                    {
                        if (!IsOnlyRestriction(restriction.TypeValue()))
                        {
                            AddForwardRestriction(tilebuilder, tileId, stats, forwardTmpCr,
                                ref forwardCount, restriction, tmpIds);
                        }
                        else
                        {
                            ExpandOnlyForwardRestrictions(reader, tileId, tilebuilder, stats,
                                forwardTmpCr, ref forwardCount, restriction, tmpIds);
                        }
                    }
                }

                ++resIt;
            }

            resToIt++;
        }
    }

    // The only_* sibling expansion for the forward-store branch. Faithful port of the inner while
    // loop that walks from the pre-last edge's siblings.
    private static void ExpandOnlyForwardRestrictions(
        GraphReader reader,
        GraphId tileId,
        GraphTileBuilder tilebuilder,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>> forwardTmpCr,
        ref uint forwardCount,
        OSMRestriction restriction,
        List<GraphId> tmpIds)
    {
        while (tmpIds.Count > 1)
        {
            GraphId lastEdgeId = tmpIds[^1];
            GraphId preLastEdgeId = tmpIds[^2];

            GraphTile? preLastTile = reader.GetGraphTile(tileId);
            if (preLastEdgeId.TileBase() != preLastTile!.Id())
            {
                preLastTile = reader.GetGraphTile(preLastEdgeId);
            }

            DirectedEdge preLastEdge = preLastTile!.DirectedEdge(preLastEdgeId);
            GraphId endNode = preLastEdge.EndNode;
            GraphTile? nextTile = preLastTile;
            if (endNode.TileBase() != nextTile.Id())
            {
                nextTile = reader.GetGraphTile(endNode);
            }

            NodeInfo nodeInfo = nextTile!.Node(endNode);
            var edgeId = new GraphId(nextTile.Id().Tileid(), nextTile.Id().Level(), nodeInfo.EdgeIndex);
            for (uint k = 0; k < nodeInfo.EdgeCount; ++k, edgeId += 1)
            {
                DirectedEdge de = nextTile.DirectedEdge(edgeId);
                if (edgeId != lastEdgeId && IsEdgeAllowed(de, restriction.Modes(), true))
                {
                    tmpIds[^1] = edgeId;
                    AddForwardRestriction(tilebuilder, tileId, stats, forwardTmpCr, ref forwardCount,
                        restriction, tmpIds);
                }
            }

            foreach (NodeTransition trans in nextTile.GetNodeTransitions(nodeInfo))
            {
                GraphId toNode = trans.EndNode();
                GraphTile? toTile = reader.GetGraphTile(toNode);
                NodeInfo toNodeInfo = toTile!.Node(toNode);
                var toEdgeId = new GraphId(toTile.Id().Tileid(), toTile.Id().Level(), toNodeInfo.EdgeIndex);
                for (uint k = 0; k < toNodeInfo.EdgeCount; ++k, toEdgeId += 1)
                {
                    DirectedEdge de = toTile.DirectedEdge(toEdgeId);
                    if (toEdgeId != lastEdgeId && IsEdgeAllowed(de, restriction.Modes(), true))
                    {
                        tmpIds[^1] = toEdgeId;
                        AddForwardRestriction(tilebuilder, tileId, stats, forwardTmpCr, ref forwardCount,
                            restriction, tmpIds);
                    }
                }
            }

            tmpIds.RemoveAt(tmpIds.Count - 1);
        }
    }

    // Faithful port of the addForwardRestriction lambda.
    private static void AddForwardRestriction(
        GraphTileBuilder tilebuilder,
        GraphId tileId,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>> forwardTmpCr,
        ref uint forwardCount,
        OSMRestriction restriction,
        List<GraphId> tmpIds)
    {
        // vias = tmp_ids[1 .. end-1].
        var vias = new List<GraphId>();
        for (int v = 1; v < tmpIds.Count - 1; v++)
        {
            vias.Add(tmpIds[v]);
        }

        if (vias.Count > MaxViasPerRestriction)
        {
            return;
        }

        vias.Reverse();
        GraphId from = tmpIds[0];
        GraphId to = tmpIds[^1];
        ComplexRestrictionBuilder complexRestriction = CreateComplexRestriction(restriction, from, to, vias);

        // Determine if we need to add this complex restriction or not (no dups).
        bool bfound = false;
        if (forwardTmpCr.TryGetValue(from, out List<ComplexRestrictionBuilder>? existing))
        {
            foreach (ComplexRestrictionBuilder r in existing)
            {
                if (complexRestriction.Equals(r))
                {
                    bfound = true;
                    break;
                }
            }
        }

        if (!bfound)
        {
            if (existing is null)
            {
                existing = new List<ComplexRestrictionBuilder>();
                forwardTmpCr[from] = existing;
            }

            existing.Add(complexRestriction);

            // Happens if we got here while processing an only_* restriction.
            if (complexRestriction.ToGraphId().TileBase() != tileId)
            {
                stats.Restrictions.Add(complexRestriction);
            }
            else
            {
                DirectedEdge edge = tilebuilder.DirectedEdgeBuilder((int)to.Id());
                edge.SetEndRestriction(edge.EndRestriction | restriction.Modes());
                tilebuilder.SetDirectedEdgeBuilder((int)to.Id(), edge);

                tilebuilder.AddForwardComplexRestriction(complexRestriction);
                forwardCount++;
            }
        }
    }

    // ------------------------------------------------------------------
    // HandleOnlyRestrictionProperties
    // ------------------------------------------------------------------

    // Faithful port of HandleOnlyRestrictionProperties: write the cross-tile only_* restrictions and
    // mark the part-of-restriction edges, grouped by destination tile.
    private static void HandleOnlyRestrictionProperties(IReadOnlyList<Result> results, GraphReader reader)
    {
        var restrictions = new Dictionary<GraphId, List<ComplexRestrictionBuilder>>();
        var partOfRestriction = new Dictionary<GraphId, List<GraphId>>();
        foreach (Result res in results)
        {
            foreach (ComplexRestrictionBuilder restriction in res.Restrictions)
            {
                GraphId key = restriction.ToGraphId().TileBase();
                if (!restrictions.TryGetValue(key, out List<ComplexRestrictionBuilder>? list))
                {
                    list = new List<ComplexRestrictionBuilder>();
                    restrictions[key] = list;
                }

                list.Add(restriction);
            }

            foreach (GraphId edgeId in res.PartOfRestriction)
            {
                GraphId key = edgeId.TileBase();
                if (!partOfRestriction.TryGetValue(key, out List<GraphId>? list))
                {
                    list = new List<GraphId>();
                    partOfRestriction[key] = list;
                }

                list.Add(edgeId);
            }
        }

        foreach (KeyValuePair<GraphId, List<ComplexRestrictionBuilder>> entry in restrictions)
        {
            GraphTile? tile = reader.GetGraphTile(entry.Key);
            if (tile is null)
            {
                continue;
            }

            var tileBuilder = new GraphTileBuilder(tile);
            foreach (ComplexRestrictionBuilder restriction in entry.Value)
            {
                tileBuilder.AddForwardComplexRestriction(restriction);
                DirectedEdge edge = tileBuilder.DirectedEdgeBuilder((int)restriction.ToGraphId().Id());
                edge.SetEndRestriction(edge.EndRestriction | restriction.Modes());
                tileBuilder.SetDirectedEdgeBuilder((int)restriction.ToGraphId().Id(), edge);
            }

            tileBuilder.StoreTileData(reader.TileDir());
        }

        foreach (KeyValuePair<GraphId, List<GraphId>> entry in partOfRestriction)
        {
            GraphTile? tile = reader.GetGraphTile(entry.Key);
            if (tile is null)
            {
                continue;
            }

            var tileBuilder = new GraphTileBuilder(tile);
            foreach (GraphId edgeId in entry.Value)
            {
                DirectedEdge edge = tileBuilder.DirectedEdgeBuilder((int)edgeId.Id());
                edge.SetComplexRestriction(true);
                tileBuilder.SetDirectedEdgeBuilder((int)edgeId.Id(), edge);
            }

            tileBuilder.StoreTileData(reader.TileDir());
        }
    }

    // ------------------------------------------------------------------
    // Public Build entry point
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds complex turn restrictions to the graph tiles. Faithful port of
    /// <c>RestrictionBuilder::Build</c>. Iterates every hierarchy level (highest to lowest), reading
    /// each tile in the level, walking the from/to restrictions, and writing forward/reverse complex
    /// restrictions back to the tiles.
    /// </summary>
    /// <param name="reader">Graph reader bound to the tile directory being enhanced.</param>
    /// <param name="complexFromRestrictions">
    /// Restrictions keyed/sorted by the "from" way id (the parser output for complex-from). Must be
    /// sorted per <see cref="OSMRestriction"/> ordering.
    /// </param>
    /// <param name="complexToRestrictions">
    /// Restrictions keyed/sorted by the "to" way id stored as from() (the parser output for
    /// complex-to). Must be sorted per <see cref="OSMRestriction"/> ordering.
    /// </param>
    /// <returns>The aggregated per-level <see cref="Result"/>s.</returns>
    public static IReadOnlyList<Result> Build(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexFromRestrictions,
        IReadOnlyList<OSMRestriction> complexToRestrictions)
    {
        var allResults = new List<Result>();

        // Iterate through the tile levels from highest level to lowest (C++ uses reverse iterators).
        IReadOnlyList<TileLevel> levels = TileHierarchy.Levels();
        for (int li = levels.Count - 1; li >= 0; --li)
        {
            byte level = levels[li].Level;

            // PORT-NOTE: the C++ builds a randomized queue across threads. Here we run single-threaded
            // over the level's tile set in deterministic (sorted) order.
            var tileQueue = new List<GraphId>(reader.GetTileSet(level));
            tileQueue.Sort((a, b) => a.Value.CompareTo(b.Value));

            var stats = new Result();
            Build(complexFromRestrictions, complexToRestrictions, reader, tileQueue, stats);

            var results = new List<Result> { stats };
            HandleOnlyRestrictionProperties(results, reader);

            allResults.Add(stats);
        }

        return allResults;
    }
}
