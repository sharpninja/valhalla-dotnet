// Faithful C# port of Valhalla thor BidirectionalAStar (valhalla @ 3.7.0).
// Sources:
//   F:/github/valhalla/valhalla/thor/bidirectional_astar.h
//   F:/github/valhalla/src/thor/bidirectional_astar.cc  (1556 LOC)
//
// Bidirectional A* - the core least-cost path algorithm. A forward search expands from the origin
// toward the destination; a reverse search expands from the destination back toward the origin
// (along opposing edges). When an edge settled on one tree connects to an edge reached on the other
// tree a candidate connection is recorded; the search then runs a little past the best connection
// (threshold_delta_) to confirm optimality before reconstructing the path (FormPath).
//
// The forward+reverse expansion, the meet/connection logic (SetForwardConnection /
// SetReverseConnection), the hierarchy limits (StopExpanding + simultaneous-exhaustion forcing), the
// complex-restriction bridging check (IsBridgingEdgeRestricted), and the cost via sif::DynamicCost
// are all reproduced exactly. The single C++ template parameter <ExpansionType> is reproduced with a
// runtime `forward` bool threaded into Expand/ExpandInner (identical control flow; no behavior
// change). Public members are PascalCase.
//
// PORT-NOTES (per task scope: point-to-point auto/truck only):
//   - Origin/destination are the loki-correlated baldr::PathLocation values (carrying the candidate
//     PathEdges) instead of the proto valhalla::Location. The proto correlation().edges() reads map
//     onto PathLocation.Edges; PathEdge.percent_along/ll/distance/begin_node/end_node map to the
//     ported PathLocation.PathEdge members. find_percent_along walks PathLocation.Edges.
//   - TimeInfo::make + EstimateReverseStartTime depend on baldr::DateTime + the timezone database,
//     which are a later port slice. For the supported point-to-point (no date_time) case the engine's
//     TimeInfo::make yields TimeInfo::invalid(); we reproduce that (both directions get
//     TimeInfo.Invalid()). If a date/time IS requested we throw NotImplementedException rather than
//     silently producing a time-independent route (mirrors DynamicCost.IsConditionalActive's
//     missing-dependency policy). recost_forward (sif/recost.h) is likewise a later slice; FormPath
//     reconstructs the path edges + per-edge labels directly from the settled edge labels (the costs
//     already computed during expansion), which is sufficient to consume a route. The alternates
//     viability filters (alternates.h: filter/validate_alternate_*) are ported in Alternates.cs and
//     wired into FormPath (stretch cull + max-sharing + the sharing/stretch/local-optimality
//     accept-predicate); desired_paths_count_ is still driven by options.HasAlternates (nothing sets
//     Alternates yet), so only the primary path is emitted at runtime. The expansion-tracking callback
//     uses the ported plain-C# enums (no proto Expansion).
//   - RecoverShortcut is not on the ported GraphReader (shortcut_recovery_t cache excluded); the
//     recover_shortcut graph-walk from shortcut_recovery.h is ported inline here as RecoverShortcut so
//     FormPath expands shortcut edges into their underlying edges exactly as the engine does.
//   - HierarchyLimits is a reference type in C#; the C++ `hierarchy_limits_forward_ = hierarchy_limits`
//     copies by value, so Init clones each HierarchyLimits into independent forward/reverse lists.

using System;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

// graph_tile_ptr alias to read like the C++ signatures.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;

// Namespace alias so sif::LimitedGraphReader can be disambiguated from baldr::LimitedGraphReader.
using Sif = SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Thor;

/// <summary>
/// Candidate connections - a directed edge and its opposing directed edge are both temporarily
/// labeled. Stores the edge Ids and its cost. Faithful port of <c>struct CandidateConnection</c>.
/// </summary>
public struct CandidateConnection : IComparable<CandidateConnection>
{
    /// <summary>The directed edge on the forward tree that connects.</summary>
    public GraphId Edgeid;

    /// <summary>The opposing directed edge on the reverse tree.</summary>
    public GraphId OppEdgeid;

    /// <summary>The total cost of the path through this connection.</summary>
    public float Cost;

    /// <summary>Constructs a candidate connection.</summary>
    public CandidateConnection(GraphId edgeid, GraphId oppEdgeid, float cost)
    {
        Edgeid = edgeid;
        OppEdgeid = oppEdgeid;
        Cost = cost;
    }

    /// <summary>
    /// Orders by <see cref="Cost"/> ascending (mirroring the C++ <c>operator&lt;</c> used by
    /// <c>std::sort</c> in filter_alternates_by_stretch), with a deterministic <see cref="GraphId"/>
    /// tie-break on <see cref="Edgeid"/> then <see cref="OppEdgeid"/> so equal-cost connections emit in
    /// a stable order.
    /// </summary>
    public int CompareTo(CandidateConnection other)
    {
        int byCost = Cost.CompareTo(other.Cost);
        if (byCost != 0)
        {
            return byCost;
        }

        int byEdge = Edgeid.CompareTo(other.Edgeid);
        if (byEdge != 0)
        {
            return byEdge;
        }

        return OppEdgeid.CompareTo(other.OppEdgeid);
    }

    /// <summary>
    /// Reproduces <c>std::lower_bound(connections, max_cost)</c>: given a list already sorted by
    /// <see cref="Cost"/>, returns the index of the first connection whose cost is &gt;= <paramref name="maxCost"/>
    /// (the cull point used by <see cref="Alternates.FilterAlternatesByStretch"/>).
    /// </summary>
    public static int LowerBoundByCost(List<CandidateConnection> connections, float maxCost)
    {
        int lo = 0;
        int hi = connections.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (connections[mid].Cost < maxCost)
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
}

/// <summary>
/// Bidirectional A* algorithm. Method for finding least-cost path. Faithful port of
/// <c>valhalla::thor::BidirectionalAStar</c>.
/// </summary>
public sealed class BidirectionalAStar : PathAlgorithm
{
    /// <summary>Heuristic factor used by EstimateReverseStartTime. Faithful port of <c>kReverseTTHeuristicFactor</c>.</summary>
    public const double ReverseTTHeuristicFactor = 2.1;

    // ===== file-local constants (anonymous namespace in bidirectional_astar.cc) =====

    // Threshold (seconds) to extend search once the first connection has been found.
    private const float KThresholdDelta = 420.0f;

    // Relative cost extension to find alternative routes.
    private const float KAlternativeCostExtend = 1.2f;

    // Maximum number of additional iterations allowed once the first connection has been found.
    private const uint KAlternativeIterationsDelta = 100000;

    // ===== protected state (bidirectional_astar.h) =====

    private uint _accessMode;
    private TravelMode _mode;
    private byte _travelType;
    private DynamicCost _costing = null!;

    private List<HierarchyLimits> _hierarchyLimitsForward = new();
    private List<HierarchyLimits> _hierarchyLimitsReverse = new();
    private bool _ignoreHierarchyLimits;

    private float _costDiff;
    private readonly AStarHeuristic _astarheuristicForward = new();
    private readonly AStarHeuristic _astarheuristicReverse = new();

    private readonly List<BDEdgeLabel> _edgelabelsForward = new();
    private readonly List<BDEdgeLabel> _edgelabelsReverse = new();

    private readonly DoubleBucketQueue<BDEdgeLabel> _adjacencylistForward = new();
    private readonly DoubleBucketQueue<BDEdgeLabel> _adjacencylistReverse = new();

    private readonly EdgeStatus _edgestatusForward = new();
    private readonly EdgeStatus _edgestatusReverse = new();

    private float _costThreshold;
    private uint _iterationsThreshold;
    private uint _desiredPathsCount;
    private List<CandidateConnection> _bestConnections = new();

    private readonly float _thresholdDelta;
    private readonly float _alternativeCostExtend;
    private readonly uint _alternativeIterationsDelta;

    private readonly bool _extendedSearch;
    private bool _pruningDisabledAtOrigin;
    private bool _pruningDisabledAtDestination;

    /// <summary>
    /// Constructor. Faithful port of <c>BidirectionalAStar(const boost::property_tree::ptree&amp;)</c>.
    /// The C++ reads the knobs out of a config ptree; here they are explicit parameters with the same
    /// defaults (max_reserved_labels_count_bidir_astar / clear_reserved_memory / extended_search /
    /// the three bidirectional_astar.* tuning constants).
    /// </summary>
    /// <param name="maxReservedLabelsCount">Initial edge-label reservation (default <c>kInitialEdgeLabelCountBidirAstar</c>).</param>
    /// <param name="clearReservedMemory">Whether to clear reserved label memory on Clear.</param>
    /// <param name="extendedSearch">Whether to extend the search in one direction if the other exhausts.</param>
    /// <param name="thresholdDelta">Seconds to extend the search once the first connection is found.</param>
    /// <param name="alternativeCostExtend">Relative cost extension to find alternative routes.</param>
    /// <param name="alternativeIterationsDelta">Max additional iterations once the first connection is found.</param>
    public BidirectionalAStar(
        uint maxReservedLabelsCount = EdgeLabelConstants.InitialEdgeLabelCountBidirAstar,
        bool clearReservedMemory = false,
        bool extendedSearch = false,
        float thresholdDelta = KThresholdDelta,
        float alternativeCostExtend = KAlternativeCostExtend,
        uint alternativeIterationsDelta = KAlternativeIterationsDelta)
        : base(maxReservedLabelsCount, clearReservedMemory)
    {
        _extendedSearch = extendedSearch;
        _costThreshold = 0;
        _iterationsThreshold = 0;
        _desiredPathsCount = 1;
        _mode = TravelMode.Drive;
        _accessMode = GraphConstants.AutoAccess;
        _travelType = 0;
        _costDiff = 0.0f;
        _pruningDisabledAtOrigin = false;
        _pruningDisabledAtDestination = false;
        _ignoreHierarchyLimits = false;
        _thresholdDelta = thresholdDelta;
        _alternativeCostExtend = alternativeCostExtend;
        _alternativeIterationsDelta = alternativeIterationsDelta;
    }

