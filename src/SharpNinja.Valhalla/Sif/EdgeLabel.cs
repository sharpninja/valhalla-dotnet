// Faithful C# port of Valhalla sif edgelabel.h (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/sif/edgelabel.h
//
// Labeling information for shortest path and graph expansion algorithms. Contains cost,
// predecessor, path distance, and assorted edge information required during construction of
// the shortest path and for reconstructing the path upon completion.
//
// PORT-NOTE: The C++ class packs most members into bit fields purely to keep the label small
// (it is allocated in the millions). The bit widths do not affect routing results except where
// a value is intentionally truncated (none of the accessors here are). The C# port stores the
// values in ordinary backing fields of the corresponding width and exposes PascalCase getters
// with the same semantics. The derived label classes (PathEdgeLabel, BDEdgeLabel, MMEdgeLabel)
// follow in the same file as in the C++ header.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Sif;

/// <summary>
/// Sif edge-label initial-capacity constants. Faithful port of the <c>constexpr uint32_t</c>
/// declarations at the top of <c>edgelabel.h</c>.
/// </summary>
public static class EdgeLabelConstants
{
    public const uint InitialEdgeLabelCountAstar = 2000000;
    public const uint InitialEdgeLabelCountBidirAstar = 1000000;
    public const uint InitialEdgeLabelCountDijkstras = 4000000;
    public const uint InitialEdgeLabelCountBidirDijkstra = 2000000;
}

/// <summary>
/// Labeling information for shortest path and graph expansion algorithms.
/// Faithful port of <c>valhalla::sif::EdgeLabel</c>.
/// </summary>
/// <remarks>
/// PORT-NOTE: implements <see cref="ISortCost"/> so the labels can be stored directly in the ported
/// <see cref="DoubleBucketQueue{TLabel}"/> (the C++ queue reads <c>sortcost()</c> from the label
/// container by index; the interface exposes the identical value via <see cref="SortCost"/>).
/// </remarks>
public class EdgeLabel : ISortCost
{
    protected uint Predecessor_;
    protected uint PathDistance_;     // :25
    protected uint Restrictions_;     // :7
    protected ulong Edgeid_;          // :46
    protected uint OppIndex_;         // :7
    protected uint OppLocalIdx_;      // :7
    protected uint Mode_;             // :4
    protected ulong Endnode_;         // :46
    protected uint Use_;              // :6
    protected uint Classification_;   // :3
    protected bool Shortcut_;
    protected bool DestOnly_;
    protected bool Origin_;
    protected bool Destination_;
    protected bool Toll_;
    protected bool NotThru_;
    protected bool Deadend_;
    protected bool OnComplexRest_;
    protected bool ClosurePruning_;
    protected byte PathId_;           // :7
    protected byte RestrictionIdx_;   // :8
    protected byte InternalTurn_;     // :2
    protected bool Unpaved_;
    protected bool HasMeasuredSpeed_;
    protected bool HgvAccess_;
    protected bool Bridge_;
    protected bool Tunnel_;
    protected byte DestonlyAccessRestrMask_; // :7
    protected Cost Cost_;
    protected float Sortcost_;

    /// <summary>Default constructor. Mirrors the C++ default member-initializer list.</summary>
    public EdgeLabel()
    {
        Predecessor_ = GraphConstants.InvalidLabel;
        PathDistance_ = 0;
        Restrictions_ = 0;
        Edgeid_ = GraphId.InvalidGraphId;
        OppIndex_ = 0;
        OppLocalIdx_ = 0;
        Mode_ = 0;
        Endnode_ = GraphId.InvalidGraphId;
        Use_ = 0;
        Classification_ = 0;
        Shortcut_ = false;
        DestOnly_ = false;
        Origin_ = false;
        Destination_ = false;
        Toll_ = false;
        NotThru_ = false;
        Deadend_ = false;
        OnComplexRest_ = false;
        ClosurePruning_ = false;
        PathId_ = 0;
        RestrictionIdx_ = 0;
        InternalTurn_ = 0;
        Unpaved_ = false;
        HasMeasuredSpeed_ = false;
        HgvAccess_ = false;
        Bridge_ = false;
        Tunnel_ = false;
        DestonlyAccessRestrMask_ = 0;
        Cost_ = new Cost(0, 0);
        Sortcost_ = 0;
    }

