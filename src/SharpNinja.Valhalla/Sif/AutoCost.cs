// Faithful C# port of Valhalla sif autocost (valhalla @ 3.7.0).
// Source: src/sif/autocost.cc (+ the matching declarations folded in from autocost.h).
//
// Provides dynamic edge costing for "direct" auto routes (AutoCost), plus the two derived costers
// that live in the same translation unit: BusCost and TaxiCost. Algorithms, constants and option
// ranges are preserved EXACTLY. Public members are PascalCase per project convention; the protected
// AutoCost state fields that the INLINE_TEST exposes keep their C++ trailing-underscore names so the
// ported test reads like the gtest.
//
// PORT-NOTE: the foundation (DynamicCost, Cost, EdgeLabel, CostingOptions, CostFactory) is already
// ported under this namespace; this slice does NOT re-port it and matches its exact signatures.
//
// PORT-NOTE: a few base helpers the C++ AutoCost calls live on DynamicCost in C++ but were not part
// of the already-shipped foundation slice (EvaluateRestrictions; the index-based GetSpeed wrapper
// the EdgeCost methods use). They are reproduced here as protected helpers on AutoCost so the three
// costers in this file can call them. EvaluateRestrictions is a faithful port of the inline
// DynamicCost::EvaluateRestrictions; it delegates conditional-restriction timing to the foundation's
// DynamicCost.IsConditionalActive (which throws until the DateTime/tz slice is ported, matching the
// foundation's explicit-missing-dependency stance).
//
// PORT-NOTE: the ported GraphTile is index-based (C++ derives the directed-edge index from pointer
// arithmetic). The C++ tile->GetSpeed(edge,...) / tile->IsClosed(edge) / SpeedPenalty(edge,...) are
// reproduced by threading the directed-edge index, derived from the edge's GraphId, explicitly.

using System;
using System.Collections.Generic;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

// PORT-NOTE: matches the foundation's alias: graph_tile_ptr -> the concrete (nullable) GraphTile.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;

namespace SharpNinja.Valhalla.Sif;

/// <summary>
/// Default options/values for auto costing. Faithful port of the file-local anonymous namespace in
/// <c>autocost.cc</c> (the <c>constexpr</c> defaults, turn-cost tables and the
/// <c>ranged_default_t</c> option ranges).
/// </summary>
public static class AutoCostConstants
{
    // Base transition costs
    /// <summary>kDefaultServicePenalty (seconds).</summary>
    public const float DefaultServicePenalty = 75.0f;

    // Other options
    /// <summary>Default preference of using a motorway or trunk 0-1.</summary>
    public const float DefaultUseHighways = 0.5f;

    /// <summary>Default preference of using toll roads 0-1.</summary>
    public const float DefaultUseTolls = 0.5f;

    /// <summary>Default preference of using tracks 0-1.</summary>
    public const float DefaultUseTracks = 0.0f;

    /// <summary>Default preference of using distance vs time 0-1.</summary>
    public const float DefaultUseDistance = 0.0f;

    /// <summary>
    /// Default percentage of allowing probable restrictions; 0% means do not include them.
    /// </summary>
    public const uint DefaultRestrictionProbability = 100;

    // Default turn costs
    public const float TCStraight = 0.5f;
    public const float TCSlight = 0.75f;
    public const float TCFavorable = 1.0f;
    public const float TCFavorableSharp = 1.5f;
    public const float TCCrossing = 2.0f;
    public const float TCUnfavorable = 2.5f;
    public const float TCUnfavorableSharp = 3.5f;
    public const float TCReverse = 9.5f;
    public const float TCRamp = 1.5f;
    public const float TCRoundabout = 0.5f;

    /// <summary>How much to favor taxi roads.</summary>
    public const float TaxiFactor = 0.85f;

    /// <summary>Do not avoid alleys by default.</summary>
    public const float DefaultAlleyFactor = 1.0f;

    /// <summary>How much to favor turn channels.</summary>
    public const float TurnChannelFactor = 0.6f;

    /// <summary>Turn costs based on side of street driving (right-hand drive).</summary>
    public static readonly float[] RightSideTurnCosts =
    {
        TCStraight, TCSlight, TCFavorable,
        TCFavorableSharp, TCReverse, TCUnfavorableSharp,
        TCUnfavorable, TCSlight,
    };

    /// <summary>Turn costs based on side of street driving (left-hand drive).</summary>
    public static readonly float[] LeftSideTurnCosts =
    {
        TCStraight, TCSlight, TCUnfavorable,
        TCUnfavorableSharp, TCReverse, TCFavorableSharp,
        TCFavorable, TCSlight,
    };

    public const float MinFactor = 0.1f;
    public const float MaxFactor = 100000.0f;

    // Valid ranges and defaults
    public static readonly RangedDefault<float> AlleyFactorRange =
        new RangedDefault<float>(MinFactor, DefaultAlleyFactor, MaxFactor);

    public static readonly RangedDefault<float> UseHighwaysRange =
        new RangedDefault<float>(0f, DefaultUseHighways, 1.0f);

    public static readonly RangedDefault<float> UseTollsRange =
        new RangedDefault<float>(0f, DefaultUseTolls, 1.0f);

    public static readonly RangedDefault<float> UseDistanceRange =
        new RangedDefault<float>(0f, DefaultUseDistance, 1.0f);

    public static readonly RangedDefault<uint> ProbabilityRange =
        new RangedDefault<uint>(0, DefaultRestrictionProbability, 100);

    public static readonly RangedDefault<uint> VehicleSpeedRange =
        new RangedDefault<uint>(10, GraphConstants.MaxAssumedSpeed, GraphConstants.MaxSpeedKph);

    // TruckMate custom costing (FR-OSMNAV-022 / TR-OSMNAV-LEFTTURN-033): unprotected-left avoidance.
    // The hard "avoid unprotected left turns" rule applies to auto/taxi/bus exactly as it does to
    // truck costing, so the same nominal surface-street reference speed and detour-threshold range are
    // mirrored here from TruckCostConstants (Valhalla keeps file-local constants per cost file).
    public const float UnprotectedLeftReferenceSpeedMps = 13.4f; // ~30 mph

