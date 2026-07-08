// Faithful C# port of the mjolnir util helpers used by the graph filter / enhancer transition pass.
// Sources:
//   F:/github/valhalla/src/mjolnir/util.cc  (GetOpposingEdgeIndex, ProcessEdgeTransitions,
//                                            GetStopImpact, IsPencilPointUturn, IsCyclewayUturn,
//                                            shapes_match)
//   F:/github/valhalla/valhalla/mjolnir/util.h  (struct enhancer_stats)
//
// These functions are shared between graphenhancer.cc and graphfilter.cc. The slice ported here is
// exactly the subset that GraphFilter::UpdateOpposingIndexAndTransitions consumes:
//   - GetOpposingEdgeIndex  : find the opposing local-edge index at an edge's end node.
//   - ProcessEdgeTransitions: set turn type / edge-to-left/right / stop impact on a directed edge.
//   - GetStopImpact (+ IsPencilPointUturn / IsCyclewayUturn): the stop-impact heuristic.
//   - shapes_match          : do two shape vectors describe the same edge (either direction).
//   - enhancer_stats        : the per-thread stats accumulator (only pencilucount is touched here).
//
// EXCLUDED (not needed by GraphFilter): the build_tile_set pipeline, file/temp helpers, the
// elevation / admin / timezone helpers, and the rest of util.cc.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// A little struct to hold stats information during graph enhancement / filtering. Faithful port of
/// the C++ <c>struct enhancer_stats</c>.
/// </summary>
public sealed class EnhancerStats
{
    /// <summary>Maximum density (km/km2).</summary>
    public float MaxDensity { get; set; } = float.MinValue;

    /// <summary>Count of not-thru edges.</summary>
    public uint NotThru { get; set; }

    /// <summary>Count of nodes for which no country was found.</summary>
    public uint NoCountryFound { get; set; }

    /// <summary>Count of internal edges.</summary>
    public uint InternalCount { get; set; }

    /// <summary>Count of turn channels.</summary>
    public uint TurnChannelCount { get; set; }

    /// <summary>Count of ramps.</summary>
    public uint RampCount { get; set; }

    /// <summary>Count of pencil-point u-turns.</summary>
    public uint PencilUCount { get; set; }

    /// <summary>Density histogram counts (16 buckets).</summary>
    public uint[] DensityCounts { get; } = new uint[16];

    /// <summary>Merge another stats accumulator into this one. Faithful port of <c>operator()</c>.</summary>
    public void Merge(EnhancerStats other)
    {
        if (MaxDensity < other.MaxDensity)
        {
            MaxDensity = other.MaxDensity;
        }

        NotThru += other.NotThru;
        NoCountryFound += other.NoCountryFound;
        InternalCount += other.InternalCount;
        TurnChannelCount += other.TurnChannelCount;
        RampCount += other.RampCount;
        PencilUCount += other.PencilUCount;
        for (int i = 0; i < 16; i++)
        {
            DensityCounts[i] += other.DensityCounts[i];
        }
    }
}

/// <summary>
/// Mjolnir util helpers shared by the graph enhancer and graph filter. Faithful port of the
/// corresponding free functions in <c>src/mjolnir/util.cc</c>.
/// </summary>
public static class MjolnirUtil
{
    /// <summary>
    /// Do the 2 supplied shape vectors match (either direction). Returns true if the shapes match
    /// (one may be the reverse of the other). Faithful port of <c>shapes_match</c>.
    /// </summary>
    public static bool ShapesMatch(IReadOnlyList<PointLL> shape1, IReadOnlyList<PointLL> shape2)
    {
        if (shape1.Count != shape2.Count)
        {
            return false;
        }

        if (shape1[0].Equals(shape2[0]))
        {
            // Compare shape in forward direction.
            for (int i = 0; i < shape1.Count; i++)
            {
                if (!shape1[i].Equals(shape2[i]))
                {
                    return false;
                }
            }

            return true;
        }

        if (shape1[0].Equals(shape2[shape2.Count - 1]))
        {
            // Compare shape (reverse direction for shape2).
            for (int i = 0; i < shape1.Count; i++)
            {
                if (!shape1[i].Equals(shape2[shape2.Count - 1 - i]))
                {
                    return false;
                }
            }

            return true;
        }

        // LOG_WARN("Neither end of the shape matches");
        return false;
    }

