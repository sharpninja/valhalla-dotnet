// Faithful C# port of Valhalla thor UnidirectionalAStar (valhalla @ 3.7.0).
// Sources:
//   F:/github/valhalla/src/thor/unidirectional_astar.cc (930 LOC)
//   F:/github/valhalla/valhalla/thor/unidirectional_astar.h
//
// The time-dependent (depart-at / arrive-by) forward and reverse A* algorithm. This is the
// UNIDIRECTIONAL A* that the engine falls back to when a route is time-dependent (the default
// point-to-point path uses bidirectional A*; this is the fallback the task asks for). It is a single
// C++ class template parameterized on the expansion direction (forward/reverse); both
// specializations are reproduced here behind a single C# class plus the FORWARD flag (the engine's
// `UnidirectionalAStar<forward>` / `<reverse>` typedefs map to the static factories
// TimeDepForward()/TimeDepReverse()).
//
// PORT-NOTES (per task scope: point-to-point auto/truck only):
//   - The proto valhalla::Location origin/destination correlation output is consumed as the
//     already-ported baldr::PathLocation (its .Edges carry the loki-correlated PathEdges). Each proto
//     PathEdge field maps as: graph_id() -> Id, percent_along() -> PercentAlong, ll() -> Projected,
//     distance() -> Distance (the snap score), begin_node()/end_node() -> BeginNode()/EndNode().
//   - The expansion-tracking callback (set_track_expansion) is the /expansion debug hook (EXCLUDED
//     surface) but the base PathAlgorithm preserves the functor with plain C# enums, so the callback
//     points are reproduced faithfully against ExpansionEdgeStatus / ExpansionAlgoType.
//   - TimeInfo.make(...) depends on the proto Location date_time + the DateTime tz database (a later
//     port slice). For the single-timezone, depart-now/at-T case the engine builds a TimeInfo whose
//     forward()/reverse() arithmetic IS ported; here time info is taken from the (optional) supplied
//     TimeInfo on the start location, defaulting to TimeInfo.Invalid() (a non-time-dependent route),
//     which keeps EdgeCost/TransitionCost behavior identical to the engine when no time is set.
//   - costing_->Restricted / GetExemptedAccessRestrictions are ported onto the base DynamicCost (the
//     thor A* calls them through the base cost_ptr_t). Their time-dependent complex-restriction branch
//     defers to the excluded DateTime tz database (throws), exactly as the foundation handles it.
//   - The C++ EdgeMetadata exposes an EdgeStatusInfo* that the inner loop dereferences/writes; the C#
//     EdgeMetadata models that pointer as (backing array + index) via EdgeStatusRef / SetEdgeStatus.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

// graph_tile_ptr alias to read like the C++ signatures.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;

namespace SharpNinja.Valhalla.Thor;

/// <summary>
/// Time-dependent (depart-at / arrive-by) forward and reverse A* algorithm to create the
/// shortest / least cost path. Faithful port of the C++ class template
/// <c>UnidirectionalAStar&lt;ExpansionType, FORWARD&gt;</c>. The expansion direction is selected at
/// construction; use <see cref="TimeDepForward"/> / <see cref="TimeDepReverse"/> for the engine's
/// two typedefs.
/// </summary>
public sealed class UnidirectionalAStar : PathAlgorithm
{
    // Number of iterations to allow with no convergence to the destination.
    private const uint MaxIterationsWithoutConvergence = 1800000;

    // The expansion direction (forward = true), faithful to the FORWARD template parameter.
    private readonly bool _forward;

    // mode_ / travel_type_ / access_mode_.
    private TravelMode _mode;
    private byte _travelType;
    private uint _accessMode;

    // Hierarchy limits (copied from the costing so we can increment transition counts).
    private List<HierarchyLimits> _hierarchyLimits = new();

    // A* heuristic.
    private readonly AStarHeuristic _astarheuristic = new();

    // Current costing mode.
    private DynamicCost? _costing;

    // Vector of edge labels (requires access by index).
    private readonly List<BDEdgeLabel> _edgelabels = new();

    // Edge status. Mark edges that are in the adjacency list or settled.
    private readonly EdgeStatus _edgestatus = new();

    // Mark if an edge is a destination: keyed by edge id, values are the destination PathEdges. C++
    // is an unordered_multimap<GraphId, ref<const PathEdge>>; reproduced as a dictionary of lists.
    private readonly Dictionary<ulong, List<PathLocation.PathEdge>> _destinations = new();

    // Adjacency list - approximate double bucket sort.
    private readonly DoubleBucketQueue<BDEdgeLabel> _adjacencylist;

    // Best path found so far. C++ threads a std::pair<int32_t,float>& through Expand/ExpandInner; in
    // C# a ref tuple cannot be captured by the AddLabel local function, so the same mutable state is
    // carried by this small holder (Index == -1 means "no connection yet", matching the C++ pair).
    private sealed class BestPath
    {
        public int Index = -1;
        public float Cost;
    }

    /// <summary>
    /// Constructor. Faithful port of <c>UnidirectionalAStar(config)</c> (the config defaults are
    /// taken from <c>edgelabel.h</c> / the base PathAlgorithm). Prefer <see cref="TimeDepForward"/> /
    /// <see cref="TimeDepReverse"/>.
    /// </summary>
    /// <param name="forward">True for the forward (depart-at) specialization, false for reverse (arrive-by).</param>
    /// <param name="maxReservedLabelsCount">Max reserved edge labels (C++ max_reserved_labels_count_astar).</param>
    /// <param name="clearReservedMemory">Whether to clear reserved memory on Clear (C++ clear_reserved_memory).</param>
    public UnidirectionalAStar(
        bool forward,
        uint maxReservedLabelsCount = EdgeLabelConstants.InitialEdgeLabelCountAstar,
        bool clearReservedMemory = false)
        : base(maxReservedLabelsCount, clearReservedMemory)
    {
        _forward = forward;
        _mode = TravelMode.Drive;
        _travelType = 0;
        _accessMode = GraphConstants.AutoAccess;
        _adjacencylist = new DoubleBucketQueue<BDEdgeLabel>();
    }

