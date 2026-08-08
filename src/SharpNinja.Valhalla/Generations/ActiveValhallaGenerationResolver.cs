using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Generations;

public sealed record ValhallaGenerationFreshnessPolicy(
    TimeSpan MaximumTrafficAge,
    TimeSpan MaximumClosureAge)
{
    public static ValhallaGenerationFreshnessPolicy Default { get; } =
        new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));
}

public enum ActiveValhallaGenerationStatus
{
    Available = 0,
    BaseGraphUnavailable = 1,
    TrafficUnavailable = 2,
    ClosureUnavailable = 3,
}

public sealed record ActiveValhallaGenerationSet(
    ValhallaGraphGenerationManifest BaseGraph,
    ValhallaOverlayGenerationManifest Overlay)
{
    public TrafficPolicy TrafficPolicy =>
        Overlay.Policy == TrafficSnapshotPolicy.Enabled
            ? TrafficPolicy.Enabled
            : TrafficPolicy.Disabled;

    public ValhallaRouteGenerationStamp Stamp { get; } = new(
        BaseGraph.RegionId,
        BaseGraph.GenerationId,
        Overlay.GenerationId,
        Overlay.Policy,
        Overlay.CohortId,
        BaseGraph.GraphSha256,
        Overlay.TrafficSourceVersion,
        Overlay.ClosureSourceVersion);
}

public sealed record ActiveValhallaGenerationResolution(
    ActiveValhallaGenerationStatus Status,
    ActiveValhallaGenerationSet? GenerationSet,
    ActiveValhallaGenerationSet? ClosureOnlyFallback,
    ValhallaGenerationFailureCode? FailureCode,
    string? Diagnostic)
{
    public bool IsAvailable => Status == ActiveValhallaGenerationStatus.Available;
}

public sealed class ActiveValhallaGenerationResolver
{
    private readonly ValhallaGenerationFreshnessPolicy _freshness;

    public ActiveValhallaGenerationResolver(ValhallaGenerationFreshnessPolicy? freshness = null)
    {
        _freshness = freshness ?? ValhallaGenerationFreshnessPolicy.Default;
        if (_freshness.MaximumTrafficAge <= TimeSpan.Zero
            || _freshness.MaximumClosureAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(freshness),
                "Generation freshness windows must be positive.");
        }
    }

    public ActiveValhallaGenerationResolution Resolve(
        ValhallaGenerationCohortManifest cohort,
        TrafficSnapshotPolicy requestedPolicy,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        DateTimeOffset now = nowUtc.ToUniversalTime();

        if (cohort.BaseGraph.FreshnessDeadlineUtc <= now)
        {
            return Failure(
                ActiveValhallaGenerationStatus.BaseGraphUnavailable,
                ValhallaGenerationFailureCode.BaseGraphUnavailable,
                "The active regional base graph is stale.");
        }

        ValhallaOverlayGenerationManifest closureOnly = cohort.ClosureOnly;
        if (IsClosureStale(closureOnly, now))
        {
            return Failure(
                ActiveValhallaGenerationStatus.ClosureUnavailable,
                ValhallaGenerationFailureCode.ClosureUnavailable,
                "Closure data is stale or unavailable.");
        }

        var closureSet = new ActiveValhallaGenerationSet(cohort.BaseGraph, closureOnly);
        if (requestedPolicy == TrafficSnapshotPolicy.ClosureOnly)
        {
            return Available(closureSet);
        }

        ValhallaOverlayGenerationManifest enabled = cohort.TrafficEnabled;
        if (enabled.TrafficDataAsOfUtc is null
            || enabled.TrafficDataAsOfUtc.Value + _freshness.MaximumTrafficAge <= now
            || enabled.ExpiresAtUtc <= now)
        {
            return new(
                ActiveValhallaGenerationStatus.TrafficUnavailable,
                GenerationSet: null,
                ClosureOnlyFallback: closureSet,
                FailureCode: ValhallaGenerationFailureCode.TrafficUnavailable,
                Diagnostic: "Traffic data is stale or unavailable; closure-only routing remains valid.");
        }

        if (IsClosureStale(enabled, now))
        {
            return Failure(
                ActiveValhallaGenerationStatus.ClosureUnavailable,
                ValhallaGenerationFailureCode.ClosureUnavailable,
                "The enabled overlay does not contain current closure data.");
        }

        return Available(new ActiveValhallaGenerationSet(cohort.BaseGraph, enabled));
    }

    private bool IsClosureStale(
        ValhallaOverlayGenerationManifest overlay,
        DateTimeOffset nowUtc) =>
        overlay.ClosureDataAsOfUtc + _freshness.MaximumClosureAge <= nowUtc
        || overlay.ExpiresAtUtc <= nowUtc;

    private static ActiveValhallaGenerationResolution Available(
        ActiveValhallaGenerationSet set) =>
        new(
            ActiveValhallaGenerationStatus.Available,
            set,
            ClosureOnlyFallback: null,
            FailureCode: null,
            Diagnostic: null);

    private static ActiveValhallaGenerationResolution Failure(
        ActiveValhallaGenerationStatus status,
        ValhallaGenerationFailureCode failureCode,
        string diagnostic) =>
        new(status, null, null, failureCode, diagnostic);
}

/// <summary>
/// Pins one exact compatible generation set for the lifetime of a routing acquisition.
/// Later catalog promotions cannot mutate this immutable lease.
/// </summary>
public sealed class ValhallaGenerationLease
{
    public ValhallaGenerationLease(ActiveValhallaGenerationSet generationSet)
    {
        GenerationSet = generationSet ?? throw new ArgumentNullException(nameof(generationSet));
    }

    public ActiveValhallaGenerationSet GenerationSet { get; }

    public ValhallaRouteGenerationStamp Stamp => GenerationSet.Stamp;
}
