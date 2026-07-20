using System.Globalization;

namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>Controls whether dynamic traffic affects route metrics while preserving verified closures.</summary>
public sealed record TrafficPolicy(
    bool IncludeTrafficDelayInEta,
    bool IncludeTrafficDelayInFriction,
    bool KeepClosuresAsRouteConstraints)
{
    public static TrafficPolicy Disabled { get; } = new(false, false, true);
    public static TrafficPolicy Enabled { get; } = new(true, true, true);
}

/// <summary>Describes whether the route engine base duration already incorporates live traffic.</summary>
public enum RouteDurationSource
{
    FreeFlow = 0,
    ProviderTrafficAdjusted = 1,
    LiveTraffic = 2,
    ValhallaTrafficTileAdjusted = LiveTraffic,
}

/// <summary>Provider-neutral route metrics used by deterministic DATA-layer rankers.</summary>
public sealed record RouteCandidateMetrics(
    string ProviderId,
    int Index,
    double DistanceMeters,
    int DurationSeconds,
    IReadOnlyList<string>? RouteLabels = null,
    int TrafficDelaySeconds = 0,
    int IncidentCount = 0,
    int ManeuverCount = 0,
    int TollManeuverCount = 0,
    int HighwayManeuverCount = 0,
    int FerryManeuverCount = 0,
    bool HasToll = false,
    bool HasHighway = false,
    bool HasFerry = false,
    string? RouteKey = null,
    IReadOnlyList<ulong>? DirectedEdgeIds = null,
    RouteDurationSource DurationSource = RouteDurationSource.FreeFlow,
    double StaticFrictionScore = 0d);

/// <summary>Builds stable route identities from ordered Valhalla directed-edge IDs.</summary>
public static class RouteIdentity
{
    public static string Create(RouteCandidateMetrics candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.DirectedEdgeIds is { Count: > 0 })
        {
            return "edges:" + string.Join(
                ",",
                candidate.DirectedEdgeIds.Select(static id => id.ToString("X16", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(candidate.RouteKey))
        {
            return $"key:{Normalize(candidate.RouteKey)}";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"fallback:{Normalize(candidate.ProviderId)}:{candidate.Index}");
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}

public sealed record RouteModifierImpact(
    string RouteKey,
    RouteModifierImpactKind Kind,
    string Description,
    bool HardDeny);

public enum RouteModifierImpactKind
{
    Unknown = 0,
    RoadClosure = 1,
    TrafficDelay = 2,
    Incident = 3,
    Restriction = 4,
    Avoidance = 5,
}

public sealed record RouteModifierAdvisory(
    string RouteKey,
    string DisplayName,
    int NormalRank,
    int NormalDurationSeconds,
    double NormalDistanceMeters,
    IReadOnlyList<RouteModifierImpact> Impacts,
    string Message,
    int? ModifiedRank = null);

public sealed record RouteModifierImpactOptions(
    int NormalCandidateLimit = 3,
    bool RequireHardDeny = false,
    bool ReportUnknownModifierCause = true)
{
    public static RouteModifierImpactOptions Default { get; } = new();
}