    /// <summary>Creates the forward (depart-at) specialization. Mirrors the C++ <c>TimeDepForward</c> typedef.</summary>
    public static UnidirectionalAStar TimeDepForward(
        uint maxReservedLabelsCount = EdgeLabelConstants.InitialEdgeLabelCountAstar,
        bool clearReservedMemory = false)
        => new UnidirectionalAStar(true, maxReservedLabelsCount, clearReservedMemory);

    /// <summary>Creates the reverse (arrive-by) specialization. Mirrors the C++ <c>TimeDepReverse</c> typedef.</summary>
    public static UnidirectionalAStar TimeDepReverse(
        uint maxReservedLabelsCount = EdgeLabelConstants.InitialEdgeLabelCountAstar,
        bool clearReservedMemory = false)
        => new UnidirectionalAStar(false, maxReservedLabelsCount, clearReservedMemory);

    /// <inheritdoc/>
    public override string Name() => _forward ? "time_dependent_forward_a*" : "time_dependent_reverse_a*";

    /// <summary>
    /// Clear the temporary information generated during path construction. Faithful port of
    /// <c>Clear()</c>.
    /// </summary>
    public override void Clear()
    {
        // Clear the edge labels and destination list. Reset the adjacency list and clear edge status.
        // C# labels are GC-managed; the C++ resize/shrink_to_fit reduces to clearing the list. The
        // reservation branch is preserved for parity even though it is a no-op on a managed list.
        uint reservation = ClearReservedMemory_ ? 0 : MaxReservedLabelsCount_;
        if (_edgelabels.Count > reservation)
        {
            _edgelabels.Capacity = (int)reservation;
        }

        _edgelabels.Clear();
        _destinations.Clear();
        _adjacencylist.Clear();
        _edgestatus.Clear();

        // Set the ferry flag to false.
        HasFerry_ = false;
    }

    /// <summary>
    /// Form path between an origin and destination location using the supplied costing method.
    /// Faithful port of <c>GetBestPath</c>.
    /// </summary>
    /// <param name="origin">Origin location (loki-correlated).</param>
    /// <param name="dest">Destination location (loki-correlated).</param>
    /// <param name="graphreader">Graph reader for accessing the routing graph.</param>
    /// <param name="modeCosting">Costing methods (indexed by travel mode).</param>
    /// <param name="mode">Travel mode to use.</param>
    /// <param name="options">Request options (unused by this algorithm; preserved for parity).</param>
    /// <returns>The path edges (and elapsed time/modes at the end of each edge). Empty if no route.</returns>
    public override List<List<PathInfo>> GetBestPath(
        PathLocation origin,
        PathLocation dest,
        GraphReader graphreader,
        ModeCosting modeCosting,
        TravelMode mode,
        Options? options = null)
    {
        // Set the mode and costing.
        _mode = mode;
        _costing = modeCosting[(int)_mode] ?? throw new InvalidOperationException("No costing for travel mode");
        _travelType = _costing.TravelType();
        _accessMode = _costing.AccessMode();

        if (!_forward)
        {
            // date_time must be set on the destination. Log an error but allow routes for now.
            if (string.IsNullOrEmpty(dest.DateTime))
            {
                // LOG_ERROR: TimeDepReverse called without time set on the destination location.
                // (C++ logs and continues; we do the same.)
            }
        }

        // Initialize - create adjacency list, edgestatus support, A*, etc.
        // Note: because we can correlate to more than one place for a given PathLocation, using
        // edges[0] here means we only set the heuristics to one of them; alternate paths using the
        // other correlated points may be harder to find.
        var originNew = new PointLL(origin.Edges[0].Projected.Lng, origin.Edges[0].Projected.Lat);
        var destinationNew = new PointLL(dest.Edges[0].Projected.Lng, dest.Edges[0].Projected.Lat);
        Init(originNew, destinationNew);
        float mindist = _astarheuristic.GetDistance(_forward ? originNew : destinationNew);

        PathLocation startpoint = _forward ? origin : dest;
        PathLocation endpoint = _forward ? dest : origin;

        // Get time information for the start point. PORT-NOTE: TimeInfo.make depends on the proto
        // date_time + tz database (later slice); use the supplied start TimeInfo or Invalid().
        TimeInfo timeInfo = startpoint.TimeInfo ?? TimeInfo.Invalid();

        // Initialize the origin and destination locations. Initialize the destination first in case
        // the origin edge includes a destination edge.
        uint density = SetDestination(graphreader, endpoint);
        SetOrigin(graphreader, startpoint, endpoint, timeInfo);

        // Update hierarchy limits.
        ModifyHierarchyLimits(mindist, density);

        // Find shortest path.
        uint nc = 0; // Count of iterations with no convergence towards destination.
        var bestPath = new BestPath();
        long n = 0;
        while (true)
        {
            // Allow this process to be aborted.
            if (Interrupt is not null && (++n % InterruptIterationsInterval) == 0)
            {
                Interrupt();
            }

            // Get the next element from the adjacency list. Check that it is valid. An invalid label
            // indicates there are no edges that can be expanded.
            uint predindex = _adjacencylist.Pop();
            if (predindex == GraphConstants.InvalidLabel)
            {
                // LOG_ERROR: Route failed after iterations = edgelabels_.size().
                return new List<List<PathInfo>>();
            }

            // Copy the EdgeLabel for use in costing. Check if this is a destination edge and
            // potentially complete the path.
            BDEdgeLabel pred = _edgelabels[(int)predindex];

            if (pred.Destination())
            {
                if (ExpansionCallback_ is not null)
                {
                    ExpansionAlgoType expansionType = _forward ? ExpansionAlgoType.Forward : ExpansionAlgoType.Reverse;
                    GraphId prevPred = pred.Predecessor() == GraphConstants.InvalidLabel
                        ? GraphId.Invalid
                        : _edgelabels[(int)pred.Predecessor()].Edgeid();
                    ExpansionCallback_(graphreader, pred.Edgeid(), prevPred, "unidirectional_astar",
                        ExpansionEdgeStatus.Connected, pred.Cost().Secs, pred.PathDistance(),
                        pred.Cost().CostValue, expansionType, GraphConstants.NoFlowMask, TravelMode.MaxTravelMode);
                }

                return new List<List<PathInfo>> { FormPath(predindex) };
            }

            // Mark the edge as permanently labeled. Do not do this for an origin edge (this allows
            // loops / around-the-block cases).
            if (!pred.Origin())
            {
                _edgestatus.Update(pred.Edgeid(), EdgeSet.Permanent);
            }

            // Setting this edge as settled.
            if (ExpansionCallback_ is not null)
            {
                ExpansionAlgoType expansionType = _forward ? ExpansionAlgoType.Forward : ExpansionAlgoType.Reverse;
                GraphId prevPred = pred.Predecessor() == GraphConstants.InvalidLabel
                    ? GraphId.Invalid
                    : _edgelabels[(int)pred.Predecessor()].Edgeid();
                ExpansionCallback_(graphreader, pred.Edgeid(), prevPred, "unidirectional_astar",
                    ExpansionEdgeStatus.Settled, pred.Cost().Secs, pred.PathDistance(),
                    pred.Cost().CostValue, expansionType, GraphConstants.NoFlowMask, TravelMode.MaxTravelMode);
            }

            // Check that distance is converging towards the destination. Return route failure if no
            // convergence for MaxIterationsWithoutConvergence iterations. NOTE: due to a somewhat high
            // penalty for entering a destination-only (private) road this value needs to be high.
            float dist2dest = pred.Distance();
            if (dist2dest < mindist)
            {
                mindist = dist2dest;
                nc = 0;
            }
            else if (nc++ > MaxIterationsWithoutConvergence)
            {
                if (bestPath.Index >= 0)
                {
                    return new List<List<PathInfo>> { FormPath((uint)bestPath.Index) };
                }


                // LOG_ERROR: No convergence to destination after = edgelabels_.size().
                return new List<List<PathInfo>>();
            }

            // Do not expand based on hierarchy level based on number of upward transitions and
            // distance to the destination.
            if (HierarchyLimitsFunctions.StopExpanding(_hierarchyLimits[(int)pred.Endnode().Level()], dist2dest))
            {
                continue;
            }

            // Get the opposing predecessor directed edge. Need to make sure we get the correct one if
            // a transition occurred.
            DirectedEdge? oppPredEdge = null;
            if (!_forward)
            {
                GraphTilePtr? oppPredTile = graphreader.GetGraphTile(pred.OppEdgeid());
                oppPredEdge = oppPredTile!.DirectedEdge(pred.OppEdgeid());
            }

            // Expand forward from the end node of the predecessor edge.
            Expand(graphreader, pred.Endnode(), pred, predindex, oppPredEdge, timeInfo, dest, bestPath);
        }
    }

