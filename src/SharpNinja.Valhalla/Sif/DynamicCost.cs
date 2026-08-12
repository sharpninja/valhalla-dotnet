// Faithful C# port of Valhalla sif dynamiccost (valhalla @ 3.7.0).
// Sources:
//   - valhalla/sif/dynamiccost.h  (DynamicCost base class, constants, helpers, get_base_costs,
//                                  base_transition_cost, TurnType, AddUturnPenalty)
//   - src/sif/dynamiccost.cc      (ctor, virtual defaults, set_use_tracks/living_streets/lit,
//                                  custom_cost_t::sort_and_find_smallest)
//   - valhalla/sif/osrm_car_duration.h (OSRMCarTurnDuration)
//
// Abstract base class for dynamic edge costing. Derived classes (Auto/Truck/etc) implement
// access checks, edge cost, and A* setup. This is the FOUNDATION port: the shared helpers and
// the exact virtual-method signatures that the Auto/Truck ports must override verbatim.

using System;
using System.Collections.Generic;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

// PORT-NOTE: `graph_tile_ptr` in C++ is a ref-counted pointer to a const GraphTile. The concrete
// GraphTile is the only implementer of IGraphTilePtr in this codebase; we alias GraphTilePtr to a
// (nullable) GraphTile reference so sif signatures read like the C++ ones.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;

// thor aliases: DynamicCost::Restricted takes the thor EdgeStatus map / EdgeSet to reset
// permanently-labeled via edges (valhalla issue 2103); referenced via aliases to avoid importing
// the whole Thor namespace into Sif.
using ThorEdgeStatusMap = SharpNinja.Valhalla.Thor.EdgeStatus;
using ThorEdgeSet = SharpNinja.Valhalla.Thor.EdgeSet;

namespace SharpNinja.Valhalla.Sif;



/// <summary>
/// Limited graph reader placeholder. The full <c>valhalla::baldr::GraphReader::LimitedGraphReader</c>
/// belongs to a later thor/loki port slice; the transition-cost signatures take a getter for it so
/// derived costers can fetch the predecessor tile. PORT-NOTE: stub kept minimal.
/// </summary>
public sealed class LimitedGraphReader
{
}


/// <summary>cost_edge_t: a [start,end] fraction along an edge plus a cost factor.</summary>
public struct CostEdge
{
    public double Start;
    public double End;
    public double Factor;

    public CostEdge(double start, double end, double factor)
    {
        Start = start;
        End = end;
        Factor = factor;
    }

    public CostEdge(double factor)
    {
        Start = 0.0;
        End = 1.0;
        Factor = factor;
    }
}

/// <summary>
/// custom_cost_t: a set of cost-factor ranges along a single edge plus the precomputed average
/// factor. Faithful port of <c>valhalla::sif::custom_cost_t</c>.
/// </summary>
public sealed class CustomCost
{
    public List<CostEdge> Ranges { get; } = new();
    public double AvgFactor { get; set; } = 1.0;

    /// <summary>
    /// When all ranges for a given edge are added, sort by range start and return the smallest
    /// factor found for this edge. Faithful port of <c>sort_and_find_smallest()</c>.
    /// </summary>
    public double SortAndFindSmallest()
    {
        if (Ranges.Count == 0)
            return 1.0;

        Ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        double uncovered = 1.0;
        double avg = 0.0;
        double minFactor = 1.0;
        foreach (var range in Ranges)
        {
            uncovered -= range.End - range.Start;
            avg += (range.End - range.Start) * range.Factor;
            minFactor = Math.Min(minFactor, range.Factor);
        }

        avg += uncovered * 1.0;
        AvgFactor = Math.Max(avg, DynamicCost.MinCustomFactor);
        return Math.Max(minFactor, DynamicCost.MinCustomFactor);
    }
}

/// <summary>
/// Base class for dynamic edge costing. Faithful port of <c>valhalla::sif::DynamicCost</c>.
/// </summary>
public abstract class DynamicCost
{
    // ===================== constants (dynamiccost.h) =====================

    /// <summary>Default unit size (seconds) for cost sorting.</summary>
    public const uint DefaultUnitSize = 1;

    /// <summary>Maximum penalty allowed (12 hours).</summary>
    public const float MaxPenalty = 12.0f * Constants.SecPerHour;

    /// <summary>Maximum ferry penalty (when use_ferry == 0 or use_rail_ferry == 0) (6 hours).</summary>
    public const float MaxFerryPenalty = 6.0f * Constants.SecPerHour;

    /// <summary>Default uturn cost: unfavorable pencil point uturn (multiplier).</summary>
    public const float TCUnfavorablePencilPointUturn = 15.0f;

    /// <summary>Default uturn cost: unfavorable uturn (seconds).</summary>
    public const float TCUnfavorableUturn = 600.0f;

    /// <summary>Default uturn cost: name-inconsistent uturn (seconds).</summary>
    public const float TCNameInconsistentUturn = 10.0f;

    /// <summary>Maximum highway avoidance bias.</summary>
    public const float MaxHighwayBiasFactor = 8.0f;

    // loki::reach disallow mask values
    public const ushort DisallowNone = 0x0;
    public const ushort DisallowStartRestriction = 0x1;
    public const ushort DisallowEndRestriction = 0x2;
    public const ushort DisallowSimpleRestriction = 0x4;
    public const ushort DisallowClosure = 0x8;
    public const ushort DisallowShortcut = 0x10;

    // ===================== constants (dynamiccost.cc, file-local) =====================

    public const double MinCustomFactor = double.Epsilon;

    // track penalty/factor bounds
    public const float MaxTrackPenalty = 300.0f;
    public const float MinTrackFactor = 0.8f;
    public const float MaxTrackFactor = 4.0f;

    // living-street penalty/factor bounds
    public const float MaxLivingStreetPenalty = 500.0f;
    public const float MinLivingStreetFactor = 0.8f;
    public const float MaxLivingStreetFactor = 3.0f;

    // lit factor bounds
    public const float MinLitFactor = 1.0f;

    public const float MinFactor = 0.1f;
    public const float MaxFactor = 100000.0f;

    // base transition cost defaults (seconds)
    public const float DefaultDestinationOnlyPenalty = 600.0f;
    public const float DefaultManeuverPenalty = 5.0f;
    public const float DefaultAlleyPenalty = 5.0f;
    public const float DefaultGateCost = 30.0f;
    public const float DefaultGatePenalty = 300.0f;
    public const float DefaultPrivateAccessPenalty = 450.0f;
    public const float DefaultTollBoothCost = 15.0f;
    public const float DefaultTollBoothPenalty = 0.0f;
    public const float DefaultFerryCost = 300.0f;
    public const float DefaultRailFerryCost = 300.0f;
    public const float DefaultCountryCrossingCost = 600.0f;
    public const float DefaultCountryCrossingPenalty = 0.0f;
    public const float DefaultServicePenalty = 15.0f;