    /// <summary>Constructor with values. Faithful port of the C++ value constructor.</summary>
    public EdgeLabel(
        uint predecessor,
        GraphId edgeid,
        DirectedEdge edge,
        Cost cost,
        float sortcost,
        TravelMode mode,
        uint pathDistance,
        byte restrictionIdx,
        bool closurePruning,
        bool hasMeasuredSpeed,
        InternalTurn internalTurn,
        byte pathId = 0,
        bool destonly = false,
        bool hgvAccess = false,
        byte destonlyAccessRestrMask = 0)
    {
        Predecessor_ = predecessor;
        PathDistance_ = pathDistance;
        Restrictions_ = edge.Restrictions;
        Edgeid_ = edgeid.Value;
        OppIndex_ = edge.OppIndex;
        OppLocalIdx_ = edge.OppLocalIdx;
        Mode_ = (uint)mode;
        Endnode_ = edge.EndNode.Value;
        Use_ = (uint)edge.Use;
        Classification_ = (uint)edge.Classification;
        Shortcut_ = edge.Shortcut != 0;
        Origin_ = false;
        Destination_ = false;
        Toll_ = edge.Toll;
        NotThru_ = edge.NotThru;
        Deadend_ = edge.Deadend;
        OnComplexRest_ = edge.PartOfComplexRestriction || edge.StartRestriction != 0 || edge.EndRestriction != 0;
        ClosurePruning_ = closurePruning;
        PathId_ = pathId;
        RestrictionIdx_ = restrictionIdx;
        InternalTurn_ = (byte)internalTurn;
        Unpaved_ = edge.Unpaved;
        HasMeasuredSpeed_ = hasMeasuredSpeed;
        HgvAccess_ = hgvAccess;
        Bridge_ = edge.Bridge;
        Tunnel_ = edge.Tunnel;
        DestonlyAccessRestrMask_ = destonlyAccessRestrMask;
        Cost_ = cost;
        Sortcost_ = sortcost;
        DestOnly_ = destonly ? destonly : edge.DestOnly;
    }

    /// <summary>
    /// Update an existing edge label with new predecessor and cost information.
    /// </summary>
    public void Update(uint predecessor, Cost cost, float sortcost, uint pathDistance, byte restrictionIdx)
    {
        Predecessor_ = predecessor;
        Cost_ = cost;
        Sortcost_ = sortcost;
        PathDistance_ = pathDistance;
        RestrictionIdx_ = restrictionIdx;
    }

    /// <summary>Get the predecessor edge label.</summary>
    public uint Predecessor() => Predecessor_;

    /// <summary>Get the GraphId of this directed edge.</summary>
    public GraphId Edgeid() => new GraphId(Edgeid_);

    /// <summary>Get the end node of this directed edge.</summary>
    public GraphId Endnode() => new GraphId(Endnode_);

    /// <summary>Get the cost from the origin to this directed edge.</summary>
    public Cost Cost() => Cost_;

    /// <summary>Get the sort cost from the origin to this directed edge (includes A* heuristic).</summary>
    public float Sortcost() => Sortcost_;

    /// <summary>
    /// <see cref="ISortCost"/> implementation. Identical to <see cref="Sortcost"/>; lets the label be
    /// stored in <see cref="DoubleBucketQueue{TLabel}"/> (PORT-NOTE on the class).
    /// </summary>
    public float SortCost() => Sortcost_;

    /// <summary>Set the sort cost from the origin to this directed edge.</summary>
    public void SetSortCost(float sortcost) => Sortcost_ = sortcost;

    /// <summary>Get the use of the directed edge.</summary>
    public Use Use() => (Use)Use_;

    /// <summary>Get the opposing index - for bidirectional A*.</summary>
    public uint OppIndex() => OppIndex_;

    /// <summary>Get the opposing local index.</summary>
    public uint OppLocalIdx() => OppLocalIdx_;

    /// <summary>Get the restriction mask at the end node.</summary>
    public uint Restrictions() => Restrictions_;

    /// <summary>Get the shortcut flag.</summary>
    public bool Shortcut() => Shortcut_;

    /// <summary>Get the travel mode along this edge.</summary>
    public TravelMode Mode() => (TravelMode)Mode_;