    // Set the mode and costing; initialize prior to finding best path. Faithful port of Init.
    private void Init(PointLL origll, PointLL destll)
    {
        float mincost;
        if (_forward)
        {
            _astarheuristic.Init(destll, _costing!.AStarCostFactor());
            mincost = _astarheuristic.Get(origll);
        }
        else
        {
            _astarheuristic.Init(origll, _costing!.AStarCostFactor());
            mincost = _astarheuristic.Get(destll);
        }

        // edgelabels_.reserve(min(max_reserved_labels_count_, kInitialEdgeLabelCountAstar)) is a
        // capacity hint on a managed list.
        _edgelabels.Capacity = (int)Math.Min(MaxReservedLabelsCount_, EdgeLabelConstants.InitialEdgeLabelCountAstar);

        // Construct the adjacency list, clear edge status. Set the bucket size and cost range based on
        // the DynamicCost.
        uint bucketsize = _costing.UnitSize();
        float range = BucketCount * bucketsize;
        _adjacencylist.Reuse(mincost, range, bucketsize, _edgelabels);
        _edgestatus.Clear();

        // Get the hierarchy limits from the costing. Get a copy since we increment transition counts.
        _hierarchyLimits = CopyHierarchyLimits(_costing.GetHierarchyLimits());
    }

    // Faithful copy of the costing's hierarchy limits (the C++ assigns a vector copy; the C#
    // HierarchyLimits is a reference type, so we deep-copy to avoid mutating the costing's copy).
    private static List<HierarchyLimits> CopyHierarchyLimits(List<HierarchyLimits> source)
    {
        var copy = new List<HierarchyLimits>(source.Count);
        foreach (HierarchyLimits hl in source)
        {
            copy.Add(new HierarchyLimits
            {
                UpTransitionCount = hl.UpTransitionCount,
                MaxUpTransitions = hl.MaxUpTransitions,
                ExpandWithinDist = hl.ExpandWithinDist,
            });
        }

        return copy;
    }

    // Modulate the hierarchy expansion within distance based on density at the destination and the
    // distance between origin and destination. Faithful port of ModifyHierarchyLimits.
    private void ModifyHierarchyLimits(float dist, uint density)
    {
        if (!_costing!.GetDefaultHierarchyLimits())
        {
            return;
        }

        // TODO (engine) - default distance below which we increase expansion within distance.
        float factor = 1.0f;
        if (25000.0f < dist && dist < 100000.0f)
        {
            factor = Math.Min(3.0f, 100000.0f / dist);
        }

        // TODO (engine) - density factor near the destination is commented out in the engine.
        // Just arterial (level 1) for now.
        _hierarchyLimits[1].SetExpandWithinDist(_hierarchyLimits[1].ExpandWithinDist * factor);
    }

