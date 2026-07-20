namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>Ranks already-computed route candidates by policy-adjusted ETA.</summary>
public static class TrafficAwareRerouteRanker
{
    public static int PickBestRouteIndex(
        IReadOnlyList<RouteCandidateMetrics> candidates,
        TrafficPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        TrafficPolicy effectivePolicy = policy ?? TrafficPolicy.Enabled;
        if (candidates.Count == 0)
        {
            return 0;
        }

        RouteCandidateMetrics best = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            RouteCandidateMetrics current = candidates[i];
            if (IsBetter(current, best, effectivePolicy))
            {
                best = current;
            }
        }

        return best.Index;
    }

    public static int AdjustedEtaSeconds(RouteCandidateMetrics candidate, TrafficPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        int baseDuration = Math.Max(0, candidate.DurationSeconds);
        if (!policy.IncludeTrafficDelayInEta || BaseDurationIncludesTraffic(candidate.DurationSource))
        {
            return baseDuration;
        }

        int delaySeconds = Math.Max(0, candidate.TrafficDelaySeconds);
        return baseDuration >= int.MaxValue - delaySeconds
            ? int.MaxValue
            : baseDuration + delaySeconds;
    }

    internal static bool BaseDurationIncludesTraffic(RouteDurationSource source)
        => source is RouteDurationSource.ProviderTrafficAdjusted or RouteDurationSource.ValhallaTrafficTileAdjusted;

    private static bool IsBetter(RouteCandidateMetrics current, RouteCandidateMetrics best, TrafficPolicy policy)
    {
        int currentEta = AdjustedEtaSeconds(current, policy);
        int bestEta = AdjustedEtaSeconds(best, policy);
        if (currentEta != bestEta)
        {
            return currentEta < bestEta;
        }

        if (current.DurationSeconds != best.DurationSeconds)
        {
            return current.DurationSeconds < best.DurationSeconds;
        }

        double currentDistance = NormalizeDistance(current.DistanceMeters);
        double bestDistance = NormalizeDistance(best.DistanceMeters);
        if (Math.Abs(currentDistance - bestDistance) > 0.001d)
        {
            return currentDistance < bestDistance;
        }

        return current.Index < best.Index;
    }

    private static double NormalizeDistance(double value)
        => double.IsFinite(value) ? Math.Max(0d, value) : double.MaxValue;
}