    public static readonly RangedDefault<float> UnprotectedLeftAvoidanceRange =
        new RangedDefault<float>(0f, 0f, 1000000.0f);

    /// <summary>kHighwayFactor: per road-class highway weighting (8 entries).</summary>
    public static readonly float[] HighwayFactor =
    {
        1.0f, // Motorway
        0.5f, // Trunk
        0.0f, // Primary
        0.0f, // Secondary
        0.0f, // Tertiary
        0.0f, // Unclassified
        0.0f, // Residential
        0.0f, // Service, other
    };

    /// <summary>kSurfaceFactor: per surface-type weighting (7 entries).</summary>
    public static readonly float[] SurfaceFactor =
    {
        0.0f, // kPavedSmooth
        0.0f, // kPaved
        0.0f, // kPaveRough
        0.1f, // kCompacted
        0.2f, // kDirt
        0.5f, // kGravel
        1.0f, // kPath
    };

    /// <summary>
    /// kInvMedianSpeed: the basic costing for an edge trades time vs distance. To make a linear
    /// combination meaningful, distance (meters) is converted into time units by the reciprocal of a
    /// constant speed (1/16, about 37mph).
    /// </summary>
    public const float InvMedianSpeed = 1f / 16f;

    /// <summary>
    /// kBaseCostOptsConfig: the auto base-costing-options config (GetBaseCostOptsConfig overrides
    /// service_penalty and use_tracks defaults). Faithful port of the file-local const.
    /// </summary>
    public static BaseCostingOptionsConfig GetBaseCostOptsConfig()
    {
        var cfg = new BaseCostingOptionsConfig();
        // override defaults
        cfg.ServicePenalty = new RangedDefault<float>(0f, DefaultServicePenalty, DynamicCost.MaxPenalty);
        cfg.UseTracks = new RangedDefault<float>(0f, DefaultUseTracks, 1f);
        return cfg;
    }

    /// <summary>kBaseCostOptsConfig: cached auto base-costing-options config.</summary>
    public static readonly BaseCostingOptionsConfig BaseCostOptsConfig = GetBaseCostOptsConfig();
}

/// <summary>
/// Derived class providing dynamic edge costing for "direct" auto routes. This is a route that is
/// generally shortest time but uses route hierarchies that can result in slightly longer routes that
/// avoid shortcuts on residential roads. Faithful port of <c>valhalla::sif::AutoCost</c>.
/// </summary>
public class AutoCost : DynamicCost
{
    // Hidden in source file so we don't need it to be protected.
    // We expose it within the source file for testing purposes (matches the C++ `public:` block).
    public VehicleType Type_;            // Vehicle type: car (default), motorcycle, etc
    public float HighwayFactor_;         // Factor applied when road is a motorway or trunk
    public float AlleyFactor_;           // Avoid alleys factor.
    public float TollFactor_;            // Factor applied when road has a toll
    public float SurfaceFactor_;         // How much the surface factors are applied.
    public float DistanceFactor_;        // How much distance factors in overall favorability
    public float InvDistanceFactor_;     // How much time factors in overall favorability

    // Vehicle attributes (used for special restrictions and costing)
    public new float Height_; // Vehicle height in meters
    public new float Width_;  // Vehicle width in meters
    public new float Length_; // Vehicle length in meters
    public new float Weight_; // Vehicle weight in metric tons

    // TruckMate custom costing (FR-OSMNAV-022): unprotected-left detour threshold (meters); 0 disables
    // the rule. Honored by AutoCost and its derived TaxiCost/BusCost, matching TruckCost.
    public float UnprotectedLeftAvoidanceMeters_;

    /// <summary>
    /// Construct auto costing. Faithful port of <c>AutoCost::AutoCost</c>.
    /// </summary>
    /// <param name="costing">Request costing options.</param>
    /// <param name="accessMask">Access mask (defaults to auto | HOV access).</param>
    public AutoCost(Costing costing, uint accessMask = (uint)(GraphConstants.AutoAccess | GraphConstants.HovAccess))
        : base(costing, global::SharpNinja.Valhalla.Sif.TravelMode.Drive, accessMask, true)
    {
        var costingOptions = costing.Options;

        // Get the vehicle type - enter as string and convert to enum.
        // Used to set the surface factor - penalize some roads based on surface type.
        SurfaceFactor_ = 0.5f;
        Type_ = VehicleType.Car;

        // Get the base transition costs
        GetBaseCosts(costing);

        // Get alley factor from costing options.
        AlleyFactor_ = costingOptions.AlleyFactor;

        // Preference to use highways. Is a value from 0 to 1
        // Factor for highway use - use a non-linear factor with values at 0.5 being neutral (factor
        // of 0). Values between 0.5 and 1 slowly decrease to a maximum of -0.125 (to slightly prefer
        // highways) while values between 0.5 to 0 slowly increase to a maximum of kMaxHighwayBiasFactor
        // to avoid/penalize highways.
        float useHighways = costingOptions.UseHighways;
        if (useHighways >= 0.5f)
        {
            float f = 0.5f - useHighways;
            HighwayFactor_ = f * f * f;
        }
        else
        {
            float f = 1.0f - (useHighways * 2.0f);
            HighwayFactor_ = MaxHighwayBiasFactor * (f * f);
        }

        // Preference for distance vs time
        DistanceFactor_ = costingOptions.UseDistance * AutoCostConstants.InvMedianSpeed;
        InvDistanceFactor_ = 1f - costingOptions.UseDistance;

        // Preference to use toll roads (separate from toll booth penalty). Sets a toll
        // factor. A toll factor of 0 would indicate no adjustment to weighting for toll roads.
        // use_tolls = 1 would reduce weighting slightly (a negative delta) while
        // use_tolls = 0 would penalize (positive delta to weighting factor).
        float useTolls = costingOptions.UseTolls;
        TollFactor_ = useTolls < 0.5f
            ? (4.0f - 8 * useTolls)       // ranges from 4 to 0
            : (0.5f - useTolls) * 0.03f;  // ranges from 0 to -0.015

        IncludeHot_ = costingOptions.IncludeHot;
        IncludeHov2_ = costingOptions.IncludeHov2;
        IncludeHov3_ = costingOptions.IncludeHov3;

        // Get the vehicle attributes
        Height_ = costingOptions.Height;
        Width_ = costingOptions.Width;
        Length_ = costingOptions.Length;
        Weight_ = costingOptions.Weight;

        // TruckMate custom costing: the unprotected-left avoidance rule applies to auto/taxi as well
        // as truck. Read the configured detour threshold (0 = disabled).
        UnprotectedLeftAvoidanceMeters_ = costingOptions.UnprotectedLeftAvoidanceMeters;
    }

