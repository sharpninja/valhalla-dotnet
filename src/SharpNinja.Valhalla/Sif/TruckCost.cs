// Faithful C# port of the Valhalla sif TruckCost coster (sharpninja/valhalla fork, branch
// feature/unprotected-left-costing, based on valhalla @ 3.7.0).
// Sources:
//   - src/sif/truckcost.cc  (the FORK-MODIFIED file: TruckCost class, file-local defaults/ranges,
//                            ParseTruckCostOptions, CreateTruckCost, the INLINE_TEST cases)
//   - valhalla/sif/truckcost.h (ParseTruckCostOptions / CreateTruckCost declarations)
//
// This reproduces the TruckMate CUSTOM additions EXACTLY:
//   - members  unprotected_left_avoidance_meters_  +  enable_static_friction_
//   - the file-local  kUnprotectedLeftReferenceSpeedMps  +  kUnprotectedLeftAvoidanceRange
//   - the ctor reads of  costing_options.unprotected_left_avoidance_meters()  and
//     costing_options.enable_static_friction()
//   - the unprotected-left penalty in TransitionCost (protected only when node->traffic_signal();
//     conservative fallback - untagged/uncertain conflicting approach is treated as unprotected)
//   - the low_class_penalty gating on enable_static_friction_ (both forward and reverse transitions)
//   - the ParseTruckCostOptions parse of both custom options
//
// PORT-NOTE: the C++ coster lives in an anonymous namespace and is only handed out as a
// cost_ptr_t via CreateTruckCost. Here TruckCost is public so the ported INLINE_TEST can subclass
// it (mirroring the gtest's TestTruckCost) and read the protected/internal members.

using System;
using System.Collections.Generic;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

// PORT-NOTE: matches the alias used by DynamicCost so the sif signatures read like the C++ ones.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;

namespace SharpNinja.Valhalla.Sif;

/// <summary>
/// File-local default options/values for truck costing. Faithful port of the anonymous-namespace
/// constants in <c>src/sif/truckcost.cc</c>.
/// </summary>
internal static class TruckCostConstants
{
    // Base transition costs
    // Note: all roads of class "Service, other" are already penalized with low_class_penalty, so for
    // generic service roads these penalties will add up
    public const float DefaultServicePenalty = 0.0f; // Seconds

    // Other options
    public const float DefaultLowClassPenalty = 30.0f; // Seconds
    public const float DefaultUseTolls = 0.5f;         // Factor between 0 and 1
    public const float DefaultUseTracks = 0.0f;        // Avoid tracks by default. Factor between 0 and 1
    public const float DefaultUseLivingStreets = 0.0f; // Avoid living streets by default. Factor between 0 and 1
    public const float DefaultUseHighways = 0.5f;      // Factor between 0 and 1

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

    // Default truck attributes
    public const float DefaultTruckWeight = 21.77f;  // Metric Tons (48,000 lbs)
    public const float DefaultTruckAxleLoad = 9.07f; // Metric Tons (20,000 lbs)
    public const float DefaultTruckHeight = 4.11f;   // Meters (13 feet 6 inches)
    public const float DefaultTruckWidth = 2.6f;     // Meters (102.36 inches)
    public const float DefaultTruckLength = 21.64f;  // Meters (71 feet)
    public const uint DefaultAxleCount = 5;          // 5 axles for above truck config

    // Turn costs based on side of street driving
    public static readonly float[] RightSideTurnCosts =
    {
        TCStraight, TCSlight, TCFavorable, TCFavorableSharp, TCReverse, TCUnfavorableSharp,
        TCUnfavorable, TCSlight,
    };

    public static readonly float[] LeftSideTurnCosts =
    {
        TCStraight, TCSlight, TCUnfavorable, TCUnfavorableSharp, TCReverse, TCFavorableSharp,
        TCFavorable, TCSlight,
    };

    // How much to favor truck routes.
    public const float TruckRouteFactor = 0.85f;
    public const float DefaultUseTruckRoute = 0.0f;
    public const float MinNonTruckRouteFactor = 1.0f;

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