    // Expand from the node along the search path. Immediately expands from the end node of any
    // transition edge. Faithful port of Expand. Returns whether any edge could have been expanded.
    private bool Expand(
        GraphReader graphreader,
        GraphId node,
        BDEdgeLabel pred,
        uint predIdx,
        DirectedEdge? oppPredEdge,
        TimeInfo timeInfo,
        PathLocation destination,
        BestPath bestPath)
    {
        // Get the tile and the node info. Skip if the tile is null or if there is no access at the node.
        GraphTilePtr? tile = graphreader.GetGraphTile(node);
        if (tile is null)
        {
            return false;
        }

        NodeInfo nodeinfo = tile.Node(node);

        // Update the time information.
        TimeInfo offsetTime = _forward
            ? timeInfo.Forward(pred.Cost().Secs, (int)nodeinfo.Timezone())
            : timeInfo.Reverse(pred.Cost().Secs, (int)nodeinfo.Timezone());

        if (!_costing!.Allowed(nodeinfo))
        {
            GraphTilePtr? oppTile = tile;
            GraphId oppEdgeId = graphreader.GetOpposingEdgeId(pred.Edgeid(), out DirectedEdge? oppEdge, ref oppTile);

            // Check if the edge is null before using it (can happen with regional data sets).
            pred.SetDeadend(true);
            if (oppEdge is null)
            {
                return false;
            }

            (EdgeStatusInfo[] arr, int idx) = _edgestatus.GetPtr(oppEdgeId, oppTile!);
            var oppMeta = EdgeMetadata.MakeAt(oppEdge.Value, oppEdgeId, arr, idx, oppTile!);
            return ExpandInner(graphreader, pred, oppPredEdge, nodeinfo, predIdx, oppMeta, tile, offsetTime,
                destination, bestPath);
        }

        // Expand from the node.
        EdgeMetadata meta = EdgeMetadata.Make(node, nodeinfo, tile, _edgestatus);

        bool disableUturn = false;
        EdgeMetadata uturnMeta = default;
        bool haveUturn = false;

        for (uint i = 0; i < nodeinfo.EdgeCount; ++i, meta = meta.Increment())
        {
            // Begin by checking if this is the opposing edge to pred. If so, we are attempting a
            // u-turn; wait with evaluating this edge until last. If any other edges were emplaced, do
            // not even try a u-turn (u-turns should only happen for deadends).
            if (pred.OppLocalIdx() == meta.Edge.LocalEdgeIdx)
            {
                uturnMeta = meta;
                haveUturn = true;
            }

            // Expand but only if this isn't the uturn (try that later if nothing else works out).
            disableUturn = (pred.OppLocalIdx() != meta.Edge.LocalEdgeIdx &&
                            ExpandInner(graphreader, pred, oppPredEdge, nodeinfo, predIdx, meta, tile,
                                offsetTime, destination, bestPath)) || disableUturn;
        }

        // Handle transitions - expand from the end node of each transition.
        if (nodeinfo.TransitionCount > 0)
        {
            for (uint i = 0; i < nodeinfo.TransitionCount; ++i)
            {
                NodeTransition trans = tile.Transition(nodeinfo.TransitionIndex + i);

                // If this is a downward transition (ups are always allowed) AND we are no longer
                // allowed, OR we can't get the tile at that level, then bail.
                GraphTilePtr? transTile;
                if ((!trans.Up() &&
                     HierarchyLimitsFunctions.StopExpanding(_hierarchyLimits[(int)trans.EndNode().Level()], pred.Distance())) ||
                    (transTile = graphreader.GetGraphTile(trans.EndNode())) is null)
                {
                    continue;
                }

                // Set up for expansion at this level.
                _hierarchyLimits[(int)node.Level()].SetUpTransitionCount(
                    _hierarchyLimits[(int)node.Level()].UpTransitionCount + (trans.Up() ? 1u : 0u));
                NodeInfo transNode = transTile.Node(trans.EndNode());
                EdgeMetadata transMeta = EdgeMetadata.Make(trans.EndNode(), transNode, transTile, _edgestatus);

                // Expand the edges from this node at this level.
                for (uint j = 0; j < transNode.EdgeCount; ++j, transMeta = transMeta.Increment())
                {
                    disableUturn = ExpandInner(graphreader, pred, oppPredEdge, transNode, predIdx, transMeta,
                        transTile, offsetTime, destination, bestPath) || disableUturn;
                }
            }
        }

        // Now, after looking at all the edges (including edges on other levels), we can say if this is
        // a deadend; if so, evaluate the uturn-edge (if it exists).
        if (!disableUturn && haveUturn)
        {
            // We found no suitable edge to add, so we're at a deadend; re-evaluate a potential u-turn.
            pred.SetDeadend(true);

            // We didn't add any shortcut of the uturn, therefore evaluate the regular uturn instead.
            disableUturn = ExpandInner(graphreader, pred, oppPredEdge, nodeinfo, predIdx, uturnMeta, tile,
                offsetTime, destination, bestPath) || disableUturn;
        }

        return disableUturn;
    }

