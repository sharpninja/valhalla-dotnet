namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>Deterministic provider-neutral route friction scorer.</summary>
public static partial class FrictionModel
{
    private const double IncidentPenaltySeconds = 180d;

    public static RouteFrictionScore Score(RouteCandidateMetrics candidate, TrafficPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        TrafficPolicy effectivePolicy = policy ?? TrafficPolicy.Enabled;
        int durationSeconds = Math.Max(0, candidate.DurationSeconds);
        double distanceMeters = double.IsFinite(candidate.DistanceMeters)
            ? Math.Max(0d, candidate.DistanceMeters)
            : double.MaxValue;
        int delay = effectivePolicy.IncludeTrafficDelayInFriction
            ? Math.Max(0, candidate.TrafficDelaySeconds)
            : 0;
        double incidents = effectivePolicy.IncludeTrafficDelayInFriction
            ? Math.Max(0, candidate.IncidentCount) * IncidentPenaltySeconds
            : 0d;
        double staticFriction = double.IsFinite(candidate.StaticFrictionScore)
            ? Math.Max(0d, candidate.StaticFrictionScore)
            : double.MaxValue;

        return new RouteFrictionScore(
            staticFriction + delay + incidents,
            durationSeconds,
            distanceMeters,
            staticFriction,
            delay,
            incidents);
    }

    public static int PickBestRouteIndex(
        IReadOnlyList<RouteCandidateMetrics> candidates,
        TrafficPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return 0;
        }

        TrafficPolicy effectivePolicy = policy ?? TrafficPolicy.Enabled;
        RouteCandidateMetrics best = candidates[0];
        RouteFrictionScore bestScore = Score(best, effectivePolicy);
        for (int i = 1; i < candidates.Count; i++)
        {
            RouteCandidateMetrics current = candidates[i];
            RouteFrictionScore currentScore = Score(current, effectivePolicy);
            if (currentScore.TotalCost < bestScore.TotalCost ||
                (currentScore.TotalCost == bestScore.TotalCost && current.Index < best.Index))
            {
                best = current;
                bestScore = currentScore;
            }
        }

        return best.Index;
    }
}

public sealed record RouteFrictionScore(
    double TotalCost,
    int DurationSeconds,
    double DistanceMeters,
    double StaticFrictionScore,
    int TrafficDelaySeconds,
    double IncidentPenaltySeconds);