    // Valid ranges and defaults
    public static readonly RangedDefault<float> LowClassPenaltyRange =
        new RangedDefault<float>(0f, DefaultLowClassPenalty, DynamicCost.MaxPenalty);

    public static readonly RangedDefault<float> TruckAxleLoadRange =
        new RangedDefault<float>(0f, DefaultTruckAxleLoad, 40.0f);

    public static readonly RangedDefault<float> UseTollsRange =
        new RangedDefault<float>(0f, DefaultUseTolls, 1.0f);

    public static readonly RangedDefault<uint> AxleCountRange =
        new RangedDefault<uint>(2, DefaultAxleCount, 20);

    public static readonly RangedDefault<float> UseHighwaysRange =
        new RangedDefault<float>(0f, DefaultUseHighways, 1.0f);

    public static readonly RangedDefault<float> TopSpeedRange =
        new RangedDefault<float>(10f, GraphConstants.MaxAssumedTruckSpeed, GraphConstants.MaxSpeedKph);

    // TruckMate custom costing (FR-OSMNAV-022 / TR-OSMNAV-LEFTTURN-033): unprotected-left avoidance.
    // The avoidance detour threshold is supplied in meters and converted to a finite penalty using a
    // nominal surface-street speed, so the engine takes an unprotected left only when avoiding it
    // would detour more than roughly that distance of travel.
    public const float UnprotectedLeftReferenceSpeedMps = 13.4f; // ~30 mph

    public static readonly RangedDefault<float> UnprotectedLeftAvoidanceRange =
        new RangedDefault<float>(0f, 0f, 1000000.0f);

    public static readonly RangedDefault<float> HgvNoAccessRange =
        new RangedDefault<float>(0f, DynamicCost.MaxPenalty, DynamicCost.MaxPenalty);

    public static readonly RangedDefault<float> UseTruckRouteRange =
        new RangedDefault<float>(0f, DefaultUseTruckRoute, 1.0f);

    /// <summary>
    /// Build the base costing options config used by truck costing. Faithful port of
    /// <c>GetBaseCostOptsConfig()</c>; the <c>kBaseCostOptsConfig</c> file-local instance below.
    /// </summary>
    public static BaseCostingOptionsConfig GetBaseCostOptsConfig()
    {
        var cfg = new BaseCostingOptionsConfig();
        // override defaults
        cfg.ServicePenalty = new RangedDefault<float>(cfg.ServicePenalty.Min, DefaultServicePenalty, cfg.ServicePenalty.Max);
        cfg.UseTracks = new RangedDefault<float>(cfg.UseTracks.Min, DefaultUseTracks, cfg.UseTracks.Max);
        cfg.UseLivingStreets = new RangedDefault<float>(cfg.UseLivingStreets.Min, DefaultUseLivingStreets, cfg.UseLivingStreets.Max);
        cfg.Height = new RangedDefault<float>(cfg.Height.Min, DefaultTruckHeight, cfg.Height.Max);
        cfg.Width = new RangedDefault<float>(cfg.Width.Min, DefaultTruckWidth, cfg.Width.Max);
        cfg.Length = new RangedDefault<float>(cfg.Length.Min, DefaultTruckLength, cfg.Length.Max);
        cfg.Weight = new RangedDefault<float>(cfg.Weight.Min, DefaultTruckWeight, cfg.Weight.Max);
        return cfg;
    }

    public static readonly BaseCostingOptionsConfig BaseCostOptsConfig = GetBaseCostOptsConfig();
}

/// <summary>
/// Derived class providing dynamic edge costing for truck routes. Faithful port of
/// <c>valhalla::sif::TruckCost</c> (fork-modified with the TruckMate unprotected-left avoidance and
/// the static-friction toggle).
/// </summary>
public class TruckCost : DynamicCost
{
    // ----- public members (mirroring the C++ public fields) -----

    public VehicleType Type_;        // Vehicle type: truck
    public float TollFactor_;        // Factor applied when road has a toll
    public float LowClassPenalty_;   // Penalty (seconds) to go to residential or service road

