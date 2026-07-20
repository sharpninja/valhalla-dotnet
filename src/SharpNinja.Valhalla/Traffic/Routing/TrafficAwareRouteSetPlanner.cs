using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic.Routing;

public sealed record TrafficAwareRouteSetRequest(
    OsmRouteRequest RouteRequest,
    TrafficSnapshotReference ActiveSnapshot,
    IReadOnlyList<TrafficRouteModifierSource> ModifierSources,
    RoutePreferenceGoal PreferenceGoal = RoutePreferenceGoal.Fastest,
    RoutePreferenceWeights? PreferenceWeights = null,
    string? CurrentRouteIdentity = null,
    int MinimumAutomaticSwitchImprovementSeconds = 120,
    double MinimumAutomaticSwitchImprovementRatio = 0.10d);

public enum TrafficAwareRouteSetStatus
{
    Success = 0,
    AdvisoryOnly = 1,
    BaselinePassFailed = 2,
    ActivePassFailed = 3,
    NoSafeRouteAvailable = 4,
}

public sealed record TrafficAwareRouteAdvisory
{
    public TrafficAwareRouteAdvisory(
        string routeIdentity,
        string message,
        IReadOnlyList<RouteModifierImpact> impacts,
        IReadOnlyList<string> providerIds,
        IReadOnlyList<string> sourceEventIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(impacts);
        ArgumentNullException.ThrowIfNull(providerIds);
        ArgumentNullException.ThrowIfNull(sourceEventIds);

        RouteIdentity = routeIdentity;
        Message = message;
        Impacts = Array.AsReadOnly(impacts.ToArray());
        ProviderIds = Array.AsReadOnly(providerIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        SourceEventIds = Array.AsReadOnly(sourceEventIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    public string RouteIdentity { get; }

    public string Message { get; }

    public IReadOnlyList<RouteModifierImpact> Impacts { get; }

    public IReadOnlyList<string> ProviderIds { get; }

    public IReadOnlyList<string> SourceEventIds { get; }
}

public sealed record TrafficAwareRouteSetPlan
{
    public TrafficAwareRouteSetPlan(
        TrafficAwareRouteSetStatus status,
        DateTimeOffset departureTimeUtc,
        IReadOnlyList<OsmRouteCandidate> baselineCandidates,
        IReadOnlyList<OsmRouteCandidate> activeCandidates,
        OsmRouteCandidate? selectedCandidate,
        string? selectedRouteIdentity,
        bool automaticReplacement,
        IReadOnlyList<TrafficAwareRouteAdvisory> advisories,
        string? baselineError = null,
        string? activeError = null,
        TrafficSnapshotFailure? activeSnapshotFailure = null)
    {
        ArgumentNullException.ThrowIfNull(baselineCandidates);
        ArgumentNullException.ThrowIfNull(activeCandidates);
        ArgumentNullException.ThrowIfNull(advisories);

        Status = status;
        DepartureTimeUtc = departureTimeUtc;
        BaselineCandidates = Array.AsReadOnly(baselineCandidates.ToArray());
        ActiveCandidates = Array.AsReadOnly(activeCandidates.ToArray());
        SelectedCandidate = selectedCandidate;
        SelectedRouteIdentity = selectedRouteIdentity;
        AutomaticReplacement = automaticReplacement;
        Advisories = Array.AsReadOnly(advisories.ToArray());
        BaselineError = baselineError;
        ActiveError = activeError;
        ActiveSnapshotFailure = activeSnapshotFailure;
    }

    public TrafficAwareRouteSetStatus Status { get; }

    public DateTimeOffset DepartureTimeUtc { get; }

    public IReadOnlyList<OsmRouteCandidate> BaselineCandidates { get; }

    public IReadOnlyList<OsmRouteCandidate> ActiveCandidates { get; }

    public OsmRouteCandidate? SelectedCandidate { get; }

    public string? SelectedRouteIdentity { get; }

    public bool AutomaticReplacement { get; }

    public IReadOnlyList<TrafficAwareRouteAdvisory> Advisories { get; }

    public string? BaselineError { get; }

    public string? ActiveError { get; }

    public TrafficSnapshotFailure? ActiveSnapshotFailure { get; }

    public bool ActivePassSucceeded =>
        Status is TrafficAwareRouteSetStatus.Success or TrafficAwareRouteSetStatus.AdvisoryOnly;
}

public interface ITrafficAwareRouteSetPlanner
{
    Task<TrafficAwareRouteSetPlan> PlanAsync(
        TrafficAwareRouteSetRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs one unmodified and one snapshot-backed engine route pass, then applies conservative
/// automatic-switch policy without disguising a failed active pass as traffic-aware success.
/// </summary>
public sealed class TrafficAwareRouteSetPlanner : ITrafficAwareRouteSetPlanner
{
    private readonly IOsmRoutingClient _routingClient;
    private readonly TimeProvider _timeProvider;

    public TrafficAwareRouteSetPlanner(
        IOsmRoutingClient routingClient,
        TimeProvider? timeProvider = null)
    {
        _routingClient = routingClient ?? throw new ArgumentNullException(nameof(routingClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TrafficAwareRouteSetPlan> PlanAsync(
        TrafficAwareRouteSetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RouteRequest);
        ArgumentNullException.ThrowIfNull(request.ActiveSnapshot);
        ArgumentNullException.ThrowIfNull(request.ModifierSources);
        if (request.MinimumAutomaticSwitchImprovementSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The automatic-switch improvement threshold cannot be negative.");
        }

        if (!double.IsFinite(request.MinimumAutomaticSwitchImprovementRatio)
            || request.MinimumAutomaticSwitchImprovementRatio < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The automatic-switch improvement ratio must be finite and nonnegative.");
        }

        DateTimeOffset departure =
            (request.RouteRequest.DepartureTimeUtc ?? _timeProvider.GetUtcNow()).ToUniversalTime();
        OsmRouteRequest baselineRequest = request.RouteRequest with
        {
            TrafficSnapshot = null,
            DepartureTimeUtc = departure,
        };
        OsmRouteResult baseline = await _routingClient.CalculateRouteAsync(
            baselineRequest,
            cancellationToken).ConfigureAwait(false);
        if (!Succeeded(baseline))
        {
            return new TrafficAwareRouteSetPlan(
                TrafficAwareRouteSetStatus.BaselinePassFailed,
                departure,
                baseline.Routes,
                [],
                null,
                null,
                false,
                [],
                baseline.Error ?? "baseline_no_routes");
        }

        OsmRouteRequest activeRequest = request.RouteRequest with
        {
            TrafficSnapshot = request.ActiveSnapshot,
            DepartureTimeUtc = departure,
        };
        OsmRouteResult active = await _routingClient.CalculateRouteAsync(
            activeRequest,
            cancellationToken).ConfigureAwait(false);

        RoutePreferenceWeights weights = request.PreferenceWeights ?? RoutePreferenceWeights.Balanced;
        RouteCandidateMetrics[] baselineMetrics = ToMetrics(baseline.Routes);
        IReadOnlyList<TrafficAwareRouteAdvisory> advisories = CreateAdvisories(
            baselineMetrics,
            Succeeded(active) ? ToMetrics(active.Routes) : [],
            request.ModifierSources,
            request.PreferenceGoal,
            weights);
        if (!Succeeded(active))
        {
            bool hardDenied = CurrentRouteIsHardDenied(
                request.CurrentRouteIdentity,
                baselineMetrics,
                request.ModifierSources);
            bool noSafeRoute = hardDenied
                && active.Routes.Count == 0
                && active.TrafficSnapshotFailure is null
                && string.IsNullOrWhiteSpace(active.Error);
            return new TrafficAwareRouteSetPlan(
                noSafeRoute
                    ? TrafficAwareRouteSetStatus.NoSafeRouteAvailable
                    : TrafficAwareRouteSetStatus.ActivePassFailed,
                departure,
                baseline.Routes,
                active.Routes,
                noSafeRoute
                    ? null
                    : CurrentBaselineCandidate(request.CurrentRouteIdentity, baseline.Routes, baselineMetrics),
                noSafeRoute ? null : request.CurrentRouteIdentity,
                false,
                advisories,
                activeError: noSafeRoute ? "no_safe_route_available" : active.Error ?? "active_no_routes",
                activeSnapshotFailure: active.TrafficSnapshotFailure);
        }

        RouteCandidateMetrics[] activeMetrics = ToMetrics(active.Routes);
        int[] activeRanking = Rank(activeMetrics, request.PreferenceGoal, weights, TrafficPolicy.Enabled);
        string? currentIdentity = request.CurrentRouteIdentity;
        bool currentHardDenied = CurrentRouteIsHardDenied(
            currentIdentity,
            baselineMetrics,
            request.ModifierSources);
        int selectedActiveIndex = activeRanking[0];
        bool automaticReplacement = false;
        TrafficAwareRouteSetStatus status = TrafficAwareRouteSetStatus.Success;

        if (currentIdentity is null)
        {
            // Initial planning has no incumbent route to replace; use the active best route.
        }
        else if (currentHardDenied)
        {
            int safeIndex = activeRanking.FirstOrDefault(
                index => !string.Equals(
                    RouteIdentity.Create(activeMetrics[index]),
                    currentIdentity,
                    StringComparison.Ordinal),
                -1);
            if (safeIndex < 0)
            {
                return new TrafficAwareRouteSetPlan(
                    TrafficAwareRouteSetStatus.NoSafeRouteAvailable,
                    departure,
                    baseline.Routes,
                    active.Routes,
                    null,
                    null,
                    false,
                    advisories,
                    activeError: "no_safe_route_available");
            }

            selectedActiveIndex = safeIndex;
            automaticReplacement = true;
        }
        else
        {
            int incumbentIndex = Array.FindIndex(
                activeMetrics,
                candidate => string.Equals(RouteIdentity.Create(candidate), currentIdentity, StringComparison.Ordinal));
            if (incumbentIndex < 0)
            {
                OsmRouteCandidate? incumbent = CurrentBaselineCandidate(
                    currentIdentity,
                    baseline.Routes,
                    baselineMetrics);
                return new TrafficAwareRouteSetPlan(
                    TrafficAwareRouteSetStatus.AdvisoryOnly,
                    departure,
                    baseline.Routes,
                    active.Routes,
                    incumbent,
                    currentIdentity,
                    false,
                    advisories,
                    activeError: "incumbent_not_returned_by_active_pass");
            }

            if (selectedActiveIndex != incumbentIndex)
            {
                int incumbentEta = TrafficAwareRerouteRanker.AdjustedEtaSeconds(
                    activeMetrics[incumbentIndex],
                    TrafficPolicy.Enabled);
                int replacementEta = TrafficAwareRerouteRanker.AdjustedEtaSeconds(
                    activeMetrics[selectedActiveIndex],
                    TrafficPolicy.Enabled);
                int improvementSeconds = Math.Max(0, incumbentEta - replacementEta);
                double improvementRatio = incumbentEta == 0
                    ? 0d
                    : improvementSeconds / (double)incumbentEta;
                bool thresholdMet =
                    improvementSeconds >= request.MinimumAutomaticSwitchImprovementSeconds
                    && improvementRatio >= request.MinimumAutomaticSwitchImprovementRatio;
                if (thresholdMet)
                {
                    automaticReplacement = true;
                }
                else
                {
                    selectedActiveIndex = incumbentIndex;
                    status = TrafficAwareRouteSetStatus.AdvisoryOnly;
                }
            }
        }

        string selectedIdentity = RouteIdentity.Create(activeMetrics[selectedActiveIndex]);
        return new TrafficAwareRouteSetPlan(
            status,
            departure,
            baseline.Routes,
            active.Routes,
            active.Routes[selectedActiveIndex],
            selectedIdentity,
            automaticReplacement,
            advisories);
    }

    private static bool Succeeded(OsmRouteResult result) =>
        result.TrafficSnapshotFailure is null
        && string.IsNullOrWhiteSpace(result.Error)
        && result.Routes.Count != 0;

    private static RouteCandidateMetrics[] ToMetrics(IReadOnlyList<OsmRouteCandidate> candidates) =>
        candidates.Select(static (candidate, index) => new RouteCandidateMetrics(
            "SharpNinja.Valhalla",
            index,
            candidate.DistanceMeters,
            candidate.DurationSeconds,
            TrafficDelaySeconds: candidate.EngineAppliedTrafficDelaySeconds,
            ManeuverCount: candidate.FrictionInputs.ManeuverCount,
            TollManeuverCount: candidate.FrictionInputs.TollManeuverCount,
            HighwayManeuverCount: candidate.FrictionInputs.HighwayManeuverCount,
            FerryManeuverCount: candidate.FrictionInputs.FerryManeuverCount,
            HasToll: candidate.FrictionInputs.HasToll,
            HasHighway: candidate.FrictionInputs.HasHighway,
            HasFerry: candidate.FrictionInputs.HasFerry,
            DirectedEdgeIds: candidate.DirectedEdgeIds,
            DurationSource: candidate.DurationSource)).ToArray();

    private static int[] Rank(
        IReadOnlyList<RouteCandidateMetrics> candidates,
        RoutePreferenceGoal goal,
        RoutePreferenceWeights weights,
        TrafficPolicy policy)
    {
        RoutePreferenceRanking ranking = RoutePreferenceRanker.Rank(
            candidates.Select((candidate, index) => new RoutePreferenceCandidate(
                index,
                TrafficAwareRerouteRanker.AdjustedEtaSeconds(candidate, policy),
                candidate.DistanceMeters,
                FrictionModel.Score(candidate, policy).TotalCost)).ToArray(),
            goal,
            weights,
            maxAlternatives: Math.Max(0, candidates.Count - 1));
        return new[] { ranking.Best.Index }
            .Concat(ranking.Alternatives.Select(static candidate => candidate.Index))
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<TrafficAwareRouteAdvisory> CreateAdvisories(
        IReadOnlyList<RouteCandidateMetrics> baseline,
        IReadOnlyList<RouteCandidateMetrics> active,
        IReadOnlyList<TrafficRouteModifierSource> sources,
        RoutePreferenceGoal goal,
        RoutePreferenceWeights weights)
    {
        if (baseline.Count == 0 || sources.Count == 0)
        {
            return [];
        }

        int[] baselineRanking = Rank(baseline, goal, weights, TrafficPolicy.Disabled);
        int[] activeRanking = active.Count == 0
            ? []
            : Rank(active, goal, weights, TrafficPolicy.Enabled);
        var activeRanks = activeRanking
            .Select((candidateIndex, rank) => new
            {
                Identity = RouteIdentity.Create(active[candidateIndex]),
                Rank = rank,
            })
            .GroupBy(static item => item.Identity, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Min(static item => item.Rank),
                StringComparer.Ordinal);
        var advisories = new List<TrafficAwareRouteAdvisory>();
        for (int normalRank = 0; normalRank < baselineRanking.Length; normalRank++)
        {
            RouteCandidateMetrics candidate = baseline[baselineRanking[normalRank]];
            string identity = RouteIdentity.Create(candidate);
            bool remains = activeRanks.TryGetValue(identity, out int activeRank);
            if (remains && activeRank <= normalRank)
            {
                continue;
            }

            TrafficRouteModifierSource[] matchingSources = MatchingSources(candidate, identity, sources);
            if (matchingSources.Length == 0)
            {
                continue;
            }

            string[] providers = matchingSources
                .SelectMany(static source => source.ProviderIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static provider => provider, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string sourceText = providers.Length == 0 ? "an unavailable provider" : string.Join(", ", providers);
            string change = remains ? $"deprioritized to rank {activeRank + 1}" : "excluded";
            advisories.Add(new TrafficAwareRouteAdvisory(
                identity,
                $"Normal route {identity} was {change} by active data from {sourceText}.",
                matchingSources.Select(source => source.Impact with { RouteKey = identity }).ToArray(),
                providers,
                matchingSources.SelectMany(static source => source.SourceEventIds).ToArray()));
        }

        return advisories;
    }

    private static bool CurrentRouteIsHardDenied(
        string? currentIdentity,
        IReadOnlyList<RouteCandidateMetrics> baseline,
        IReadOnlyList<TrafficRouteModifierSource> sources)
    {
        if (currentIdentity is null)
        {
            return false;
        }

        RouteCandidateMetrics? current = baseline.FirstOrDefault(
            candidate => string.Equals(RouteIdentity.Create(candidate), currentIdentity, StringComparison.Ordinal));
        return current is not null
            && MatchingSources(current, currentIdentity, sources)
                .Any(static source => source.Impact.HardDeny);
    }

    private static TrafficRouteModifierSource[] MatchingSources(
        RouteCandidateMetrics candidate,
        string identity,
        IReadOnlyList<TrafficRouteModifierSource> sources)
    {
        HashSet<ulong>? routeEdges = candidate.DirectedEdgeIds?.ToHashSet();
        return sources.Where(source =>
                string.Equals(source.Impact.RouteKey, identity, StringComparison.Ordinal)
                || (routeEdges is not null
                    && source.AffectedEdges.Any(edge => routeEdges.Contains(edge.CanonicalDirectedEdgeId))))
            .ToArray();
    }

    private static OsmRouteCandidate? CurrentBaselineCandidate(
        string? currentIdentity,
        IReadOnlyList<OsmRouteCandidate> candidates,
        IReadOnlyList<RouteCandidateMetrics> metrics)
    {
        if (currentIdentity is null)
        {
            return null;
        }

        int index = metrics
            .Select((candidate, candidateIndex) => new { candidate, candidateIndex })
            .Where(item => string.Equals(
                RouteIdentity.Create(item.candidate),
                currentIdentity,
                StringComparison.Ordinal))
            .Select(static item => item.candidateIndex)
            .DefaultIfEmpty(-1)
            .First();
        return index < 0 ? null : candidates[index];
    }
}
