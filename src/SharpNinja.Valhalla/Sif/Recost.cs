// Faithful C# port of Valhalla sif recost (valhalla @ 3.7.0).
// Sources:
//   - valhalla/sif/recost.h  (EdgeCallback / LabelCallback signatures + recost_forward declaration)
//   - src/sif/recost.cc      (the whole recost_forward, ~150 lines)
//   - src/thor/bidirectional_astar.cc (the file-local find_percent_along helper, ported as
//                                      Recost.FindPercentAlong)
//
// Takes a sequence of edges (delivered one id at a time through an edge callback) and re-creates the
// set of edge labels that would represent that path, emitting each through a label callback. This
// lets a caller re-compute the costing of a given path so that every reconstructed path edge gets a
// real per-edge elapsed_cost, transition_cost, and cumulative-from-origin path_distance.
//
// PORT-NOTES:
//   - C++ uses raw pointers (const DirectedEdge*, const NodeInfo*) that can be null; the ported
//     DirectedEdge/NodeInfo are value structs, so the null-able "current edge"/"previous node" are
//     modeled as nullable structs (DirectedEdge? / NodeInfo?).
//   - The out-of-bounds percent check throws std::logic_error in C++; the closest C# analogue that
//     preserves the "programmer error / invalid argument" intent is ArgumentOutOfRangeException.
//     Missing/filtered edges and nodes throw std::runtime_error in C++; these map to
//     InvalidOperationException (matching BidirectionalAStar.FormPath's tile-gone throw).
//   - Time / timezone: the C++ code advances a TimeInfo per edge (time_info.forward(seconds_offset,
//     node->timezone())). This port is time-independent elsewhere, so the effective TimeInfo is
//     TimeInfo.Invalid() unless the caller supplies one; forward() on an Invalid TimeInfo is a no-op,
//     which matches the rest of the port. The cross-timezone correction delegate is left null (no tz
//     database in this slice), i.e. behavior matches a route that never changes timezone.
//   - PathEdgeLabel's path_distance parameter is a uint (as in C++); the accumulated length is a
//     double truncated to uint on emission, exactly as the C++ narrowing conversion does.

using System;

using SharpNinja.Valhalla.Baldr;

// graph_tile_ptr alias so the sif costing signatures read like the C++ ones.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;

namespace SharpNinja.Valhalla.Sif;

