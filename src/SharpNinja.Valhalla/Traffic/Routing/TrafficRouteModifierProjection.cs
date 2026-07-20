using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic.Routing;

public sealed record TrafficRouteModifierSource
{
    public TrafficRouteModifierSource(
        RouteModifierImpact impact,
        IReadOnlyList<string> providerIds,
        IReadOnlyList<string> sourceEventIds,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> affectedEdges,
        int? delaySeconds,
        TrafficSeverity severity)
    {
        ArgumentNullException.ThrowIfNull(impact);
        ArgumentNullException.ThrowIfNull(providerIds);
        ArgumentNullException.ThrowIfNull(sourceEventIds);
        ArgumentNullException.ThrowIfNull(affectedEdges);
        Impact = impact;
        ProviderIds = Array.AsReadOnly(providerIds.ToArray());
        SourceEventIds = Array.AsReadOnly(sourceEventIds.ToArray());
        AffectedEdges = Array.AsReadOnly(affectedEdges.ToArray());
        DelaySeconds = delaySeconds;
        Severity = severity;
    }

    public RouteModifierImpact Impact { get; }
    public IReadOnlyList<string> ProviderIds { get; }
    public IReadOnlyList<string> SourceEventIds { get; }
    public IReadOnlyList<ValhallaTrafficEdgeUpdate> AffectedEdges { get; }
    public int? DelaySeconds { get; }
    public TrafficSeverity Severity { get; }
}

public static class TrafficRouteModifierProjection
{
    public static TrafficRouteModifierSource Project(
        NormalizedTrafficEvent trafficEvent,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> affectedEdges,
        TrafficPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(trafficEvent);
        ArgumentNullException.ThrowIfNull(affectedEdges);
        ArgumentNullException.ThrowIfNull(policy);

        RouteModifierImpactKind kind = trafficEvent.Kind switch
        {
            NormalizedTrafficEventKind.Flow => RouteModifierImpactKind.TrafficDelay,
            NormalizedTrafficEventKind.Incident => RouteModifierImpactKind.Incident,
            NormalizedTrafficEventKind.Closure => RouteModifierImpactKind.RoadClosure,
            NormalizedTrafficEventKind.Restriction => RouteModifierImpactKind.Restriction,
            _ => RouteModifierImpactKind.Unknown,
        };
        bool eligibleHardConstraint =
            trafficEvent.RoadClosure ||
            (trafficEvent.Kind == NormalizedTrafficEventKind.Restriction &&
             trafficEvent.RestrictionApplicability ==
             TrafficRestrictionApplicability.UnconditionalAllVehicles);
        bool directionSafeHardConstraint = eligibleHardConstraint
            && policy.KeepClosuresAsRouteConstraints
            && affectedEdges.Any(static edge => edge.Closed && edge.DirectionResolved);
        bool unresolvedHardConstraint = eligibleHardConstraint && !directionSafeHardConstraint;
        string description = BuildDescription(trafficEvent, unresolvedHardConstraint);
        var impact = new RouteModifierImpact(
            RouteKey: $"traffic-event:{trafficEvent.ProviderId}:{trafficEvent.Id}",
            Kind: kind,
            Description: description,
            HardDeny: directionSafeHardConstraint);
        int? effectiveDelay = policy.IncludeTrafficDelayInEta || policy.IncludeTrafficDelayInFriction
            ? trafficEvent.DelaySeconds
            : null;

        return new TrafficRouteModifierSource(
            impact,
            [trafficEvent.ProviderId],
            [trafficEvent.Id],
            affectedEdges,
            effectiveDelay,
            trafficEvent.Severity);
    }

    private static string BuildDescription(
        NormalizedTrafficEvent trafficEvent,
        bool unresolvedHardConstraint)
    {
        string description = string.IsNullOrWhiteSpace(trafficEvent.Description)
            ? $"{trafficEvent.ProviderId} {trafficEvent.Kind}"
            : trafficEvent.Description;
        return unresolvedHardConstraint
            ? $"{description}; constraint direction is unresolved, so the event remains advisory"
            : description;
    }
}