    // Runs in the inner loop of Expand, evaluating if the edge described in meta should be placed on
    // the stack as well as doing just that. Returns true if any edge could have been expanded after
    // restrictions etc. Faithful port of ExpandInner.
    private bool ExpandInner(
        GraphReader graphreader,
        BDEdgeLabel pred,
        DirectedEdge? oppPredEdge,
        NodeInfo nodeinfo,
        uint predIdx,
        EdgeMetadata meta,
        GraphTilePtr tile,
        TimeInfo timeInfo,
        PathLocation destination,
        BestPath bestPath)
    {
        // Skip shortcut edges for time dependent routes.
        if (meta.Edge.IsShortcut)
        {
            return false;
        }

        if (!_forward)
        {
            // Skip this edge if no access possible.
            if ((meta.Edge.ReverseAccess & _accessMode) == 0)
            {
                return false;
            }
        }

        // Skip this edge if permanently labeled (best path already found to this directed edge).
        if (meta.EdgeStatusRef.Set() == EdgeSet.Permanent)
        {
            return true; // This is an edge we could have expanded, so return true.
        }

        GraphId oppEdgeId = GraphId.Invalid;
        DirectedEdge? oppEdge = null;
        GraphTilePtr? endtile = meta.Edge.LeavesTile ? graphreader.GetGraphTile(meta.Edge.EndNode) : tile;
        if (endtile is null)
        {
            return false;
        }

        if (!_forward)
        {
            oppEdgeId = endtile.GetOpposingEdgeId(meta.Edge);
            oppEdge = endtile.DirectedEdge(oppEdgeId);
        }

        // Compute the cost to the end of this edge.
        byte flowSources = 0;
        Cost edgeCost = _forward
            ? _costing!.EdgeCost(meta.Edge, meta.EdgeId, tile, timeInfo, ref flowSources)
            : _costing!.EdgeCost(oppEdge!.Value, oppEdgeId, endtile, timeInfo, ref flowSources);

        // PORT-NOTE: the C++ reader_getter returns a baldr::LimitedGraphReader the costers can use to
        // fetch the predecessor tile; the ported transition-cost signatures take the Sif stub overload.
        Func<Sif.LimitedGraphReader> readerGetter = () => new Sif.LimitedGraphReader();

        Cost transitionCost = _forward
            ? _costing.TransitionCost(meta.Edge, nodeinfo, pred, tile, readerGetter)
            : _costing.TransitionCostReverse(meta.Edge.LocalEdgeIdx, nodeinfo, oppEdge!.Value, oppPredEdge ?? default,
                endtile, pred.Edgeid(), readerGetter, (flowSources & GraphConstants.DefaultFlowMask) != 0,
                pred.InternalTurn());

        PointLL endpoint = endtile.GetNodeLl(meta.Edge.EndNode);

        // add_label closure: returns true if a label was added. dest_path_edge is null for a plain
        // edge, or the destination PathEdge for a destination edge.
        bool AddLabel(PathLocation.PathEdge? destPathEdge)
        {
            byte restrictionIdx = GraphConstants.InvalidRestriction;
            byte destonlyRestrictionMask = pred.DestonlyAccessRestrMask();
            if (_forward)
            {
                if (!_costing.Allowed(meta.Edge, destPathEdge is not null, pred, tile, meta.EdgeId,
                        timeInfo.LocalTime, (uint)nodeinfo.Timezone(), ref restrictionIdx, ref destonlyRestrictionMask) ||
                    _costing.Restricted(meta.Edge, pred, _edgelabels, tile, meta.EdgeId, true, _edgestatus,
                        timeInfo.LocalTime, (uint)nodeinfo.Timezone()))
                {
                    return false;
                }
            }
            else
            {
                if (!_costing.AllowedReverse(meta.Edge, pred, oppEdge!.Value, endtile, oppEdgeId,
                        timeInfo.LocalTime, (uint)nodeinfo.Timezone(), ref restrictionIdx, ref destonlyRestrictionMask) ||
                    _costing.Restricted(meta.Edge, pred, _edgelabels, tile, meta.EdgeId, false, _edgestatus,
                        timeInfo.LocalTime, (uint)nodeinfo.Timezone()))
                {
                    return false;
                }
            }

            float percentTraversed = destPathEdge is null
                ? 1.0f
                : (_forward ? (float)destPathEdge.PercentAlong : 1.0f - (float)destPathEdge.PercentAlong);

            Cost cost = pred.Cost() + transitionCost + (edgeCost * percentTraversed);
            cost.CostValue += destPathEdge is not null ? (float)destPathEdge.Distance : 0.0f;

            float dist = 0.0f;
            float sortcost = cost.CostValue +
                             (destPathEdge is not null ? _astarheuristic.Get(0) : _astarheuristic.Get(endpoint, out dist));

            var pathDistance = (uint)(pred.PathDistance() + (meta.Edge.Length * percentTraversed) + 0.5f);

            // Add the EdgeLabel to the adjacency list and set status.
            uint idx = (uint)_edgelabels.Count;

            if (destPathEdge is not null && (bestPath.Index == -1 || cost.CostValue < bestPath.Cost))
            {
                // Mark this as the best connection if that applies. This allows a path to be formed
                // even if the convergence test fails (can happen with large edge scores).
                bestPath.Index = (int)idx;
                bestPath.Cost = cost.CostValue;
            }

            if (_forward)
            {
                _edgelabels.Add(new BDEdgeLabel(predIdx, meta.EdgeId, oppEdgeId, meta.Edge, cost, sortcost, dist,
                    _mode, transitionCost,
                    pred.NotThruPruning() || !meta.Edge.NotThru,
                    pred.ClosurePruning() || !_costing.IsClosed(meta.Edge, tile, meta.EdgeId.Id()),
                    (flowSources & GraphConstants.DefaultFlowMask) != 0,
                    _costing.TurnType(pred.OppLocalIdx(), nodeinfo, meta.Edge),
                    restrictionIdx, 0,
                    meta.Edge.DestOnly || (_costing.IsHgv() && meta.Edge.DestOnlyHgv),
                    (meta.Edge.ForwardAccess & GraphConstants.TruckAccess) != 0, destonlyRestrictionMask));
            }
            else
            {
                _edgelabels.Add(new BDEdgeLabel(predIdx, meta.EdgeId, oppEdgeId, meta.Edge, cost, sortcost, dist,
                    _mode, transitionCost,
                    pred.NotThruPruning() || !meta.Edge.NotThru,
                    pred.ClosurePruning() || !_costing.IsClosed(oppEdge!.Value, endtile, oppEdgeId.Id()),
                    (flowSources & GraphConstants.DefaultFlowMask) != 0,
                    _costing.TurnType(meta.Edge.LocalEdgeIdx, nodeinfo, oppEdge!.Value, oppPredEdge),
                    restrictionIdx, 0,
                    oppEdge!.Value.DestOnly || (_costing.IsHgv() && oppEdge!.Value.DestOnlyHgv),
                    (oppEdge!.Value.ForwardAccess & GraphConstants.TruckAccess) != 0, destonlyRestrictionMask));
            }

            BDEdgeLabel edgeLabel = _edgelabels[^1];

            // BDEdgeLabel can't set dist and path_distance at the same time, so update immediately to
            // set path_distance.
            edgeLabel.Update(predIdx, cost, sortcost, transitionCost, pathDistance, restrictionIdx);

            if (destPathEdge is not null)
            {
                edgeLabel.SetDestination();
            }

            _adjacencylist.Add(idx);
            if (destPathEdge is null)
            {
                // Only non-destination labels get an edge status.
                meta.SetEdgeStatus(new EdgeStatusInfo(EdgeSet.Temporary, idx));
            }

            if (ExpansionCallback_ is not null)
            {
                ExpansionAlgoType expansionType = _forward ? ExpansionAlgoType.Forward : ExpansionAlgoType.Reverse;
                GraphId prevPred = pred.Predecessor() == GraphConstants.InvalidLabel
                    ? GraphId.Invalid
                    : _edgelabels[(int)pred.Predecessor()].Edgeid();
                ExpansionCallback_(graphreader, _forward ? meta.EdgeId : oppEdgeId, prevPred, "unidirectional_astar",
                    ExpansionEdgeStatus.Reached, cost.Secs, pathDistance, cost.CostValue, expansionType, flowSources,
                    TravelMode.MaxTravelMode);
            }

            return true;
        }

        bool added;

        // Check if the edge is temporarily labeled and this path has less cost. If less cost, the
        // predecessor is updated and the sort cost is decremented by the difference in real cost (the
        // A* heuristic doesn't change).
        if (meta.EdgeStatusRef.Set() == EdgeSet.Temporary)
        {
            bool UpdateLabel()
            {
                byte restrictionIdx = GraphConstants.InvalidRestriction;
                byte destonlyRestrictionMask = pred.DestonlyAccessRestrMask();
                if (_forward)
                {
                    if (!_costing.Allowed(meta.Edge, false, pred, tile, meta.EdgeId, timeInfo.LocalTime,
                            (uint)nodeinfo.Timezone(), ref restrictionIdx, ref destonlyRestrictionMask) ||
                        _costing.Restricted(meta.Edge, pred, _edgelabels, tile, meta.EdgeId, true, _edgestatus,
                            timeInfo.LocalTime, (uint)nodeinfo.Timezone()))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!_costing.AllowedReverse(meta.Edge, pred, oppEdge!.Value, endtile, oppEdgeId,
                            timeInfo.LocalTime, (uint)nodeinfo.Timezone(), ref restrictionIdx, ref destonlyRestrictionMask) ||
                        _costing.Restricted(meta.Edge, pred, _edgelabels, tile, meta.EdgeId, false, _edgestatus,
                            timeInfo.LocalTime, (uint)nodeinfo.Timezone()))
                    {
                        return false;
                    }
                }

                BDEdgeLabel lab = _edgelabels[(int)meta.EdgeStatusRef.Index()];
                Cost newcost = pred.Cost() + transitionCost + edgeCost;

                if (newcost.CostValue < lab.Cost().CostValue)
                {
                    float newsortcost = lab.Sortcost() - (lab.Cost().CostValue - newcost.CostValue);
                    _adjacencylist.Decrease(meta.EdgeStatusRef.Index(), newsortcost);
                    lab.Update(predIdx, newcost, newsortcost, transitionCost, restrictionIdx);
                }

                if (ExpansionCallback_ is not null)
                {
                    ExpansionAlgoType expansionType = _forward ? ExpansionAlgoType.Forward : ExpansionAlgoType.Reverse;
                    GraphId prevPred = pred.Predecessor() == GraphConstants.InvalidLabel
                        ? GraphId.Invalid
                        : _edgelabels[(int)pred.Predecessor()].Edgeid();
                    ExpansionCallback_(graphreader, _forward ? meta.EdgeId : oppEdgeId, prevPred, "unidirectional_astar",
                        ExpansionEdgeStatus.Reached, newcost.Secs, lab.PathDistance(), newcost.CostValue, expansionType,
                        flowSources, TravelMode.MaxTravelMode);
                }

                return true;
            }

            added = UpdateLabel();
        }
        else
        {
            // Add as a normal edge (fixes engine issue #3585).
            added = AddLabel(null);
        }