    /// <summary>
    /// Does the costing method allow multiple passes (with relaxed hierarchy limits).
    /// </summary>
    public override bool AllowMultiPass() => true;

    /// <summary>HOV-allowance check. Faithful port of <c>IsHOVAllowed</c>.</summary>
    public bool IsHovAllowed(DirectedEdge edge)
    {
        // A non-hov edge means hov is allowed.
        if (!edge.IsHovOnly())
            return true;

        // The edge is either HOV-2 or HOV-3 from this point forward.

        // If include_hov3 is set we can route onto both HOV-2 and HOV-3 edges
        if (IncludeHov3_)
            return true;

        // If include_hov2 is set we can route onto HOV-2 edges.
        if (IncludeHov2_ && edge.HovType == HovEdgeType.Hov2)
            return true;

        // If include_hot is set we can route onto HOT edges (HOV and tolled).
        if (IncludeHot_ && edge.Toll)
            return true;

        return false;
    }

    /// <summary>
    /// Checks if access is allowed for the provided directed edge (forward path). Faithful port of
    /// <c>AutoCost::Allowed</c>.
    /// </summary>
    public override bool Allowed(
        DirectedEdge edge,
        bool isDest,
        EdgeLabel pred,
        GraphTilePtr tile,
        GraphId edgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask)
    {
        // Check access, U-turn, and simple turn restriction.
        // Allow U-turns at dead-end nodes in case the origin is inside
        // a not thru region and a heading selected an edge entering the
        // region.
        if (!IsAccessible(edge) || (!pred.Deadend() && pred.OppLocalIdx() == edge.LocalEdgeIdx) ||
            ((pred.Restrictions() & (1u << (int)edge.LocalEdgeIdx)) != 0 && !IgnoreTurnRestrictions_) ||
            edge.Surface == Surface.Impassable || IsUserAvoidEdge(edgeid) ||
            (!AllowDestinationOnly_ && !pred.Destonly() && edge.DestOnly) ||
            (pred.ClosurePruning() && IsClosed(edge, tile, edgeid.Id())) ||
            (ExcludeUnpaved_ && !pred.Unpaved() && edge.Unpaved) || !IsHovAllowed(edge) ||
            CheckExclusions(edge, pred, true))
        {
            return false;
        }

        return EvaluateRestrictions(AccessMask_, edge, isDest, tile, edgeid, currentTime,
                                    tzIndex, ref restrictionIdx, ref destonlyAccessRestrMask);
    }

    /// <summary>
    /// Checks if access is allowed for an edge on the reverse path (from destination towards origin).
    /// Both opposing edges are provided. Faithful port of <c>AutoCost::AllowedReverse</c>.
    /// </summary>
    public override bool AllowedReverse(
        DirectedEdge edge,
        EdgeLabel pred,
        DirectedEdge oppEdge,
        GraphTilePtr tile,
        GraphId oppEdgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask)
    {
        // Check access, U-turn, and simple turn restriction.
        // Allow U-turns at dead-end nodes.
        if (!IsAccessible(oppEdge) || (!pred.Deadend() && pred.OppLocalIdx() == edge.LocalEdgeIdx) ||
            ((oppEdge.Restrictions & (1u << (int)pred.OppLocalIdx())) != 0 && !IgnoreTurnRestrictions_) ||
            oppEdge.Surface == Surface.Impassable || IsUserAvoidEdge(oppEdgeid) ||
            (!AllowDestinationOnly_ && !pred.Destonly() && oppEdge.DestOnly) ||
            (pred.ClosurePruning() && IsClosed(oppEdge, tile, oppEdgeid.Id())) ||
            (ExcludeUnpaved_ && !pred.Unpaved() && oppEdge.Unpaved) || !IsHovAllowed(oppEdge) ||
            CheckExclusions(oppEdge, pred, false))
        {
            return false;
        }

        return EvaluateRestrictions(AccessMask_, oppEdge, false, tile, oppEdgeid,
                                    currentTime, tzIndex, ref restrictionIdx,
                                    ref destonlyAccessRestrMask);
    }

    /// <summary>Callback for Allowed doing mode-specific restriction checks. Faithful port.</summary>
    public override bool ModeSpecificAllowed(AccessRestriction restriction)
    {
        switch (restriction.Type())
        {
            case AccessType.MaxHeight:
                return Height_ <= (float)(restriction.Value() * 0.01);
            case AccessType.MaxWidth:
                return Width_ <= (float)(restriction.Value() * 0.01);
            case AccessType.MaxLength:
                return Length_ <= (float)(restriction.Value() * 0.01);
            case AccessType.MaxWeight:
                return Weight_ <= (float)(restriction.Value() * 0.01);
            default:
                return true;
        }
    }

    /// <summary>
    /// Only transit costings are valid for this method call, hence we throw. Faithful port.
    /// </summary>
    public override Cost EdgeCost(DirectedEdge edge, TransitDeparture departure, uint currTime)
        => throw new InvalidOperationException("AutoCost::EdgeCost does not support transit edges");