    // TruckMate custom costing (FR-OSMNAV-022).
    public float UnprotectedLeftAvoidanceMeters_; // Detour threshold (meters); 0 disables the rule.
    public bool EnableStaticFriction_;            // When false, comfort friction (low-class penalty) is off.

    // Vehicle attributes (used for special restrictions and costing)
    public bool Hazmat_;               // Carrying hazardous materials
    public new float Weight_;          // Vehicle weight in metric tons
    public float AxleLoad_;            // Axle load weight in metric tons
    public new float Height_;          // Vehicle height in meters
    public new float Width_;           // Vehicle width in meters
    public new float Length_;          // Vehicle length in meters
    public float HighwayFactor_;       // Factor applied when road is a motorway or trunk
    public float NonTruckRouteFactor_; // Factor applied when road is not part of a designated truck route
    public byte AxleCount_;            // Vehicle axle count

    // determine if we should allow hgv=no edges and penalize them instead
    public float NoHgvAccessPenalty_;

    /// <summary>
    /// Construct truck costing. Faithful port of <c>TruckCost::TruckCost(const Costing&amp;)</c>.
    /// </summary>
    /// <param name="costing">Specified costing options.</param>
    public TruckCost(Costing costing)
        : base(costing, global::SharpNinja.Valhalla.Sif.TravelMode.Drive, GraphConstants.TruckAccess, true)
    {
        var costingOptions = costing.Options;

        Type_ = VehicleType.Truck;

        // Get the base costs
        GetBaseCosts(costing);

        LowClassPenalty_ = costingOptions.LowClassPenalty;
        NonTruckRouteFactor_ =
            costingOptions.UseTruckRoute < 0.5f
                ? TruckCostConstants.MinNonTruckRouteFactor + 2.0f * costingOptions.UseTruckRoute
                : ((TruckCostConstants.MinNonTruckRouteFactor - 5.0f) + 12.0f * costingOptions.UseTruckRoute);

        // Get the vehicle attributes
        Hazmat_ = costingOptions.Hazmat;
        Weight_ = costingOptions.Weight;
        AxleLoad_ = costingOptions.AxleLoad;
        Height_ = costingOptions.Height;
        Width_ = costingOptions.Width;
        Length_ = costingOptions.Length;
        AxleCount_ = (byte)costingOptions.AxleCount;

        // TruckMate custom costing options.
        UnprotectedLeftAvoidanceMeters_ = costingOptions.UnprotectedLeftAvoidanceMeters;
        EnableStaticFriction_ = costingOptions.EnableStaticFriction;

        // Create speed cost table
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

        // Preference to use toll roads (separate from toll booth penalty). Sets a toll
        // factor. A toll factor of 0 would indicate no adjustment to weighting for toll roads.
        // use_tolls = 1 would reduce weighting slightly (a negative delta) while
        // use_tolls = 0 would penalize (positive delta to weighting factor).
        float useTolls = costingOptions.UseTolls;
        TollFactor_ = useTolls < 0.5f
            ? (2.0f - 4 * useTolls)             // ranges from 2 to 0
            : (0.5f - useTolls) * 0.03f;        // ranges from 0 to -0.015

        // determine what to do with hgv=no edges
        bool noHgvAccessPenaltyActive = !(costingOptions.HgvNoAccessPenalty == MaxPenalty);
        NoHgvAccessPenalty_ = (noHgvAccessPenaltyActive ? 1.0f : 0.0f) * costingOptions.HgvNoAccessPenalty;
        // set the access mask to both car & truck if that penalty is active
        AccessMask_ = noHgvAccessPenaltyActive
            ? (uint)(GraphConstants.AutoAccess | GraphConstants.TruckAccess)
            : GraphConstants.TruckAccess;
    }

    /// <summary>
    /// Does the costing allow hierarchy transitions. Truck costing allows transitions by default.
    /// </summary>
    public virtual bool AllowTransitions() => true;

    /// <summary>Does the costing method allow multiple passes (with relaxed hierarchy limits).</summary>
    public override bool AllowMultiPass() => true;

