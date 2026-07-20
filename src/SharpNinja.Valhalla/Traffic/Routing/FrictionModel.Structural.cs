using System.Globalization;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic.Routing;

public static partial class FrictionModel
{
    /// <summary>
    /// Scores route structure and exact-edge traffic evaluation with named, explainable
    /// contributions. Highway exposure is intentionally not a contribution.
    /// </summary>
    public static RouteStructuralFrictionScore Score(
        RouteStructuralFrictionInput input,
        TrafficPolicy? policy = null,
        RouteFrictionWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        TrafficPolicy effectivePolicy = policy ?? TrafficPolicy.Enabled;
        RouteFrictionWeights effectiveWeights = weights ?? RouteFrictionWeights.Default;
        var contributions = new List<RouteFrictionContribution>();

        AddContribution(contributions, FrictionContributionKind.Maneuvers, "turns/intersections",
            input.ManeuverCount, effectiveWeights.Maneuver, "Valhalla route maneuvers", false);
        AddContribution(contributions, FrictionContributionKind.TrafficSignals, "traffic signals",
            input.TrafficSignalCount, effectiveWeights.TrafficSignal,
            "route-adjacent traffic signals from the Valhalla graph", false);
        AddContribution(contributions, FrictionContributionKind.StopSigns, "stop signs",
            input.StopSignCount, effectiveWeights.StopSign,
            "route-adjacent stop signs from the Valhalla graph", false);
        AddContribution(contributions, FrictionContributionKind.YieldSigns, "yield signs",
            input.YieldSignCount, effectiveWeights.YieldSign,
            "route-adjacent yield signs from the Valhalla graph", false);
        AddContribution(contributions, FrictionContributionKind.RequiredRampMergeExit,
            "required ramps/merges/exits", input.RequiredRampMergeExitCount,
            effectiveWeights.RequiredRampMergeExit,
            "required ramp, merge, exit, or stay maneuvers", false);
        AddContribution(contributions, FrictionContributionKind.Tolls, "tolls",
            Math.Max(input.TollManeuverCount, input.HasToll ? 1 : 0),
            effectiveWeights.Toll, "toll road or toll maneuver exposure", false);
        AddContribution(contributions, FrictionContributionKind.Ferry, "ferry",
            Math.Max(input.FerryManeuverCount, input.HasFerry ? 1 : 0),
            effectiveWeights.Ferry, "ferry boarding or ferry maneuver exposure", false);

        if (input.LaneFriction is not null)
        {
            AddLaneContribution(
                contributions,
                input.LaneFriction,
                effectiveWeights.LaneFriction);
        }

        if (effectivePolicy.IncludeTrafficDelayInFriction && input.Traffic is not null)
        {
            AddContribution(contributions, FrictionContributionKind.TrafficDelay, "traffic delay",
                input.Traffic.ObservedTrafficDelaySeconds, effectiveWeights.TrafficDelaySecond,
                "provider delay matched to exact Valhalla directed edges", true);
            AddContribution(contributions, FrictionContributionKind.Incidents, "incidents",
                input.Traffic.ObservedIncidentCount, effectiveWeights.Incident,
                "conflict-resolved provider incidents matched to exact Valhalla directed edges", true);
        }

        double staticScore = contributions
            .Where(static contribution => !contribution.IsDynamic)
            .Sum(static contribution => contribution.Score);
        double dynamicScore = contributions
            .Where(static contribution => contribution.IsDynamic)
            .Sum(static contribution => contribution.Score);
        return new RouteStructuralFrictionScore(
            staticScore + dynamicScore,
            staticScore,
            dynamicScore,
            Array.AsReadOnly(contributions.ToArray()))
        {
            LaneGuidance = input.LaneFriction?.Guidance ?? Array.Empty<LaneGuidancePoint>(),
        };
    }

    /// <summary>
    /// Builds the complete DATA structural-friction input from one production route candidate,
    /// graph-derived traffic controls, and exact-edge traffic evaluation.
    /// </summary>
    public static RouteStructuralFrictionScore Score(
        OsmRouteCandidate route,
        ValhallaRouteTrafficControlCounts trafficControls,
        RouteTrafficEvaluation traffic,
        TrafficPolicy? policy = null,
        RouteFrictionWeights? weights = null)
        => Score(route, trafficControls, traffic, laneFriction: null, policy, weights);