/// <summary>
/// Re-costs a sequence of edges into the labels that would represent it. Faithful port of the free
/// functions in <c>valhalla::sif</c> (recost.h / recost.cc) plus the file-local
/// <c>find_percent_along</c> helper from bidirectional_astar.cc.
/// </summary>
public static class Recost
{
    /// <summary>
    /// Will take a sequence of edges and create the set of edge labels that would represent it. Allows
    /// the caller to essentially re-compute the costing of a given path. Faithful port of
    /// <c>recost_forward</c>.
    /// </summary>
    /// <param name="reader">Used to get access to graph data (modifiable because it has a cache).</param>
    /// <param name="costing">Single costing object used for costing/access computations.</param>
    /// <param name="edgeCb">The callback used to get each edge in the path (EdgeCallback).</param>
    /// <param name="labelCb">The callback used to emit each label in the path (LabelCallback).</param>
    /// <param name="sourcePct">The percent along the initial edge the source location is.</param>
    /// <param name="targetPct">The percent along the final edge the target location is.</param>
    /// <param name="timeInfo">
    /// The time tracking information representing the local time before traversing the first edge. When
    /// null, <see cref="TimeInfo.Invalid"/> is used (time-independent, matching the rest of the port).
    /// </param>
    /// <param name="invariant">Static date_time, don't offset the time as the path lengthens.</param>
    /// <param name="ignoreAccess">Ignore access restrictions for edges and nodes if true.</param>
    public static void Forward(
        GraphReader reader,
        DynamicCost costing,
        Func<GraphId> edgeCb,
        Action<PathEdgeLabel> labelCb,
        float sourcePct = 0.0f,
        float targetPct = 1.0f,
        TimeInfo? timeInfo = null,
        bool invariant = false,
        bool ignoreAccess = false)
    {
        // out of bounds edge scaling
        if (sourcePct < 0.0f || sourcePct > 1.0f || targetPct < 0.0f || targetPct > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourcePct), "Source and target percentages must be between 0 and 1 inclusive");
        }

        // PORT-NOTE: the effective time info (see file PORT-NOTE). Invalid() -> forward() is a no-op.
        TimeInfo time = timeInfo ?? TimeInfo.Invalid();

        // grab the first path edge
        GraphId edgeId = edgeCb();
        if (!edgeId.IsValid())
        {
            return;
        }

        // fetch the graph objects
        GraphTilePtr? tile = null;
        DirectedEdge? edge = reader.Directededge(edgeId, ref tile);

        // first edge is bogus
        if (!edge.HasValue)
        {
            throw new InvalidOperationException("Edge cannot be found");
        }

        // fail if the first edge is filtered
        if (!ignoreAccess && !costing.Allowed(edge.Value, tile!))
        {
            throw new InvalidOperationException(
                "This path requires different edge access than this costing allows");
        }

        edge = null;
        NodeInfo? node = null;

        // keep grabbing edges while we get valid ids
        var label = new PathEdgeLabel();
        uint predecessor = GraphConstants.InvalidLabel;
        var cost = new Cost();
        double length = 0;

        while (edgeId.IsValid())
        {
            // get the previous edge's node
            node = edge.HasValue ? reader.NodeInfo(edge.Value.EndNode, ref tile) : null;
            if (edge.HasValue && !node.HasValue)
            {
                throw new InvalidOperationException("Node cannot be found");
            }

            // grab the edge
            edge = reader.Directededge(edgeId, ref tile);
            if (!edge.HasValue)
            {
                throw new InvalidOperationException("Edge cannot be found");
            }

            // re-derive uturns, would have been nice to return this but we don't know the next edge yet
            label.SetDeadend(label.OppLocalIdx() == edge.Value.LocalEdgeIdx);

            // this node is not allowed, unless we made a uturn at it
            if (!ignoreAccess && node.HasValue && !label.Deadend() && !costing.Allowed(node.Value))
            {
                throw new InvalidOperationException(
                    "This path requires different node access than this costing allows");
            }

            // Update the time information even if time is invariant to account for timezones
            float secondsOffset = invariant ? 0.0f : cost.Secs;
            TimeInfo offsetTime = node.HasValue
                ? time.Forward(secondsOffset, (int)node.Value.Timezone())
                : time;

            // TODO: if this edge begins a restriction, we need to start popping off edges into a queue
            // so that we can find if we reach the end of the restriction. then we need to replay the
            // queued edges as normal
            byte timeRestrictionsTODO = GraphConstants.InvalidRestriction;
            byte destonlyRestrictionMask = 0;
            // if it's not time dependent set to 0 for the Allowed method below
            ulong localtime = offsetTime.Valid ? offsetTime.LocalTime : 0;
            // we should call 'Allowed' even if 'ignore_access' is true in order to evaluate time
            // restrictions
            GraphId nextId = edgeCb();
            if (predecessor != GraphConstants.InvalidLabel &&
                !costing.Allowed(edge.Value, !nextId.IsValid(), label, tile!, edgeId, localtime,
                                 (uint)offsetTime.TimezoneIndex, ref timeRestrictionsTODO,
                                 ref destonlyRestrictionMask) &&
                !ignoreAccess)
            {
                throw new InvalidOperationException(
                    "This path requires different edge access than this costing allows");
            }

            // how much of the edge will we use, trim if it's the first or last edge
            float edgePct = 1.0f;
            float start = 0.0f;
            float end = 1.0f;
            if (sourcePct != -1.0f)
            {
                edgePct -= sourcePct;
                start = sourcePct;
                sourcePct = -1.0f;
            }

            if (!nextId.IsValid())
            {
                edgePct -= 1.0f - targetPct;
                // just to keep compatibility with the logic that handled trivial path in bidiastar
                edgePct = Math.Max(0.0f, edgePct);
                end = targetPct;
            }

            // the cost for traversing this intersection
            Func<LimitedGraphReader> readerGetter = () => new LimitedGraphReader();
            Cost transitionCost = node.HasValue
                ? costing.TransitionCost(edge.Value, node.Value, label, tile!, readerGetter)
                : new Cost();
            // update the cost to the end of this edge
            byte flowSources = 0;
            cost += transitionCost + costing.PartialEdgeCost(edge.Value, new GraphId(GraphId.InvalidGraphId),
                                                             tile!, offsetTime, ref flowSources, start, end);
            // update the length to the end of this edge
            length += edge.Value.Length * edgePct;
            // construct the label

            InternalTurn turn = node.HasValue
                ? costing.TurnType(label.OppLocalIdx(), node.Value, edge.Value)
                : InternalTurn.NoTurn;
            label = new PathEdgeLabel(predecessor, edgeId, edge.Value, cost, cost.CostValue,
                                      costing.TravelMode(), (uint)length, transitionCost,
                                      timeRestrictionsTODO, !ignoreAccess,
                                      (flowSources & GraphConstants.DefaultFlowMask) != 0, turn);
            predecessor++;
            // hand back the label
            labelCb(label);
            // next edge
            edgeId = nextId;
        }
    }

    /// <summary>
    /// Returns the percent-along of the candidate edge that matches <paramref name="edgeId"/> for the
    /// correlated <paramref name="loc"/>. Faithful port of the file-local <c>find_percent_along</c> in
    /// bidirectional_astar.cc (which walks <c>location.correlation().edges()</c>).
    /// </summary>
    /// <param name="loc">The correlated location whose candidate edges are searched.</param>
    /// <param name="edgeId">The directed edge id to match.</param>
    /// <returns>The percent (0..1) along the matching candidate edge.</returns>
    /// <exception cref="InvalidOperationException">The edge id is not among the candidate edges.</exception>
    public static float FindPercentAlong(PathLocation loc, GraphId edgeId)
    {
        foreach (PathLocation.PathEdge e in loc.Edges)
        {
            if (e.Id == edgeId)
            {
                return (float)e.PercentAlong;
            }
        }

        throw new InvalidOperationException("Could not find candidate edge for the location");
    }
}