    /// <summary>
    /// Gets the index of the opposing edge at the end node. This is on the local hierarchy, before
    /// adding transition and shortcut edges. Even if the end nodes and lengths match, the shape (or
    /// edgeinfo offset) is checked so the correct edge is selected (some loops have the same length
    /// and end node). Faithful port of <c>GetOpposingEdgeIndex</c>.
    /// </summary>
    /// <param name="endNodeTile">Graph tile at the end node.</param>
    /// <param name="startNode">Start node of the directed edge.</param>
    /// <param name="tile">Graph tile of the edge.</param>
    /// <param name="edge">Directed edge to match.</param>
    public static uint GetOpposingEdgeIndex(
        GraphTile endNodeTile,
        GraphId startNode,
        GraphTile tile,
        DirectedEdge edge)
    {
        // Get the nodeinfo at the end of the edge.
        NodeInfo nodeinfo = endNodeTile.Node((int)edge.EndNode.Id());

        // Iterate through the directed edges and return when the end node matches the specified node,
        // the length matches, and the shape matches (or edgeinfo offset matches).
        uint edgeIndex = nodeinfo.EdgeIndex;
        for (uint i = 0; i < nodeinfo.EdgeCount; i++)
        {
            DirectedEdge directededge = endNodeTile.DirectedEdge((int)(edgeIndex + i));
            if (directededge.EndNode == startNode && directededge.Length == edge.Length)
            {
                // If in the same tile and the edgeinfo offset matches then the shape and names match.
                if (ReferenceEquals(endNodeTile, tile) && directededge.EdgeInfoOffset == edge.EdgeInfoOffset)
                {
                    return i;
                }

                // Need to compare shape if not in the same tile or different EdgeInfo (could be
                // different names in opposing directions).
                if (ShapesMatch(tile.EdgeInfo(edge).Shape(), endNodeTile.EdgeInfo(directededge).Shape()))
                {
                    return i;
                }
            }
        }

        // LOG_ERROR("Could not find opposing edge index");
        return GraphConstants.MaxEdgesPerNode;
    }

    /// <summary>
    /// Process edge transitions from all other incoming edges onto the specified outbound directed
    /// edge. Faithful port of <c>ProcessEdgeTransitions</c>. The directed edge is mutated in place
    /// and returned.
    /// </summary>
    /// <param name="idx">Index of the directed edge - the "to" edge.</param>
    /// <param name="directededge">Directed edge to set values on.</param>
    /// <param name="edges">Other directed edges at the node (indexed by local index).</param>
    /// <param name="ntrans">Number of transitions (number of edges or max).</param>
    /// <param name="nodeinfo">Node info used for headings / drive on right / signals.</param>
    /// <param name="stats">Stats accumulator (pencil-point u-turn count).</param>
    public static void ProcessEdgeTransitions(
        uint idx,
        ref DirectedEdge directededge,
        IReadOnlyList<DirectedEdge> edges,
        uint ntrans,
        NodeInfo nodeinfo,
        EnhancerStats stats)
    {
        for (uint i = 0; i < ntrans; i++)
        {
            // Get the turn type (reverse the heading of the from directed edge since it is incoming).
            uint fromHeading = (nodeinfo.Heading(i) + 180) % 360;
            uint turnDegree = Util.GetTurnDegree(fromHeading, nodeinfo.Heading(idx));
            directededge.SetTurnType(i, Turn.GetType(turnDegree));

            // Set the edge_to_left and edge_to_right flags.
            uint rightCount = 0;
            uint leftCount = 0;
            if (ntrans > 2)
            {
                for (uint j = 0; j < ntrans; ++j)
                {
                    // Skip the from and to edges; also skip roads under construction.
                    if (j == i || j == idx || edges[(int)j].Use == Use.Construction)
                    {
                        continue;
                    }

                    // Get the turn degree from incoming edge i to j and check if right or left of the
                    // turn degree from incoming edge i onto idx.
                    uint degree = Util.GetTurnDegree(fromHeading, nodeinfo.Heading(j));
                    if (turnDegree > 180)
                    {
                        if (degree > turnDegree || degree < 180)
                        {
                            ++rightCount;
                        }
                        else if (degree < turnDegree && degree > 180)
                        {
                            ++leftCount;
                        }
                    }
                    else
                    {
                        if (degree > turnDegree && degree < 180)
                        {
                            ++rightCount;
                        }
                        else if (degree < turnDegree || degree > 180)
                        {
                            ++leftCount;
                        }
                    }
                }
            }

            directededge.SetEdgeToLeft(i, leftCount > 0);
            directededge.SetEdgeToRight(i, rightCount > 0);

            // Get stop impact.
            // NOTE: stop impact uses the right and left edges so this logic must come after the
            // right/left edge logic.
            uint stopimpact = GetStopImpact(i, idx, directededge, edges, ntrans, nodeinfo, turnDegree, stats);
            directededge.SetStopImpact(i, stopimpact);
        }
    }