    /// <summary>
    /// Builds and scores production route data while composing graph-derived lane friction and
    /// exposing its ordered lane guidance on the returned score.
    /// </summary>
    public static RouteStructuralFrictionScore Score(
        OsmRouteCandidate route,
        ValhallaRouteTrafficControlCounts trafficControls,
        RouteTrafficEvaluation traffic,
        LaneFrictionProfile? laneFriction,
        TrafficPolicy? policy = null,
        RouteFrictionWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(trafficControls);
        ArgumentNullException.ThrowIfNull(traffic);

        int requiredRampMergeExitCount = route.Maneuvers.Count(
            static maneuver => IsRequiredRampMergeExitManeuver(maneuver.Type));
        var input = new RouteStructuralFrictionInput(
            ManeuverCount: route.FrictionInputs.ManeuverCount,
            TrafficSignalCount: trafficControls.TrafficSignalCount,
            StopSignCount: trafficControls.StopSignCount,
            YieldSignCount: trafficControls.YieldSignCount,
            RequiredRampMergeExitCount: requiredRampMergeExitCount,
            TollManeuverCount: route.FrictionInputs.TollManeuverCount,
            FerryManeuverCount: route.FrictionInputs.FerryManeuverCount,
            HasToll: route.FrictionInputs.HasToll,
            HasFerry: route.FrictionInputs.HasFerry,
            HighwayManeuverCount: route.FrictionInputs.HighwayManeuverCount,
            Traffic: traffic,
            LaneFriction: laneFriction);
        return Score(input, policy, weights);
    }

    private static bool IsRequiredRampMergeExitManeuver(int maneuverType)
        => (DirectionsLegManeuverType)maneuverType is
            DirectionsLegManeuverType.RampStraight or
            DirectionsLegManeuverType.RampRight or
            DirectionsLegManeuverType.RampLeft or
            DirectionsLegManeuverType.ExitRight or
            DirectionsLegManeuverType.ExitLeft or
            DirectionsLegManeuverType.StayStraight or
            DirectionsLegManeuverType.StayRight or
            DirectionsLegManeuverType.StayLeft or
            DirectionsLegManeuverType.Merge or
            DirectionsLegManeuverType.MergeRight or
            DirectionsLegManeuverType.MergeLeft;

    private static void AddLaneContribution(
        ICollection<RouteFrictionContribution> contributions,
        LaneFrictionProfile profile,
        double rawWeight)
    {
        double weight = double.IsFinite(rawWeight) ? Math.Max(0d, rawWeight) : 0d;
        int laneFrictionUnits = Math.Max(0, profile.Score);
        if (laneFrictionUnits == 0 || weight == 0d)
        {
            return;
        }

        double score = laneFrictionUnits * weight;
        contributions.Add(new RouteFrictionContribution(
            FrictionContributionKind.LaneTopology,
            "lane topology",
            profile.Contributions.Count,
            score,
            string.Format(
                CultureInfo.InvariantCulture,
                "Graph-derived lane topology and route-specific lane changes: {0} friction units x {1:0.###} = {2:0.###}.",
                laneFrictionUnits,
                weight,
                score),
            false));
    }

    private static void AddContribution(
        ICollection<RouteFrictionContribution> contributions,
        FrictionContributionKind kind,
        string name,
        int rawCount,
        double rawWeight,
        string sourceExplanation,
        bool isDynamic)
    {
        int count = Math.Max(0, rawCount);
        double weight = double.IsFinite(rawWeight) ? Math.Max(0d, rawWeight) : 0d;
        if (count == 0 || weight == 0d)
        {
            return;
        }

        double score = count * weight;
        contributions.Add(new RouteFrictionContribution(
            kind,
            name,
            count,
            score,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1} x {2:0.###} = {3:0.###}.",
                sourceExplanation,
                count,
                weight,
                score),
            isDynamic));
    }
}