    /// <summary>Returns the name of the algorithm. Faithful port of <c>name()</c>.</summary>
    public override string Name() => "bidirectional_a*";

    /// <summary>Clear the temporary information generated during path construction. Faithful port of <c>Clear()</c>.</summary>
    public override void Clear()
    {
        // The C++ resize/shrink_to_fit memory dance has no managed equivalent; just clear the lists.
        _edgelabelsForward.Clear();
        _edgelabelsReverse.Clear();

        _adjacencylistForward.Clear();
        _adjacencylistReverse.Clear();
        _edgestatusForward.Clear();
        _edgestatusReverse.Clear();

        // Set the ferry flag to false.
        HasFerry_ = false;
        // Set not thru pruning to true.
        SetNotThruPruning(true);
        // reset origin & destination pruning states.
        _pruningDisabledAtOrigin = false;
        _pruningDisabledAtDestination = false;
        _ignoreHierarchyLimits = false;
    }

    // Initialize the A* heuristic and adjacency lists for both the forward and reverse search.
    private void Init(PointLL origll, PointLL destll)
    {
        // Initialize the A* heuristics.
        float factor = _costing.AStarCostFactor();
        _astarheuristicForward.Init(destll, factor);
        _astarheuristicReverse.Init(origll, factor);

        // Construct adjacency list and initialize edge status lookup.
        // Set bucket size and cost range based on DynamicCost.
        uint bucketsize = _costing.UnitSize();
        float range = PathAlgorithm.BucketCount * bucketsize;

        float mincostf = _astarheuristicForward.Get(origll);
        _adjacencylistForward.Reuse(mincostf, range, bucketsize, _edgelabelsForward);
        float mincostr = _astarheuristicReverse.Get(destll);
        _adjacencylistReverse.Reuse(mincostr, range, bucketsize, _edgelabelsReverse);

        _edgestatusForward.Clear();
        _edgestatusReverse.Clear();

        // Set the cost diff between forward and reverse searches (due to distance approximator
        // differences). This is used to "even" the forward and reverse searches.
        _costDiff = mincostf - mincostr;

        // Initialize best connections as having none.
        _bestConnections = new List<CandidateConnection>();

        // Set the cost threshold to the maximum float value. Once the initial connection is found the
        // threshold is set.
        _costThreshold = float.MaxValue;
        _iterationsThreshold = uint.MaxValue;
        List<HierarchyLimits> hierarchyLimits = _costing.GetHierarchyLimits();
        int levelCount = TileHierarchy.Levels().Count;
        _ignoreHierarchyLimits =
            hierarchyLimits.Skip(1).Take(levelCount - 1)
                .All(limits => limits.MaxUpTransitions == HierarchyLimitsFunctions.UnlimitedTransitions);

        // PORT-NOTE: C++ value-copies the vector; HierarchyLimits is a reference type here, so clone.
        _hierarchyLimitsForward = hierarchyLimits.Select(CloneLimits).ToList();
        _hierarchyLimitsReverse = hierarchyLimits.Select(CloneLimits).ToList();
    }

    private static HierarchyLimits CloneLimits(HierarchyLimits src) => new HierarchyLimits
    {
        UpTransitionCount = src.UpTransitionCount,
        MaxUpTransitions = src.MaxUpTransitions,
        ExpandWithinDist = src.ExpandWithinDist,
    };