    // other option defaults
    public const float DefaultUseFerry = 0.5f;
    public const float DefaultUseRailFerry = 0.4f;
    public const float DefaultUseTracks = 0.5f;
    public const float DefaultUseLivingStreets = 0.1f;
    public const float DefaultUseLit = 0.0f;
    public const float DefaultServiceFactor = 1.0f;
    public const float DefaultClosureFactor = 9.0f;
    public const float DefaultSpeedPenaltyFactor = 0.05f;

    // dimension defaults (meters / metric tons)
    public const float DefaultHeight = 1.6f;
    public const float DefaultWidth = 1.9f;
    public const float DefaultLength = 2.7f;
    public const float DefaultWeight = 0.8f;

    /// <summary>fixed_speed clamp range: {0, kDisableFixedSpeed, kMaxSpeedKph}.</summary>
    public static readonly RangedDefault<uint> FixedSpeedRange =
        new RangedDefault<uint>(0, GraphConstants.DisableFixedSpeed, GraphConstants.MaxSpeedKph);

    /// <summary>kNoCost: the zero cost.</summary>
    public static readonly Cost NoCost = new Cost(0.0f, 0.0f);

    // ===================== speed/density factor tables =====================

    /// <summary>kSpeedFactor (253 entries). Faithful port of populate_speedfactor().</summary>
    public static readonly float[] SpeedFactor = PopulateSpeedFactor();

    /// <summary>kDensityFactor (16 entries). Faithful port of populate_densityfactor().</summary>
    public static readonly float[] DensityFactor = PopulateDensityFactor();

    /// <summary>kTransDensityFactor (16 entries).</summary>
    public static readonly float[] TransDensityFactor =
    {
        1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.1f, 1.2f, 1.3f,
        1.4f, 1.6f, 1.9f, 2.2f, 2.5f, 2.8f, 3.1f, 3.5f,
    };

    private static float[] PopulateSpeedFactor()
    {
        var speedfactor = new float[253];
        speedfactor[0] = Constants.SecPerHour; // TODO - what to make speed=0?
        for (uint s = 1; s <= GraphConstants.MaxSpeedKph; s++)
            speedfactor[s] = (Constants.SecPerHour * 0.001f) / s;
        return speedfactor;
    }

    private static float[] PopulateDensityFactor()
    {
        var densityfactor = new float[16];
        for (uint d = 0; d < 16; d++)
            densityfactor[d] = 0.85f + (d * 0.025f);
        return densityfactor;
    }

    // ===================== protected state (dynamiccost.h) =====================

    protected uint Pass_;
    protected bool AllowTransitConnections_;
    protected bool AllowDestinationOnly_;
    protected bool AllowConditionalDestination_;
    protected bool ProjectOnBssConnection_;
    protected TravelMode TravelMode_;
    protected uint AccessMask_;
    protected List<HierarchyLimits> HierarchyLimits_ = new();
    protected Dictionary<GraphId, float> UserExcludeEdges_ = new();

    // ferry / road-type weighting factors
    protected float FerryFactor_;
    protected float RailFerryFactor_;
    protected float TrackFactor_;
    protected float LivingStreetFactor_;
    protected float ServiceFactor_;
    protected float ClosureFactor_;
    protected float UnlitFactor_;
    protected float SpeedPenaltyFactor_;

    // transition costs
    protected Cost CountryCrossingCost_;
    protected Cost GateCost_;
    protected Cost PrivateAccessCost_;
    protected Cost TollBoothCost_;
    protected Cost FerryTransitionCost_;
    protected Cost BikeShareCost_;
    protected Cost RailFerryTransitionCost_;

    // penalties
    protected float ManeuverPenalty_;
    protected float AlleyPenalty_;
    protected float DestinationOnlyPenalty_;
    protected float LivingStreetPenalty_;
    protected float TrackPenalty_;
    protected float ServicePenalty_;

    // vehicle dimensions
    protected float Height_;
    protected float Width_;
    protected float Length_;
    protected float Weight_;

    protected byte FlowMask_;
    protected byte RestrictionProbability_;
    protected bool Shortest_;

    protected bool IgnoreRestrictions_;
    protected bool IgnoreNonVehicularRestrictions_;
    protected bool IgnoreTurnRestrictions_;
    protected bool IgnoreOneways_;
    protected bool IgnoreAccess_;
    protected bool IgnoreClosures_;
    protected bool IgnoreConstruction_;
    protected uint TopSpeed_;
    protected uint FixedSpeed_;
    protected bool FilterClosures_ = true;

    protected bool PenalizeUturns_;

    protected bool ExcludeUnpaved_;
    protected bool ExcludeBridges_;
    protected bool ExcludeTunnels_;
    protected bool ExcludeTolls_;
    protected bool ExcludeHighways_;
    protected bool ExcludeFerries_;
    protected bool HasExcludes_;
    protected bool DefaultHierarchyLimits = true;
    protected bool UseHierarchyLimits = true;

    protected bool ExcludeCashOnlyTolls_;

    protected bool IncludeHot_;
    protected bool IncludeHov2_;
    protected bool IncludeHov3_;

    protected bool IsHgv_;

    protected Dictionary<GraphId, CustomCost> LinearCostEdges_ = new();
    protected double MinLinearCostFactor_ = 1.0;

    // ===================== constructor =====================

    /// <summary>
    /// Constructor. Faithful port of <c>DynamicCost::DynamicCost</c>.
    /// </summary>
    /// <param name="costing">Request options.</param>
    /// <param name="mode">Travel mode.</param>
    /// <param name="accessMask">Access mask.</param>
    /// <param name="penalizeUturns">Should we penalize uturns?</param>
    protected DynamicCost(Costing costing, TravelMode mode, uint accessMask, bool penalizeUturns = false)
    {
        var options = costing.Options;
        Pass_ = 0;
        AllowTransitConnections_ = false;
        AllowDestinationOnly_ = true;
        AllowConditionalDestination_ = false;
        TravelMode_ = mode;
        AccessMask_ = accessMask;
        ClosureFactor_ = DefaultClosureFactor;
        SpeedPenaltyFactor_ = DefaultSpeedPenaltyFactor;
        FlowMask_ = GraphConstants.DefaultFlowMask;
        Shortest_ = options.Shortest;
        IgnoreRestrictions_ = options.IgnoreRestrictions;
        IgnoreNonVehicularRestrictions_ = options.IgnoreNonVehicularRestrictions;
        IgnoreTurnRestrictions_ = options.IgnoreRestrictions || options.IgnoreNonVehicularRestrictions;
        IgnoreOneways_ = options.IgnoreOneways;
        IgnoreAccess_ = options.IgnoreAccess;
        IgnoreClosures_ = options.IgnoreClosures;
        IgnoreConstruction_ = options.IgnoreConstruction;
        TopSpeed_ = (uint)options.TopSpeed;
        FixedSpeed_ = options.FixedSpeed;
        FilterClosures_ = IgnoreClosures_ ? false : costing.FilterClosures;
        PenalizeUturns_ = penalizeUturns;
        IsHgv_ = costing.CostingType == Costing.Type.Truck;
        MinLinearCostFactor_ = 1.0;

        // set user supplied hierarchy limits if present, fill the other required levels with sentinels
        foreach (var level in TileHierarchy.Levels())
        {
            if (!options.HierarchyLimits.TryGetValue(level.Level, out var res))
            {
                var hl = new HierarchyLimits();
                hl.SetExpandWithinDist(HierarchyLimitsFunctions.MaxDistance);
                hl.SetMaxUpTransitions(HierarchyLimitsFunctions.UnlimitedTransitions);
                hl.SetUpTransitionCount(0);
                HierarchyLimits_.Add(hl);
            }
            else
            {
                HierarchyLimits_.Add(res);
                HierarchyLimits_[^1].SetUpTransitionCount(0);
            }
        }

        // Add avoid edges to internal set
        foreach (var edge in options.ExcludeEdges)
            UserExcludeEdges_[new GraphId(edge.Id)] = edge.PercentAlong;

        // add linear feature factors
        foreach (var e in options.CostFactorEdges)
        {
            if (e.Factor == 0.0)
            {
                UserExcludeEdges_[new GraphId(e.Id)] = (float)e.Start;
                break;
            }

            if (!LinearCostEdges_.TryGetValue(new GraphId(e.Id), out var costEdge))
            {
                costEdge = new CustomCost();
                LinearCostEdges_[new GraphId(e.Id)] = costEdge;
            }

            costEdge.Ranges.Add(new CostEdge(e.Start, e.End, e.Factor));
        }

        // once all cost factors are filled, sort by range, precompute overall average
        foreach (var kvp in LinearCostEdges_)
            MinLinearCostFactor_ = Math.Min(MinLinearCostFactor_, kvp.Value.SortAndFindSmallest());
    }

