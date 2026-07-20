using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>
/// UI-agnostic input for one engine route candidate and its graph-derived static evidence.
/// </summary>
public sealed record RouteSelectionCandidateInput(
    int Index,
    OsmRouteCandidate Candidate,
    ValhallaRouteTrafficControlCounts TrafficControls,
    RouteLaneFrictionProjection LaneProjection,
    string ProviderId,
    IReadOnlyList<string>? RouteLabels = null);

/// <summary>
/// DATA-layer request for evaluating traffic and structural friction once, then ranking all goals.
/// </summary>
public sealed record RouteSelectionRequest(
    IReadOnlyList<RouteSelectionCandidateInput> Candidates,
    NormalizedTrafficSnapshot TrafficSnapshot,
    TrafficPolicy TrafficPolicy,
    RoutePreferenceGoal PreferenceGoal,
    RoutePreferenceWeights PreferenceWeights)
{
    public RoutePreferenceNearTieThresholds? NearTieThresholds { get; init; }

    public RouteFrictionWeights? FrictionWeights { get; init; }

    public int MaxAlternatives { get; init; } = int.MaxValue;
}

public enum RouteSelectionProvenanceKind
{
    RouteIdentity = 0,
    TrafficEvent = 1,
    LaneTopology = 2,
    GraphTrafficControls = 3,
}

public sealed record RouteSelectionProvenance(
    RouteSelectionProvenanceKind Kind,
    string SourceId,
    string Description);

public enum RouteSelectionDecisionKind
{
    Selected = 0,
    Alternative = 1,
    Deprioritized = 2,
    Excluded = 3,
}

public enum RouteSelectionDecisionReason
{
    SelectedByPreference = 0,
    AlternativeByPreference = 1,
    DirectionSafeHardDeny = 2,
    CanonicalOverlayMismatch = 3,
    InfeasibleLaneChanges = 4,
    UnverifiedLaneTopology = 5,
    RankedBelowAlternativeLimit = 6,
    DuplicateCanonicalRoute = 7,
}

/// <summary>
/// One candidate after canonical traffic joining and structural friction composition.
/// </summary>
public sealed record RouteSelectionCandidateResult
{
    internal RouteSelectionCandidateResult(
        RouteSelectionCandidateInput input,
        RouteCandidateMetrics metrics,
        RouteTrafficEvaluation trafficEvaluation,
        RouteStructuralFrictionScore friction,
        int adjustedEtaSeconds,
        string routeIdentity,
        IReadOnlyList<RouteSelectionProvenance> provenance)
    {
        Input = input;
        Metrics = metrics;
        TrafficEvaluation = trafficEvaluation;
        Friction = friction;
        AdjustedEtaSeconds = adjustedEtaSeconds;
        RouteIdentity = routeIdentity;
        Provenance = Array.AsReadOnly(provenance.ToArray());
    }

    public RouteSelectionCandidateInput Input { get; }

    public int Index => Input.Index;

    public OsmRouteCandidate Candidate => Input.Candidate;

    public ValhallaRouteTrafficControlCounts TrafficControls => Input.TrafficControls;

    public RouteLaneFrictionProjection LaneProjection => Input.LaneProjection;

    public RouteCandidateMetrics Metrics { get; }

    public RouteTrafficEvaluation TrafficEvaluation { get; }

    public RouteStructuralFrictionScore Friction { get; }

    public int AdjustedEtaSeconds { get; }

    public string RouteIdentity { get; }

    public IReadOnlyList<RouteSelectionProvenance> Provenance { get; }
}

public sealed record RouteSelectionDecision(
    RouteSelectionCandidateResult Candidate,
    RouteSelectionDecisionKind Kind,
    RouteSelectionDecisionReason Reason,
    string Explanation);

/// <summary>Deterministic ranking and disposition of every input candidate for one goal.</summary>
public sealed record RouteSelectionRanking
{
    internal RouteSelectionRanking(
        RoutePreferenceGoal goal,
        IReadOnlyList<RouteSelectionCandidateResult> orderedCandidates,
        IReadOnlyList<RouteSelectionDecision> decisions,
        string reason,
        bool usesUnverifiedLaneTopology)
    {
        Goal = goal;
        OrderedCandidates = Array.AsReadOnly(orderedCandidates.ToArray());
        Decisions = Array.AsReadOnly(decisions.ToArray());
        Reason = reason;
        UsesUnverifiedLaneTopology = usesUnverifiedLaneTopology;
    }

    public RoutePreferenceGoal Goal { get; }

