using System.Globalization;

namespace SharpNinja.Valhalla.Traffic.Routing;

public static class RouteModifierImpactAnalyzer
{
    public static IReadOnlyList<RouteModifierAdvisory> FindSuppressedNormalRoutes(
        IReadOnlyList<RouteCandidateMetrics> unmodifiedCandidates,
        IReadOnlyList<RouteCandidateMetrics> modifiedCandidates,
        IReadOnlyList<RouteModifierImpact> modifierImpacts,
        RoutePreferenceGoal goal,
        RoutePreferenceWeights weights,
        RouteModifierImpactOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modifierImpacts);
        Dictionary<string, IReadOnlyList<RouteModifierImpact>> impactsByRoute = modifierImpacts
            .Where(static impact => !string.IsNullOrWhiteSpace(impact.RouteKey))
            .GroupBy(static impact => impact.RouteKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<RouteModifierImpact>)group.ToArray(),
                StringComparer.Ordinal);
        return FindAffectedNormalRoutes(
            unmodifiedCandidates,
            modifiedCandidates,
            (candidate, identity) => impactsByRoute.TryGetValue(
                identity,
                out IReadOnlyList<RouteModifierImpact>? impacts)
                    ? impacts
                    : [],
            goal,
            weights,
            options);
    }

    public static IReadOnlyList<RouteModifierAdvisory> FindTrafficAffectedNormalRoutes(
        IReadOnlyList<RouteCandidateMetrics> unmodifiedCandidates,
        IReadOnlyList<RouteCandidateMetrics> modifiedCandidates,
        IReadOnlyList<TrafficRouteModifierSource> modifierSources,
        RoutePreferenceGoal goal,
        RoutePreferenceWeights weights,
        RouteModifierImpactOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modifierSources);
        return FindAffectedNormalRoutes(
            unmodifiedCandidates,
            modifiedCandidates,
            (candidate, identity) =>
            {
                if (candidate.DirectedEdgeIds is not { Count: > 0 })
                {
                    return [];
                }

                HashSet<ulong> routeEdges = candidate.DirectedEdgeIds.ToHashSet();
                return modifierSources
                    .Where(source => source.AffectedEdges.Any(
                        edge => routeEdges.Contains(edge.CanonicalDirectedEdgeId)))
                    .Select(source => source.Impact with { RouteKey = identity })
                    .ToArray();
            },
            goal,
            weights,
            options);
    }

    private static IReadOnlyList<RouteModifierAdvisory> FindAffectedNormalRoutes(
        IReadOnlyList<RouteCandidateMetrics> unmodifiedCandidates,
        IReadOnlyList<RouteCandidateMetrics> modifiedCandidates,
        Func<RouteCandidateMetrics, string, IReadOnlyList<RouteModifierImpact>> impactsForRoute,
        RoutePreferenceGoal goal,
        RoutePreferenceWeights weights,
        RouteModifierImpactOptions? options)
    {
        ArgumentNullException.ThrowIfNull(unmodifiedCandidates);
        ArgumentNullException.ThrowIfNull(modifiedCandidates);
        ArgumentNullException.ThrowIfNull(impactsForRoute);
        ArgumentNullException.ThrowIfNull(weights);
        RouteModifierImpactOptions effectiveOptions = options ?? RouteModifierImpactOptions.Default;
        int limit = Math.Max(0, effectiveOptions.NormalCandidateLimit);
        if (unmodifiedCandidates.Count == 0 || limit == 0)
        {
            return [];
        }

        Dictionary<string, int> modifiedRanks = modifiedCandidates.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : RankNormalCandidates(
                    modifiedCandidates,
                    goal,
                    weights,
                    modifiedCandidates.Count)
                .GroupBy(item => RouteIdentity.Create(item.Candidate), StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Min(static item => item.Rank),
                    StringComparer.Ordinal);
        var advisories = new List<RouteModifierAdvisory>();
        foreach ((RouteCandidateMetrics candidate, int normalRank) in
                 RankNormalCandidates(unmodifiedCandidates, goal, weights, limit))
        {
            string identity = RouteIdentity.Create(candidate);
            bool remains = modifiedRanks.TryGetValue(identity, out int modifiedRank);
            bool deprioritized = remains && modifiedRank > normalRank;
            if (remains && !deprioritized)
            {
                continue;
            }

            IReadOnlyList<RouteModifierImpact> impacts = impactsForRoute(candidate, identity);
            if (effectiveOptions.RequireHardDeny && impacts.All(static impact => !impact.HardDeny))
            {
                continue;
            }

            if (impacts.Count == 0 && !effectiveOptions.ReportUnknownModifierCause)
            {
                continue;
            }

            string displayName = GetDisplayName(candidate);
            advisories.Add(new(
                identity,
                displayName,
                normalRank,
                Math.Max(0, candidate.DurationSeconds),
                NormalizeDistance(candidate.DistanceMeters),
                impacts,
                BuildMessage(displayName, impacts, remains),
                remains ? modifiedRank : null));
        }

        return advisories;
    }

    private static IEnumerable<(RouteCandidateMetrics Candidate, int Rank)> RankNormalCandidates(
        IReadOnlyList<RouteCandidateMetrics> candidates,
        RoutePreferenceGoal goal,
        RoutePreferenceWeights weights,
        int limit)
    {
        RoutePreferenceRanking ranking = RoutePreferenceRanker.Rank(
            candidates.Select(static (candidate, index) => new RoutePreferenceCandidate(
                index,
                TrafficAwareRerouteRanker.AdjustedEtaSeconds(candidate, TrafficPolicy.Disabled),
                NormalizeDistance(candidate.DistanceMeters),
                FrictionModel.Score(candidate, TrafficPolicy.Disabled).TotalCost)).ToArray(),
            goal,
            weights,
            maxAlternatives: Math.Max(0, limit - 1));
        IEnumerable<int> indexes = new[] { ranking.Best.Index }
            .Concat(ranking.Alternatives.Select(static alternative => alternative.Index))
            .Take(limit);
        int rank = 1;
        foreach (int index in indexes)
        {
            yield return (candidates[index], rank++);
        }
    }

    private static string GetDisplayName(RouteCandidateMetrics candidate)
        => candidate.RouteLabels?.FirstOrDefault(static label => !string.IsNullOrWhiteSpace(label))
            ?? candidate.RouteKey
            ?? string.Format(CultureInfo.InvariantCulture, "{0} route {1}", candidate.ProviderId, candidate.Index);

    private static string BuildMessage(
        string displayName,
        IReadOnlyList<RouteModifierImpact> impacts,
        bool remainsInModifiedSet)
    {
        string outcome = remainsInModifiedSet ? "deprioritized" : "excluded";
        if (impacts.Count == 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Normal {0} navigation {1} because active route modifiers changed the modified route set.",
                displayName,
                outcome);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Normal {0} navigation {1} because {2}.",
            displayName,
            outcome,
            string.Join("; ", impacts.Select(FormatImpact)));
    }

    private static string FormatImpact(RouteModifierImpact impact)
    {
        string reason = impact.Kind switch
        {
            RouteModifierImpactKind.RoadClosure => "road closure",
            RouteModifierImpactKind.TrafficDelay => "traffic delay",
            RouteModifierImpactKind.Incident => "incident",
            RouteModifierImpactKind.Restriction => "restriction",
            RouteModifierImpactKind.Avoidance => "avoidance setting",
            _ => "route modifier",
        };
        return string.IsNullOrWhiteSpace(impact.Description)
            ? reason
            : string.Format(CultureInfo.InvariantCulture, "{0}: {1}", reason, impact.Description);
    }

    private static double NormalizeDistance(double value)
        => double.IsFinite(value) ? Math.Max(0d, value) : double.MaxValue;
}
