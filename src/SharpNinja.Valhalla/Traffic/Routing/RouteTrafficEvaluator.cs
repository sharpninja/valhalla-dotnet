using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>
/// Route-specific traffic facts joined exclusively by canonical Valhalla directed-edge id.
/// Observed values preserve snapshot truth; effective values apply the supplied traffic policy.
/// </summary>
public sealed record RouteTrafficEvaluation(
    string RouteKey,
    TrafficPolicy Policy,
    int ObservedTrafficDelaySeconds,
    int TrafficDelaySeconds,
    int ObservedIncidentCount,
    int IncidentCount,
    bool HasClosureHardDeny,
    bool HasRestrictionHardDeny,
    IReadOnlyList<RouteModifierImpact> Impacts,
    IReadOnlyList<TrafficRouteModifierSource> Sources,
    IReadOnlyList<ValhallaTrafficEdgeUpdate> AffectedEdges)
{
    public bool HasHardDeny => HasClosureHardDeny || HasRestrictionHardDeny;

    /// <summary>
    /// Applies route-specific traffic once. Provider- or tile-adjusted base durations are not
    /// adjusted again, and incident delay is already part of TrafficDelaySeconds.
    /// </summary>
    public int AdjustedEtaSeconds(RouteCandidateMetrics candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        int baseDuration = Math.Max(0, candidate.DurationSeconds);
        if (!Policy.IncludeTrafficDelayInEta ||
            TrafficAwareRerouteRanker.BaseDurationIncludesTraffic(candidate.DurationSource))
        {
            return baseDuration;
        }

        int delaySeconds = Math.Max(0, ObservedTrafficDelaySeconds);
        return baseDuration >= int.MaxValue - delaySeconds
            ? int.MaxValue
            : baseDuration + delaySeconds;
    }

    /// <summary>Copies effective traffic metrics onto a candidate for existing DATA rankers.</summary>
    public RouteCandidateMetrics ApplyTo(RouteCandidateMetrics candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate with
        {
            TrafficDelaySeconds = TrafficDelaySeconds,
            IncidentCount = IncidentCount,
        };
    }
}