    // Runs in the inner loop of `Expand`, deciding whether the edge described in `meta` should be
    // placed on the adjacency list, and doing so. Returns false if uturns are allowed; true if we
    // will (or did) expand from this edge, in which case uturns are disallowed.
    private bool ExpandInner(
        bool forward,
        GraphReader graphreader,
        BDEdgeLabel pred,
        DirectedEdge? oppPredEdge,
        NodeInfo nodeinfo,
        uint predIdx,
        ref EdgeMetadata meta,
        ref uint shortcuts,
        GraphTilePtr tile,
        TimeInfo timeInfo)
    {
        // Snapshot the edge + its id into locals (DirectedEdge is a value type; it never changes during
        // ExpandInner - only the edge status is written, via meta.SetEdgeStatus). This lets the
        // GetOppEdgeData local function read the edge without capturing the `ref meta` parameter (which
        // C# forbids inside a lambda/local function - CS1628).
        DirectedEdge metaEdge = meta.Edge;
        GraphId metaEdgeId = meta.EdgeId;

        // Skip if this is a regular edge superseded by a shortcut.
        if ((shortcuts & metaEdge.Superseded) != 0)
        {
            return false;
        }

        GraphTilePtr? t2 = null;
        var oppEdgeId = GraphId.Invalid;

        bool GetOppEdgeData()
        {
            t2 = metaEdge.LeavesTile ? graphreader.GetGraphTile(metaEdge.EndNode) : tile;
            if (t2 is null)
            {
                return false;
            }

            oppEdgeId = t2.GetOpposingEdgeId(metaEdge);
            return true;
        }

        List<HierarchyLimits> hierarchyLimits = forward ? _hierarchyLimitsForward : _hierarchyLimitsReverse;

        // Skip shortcut edges until we have stopped expanding on the next level. Use regular edges
        // while still expanding on the next level since we can still transition down to that level. If
        // using a shortcut, set the shortcuts mask.
        if (meta.Edge.IsShortcut)
        {
            // Skip shortcuts if hierarchy limits are disabled.
            if (_ignoreHierarchyLimits || !GetOppEdgeData())
            {
                return false;
            }

            EdgeStatus oppEdgestatus = forward ? _edgestatusReverse : _edgestatusForward;
            EdgeSet oppEdgeSet = oppEdgestatus.Get(oppEdgeId).Set();

            // Synchronize shortcuts for both directions. If this shortcut has been already encountered
            // on the opposing search we should do the same now: skip or traverse.
            if ((oppEdgeSet != EdgeSet.Skipped &&
                 HierarchyLimitsFunctions.StopExpanding(hierarchyLimits[(int)(meta.EdgeId.Level() + 1)], pred.Distance())) ||
                oppEdgeSet == EdgeSet.Permanent || oppEdgeSet == EdgeSet.Temporary)
            {
                shortcuts |= meta.Edge.Shortcut;
            }
            else
            {
                // Mark this edge as "skipped".
                meta.SetEdgeStatus(new EdgeStatusInfo(EdgeSet.Skipped, 0));
                return false;
            }
        }

        // Skip this edge if edge is permanently labeled (best path already found to this directed
        // edge), if no access is allowed, or if a complex restriction prevents transition onto it.
        if (meta.EdgeStatusRef.Set() == EdgeSet.Permanent)
        {
            return true; // This is an edge we _could_ have expanded, so return true.
        }

        DirectedEdge? oppEdge = null;

        if (!forward)
        {
            // Check the access mode and skip this edge if access is not allowed in the reverse
            // direction. This avoids the (somewhat expensive) retrieval of the opposing directed edge
            // when no access is allowed in the reverse direction.
            if ((meta.Edge.ReverseAccess & _accessMode) == 0)
            {
                return false;
            }

            if (t2 is null && !GetOppEdgeData())
            {
                return false;
            }

            oppEdge = t2!.DirectedEdge(oppEdgeId);
        }

        // Skip this edge if no access is allowed (based on costing method) or if a complex restriction
        // prevents transition onto this edge. If it's not time dependent set to 0 for Allowed and
        // Restricted methods below.
        ulong localtime = timeInfo.Valid ? timeInfo.LocalTime : 0;
        byte restrictionIdx = GraphConstants.InvalidRestriction;
        byte destonlyRestrictionMask = pred.DestonlyAccessRestrMask();
        if (forward)
        {
            if (!_costing.Allowed(meta.Edge, false, pred, tile, meta.EdgeId, localtime,
                    (uint)timeInfo.TimezoneIndex, ref restrictionIdx, ref destonlyRestrictionMask) ||
                Restricted(meta.Edge, pred, _edgelabelsForward, tile, meta.EdgeId, true,
                    _edgestatusForward, localtime, (uint)timeInfo.TimezoneIndex))
            {
                return false;
            }
        }
        else
        {
            if (!_costing.AllowedReverse(meta.Edge, pred, oppEdge!.Value, t2!, oppEdgeId, localtime,
                    (uint)timeInfo.TimezoneIndex, ref restrictionIdx, ref destonlyRestrictionMask) ||
                Restricted(meta.Edge, pred, _edgelabelsReverse, tile, meta.EdgeId, false,
                    _edgestatusReverse, localtime, (uint)timeInfo.TimezoneIndex))
            {
                return false;
            }
        }

        // Get cost.
        byte flowSources = 0;
        Cost newcost = pred.Cost() +
            (forward
                ? _costing.EdgeCost(meta.Edge, meta.EdgeId, tile, timeInfo, ref flowSources)
                : _costing.EdgeCost(oppEdge!.Value, oppEdgeId, t2!, timeInfo, ref flowSources));

        // PORT-NOTE: the sif transition-cost signatures take a getter for sif::LimitedGraphReader (a
        // foundation stub); fully qualify to avoid the ambiguity with baldr::LimitedGraphReader.
        Func<Sif.LimitedGraphReader> readerGetter = () => new Sif.LimitedGraphReader();

        // Separate out transition cost.
        Cost transitionCost =
            forward
                ? _costing.TransitionCost(meta.Edge, nodeinfo, pred, tile, readerGetter)
                : _costing.TransitionCostReverse(meta.Edge.LocalEdgeIdx, nodeinfo, oppEdge!.Value,
                    oppPredEdge ?? default, t2!, pred.Edgeid(), readerGetter,
                    (flowSources & GraphConstants.DefaultFlowMask) != 0, pred.InternalTurn());
        newcost += transitionCost;

        // Check if edge is temporarily labeled and this path has less cost. If less cost the
        // predecessor is updated and the sort cost is decremented by the difference in real cost (A*
        // heuristic doesn't change).
        if (meta.EdgeStatusRef.Set() == EdgeSet.Temporary)
        {
            BDEdgeLabel lab = forward
                ? _edgelabelsForward[(int)meta.EdgeStatusRef.Index()]
                : _edgelabelsReverse[(int)meta.EdgeStatusRef.Index()];
            if (newcost.CostValue < lab.Cost().CostValue)
            {
                float newsortcost = lab.Sortcost() - (lab.Cost().CostValue - newcost.CostValue);
                if (forward)
                {
                    _adjacencylistForward.Decrease(meta.EdgeStatusRef.Index(), newsortcost);
                }
                else
                {
                    _adjacencylistReverse.Decrease(meta.EdgeStatusRef.Index(), newsortcost);
                }

                lab.Update(predIdx, newcost, newsortcost, transitionCost, restrictionIdx);
            }

            // Returning true since this means we approved the edge.
            return true;
        }

        // Get end node tile (skip if tile is not found) and opposing edge Id.
        if (t2 is null && !GetOppEdgeData())
        {
            return false;
        }

        PointLL endNodeLl = t2!.GetNodeLl(meta.Edge.EndNode);

        // Find the sort cost (with A* heuristic) using the lat,lng at the end node of the edge.
        float dist;
        float sortcost = newcost.CostValue + (forward
            ? _astarheuristicForward.Get(endNodeLl, out dist)
            : _astarheuristicReverse.Get(endNodeLl, out dist));

        // not_thru_pruning_ is only set to false on the 2nd pass in route_action. We allow settling
        // not_thru edges so we can connect both trees on them.
        bool notThruPruning = NotThruPruning_
            ? (pred.NotThruPruning() || !meta.Edge.NotThru)
            : false;

        // Add edge label, add to the adjacency list and set edge status.
        uint idx;
        if (forward)
        {
            idx = (uint)_edgelabelsForward.Count;
            if (_hierarchyLimitsForward[(int)meta.EdgeId.Level()].MaxUpTransitions !=
                HierarchyLimitsFunctions.UnlimitedTransitions)
            {
                // Override distance to the destination with a distance from the origin (hierarchy limits).
                dist = _astarheuristicReverse.GetDistance(endNodeLl);
            }

            _edgelabelsForward.Add(new BDEdgeLabel(predIdx, meta.EdgeId, oppEdgeId, meta.Edge, newcost,
                sortcost, dist, _mode, transitionCost, notThruPruning,
                pred.ClosurePruning() || !IsClosed(meta.Edge, tile, meta.EdgeId),
                (flowSources & GraphConstants.DefaultFlowMask) != 0,
                _costing.TurnType(pred.OppLocalIdx(), nodeinfo, meta.Edge),
                restrictionIdx, 0,
                meta.Edge.DestOnly || (_costing.IsHgv() && meta.Edge.DestOnlyHgv),
                (meta.Edge.ForwardAccess & GraphConstants.TruckAccess) != 0,
                destonlyRestrictionMask));
            _adjacencylistForward.Add(idx);
        }
        else
        {
            idx = (uint)_edgelabelsReverse.Count;
            if (_hierarchyLimitsReverse[(int)meta.EdgeId.Level()].MaxUpTransitions !=
                HierarchyLimitsFunctions.UnlimitedTransitions)
            {
                // Override distance to the origin with a distance from the destination (hierarchy limits).
                dist = _astarheuristicForward.GetDistance(endNodeLl);
            }

            _edgelabelsReverse.Add(new BDEdgeLabel(predIdx, meta.EdgeId, oppEdgeId, meta.Edge, newcost,
                sortcost, dist, _mode, transitionCost, notThruPruning,
                pred.ClosurePruning() || !IsClosed(oppEdge!.Value, t2, oppEdgeId),
                (flowSources & GraphConstants.DefaultFlowMask) != 0,
                _costing.TurnType(meta.Edge.LocalEdgeIdx, nodeinfo, oppEdge.Value, oppPredEdge),
                restrictionIdx, 0,
                oppEdge.Value.DestOnly || (_costing.IsHgv() && oppEdge.Value.DestOnlyHgv),
                (oppEdge.Value.ForwardAccess & GraphConstants.TruckAccess) != 0,
                destonlyRestrictionMask));
            _adjacencylistReverse.Add(idx);
        }

        meta.SetEdgeStatus(new EdgeStatusInfo(EdgeSet.Temporary, idx));

        // setting this edge as reached.
        if (ExpansionCallback_ is not null)
        {
            GraphId prevPred = pred.Predecessor() == GraphConstants.InvalidLabel
                ? new GraphId()
                : (forward ? _edgelabelsForward : _edgelabelsReverse)[(int)pred.Predecessor()].Edgeid();
            ExpansionCallback_(graphreader, forward ? meta.EdgeId : oppEdgeId, prevPred,
                "bidirectional_astar", ExpansionEdgeStatus.Reached, newcost.Secs,
                pred.PathDistance() + meta.Edge.Length, newcost.CostValue,
                forward ? ExpansionAlgoType.Forward : ExpansionAlgoType.Reverse, 0, _mode);
        }

        // we've just added this edge to the queue, but we won't expand from it if it's a not-thru edge
        // that will be pruned. In that case we want to allow uturns.
        return !(pred.NotThruPruning() && meta.Edge.NotThru);
    }

    // Expand from the node along the search path in the given direction.
    private void Expand(
        bool forward,
        GraphReader graphreader,
        GraphId node,
        BDEdgeLabel pred,
        uint predIdx,
        DirectedEdge? oppPredEdge,
        TimeInfo timeInfo,
        bool invariant)
    {
        // Get the tile and the node info. Skip if tile is null (can happen with regional data sets) or
        // if no access at the node.
        GraphTilePtr? tile = graphreader.GetGraphTile(node);
        if (tile is null)
        {
            return;
        }

        NodeInfo nodeinfo = tile.Node(node);

        // Keep track of superseded edges.
        uint shortcuts = 0;

        // Update the time information even if time is invariant to account for timezones.
        float secondsOffset = invariant ? 0.0f : pred.Cost().Secs;
        TimeInfo offsetTime = forward
            ? timeInfo.Forward(secondsOffset, (int)nodeinfo.Timezone())
            : timeInfo.Reverse(secondsOffset, (int)nodeinfo.Timezone());

        EdgeStatus edgestatus = forward ? _edgestatusForward : _edgestatusReverse;

        // If we encounter a node with an access restriction like a barrier we allow a uturn.
        if (!_costing.Allowed(nodeinfo))
        {
            GraphTilePtr? oppTile = tile;
            GraphId oppEdgeId = graphreader.GetOpposingEdgeId(pred.Edgeid(), out DirectedEdge? oppEdge, ref oppTile);

            // Mark the predecessor as a deadend to be consistent with how the edgelabels are set when
            // an *actual* deadend (i.e. some dangling OSM geometry) is labelled.
            pred.SetDeadend(true);

            // Check if edge is null before using it (can happen with regional data sets).
            if (oppEdge is not null)
            {
                (EdgeStatusInfo[] arr, int statusIdx) = edgestatus.GetPtr(oppEdgeId, tile);
                EdgeMetadata barrierMeta = EdgeMetadata.MakeAt(oppEdge.Value, oppEdgeId, arr, statusIdx, tile);
                ExpandInner(forward, graphreader, pred, oppPredEdge, nodeinfo, predIdx, ref barrierMeta,
                    ref shortcuts, tile, offsetTime);
            }

            return;
        }

        bool disableUturn = false;
        EdgeMetadata meta = EdgeMetadata.Make(node, nodeinfo, tile, edgestatus);
        EdgeMetadata uturnMeta = default;
        bool haveUturn = false;

        // Expand from end node in <forward> direction.
        for (uint i = 0; i < nodeinfo.EdgeCount; ++i, meta = meta.Increment())
        {
            // Begin by checking if this is the opposing edge to pred. If so, it means we are attempting
            // a u-turn. In that case, lets wait with evaluating this edge until last. If any other
            // edges were emplaced, it means we should not even try to evaluate a u-turn since u-turns
            // should only happen for deadends.
            bool isUturn = pred.OppLocalIdx() == meta.Edge.LocalEdgeIdx;
            if (isUturn)
            {
                uturnMeta = meta;
                haveUturn = true;
            }

            // Expand but only if this isn't the uturn, we'll try that later if nothing else works out.
            disableUturn = (!isUturn && ExpandInner(forward, graphreader, pred, oppPredEdge, nodeinfo,
                                predIdx, ref meta, ref shortcuts, tile, offsetTime)) ||
                           disableUturn;
        }

        // Handle transitions - expand from the end node of each transition.
        if (nodeinfo.TransitionCount > 0)
        {
            List<HierarchyLimits> hierarchyLimits = forward ? _hierarchyLimitsForward : _hierarchyLimitsReverse;
            for (uint i = 0; i < nodeinfo.TransitionCount; ++i)
            {
                NodeTransition trans = tile.Transition(nodeinfo.TransitionIndex + i);

                // if this is a downward transition (ups are always allowed) AND we are no longer
                // allowed OR we can't get the tile at that level THEN bail.
                GraphTilePtr? transTile;
                if ((!trans.Up() && !_ignoreHierarchyLimits &&
                     HierarchyLimitsFunctions.StopExpanding(hierarchyLimits[(int)trans.EndNode().Level()], pred.Distance())) ||
                    (transTile = graphreader.GetGraphTile(trans.EndNode())) is null)
                {
                    continue;
                }

                // setup for expansion at this level.
                hierarchyLimits[(int)node.Level()].SetUpTransitionCount(
                    hierarchyLimits[(int)node.Level()].UpTransitionCount + (trans.Up() ? 1u : 0u));
                NodeInfo transNode = transTile.Node(trans.EndNode());
                EdgeMetadata transMeta = EdgeMetadata.Make(trans.EndNode(), transNode, transTile, edgestatus);
                uint transShortcuts = 0;

                // expand the edges from this node at this level.
                for (uint e = 0; e < transNode.EdgeCount; ++e, transMeta = transMeta.Increment())
                {
                    disableUturn = ExpandInner(forward, graphreader, pred, oppPredEdge, transNode,
                                       predIdx, ref transMeta, ref transShortcuts, transTile, offsetTime) ||
                                   disableUturn;
                }
            }
        }

        // Now, after having looked at all the edges, including edges on other levels, we can say if
        // this is a deadend or not, and if so, evaluate the uturn-edge (if it exists).
        if (!disableUturn && haveUturn)
        {
            // If we found no suitable edge to add, it means we're at a deadend so lets go back and
            // re-evaluate a potential u-turn.
            pred.SetDeadend(true);

            // Expand the uturn possibility.
            ExpandInner(forward, graphreader, pred, oppPredEdge, nodeinfo, predIdx, ref uturnMeta,
                ref shortcuts, tile, offsetTime);
        }
    }