    public RouteSelectionCandidateResult? Best =>
        OrderedCandidates.Count == 0 ? null : OrderedCandidates[0];

    public IReadOnlyList<RouteSelectionCandidateResult> OrderedCandidates { get; }

    public IReadOnlyList<RouteSelectionDecision> Decisions { get; }

    public string Reason { get; }

    public bool UsesUnverifiedLaneTopology { get; }
}

/// <summary>Complete candidate assessments plus Fastest, Shortest, and Easiest rankings.</summary>
public sealed record RouteSelectionResult
{
    internal RouteSelectionResult(
        RoutePreferenceGoal preferenceGoal,
        IReadOnlyList<RouteSelectionCandidateResult> candidates,
        IReadOnlyList<RouteSelectionRanking> rankings)
    {
        PreferenceGoal = preferenceGoal;
        Candidates = Array.AsReadOnly(candidates.ToArray());
        Rankings = Array.AsReadOnly(rankings.ToArray());
    }

    public RoutePreferenceGoal PreferenceGoal { get; }

    public IReadOnlyList<RouteSelectionCandidateResult> Candidates { get; }

    public IReadOnlyList<RouteSelectionRanking> Rankings { get; }

    public RouteSelectionCandidateResult? Selected => GetRanking(PreferenceGoal).Best;

    public RouteSelectionRanking GetRanking(RoutePreferenceGoal goal)
        => Rankings.Single(ranking => ranking.Goal == goal);
}

public interface IRouteSelectionCoordinator
{
    RouteSelectionResult Select(RouteSelectionRequest request);
}

/// <summary>
/// Owns route-metric construction, exact-edge traffic application, structural/lane friction,
/// lane-safety eligibility, and deterministic preference ranking for presentation consumers.
/// </summary>
public sealed class RouteSelectionCoordinator : IRouteSelectionCoordinator
{
    public RouteSelectionResult Select(RouteSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Candidates);
        ArgumentNullException.ThrowIfNull(request.TrafficSnapshot);
        ArgumentNullException.ThrowIfNull(request.TrafficPolicy);
        ArgumentNullException.ThrowIfNull(request.PreferenceWeights);
        if (!Enum.IsDefined(request.PreferenceGoal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.PreferenceGoal,
                "PreferenceGoal must be a defined route preference goal.");
        }