    /// <summary>
    /// Get the cost to traverse the specified directed edge in seconds. Faithful port of
    /// <c>AutoCost::EdgeCost</c>.
    /// </summary>
    public override Cost EdgeCost(
        DirectedEdge edge,
        GraphId edgeid,
        GraphTilePtr tile,
        TimeInfo timeInfo,
        ref byte flowSources)
    {
        // either the computed edge speed or optional top_speed
        uint edgeSpeed;
        if (FixedSpeed_ == GraphConstants.DisableFixedSpeed)
        {
            edgeSpeed = tile.GetSpeed(edge, edgeid.Id(), FlowMask_, timeInfo.SecondOfWeek, false,
                                      out flowSources, timeInfo.SecondsFromNow);
        }
        else
        {
            edgeSpeed = FixedSpeed_;
        }

        uint finalSpeed = Math.Min(edgeSpeed, TopSpeed_);

        float sec = edge.Length * SpeedFactor[finalSpeed];

        if (Shortest_)
        {
            return new Cost(edge.Length, sec);
        }

        // base factor is either ferry, rail ferry or density based
        float factor;
        switch (edge.Use)
        {
            case Use.Ferry:
                factor = FerryFactor_;
                break;
            case Use.RailFerry:
                factor = RailFerryFactor_;
                break;
            default:
                factor = DensityFactor[edge.Density];
                break;
        }

        factor += HighwayFactor_ * AutoCostConstants.HighwayFactor[(uint)edge.Classification] +
                  SurfaceFactor_ * AutoCostConstants.SurfaceFactor[(uint)edge.Surface] +
                  SpeedPenalty(edge, edgeid.Id(), tile, timeInfo, flowSources, edgeSpeed) +
                  (edge.Toll ? 1 : 0) * TollFactor_;

        switch (edge.Use)
        {
            case Use.Alley:
                factor *= AlleyFactor_;
                break;
            case Use.Track:
                factor *= TrackFactor_;
                break;
            case Use.LivingStreet:
                factor *= LivingStreetFactor_;
                break;
            case Use.ServiceRoad:
                factor *= ServiceFactor_;
                break;
            case Use.TurnChannel:
                if ((flowSources & GraphConstants.DefaultFlowMask) != 0)
                {
                    // boost only historic & live speeds
                    factor *= AutoCostConstants.TurnChannelFactor;
                }

                break;
            default:
                break;
        }

        factor *= (float)EdgeFactor(edgeid);

        if (IsClosed(edge, tile, edgeid.Id()))
        {
            // Add a penalty for traversing a closed edge
            factor *= ClosureFactor_;
        }

        // base cost before the factor is a linear combination of time vs distance, depending on which
        // one the user thinks is more important to them
        return new Cost((sec * InvDistanceFactor_ + edge.Length * DistanceFactor_) * factor, sec);
    }

    /// <summary>
    /// Returns the time (in seconds) to make the transition from the predecessor. Faithful port of
    /// <c>AutoCost::TransitionCost</c>.
    /// </summary>
    public override Cost TransitionCost(
        DirectedEdge edge,
        NodeInfo node,
        EdgeLabel pred,
        GraphTilePtr tile,
        Func<LimitedGraphReader> readerGetter)
    {
        // Get the transition cost for country crossing, ferry, gate, toll booth,
        // destination only, alley, maneuver penalty
        uint idx = pred.OppLocalIdx();
        Cost c = BaseTransitionCost(node, edge, pred, idx);
        c.Secs += OSRMCarTurnDuration(edge, node, pred.OppLocalIdx());

        uint stopimpact = edge.StopImpact(idx);
        Turn.Type turntype = edge.TurnType(idx);
        // Transition time = turncost * stopimpact * densityfactor
        if (stopimpact > 0 && !Shortest_)
        {
            float turnCost;
            if (edge.EdgeToRight(idx) && edge.EdgeToLeft(idx))
            {
                turnCost = AutoCostConstants.TCCrossing;
            }
            else
            {
                turnCost = node.DriveOnRight
                    ? AutoCostConstants.RightSideTurnCosts[(uint)turntype]
                    : AutoCostConstants.LeftSideTurnCosts[(uint)turntype];
            }

            if ((edge.Use != Use.Ramp && pred.Use() == Use.Ramp) ||
                (edge.Use == Use.Ramp && pred.Use() != Use.Ramp))
            {
                turnCost += AutoCostConstants.TCRamp;
                if (edge.Roundabout)
                    turnCost += AutoCostConstants.TCRoundabout;
            }

            float seconds = turnCost;

            bool hasLeft = turntype == Turn.Type.Left || turntype == Turn.Type.SharpLeft;
            bool hasRight = turntype == Turn.Type.Right || turntype == Turn.Type.SharpRight;
            bool hasReverse = turntype == Turn.Type.Reverse;

            bool isTurn = hasLeft || hasRight || hasReverse;
            // Separate time and penalty when traffic is present. With traffic, edge speeds account for
            // much of the intersection transition time (TODO - evaluate different elapsed time settings).
            // Still want to add a penalty so routes avoid high cost intersections.
            if (isTurn)
            {
                seconds *= stopimpact;
            }

            AddUturnPenalty(idx, node, edge, hasReverse, hasLeft, hasRight, true, pred.InternalTurn(),
                            ref seconds);

            // Apply density factor and stop impact penalty if there isn't traffic on this edge or you're
            // not using traffic
            if (!pred.HasMeasuredSpeed())
            {
                if (!isTurn)
                    seconds *= stopimpact;
                seconds *= TransDensityFactor[node.Density];
            }

            c.CostValue += seconds;
        }

        // Account for the user preferring distance
        c.CostValue *= InvDistanceFactor_;

        // TruckMate (TR-OSMNAV-LEFTTURN-033): avoid unprotected left turns for auto/taxi/bus, applying
        // the same conservative rule as TruckCost. A left is protected only at a signalized
        // intersection; an untagged/uncertain conflicting approach is treated as unprotected (never
        // wrongly permits an unprotected left). Added as a flat finite penalty AFTER the distance
        // scaling so it stays a consistent time-equivalent deterrent (sized to the configured detour
        // distance) regardless of use_distance.
        if (UnprotectedLeftAvoidanceMeters_ > 0.0f)
        {
            Turn.Type truckmateTurn = edge.TurnType(idx);
            if ((truckmateTurn == Turn.Type.Left || truckmateTurn == Turn.Type.SharpLeft) &&
                !node.TrafficSignal)
            {
                c.CostValue += UnprotectedLeftAvoidanceMeters_ / AutoCostConstants.UnprotectedLeftReferenceSpeedMps;
            }
        }

        return c;
    }