        if (_destinations.TryGetValue((_forward ? meta.EdgeId : oppEdgeId).Value, out List<PathLocation.PathEdge>? dests))
        {
            foreach (PathLocation.PathEdge destPathEdge in dests)
            {
                added = AddLabel(destPathEdge) || added;
            }
        }

        return added;
    }

    // Add an edge at the origin to the adjacency list. Faithful port of SetOrigin.
    private void SetOrigin(GraphReader graphreader, PathLocation origin, PathLocation destination, TimeInfo timeInfo)
    {
        // Only skip inbound edges if we have other options.
        bool hasOtherEdges = false;
        foreach (PathLocation.PathEdge e in origin.Edges)
        {
            hasOtherEdges = hasOtherEdges || (_forward ? !e.EndNode() : !e.BeginNode());
        }

        // super_trivial: it's super trivial if both are node snapped to the same end of the same edge
        // (the node-snapping check is in the loop below, not in this lambda). Faithful port.
        bool SuperTrivial(PathLocation.PathEdge edge)
        {
            if (_destinations.TryGetValue(edge.Id.Value, out List<PathLocation.PathEdge>? dests))
            {
                foreach (PathLocation.PathEdge destPathEdge in dests)
                {
                    if (edge.PercentAlong == destPathEdge.PercentAlong)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Iterate through edges and add to the adjacency list.
        foreach (PathLocation.PathEdge edge in origin.Edges)
        {
            // If this is a node snap and we have other candidates, skip this unless it's the one we
            // need for a super-trivial route.
            if ((_forward ? edge.EndNode() : edge.BeginNode()) && hasOtherEdges && !SuperTrivial(edge))
            {
                continue;
            }

            GraphId edgeid = edge.Id;
            var percentAlong = (float)edge.PercentAlong;

            // Disallow any user-avoided edges if the avoid location is behind the destination along
            // the edge (check has to be done BEFORE we invert the edge if !FORWARD).
            if (_forward
                ? _costing!.AvoidAsOriginEdge(edgeid, percentAlong)
                : _costing!.AvoidAsDestinationEdge(edgeid, percentAlong))
            {
                continue;
            }

            // Get the directed edge.
            GraphTilePtr? tile = graphreader.GetGraphTile(edgeid);
            DirectedEdge directededge = tile!.DirectedEdge(edgeid);

            // Get the tile at the end node. Skip if the tile is not found as we won't be able to
            // expand from this origin edge.
            GraphId oppEdgeId = GraphId.Invalid;
            DirectedEdge oppDirEdge = default;
            PointLL endpoint;
            if (_forward)
            {
                GraphTilePtr? endtile = graphreader.GetGraphTile(directededge.EndNode);
                if (endtile is null)
                {
                    continue;
                }

                endpoint = endtile.GetNodeLl(directededge.EndNode);
            }
            else
            {
                // Get the opposing directed edge; continue if we cannot get it.
                oppEdgeId = graphreader.GetOpposingEdgeId(edgeid);
                if (!oppEdgeId.IsValid())
                {
                    continue;
                }

                DirectedEdge? oppDirEdgeMaybe = graphreader.GetOpposingEdge(edgeid);
                oppDirEdge = oppDirEdgeMaybe!.Value;
                endpoint = tile.GetNodeLl(oppDirEdge.EndNode);
            }

            byte flowSources = 0;

            // add_label closure for the origin. dest_path_edge is null for a plain origin edge, or the
            // destination PathEdge if the origin edge is also a destination edge (trivial route).
            void AddLabel(PathLocation.PathEdge? destPathEdge)
            {
                float start = _forward
                    ? (float)edge.PercentAlong
                    : (destPathEdge is not null ? (float)destPathEdge.PercentAlong : 0.0f);
                float end = _forward
                    ? (destPathEdge is not null ? (float)destPathEdge.PercentAlong : 1.0f)
                    : (float)edge.PercentAlong;

                float percentTraversed = end - start;
                if (percentTraversed < 0)
                {
                    // Not trivial.
                    return;
                }

                Cost cost = _costing!.PartialEdgeCost(directededge, edgeid, tile, timeInfo, ref flowSources, start, end);
                cost.CostValue += (float)edge.Distance + (destPathEdge is not null ? (float)destPathEdge.Distance : 0.0f);

                float dist = 0.0f;
                float sortcost = cost.CostValue +
                                 (destPathEdge is not null ? _astarheuristic.Get(0) : _astarheuristic.Get(endpoint, out dist));

                var pathDistance = (uint)((directededge.Length * percentTraversed) + 0.5f);

                // Add EdgeLabel to the adjacency list.
                uint idx = (uint)_edgelabels.Count;
                byte destonlyRestrictionMask = _costing.GetExemptedAccessRestrictions(directededge, tile, edgeid);

                if (_forward)
                {
                    _edgelabels.Add(new BDEdgeLabel(GraphConstants.InvalidLabel, edgeid, GraphId.Invalid, directededge,
                        cost, sortcost, dist, _mode, new Cost(), false, !_costing.IsClosed(directededge, tile, edgeid.Id()),
                        (flowSources & GraphConstants.DefaultFlowMask) != 0, InternalTurn.NoTurn,
                        GraphConstants.InvalidRestriction, 0,
                        directededge.DestOnly || (_costing.IsHgv() && directededge.DestOnlyHgv),
                        (directededge.ForwardAccess & GraphConstants.TruckAccess) != 0, destonlyRestrictionMask));
                }
                else
                {
                    _edgelabels.Add(new BDEdgeLabel(GraphConstants.InvalidLabel, oppEdgeId, edgeid, oppDirEdge,
                        cost, sortcost, dist, _mode, new Cost(), false, !_costing.IsClosed(directededge, tile, edgeid.Id()),
                        (flowSources & GraphConstants.DefaultFlowMask) != 0, InternalTurn.NoTurn,
                        GraphConstants.InvalidRestriction, 0,
                        directededge.DestOnly || (_costing.IsHgv() && directededge.DestOnlyHgv),
                        (directededge.ForwardAccess & GraphConstants.TruckAccess) != 0, destonlyRestrictionMask));
                }

                BDEdgeLabel edgeLabel = _edgelabels[^1];

                if (!_forward)
                {
                    // Set the initial not_thru flag to false. There is an issue with not_thru flags on
                    // small loops; override this for now.
                    edgeLabel.SetNotThru(false);
                }

                // BDEdgeLabel can't set dist and path_distance at the same time, so update immediately.
                edgeLabel.Update(GraphConstants.InvalidLabel, cost, sortcost, new Cost(), pathDistance,
                    GraphConstants.InvalidRestriction);

                // Set the origin flag.
                edgeLabel.SetOrigin();
                if (destPathEdge is not null)
                {
                    edgeLabel.SetDestination();
                }

                if (ExpansionCallback_ is not null)
                {
                    ExpansionAlgoType expansionType = _forward ? ExpansionAlgoType.Forward : ExpansionAlgoType.Reverse;
                    ExpansionCallback_(graphreader, edgeid, GraphId.Invalid, "unidirectional_astar",
                        ExpansionEdgeStatus.Reached, cost.Secs, (uint)(edge.Distance + 0.5), cost.CostValue,
                        expansionType, flowSources, TravelMode.MaxTravelMode);
                }

                _adjacencylist.Add(idx);
            }

            // Add as a normal edge (fixes engine issue #3585).
            AddLabel(null);

            if (_destinations.TryGetValue(edgeid.Value, out List<PathLocation.PathEdge>? dests))
            {
                foreach (PathLocation.PathEdge destPathEdge in dests)
                {
                    AddLabel(destPathEdge);
                }
            }
        }
    }

    // Add a destination edge. Faithful port of SetDestination. Returns the relative density (0-15).
    private uint SetDestination(GraphReader graphreader, PathLocation dest)
    {
        // Only skip outbound edges if we have other options.
        bool hasOtherEdges = false;
        foreach (PathLocation.PathEdge e in dest.Edges)
        {
            hasOtherEdges = hasOtherEdges || !(_forward ? e.BeginNode() : e.EndNode());
        }

        // For each edge.
        uint density = 0;
        foreach (PathLocation.PathEdge edge in dest.Edges)
        {
            // If the destination is at a node, skip any outbound edges.
            if (hasOtherEdges && (_forward ? edge.BeginNode() : edge.EndNode()))
            {
                continue;
            }

            GraphId edgeid = edge.Id;
            GraphTilePtr? tile = graphreader.GetGraphTile(edgeid);
            if (tile is null)
            {
                continue;
            }

            // Disallow any user-avoided edges if the avoid location is behind the destination along
            // the edge.
            if (_forward
                ? _costing!.AvoidAsDestinationEdge(edgeid, (float)edge.PercentAlong)
                : _costing!.AvoidAsOriginEdge(edgeid, (float)edge.PercentAlong))
            {
                continue;
            }

            // NOTE: we store by edgeid, not opposing edgeid.
            if (!_destinations.TryGetValue(edgeid.Value, out List<PathLocation.PathEdge>? list))
            {
                list = new List<PathLocation.PathEdge>();
                _destinations[edgeid.Value] = list;
            }

            list.Add(edge);

            // Edge score (penalty) is handled within GetPath. Do not add score here.

            // Get the tile relative density.
            density = tile.Header().Density();
        }

        return density;
    }

    // Form the path from the adjacency list. Recovers the path from the destination backwards towards
    // the origin (using predecessor information). Faithful port of the forward/reverse FormPath.
    private List<PathInfo> FormPath(uint dest)
    {
        // path_cost / path_iterations are LOG_DEBUG only.
        if (_forward)
        {
            // Work backwards from the destination.
            var path = new List<PathInfo>();
            for (uint edgelabelIndex = dest; edgelabelIndex != GraphConstants.InvalidLabel;
                 edgelabelIndex = _edgelabels[(int)edgelabelIndex].Predecessor())
            {
                BDEdgeLabel edgelabel = _edgelabels[(int)edgelabelIndex];
                path.Add(new PathInfo(edgelabel.Mode(), edgelabel.Cost(), edgelabel.Edgeid(), 0,
                    edgelabel.PathDistance(), edgelabel.RestrictionIdx(), edgelabel.TransitionCost()));

                // Check if this is a ferry.
                if (edgelabel.Use() == Use.Ferry)
                {
                    HasFerry_ = true;
                }
            }

            // Reverse the list and return.
            path.Reverse();
            return path;
        }
        else
        {
            // Form the reverse path from the destination (true origin) using opposing edges.
            var path = new List<PathInfo>();
            var cost = new Cost();
            var previousTransitionCost = new Cost();
            uint edgelabelIndex = dest;
            while (edgelabelIndex != GraphConstants.InvalidLabel)
            {
                BDEdgeLabel edgelabel = _edgelabels[(int)edgelabelIndex];

                // Get the elapsed time on the edge, then add the transition cost at the prior edge.
                uint predidx = edgelabel.Predecessor();
                if (predidx == GraphConstants.InvalidLabel)
                {
                    cost += edgelabel.Cost();
                }
                else
                {
                    cost += edgelabel.Cost() - _edgelabels[(int)predidx].Cost();
                }

                // PathInfo expects, looking forward along the route, the cost to be the transition at
                // the start of the edge plus the cost of the edge. In the reverse search, EdgeLabels
                // contain the cost of the edge plus the transition at the _end_ of the edge looking
                // forward. Subtract the transition at the end and add the transition at the start
                // (taken from the previous edge, since we walk from the origin to the destination).
                cost -= edgelabel.TransitionCost();
                cost += previousTransitionCost;

                path.Add(new PathInfo(edgelabel.Mode(), cost, edgelabel.OppEdgeid(), 0, edgelabel.PathDistance(),
                    edgelabel.RestrictionIdx(), previousTransitionCost));

                // Check if this is a ferry.
                if (edgelabel.Use() == Use.Ferry)
                {
                    HasFerry_ = true;
                }

                // Update the index and transition cost to apply at the next iteration. The turn cost is
                // applied at the beginning of the edge, as in the forward path; cache it for the next.
                edgelabelIndex = predidx;
                previousTransitionCost = edgelabel.TransitionCost();
            }

            return path;
        }
    }
}