        if (request.MaxAlternatives < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "MaxAlternatives cannot be negative.");
        }

        RouteSelectionCandidateInput[] inputs = request.Candidates.ToArray();
        if (inputs.Any(static input => input is null))
        {
            throw new ArgumentException("Route selection candidates cannot contain null.", nameof(request));
        }

        RouteSelectionCandidateInput? negativeIndex =
            inputs.FirstOrDefault(static input => input.Index < 0);
        if (negativeIndex is not null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                negativeIndex.Index,
                "Route selection candidate indexes cannot be negative.");
        }

        IGrouping<int, RouteSelectionCandidateInput>? duplicateIndex = inputs
            .GroupBy(static input => input.Index)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateIndex is not null)
        {
            throw new ArgumentException(
                $"Route selection candidate index {duplicateIndex.Key} is duplicated.",
                nameof(request));
        }

        RouteSelectionCandidateResult[] candidates = inputs
            .Select(input => Assess(input, request))
            .ToArray();
        HashSet<int> duplicateRouteIndexes = candidates
            .GroupBy(static candidate => candidate.RouteIdentity, StringComparer.Ordinal)
            .SelectMany(static group => group
                .OrderBy(SelectionSafetyRank)
                .ThenBy(static candidate => candidate.Index)
                .Skip(1))
            .Select(static candidate => candidate.Index)
            .ToHashSet();
        RouteSelectionRanking[] rankings =
        [
            Rank(candidates, duplicateRouteIndexes, RoutePreferenceGoal.Fastest, request),
            Rank(candidates, duplicateRouteIndexes, RoutePreferenceGoal.Shortest, request),
            Rank(candidates, duplicateRouteIndexes, RoutePreferenceGoal.Easiest, request),
        ];
        return new RouteSelectionResult(request.PreferenceGoal, candidates, rankings);
    }

    private static RouteSelectionCandidateResult Assess(
        RouteSelectionCandidateInput input,
        RouteSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(input.Candidate);
        ArgumentNullException.ThrowIfNull(input.TrafficControls);
        ArgumentNullException.ThrowIfNull(input.LaneProjection);
        RouteSelectionCandidateInput snapshotInput = SnapshotInput(input);
        OsmRouteCandidate candidate = snapshotInput.Candidate;
        var baseMetrics = new RouteCandidateMetrics(
            snapshotInput.ProviderId,
            snapshotInput.Index,
            candidate.DistanceMeters,
            Math.Max(0, candidate.DurationSeconds),
            RouteLabels: snapshotInput.RouteLabels,
            ManeuverCount: candidate.FrictionInputs.ManeuverCount,
            TollManeuverCount: candidate.FrictionInputs.TollManeuverCount,
            HighwayManeuverCount: candidate.FrictionInputs.HighwayManeuverCount,
            FerryManeuverCount: candidate.FrictionInputs.FerryManeuverCount,
            HasToll: candidate.FrictionInputs.HasToll,
            HasHighway: candidate.FrictionInputs.HasHighway,
            HasFerry: candidate.FrictionInputs.HasFerry,
            DirectedEdgeIds: candidate.DirectedEdgeIds,
            DurationSource: candidate.DurationSource);

        RouteTrafficEvaluation traffic = RouteTrafficEvaluator.Evaluate(
            baseMetrics,
            request.TrafficSnapshot,
            request.TrafficPolicy);
        RouteStructuralFrictionScore friction = FrictionModel.Score(
            candidate,
            snapshotInput.TrafficControls,
            traffic,
            snapshotInput.LaneProjection.Profile,
            request.TrafficPolicy,
            request.FrictionWeights);
        RouteCandidateMetrics metrics = traffic.ApplyTo(baseMetrics) with
        {
            StaticFrictionScore = friction.StaticScore,
        };
        string identity = RouteIdentity.Create(metrics);
        return new RouteSelectionCandidateResult(
            snapshotInput,
            metrics,
            traffic,
            friction,
            traffic.AdjustedEtaSeconds(metrics),
            identity,
            BuildProvenance(snapshotInput, traffic, identity));
    }

    private static RouteSelectionCandidateInput SnapshotInput(
        RouteSelectionCandidateInput input)
    {
        OsmRouteCandidate candidate = input.Candidate;
        var candidateSnapshot = new OsmRouteCandidate(
            candidate.DistanceMeters,
            candidate.DurationSeconds,
            candidate.EncodedPolyline,
            Array.AsReadOnly(candidate.RoutePoints
                .Select(static point => new GeoCoordinate(point.Latitude, point.Longitude))
                .ToArray()),
            Array.AsReadOnly(candidate.Maneuvers.ToArray()),
            candidate.FrictionInputs)
        {
            DirectedEdgeIds = candidate.DirectedEdgeIds is null
                ? null
                : Array.AsReadOnly(candidate.DirectedEdgeIds.ToArray()),
            DurationSource = candidate.DurationSource,
            TrafficSnapshotVersion = candidate.TrafficSnapshotVersion,
            EngineAppliedTrafficDelaySeconds = candidate.EngineAppliedTrafficDelaySeconds,
        };
        var controlSnapshot = new ValhallaRouteTrafficControlCounts(
            input.TrafficControls.TrafficSignalCount,
            input.TrafficControls.StopSignCount,
            input.TrafficControls.YieldSignCount,
            Array.AsReadOnly(input.TrafficControls.Controls.ToArray()));
        RouteLaneFrictionProjection laneSnapshot = SnapshotLaneProjection(input.LaneProjection);
        string providerId = string.IsNullOrWhiteSpace(input.ProviderId)
            ? "valhalla"
            : input.ProviderId.Trim();
        IReadOnlyList<string>? labels = input.RouteLabels is null
            ? null
            : Array.AsReadOnly(input.RouteLabels
                .Where(static label => !string.IsNullOrWhiteSpace(label))
                .Select(static label => label.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        return new RouteSelectionCandidateInput(
            input.Index,
            candidateSnapshot,
            controlSnapshot,
            laneSnapshot,
            providerId,
            labels);
    }

    private static RouteLaneFrictionProjection SnapshotLaneProjection(
        RouteLaneFrictionProjection projection)
    {
        var profile = new LaneFrictionProfile(
            projection.Profile.Score,
            projection.Profile.CanonicalPointCount,
            projection.Profile.RouteLaneChangeCount,
            projection.Profile.AdjacentMergeCount,
            Array.AsReadOnly(projection.Profile.Contributions.ToArray()),
            Array.AsReadOnly(projection.Profile.Guidance.ToArray()));
        return new RouteLaneFrictionProjection(
            projection.HasTopologyData,
            projection.UsedFallbackConnectivity,
            Array.AsReadOnly(projection.RouteSegments.ToArray()),
            Array.AsReadOnly(projection.CanonicalPoints.ToArray()),
            profile,
            Array.AsReadOnly(projection.MissingDirectedEdgeIds.ToArray()))
        {
            FailureReason = projection.FailureReason,
            RouteModifiers = Array.AsReadOnly(projection.RouteModifiers.ToArray()),
            TransitionDerivations = Array.AsReadOnly(projection.TransitionDerivations.ToArray()),
            OverlayDiagnostics = Array.AsReadOnly(projection.OverlayDiagnostics.ToArray()),
        };
    }

    private static int SelectionSafetyRank(RouteSelectionCandidateResult candidate)
        => candidate.TrafficEvaluation.HasHardDeny
            || candidate.LaneProjection.FailureReason is
                LaneProjectionFailureReason.CanonicalOverlayMismatch
                or LaneProjectionFailureReason.InfeasibleLaneChanges
            ? 1
            : 0;

    private static RouteSelectionRanking Rank(
        IReadOnlyList<RouteSelectionCandidateResult> candidates,
        IReadOnlySet<int> duplicateRouteIndexes,
        RoutePreferenceGoal goal,
        RouteSelectionRequest request)
    {
        var decisions = new List<RouteSelectionDecision>(candidates.Count);
        var safetyEligible = new List<RouteSelectionCandidateResult>(candidates.Count);
        foreach (RouteSelectionCandidateResult candidate in candidates)
        {
            if (candidate.TrafficEvaluation.HasHardDeny)
            {
                decisions.Add(new RouteSelectionDecision(
                    candidate,
                    RouteSelectionDecisionKind.Excluded,
                    RouteSelectionDecisionReason.DirectionSafeHardDeny,
                    "Excluded because direction-safe traffic data produced a hard deny for this route."));
                continue;
            }

            if (candidate.LaneProjection.FailureReason == LaneProjectionFailureReason.CanonicalOverlayMismatch)
            {
                decisions.Add(new RouteSelectionDecision(
                    candidate,
                    RouteSelectionDecisionKind.Excluded,
                    RouteSelectionDecisionReason.CanonicalOverlayMismatch,
                    "Excluded because lane topology did not match the active canonical graph."));
                continue;
            }

            if (candidate.LaneProjection.FailureReason == LaneProjectionFailureReason.InfeasibleLaneChanges)
            {
                decisions.Add(new RouteSelectionDecision(
                    candidate,
                    RouteSelectionDecisionKind.Excluded,
                    RouteSelectionDecisionReason.InfeasibleLaneChanges,
                    "Excluded because the required lane changes are infeasible for the evaluated vehicle path."));
                continue;
            }

            if (duplicateRouteIndexes.Contains(candidate.Index))
            {
                decisions.Add(new RouteSelectionDecision(
                    candidate,
                    RouteSelectionDecisionKind.Excluded,
                    RouteSelectionDecisionReason.DuplicateCanonicalRoute,
                    "Excluded as a duplicate of another candidate with the same ordered canonical directed-edge route identity."));
                continue;
            }

            safetyEligible.Add(candidate);
        }

        bool preferVerified = goal == RoutePreferenceGoal.Easiest;
        RouteSelectionCandidateResult[] verified = safetyEligible
            .Where(static candidate => candidate.LaneProjection.HasRouteLanePath)
            .ToArray();
        IReadOnlyList<RouteSelectionCandidateResult> pool;
        if (preferVerified && verified.Length > 0)
        {
            pool = verified;
            foreach (RouteSelectionCandidateResult candidate in safetyEligible
                         .Where(static candidate => !candidate.LaneProjection.HasRouteLanePath))
            {
                decisions.Add(new RouteSelectionDecision(
                    candidate,
                    RouteSelectionDecisionKind.Deprioritized,
                    RouteSelectionDecisionReason.UnverifiedLaneTopology,
                    "Deprioritized for Easiest because a verified lane path is available and this candidate's lane topology is unverified."));
            }
        }
        else
        {
            pool = safetyEligible;
        }

        if (pool.Count == 0)
        {
            return new RouteSelectionRanking(
                goal,
                [],
                decisions.OrderBy(static decision => decision.Candidate.Index).ToArray(),
                $"No traffic-safe route candidate remained for {goal}.",
                usesUnverifiedLaneTopology: false);
        }

        int maximumAlternatives = Math.Min(
            request.MaxAlternatives,
            Math.Max(0, pool.Count - 1));
        RoutePreferenceRanking ranking = RoutePreferenceRanker.Rank(
            pool.Select(static candidate => new RoutePreferenceCandidate(
                candidate.Index,
                candidate.AdjustedEtaSeconds,
                candidate.Metrics.DistanceMeters,
                candidate.Friction.TotalScore)).ToArray(),
            goal,
            request.PreferenceWeights,
            request.NearTieThresholds,
            maximumAlternatives);
        Dictionary<int, RouteSelectionCandidateResult> byIndex =
            pool.ToDictionary(static candidate => candidate.Index);
        RouteSelectionCandidateResult[] ordered = new[] { ranking.Best }
            .Concat(ranking.Alternatives)
            .Select(candidate => byIndex[candidate.Index])
            .ToArray();

        decisions.Add(new RouteSelectionDecision(
            ordered[0],
            RouteSelectionDecisionKind.Selected,
            RouteSelectionDecisionReason.SelectedByPreference,
            ranking.Reason));
        for (int index = 1; index < ordered.Length; index++)
        {
            decisions.Add(new RouteSelectionDecision(
                ordered[index],
                RouteSelectionDecisionKind.Alternative,
                RouteSelectionDecisionReason.AlternativeByPreference,
                $"Ranked alternative {index} for {goal} after candidate {ordered[0].Index}."));
        }

        HashSet<int> surfacedIndexes = ordered
            .Select(static candidate => candidate.Index)
            .ToHashSet();
        foreach (RouteSelectionCandidateResult candidate in pool
                     .Where(candidate => !surfacedIndexes.Contains(candidate.Index)))
        {
            decisions.Add(new RouteSelectionDecision(
                candidate,
                RouteSelectionDecisionKind.Deprioritized,
                RouteSelectionDecisionReason.RankedBelowAlternativeLimit,
                $"Ranked below the configured alternative limit for {goal}."));
        }

        bool usesUnverifiedLaneTopology = !ordered[0].LaneProjection.HasRouteLanePath;
        string reason = usesUnverifiedLaneTopology
            ? ranking.Reason
                + (preferVerified
                    ? " No verified lane path was available; ranking used an explicitly qualified unverified lane-topology fallback."
                    : " The selected route has unverified lane topology.")
            : ranking.Reason;
        return new RouteSelectionRanking(
            goal,
            ordered,
            decisions.OrderBy(static decision => decision.Candidate.Index).ToArray(),
            reason,
            usesUnverifiedLaneTopology);
    }

    private static IReadOnlyList<RouteSelectionProvenance> BuildProvenance(
        RouteSelectionCandidateInput input,
        RouteTrafficEvaluation traffic,
        string identity)
    {
        var provenance = new List<RouteSelectionProvenance>
        {
            new(
                RouteSelectionProvenanceKind.RouteIdentity,
                identity,
                "Ordered canonical directed-edge route identity."),
            new(
                RouteSelectionProvenanceKind.LaneTopology,
                input.LaneProjection.FailureReason.ToString(),
                input.LaneProjection.HasRouteLanePath
                    ? "Graph-backed lane path verified."
                    : "Lane path is unavailable or qualified; inspect the failure reason."),
            new(
                RouteSelectionProvenanceKind.GraphTrafficControls,
                "valhalla-graph",
                $"Traffic signals {input.TrafficControls.TrafficSignalCount}; stop signs {input.TrafficControls.StopSignCount}; yield signs {input.TrafficControls.YieldSignCount}."),
        };

        foreach (TrafficRouteModifierSource source in traffic.Sources)
        {
            foreach (string providerId in source.ProviderIds)
            {
                foreach (string eventId in source.SourceEventIds)
                {
                    provenance.Add(new RouteSelectionProvenance(
                        RouteSelectionProvenanceKind.TrafficEvent,
                        $"{providerId}:{eventId}",
                        source.Impact.Description));
                }
            }
        }

        IEnumerable<LaneTopologyOverlayDescriptor> overlaySources =
            input.LaneProjection.Profile.Contributions
                .Select(static contribution => contribution.OverlaySource)
                .Concat(input.LaneProjection.RouteSegments.Select(static segment => segment.OverlaySource))
                .Where(static source => source is not null)
                .Cast<LaneTopologyOverlayDescriptor>()
                .Distinct();
        foreach (LaneTopologyOverlayDescriptor source in overlaySources)
        {
            provenance.Add(new RouteSelectionProvenance(
                RouteSelectionProvenanceKind.LaneTopology,
                $"{source.DatasetId}:{source.DatasetVersion}",
                $"{source.Provenance} lane-topology overlay for graph {source.GraphSignature}."));
        }

        return Array.AsReadOnly(provenance
            .Distinct()
            .ToArray());
    }
}