    /// <summary>Callback for Allowed doing mode-specific restriction checks.</summary>
    public override bool ModeSpecificAllowed(AccessRestriction restriction)
    {
        switch (restriction.Type())
        {
            case AccessType.Hazmat:
                if (Hazmat_ && restriction.Value() == 0)
                    return false;
                break;
            case AccessType.MaxAxleLoad:
                if (AxleLoad_ > (float)(restriction.Value() * 0.01))
                    return false;
                break;
            case AccessType.MaxAxles:
                if (AxleCount_ > (byte)restriction.Value())
                    return false;
                break;
            case AccessType.MaxHeight:
                if (Height_ > (float)(restriction.Value() * 0.01))
                    return false;
                break;
            case AccessType.MaxLength:
                if (Length_ > (float)(restriction.Value() * 0.01))
                    return false;
                break;
            case AccessType.MaxWeight:
                if (Weight_ > (float)(restriction.Value() * 0.01))
                    return false;
                break;
            case AccessType.MaxWidth:
                if (Width_ > (float)(restriction.Value() * 0.01))
                    return false;
                break;
            default:
                return true;
        }

        return true;
    }

    /// <summary>
    /// Only transit costings are valid for this method call, hence we throw. Faithful port of
    /// <c>TruckCost::EdgeCost(DirectedEdge*, TransitDeparture*, uint32_t)</c>.
    /// </summary>
    public override Cost EdgeCost(DirectedEdge edge, TransitDeparture departure, uint currTime)
        => throw new InvalidOperationException("TruckCost::EdgeCost does not support transit edges");

    /// <summary>
    /// Check if access is allowed on the specified edge. Faithful port of <c>TruckCost::Allowed</c>.
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
        if (!IsAccessible(edge) || (!pred.Deadend() && pred.OppLocalIdx() == edge.LocalEdgeIdx) ||
            ((pred.Restrictions() & (1 << (int)edge.LocalEdgeIdx)) != 0 && !IgnoreTurnRestrictions_) ||
            edge.Surface == Surface.Impassable || IsUserAvoidEdge(edgeid) ||
            (!AllowDestinationOnly_ && !pred.Destonly() && edge.DestOnlyHgv) ||
            (pred.ClosurePruning() && IsClosed(edge, tile, edgeid.Id())) ||
            (ExcludeUnpaved_ && !pred.Unpaved() && edge.Unpaved) || CheckExclusions(edge, pred, true))
        {
            return false;
        }