    // ===================== pass / mode accessors =====================

    /// <summary>Does the costing method allow multiple passes (with relaxed hierarchy limits).</summary>
    public virtual bool AllowMultiPass() => false;

    /// <summary>Get the pass number.</summary>
    public uint Pass() => Pass_;

    /// <summary>Set the pass number.</summary>
    public void SetPass(uint pass) => Pass_ = pass;

    /// <summary>Returns the maximum transfer distance between stops for this mode (multimodal).</summary>
    public virtual uint GetMaxTransferDistanceMM() => 0;

    /// <summary>This method overrides the factor for this mode. Lower favors the mode more.</summary>
    public virtual float GetModeFactor() => 1.0f;

    /// <summary>Get the access mode used by this costing method.</summary>
    public virtual uint AccessMode() => AccessMask_;

    // ===================== access checks =====================

    /// <summary>
    /// Checks if access is allowed for the provided directed edge (forward path). Abstract.
    /// </summary>
    public abstract bool Allowed(
        DirectedEdge edge,
        bool isDest,
        EdgeLabel pred,
        GraphTilePtr tile,
        GraphId edgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask);

    /// <summary>
    /// Checks if access is allowed for an edge on the reverse path. Abstract.
    /// </summary>
    public abstract bool AllowedReverse(
        DirectedEdge edge,
        EdgeLabel pred,
        DirectedEdge oppEdge,
        GraphTilePtr tile,
        GraphId oppEdgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask);

    /// <summary>
    /// Checks if any edge exclusion is present (bridges, tolls, tunnels, ferries, highways).
    /// Faithful port of the templated <c>CheckExclusions&lt;FORWARD&gt;</c>.
    /// </summary>
    public bool CheckExclusions(DirectedEdge edge, EdgeLabel pred, bool forward)
    {
        bool IsDriveOnto(bool condition, bool predCondition) => forward == condition && predCondition != condition;

        return HasExcludes_ &&
               ((ExcludeBridges_ && IsDriveOnto(edge.Bridge, pred.Bridge())) ||
                (ExcludeTunnels_ && IsDriveOnto(edge.Tunnel, pred.Tunnel())) ||
                (ExcludeTolls_ && IsDriveOnto(edge.Toll, pred.Toll())) ||
                (ExcludeHighways_ &&
                 IsDriveOnto(edge.Classification == RoadClass.Motorway,
                             pred.Classification() == RoadClass.Motorway)) ||
                (ExcludeFerries_ &&
                 IsDriveOnto(edge.Use == Use.Ferry || edge.Use == Use.RailFerry,
                             pred.Use() == Use.Ferry || pred.Use() == Use.RailFerry)));
    }

    /// <summary>Checks if access is allowed for the provided node (bollards / cash-only tolls).</summary>
    public virtual bool Allowed(NodeInfo node)
        => ((node.Access & AccessMask_) != 0 || IgnoreAccess_) &&
           !(ExcludeCashOnlyTolls_ && node.CashOnlyToll);

    /// <summary>
    /// Conservative reachability/candidate-viability check. Faithful port of the disallow-mask
    /// <c>Allowed(edge, tile, disallow_mask)</c> overload.
    /// </summary>
    public virtual bool Allowed(DirectedEdge edge, GraphTilePtr tile, ushort disallowMask = DisallowNone)
    {
        uint accessMask = IgnoreAccess_ ? GraphConstants.AllAccess : AccessMask_;
        bool accessible = (edge.ForwardAccess & accessMask) != 0 ||
                          (IgnoreOneways_ && (edge.ReverseAccess & accessMask) != 0);
        bool assumedRestricted =
            ((disallowMask & DisallowStartRestriction) != 0 && edge.StartRestriction != 0) ||
            ((disallowMask & DisallowEndRestriction) != 0 && edge.EndRestriction != 0) ||
            ((disallowMask & DisallowSimpleRestriction) != 0 && edge.Restrictions != 0) ||
            ((disallowMask & DisallowShortcut) != 0 && edge.IsShortcut);
        return accessible && !assumedRestricted &&
               (edge.Use != Use.Construction || IgnoreConstruction_);
    }

    /// <summary>
    /// Index-carrying location-search overload used when the port must inspect per-edge traffic
    /// state. Costings that do not need the index retain the source-compatible overload above.
    /// </summary>
    public virtual bool Allowed(
        DirectedEdge edge,
        GraphTilePtr tile,
        uint directedEdgeIndex,
        ushort disallowMask = DisallowNone) =>
        Allowed(edge, tile, disallowMask);

    /// <summary>Checks if access is allowed for the provided edge (mode-based forward access).</summary>
    public virtual bool IsAccessible(DirectedEdge edge)
        => (edge.ForwardAccess & AccessMask_) != 0 ||
           (IgnoreAccess_ && (edge.ForwardAccess & GraphConstants.AllAccess) != 0) ||
           (IgnoreOneways_ && (edge.ReverseAccess & AccessMask_) != 0) ||
           (IgnoreConstruction_ && edge.Use == Use.Construction);

    /// <summary>Additional mode-specific access-restriction check. Defaults to true.</summary>
    public virtual bool ModeSpecificAllowed(AccessRestriction restriction) => true;

    // ===================== restriction helpers =====================

    /// <summary>
    /// Test if an edge should be restricted due to a date time access restriction. Faithful port
    /// of <c>IsConditionalActive</c>.
    /// </summary>
    /// <remarks>
    /// Converts the UTC epoch instant through the stable Valhalla timezone index and evaluates the
    /// packed <see cref="TimeDomain"/> against local calendar and wall-clock fields.
    /// </remarks>
    public static bool IsConditionalActive(
        ulong restriction,
        ulong currentTime,
        uint tzIndex) =>
        ConditionalTimeDomainEvaluator.IsActive(
            restriction,
            currentTime,
            tzIndex);