    /// <summary>
    /// Returns the cost to make the transition from the predecessor edge when using a reverse search
    /// (from destination towards the origin). Faithful port of <c>AutoCost::TransitionCostReverse</c>.
    /// </summary>
    /// <remarks>
    /// pred is the opposing current edge in the reverse tree; edge is the opposing predecessor.
    /// </remarks>
    public override Cost TransitionCostReverse(
        uint idx,
        NodeInfo node,
        DirectedEdge pred,
        DirectedEdge edge,
        GraphTilePtr tile,
        GraphId predId,
        Func<LimitedGraphReader> readerGetter,
        bool hasMeasuredSpeed = false,
        InternalTurn internalTurn = InternalTurn.NoTurn)
    {
        // Get the transition cost for country crossing, ferry, gate, toll booth,
        // destination only, alley, maneuver penalty
        Cost c = BaseTransitionCost(node, edge, pred, idx);
        c.Secs += OSRMCarTurnDuration(edge, node, pred.OppLocalIdx);

        uint stopimpact = edge.StopImpact(idx);
        Turn.Type turntype = edge.TurnType(idx);
        // Transition time = turncost * stopimpact * densityfactor
        if (stopimpact > 0 && !Shortest_)
        {
            float turnCost;
            if (edge.EdgeToRight(idx) && edge.EdgeToLeft(idx))
            {
                turnCost = AutoCostConstants.TCCrossing;
            }
            else
            {
                turnCost = node.DriveOnRight
                    ? AutoCostConstants.RightSideTurnCosts[(uint)turntype]
                    : AutoCostConstants.LeftSideTurnCosts[(uint)turntype];
            }

            if ((edge.Use != Use.Ramp && pred.Use == Use.Ramp) ||
                (edge.Use == Use.Ramp && pred.Use != Use.Ramp))
            {
                turnCost += AutoCostConstants.TCRamp;
                if (edge.Roundabout)
                    turnCost += AutoCostConstants.TCRoundabout;
            }

            float seconds = turnCost;

            bool hasLeft = turntype == Turn.Type.Left || turntype == Turn.Type.SharpLeft;
            bool hasRight = turntype == Turn.Type.Right || turntype == Turn.Type.SharpRight;
            bool hasReverse = turntype == Turn.Type.Reverse;

            bool isTurn = hasLeft || hasRight || hasReverse;
            // Separate time and penalty when traffic is present. With traffic, edge speeds account for
            // much of the intersection transition time (TODO - evaluate different elapsed time settings).
            // Still want to add a penalty so routes avoid high cost intersections.
            if (isTurn)
            {
                seconds *= stopimpact;
            }

            AddUturnPenalty(idx, node, edge, hasReverse, hasLeft, hasRight, true, internalTurn, ref seconds);

            // Apply density factor and stop impact penalty if there isn't traffic on this edge or you're
            // not using traffic
            if (!hasMeasuredSpeed)
            {
                if (!isTurn)
                    seconds *= stopimpact;
                seconds *= TransDensityFactor[node.Density];
            }

            c.CostValue += seconds;
        }

        // Account for the user preferring distance
        c.CostValue *= InvDistanceFactor_;

        return c;
    }

    /// <summary>
    /// Get the cost factor for A* heuristics. Faithful port of <c>AStarCostFactor</c>.
    /// </summary>
    public override float AStarCostFactor()
        => SpeedFactor[TopSpeed_] * (float)MinLinearCostFactor_;

    /// <summary>Get the current travel type. Faithful port of <c>travel_type</c>.</summary>
    public override byte TravelType() => (byte)Type_;

    /// <summary>
    /// Function used in location searching to exclude/allow ranking results by edge attribution and
    /// suitability for use as a location by the travel mode. Faithful port of the disallow-mask
    /// <c>AutoCost::Allowed(edge, tile, disallow_mask)</c> overload.
    /// </summary>
    public override bool Allowed(DirectedEdge edge, GraphTilePtr tile, ushort disallowMask = DisallowNone)
    {
        bool allowClosures = (!FilterClosures_ && (disallowMask & DisallowClosure) == 0) ||
                             (FlowMask_ & GraphConstants.CurrentFlowMask) == 0;
        return base.Allowed(edge, tile, disallowMask) && !edge.BssConnection &&
               (allowClosures || !IsClosedForDisallow(edge, tile)) && IsHovAllowed(edge);
    }

    /// <summary>
    /// PORT-NOTE: the disallow-mask Allowed overload calls C++ <c>tile->IsClosed(edge)</c> directly
    /// (not the closure-aware DynamicCost::IsClosed). In C++ <c>GraphTile::IsClosed(const
    /// DirectedEdge*)</c> recovers the directed-edge index via pointer arithmetic
    /// (<c>edge - directededges_</c>) and reads <c>traffic_tile.trafficspeed(idx).closed()</c>; when
    /// no traffic tile is loaded the <c>trafficspeed</c> is the invalid sentinel and <c>closed()</c>
    /// is false. The ported tile is index-based and this overload receives a value-type
    /// <see cref="DirectedEdge"/> with no GraphId, so the index cannot be recovered by pointer math.
    /// When the tile carries no valid traffic tile (the only configuration this embedded engine runs
    /// in) the result is unconditionally "not closed", which is exactly what the C++ produces - so we
    /// return false faithfully. If a valid traffic tile is ever present, the index genuinely cannot be
    /// recovered here and we throw to make that missing input explicit (loki ranking, which supplies
    /// the edge id, is a later port slice; the index-carrying TruckCost overload is the template).
    /// </summary>
    private static bool IsClosedForDisallow(DirectedEdge edge, GraphTilePtr tile)
    {
        // No live-traffic tile => trafficspeed sentinel => closed() == false (faithful to C++).
        if (!tile.GetTrafficTile().IsValid)
        {
            return false;
        }

        throw new NotImplementedException(
            "AutoCost.Allowed(edge, tile, disallowMask) needs the directed-edge index for " +
            "tile.IsClosed when a live-traffic tile is present; supplied by the not-yet-ported loki " +
            "ranking slice.");
    }

    // ===================== EvaluateRestrictions (base helper reproduced here) =====================