    /// <summary>Get the dest only flag.</summary>
    public bool Destonly() => DestOnly_;

    /// <summary>Is this edge an origin edge?</summary>
    public bool Origin() => Origin_;

    /// <summary>Sets this edge as an origin.</summary>
    public void SetOrigin() => Origin_ = true;

    /// <summary>Is this edge a destination edge?</summary>
    public bool Destination() => Destination_;

    /// <summary>Sets this edge as a destination.</summary>
    public void SetDestination() => Destination_ = true;

    /// <summary>Get the restriction idx, 255 means no restriction.</summary>
    public byte RestrictionIdx() => RestrictionIdx_;

    /// <summary>Get the internal_turn.</summary>
    public InternalTurn InternalTurn() => (InternalTurn)InternalTurn_;

    /// <summary>Does this edge have a toll?</summary>
    public bool Toll() => Toll_;

    /// <summary>Get the current path distance in meters.</summary>
    public uint PathDistance() => PathDistance_;

    /// <summary>Get the predecessor road classification.</summary>
    public RoadClass Classification() => (RoadClass)Classification_;

    /// <summary>Operator &lt; used for sorting.</summary>
    public static bool operator <(EdgeLabel a, EdgeLabel b) => a.Sortcost() < b.Sortcost();

    /// <summary>Operator &gt; used for sorting.</summary>
    public static bool operator >(EdgeLabel a, EdgeLabel b) => a.Sortcost() > b.Sortcost();

    /// <summary>Is this edge part of a complex restriction.</summary>
    public bool OnComplexRest() => OnComplexRest_;

    /// <summary>Is this edge not-through.</summary>
    public bool NotThru() => NotThru_;

    /// <summary>Set the not-through flag for this edge.</summary>
    public void SetNotThru(bool notThru) => NotThru_ = notThru;

    /// <summary>Is this edge a dead end.</summary>
    public bool Deadend() => Deadend_;

    /// <summary>Set the dead end flag.</summary>
    public void SetDeadend(bool isDeadend) => Deadend_ = isDeadend;

    /// <summary>Returns the location/path id (index) of the path that this label is tracking.</summary>
    public byte PathId() => PathId_;

    /// <summary>Should closure pruning be enabled on this path?</summary>
    public bool ClosurePruning() => ClosurePruning_;

    /// <summary>Do we have any of the measured speed types set?</summary>
    public bool HasMeasuredSpeed() => HasMeasuredSpeed_;

    /// <summary>Get the unpaved flag.</summary>
    public bool Unpaved() => Unpaved_;

    /// <summary>Get the bridge flag.</summary>
    public bool Bridge() => Bridge_;

    /// <summary>Get the tunnel flag.</summary>
    public bool Tunnel() => Tunnel_;

    /// <summary>Does it have HGV access? Returns true if the (opposing) edge had HGV access.</summary>
    public bool HasHgvAccess() => HgvAccess_;

    /// <summary>Get the access restriction mask for restrictions with a local traffic exemption.</summary>
    public byte DestonlyAccessRestrMask() => DestonlyAccessRestrMask_;
}

/// <summary>
/// Derived label class used for recosting paths within the LabelCallback.
/// Faithful port of <c>valhalla::sif::PathEdgeLabel</c>.
/// </summary>
public class PathEdgeLabel : EdgeLabel
{
    protected Cost TransitionCost_;

    /// <summary>Default constructor.</summary>
    public PathEdgeLabel()
    {
    }

    /// <summary>Constructor with values.</summary>
    public PathEdgeLabel(
        uint predecessor,
        GraphId edgeid,
        DirectedEdge edge,
        Cost cost,
        float sortcost,
        TravelMode mode,
        uint pathDistance,
        Cost transitionCost,
        byte restrictionIdx,
        bool closurePruning,
        bool hasMeasuredSpeed,
        InternalTurn internalTurn,
        byte pathId = 0,
        bool destonly = false,
        bool hgvAccess = false)
        : base(predecessor, edgeid, edge, cost, sortcost, mode, pathDistance, restrictionIdx,
               closurePruning, hasMeasuredSpeed, internalTurn, pathId, destonly, hgvAccess)
    {
        TransitionCost_ = transitionCost;
    }