    // PORT-NOTE: EvaluateRestrictions / GetExemptedAccessRestrictions live on DynamicCost in C++ but
    // were not part of the already-shipped foundation slice. To avoid colliding with the parallel
    // AutoCost slice (which reproduces EvaluateRestrictions as its own protected helper), each
    // derived coster that needs it reproduces it locally (see TruckCost). When the foundation slice
    // is revised these should be hoisted here and the per-coster copies removed.

    /// <summary>
    /// Test if an edge should be restricted due to a complex restriction. Faithful port of the
    /// templated <c>DynamicCost::Restricted</c> (the thor A* algorithms call this through the base
    /// <c>cost_ptr_t</c>). Returns true if there is a complex restriction onto this edge that matches
    /// the mode and the predecessor list for the current path.
    /// </summary>
    /// <remarks>
    /// Timed restrictions use the serialized restriction domain and the graph node's timezone.
    /// The edge-status reset preserves the upstream fix for permanently labeled via edges.
    /// </remarks>
    /// <param name="edge">Directed edge being expanded onto.</param>
    /// <param name="pred">Predecessor edge label.</param>
    /// <param name="edgeLabels">The edge-label list (to walk predecessors along the path).</param>
    /// <param name="tile">Graph tile (to read the restriction).</param>
    /// <param name="edgeid">Edge id for the directed edge.</param>
    /// <param name="forward">Forward search (true) or reverse search (false).</param>
    /// <param name="edgestatus">Edge status map (to reset permanently-labeled via edges); may be null.</param>
    /// <param name="currentTime">Current time (seconds since epoch); 0 means not time-dependent.</param>
    /// <param name="tzIndex">Timezone index for the node.</param>
    public bool Restricted(
        DirectedEdge edge,
        EdgeLabel pred,
        IReadOnlyList<EdgeLabel> edgeLabels,
        GraphTilePtr tile,
        GraphId edgeid,
        bool forward,
        ThorEdgeStatusMap? edgestatus = null,
        ulong currentTime = 0,
        uint tzIndex = 0)
    {
        if (IgnoreTurnRestrictions_)
        {
            return false;
        }

        // Lambda to get the next predecessor EdgeLabel (that is not a transition).
        EdgeLabel NextPredecessor(EdgeLabel label)
            => label.Predecessor() == GraphConstants.InvalidLabel ? label : edgeLabels[(int)label.Predecessor()];

        // A complex restriction spans multiple edges, e.g. from A to C via B. At the point of
        // triggering, all edges leading up to C are already kPermanent; reset all but the last so
        // that new paths involving them remain visible. Faithful port of reset_edge_status.
        void ResetEdgeStatus(List<GraphId> edgeIdsInComplexRestriction)
        {
            if (edgestatus is null)
            {
                return;
            }

            // Nothing to do if the restriction has no vias.
            if (edgeIdsInComplexRestriction.Count == 0)
            {
                return;
            }

            // Reset all but the last edge (no point possibly expanding from A a second time).
            for (int i = 0; i < edgeIdsInComplexRestriction.Count - 1; i++)
            {
                edgestatus.Update(edgeIdsInComplexRestriction[i], ThorEdgeSet.UnreachedOrReset);
            }
        }

        // If forward, check if the edge marks the end of a restriction, else the start.
        if ((forward && (edge.EndRestriction & AccessMask_) != 0) ||
            (!forward && (edge.StartRestriction & AccessMask_) != 0))
        {
            // Get complex restrictions. Return false if no restrictions are found.
            ComplexRestrictionView restrictions = tile.GetComplexRestrictions(forward, edgeid, AccessMask_);
            if (restrictions.Empty())
            {
                return false;
            }

            // Iterate through the restrictions.
            EdgeLabel firstPred = pred;
            ComplexRestrictionView.Enumerator it = restrictions.GetEnumerator();
            while (it.MoveNext())
            {
                ComplexRestriction cr = it.Current;
                if (cr.Type() == RestrictionType.NoProbable || cr.Type() == RestrictionType.OnlyProbable)
                {
                    // A complex restriction can not have a 0 probability set; range is 1 to 100.
                    // restriction_probability_ == 0 means ignore probable restrictions.
                    if (RestrictionProbability_ == 0 || RestrictionProbability_ > cr.Probability())
                    {
                        continue;
                    }
                }

                // Walk the via list, move to the next restriction if the via edge ids do not match
                // the path for this restriction.
                bool match = true;
                EdgeLabel nextPred = firstPred;
                var edgeIdsInComplexRestriction = new List<GraphId>();

                cr.WalkVias(it.CurrentVias(), via =>
                {
                    if (via.Value != nextPred.Edgeid().Value)
                    {
                        // Pred diverged from restriction, exit early.
                        match = false;
                        return WalkingVia.StopWalking;
                    }

                    edgeIdsInComplexRestriction.Add(nextPred.Edgeid());
                    nextPred = NextPredecessor(nextPred);
                    return WalkingVia.KeepWalking;
                });

                // Don't forget the last one.
                edgeIdsInComplexRestriction.Add(nextPred.Edgeid());

                // Check against the start/end of the complex restriction.
                if (match && ((forward && nextPred.Edgeid() == cr.FromGraphId()) ||
                              (!forward && nextPred.Edgeid() == cr.ToGraphId())))
                {
                    if (currentTime != 0 && cr.HasDt())
                    {
                        // Evaluate the exact packed date-time fields in the graph node's timezone.
                        if (IsConditionalActive(
                                cr.ToTimeDomain(),
                                currentTime,
                                tzIndex))
                        {
                            ResetEdgeStatus(edgeIdsInComplexRestriction);
                            return true;
                        }

                        continue;
                    }
                    else if (currentTime == 0 && cr.HasDt())
                    {
                        return false;
                    }
                    else
                    {
                        // Non-timed restriction: it exists all the time.
                        ResetEdgeStatus(edgeIdsInComplexRestriction);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets an edge's restrictions that have an "except_destination" flag set. Returns an 8-bit mask
    /// containing a flag for each access restriction type that can be ignored by destination-only
    /// traffic. Faithful port of <c>DynamicCost::GetExemptedAccessRestrictions</c>.
    /// </summary>
    /// <param name="edge">The directed edge.</param>
    /// <param name="tile">The edge's tile.</param>
    /// <param name="edgeid">The directed edge id.</param>
    /// <returns>The exempted-access-restriction mask (0 if none).</returns>
    public byte GetExemptedAccessRestrictions(DirectedEdge edge, GraphTilePtr tile, GraphId edgeid)
    {
        byte destonlyAccessRestrMask = 0;
        if (IgnoreRestrictions_ || (edge.AccessRestriction & AccessMask_) == 0 || AllowDestinationOnly_)
        {
            return 0;
        }

        foreach (AccessRestriction restr in tile.GetAccessRestrictions(edgeid.Id(), AccessMask_))
        {
            if (restr.ExceptDestination())
            {
                destonlyAccessRestrMask |= GraphConstants.AccessRestrictionMasks[(int)restr.Type()];
            }
        }

        return destonlyAccessRestrMask;
    }

    // ===================== edge cost (abstract / virtual) =====================

    /// <summary>Transit-departure edge cost. Abstract.</summary>
    public abstract Cost EdgeCost(DirectedEdge edge, TransitDeparture departure, uint currTime);

    /// <summary>Time-aware edge cost. Abstract.</summary>
    public abstract Cost EdgeCost(
        DirectedEdge edge,
        GraphId id,
        GraphTilePtr tile,
        TimeInfo timeInfo,
        ref byte flowSources);

    /// <summary>
    /// Convenience non-time-aware edge cost. Faithful port: calls the time-aware overload with
    /// <c>TimeInfo.Invalid()</c>.
    /// </summary>
    public virtual Cost EdgeCost(DirectedEdge edge, GraphId edgeid, GraphTilePtr tile)
    {
        byte flowSources = 0;
        return EdgeCost(edge, edgeid, tile, TimeInfo.Invalid(), ref flowSources);
    }

    // ===================== transition cost (virtual; default 0) =====================

    /// <summary>
    /// Returns the cost to make the transition from the predecessor edge (forward). Defaults to 0.
    /// </summary>
    public virtual Cost TransitionCost(
        DirectedEdge edge,
        NodeInfo node,
        EdgeLabel pred,
        GraphTilePtr tile,
        Func<LimitedGraphReader> readerGetter)
        => new Cost(0.0f, 0.0f);

    /// <summary>
    /// Returns the cost to make the transition from the predecessor edge (reverse). Defaults to 0.
    /// </summary>
    public virtual Cost TransitionCostReverse(
        uint idx,
        NodeInfo node,
        DirectedEdge oppEdge,
        DirectedEdge oppPredEdge,
        GraphTilePtr tile,
        GraphId predId,
        Func<LimitedGraphReader> readerGetter,
        bool hasMeasuredSpeed = false,
        InternalTurn internalTurn = InternalTurn.NoTurn)
        => new Cost(0.0f, 0.0f);

    // ===================== turn / uturn helpers =====================

    /// <summary>
    /// Returns the turn type from the predecessor edge. Faithful port of <c>TurnType</c>.
    /// </summary>
    public InternalTurn TurnType(uint idx, NodeInfo node, DirectedEdge edge, DirectedEdge? oppPredEdge = null)
    {
        if (!PenalizeUturns_ || !edge.Internal)
            return InternalTurn.NoTurn;

        Turn.Type turntype = oppPredEdge.HasValue ? oppPredEdge.Value.TurnType(idx) : edge.TurnType(idx);
        if (node.DriveOnRight)
        {
            // did we make a left onto a small internal edge?
            if (edge.Length <= CostConstants.ShortInternalLength &&
                (turntype == Turn.Type.SharpLeft || turntype == Turn.Type.Left))
                return InternalTurn.LeftTurn;
            // did we make a right onto a small internal edge?
        }
        else if (edge.Length <= CostConstants.ShortInternalLength &&
                 (turntype == Turn.Type.SharpRight || turntype == Turn.Type.Right))
        {
            return InternalTurn.RightTurn;
        }

        return InternalTurn.NoTurn;
    }

    /// <summary>
    /// Adds a penalty to 3 types of uturns. Faithful port of <c>AddUturnPenalty</c>.
    /// </summary>
    public void AddUturnPenalty(
        uint idx,
        NodeInfo node,
        DirectedEdge edge,
        bool hasReverse,
        bool hasLeft,
        bool hasRight,
        bool penalizeInternalUturns,
        InternalTurn internalTurn,
        ref float seconds)
    {
        if (node.DriveOnRight)
        {
            if (hasReverse && !edge.NameConsistencyAt(idx))
            {
                seconds += TCNameInconsistentUturn;
            }
            else if (hasReverse ||
                     (penalizeInternalUturns && internalTurn == InternalTurn.LeftTurn && hasLeft))
            {
                seconds += TCUnfavorableUturn;
            }
            else if (edge.TurnType(idx) == Turn.Type.SharpLeft && edge.EdgeToRight(idx) &&
                     !edge.EdgeToLeft(idx) && edge.Named && edge.NameConsistencyAt(idx))
            {
                seconds *= TCUnfavorablePencilPointUturn;
            }
        }
        else
        {
            if (hasReverse && !edge.NameConsistencyAt(idx))
            {
                seconds += TCNameInconsistentUturn;
            }
            else if (hasReverse ||
                     (penalizeInternalUturns && internalTurn == InternalTurn.RightTurn && hasRight))
            {
                seconds += TCUnfavorableUturn;
            }
            else if (edge.TurnType(idx) == Turn.Type.SharpRight && !edge.EdgeToRight(idx) &&
                     edge.EdgeToLeft(idx) && edge.Named && edge.NameConsistencyAt(idx))
            {
                seconds *= TCUnfavorablePencilPointUturn;
            }
        }
    }

    // ===================== OSRM car turn duration =====================

    private const double OsrmTurnPenalty = 7.5;
    private const double OsrmUTurnPenalty = 20;
    private const double OsrmTurnBias = 1.075;
    private const double OsrmTurnBiasInv = 1.0 / OsrmTurnBias;
    private const double OsrmTrafficLightPenalty = 2;

    private static readonly double[] OsrmLeftHandLookup = OsrmLookupTable(false);
    private static readonly double[] OsrmRightHandLookup = OsrmLookupTable(true);

    private static uint CalculateTurnDegree(DirectedEdge edge, NodeInfo node, uint idxPredOpp)
    {
        uint inHeading = node.Heading(idxPredOpp);
        inHeading = (inHeading + 180) % 360;
        uint outHeading = node.Heading(edge.LocalEdgeIdx);
        return Util.GetTurnDegree(inHeading, outHeading);
    }

    private static double[] OsrmLookupTable(bool right)
    {
        var turnDurations = new double[360];
        for (int angle = 0; angle < 360; ++angle)
        {
            int symmetric = angle > 180 ? angle - 360 : angle;
            if (symmetric >= 0)
            {
                turnDurations[angle] =
                    OsrmTurnPenalty / (1 + Math.Exp(-((13 * (right ? OsrmTurnBiasInv : OsrmTurnBias)) * symmetric / 180.0 -
                                                      6.5 * (right ? OsrmTurnBias : OsrmTurnBiasInv))));
            }
            else
            {
                turnDurations[angle] =
                    OsrmTurnPenalty / (1 + Math.Exp(-((13 * (right ? OsrmTurnBias : OsrmTurnBiasInv)) * -symmetric / 180.0 -
                                                      6.5 * (right ? OsrmTurnBiasInv : OsrmTurnBias))));
            }
        }

        return turnDurations;
    }

    /// <summary>
    /// Port of the OSRM car profile turn-duration calculation. Faithful port of
    /// <c>OSRMCarTurnDuration</c> from <c>osrm_car_duration.h</c>.
    /// </summary>
    public static float OSRMCarTurnDuration(DirectedEdge edge, NodeInfo node, uint idxPredOpp)
    {
        double turnDuration = node.TrafficSignal ? OsrmTrafficLightPenalty : 0;

        uint turnDegree = CalculateTurnDegree(edge, node, idxPredOpp);
        bool isUTurn = Turn.GetType(turnDegree) == Turn.Type.Reverse;

        uint numberOfRoads = node.LocalEdgeCount;
        if (numberOfRoads > 2 || isUTurn)
        {
            turnDuration += node.DriveOnRight ? OsrmRightHandLookup[turnDegree] : OsrmLeftHandLookup[turnDegree];
            turnDuration += isUTurn ? OsrmUTurnPenalty : 0;
        }

        return (float)turnDuration;
    }

    // ===================== transfer / A* heuristics / sorting =====================

    /// <summary>Returns the transfer cost between 2 transit stops. Defaults to 0.</summary>
    public virtual Cost TransferCost() => new Cost(0.0f, 0.0f);

    /// <summary>Returns the default transfer cost between 2 transit lines. Defaults to 0.</summary>
    public virtual Cost DefaultTransferCost() => new Cost(0.0f, 0.0f);

    /// <summary>Get the cost factor for A* heuristics. Abstract.</summary>
    public abstract float AStarCostFactor();

    /// <summary>Get the general unit size for sorting. Defaults to 1 (second).</summary>
    public virtual uint UnitSize() => DefaultUnitSize;

    // ===================== flags / mode setters =====================

    /// <summary>Sets the flag indicating whether destination only edges are allowed.</summary>
    public virtual void SetAllowDestinationOnly(bool allow) => AllowDestinationOnly_ = allow;

    /// <summary>Set to allow use of transit connections.</summary>
    public virtual void SetAllowTransitConnections(bool allow) => AllowTransitConnections_ = allow;

    /// <summary>Sets the flag indicating whether conditional=destination restriction edges are allowed.</summary>
    public void SetAllowConditionalDestination(bool allow) => AllowConditionalDestination_ = allow;

    /// <summary>Set the current travel mode.</summary>
    public void SetTravelMode(TravelMode mode) => TravelMode_ = mode;

    /// <summary>Get the current travel mode.</summary>
    public TravelMode TravelMode() => TravelMode_;

    /// <summary>Get the current travel type. Defaults to 0.</summary>
    public virtual byte TravelType() => 0;

    /// <summary>Is the current vehicle type HGV?</summary>
    public bool IsHgv() => IsHgv_;

    /// <summary>Get the wheelchair required flag. Defaults to false.</summary>
    public virtual bool Wheelchair() => false;

    /// <summary>Get the bicycle required flag. Defaults to false.</summary>
    public virtual bool Bicycle() => false;

    /// <summary>Gets the hierarchy limits.</summary>
    public List<HierarchyLimits> GetHierarchyLimits() => HierarchyLimits_;

    /// <summary>Sets the hierarchy limits.</summary>
    public void SetHierarchyLimits(List<HierarchyLimits> hierarchyLimits) => HierarchyLimits_ = hierarchyLimits;

    /// <summary>Relax hierarchy limits using pre-defined algorithm-based factors.</summary>
    public void RelaxHierarchyLimits(bool usingBidirectional)
    {
        float relaxFactor = usingBidirectional ? 8.0f : 16.0f;
        float expansionWithinFactor = usingBidirectional ? 2.0f : 4.0f;
        foreach (var hierarchy in HierarchyLimits_)
            HierarchyLimitsFunctions.RelaxHierarchyLimits(hierarchy, relaxFactor, expansionWithinFactor);
    }

    /// <summary>Checks if we should exclude or not (add tile to exclude list). Defaults to no-op.</summary>
    public virtual void AddToExcludeList(GraphTilePtr tile)
    {
    }

    /// <summary>Checks if an edge should be excluded. Defaults to false.</summary>
    public virtual bool IsExcluded(GraphTilePtr tile, DirectedEdge edge) => false;

    /// <summary>Checks if a node should be excluded. Defaults to false.</summary>
    public virtual bool IsExcluded(GraphTilePtr tile, NodeInfo node) => false;

    /// <summary>Adds a list of edges (GraphIds) to the user specified avoid list.</summary>
    public void AddUserAvoidEdges(IReadOnlyList<AvoidEdge> excludeEdges)
    {
        foreach (var edge in excludeEdges)
            UserExcludeEdges_[edge.Id] = (float)edge.PercentAlong;
    }

    /// <summary>Check if the edge is in the user-specified avoid list.</summary>
    public bool IsUserAvoidEdge(GraphId edgeid)
        => UserExcludeEdges_.Count != 0 && UserExcludeEdges_.ContainsKey(edgeid);

    /// <summary>Check if the edge is in the user-specified avoid list and should be avoided as an origin.</summary>
    public bool AvoidAsOriginEdge(GraphId edgeid, float percentAlong)
        => UserExcludeEdges_.TryGetValue(edgeid, out float along) && along >= percentAlong;

    /// <summary>Check if the edge is in the user-specified avoid list and should be avoided as a destination.</summary>
    public bool AvoidAsDestinationEdge(GraphId edgeid, float percentAlong)
        => UserExcludeEdges_.TryGetValue(edgeid, out float along) && along <= percentAlong;

    /// <summary>Get the flow mask used for accessing traffic flow data from the tile.</summary>
    public byte FlowMask() => FlowMask_;

    /// <summary>Returns the bike-share-station cost. Defaults to kNoCost.</summary>
    public virtual Cost BSSCost() => NoCost;

    /// <summary>Determine whether an edge is currently closed due to traffic.</summary>
    /// <remarks>
    /// PORT-NOTE: C++ <c>tile->IsClosed(edge)</c> derives the directed-edge index from pointer
    /// arithmetic against the tile's edge array. The ported <see cref="GraphTile"/> is index-based,
    /// so the directed-edge index <paramref name="deIndex"/> (which derived costers already hold via
    /// the edge's <see cref="GraphId"/>) is threaded through explicitly.
    /// </remarks>
    public virtual bool IsClosed(DirectedEdge edge, GraphTilePtr tile, uint deIndex)
        => !IgnoreClosures_ && (FlowMask_ & GraphConstants.CurrentFlowMask) != 0 && tile.IsClosed(deIndex);

    /// <summary>
    /// Computes the penalty applied when an edge's speed exceeds the requested top speed.
    /// Faithful port of <c>SpeedPenalty</c>.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: As with <see cref="IsClosed"/>, the index-based ported tile requires the
    /// directed-edge record and its index; <paramref name="de"/> / <paramref name="deIndex"/>
    /// replace the C++ edge pointer that the tile would otherwise index by pointer arithmetic.
    /// </remarks>
    public float SpeedPenalty(DirectedEdge de, uint deIndex, GraphTilePtr tile, TimeInfo timeInfo, byte flowSources, float edgeSpeed)
    {
        float averageEdgeSpeed = edgeSpeed;
        if (TopSpeed_ != GraphConstants.MaxAssumedSpeed && (flowSources & GraphConstants.CurrentFlowMask) != 0)
        {
            averageEdgeSpeed =
                tile.GetSpeed(de, deIndex, (byte)(FlowMask_ & ~GraphConstants.CurrentFlowMask), timeInfo.SecondOfWeek);
        }

        float speedPenalty = averageEdgeSpeed > TopSpeed_
            ? (averageEdgeSpeed - TopSpeed_) * SpeedPenaltyFactor_
            : 0.0f;

        return speedPenalty;
    }

    /// <summary>Whether default hierarchy limits are in effect.</summary>
    public bool GetDefaultHierarchyLimits() => DefaultHierarchyLimits;

    /// <summary>Set whether default hierarchy limits are in effect.</summary>
    public void SetDefaultHierarchyLimits(bool value) => DefaultHierarchyLimits = value;

    /// <summary>Whether hierarchy limits are used.</summary>
    public bool GetUseHierarchyLimits() => UseHierarchyLimits;

    /// <summary>
    /// Rough time estimation in seconds given a distance in meters, based on top speed (default)
    /// or fixed speed (if set). Faithful port of <c>BeeLineTimeEstimate</c>.
    /// </summary>
    public uint BeeLineTimeEstimate(double distanceMeters, double factor)
    {
        return FixedSpeed_ == GraphConstants.DisableFixedSpeed
            ? (uint)((distanceMeters / (TopSpeed_ * Constants.KphToMetersPerSec)) * factor)
            : (uint)(distanceMeters / (FixedSpeed_ * Constants.KphToMetersPerSec) * factor * 0.85);
    }

    /// <summary>Set whether to project locations onto bike-share-station connection edges.</summary>
    public void SetProjectOnBssConnection(bool projectOnBssConnection) => ProjectOnBssConnection_ = projectOnBssConnection;

    // ===================== protected edge-factor helpers =====================

    /// <summary>
    /// Returns the averaged factor for an edge fraction based on user provided custom factors.
    /// Faithful port of <c>PartialEdgeFactor</c>.
    /// </summary>
    protected double PartialEdgeFactor(GraphId edgeid, float start, float end)
    {
        if (LinearCostEdges_.Count == 0 || start == end)
            return 1.0;

        if (LinearCostEdges_.TryGetValue(edgeid, out var custom))
        {
            double partialFactor = 0.0;
            double uncovered = 1.0;
            foreach (var range in custom.Ranges)
            {
                if (range.End <= start || range.Start >= end)
                    continue;

                double fraction = (range.End - Math.Max((double)start, range.Start)) / (end - start);
                partialFactor += fraction * range.Factor;
                uncovered -= fraction;
            }

            partialFactor += uncovered;
            return partialFactor;
        }

        return 1.0;
    }

    /// <summary>
    /// Returns a factor to be applied to edge cost based on user provided input. Faithful port of
    /// <c>EdgeFactor</c>.
    /// </summary>
    protected double EdgeFactor(GraphId edgeid)
    {
        if (LinearCostEdges_.Count == 0 || edgeid.Value == GraphId.InvalidGraphId)
            return 1.0;

        return LinearCostEdges_.TryGetValue(edgeid, out var custom) ? custom.AvgFactor : 1.0;
    }

    /// <summary>
    /// Partial edge cost (time-aware). Faithful port of the time-aware <c>PartialEdgeCost</c>.
    /// </summary>
    public Cost PartialEdgeCost(
        DirectedEdge edge,
        GraphId edgeid,
        GraphTilePtr tile,
        TimeInfo timeInfo,
        ref byte flowSources,
        float start,
        float end)
    {
        return EdgeCost(edge, edgeid, tile, timeInfo, ref flowSources) *
               Math.Max(end - start, float.Epsilon) *
               (float)PartialEdgeFactor(edgeid, start, end);
    }

    /// <summary>
    /// Partial edge cost (non-time-aware). Faithful port of the non-time-aware <c>PartialEdgeCost</c>.
    /// </summary>
    public Cost PartialEdgeCost(DirectedEdge edge, GraphId edgeid, GraphTilePtr tile, float start, float end)
    {
        return EdgeCost(edge, edgeid, tile) *
               Math.Max(end - start, float.Epsilon) *
               (float)PartialEdgeFactor(edgeid, start, end);
    }

    // ===================== use-* preference handling =====================

    /// <summary>
    /// Calculate <c>track</c> costs based on tracks preference. Faithful port of <c>set_use_tracks</c>.
    /// </summary>
    protected virtual void SetUseTracks(float useTracks)
    {
        TrackPenalty_ = useTracks < 0.5f ? (MaxTrackPenalty * (1.0f - 2.0f * useTracks)) : 0.0f;
        TrackFactor_ = useTracks < 0.5f
            ? (MaxTrackFactor - 2.0f * useTracks * (MaxTrackFactor - 1.0f))
            : (MinTrackFactor + 2.0f * (1.0f - useTracks) * (1.0f - MinTrackFactor));
    }

    /// <summary>
    /// Calculate <c>living_street</c> costs based on living streets preference. Faithful port of
    /// <c>set_use_living_streets</c>.
    /// </summary>
    protected virtual void SetUseLivingStreets(float useLivingStreets)
    {
        LivingStreetPenalty_ =
            useLivingStreets < 0.5f ? (MaxLivingStreetPenalty * (1.0f - 2.0f * useLivingStreets)) : 0;

        LivingStreetFactor_ = useLivingStreets < 0.5f
            ? (MaxLivingStreetFactor - 2.0f * useLivingStreets * (MaxLivingStreetFactor - 1.0f))
            : (MinLivingStreetFactor + 2.0f * (1.0f - useLivingStreets) * (1.0f - MinLivingStreetFactor));
    }

    /// <summary>
    /// Calculate <c>lit</c> costs based on lit preference. Faithful port of <c>set_use_lit</c>.
    /// </summary>
    protected virtual void SetUseLit(float useLit)
    {
        UnlitFactor_ =
            useLit < 0.5f ? MinLitFactor + 2.0f * useLit : ((MinLitFactor - 5.0f) + 12.0f * useLit);
    }

    // ===================== get_base_costs =====================

    /// <summary>
    /// Get the base transition costs (and ferry factor) from the costing options. Faithful port of
    /// <c>get_base_costs</c>.
    /// </summary>
    protected void GetBaseCosts(Costing costing)
    {
        var costingOptions = costing.Options;

        // Cost only (no time) penalties
        AlleyPenalty_ = costingOptions.AlleyPenalty;
        DestinationOnlyPenalty_ = costingOptions.DestinationOnlyPenalty;
        ManeuverPenalty_ = costingOptions.ManeuverPenalty;

        RestrictionProbability_ = (byte)costingOptions.RestrictionProbability;

        // Transition costs (both time and cost)
        TollBoothCost_ = new Cost(costingOptions.TollBoothCost + costingOptions.TollBoothPenalty,
                                  costingOptions.TollBoothCost);
        CountryCrossingCost_ = new Cost(costingOptions.CountryCrossingCost + costingOptions.CountryCrossingPenalty,
                                        costingOptions.CountryCrossingCost);
        GateCost_ = new Cost(costingOptions.GateCost + costingOptions.GatePenalty, costingOptions.GateCost);
        PrivateAccessCost_ = new Cost(costingOptions.GateCost + costingOptions.PrivateAccessPenalty,
                                      costingOptions.GateCost);
        BikeShareCost_ = new Cost(costingOptions.BikeShareCost + costingOptions.BikeSharePenalty,
                                  costingOptions.BikeShareCost);

        // ferry: modify ferry edge weighting based on use_ferry factor.
        float ferryPenalty;
        float useFerry = costingOptions.UseFerry;
        if (useFerry < 0.5f)
        {
            ferryPenalty = (uint)(MaxFerryPenalty * (1.0f - useFerry * 2.0f));
            FerryFactor_ = 10.0f - useFerry * 18.0f;
        }
        else
        {
            ferryPenalty = 0.0f;
            FerryFactor_ = 1.5f - useFerry;
        }

        FerryTransitionCost_ = new Cost(costingOptions.FerryCost + ferryPenalty, costingOptions.FerryCost);

        // rail ferry
        float railFerryPenalty;
        float useRailFerry = costingOptions.UseRailFerry;
        if (useRailFerry < 0.5f)
        {
            railFerryPenalty = (uint)(MaxFerryPenalty * (1.0f - useRailFerry * 2.0f));
            RailFerryFactor_ = 10.0f - useRailFerry * 18.0f;
        }
        else
        {
            railFerryPenalty = 0.0f;
            RailFerryFactor_ = 1.5f - useRailFerry;
        }

        RailFerryTransitionCost_ = new Cost(costingOptions.RailFerryCost + railFerryPenalty,
                                            costingOptions.RailFerryCost);

        // track / living-street / lit factors
        SetUseTracks(costingOptions.UseTracks);
        SetUseLivingStreets(costingOptions.UseLivingStreets);
        SetUseLit(costingOptions.UseLit);

        // service roads
        ServicePenalty_ = costingOptions.ServicePenalty;
        ServiceFactor_ = costingOptions.ServiceFactor;
        ClosureFactor_ = costingOptions.ClosureFactor;
        SpeedPenaltyFactor_ = costingOptions.SpeedPenaltyFactor;

        // flow / speed
        FlowMask_ = (byte)costingOptions.FlowMask;
        FixedSpeed_ = costingOptions.FixedSpeed;
        TopSpeed_ = FixedSpeed_ == GraphConstants.DisableFixedSpeed ? (uint)costingOptions.TopSpeed : FixedSpeed_;

        // exclusions
        ExcludeUnpaved_ = costingOptions.ExcludeUnpaved;
        ExcludeBridges_ = costingOptions.ExcludeBridges;
        ExcludeTunnels_ = costingOptions.ExcludeTunnels;
        ExcludeTolls_ = costingOptions.ExcludeTolls;
        ExcludeHighways_ = costingOptions.ExcludeHighways;
        ExcludeFerries_ = costingOptions.ExcludeFerries;
        HasExcludes_ = ExcludeBridges_ || ExcludeTunnels_ || ExcludeTolls_ || ExcludeHighways_ || ExcludeFerries_;
        ExcludeCashOnlyTolls_ = costingOptions.ExcludeCashOnlyTolls;
        DefaultHierarchyLimits = costingOptions.HierarchyLimitsSize == 0;
    }

    // ===================== base_transition_cost =====================

    /// <summary>
    /// Base transition cost that all costing methods use. Includes costs for country crossing,
    /// boarding a ferry, toll booth, gates, entering destination only, alleys, and maneuver
    /// penalties. Faithful port of the templated <c>base_transition_cost</c> (specialized for
    /// <see cref="EdgeLabel"/> as the predecessor).
    /// </summary>
    protected Cost BaseTransitionCost(NodeInfo node, DirectedEdge edge, EdgeLabel pred, uint idx)
        => BaseTransitionCostImpl(node, edge, pred.Use(), pred.Toll(), pred.Destonly(), idx);

    /// <summary>
    /// Base transition cost overload taking a <see cref="DirectedEdge"/> predecessor (the
    /// unidirectional algorithms call <c>base_transition_cost</c> with a DirectedEdge*).
    /// </summary>
    protected Cost BaseTransitionCost(NodeInfo node, DirectedEdge edge, DirectedEdge pred, uint idx)
        => BaseTransitionCostImpl(node, edge, pred.Use, pred.Toll,
            IsHgv() ? pred.DestOnlyHgv : pred.DestOnly, idx);

    private Cost BaseTransitionCostImpl(NodeInfo node, DirectedEdge edge, Use predUse, bool predToll, bool predDestonly, uint idx)
    {
        var c = new Cost();

        c += CountryCrossingCost_ * (node.Type == NodeType.BorderControl ? 1.0f : 0.0f);
        c += GateCost_ * ((node.Type == NodeType.Gate ? 1.0f : 0.0f) * (!node.TaggedAccess ? 1.0f : 0.0f));
        c += PrivateAccessCost_ *
             ((node.Type == NodeType.Gate || node.Type == NodeType.Bollard ? 1.0f : 0.0f) *
              (node.PrivateAccess ? 1.0f : 0.0f));
        c += BikeShareCost_ * (node.Type == NodeType.BikeShare ? 1.0f : 0.0f);
        c += TollBoothCost_ *
             (node.Type == NodeType.TollBooth || (edge.Toll && !predToll) ? 1.0f : 0.0f);
        c += FerryTransitionCost_ *
             (edge.Use == Use.Ferry && predUse != Use.Ferry ? 1.0f : 0.0f);
        c += RailFerryTransitionCost_ *
             (edge.Use == Use.RailFerry && predUse != Use.RailFerry ? 1.0f : 0.0f);

        // Additional penalties without any time cost
        bool isDestonly = (IsHgv() && edge.DestOnlyHgv) || (!IsHgv() && edge.DestOnly);
        c.CostValue += DestinationOnlyPenalty_ * (isDestonly && !predDestonly ? 1.0f : 0.0f);
        c.CostValue += AlleyPenalty_ * (edge.Use == Use.Alley && predUse != Use.Alley ? 1.0f : 0.0f);
        c.CostValue += ManeuverPenalty_ * (!edge.Link && !edge.NameConsistencyAt(idx) ? 1.0f : 0.0f);
        c.CostValue += LivingStreetPenalty_ *
                       (edge.Use == Use.LivingStreet && predUse != Use.LivingStreet ? 1.0f : 0.0f);
        c.CostValue += TrackPenalty_ * (edge.Use == Use.Track && predUse != Use.Track ? 1.0f : 0.0f);

        c.CostValue += ServicePenalty_ *
                       (edge.Use == Use.ServiceRoad && predUse != Use.ServiceRoad ? 1.0f : 0.0f) *
                       (!edge.Internal ? 1.0f : 0.0f);

        // shortest ignores any penalties in favor of path length
        c.CostValue *= Shortest_ ? 0.0f : 1.0f;
        return c;
    }
}
