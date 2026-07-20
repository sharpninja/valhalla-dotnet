namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>
/// Provider-neutral route structure consumed by the DATA friction model. Highway count is
/// informational only: highway use alone never creates friction.
/// </summary>
public sealed record RouteStructuralFrictionInput(
    int ManeuverCount = 0,
    int TrafficSignalCount = 0,
    int StopSignCount = 0,
    int YieldSignCount = 0,
    int RequiredRampMergeExitCount = 0,
    int TollManeuverCount = 0,
    int FerryManeuverCount = 0,
    bool HasToll = false,
    bool HasFerry = false,
    int HighwayManeuverCount = 0,
    RouteTrafficEvaluation? Traffic = null,
    LaneFrictionProfile? LaneFriction = null);

/// <summary>Weights for deterministic structural and dynamic friction contributions.</summary>
public sealed record RouteFrictionWeights(
    double Maneuver = 1d,
    double TrafficSignal = 4d,
    double StopSign = 3d,
    double YieldSign = 2d,
    double RequiredRampMergeExit = 5d,
    double Toll = 8d,
    double Ferry = 12d,
    double TrafficDelaySecond = 1d,
    double Incident = 180d,
    double LaneFriction = 1d)
{
    public static RouteFrictionWeights Default { get; } = new();
}

public enum FrictionContributionKind
{
    Maneuvers = 0,
    TrafficSignals = 1,
    StopSigns = 2,
    YieldSigns = 3,
    RequiredRampMergeExit = 4,
    Tolls = 5,
    Ferry = 6,
    TrafficDelay = 7,
    Incidents = 8,

    /// <summary>Reserved for reporting compatibility; the scorer never emits this penalty.</summary>
    HighwayExposure = 9,

    /// <summary>Graph-derived lane topology and route-specific lane changes.</summary>
    LaneTopology = 10,
}

public sealed record RouteFrictionContribution(
    FrictionContributionKind Kind,
    string Name,
    int Count,
    double Score,
    string Explanation,
    bool IsDynamic);

public sealed record RouteStructuralFrictionScore(
    double TotalScore,
    double StaticScore,
    double DynamicScore,
    IReadOnlyList<RouteFrictionContribution> Contributions)
{
    /// <summary>Gets ordered graph-derived lane guidance associated with this score.</summary>
    public IReadOnlyList<LaneGuidancePoint> LaneGuidance { get; init; } = Array.Empty<LaneGuidancePoint>();
}