    /// <summary>
    /// Evaluate access restrictions (time-based, destination-only exemptions, mode-specific) for an
    /// edge. Faithful port of the inline <c>DynamicCost::EvaluateRestrictions</c>.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: lives on DynamicCost in C++; reproduced here as a protected helper because the
    /// already-shipped foundation slice did not include it. Conditional-restriction timing delegates
    /// to <see cref="DynamicCost.IsConditionalActive"/> (throws until the DateTime/tz slice ports).
    /// </remarks>
    protected bool EvaluateRestrictions(
        uint accessMode,
        DirectedEdge edge,
        bool isDest,
        GraphTilePtr tile,
        GraphId edgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask)
    {
        if (IgnoreRestrictions_ || (edge.AccessRestriction & accessMode) == 0)
            return true;

        var restrictions = new List<AccessRestriction>(tile.GetAccessRestrictions(edgeid.Id(), accessMode));

        bool timeAllowed = false;

        byte tmpMask = 0;
        for (int i = 0; i < restrictions.Count; ++i)
        {
            AccessRestriction restriction = restrictions[i];

            // Compare the time to the time-based restrictions
            AccessType accessType = restriction.Type();
            if (!IgnoreNonVehicularRestrictions_ &&
                (accessType == AccessType.TimedAllowed ||
                 accessType == AccessType.TimedDenied ||
                 accessType == AccessType.DestinationAllowed))
            {
                // TODO: if (i > baldr::kInvalidRestriction) LOG_ERROR("restriction index overflow");
                restrictionIdx = (byte)i;

                if (accessType == AccessType.TimedAllowed)
                    timeAllowed = true;

                if (currentTime == 0)
                {
                    // No time supplied so ignore time-based restrictions
                    // (but mark the edge (`has_time_restrictions`)
                    continue;
                }
                else
                {
                    // is in range?
                    if (IsConditionalActive(restriction.Value(), currentTime, tzIndex))
                    {
                        // If edge really is restricted at this time, we can exit early.
                        // If not, we should keep looking

                        // We are in range at the time we are allowed at this edge
                        if (accessType == AccessType.TimedAllowed)
                        {
                            destonlyAccessRestrMask = tmpMask;
                            return true;
                        }
                        else if (accessType == AccessType.DestinationAllowed)
                        {
                            return AllowConditionalDestination_ || isDest;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
            }

            if (restriction.ExceptDestination() &&
                (int)restriction.Type() < GraphConstants.AccessRestrictionMasks.Length)
            {
                byte mask = GraphConstants.AccessRestrictionMasks[(int)restriction.Type()];
                tmpMask |= mask;
                if ((destonlyAccessRestrMask & mask) != 0 || AllowDestinationOnly_)
                    continue;
            }

            // In case there are additional restriction checks for a particular mode,
            // check them now
            if (!ModeSpecificAllowed(restriction))
            {
                return false;
            }
        }

        destonlyAccessRestrMask = tmpMask;

        // if we have time allowed restrictions then these restrictions are
        // the only time we can route here.  Meaning all other time is restricted.
        // We looped over all the time allowed restrictions and we were never in range.
        return !timeAllowed || (currentTime == 0);
    }
}

/// <summary>
/// Derived class providing bus costing for driving. Faithful port of <c>valhalla::sif::BusCost</c>.
/// </summary>
public class BusCost : AutoCost
{
    /// <summary>Construct bus costing. Faithful port of <c>BusCost::BusCost</c>.</summary>
    public BusCost(Costing costing)
        : base(costing, GraphConstants.BusAccess)
    {
        Type_ = VehicleType.Bus;
    }

    /// <summary>
    /// Checks if access is allowed for the provided directed edge (forward path). Faithful port of
    /// <c>BusCost::Allowed</c>.
    /// </summary>
    public override bool Allowed(
        DirectedEdge edge,
        bool isDest,
        EdgeLabel pred,
        GraphTilePtr tile,
        GraphId edgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask)
    {
        // Check access, U-turn, and simple turn restriction.
        // Allow U-turns at dead-end nodes.
        if (!IsAccessible(edge) || (!pred.Deadend() && pred.OppLocalIdx() == edge.LocalEdgeIdx) ||
            ((pred.Restrictions() & (1u << (int)edge.LocalEdgeIdx)) != 0 && !IgnoreRestrictions_) ||
            edge.Surface == Surface.Impassable || IsUserAvoidEdge(edgeid) ||
            (!AllowDestinationOnly_ && !pred.Destonly() && edge.DestOnly) ||
            (pred.ClosurePruning() && IsClosed(edge, tile, edgeid.Id())) ||
            (ExcludeUnpaved_ && !pred.Unpaved() && edge.Unpaved) || !IsHovAllowed(edge) ||
            CheckExclusions(edge, pred, true))
        {
            return false;
        }

        return EvaluateRestrictions(AccessMask_, edge, isDest, tile, edgeid, currentTime,
                                    tzIndex, ref restrictionIdx, ref destonlyAccessRestrMask);
    }

    /// <summary>
    /// Checks if access is allowed for an edge on the reverse path. Faithful port of
    /// <c>BusCost::AllowedReverse</c>.
    /// </summary>
    public override bool AllowedReverse(
        DirectedEdge edge,
        EdgeLabel pred,
        DirectedEdge oppEdge,
        GraphTilePtr tile,
        GraphId oppEdgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask)
    {
        // Check access, U-turn, and simple turn restriction.
        // Allow U-turns at dead-end nodes.
        if (!IsAccessible(oppEdge) || (!pred.Deadend() && pred.OppLocalIdx() == edge.LocalEdgeIdx) ||
            ((oppEdge.Restrictions & (1u << (int)pred.OppLocalIdx())) != 0 && !IgnoreTurnRestrictions_) ||
            oppEdge.Surface == Surface.Impassable || IsUserAvoidEdge(oppEdgeid) ||
            (!AllowDestinationOnly_ && !pred.Destonly() && oppEdge.DestOnly) ||
            (pred.ClosurePruning() && IsClosed(oppEdge, tile, oppEdgeid.Id())) ||
            (ExcludeUnpaved_ && !pred.Unpaved() && oppEdge.Unpaved) || !IsHovAllowed(oppEdge) ||
            CheckExclusions(oppEdge, pred, false))
        {
            return false;
        }

        return EvaluateRestrictions(AccessMask_, oppEdge, false, tile, oppEdgeid,
                                    currentTime, tzIndex, ref restrictionIdx,
                                    ref destonlyAccessRestrMask);
    }
}

/// <summary>
/// Derived class providing an alternate costing for driving that is intended to favor Taxi roads.
/// Faithful port of <c>valhalla::sif::TaxiCost</c>.
/// </summary>
public class TaxiCost : AutoCost
{
    /// <summary>Construct taxi costing. Faithful port of <c>TaxiCost::TaxiCost</c>.</summary>
    public TaxiCost(Costing costing)
        : base(costing, GraphConstants.TaxiAccess)
    {
    }

    /// <summary>
    /// Checks if access is allowed for the provided directed edge (forward path). Faithful port of
    /// <c>TaxiCost::Allowed</c>.
    /// </summary>
    public override bool Allowed(
        DirectedEdge edge,
        bool isDest,
        EdgeLabel pred,
        GraphTilePtr tile,
        GraphId edgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask)
    {
        // Check access, U-turn, and simple turn restriction.
        // Allow U-turns at dead-end nodes in case the origin is inside
        // a not thru region and a heading selected an edge entering the
        // region.
        if (!IsAccessible(edge) || (!pred.Deadend() && pred.OppLocalIdx() == edge.LocalEdgeIdx) ||
            ((pred.Restrictions() & (1u << (int)edge.LocalEdgeIdx)) != 0 && !IgnoreRestrictions_) ||
            edge.Surface == Surface.Impassable || IsUserAvoidEdge(edgeid) ||
            (!AllowDestinationOnly_ && !pred.Destonly() && edge.DestOnly) ||
            (pred.ClosurePruning() && IsClosed(edge, tile, edgeid.Id())) ||
            (ExcludeUnpaved_ && !pred.Unpaved() && edge.Unpaved) || !IsHovAllowed(edge) ||
            CheckExclusions(edge, pred, true))
        {
            return false;
        }

        return EvaluateRestrictions(AccessMask_, edge, isDest, tile, edgeid, currentTime,
                                    tzIndex, ref restrictionIdx, ref destonlyAccessRestrMask);
    }

    /// <summary>
    /// Checks if access is allowed for an edge on the reverse path. Faithful port of
    /// <c>TaxiCost::AllowedReverse</c>.
    /// </summary>
    public override bool AllowedReverse(
        DirectedEdge edge,
        EdgeLabel pred,
        DirectedEdge oppEdge,
        GraphTilePtr tile,
        GraphId oppEdgeid,
        ulong currentTime,
        uint tzIndex,
        ref byte restrictionIdx,
        ref byte destonlyAccessRestrMask)
    {
        // Check access, U-turn, and simple turn restriction.
        // Allow U-turns at dead-end nodes.
        if (!IsAccessible(oppEdge) || (!pred.Deadend() && pred.OppLocalIdx() == edge.LocalEdgeIdx) ||
            ((oppEdge.Restrictions & (1u << (int)pred.OppLocalIdx())) != 0 && !IgnoreTurnRestrictions_) ||
            oppEdge.Surface == Surface.Impassable || IsUserAvoidEdge(oppEdgeid) ||
            (!AllowDestinationOnly_ && !pred.Destonly() && oppEdge.DestOnly) ||
            (pred.ClosurePruning() && IsClosed(oppEdge, tile, oppEdgeid.Id())) ||
            (ExcludeUnpaved_ && !pred.Unpaved() && oppEdge.Unpaved) || !IsHovAllowed(oppEdge) ||
            CheckExclusions(oppEdge, pred, false))
        {
            return false;
        }

        return EvaluateRestrictions(AccessMask_, oppEdge, false, tile, oppEdgeid,
                                    currentTime, tzIndex, ref restrictionIdx,
                                    ref destonlyAccessRestrMask);
    }

    /// <summary>
    /// Returns the cost to traverse the edge and an estimate of the actual time (in seconds) to
    /// traverse the edge. Faithful port of <c>TaxiCost::EdgeCost</c>.
    /// </summary>
    public override Cost EdgeCost(
        DirectedEdge edge,
        GraphId edgeid,
        GraphTilePtr tile,
        TimeInfo timeInfo,
        ref byte flowSources)
    {
        uint edgeSpeed;
        if (FixedSpeed_ == GraphConstants.DisableFixedSpeed)
        {
            edgeSpeed = tile.GetSpeed(edge, edgeid.Id(), FlowMask_, timeInfo.SecondOfWeek, false,
                                      out flowSources, timeInfo.SecondsFromNow);
        }
        else
        {
            edgeSpeed = FixedSpeed_;
        }

        uint finalSpeed = Math.Min(edgeSpeed, TopSpeed_);

        float sec = edge.Length * SpeedFactor[finalSpeed];

        if (Shortest_)
        {
            return new Cost(edge.Length, sec);
        }

        float factor = edge.Use == Use.Ferry ? FerryFactor_ : DensityFactor[edge.Density];
        factor += SpeedPenalty(edge, edgeid.Id(), tile, timeInfo, flowSources, edgeSpeed);
        if ((edge.ForwardAccess & GraphConstants.TaxiAccess) != 0 &&
            (edge.ForwardAccess & GraphConstants.AutoAccess) == 0)
        {
            factor *= AutoCostConstants.TaxiFactor;
        }

        if (edge.Use == Use.Alley)
        {
            factor *= AlleyFactor_;
        }
        else if (edge.Use == Use.Track)
        {
            factor *= TrackFactor_;
        }
        else if (edge.Use == Use.LivingStreet)
        {
            factor *= LivingStreetFactor_;
        }
        else if (edge.Use == Use.ServiceRoad)
        {
            factor *= ServiceFactor_;
        }

        if (IsClosed(edge, tile, edgeid.Id()))
        {
            // Add a penalty for traversing a closed edge
            factor *= ClosureFactor_;
        }

        factor *= (float)EdgeFactor(edgeid);

        return new Cost(sec * factor, sec);
    }
}

/// <summary>
/// Auto/Bus/Taxi cost-option parsing and factory functions. Faithful port of the free functions in
/// <c>autocost.cc</c> (<c>ParseAutoCostOptions</c>/<c>ParseBusCostOptions</c>/<c>ParseTaxiCostOptions</c>
/// and the <c>Create*Cost</c> factory functions).
/// </summary>
public static class AutoCostFactory
{
    /// <summary>
    /// Parses auto-specific costing options. Faithful port of <c>ParseAutoCostOptions</c>.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: the C++ reads a rapidjson DOM by JSON pointer paths ("/alley_factor"). This port
    /// reads the same keys from a <see cref="System.Text.Json.JsonElement"/> object (no leading slash),
    /// mirroring the foundation's <see cref="CostOptionsParser"/>. The JSON_PBF_RANGED_DEFAULT
    /// semantics (fallback to a present value, otherwise the range default; clamp through the range)
    /// are reproduced.
    /// </remarks>
    public static void ParseAutoCostOptions(
        System.Text.Json.JsonElement doc,
        string costingOptionsKey,
        Costing c,
        List<string> warnings)
    {
        c.SetType(Costing.Type.Auto);
        c.SetName(CostingTypes.EnumName(c.CostingType));
        CostingOptions co = c.Options;

        System.Text.Json.JsonElement json = GetChild(doc, costingOptionsKey);

        CostOptionsParser.ParseBaseCostOptions(json, c, AutoCostConstants.BaseCostOptsConfig, warnings);

        RangedFloat(AutoCostConstants.AlleyFactorRange, json, "alley_factor",
            co.AlleyFactor, v => co.AlleyFactor = v, warnings);
        RangedFloat(AutoCostConstants.UseHighwaysRange, json, "use_highways",
            co.UseHighways, v => co.UseHighways = v, warnings);
        RangedFloat(AutoCostConstants.UseTollsRange, json, "use_tolls",
            co.UseTolls, v => co.UseTolls = v, warnings);
        RangedFloat(AutoCostConstants.UseDistanceRange, json, "use_distance",
            co.UseDistance, v => co.UseDistance = v, warnings);
        RangedUint(AutoCostConstants.ProbabilityRange, json, "restriction_probability",
            co.RestrictionProbability, v => co.RestrictionProbability = v, warnings);
        RangedUint(AutoCostConstants.VehicleSpeedRange, json, "top_speed",
            (uint)co.TopSpeed, v => co.TopSpeed = v, warnings);

        // TruckMate custom costing (TR-OSMNAV-COSTING-032): unprotected-left avoidance applies to
        // auto/taxi/bus as well as truck. Parsed into the shared CostingOptions field the coster reads.
        RangedFloat(AutoCostConstants.UnprotectedLeftAvoidanceRange, json, "unprotected_left_avoidance_meters",
            co.UnprotectedLeftAvoidanceMeters, v => co.UnprotectedLeftAvoidanceMeters = v, warnings);
    }

    /// <summary>Parses bus-specific costing options. Faithful port of <c>ParseBusCostOptions</c>.</summary>
    public static void ParseBusCostOptions(
        System.Text.Json.JsonElement doc,
        string costingOptionsKey,
        Costing c,
        List<string> warnings)
    {
        ParseAutoCostOptions(doc, costingOptionsKey, c, warnings);
        c.SetType(Costing.Type.Bus);
        c.SetName(CostingTypes.EnumName(c.CostingType));
    }

    /// <summary>Parses taxi-specific costing options. Faithful port of <c>ParseTaxiCostOptions</c>.</summary>
    public static void ParseTaxiCostOptions(
        System.Text.Json.JsonElement doc,
        string costingOptionsKey,
        Costing c,
        List<string> warnings)
    {
        ParseAutoCostOptions(doc, costingOptionsKey, c, warnings);
        c.SetType(Costing.Type.Taxi);
        c.SetName(CostingTypes.EnumName(c.CostingType));
    }

    /// <summary>Faithful port of <c>CreateAutoCost</c>.</summary>
    public static DynamicCost CreateAutoCost(Costing costingOptions) => new AutoCost(costingOptions);

    /// <summary>Faithful port of <c>CreateBusCost</c>.</summary>
    public static DynamicCost CreateBusCost(Costing costingOptions) => new BusCost(costingOptions);

    /// <summary>Faithful port of <c>CreateTaxiCost</c>.</summary>
    public static DynamicCost CreateTaxiCost(Costing costingOptions) => new TaxiCost(costingOptions);

    // ---- JSON_PBF_RANGED_DEFAULT helpers (auto layer) ----

    private static void RangedFloat(
        RangedDefault<float> range,
        System.Text.Json.JsonElement json,
        string key,
        float currentValue,
        Action<float> setter,
        List<string> warnings)
    {
        // JSON_PBF_RANGED_DEFAULT: fallback to a present value, otherwise the range default.
        float fallback = currentValue != 0f ? currentValue : range.Def;
        float requested = GetFloat(json, key, fallback);
        float clampedValue = range.Invoke(requested, out bool clamped);
        setter(clampedValue);
        if (clamped)
            warnings.Add($"'{key}' has been clamped to {range.Def}");
    }

    private static void RangedUint(
        RangedDefault<uint> range,
        System.Text.Json.JsonElement json,
        string key,
        uint currentValue,
        Action<uint> setter,
        List<string> warnings)
    {
        uint fallback = currentValue != 0u ? currentValue : range.Def;
        uint requested = GetUInt(json, key, fallback);
        uint clampedValue = range.Invoke(requested, out bool clamped);
        setter(clampedValue);
        if (clamped)
            warnings.Add($"'{key}' has been clamped to {range.Def}");
    }

    private static System.Text.Json.JsonElement GetChild(System.Text.Json.JsonElement json, string key)
    {
        if (json.ValueKind == System.Text.Json.JsonValueKind.Object && json.TryGetProperty(key, out var child))
            return child;
        return default;
    }

    private static float GetFloat(System.Text.Json.JsonElement json, string key, float def)
        => GetChild(json, key) is { ValueKind: System.Text.Json.JsonValueKind.Number } e ? (float)e.GetDouble() : def;

    private static uint GetUInt(System.Text.Json.JsonElement json, string key, uint def)
        => GetChild(json, key) is { ValueKind: System.Text.Json.JsonValueKind.Number } e ? e.GetUInt32() : def;
}