/// <summary>
/// Evaluates normalized traffic against a route without geometry heuristics. A source applies only
/// when one of its exact canonical Valhalla directed-edge ids is present in the route candidate.
/// </summary>
public static class RouteTrafficEvaluator
{
    public static RouteTrafficEvaluation Evaluate(
        RouteCandidateMetrics candidate,
        NormalizedTrafficSnapshot snapshot,
        TrafficPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(snapshot);
        TrafficPolicy effectivePolicy = policy ?? TrafficPolicy.Enabled;
        string routeKey = RouteIdentity.Create(candidate);
        if (candidate.DirectedEdgeIds is not { Count: > 0 })
        {
            return Empty(routeKey, effectivePolicy);
        }

        HashSet<ulong> routeEdges = candidate.DirectedEdgeIds.ToHashSet();

        var affectedEdgeKeys = new HashSet<(
            ulong EdgeId,
            string ProviderId,
            string EventId)>();
        var applicableSources = new List<TrafficRouteModifierSource>();
        var affectedEdges = new List<ValhallaTrafficEdgeUpdate>();
        int observedDelaySeconds = 0;
        int observedIncidentCount = 0;
        bool closureHardDeny = false;
        bool restrictionHardDeny = false;

        foreach (IGrouping<string, TrafficRouteModifierSource> sourceGroup in
                 snapshot.RouteModifierSources.GroupBy(CreateSourceKey, StringComparer.Ordinal))
        {
            TrafficRouteModifierSource[] groupedSources = sourceGroup.ToArray();
            TrafficRouteModifierSource first = groupedSources[0];
            RouteModifierImpactKind impactKind = first.Impact.Kind;
            bool isConstraint =
                impactKind is RouteModifierImpactKind.RoadClosure
                    or RouteModifierImpactKind.Restriction;

            ValhallaTrafficEdgeUpdate[] matchingEdges = groupedSources
                .SelectMany(static source => source.AffectedEdges)
                .Where(edge =>
                    routeEdges.Contains(edge.CanonicalDirectedEdgeId) &&
                    (edge.DirectionResolved || isConstraint))
                .GroupBy(static edge => (
                    edge.CanonicalDirectedEdgeId,
                    edge.ProviderId,
                    edge.SourceEventId))
                .Select(static edgeGroup => edgeGroup
                    .OrderByDescending(static edge => edge.DirectionResolved)
                    .ThenByDescending(static edge => edge.Closed)
                    .ThenByDescending(static edge => edge.Confidence)
                    .First())
                .ToArray();
            if (matchingEdges.Length == 0)
            {
                continue;
            }

            bool sourceHardDeny = groupedSources.Any(static source => source.Impact.HardDeny);
            bool hasResolvedEdge = matchingEdges.Any(static edge => edge.DirectionResolved);
            bool hasResolvedClosedEdge = matchingEdges.Any(
                static edge => edge.Closed && edge.DirectionResolved);
            bool routeSourceHardDeny =
                impactKind == RouteModifierImpactKind.RoadClosure
                    ? effectivePolicy.KeepClosuresAsRouteConstraints &&
                      sourceHardDeny &&
                      hasResolvedClosedEdge
                    : impactKind == RouteModifierImpactKind.Restriction &&
                      sourceHardDeny &&
                      hasResolvedEdge;
            int[] sourceDelays = groupedSources
                .Where(static source => source.DelaySeconds.HasValue)
                .Select(static source => Math.Max(0, source.DelaySeconds.GetValueOrDefault()))
                .ToArray();
            int? sourceDelaySeconds =
                !hasResolvedEdge || sourceDelays.Length == 0
                    ? null
                    : sourceDelays.Max();
            string[] providerIds = groupedSources
                .SelectMany(static source => source.ProviderIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string[] eventIds = groupedSources
                .SelectMany(static source => source.SourceEventIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var routedImpact = new RouteModifierImpact(
                routeKey,
                impactKind,
                first.Impact.Description,
                routeSourceHardDeny);
            var routedSource = new TrafficRouteModifierSource(
                routedImpact,
                providerIds,
                eventIds,
                matchingEdges,
                sourceDelaySeconds,
                groupedSources.Max(static source => source.Severity));
            applicableSources.Add(routedSource);

            observedDelaySeconds = SaturatingAdd(
                observedDelaySeconds,
                sourceDelaySeconds.GetValueOrDefault());
            if (impactKind == RouteModifierImpactKind.Incident)
            {
                observedIncidentCount = SaturatingAdd(observedIncidentCount, 1);
            }

            closureHardDeny |=
                impactKind == RouteModifierImpactKind.RoadClosure &&
                routeSourceHardDeny;
            restrictionHardDeny |=
                impactKind == RouteModifierImpactKind.Restriction &&
                routeSourceHardDeny;

            foreach (ValhallaTrafficEdgeUpdate edge in matchingEdges)
            {
                if (affectedEdgeKeys.Add((
                    edge.CanonicalDirectedEdgeId,
                    edge.ProviderId,
                    edge.SourceEventId)))
                {
                    affectedEdges.Add(edge);
                }
            }
        }

        RouteModifierImpact[] impacts = applicableSources
            .Select(source => source.Impact with { RouteKey = routeKey })
            .ToArray();
        bool includeDynamicTraffic =
            effectivePolicy.IncludeTrafficDelayInEta ||
            effectivePolicy.IncludeTrafficDelayInFriction;
        int effectiveDelay = includeDynamicTraffic ? observedDelaySeconds : 0;
        int effectiveIncidentCount =
            effectivePolicy.IncludeTrafficDelayInFriction ? observedIncidentCount : 0;

        return new RouteTrafficEvaluation(
            routeKey,
            effectivePolicy,
            observedDelaySeconds,
            effectiveDelay,
            observedIncidentCount,
            effectiveIncidentCount,
            closureHardDeny,
            restrictionHardDeny,
            impacts,
            Array.AsReadOnly(applicableSources.ToArray()),
            Array.AsReadOnly(affectedEdges.ToArray()));
    }

    private static RouteTrafficEvaluation Empty(string routeKey, TrafficPolicy policy)
        => new(
            routeKey,
            policy,
            0,
            0,
            0,
            0,
            false,
            false,
            [],
            [],
            []);

    private static string CreateSourceKey(TrafficRouteModifierSource source)
    {
        string eventIds = string.Join(
            "",
            source.SourceEventIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal));
        string providers = string.Join(
            "",
            source.ProviderIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal));
        return $"{(int)source.Impact.Kind}{providers}{eventIds}";
    }

    private static int SaturatingAdd(int left, int right)
        => left >= int.MaxValue - right ? int.MaxValue : left + right;
}