    /// <summary>
    /// Returns true if the edge transition is a pencil point u-turn. Faithful port of
    /// <c>IsPencilPointUturn</c>.
    /// </summary>
    private static bool IsPencilPointUturn(
        uint fromIndex,
        uint toIndex,
        DirectedEdge directededge,
        IReadOnlyList<DirectedEdge> edges,
        NodeInfo nodeInfo,
        uint turnDegree)
    {
        DirectedEdge from = edges[(int)fromIndex];
        DirectedEdge to = edges[(int)toIndex];

        if (nodeInfo.DriveOnRight)
        {
            if ((((turnDegree > 179) && (turnDegree < 211)) ||
                 (((from.Length < 50) || (directededge.Length < 50)) &&
                  (turnDegree > 179) && (turnDegree < 226))) &&
                ((from.ForwardAccess & GraphConstants.AutoAccess) == 0 &&
                 (from.ReverseAccess & GraphConstants.AutoAccess) != 0) &&
                ((directededge.ForwardAccess & GraphConstants.AutoAccess) != 0 &&
                 (directededge.ReverseAccess & GraphConstants.AutoAccess) == 0) &&
                directededge.EdgeToRight(fromIndex) && !directededge.EdgeToLeft(fromIndex) &&
                to.NameConsistencyAt(fromIndex))
            {
                return true;
            }
        }
        else
        {
            if ((((turnDegree > 149) && (turnDegree < 181)) ||
                 (((from.Length < 50) || (directededge.Length < 50)) &&
                  (turnDegree > 134) && (turnDegree < 181))) &&
                ((from.ForwardAccess & GraphConstants.AutoAccess) == 0 &&
                 (from.ReverseAccess & GraphConstants.AutoAccess) != 0) &&
                ((directededge.ForwardAccess & GraphConstants.AutoAccess) != 0 &&
                 (directededge.ReverseAccess & GraphConstants.AutoAccess) == 0) &&
                !directededge.EdgeToRight(fromIndex) && directededge.EdgeToLeft(fromIndex) &&
                to.NameConsistencyAt(fromIndex))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the edge transition is a cycleway u-turn. Faithful port of
    /// <c>IsCyclewayUturn</c>.
    /// </summary>
    private static bool IsCyclewayUturn(
        uint fromIndex,
        uint toIndex,
        DirectedEdge directededge,
        IReadOnlyList<DirectedEdge> edges,
        NodeInfo nodeInfo,
        uint turnDegree)
    {
        DirectedEdge from = edges[(int)fromIndex];
        DirectedEdge to = edges[(int)toIndex];

        // We only deal with Cycleways.
        if (from.Use != Use.Cycleway || to.Use != Use.Cycleway)
        {
            return false;
        }

        if (nodeInfo.DriveOnRight)
        {
            if ((((turnDegree > 179) && (turnDegree < 211)) ||
                 (((from.Length < 50) || (directededge.Length < 50)) &&
                  (turnDegree > 179) && (turnDegree < 226))) &&
                directededge.EdgeToRight(fromIndex) && directededge.EdgeToLeft(fromIndex))
            {
                return true;
            }
        }
        else
        {
            if ((((turnDegree > 149) && (turnDegree < 181)) ||
                 (((from.Length < 50) || (directededge.Length < 50)) &&
                  (turnDegree > 134) && (turnDegree < 181))) &&
                directededge.EdgeToRight(fromIndex) && directededge.EdgeToLeft(fromIndex))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the stop likelihood / impact at an intersection when transitioning from one edge to
    /// another. Faithful port of <c>GetStopImpact</c>. Returns a value from 0 (no likely impact) to 7
    /// (large impact).
    /// </summary>
    public static uint GetStopImpact(
        uint from,
        uint to,
        DirectedEdge directededge,
        IReadOnlyList<DirectedEdge> edges,
        uint count,
        NodeInfo nodeinfo,
        uint turnDegree,
        EnhancerStats stats)
    {
        // Special cases.

        // Handle Roundabouts.
        if (edges[(int)from].Roundabout && edges[(int)to].Roundabout)
        {
            return 0;
        }

        // Handle Pencil point u-turn.
        if (IsPencilPointUturn(from, to, directededge, edges, nodeinfo, turnDegree))
        {
            stats.PencilUCount++;
            return 7;
        }

        // Handle Cycleway u-turn.
        if (IsCyclewayUturn(from, to, directededge, edges, nodeinfo, turnDegree))
        {
            return 7;
        }

        // Get the highest classification of other roads at the intersection.
        bool allRamps = true;
        bool foundOtherEdge = false;

        // kUnclassified, kResidential, and kServiceOther are grouped together for the stop_impact
        // logic.
        RoadClass bestrc = RoadClass.Unclassified;
        for (uint i = 0; i < count; i++)
        {
            DirectedEdge edge = edges[(int)i];

            // Check the road if it is drivable TO the intersection and is neither the "to" nor "from"
            // edge. Treat roundabout edges as two levels lower classification (higher value) to reduce
            // the stop impact.
            if (i != to && i != from && (edge.ReverseAccess & GraphConstants.AutoAccess) != 0)
            {
                if (edge.Roundabout)
                {
                    uint c = (uint)edge.Classification + 2;
                    if (c < (uint)bestrc)
                    {
                        bestrc = (RoadClass)c;
                    }
                }
                else if (edge.Classification < bestrc)
                {
                    bestrc = edge.Classification;
                }
            }

            // Track whether any other drivable edge exists at this node (in either direction).
            if (i != to && i != from && ((edge.ReverseAccess | edge.ForwardAccess) & GraphConstants.AutoAccess) != 0)
            {
                foundOtherEdge = true;
            }

            // Check if not a ramp or turn channel.
            if (!edge.Link)
            {
                allRamps = false;
            }
        }

        // No other drivable edges means this is not a real intersection. Return 0 so we don't add
        // phantom transition costs. Don't apply this to U-turns (from == to).
        if (!foundOtherEdge && from != to)
        {
            return 0;
        }

        // kUnclassified, kResidential, and kServiceOther are grouped together for the stop_impact
        // logic.
        RoadClass fromRc = edges[(int)from].Classification;
        if (fromRc > RoadClass.Unclassified)
        {
            fromRc = RoadClass.Unclassified;
        }

        // High stop impact from a turn channel onto a turn channel unless the other edge is a low
        // class road (walkways often intersect turn channels).
        if (edges[(int)from].Use == Use.TurnChannel && edges[(int)to].Use == Use.TurnChannel &&
            bestrc < RoadClass.Unclassified)
        {
            return 7;
        }

        // Set stop impact to the difference in road class (make it non-negative).
        int impact = (int)fromRc - (int)bestrc;
        uint stopImpact = impact < -3 ? 0u : (uint)(impact + 3);

        // Reduce stop impact from a turn channel or when only links (ramps and turn channels) are
        // involved. Exception - sharp turns.
        Turn.Type turnType = Turn.GetType(turnDegree);
        bool isSharp = turnType == Turn.Type.SharpLeft || turnType == Turn.Type.SharpRight ||
                       turnType == Turn.Type.Reverse;
        bool isSlight = turnType == Turn.Type.Straight || turnType == Turn.Type.SlightRight ||
                        turnType == Turn.Type.SlightLeft;
        if (allRamps)
        {
            if (isSharp)
            {
                stopImpact += 2;
            }
            else if (isSlight)
            {
                stopImpact /= 2;
            }
            else if (stopImpact != 0)
            {
                stopImpact -= 1;
            }
        }
        else if (edges[(int)from].Use == Use.Ramp && edges[(int)to].Use == Use.Ramp &&
                 bestrc < RoadClass.Unclassified)
        {
            // Ramp may be crossing a road (not a path or service road).
            if (nodeinfo.TrafficSignal || edges[(int)from].TrafficSignal || edges[(int)from].StopSign)
            {
                stopImpact = 4;
            }
            else if (count > 3)
            {
                stopImpact += 2;
            }
        }
        else if (edges[(int)from].Use == Use.Ramp && edges[(int)to].Use != Use.Ramp &&
                 !edges[(int)from].Internal && !edges[(int)to].Internal)
        {
            // Increase stop impact on merge.
            if (isSharp)
            {
                stopImpact += 3;
            }
            else if (isSlight)
            {
                stopImpact += 1;
            }
            else
            {
                stopImpact += 2;
            }
        }
        else if (edges[(int)from].Use == Use.TurnChannel)
        {
            // Penalize sharp turns.
            if (isSharp)
            {
                stopImpact += 2;
            }
            else if (edges[(int)to].Use == Use.Ramp)
            {
                stopImpact += 1;
            }
            else if (isSlight)
            {
                stopImpact /= 2;
            }
            else if (stopImpact != 0)
            {
                stopImpact -= 1;
            }
        }
        else if (edges[(int)from].Use == Use.ParkingAisle && edges[(int)to].Use == Use.ParkingAisle)
        {
            // Decrease stop impact inside parking lots.
            if (stopImpact != 0)
            {
                stopImpact -= 1;
            }
        }
        // Add to the stop impact when transitioning from higher to lower class road and we are not on
        // a TC or ramp. Penalize lefts when driving on the right.
        else if (nodeinfo.DriveOnRight &&
                 (turnType == Turn.Type.SharpLeft || turnType == Turn.Type.Left) &&
                 fromRc != edges[(int)to].Classification && edges[(int)to].Use != Use.Ramp &&
                 edges[(int)to].Use != Use.TurnChannel)
        {
            if (nodeinfo.TrafficSignal || edges[(int)from].TrafficSignal || edges[(int)from].StopSign)
            {
                stopImpact += 2;
            }
            else if (Math.Abs((int)fromRc - (int)edges[(int)to].Classification) > 1)
            {
                stopImpact++;
            }
        }
        // Penalize rights when driving on the left.
        else if (!nodeinfo.DriveOnRight &&
                 (turnType == Turn.Type.SharpRight || turnType == Turn.Type.Right) &&
                 fromRc != edges[(int)to].Classification && edges[(int)to].Use != Use.Ramp &&
                 edges[(int)to].Use != Use.TurnChannel)
        {
            if (nodeinfo.TrafficSignal || edges[(int)from].TrafficSignal || edges[(int)from].StopSign)
            {
                stopImpact += 2;
            }
            else if (Math.Abs((int)fromRc - (int)edges[(int)to].Classification) > 1)
            {
                stopImpact++;
            }
        }

        // Clamp to kMaxStopImpact.
        return stopImpact <= GraphConstants.MaxStopImpact ? stopImpact : GraphConstants.MaxStopImpact;
    }
}