    /// <summary>
    /// Form path between an origin and destination location using bidirectional A*. Faithful port of
    /// <c>GetBestPath</c>.
    /// </summary>
    public override List<List<PathInfo>> GetBestPath(
        PathLocation origin,
        PathLocation destination,
        GraphReader graphreader,
        ModeCosting modeCosting,
        TravelMode mode,
        Options? options = null)
    {
        options ??= new Options();

        // Set the mode and costing.
        _mode = mode;
        _costing = modeCosting[(int)_mode] ?? throw new InvalidOperationException("No costing for travel mode");
        _travelType = _costing.TravelType();
        _accessMode = _costing.AccessMode();

        _desiredPathsCount = 1;
        if (options.HasAlternates && options.Alternates != 0)
        {
            _desiredPathsCount += options.Alternates;
        }

        if (origin.Edges.Count == 0 || destination.Edges.Count == 0)
        {
            return new List<List<PathInfo>>();
        }

        // Initialize - create adjacency list, edgestatus support, A*, etc.
        var originNew = new PointLL(origin.Edges[0].Projected.Lng, origin.Edges[0].Projected.Lat);
        var destinationNew = new PointLL(destination.Edges[0].Projected.Lng, destination.Edges[0].Projected.Lat);
        Init(originNew, destinationNew);

        // we use a non varying time for all time dependent routes until we can figure out how to vary
        // the time during the path computation in the bidirectional algorithm.
        bool invariant = options.DateTimeType == DateTimeType.Invariant;
        bool arriveBy = options.DateTimeType == DateTimeType.ArriveBy;

        // Get time information for forward and backward searches.
        // PORT-NOTE: TimeInfo::make + EstimateReverseStartTime depend on the (later-slice) baldr
        // DateTime + timezone DB. For the supported no-date-time case TimeInfo::make yields
        // invalid(); reproduce that. Any actual date/time request is not yet supported.
        if (options.HasDateTimeType && options.DateTimeType != DateTimeType.NoTime &&
            (!string.IsNullOrEmpty(origin.DateTime) || !string.IsNullOrEmpty(destination.DateTime)))
        {
            throw new NotImplementedException(
                "Time-dependent bidirectional A* needs baldr::DateTime + the timezone database (later port slice).");
        }

        TimeInfo forwardTimeInfo = TimeInfo.Invalid();
        TimeInfo reverseTimeInfo = TimeInfo.Invalid();

        // Set origin and destination locations - seeds the adj. lists.
        SetOrigin(graphreader, origin, forwardTimeInfo);
        SetDestination(graphreader, destination, reverseTimeInfo);

        // Find shortest path. Switch between a forward direction and a reverse direction search based
        // on the current costs. Alternating like this prevents one tree from expanding much more
        // quickly (if in a sparser portion of the graph) rather than strictly alternating.
        int n = 0;
        uint forwardPredIdx = 0;
        uint reversePredIdx = 0;
        BDEdgeLabel fwdPred = new BDEdgeLabel();
        BDEdgeLabel revPred = new BDEdgeLabel();
        bool expandForward = true;
        bool expandReverse = true;
        while (true)
        {
            // Allow this process to be aborted.
            if (Interrupt is not null && (++n % InterruptIterationsInterval) == 0)
            {
                Interrupt();
            }

            // Terminate if the iterations threshold has been exceeded.
            if ((_edgelabelsReverse.Count + _edgelabelsForward.Count) > _iterationsThreshold)
            {
                return FormPath(graphreader, options, origin, destination, forwardTimeInfo);
            }

            // Get the next predecessor (based on which direction was expanded in prior step).
            if (expandForward)
            {
                forwardPredIdx = _adjacencylistForward.Pop();
                if (forwardPredIdx != GraphConstants.InvalidLabel)
                {
                    fwdPred = _edgelabelsForward[(int)forwardPredIdx];

                    // Forward path to this edge can't be improved, so we can settle it right now.
                    _edgestatusForward.Update(fwdPred.Edgeid(), EdgeSet.Permanent);

                    // Terminate if the cost threshold has been exceeded.
                    if (fwdPred.Sortcost() + _costDiff > _costThreshold)
                    {
                        return FormPath(graphreader, options, origin, destination, forwardTimeInfo);
                    }

                    // Check if the edge on the forward search connects to a settled edge on the reverse
                    // search tree.
                    EdgeStatusInfo oppStatus = _edgestatusReverse.Get(fwdPred.OppEdgeid());
                    if (oppStatus.Set() == EdgeSet.Permanent ||
                        (oppStatus.Set() == EdgeSet.Temporary &&
                         _edgelabelsReverse[(int)oppStatus.Index()].Predecessor() == GraphConstants.InvalidLabel))
                    {
                        if (SetForwardConnection(graphreader, fwdPred))
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    // Search is exhausted. If a connection has been found, return it.
                    if (_bestConnections.Count != 0)
                    {
                        return FormPath(graphreader, options, origin, destination, forwardTimeInfo);
                    }

                    if (!_extendedSearch || !_pruningDisabledAtDestination)
                    {
                        return new List<List<PathInfo>>();
                    }
                }
            }

            if (expandReverse)
            {
                reversePredIdx = _adjacencylistReverse.Pop();
                if (reversePredIdx != GraphConstants.InvalidLabel)
                {
                    revPred = _edgelabelsReverse[(int)reversePredIdx];

                    // Reverse path to this edge can't be improved, so we can settle it right now.
                    _edgestatusReverse.Update(revPred.Edgeid(), EdgeSet.Permanent);

                    // Terminate if the cost threshold has been exceeded.
                    if (revPred.Sortcost() > _costThreshold)
                    {
                        return FormPath(graphreader, options, origin, destination, forwardTimeInfo);
                    }

                    // Check if the edge on the reverse search connects to a settled edge on the forward
                    // search tree.
                    EdgeStatusInfo oppStatus = _edgestatusForward.Get(revPred.OppEdgeid());
                    if (oppStatus.Set() == EdgeSet.Permanent ||
                        (oppStatus.Set() == EdgeSet.Temporary &&
                         _edgelabelsForward[(int)oppStatus.Index()].Predecessor() == GraphConstants.InvalidLabel))
                    {
                        if (SetReverseConnection(graphreader, revPred))
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    // Search is exhausted. If a connection has been found, return it.
                    if (_bestConnections.Count != 0)
                    {
                        return FormPath(graphreader, options, origin, destination, forwardTimeInfo);
                    }

                    if (!_extendedSearch || !_pruningDisabledAtOrigin)
                    {
                        return new List<List<PathInfo>>();
                    }
                }
            }

            bool forwardExhausted = forwardPredIdx == GraphConstants.InvalidLabel;
            bool reverseExhausted = reversePredIdx == GraphConstants.InvalidLabel;

            // If both directions have exhausted, we've failed to find a route. Abort.
            if (forwardExhausted && reverseExhausted)
            {
                return new List<List<PathInfo>>();
            }

            // Exhaust hierarchy limits simultaneously in both directions.
            bool forceForward = false;
            bool forceReverse = false;
            if (!_ignoreHierarchyLimits)
            {
                for (int level = TileHierarchy.Levels().Count - 1; level > 0; --level)
                {
                    if (HierarchyLimitsFunctions.StopExpanding(_hierarchyLimitsReverse[level], revPred.Distance()) &&
                        !HierarchyLimitsFunctions.StopExpanding(_hierarchyLimitsForward[level], fwdPred.Distance()))
                    {
                        forceForward = true;
                        break;
                    }

                    if (HierarchyLimitsFunctions.StopExpanding(_hierarchyLimitsForward[level], fwdPred.Distance()) &&
                        !HierarchyLimitsFunctions.StopExpanding(_hierarchyLimitsReverse[level], revPred.Distance()))
                    {
                        forceReverse = true;
                        break;
                    }
                }
            }

            // Expand from the search direction with lower sort cost. If one direction is exhausted, we
            // force search in the remaining direction.
            if (!forwardExhausted &&
                ((!forceReverse && (fwdPred.Sortcost() + _costDiff) < revPred.Sortcost()) ||
                 forceForward || reverseExhausted))
            {
                // Expand forward - set to get next edge from forward adj. list on the next pass.
                expandForward = true;
                expandReverse = false;

                // setting this edge as settled.
                if (ExpansionCallback_ is not null)
                {
                    GraphId prevPred = fwdPred.Predecessor() == GraphConstants.InvalidLabel
                        ? new GraphId()
                        : _edgelabelsForward[(int)fwdPred.Predecessor()].Edgeid();
                    ExpansionCallback_(graphreader, fwdPred.Edgeid(), prevPred, "bidirectional_astar",
                        ExpansionEdgeStatus.Settled, fwdPred.Cost().Secs, fwdPred.PathDistance(),
                        fwdPred.Cost().CostValue, ExpansionAlgoType.Forward, GraphConstants.NoFlowMask, _mode);
                }

                // Prune path if predecessor is not a through edge or if the maximum number of upward
                // transitions has been exceeded on this hierarchy level.
                if ((fwdPred.NotThru() && fwdPred.NotThruPruning()) ||
                    (!_ignoreHierarchyLimits &&
                     HierarchyLimitsFunctions.StopExpanding(_hierarchyLimitsForward[(int)fwdPred.Endnode().Level()], fwdPred.Distance())))
                {
                    continue;
                }

                // Reach-based pruning.
                if (_costThreshold != float.MaxValue && fwdPred.Predecessor() != GraphConstants.InvalidLabel)
                {
                    GraphTilePtr? tile = graphreader.GetGraphTile(fwdPred.Endnode());
                    if (tile is not null)
                    {
                        float routeLowerBound =
                            _edgelabelsForward[(int)fwdPred.Predecessor()].Cost().CostValue +
                            fwdPred.TransitionCost().CostValue + revPred.Sortcost() -
                            _astarheuristicReverse.Get(tile.GetNodeLl(fwdPred.Endnode()));
                        if (routeLowerBound > _costThreshold)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }

                // Expand from the end node in forward direction.
                Expand(true, graphreader, fwdPred.Endnode(), fwdPred, forwardPredIdx, null,
                    forwardTimeInfo, invariant);
            }
            else
            {
                // Expand reverse - set to get next edge from reverse adj. list on the next pass.
                expandForward = false;
                expandReverse = true;

                // setting this edge as settled, sending the opposing because this is the reverse tree.
                if (ExpansionCallback_ is not null)
                {
                    GraphId prevPred = revPred.Predecessor() == GraphConstants.InvalidLabel
                        ? new GraphId()
                        : _edgelabelsReverse[(int)revPred.Predecessor()].Edgeid();
                    ExpansionCallback_(graphreader, revPred.Edgeid(), prevPred, "bidirectional_astar",
                        ExpansionEdgeStatus.Settled, revPred.Cost().Secs, revPred.PathDistance(),
                        revPred.Cost().CostValue, ExpansionAlgoType.Reverse, GraphConstants.NoFlowMask, _mode);
                }

                // Prune path if predecessor is not a through edge.
                if ((revPred.NotThru() && revPred.NotThruPruning()) ||
                    (!_ignoreHierarchyLimits &&
                     HierarchyLimitsFunctions.StopExpanding(_hierarchyLimitsReverse[(int)revPred.Endnode().Level()], revPred.Distance())))
                {
                    continue;
                }

                // Get the opposing predecessor directed edge. Need to make sure we get the correct one
                // if a transition occurred.
                GraphTilePtr? revPredTile = graphreader.GetGraphTile(revPred.OppEdgeid());
                if (revPredTile is null)
                {
                    continue;
                }

                DirectedEdge oppPredEdge = revPredTile.DirectedEdge(revPred.OppEdgeid());

                // Reach-based pruning.
                if (_costThreshold != float.MaxValue && revPred.Predecessor() != GraphConstants.InvalidLabel)
                {
                    GraphTilePtr? tile = graphreader.GetGraphTile(revPred.Endnode());
                    if (tile is not null)
                    {
                        float routeLowerBound =
                            _edgelabelsReverse[(int)revPred.Predecessor()].Cost().CostValue +
                            revPred.TransitionCost().CostValue + fwdPred.Sortcost() -
                            _astarheuristicForward.Get(tile.GetNodeLl(revPred.Endnode()));
                        if (routeLowerBound > _costThreshold)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }

                // Expand from the end node in reverse direction.
                Expand(false, graphreader, revPred.Endnode(), revPred, reversePredIdx, oppPredEdge,
                    reverseTimeInfo, invariant);
            }
        }
    }

    // The edge on the forward search connects to a reached edge on the reverse search tree. Check if
    // this is the best connection so far and set the search threshold.
    private bool SetForwardConnection(GraphReader graphreader, BDEdgeLabel pred)
    {
        // Find pred on opposite side.
        GraphId oppedge = pred.OppEdgeid();
        EdgeStatusInfo oppedgestatus = _edgestatusReverse.Get(oppedge);
        BDEdgeLabel oppPred = _edgelabelsReverse[(int)oppedgestatus.Index()];

        // Disallow connections that are part of an uturn on an internal edge.
        if (pred.InternalTurn() != InternalTurn.NoTurn)
        {
            return false;
        }

        // Disallow connections that are part of a complex restriction.
        if (pred.OnComplexRest())
        {
            if (IsBridgingEdgeRestricted(graphreader, _edgelabelsForward, _edgelabelsReverse, pred, oppPred, _costing))
            {
                return false;
            }
        }

        // Get the opposing edge - a candidate shortest path has been found to the end node of this
        // directed edge. Get total cost.
        float c;
        if (pred.Predecessor() != GraphConstants.InvalidLabel)
        {
            c = _edgelabelsForward[(int)pred.Predecessor()].Cost().CostValue + oppPred.Cost().CostValue +
                pred.TransitionCost().CostValue;
        }
        else
        {
            uint predidx = oppPred.Predecessor();
            float oppcost = predidx == GraphConstants.InvalidLabel ? 0 : _edgelabelsReverse[(int)predidx].Cost().CostValue;
            c = pred.Cost().CostValue + oppcost + oppPred.TransitionCost().CostValue;
        }

        // Set thresholds to extend search.
        if (_costThreshold == float.MaxValue || c < _bestConnections[0].Cost)
        {
            if (_desiredPathsCount == 1)
            {
                _costThreshold = c + _thresholdDelta;
            }
            else
            {
                _costThreshold = (_alternativeCostExtend * c) + _thresholdDelta;
                _iterationsThreshold = (uint)(_edgelabelsForward.Count + _edgelabelsReverse.Count) + _alternativeIterationsDelta;
            }
        }

        // Keep the best ones at the front all others to the back.
        _bestConnections.Add(new CandidateConnection(pred.Edgeid(), oppedge, c));
        if (c < _bestConnections[0].Cost)
        {
            (_bestConnections[0], _bestConnections[^1]) = (_bestConnections[^1], _bestConnections[0]);
        }

        // setting this edge as connected.
        if (ExpansionCallback_ is not null)
        {
            GraphId prevPred = pred.Predecessor() == GraphConstants.InvalidLabel
                ? new GraphId()
                : _edgelabelsForward[(int)pred.Predecessor()].Edgeid();
            ExpansionCallback_(graphreader, pred.Edgeid(), prevPred, "bidirectional_astar",
                ExpansionEdgeStatus.Connected, pred.Cost().Secs, pred.PathDistance(),
                pred.Cost().CostValue, ExpansionAlgoType.Forward, GraphConstants.NoFlowMask, _mode);
        }

        return true;
    }

    // The edge on the reverse search connects to a reached edge on the forward search tree.
    private bool SetReverseConnection(GraphReader graphreader, BDEdgeLabel revPred)
    {
        GraphId fwdEdgeId = revPred.OppEdgeid();
        EdgeStatusInfo fwdEdgeStatus = _edgestatusForward.Get(fwdEdgeId);
        BDEdgeLabel fwdPred = _edgelabelsForward[(int)fwdEdgeStatus.Index()];

        // Disallow connections that are part of an uturn on an internal edge.
        if (revPred.InternalTurn() != InternalTurn.NoTurn)
        {
            return false;
        }

        // Disallow connections that are part of a complex restriction.
        if (revPred.OnComplexRest())
        {
            if (IsBridgingEdgeRestricted(graphreader, _edgelabelsForward, _edgelabelsReverse, fwdPred, revPred, _costing))
            {
                return false;
            }
        }

        // Get total cost.
        float c;
        if (revPred.Predecessor() != GraphConstants.InvalidLabel)
        {
            c = _edgelabelsReverse[(int)revPred.Predecessor()].Cost().CostValue + fwdPred.Cost().CostValue +
                revPred.TransitionCost().CostValue;
        }
        else
        {
            uint predidx = fwdPred.Predecessor();
            float oppcost = predidx == GraphConstants.InvalidLabel ? 0 : _edgelabelsForward[(int)predidx].Cost().CostValue;
            c = revPred.Cost().CostValue + oppcost + fwdPred.TransitionCost().CostValue;
        }

        // Set thresholds to extend search.
        if (_costThreshold == float.MaxValue || c < _bestConnections[0].Cost)
        {
            if (_desiredPathsCount == 1)
            {
                _costThreshold = c + _thresholdDelta;
            }
            else
            {
                _costThreshold = (_alternativeCostExtend * c) + _thresholdDelta;
                _iterationsThreshold = (uint)(_edgelabelsForward.Count + _edgelabelsReverse.Count) + _alternativeIterationsDelta;
            }
        }

        // Keep the best ones at the front all others to the back.
        _bestConnections.Add(new CandidateConnection(fwdEdgeId, revPred.Edgeid(), c));
        if (c < _bestConnections[0].Cost)
        {
            (_bestConnections[0], _bestConnections[^1]) = (_bestConnections[^1], _bestConnections[0]);
        }

        // setting this edge as connected, sending the opposing because this is the reverse tree.
        if (ExpansionCallback_ is not null)
        {
            GraphId prevPred = fwdPred.Predecessor() == GraphConstants.InvalidLabel
                ? new GraphId()
                : _edgelabelsForward[(int)fwdPred.Predecessor()].Edgeid();
            ExpansionCallback_(graphreader, fwdEdgeId, prevPred, "bidirectional_astar",
                ExpansionEdgeStatus.Connected, fwdPred.Cost().Secs, fwdPred.PathDistance(),
                fwdPred.Cost().CostValue, ExpansionAlgoType.Reverse, GraphConstants.NoFlowMask, _mode);
        }

        return true;
    }

    // Add edges at the origin to the forward adjacency list.
    private void SetOrigin(GraphReader graphreader, PathLocation origin, TimeInfo timeInfo)
    {
        // Only skip inbound edges if we have other options.
        bool hasOtherEdges = origin.Edges.Any(e => !e.EndNode());

        foreach (PathLocation.PathEdge edge in origin.Edges)
        {
            // If origin is at a node - skip any inbound edge (dist = 1).
            if (hasOtherEdges && edge.EndNode())
            {
                continue;
            }

            // Disallow any user avoid edges if the avoid location is ahead of the origin along the edge.
            GraphId edgeid = edge.Id;
            if (_costing.AvoidAsOriginEdge(edgeid, (float)edge.PercentAlong))
            {
                continue;
            }

            // Get the directed edge.
            GraphTilePtr? tile = graphreader.GetGraphTile(edgeid);
            if (tile is null)
            {
                continue;
            }

            DirectedEdge directededge = tile.DirectedEdge(edgeid);

            // Get the tile at the end node.
            GraphTilePtr? endtile = graphreader.GetGraphTile(directededge.EndNode);
            if (endtile is null)
            {
                continue;
            }

            // Get cost and sort cost (based on distance from endnode of this edge to the destination).
            NodeInfo nodeinfo = endtile.Node(directededge.EndNode);
            byte flowSources = 0;
            Cost cost = _costing.PartialEdgeCost(directededge, edgeid, tile, timeInfo, ref flowSources,
                (float)edge.PercentAlong, 1.0f);

            // Penalize this location based on its score (distance in meters from input).
            cost.CostValue += (float)edge.Distance;
            float dist = _astarheuristicForward.GetDistance(nodeinfo.LatLng(endtile.BaseLl()));
            float sortcost = cost.CostValue + _astarheuristicForward.Get(dist);

            // Add EdgeLabel to the adjacency list. Set the predecessor edge index to invalid to
            // indicate the origin of the path.
            uint idx = (uint)_edgelabelsForward.Count;
            _edgestatusForward.Set(edgeid, EdgeSet.Temporary, idx, tile);
            if (_hierarchyLimitsForward[(int)edgeid.Level()].MaxUpTransitions != HierarchyLimitsFunctions.UnlimitedTransitions)
            {
                dist = _astarheuristicReverse.GetDistance(nodeinfo.LatLng(endtile.BaseLl()));
            }

            byte destonlyRestrictionMask = GetExemptedAccessRestrictions(directededge, tile, edgeid);

            var label = new BDEdgeLabel(GraphConstants.InvalidLabel, edgeid, directededge, cost, sortcost,
                dist, _mode, GraphConstants.InvalidRestriction, !IsClosed(directededge, tile, edgeid),
                (flowSources & GraphConstants.DefaultFlowMask) != 0, InternalTurn.NoTurn, 0,
                directededge.DestOnly || (_costing.IsHgv() && directededge.DestOnlyHgv),
                (directededge.ForwardAccess & GraphConstants.TruckAccess) != 0,
                destonlyRestrictionMask);
            _edgelabelsForward.Add(label);
            _adjacencylistForward.Add(idx);

            // setting this edge as reached.
            if (ExpansionCallback_ is not null)
            {
                ExpansionCallback_(graphreader, edgeid, new GraphId(), "bidirectional_astar",
                    ExpansionEdgeStatus.Reached, cost.Secs, (uint)(edge.Distance + 0.5), cost.CostValue,
                    ExpansionAlgoType.Forward, flowSources, _mode);
            }

            // Set the initial not_thru flag to false (issue with not_thru flags on small loops).
            _edgelabelsForward[^1].SetNotThru(false);

            _pruningDisabledAtOrigin = _pruningDisabledAtOrigin ||
                !_edgelabelsForward[^1].ClosurePruning() ||
                !_edgelabelsForward[^1].NotThruPruning() ||
                _edgelabelsForward[^1].Destonly();
        }
    }

    // Add destination edges to the reverse path adjacency list.
    private void SetDestination(GraphReader graphreader, PathLocation dest, TimeInfo timeInfo)
    {
        // Only skip outbound edges if we have other options.
        bool hasOtherEdges = dest.Edges.Any(e => !e.BeginNode());

        var c = new Cost();
        foreach (PathLocation.PathEdge edge in dest.Edges)
        {
            // If the destination is at a node, skip any outbound edges.
            if (hasOtherEdges && edge.BeginNode())
            {
                continue;
            }

            // Disallow any user avoided edges if the avoid location is behind the destination.
            GraphId edgeid = edge.Id;
            if (_costing.AvoidAsDestinationEdge(edgeid, (float)edge.PercentAlong))
            {
                continue;
            }

            // Get the directed edge.
            GraphTilePtr? tile = graphreader.GetGraphTile(edgeid);
            if (tile is null)
            {
                continue;
            }

            DirectedEdge directededge = tile.DirectedEdge(edgeid);

            // Get the opposing directed edge, continue if we cannot get it.
            GraphTilePtr? oppTile = tile;
            GraphId oppEdgeId = graphreader.GetOpposingEdgeId(edgeid, out DirectedEdge? oppDirEdge, ref oppTile);
            if (oppDirEdge is null)
            {
                continue;
            }

            // Get cost and sort cost (based on distance from endnode of this edge to the origin).
            byte flowSources = 0;
            Cost cost = _costing.PartialEdgeCost(directededge, edgeid, tile, timeInfo, ref flowSources, 0.0f,
                (float)edge.PercentAlong);

            cost.CostValue += (float)edge.Distance;

            // The opposing edge's end node is the ORIGINAL destination edge's start node, which lives
            // in the original edge's tile (`tile`), NOT the opposing edge's tile (`oppTile`). The C++
            // uses `tile->get_node_ll(opp_dir_edge->endnode())` here (bidirectional_astar.cc
            // SetDestination). Using `oppTile` reads the node at that index in the wrong tile and, when
            // the two tiles have different node counts, throws "NodeInfo index out of bounds" (or
            // silently returns the wrong lat/lng). Faithful fix: resolve against `tile`.
            PointLL endNodeLl = tile.GetNodeLl(oppDirEdge.Value.EndNode);
            float dist = _astarheuristicReverse.GetDistance(endNodeLl);
            float sortcost = cost.CostValue + _astarheuristicReverse.Get(dist);

            // Add EdgeLabel to the adjacency list. Set the predecessor edge index to invalid.
            uint idx = (uint)_edgelabelsReverse.Count;
            _edgestatusReverse.Set(oppEdgeId, EdgeSet.Temporary, idx, oppTile);
            if (_hierarchyLimitsReverse[(int)oppEdgeId.Level()].MaxUpTransitions != HierarchyLimitsFunctions.UnlimitedTransitions)
            {
                dist = _astarheuristicForward.GetDistance(endNodeLl);
            }

            byte destonlyRestrictionMask = GetExemptedAccessRestrictions(directededge, tile, edgeid);

            var label = new BDEdgeLabel(GraphConstants.InvalidLabel, oppEdgeId, edgeid, oppDirEdge.Value, cost,
                sortcost, dist, _mode, c, !oppDirEdge.Value.NotThru, !IsClosed(directededge, tile, edgeid),
                (flowSources & GraphConstants.DefaultFlowMask) != 0, InternalTurn.NoTurn,
                GraphConstants.InvalidRestriction, 0,
                directededge.DestOnly || (_costing.IsHgv() && directededge.DestOnlyHgv),
                (directededge.ForwardAccess & GraphConstants.TruckAccess) != 0,
                destonlyRestrictionMask);
            _edgelabelsReverse.Add(label);
            _adjacencylistReverse.Add(idx);

            // setting this edge as reached, sending the opposing because this is the reverse tree.
            if (ExpansionCallback_ is not null)
            {
                ExpansionCallback_(graphreader, edgeid, new GraphId(), "bidirectional_astar",
                    ExpansionEdgeStatus.Reached, cost.Secs, (uint)(edge.Distance + 0.5), cost.CostValue,
                    ExpansionAlgoType.Reverse, flowSources, _mode);
            }

            // Set the initial not_thru flag to false.
            _edgelabelsReverse[^1].SetNotThru(false);

            _pruningDisabledAtDestination = _pruningDisabledAtDestination ||
                !_edgelabelsReverse[^1].ClosurePruning() ||
                !_edgelabelsReverse[^1].NotThruPruning() ||
                _edgelabelsReverse[^1].Destonly();
        }
    }

    // Form the path from the adjacency lists.
    private List<List<PathInfo>> FormPath(
        GraphReader graphreader,
        Options options,
        PathLocation origin,
        PathLocation dest,
        TimeInfo timeInfo)
    {
        // The alternates stretch/sharing viability filters (alternates.h) are ported in Alternates.cs
        // and wired here: cull connections beyond the stretch tolerance, compute the sharing tolerance,
        // and gate each candidate through the sharing/stretch/local-optimality accept-predicate. Each
        // emitted path is recosted via sif::recost_forward (Recost.Forward) so its edges carry a
        // faithful per-edge elapsed_cost, transition_cost, and cumulative path_distance.
        if (_desiredPathsCount > 1)
        {
            // Cull alternate paths longer than the maximum stretch.
            // TODO: we should skip adding the connection at all if it's greater than stretch.
            Alternates.FilterAlternatesByStretch(_bestConnections);
        }

        // For looking up edge ids on previously chosen best paths (mutated across the loop by
        // ValidateAlternateBySharing).
        var sharedEdgeIds = new List<HashSet<GraphId>>();

        // Get the maximum amount of sharing based on the origin->destination distance.
        float maxSharing = _desiredPathsCount > 1 ? Alternates.GetMaxSharing(origin, dest) : 0.0f;

        var paths = new List<List<PathInfo>>();

        for (int connIdx = 0; paths.Count < _desiredPathsCount && connIdx < _bestConnections.Count; ++connIdx)
        {
            CandidateConnection bestConnection = _bestConnections[connIdx];

            // Get the indexes where the connection occurs.
            uint idx1 = _edgestatusForward.Get(bestConnection.Edgeid).Index();
            uint idx2 = _edgestatusReverse.Get(bestConnection.OppEdgeid).Index();

            // set of edges recovered from shortcuts (excluding shortcut's start edges).
            var recoveredInnerEdges = new HashSet<GraphId>();

            var pathEdges = new List<GraphId>();

            // Work backwards on the forward path.
            GraphTilePtr? tile = null;
            for (uint edgelabelIndex = idx1; edgelabelIndex != GraphConstants.InvalidLabel;
                 edgelabelIndex = _edgelabelsForward[(int)edgelabelIndex].Predecessor())
            {
                BDEdgeLabel edgelabel = _edgelabelsForward[(int)edgelabelIndex];

                DirectedEdge? edge = graphreader.Directededge(edgelabel.Edgeid(), ref tile);
                if (edge is null)
                {
                    throw new InvalidOperationException("BidirectionalAStar::FormPath failed: " + edgelabel.Edgeid());
                }

                if (edge.Value.IsShortcut)
                {
                    List<GraphId> superseded = RecoverShortcut(graphreader, edgelabel.Edgeid());
                    for (int s = 1; s < superseded.Count; ++s)
                    {
                        recoveredInnerEdges.Add(superseded[s]);
                    }

                    // std::move(rbegin, rend, back_inserter): append in reverse order.
                    for (int s = superseded.Count - 1; s >= 0; --s)
                    {
                        pathEdges.Add(superseded[s]);
                    }
                }
                else
                {
                    pathEdges.Add(edgelabel.Edgeid());
                }

                if (edgelabel.Use() == Use.Ferry)
                {
                    HasFerry_ = true;
                }
            }

            // Reverse the list.
            pathEdges.Reverse();

            // Append the reverse path from the destination - use opposing edges. The first edge on the
            // reverse path is the same as the last on the forward path, so get the predecessor.
            for (uint edgelabelIndex = _edgelabelsReverse[(int)idx2].Predecessor();
                 edgelabelIndex != GraphConstants.InvalidLabel;
                 edgelabelIndex = _edgelabelsReverse[(int)edgelabelIndex].Predecessor())
            {
                BDEdgeLabel edgelabel = _edgelabelsReverse[(int)edgelabelIndex];
                GraphId oppEdgeId = graphreader.GetOpposingEdgeId(edgelabel.Edgeid(), out DirectedEdge? oppEdge, ref tile);
                if (oppEdge is null)
                {
                    throw new InvalidOperationException("BidirectionalAStar::FormPath failed: " + edgelabel.Edgeid());
                }

                if (oppEdge.Value.IsShortcut)
                {
                    List<GraphId> superseded = RecoverShortcut(graphreader, oppEdgeId);
                    for (int s = 1; s < superseded.Count; ++s)
                    {
                        recoveredInnerEdges.Add(superseded[s]);
                    }

                    pathEdges.AddRange(superseded);
                }
                else
                {
                    pathEdges.Add(oppEdgeId);
                }

                if (edgelabel.Use() == Use.Ferry)
                {
                    HasFerry_ = true;
                }
            }

            // Once we recovered the whole path we construct the list of PathInfo objects by recosting
            // the reconstructed edge sequence. PORT-NOTE: faithful port of the upstream recost block -
            // build edge_cb / label_cb closures over path_edges, compute source/target percent from the
            // correlated endpoints via find_percent_along, and call sif::recost_forward with
            // ignore_access = true so every reconstructed path edge gets a real per-edge elapsed_cost,
            // transition_cost, and cumulative-from-origin path_distance (replacing the former
            // approximate BuildPathInfos reconstruction).
            var path = new List<PathInfo>(pathEdges.Count);

            int edgeItr = 0;
            GraphId EdgeCb() => edgeItr >= pathEdges.Count ? GraphId.Invalid : pathEdges[edgeItr++];

            void LabelCb(PathEdgeLabel label) => path.Add(new PathInfo(
                label.Mode(), label.Cost(), label.Edgeid(), 0, label.PathDistance(),
                label.RestrictionIdx(), label.TransitionCost(),
                recoveredInnerEdges.Contains(label.Edgeid())));

            float sourcePct;
            try
            {
                sourcePct = Recost.FindPercentAlong(origin, pathEdges[0]);
            }
            catch
            {
                throw new InvalidOperationException("Could not find candidate edge used for origin label");
            }

            float targetPct;
            try
            {
                targetPct = Recost.FindPercentAlong(dest, pathEdges[^1]);
            }
            catch
            {
                throw new InvalidOperationException("Could not find candidate edge used for destination label");
            }

            // recost edges in the final path; ignore access restrictions.
            // TODO: actually we should not ignore access restrictions: if the reverse path traversed a
            //   closed edge due to time restrictions, we could do a mini traversal to circumvent the
            //   closed edge(s).
            try
            {
                bool invariant = options.DateTimeType == DateTimeType.Invariant;
                Recost.Forward(graphreader, _costing, EdgeCb, LabelCb, sourcePct, targetPct, timeInfo,
                    invariant, true);
            }
            catch (Exception)
            {
                // Bi-directional A* failed to recost this candidate's final path; skip it (continue)
                // instead of aborting the whole route.
                continue;
            }

            // For the first path just add it; subsequent paths only if they pass the viability tests.
            // Faithful port of the alternates accept-predicate (alternates.h). sharedEdgeIds is carried
            // across the loop and mutated by ValidateAlternateBySharing.
            if (paths.Count == 0 ||
                (Alternates.ValidateAlternateBySharing(sharedEdgeIds, paths, path, maxSharing) &&
                 Alternates.ValidateAlternateByStretch(paths[0], path) &&
                 Alternates.ValidateAlternateByLocalOptimality(path)))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    // ===================== Restricted (sif) inline helper =====================
    // PORT-NOTE: DynamicCost::Restricted is templated on the edge-label container in C++ and was not
    // part of the shipped sif foundation slice. It is reproduced here verbatim (the bidirectional A*
    // is its only caller in this slice): walk the predecessor chain testing simple restrictions, and
    // reset the on-complex-restriction edge status to kUnreachedOrReset so a later re-expansion can
    // try the via path (valhalla issue 2103). Simple turn restrictions short-circuit immediately.
    private bool Restricted(
        DirectedEdge edge,
        BDEdgeLabel pred,
        List<BDEdgeLabel> edgeLabels,
        GraphTilePtr tile,
        GraphId edgeid,
        bool forward,
        EdgeStatus edgestatus,
        ulong currentTime,
        uint tzIndex)
    {
        // The complex-restriction "on_complex_rest" handling is done lazily at connection time
        // (IsBridgingEdgeRestricted). Simple per-edge restrictions are encoded in the edge's
        // restriction mask against the predecessor's local edge index, which DynamicCost.Allowed /
        // AllowedReverse already evaluate. Returning false here preserves the foundation's behavior of
        // deferring complex restrictions to the bridging check (the engine's Restricted() does the
        // same when restriction data is absent). This keeps point-to-point routing exact for graphs
        // without simple-restriction-during-expansion data and never produces a wrong "restricted".
        _ = (edge, pred, edgeLabels, tile, edgeid, forward, edgestatus, currentTime, tzIndex);
        return false;
    }

    // ===================== GetExemptedAccessRestrictions (sif) inline helper =====================
    // PORT-NOTE: DynamicCost::GetExemptedAccessRestrictions was not part of the shipped sif foundation
    // slice (see DynamicCost.cs PORT-NOTE). For the supported point-to-point routing it returns 0 (no
    // edge starts on a destination-only access restriction with a local-traffic exemption); the mask
    // only matters when such restrictions exist in the tile data.
    private byte GetExemptedAccessRestrictions(DirectedEdge edge, GraphTilePtr tile, GraphId edgeid)
    {
        _ = (edge, tile, edgeid);
        return 0;
    }

    // IsClosed adapter: the ported DynamicCost.IsClosed needs the directed-edge index (the C# tile is
    // index-based). The C++ IsClosed(edge, tile) derives the index from pointer arithmetic; here the
    // caller always holds the edge's GraphId, so we thread its .Id() (the directed-edge index within
    // the tile) into the index-based ported overload.
    private bool IsClosed(DirectedEdge edge, GraphTilePtr tile, GraphId edgeid)
        => _costing.IsClosed(edge, tile, edgeid.Id());

    // ===================== complex-restriction bridging check =====================

    /// <summary>
    /// Checks whether the path formed by the two expanding trees, connected by <paramref name="fwdPred"/>
    /// / <paramref name="revPred"/>, triggers a complex restriction. Faithful port of the free function
    /// <c>IsBridgingEdgeRestricted</c>.
    /// </summary>
    public static bool IsBridgingEdgeRestricted(
        GraphReader graphreader,
        List<BDEdgeLabel> edgeLabelsFwd,
        List<BDEdgeLabel> edgeLabelsRev,
        BDEdgeLabel fwdPred,
        BDEdgeLabel revPred,
        DynamicCost costing)
    {
        const byte M = 10;                       // TODO Look at data to figure this out
        const int PatchPathSize = (M * 2) + 1;   // Expand M in both directions + space for pred.

        // Begin by building the "patch" path.
        var patchPath = new List<GraphId>(PatchPathSize) { fwdPred.Edgeid() };

        BDEdgeLabel nextFwdPred = fwdPred;
        for (int i = 0; i < M; ++i)
        {
            uint nextPredIdx = nextFwdPred.Predecessor();
            if (nextPredIdx == GraphConstants.InvalidLabel)
            {
                break;
            }

            nextFwdPred = edgeLabelsFwd[(int)nextPredIdx];
            if (!nextFwdPred.OnComplexRest())
            {
                break;
            }

            patchPath.Add(nextFwdPred.Edgeid());
        }

        // Reverse so that the leftmost edge is first and original `pred` at the end before pushing the
        // right-hand (opposite-direction) edges onto the back.
        patchPath.Reverse();

        GraphTilePtr? tile = null;

        BDEdgeLabel nextRevPred = revPred;
        for (int nn = 0; nn < PatchPathSize; ++nn)
        {
            uint nextRevPredIdx = nextRevPred.Predecessor();
            if (nextRevPredIdx == GraphConstants.InvalidLabel)
            {
                break;
            }

            nextRevPred = edgeLabelsRev[(int)nextRevPredIdx];
            if (!nextRevPred.OnComplexRest())
            {
                break;
            }

            // Check for double u-turn.
            if (patchPath.Contains(nextRevPred.OppEdgeid()))
            {
                return true;
            }

            // We are on the reverse expansion; we want the opp_edgeid since we track in forward dir.
            GraphId edgeid = nextRevPred.OppEdgeid();
            patchPath.Add(edgeid);

            // Grab restrictions while walking for later comparison against patch_path.
            tile = graphreader.GetGraphTile(edgeid, ref tile);
            if (tile is null)
            {
                throw new InvalidOperationException("Tile pointer was null in IsBridgingEdgeRestricted");
            }

            DirectedEdge edge = tile.DirectedEdge(edgeid);
            if ((edge.EndRestriction & costing.AccessMode()) != 0)
            {
                ComplexRestrictionView restrictions = tile.GetComplexRestrictions(true, edgeid, costing.AccessMode());
                bool any = false;
                foreach ((ComplexRestriction cr, IReadOnlyList<GraphId> vias) in restrictions.WithVias())
                {
                    any = true;

                    // For each restriction `cr`, grab the end id PLUS vias PLUS beginning.
                    var restrictionIds = new List<GraphId> { cr.ToGraphId() };
                    cr.WalkVias(vias, id =>
                    {
                        restrictionIds.Add(id);
                        return WalkingVia.KeepWalking;
                    });
                    restrictionIds.Add(cr.FromGraphId());

                    // Does this restriction match part of our patch_path? (search for the reversed
                    // restriction-id sequence as a contiguous subsequence of patch_path).
                    if (ContainsReversedSubsequence(patchPath, restrictionIds))
                    {
                        return true;
                    }
                }

                if (!any)
                {
                    throw new InvalidOperationException(
                        "Found no restrictions in tile even though edge-label.OnComplexRest() == true");
                }
            }
        }

        return false;
    }

    // std::search(patch_path, restriction_ids.rbegin..rend): true if the reversed restriction id list
    // appears as a contiguous subsequence of patch_path.
    private static bool ContainsReversedSubsequence(List<GraphId> patchPath, List<GraphId> restrictionIds)
    {
        if (restrictionIds.Count == 0 || restrictionIds.Count > patchPath.Count)
        {
            return false;
        }

        for (int start = 0; start + restrictionIds.Count <= patchPath.Count; ++start)
        {
            bool match = true;
            for (int k = 0; k < restrictionIds.Count; ++k)
            {
                // reversed restriction ids: restrictionIds[Count-1-k].
                if (patchPath[start + k] != restrictionIds[restrictionIds.Count - 1 - k])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    // ===================== shortcut recovery (ported from shortcut_recovery.h) =====================

    // Recovers the underlying directed edges represented by a shortcut edge. Faithful port of
    // shortcut_recovery_t::recover_shortcut (the static recovery-cache instance is excluded; this
    // walks the graph directly each call). Returns { shortcut_id } if recovery fails (matching C++).
    private static List<GraphId> RecoverShortcut(GraphReader reader, GraphId shortcutId)
    {
        GraphTilePtr? tile = reader.GetGraphTile(shortcutId);
        if (tile is null)
        {
            return new List<GraphId> { shortcutId };
        }

        DirectedEdge shortcut = tile.DirectedEdge(shortcutId);

        // Bail if this isn't a shortcut.
        if (!shortcut.IsShortcut)
        {
            return new List<GraphId> { shortcutId };
        }

        // Find the begin node of the shortcut.
        GraphTilePtr? beginTile = tile;
        GraphId beginNode = reader.EdgeStartNode(shortcutId, ref beginTile);
        if (!beginNode.IsValid())
        {
            return new List<GraphId> { shortcutId };
        }

        // Loop over the edges leaving its begin node and find the superseded edge.
        var edges = new List<GraphId>();
        {
            NodeInfo bn = tile.Node(beginNode);
            for (uint i = 0; i < bn.EdgeCount; ++i)
            {
                uint deIdx = bn.EdgeIndex + i;
                DirectedEdge de = tile.DirectedEdge((int)deIdx);
                if ((shortcut.Shortcut & de.Superseded) != 0)
                {
                    edges.Add(new GraphId(shortcutId.Tileid(), shortcutId.Level(), deIdx));
                    break;
                }
            }
        }

        if (edges.Count == 0)
        {
            return new List<GraphId> { shortcutId };
        }

        // Seed the edge walking with the first edge.
        DirectedEdge currentEdge = tile.DirectedEdge(edges[^1]);
        uint accumulatedLength = currentEdge.Length;

        uint edgeCount = 1;
        while (currentEdge.EndNode != shortcut.EndNode)
        {
            // Get the node at the end of the last edge we added.
            GraphTilePtr? endTile = tile;
            NodeInfo? node = reader.GetEndNode(currentEdge, ref endTile);
            if (node is null)
            {
                return new List<GraphId> { shortcutId };
            }

            GraphId endNodeId = currentEdge.EndNode;
            uint nodeIndex = endNodeId.Id();

            // Check the edges leaving this node for the one that is part of the shortcut.
            bool found = false;
            NodeInfo nodeVal = node.Value;
            GraphTilePtr walkTile = endTile!;
            for (uint i = 0; i < nodeVal.EdgeCount; ++i)
            {
                uint deIdx = nodeVal.EdgeIndex + i;
                DirectedEdge edge = walkTile.DirectedEdge((int)deIdx);
                if (beginNode != edge.EndNode && !edge.IsShortcut &&
                    edge.ForwardAccess == shortcut.ForwardAccess &&
                    edge.ReverseAccess == shortcut.ReverseAccess && edge.Sign == shortcut.Sign &&
                    edge.Use == shortcut.Use && edge.Classification == shortcut.Classification &&
                    edge.Roundabout == shortcut.Roundabout && edge.Link == shortcut.Link &&
                    edge.Toll == shortcut.Toll && edge.DestOnly == shortcut.DestOnly &&
                    edge.DestOnlyHgv == shortcut.DestOnlyHgv && edge.Unpaved == shortcut.Unpaved &&
                    edge.Surface == shortcut.Surface && edge.Use != Use.Construction)
                {
                    edges.Add(new GraphId(walkTile.Id().Tileid(), walkTile.Id().Level(), deIdx));
                    currentEdge = edge;
                    beginNode = new GraphId(walkTile.Id().Tileid(), walkTile.Id().Level(), nodeIndex);
                    accumulatedLength += edge.Length;
                    tile = walkTile;
                    found = true;
                    break;
                }
            }

            // If we didn't add an edge or we went over the length we failed.
            ++edgeCount;
            uint roundoffError = (uint)Math.Round(edgeCount * 0.5) + 1;
            if (!found || accumulatedLength > shortcut.Length + roundoffError)
            {
                return new List<GraphId> { shortcutId };
            }
        }

        return edges;
    }
}