    /// <summary>Get the transition cost (including penalties).</summary>
    public Cost TransitionCost() => TransitionCost_;
}

/// <summary>
/// Derived EdgeLabel class used for A* path algorithms and CostMatrix.
/// Faithful port of <c>valhalla::sif::BDEdgeLabel</c>.
/// </summary>
public class BDEdgeLabel : EdgeLabel
{
    protected Cost TransitionCost_;
    protected ulong OppEdgeid_;       // :63
    protected bool NotThruPruning_;
    protected float Distance_;

    /// <summary>Default constructor.</summary>
    public BDEdgeLabel()
    {
    }

    /// <summary>Constructor with values (with sortcost and distance to destination).</summary>
    public BDEdgeLabel(
        uint predecessor,
        GraphId edgeid,
        GraphId oppedgeid,
        DirectedEdge edge,
        Cost cost,
        float sortcost,
        float dist,
        TravelMode mode,
        Cost transitionCost,
        bool notThruPruning,
        bool closurePruning,
        bool hasMeasuredSpeed,
        InternalTurn internalTurn,
        byte restrictionIdx,
        byte pathId = 0,
        bool destonly = false,
        bool hgvAccess = false,
        byte destonlyAccessRestrMask = 0)
        : base(predecessor, edgeid, edge, cost, sortcost, mode, 0, restrictionIdx, closurePruning,
               hasMeasuredSpeed, internalTurn, pathId, destonly, hgvAccess, destonlyAccessRestrMask)
    {
        TransitionCost_ = transitionCost;
        OppEdgeid_ = oppedgeid.Value;
        NotThruPruning_ = notThruPruning;
        Distance_ = dist;
    }

    /// <summary>Constructor with values. Sets sortcost to the true cost (CostMatrix).</summary>
    public BDEdgeLabel(
        uint predecessor,
        GraphId edgeid,
        GraphId oppedgeid,
        DirectedEdge edge,
        Cost cost,
        TravelMode mode,
        Cost transitionCost,
        uint pathDistance,
        bool notThruPruning,
        bool closurePruning,
        bool hasMeasuredSpeed,
        InternalTurn internalTurn,
        byte restrictionIdx,
        byte pathId = 0,
        bool destonly = false,
        bool hgvAccess = false,
        byte destonlyAccessRestrMask = 0)
        : base(predecessor, edgeid, edge, cost, cost.CostValue, mode, pathDistance, restrictionIdx,
               closurePruning, hasMeasuredSpeed, internalTurn, pathId, destonly, hgvAccess,
               destonlyAccessRestrMask)
    {
        TransitionCost_ = transitionCost;
        OppEdgeid_ = oppedgeid.Value;
        NotThruPruning_ = notThruPruning;
        Distance_ = 0.0f;
    }

    /// <summary>Constructor with values. Used in SetOrigin.</summary>
    public BDEdgeLabel(
        uint predecessor,
        GraphId edgeid,
        DirectedEdge edge,
        Cost cost,
        float sortcost,
        float dist,
        TravelMode mode,
        byte restrictionIdx,
        bool closurePruning,
        bool hasMeasuredSpeed,
        InternalTurn internalTurn,
        byte pathId = 0,
        bool destonly = false,
        bool hgvAccess = false,
        byte destonlyAccessRestrMask = 0)
        : base(predecessor, edgeid, edge, cost, sortcost, mode, 0, restrictionIdx, closurePruning,
               hasMeasuredSpeed, internalTurn, pathId, destonly, hgvAccess, destonlyAccessRestrMask)
    {
        TransitionCost_ = new Cost();
        NotThruPruning_ = !edge.NotThru;
        Distance_ = dist;
        OppEdgeid_ = 0;
    }

    /// <summary>Update an existing edge label with new predecessor and cost information.</summary>
    public void Update(uint predecessor, Cost cost, float sortcost, Cost tc, byte restrictionIdx)
    {
        Predecessor_ = predecessor;
        Cost_ = cost;
        Sortcost_ = sortcost;
        TransitionCost_ = tc;
        RestrictionIdx_ = restrictionIdx;
    }