        return EvaluateRestrictions(AccessMask_, edge, isDest, tile, edgeid, currentTime,
            tzIndex, ref restrictionIdx, ref destonlyAccessRestrMask);
    }

    /// <summary>
    /// Checks if access is allowed for an edge on the reverse path (from destination towards
    /// origin). Both opposing edges are provided. Faithful port of <c>TruckCost::AllowedReverse</c>.
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
        if (!IsAccessible(oppEdge) || (!pred.Deadend() && pred.OppLocalIdx() == edge.LocalEdgeIdx) ||
            ((oppEdge.Restrictions & (1 << (int)pred.OppLocalIdx())) != 0 && !IgnoreTurnRestrictions_) ||
            oppEdge.Surface == Surface.Impassable || IsUserAvoidEdge(oppEdgeid) ||
            (!AllowDestinationOnly_ && !pred.Destonly() && oppEdge.DestOnlyHgv) ||
            (pred.ClosurePruning() && IsClosed(oppEdge, tile, oppEdgeid.Id())) ||
            (ExcludeUnpaved_ && !pred.Unpaved() && oppEdge.Unpaved) ||
            CheckExclusions(oppEdge, pred, false))
        {
            return false;
        }

        return EvaluateRestrictions(AccessMask_, oppEdge, false, tile, oppEdgeid,
            currentTime, tzIndex, ref restrictionIdx, ref destonlyAccessRestrMask);
    }

    /// <summary>
    /// Get the cost to traverse the edge in seconds. Faithful port of <c>TruckCost::EdgeCost</c>
    /// (time-aware overload).
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: C++ <c>GetSpeed</c> takes a <c>uint8_t* flow_sources</c> out-param that it sets to
    /// the flow source actually used; the ported <see cref="GraphTile.GetSpeed"/> does not expose
    /// that out-param, so <paramref name="flowSources"/> is threaded into <see cref="SpeedPenalty"/>
    /// unchanged. The directed-edge index that the C++ derives by pointer arithmetic is taken from
    /// <paramref name="edgeid"/>'s <c>Id()</c>.
    /// </remarks>
    public override Cost EdgeCost(
        DirectedEdge edge,
        GraphId edgeid,
        GraphTilePtr tile,
        TimeInfo timeInfo,
        ref byte flowSources)
    {
        uint deIndex = edgeid.Id();
        uint edgeSpeed = FixedSpeed_ == GraphConstants.DisableFixedSpeed
            ? tile.GetSpeed(edge, deIndex, FlowMask_, timeInfo.SecondOfWeek, true, timeInfo.SecondsFromNow)
            : FixedSpeed_;

        uint finalSpeed =
            Math.Min(edgeSpeed,
                edge.TruckSpeed != 0 ? Math.Min(edge.TruckSpeed, TopSpeed_) : TopSpeed_);

        float sec = edge.Length * SpeedFactor[finalSpeed];

        if (Shortest_)
        {
            return new Cost(edge.Length, sec);
        }

        float factor = 1.0f;
        switch (edge.Use)
        {
            case Use.Ferry:
                factor = FerryFactor_;
                break;
            case Use.RailFerry:
                factor = RailFerryFactor_;
                break;
            default:
                factor = DensityFactor[edge.Density] +
                         HighwayFactor_ * TruckCostConstants.HighwayFactor[(uint)edge.Classification] +
                         TruckCostConstants.SurfaceFactor[(uint)edge.Surface] +
                         SpeedPenalty(edge, deIndex, tile, timeInfo, flowSources, edgeSpeed);
                break;
        }

        if (edge.TruckRoute)
        {
            factor *= TruckCostConstants.TruckRouteFactor;
        }
        else
        {
            factor *= NonTruckRouteFactor_;
        }

        if (edge.Toll)
        {
            factor += TollFactor_;
        }

        if (edge.Use == Use.Track)
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

        if (IsClosed(edge, tile, deIndex))
        {
            // Add a penalty for traversing a closed edge
            factor *= ClosureFactor_;
        }

        factor *= (float)EdgeFactor(edgeid);

        return new Cost(sec * factor, sec);
    }

    /// <summary>
    /// Returns the time (in seconds) to make the transition from the predecessor. Faithful port of
    /// <c>TruckCost::TransitionCost</c> (fork-modified: unprotected-left avoidance + static-friction
    /// gating of the low-class penalty).
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
        c.Secs += OSRMCarTurnDuration(edge, node, idx);

        // TruckMate (TR-OSMNAV-LEFTTURN-033): avoid unprotected left turns. A left is protected only
        // when the intersection is signalized; per the data-fidelity gate (TR-OSMNAV-DATAFIDELITY-035)
        // an untagged/uncertain conflicting approach is treated as unprotected (conservative fallback,
        // never wrongly permits an unprotected left). The finite penalty, sized to the configured
        // detour distance, makes A* take the left only when avoiding it would cost more than that
        // distance.
        if (UnprotectedLeftAvoidanceMeters_ > 0.0f)
        {
            Turn.Type truckmateTurn = edge.TurnType(idx);
            if ((truckmateTurn == Turn.Type.Left || truckmateTurn == Turn.Type.SharpLeft) &&
                !node.TrafficSignal)
            {
                c.CostValue += UnprotectedLeftAvoidanceMeters_ / TruckCostConstants.UnprotectedLeftReferenceSpeedMps;
            }
        }

        // Penalty to transition onto low class roads (TruckMate: gated by static-friction toggle).
        if (EnableStaticFriction_ &&
            (edge.Classification == RoadClass.Residential ||
             edge.Classification == RoadClass.ServiceOther))
        {
            c.CostValue += LowClassPenalty_;
        }

        // Penalty if the request wants to avoid hgv=no edges instead of disallowing
        c.CostValue +=
            NoHgvAccessPenalty_ * ((pred.HasHgvAccess() && (edge.ForwardAccess & GraphConstants.TruckAccess) == 0) ? 1.0f : 0.0f);

        uint stopimpact = edge.StopImpact(idx);
        Turn.Type turntype = edge.TurnType(idx);
        // Transition time = turncost * stopimpact * densityfactor
        if (stopimpact > 0 && !Shortest_)
        {
            float turnCost;
            if (edge.EdgeToRight(idx) && edge.EdgeToLeft(idx))
            {
                turnCost = TruckCostConstants.TCCrossing;
            }
            else
            {
                turnCost = node.DriveOnRight
                    ? TruckCostConstants.RightSideTurnCosts[(uint)turntype]
                    : TruckCostConstants.LeftSideTurnCosts[(uint)turntype];
            }

            if ((edge.Use != Use.Ramp && pred.Use() == Use.Ramp) ||
                (edge.Use == Use.Ramp && pred.Use() != Use.Ramp))
            {
                turnCost += TruckCostConstants.TCRamp;
                if (edge.Roundabout)
                    turnCost += TruckCostConstants.TCRoundabout;
            }

            float seconds = turnCost;

            bool hasLeft = turntype == Turn.Type.Left || turntype == Turn.Type.SharpLeft;
            bool hasRight = turntype == Turn.Type.Right || turntype == Turn.Type.SharpRight;
            bool hasReverse = turntype == Turn.Type.Reverse;
            bool isTurn = hasLeft || hasRight || hasReverse;
            // Separate time and penalty when traffic is present. With traffic, edge speeds account
            // for much of the intersection transition time (TODO - evaluate different elapsed time
            // settings). Still want to add a penalty so routes avoid high cost intersections.
            if (isTurn)
            {
                seconds *= stopimpact;
            }

            AddUturnPenalty(idx, node, edge, hasReverse, hasLeft, hasRight, true, pred.InternalTurn(),
                ref seconds);

            // Apply density factor and stop impact penalty if there isn't traffic on this edge or
            // you're not using traffic
            if (!pred.HasMeasuredSpeed())
            {
                if (!isTurn)
                    seconds *= stopimpact;
                seconds *= TransDensityFactor[node.Density];
            }

            c.CostValue += seconds;
        }

        return c;
    }

    /// <summary>
    /// Returns the cost to make the transition from the predecessor edge when using a reverse search
    /// (from destination towards the origin). Faithful port of <c>TruckCost::TransitionCostReverse</c>
    /// (fork-modified: static-friction gating of the low-class penalty).
    /// </summary>
    /// <remarks>pred is the opposing current edge in the reverse tree; edge is the opposing
    /// predecessor in the reverse tree.</remarks>
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
        // TODO: do we want to update the cost if we have flow or speed from traffic.

        // Get the transition cost for country crossing, ferry, gate, toll booth,
        // destination only, alley, maneuver penalty
        Cost c = BaseTransitionCost(node, edge, pred, idx);
        c.Secs += OSRMCarTurnDuration(edge, node, pred.OppLocalIdx);

        // Penalty to transition onto low class roads (TruckMate: gated by static-friction toggle).
        if (EnableStaticFriction_ &&
            (edge.Classification == RoadClass.Residential ||
             edge.Classification == RoadClass.ServiceOther))
        {
            c.CostValue += LowClassPenalty_;
        }

        // Penalty if the request wants to avoid hgv=no edges instead of disallowing
        c.CostValue += NoHgvAccessPenalty_ *
            (((pred.ForwardAccess & GraphConstants.TruckAccess) != 0 &&
              (edge.ForwardAccess & GraphConstants.TruckAccess) == 0) ? 1.0f : 0.0f);

        uint stopimpact = edge.StopImpact(idx);
        Turn.Type turntype = edge.TurnType(idx);
        // Transition time = turncost * stopimpact * densityfactor
        if (stopimpact > 0 && !Shortest_)
        {
            float turnCost;
            if (edge.EdgeToRight(idx) && edge.EdgeToLeft(idx))
            {
                turnCost = TruckCostConstants.TCCrossing;
            }
            else
            {
                turnCost = node.DriveOnRight
                    ? TruckCostConstants.RightSideTurnCosts[(uint)turntype]
                    : TruckCostConstants.LeftSideTurnCosts[(uint)turntype];
            }

            if ((edge.Use != Use.Ramp && pred.Use == Use.Ramp) ||
                (edge.Use == Use.Ramp && pred.Use != Use.Ramp))
            {
                turnCost += TruckCostConstants.TCRamp;
                if (edge.Roundabout)
                    turnCost += TruckCostConstants.TCRoundabout;
            }

            float seconds = turnCost;
            bool hasLeft = turntype == Turn.Type.Left || turntype == Turn.Type.SharpLeft;
            bool hasRight = turntype == Turn.Type.Right || turntype == Turn.Type.SharpRight;
            bool hasReverse = turntype == Turn.Type.Reverse;

            bool isTurn = hasLeft || hasRight || hasReverse;
            // Separate time and penalty when traffic is present. With traffic, edge speeds account
            // for much of the intersection transition time (TODO - evaluate different elapsed time
            // settings). Still want to add a penalty so routes avoid high cost intersections.
            if (isTurn)
            {
                seconds *= stopimpact;
            }

            AddUturnPenalty(idx, node, edge, hasReverse, hasLeft, hasRight, true, internalTurn,
                ref seconds);

            // Apply density factor and stop impact penalty if there isn't traffic on this edge or
            // you're not using traffic
            if (!hasMeasuredSpeed)
            {
                if (!isTurn)
                    seconds *= stopimpact;
                seconds *= TransDensityFactor[node.Density];
            }

            c.CostValue += seconds;
        }

        return c;
    }

    /// <summary>
    /// Get the cost factor for A* heuristics. Faithful port of <c>TruckCost::AStarCostFactor</c>.
    /// </summary>
    public override float AStarCostFactor() => SpeedFactor[TopSpeed_] * (float)MinLinearCostFactor_;

    /// <summary>Returns the current travel type. Faithful port of <c>TruckCost::travel_type</c>.</summary>
    public override byte TravelType() => (byte)Type_;

    /// <summary>
    /// Function used in location searching which excludes and allows ranking results from the search
    /// by looking at each edge's attribution and suitability for use as a location by the travel mode.
    /// Also used to filter edges not usable / inaccessible by truck. Faithful port of the
    /// <c>Allowed(edge, tile, disallow_mask)</c> override.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: the index-based ported tile needs the directed-edge index for <c>IsClosed</c>; the
    /// caller supplies it via <paramref name="deIndex"/> (the C++ derives it from the edge pointer).
    /// </remarks>
    public bool Allowed(DirectedEdge edge, GraphTilePtr tile, uint deIndex, ushort disallowMask = DisallowNone)
    {
        bool allowClosures = (!FilterClosures_ && (disallowMask & DisallowClosure) == 0) ||
                             (FlowMask_ & GraphConstants.CurrentFlowMask) == 0;
        return base.Allowed(edge, tile, disallowMask) && !edge.BssConnection &&
               (allowClosures || !tile.IsClosed(deIndex));
    }

    // ===================== EvaluateRestrictions (base helper reproduced here) =====================

    /// <summary>
    /// Evaluates mode-specific and time-dependent access restrictions, including a binary search to
    /// get the tile's access restrictions. Faithful port of the inline
    /// <c>DynamicCost::EvaluateRestrictions</c>.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: lives on DynamicCost in C++; reproduced here as a protected helper because the
    /// already-shipped foundation slice did not include it (the parallel AutoCost slice does the
    /// same). Conditional-restriction timing delegates to <see cref="DynamicCost.IsConditionalActive"/>
    /// (which throws until the DateTime/tz slice ports); callers passing <paramref name="currentTime"/>
    /// == 0 never reach it, matching the C++ early-continue behavior.
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
/// Free-function helpers for truck costing: option parsing and the factory. Faithful port of
/// <c>ParseTruckCostOptions</c> and <c>CreateTruckCost</c> from <c>src/sif/truckcost.cc</c>.
/// </summary>
public static class TruckCostFactory
{
    /// <summary>
    /// Parses the truck cost options from json and stores values on the costing options. Faithful
    /// port of <c>ParseTruckCostOptions</c>.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: as with <see cref="CostOptionsParser.ParseBaseCostOptions"/> the JSON is read from
    /// a <see cref="JsonElement"/> object with the same top-level keys (no leading slash) instead of
    /// a rapidjson DOM addressed by JSON pointer. The JSON_PBF_RANGED_DEFAULT[_V2] / JSON_PBF_DEFAULT_V2
    /// macro semantics are reproduced through the shared parser helpers.
    /// </remarks>
    public static void ParseTruckCostOptions(JsonElement json, Costing c, List<string> warnings)
    {
        c.SetType(Costing.Type.Truck);
        c.SetName(CostingTypes.EnumName(c.CostingType));
        CostingOptions co = c.Options;

        CostOptionsParser.ParseBaseCostOptions(json, c, TruckCostConstants.BaseCostOptsConfig, warnings);

        CostOptionsParser.RangedDefault(co, TruckCostConstants.LowClassPenaltyRange, json,
            "low_class_penalty", co.HasLowClassPenalty, co.LowClassPenalty,
            (v, h) => { co.LowClassPenalty = v; co.HasLowClassPenalty = h; }, warnings);

        co.Hazmat = CostOptionsParser.GetBool(json, "hazmat", co.Hazmat || false);

        CostOptionsParser.RangedDefault(co, TruckCostConstants.TruckAxleLoadRange, json,
            "axle_load", co.HasAxleLoad, co.AxleLoad,
            (v, h) => { co.AxleLoad = v; co.HasAxleLoad = h; }, warnings);

        CostOptionsParser.RangedDefault(co, TruckCostConstants.UseTollsRange, json,
            "use_tolls", co.HasUseTolls, co.UseTolls,
            (v, h) => { co.UseTolls = v; co.HasUseTolls = h; }, warnings);

        CostOptionsParser.RangedDefault(co, TruckCostConstants.UseHighwaysRange, json,
            "use_highways", co.HasUseHighways, co.UseHighways,
            (v, h) => { co.UseHighways = v; co.HasUseHighways = h; }, warnings);

        CostOptionsParser.RangedDefaultUintV2(co, TruckCostConstants.AxleCountRange, json,
            "axle_count", co.AxleCount, v => co.AxleCount = v, warnings);

        CostOptionsParser.RangedDefault(co, TruckCostConstants.TopSpeedRange, json,
            "top_speed", co.HasTopSpeed, co.TopSpeed,
            (v, h) => { co.TopSpeed = v; co.HasTopSpeed = h; }, warnings);

        CostOptionsParser.RangedDefault(co, TruckCostConstants.HgvNoAccessRange, json,
            "hgv_no_access_penalty", co.HasHgvNoAccessPenalty, co.HgvNoAccessPenalty,
            (v, h) => { co.HgvNoAccessPenalty = v; co.HasHgvNoAccessPenalty = h; }, warnings);

        CostOptionsParser.RangedDefaultV2(co, TruckCostConstants.UseTruckRouteRange, json,
            "use_truck_route", co.UseTruckRoute, v => co.UseTruckRoute = v, warnings);

        // TruckMate custom costing options (TR-OSMNAV-COSTING-032).
        CostOptionsParser.RangedDefault(co, TruckCostConstants.UnprotectedLeftAvoidanceRange, json,
            "unprotected_left_avoidance_meters", co.HasUnprotectedLeftAvoidanceMeters,
            co.UnprotectedLeftAvoidanceMeters,
            (v, h) => { co.UnprotectedLeftAvoidanceMeters = v; co.HasUnprotectedLeftAvoidanceMeters = h; },
            warnings);

        co.EnableStaticFriction = CostOptionsParser.GetBool(json, "enable_static_friction", true);
    }

    /// <summary>Create a truck cost. Faithful port of <c>CreateTruckCost</c>.</summary>
    public static DynamicCost CreateTruckCost(Costing costing) => new TruckCost(costing);
}
