using System.Globalization;

namespace SharpNinja.Valhalla.Traffic.Routing;

public static class RoutePreferenceRanker
{
    private const double Epsilon = 0.000_001d;

    public static RoutePreferenceRanking Rank(
        IReadOnlyList<RoutePreferenceCandidate> candidates,
        RoutePreferenceGoal goal,
        RoutePreferenceWeights weights,
        RoutePreferenceNearTieThresholds? thresholds = null,
        int maxAlternatives = 2)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(weights);
        thresholds ??= RoutePreferenceNearTieThresholds.Default;
        if (candidates.Count == 0)
        {
            var empty = new RoutePreferenceCandidate(0, 0, 0d, 0d);
            return new RoutePreferenceRanking(goal, empty, [], "No route candidates were available.");
        }

        RoutePreferenceCandidate[] materialized = candidates.Select(NormalizeCandidate).ToArray();
        double bestPrimary = materialized.Min(candidate => GetMetric(candidate, goal));
        RoutePreferenceCandidate[] nearTies = materialized
            .Where(candidate => IsNearTie(GetMetric(candidate, goal), bestPrimary, GetThreshold(goal, thresholds)))
            .ToArray();
        RoutePreferenceCandidate[] pool = nearTies.Length == 0 ? materialized : nearTies;
        RoutePreferenceMetricRanges ranges = RoutePreferenceMetricRanges.From(pool);
        RoutePreferenceCandidate best = pool
            .OrderBy(candidate => PreferenceScore(candidate, goal, weights, ranges))
            .ThenBy(candidate => GetMetric(candidate, goal))
            .ThenBy(candidate => candidate.Index)
            .First();
        RoutePreferenceCandidate[] alternatives = materialized
            .Where(candidate => candidate.Index != best.Index)
            .OrderBy(candidate => nearTies.Any(nearTie => nearTie.Index == candidate.Index) ? 0 : 1)
            .ThenBy(candidate => PreferenceScore(candidate, goal, weights, ranges))
            .ThenBy(candidate => GetMetric(candidate, goal))
            .ThenBy(candidate => candidate.Index)
            .Take(Math.Max(0, maxAlternatives))
            .ToArray();
        string reason = nearTies.Length > 1
            ? string.Format(
                CultureInfo.InvariantCulture,
                "Selected candidate {0} for {1} from {2} near-tie candidates using secondary preference weights: fastest {3:0.###}, shortest {4:0.###}, easiest {5:0.###}.",
                best.Index,
                goal,
                nearTies.Length,
                Math.Max(0d, weights.Fastest),
                Math.Max(0d, weights.Shortest),
                Math.Max(0d, weights.Easiest))
            : string.Format(
                CultureInfo.InvariantCulture,
                "Selected candidate {0} for {1}; no near-tie candidate was close enough to invoke secondary preferences.",
                best.Index,
                goal);
        return new RoutePreferenceRanking(goal, best, alternatives, reason);
    }

    private static RoutePreferenceCandidate NormalizeCandidate(RoutePreferenceCandidate candidate)
        => candidate with
        {
            DurationSeconds = Math.Max(0, candidate.DurationSeconds),
            DistanceMeters = double.IsFinite(candidate.DistanceMeters) ? Math.Max(0d, candidate.DistanceMeters) : double.MaxValue,
            FrictionScore = double.IsFinite(candidate.FrictionScore) ? Math.Max(0d, candidate.FrictionScore) : double.MaxValue,
        };

    private static bool IsNearTie(double value, double best, RoutePreferenceNearTieThreshold threshold)
    {
        double allowed = Math.Max(Math.Max(0d, threshold.Absolute), Math.Abs(best) * Math.Max(0d, threshold.Ratio));
        return value <= best + allowed;
    }

    private static RoutePreferenceNearTieThreshold GetThreshold(
        RoutePreferenceGoal goal,
        RoutePreferenceNearTieThresholds thresholds)
        => goal switch
        {
            RoutePreferenceGoal.Fastest => new(thresholds.DurationSeconds, thresholds.DurationRatio),
            RoutePreferenceGoal.Shortest => new(thresholds.DistanceMeters, thresholds.DistanceRatio),
            RoutePreferenceGoal.Easiest => new(thresholds.FrictionScore, thresholds.FrictionRatio),
            _ => new(thresholds.DurationSeconds, thresholds.DurationRatio),
        };

    private static double PreferenceScore(
        RoutePreferenceCandidate candidate,
        RoutePreferenceGoal goal,
        RoutePreferenceWeights weights,
        RoutePreferenceMetricRanges ranges)
    {
        double score = 0d;
        double total = 0d;
        Add(RoutePreferenceGoal.Fastest, Math.Max(0d, weights.Fastest), ranges.Normalize(candidate.DurationSeconds, ranges.MinDuration, ranges.MaxDuration));
        Add(RoutePreferenceGoal.Shortest, Math.Max(0d, weights.Shortest), ranges.Normalize(candidate.DistanceMeters, ranges.MinDistance, ranges.MaxDistance));
        Add(RoutePreferenceGoal.Easiest, Math.Max(0d, weights.Easiest), ranges.Normalize(candidate.FrictionScore, ranges.MinFriction, ranges.MaxFriction));
        return total > Epsilon ? score / total : GetMetric(candidate, goal);

        void Add(RoutePreferenceGoal metric, double weight, double value)
        {
            if (metric == goal || weight <= Epsilon)
            {
                return;
            }

            score += value * weight;
            total += weight;
        }
    }

    private static double GetMetric(RoutePreferenceCandidate candidate, RoutePreferenceGoal goal)
        => goal switch
        {
            RoutePreferenceGoal.Fastest => candidate.DurationSeconds,
            RoutePreferenceGoal.Shortest => candidate.DistanceMeters,
            RoutePreferenceGoal.Easiest => candidate.FrictionScore,
            _ => candidate.DurationSeconds,
        };

    private sealed record RoutePreferenceMetricRanges(
        double MinDuration,
        double MaxDuration,
        double MinDistance,
        double MaxDistance,
        double MinFriction,
        double MaxFriction)
    {
        public static RoutePreferenceMetricRanges From(IReadOnlyList<RoutePreferenceCandidate> candidates)
            => new(
                candidates.Min(static candidate => (double)candidate.DurationSeconds),
                candidates.Max(static candidate => (double)candidate.DurationSeconds),
                candidates.Min(static candidate => candidate.DistanceMeters),
                candidates.Max(static candidate => candidate.DistanceMeters),
                candidates.Min(static candidate => candidate.FrictionScore),
                candidates.Max(static candidate => candidate.FrictionScore));

        public double Normalize(double value, double min, double max)
        {
            if (!double.IsFinite(value))
            {
                return 1d;
            }

            double range = max - min;
            return range <= Epsilon ? 0d : Math.Clamp((value - min) / range, 0d, 1d);
        }
    }
}

public sealed record RoutePreferenceCandidate(int Index, int DurationSeconds, double DistanceMeters, double FrictionScore);
public sealed record RoutePreferenceRanking(RoutePreferenceGoal Goal, RoutePreferenceCandidate Best, IReadOnlyList<RoutePreferenceCandidate> Alternatives, string Reason);
public sealed record RoutePreferenceWeights(double Fastest, double Shortest, double Easiest)
{
    public static RoutePreferenceWeights Balanced { get; } = new(1d, 1d, 1d);
}

public sealed record RoutePreferenceNearTieThresholds(
    int DurationSeconds,
    double DistanceMeters,
    double FrictionScore,
    double DurationRatio = 0.10d,
    double DistanceRatio = 0.10d,
    double FrictionRatio = 0.10d)
{
    public static RoutePreferenceNearTieThresholds Default { get; } = new(90, 1_000d, 10d);
}

public enum RoutePreferenceGoal
{
    Fastest = 0,
    Shortest = 1,
    Easiest = 2,
}

internal sealed record RoutePreferenceNearTieThreshold(double Absolute, double Ratio);