    /// <summary>Update an existing edge label including distance (used in time distance matrix).</summary>
    public void Update(uint predecessor, Cost cost, float sortcost, Cost tc, uint pathDistance, byte restrictionIdx)
    {
        Predecessor_ = predecessor;
        Cost_ = cost;
        Sortcost_ = sortcost;
        TransitionCost_ = tc;
        PathDistance_ = pathDistance;
        RestrictionIdx_ = restrictionIdx;
    }

    /// <summary>Get the distance to the destination (meters).</summary>
    public float Distance() => Distance_;

    /// <summary>Get the transition cost (including penalties).</summary>
    public Cost TransitionCost() => TransitionCost_;

    /// <summary>Get the GraphId of the opposing directed edge.</summary>
    public GraphId OppEdgeid() => new GraphId(OppEdgeid_);

    /// <summary>Should not thru pruning be enabled on this path?</summary>
    public bool NotThruPruning() => NotThruPruning_;

    /// <summary>Sets the path distance for this EdgeLabel.</summary>
    public void SetPathDistance(float distance) => PathDistance_ = (uint)distance;
}

/// <summary>
/// EdgeLabel used for multi-modal A* path algorithm.
/// Faithful port of <c>valhalla::sif::MMEdgeLabel</c>.
/// </summary>
public class MMEdgeLabel : EdgeLabel
{
    protected Cost TransitionCost_;
    protected GraphId PriorStopid_;
    protected uint Tripid_;
    protected uint Blockid_;            // :21
    protected uint TransitOperator_;    // :10
    protected bool HasTransit_;
    protected uint WalkingDistance_;
    protected float Distance_;

    /// <summary>Default constructor.</summary>
    public MMEdgeLabel()
    {
    }

    /// <summary>Constructor with values. Used for multi-modal path.</summary>
    public MMEdgeLabel(
        uint predecessor,
        GraphId edgeid,
        DirectedEdge edge,
        Cost cost,
        float sortcost,
        float dist,
        TravelMode mode,
        uint pathDistance,
        uint walkingDistance,
        uint tripid,
        GraphId priorStopid,
        uint blockid,
        uint transitOperator,
        bool hasTransit,
        Cost transitionCost,
        byte restrictionIdx,
        byte pathId = 0,
        bool destonly = false)
        : base(predecessor, edgeid, edge, cost, sortcost, mode, pathDistance, restrictionIdx, true,
               false, Sif.InternalTurn.NoTurn, pathId, destonly)
    {
        TransitionCost_ = transitionCost;
        PriorStopid_ = priorStopid;
        Tripid_ = tripid;
        Blockid_ = blockid;
        TransitOperator_ = transitOperator;
        HasTransit_ = hasTransit;
        WalkingDistance_ = walkingDistance;
        Distance_ = dist;
    }

    /// <summary>Update an existing edge label with new predecessor, cost and transit information.</summary>
    public void Update(
        uint predecessor,
        Cost cost,
        float sortcost,
        uint pathDistance,
        uint walkingDistance,
        uint tripid,
        uint blockid,
        Cost transitionCost,
        byte restrictionIdx)
    {
        Predecessor_ = predecessor;
        Cost_ = cost;
        Sortcost_ = sortcost;
        PathDistance_ = pathDistance;
        WalkingDistance_ = walkingDistance;
        Tripid_ = tripid;
        Blockid_ = blockid;
        TransitionCost_ = transitionCost;
        RestrictionIdx_ = restrictionIdx;
    }

    /// <summary>Get the distance to the destination (meters).</summary>
    public float Distance() => Distance_;

    /// <summary>Get the transition cost (including penalties).</summary>
    public Cost TransitionCost() => TransitionCost_;

    /// <summary>Get the prior transit stop Id.</summary>
    public GraphId PriorStopid() => PriorStopid_;

    /// <summary>Get the transit trip Id of the prior edge.</summary>
    public uint Tripid() => Tripid_;

    /// <summary>Return the transit block Id of the prior trip.</summary>
    public uint Blockid() => Blockid_;

    /// <summary>Get the index of the transit operator (0 if none).</summary>
    public uint TransitOperator() => TransitOperator_;

    /// <summary>Has any transit been taken up to this point on the path.</summary>
    public bool HasTransit() => HasTransit_;

    /// <summary>Return the current walking distance in meters.</summary>
    public uint WalkingDistance() => WalkingDistance_;
}
