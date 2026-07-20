using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class RouteSelectionCoordinatorTests
{
    [Fact]
    public void Select_PrefersVerifiedLanePathAndRejectsUnsafeCandidates()
    {
        RouteSelectionResult result = new RouteSelectionCoordinator().Select(new RouteSelectionRequest(
            [
                Input(0, durationSeconds: 90, lane: Lane(LaneProjectionFailureReason.MissingLaneConnectivity)),
                Input(1, durationSeconds: 110, lane: Lane(LaneProjectionFailureReason.None, score: 8)),
                Input(2, durationSeconds: 80, lane: Lane(LaneProjectionFailureReason.CanonicalOverlayMismatch)),
                Input(3, durationSeconds: 70, lane: Lane(LaneProjectionFailureReason.InfeasibleLaneChanges)),
            ],
            EmptySnapshot(),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Easiest,
            RoutePreferenceWeights.Balanced));

        RouteSelectionRanking easiest = result.GetRanking(RoutePreferenceGoal.Easiest);

        Assert.Equal(1, easiest.Best!.Index);
        Assert.Equal(
            RouteSelectionDecisionReason.UnverifiedLaneTopology,
            Decision(easiest, 0).Reason);
        Assert.Equal(
            RouteSelectionDecisionReason.CanonicalOverlayMismatch,
            Decision(easiest, 2).Reason);
        Assert.Equal(
            RouteSelectionDecisionReason.InfeasibleLaneChanges,
            Decision(easiest, 3).Reason);
        Assert.Equal(RouteSelectionDecisionKind.Deprioritized, Decision(easiest, 0).Kind);
        Assert.Equal(RouteSelectionDecisionKind.Excluded, Decision(easiest, 2).Kind);
        Assert.Equal(RouteSelectionDecisionKind.Excluded, Decision(easiest, 3).Kind);
    }

    [Fact]
    public void Select_AppliesTrafficAndLaneFrictionExactlyOnce()
    {
        const ulong edgeId = 42;
        RouteSelectionResult result = new RouteSelectionCoordinator().Select(new RouteSelectionRequest(
            [Input(0, durationSeconds: 100, lane: Lane(LaneProjectionFailureReason.None, score: 7), edgeId)],
            Snapshot(TrafficSource(edgeId, RouteModifierImpactKind.TrafficDelay, delaySeconds: 30)),
            TrafficPolicy.Enabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced));

        RouteSelectionCandidateResult candidate = Assert.Single(result.Candidates);

        Assert.Equal(100, candidate.Metrics.DurationSeconds);
        Assert.Equal(30, candidate.Metrics.TrafficDelaySeconds);
        Assert.Equal(130, candidate.AdjustedEtaSeconds);
        Assert.Equal(8d, candidate.Friction.StaticScore);
        Assert.Equal(30d, candidate.Friction.DynamicScore);
        Assert.Equal(38d, candidate.Friction.TotalScore);
        Assert.Single(
            candidate.Friction.Contributions,
            contribution => contribution.Kind == FrictionContributionKind.TrafficDelay);
        Assert.Equal(candidate.Friction.StaticScore, candidate.Metrics.StaticFrictionScore);
    }

    [Fact]
    public void Select_ReturnsDeterministicOrderReasonsAndProvenance()
    {
        RouteSelectionResult result = new RouteSelectionCoordinator().Select(new RouteSelectionRequest(
            [
                Input(2, durationSeconds: 100, lane: Lane(LaneProjectionFailureReason.None)),
                Input(0, durationSeconds: 100, lane: Lane(LaneProjectionFailureReason.None)),
                Input(1, durationSeconds: 100, lane: Lane(LaneProjectionFailureReason.None)),
            ],
            EmptySnapshot(),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced));

        RouteSelectionRanking fastest = result.GetRanking(RoutePreferenceGoal.Fastest);

        Assert.Equal([0, 1, 2], fastest.OrderedCandidates.Select(static candidate => candidate.Index));
        Assert.Equal(0, result.Selected!.Index);
        Assert.Contains("candidate 0", fastest.Reason, StringComparison.Ordinal);
        Assert.Equal(RouteSelectionDecisionKind.Selected, Decision(fastest, 0).Kind);
        Assert.Equal(RouteSelectionDecisionKind.Alternative, Decision(fastest, 1).Kind);
        Assert.All(
            fastest.Decisions,
            decision => Assert.False(string.IsNullOrWhiteSpace(decision.Explanation)));
        Assert.Contains(
            result.Selected.Provenance,
            provenance => provenance.Kind == RouteSelectionProvenanceKind.RouteIdentity
                && provenance.SourceId.StartsWith("edges:", StringComparison.Ordinal));
    }

    [Fact]
    public void Select_HardDeniedCandidateIsExcludedWithReason()
    {
        const ulong deniedEdge = 10;
        RouteSelectionResult result = new RouteSelectionCoordinator().Select(new RouteSelectionRequest(
            [
                Input(0, durationSeconds: 50, lane: Lane(LaneProjectionFailureReason.None), deniedEdge),
                Input(1, durationSeconds: 100, lane: Lane(LaneProjectionFailureReason.None), edgeId: 11),
            ],
            Snapshot(TrafficSource(deniedEdge, RouteModifierImpactKind.RoadClosure, hardDeny: true)),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced));

        RouteSelectionRanking fastest = result.GetRanking(RoutePreferenceGoal.Fastest);
        RouteSelectionDecision excluded = Decision(fastest, 0);

        Assert.Equal(1, fastest.Best!.Index);
        Assert.Equal(RouteSelectionDecisionKind.Excluded, excluded.Kind);
        Assert.Equal(RouteSelectionDecisionReason.DirectionSafeHardDeny, excluded.Reason);
        Assert.Contains("hard deny", excluded.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            excluded.Candidate.Provenance,
            provenance => provenance.Kind == RouteSelectionProvenanceKind.TrafficEvent
                && provenance.SourceId == "provider:event");
    }

    [Fact]
    public void Select_AssignsExactlyOneDecisionWhenAlternativeLimitTruncatesRanking()
    {
        RouteSelectionResult result = new RouteSelectionCoordinator().Select(new RouteSelectionRequest(
            [
                Input(0, 100, Lane(LaneProjectionFailureReason.None)),
                Input(1, 101, Lane(LaneProjectionFailureReason.None)),
                Input(2, 102, Lane(LaneProjectionFailureReason.None)),
                Input(3, 103, Lane(LaneProjectionFailureReason.None)),
            ],
            EmptySnapshot(),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced)
        {
            MaxAlternatives = 1,
        });

        RouteSelectionRanking ranking = result.GetRanking(RoutePreferenceGoal.Fastest);

        Assert.Equal(4, ranking.Decisions.Count);
        Assert.Equal(4, ranking.Decisions.Select(static decision => decision.Candidate.Index).Distinct().Count());
        Assert.Equal(2, ranking.OrderedCandidates.Count);
        Assert.Equal(
            2,
            ranking.Decisions.Count(decision =>
                decision.Kind == RouteSelectionDecisionKind.Deprioritized
                && decision.Reason == RouteSelectionDecisionReason.RankedBelowAlternativeLimit));
    }

    [Fact]
    public void Select_DefensivelyCopiesCandidateInputCollections()
    {
        var labels = new List<string> { "I-40" };
        var edgeIds = new List<ulong> { 42 };
        var laneContributions = new List<LaneFrictionContribution>
        {
            new(
                LaneFrictionContributionKind.RouteLaneChange,
                3,
                "segment",
                1,
                "lane change"),
        };
        OsmRouteCandidate candidate = Candidate(100, 42) with
        {
            DirectedEdgeIds = edgeIds,
        };
        var lane = new RouteLaneFrictionProjection(
            true,
            false,
            [],
            [],
            new LaneFrictionProfile(3, 1, 1, 0, laneContributions, []),
            [])
        {
            FailureReason = LaneProjectionFailureReason.None,
        };
        var input = new RouteSelectionCandidateInput(
            0,
            candidate,
            new ValhallaRouteTrafficControlCounts(0, 0, 0, []),
            lane,
            "valhalla",
            labels);

        RouteSelectionResult result = new RouteSelectionCoordinator().Select(new RouteSelectionRequest(
            [input],
            EmptySnapshot(),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced));

        labels.Add("MUTATED");
        edgeIds.Add(99);
        laneContributions.Add(new LaneFrictionContribution(
            LaneFrictionContributionKind.Weave,
            9,
            "mutated",
            2,
            "mutated"));

        RouteSelectionCandidateInput snapshot = Assert.Single(result.Candidates).Input;
        Assert.Equal(["I-40"], snapshot.RouteLabels);
        Assert.Equal([42UL], snapshot.Candidate.DirectedEdgeIds);
        Assert.Single(snapshot.LaneProjection.Profile.Contributions);
    }

    [Fact]
    public void Select_RejectsNegativeAndDuplicateCandidateIndexes()
    {
        var coordinator = new RouteSelectionCoordinator();

        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Select(new RouteSelectionRequest(
            [Input(int.MinValue, 100, Lane(LaneProjectionFailureReason.None), 1)],
            EmptySnapshot(),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced)));
        Assert.Throws<ArgumentException>(() => coordinator.Select(new RouteSelectionRequest(
            [
                Input(0, 100, Lane(LaneProjectionFailureReason.None), 1),
                Input(0, 101, Lane(LaneProjectionFailureReason.None), 2),
            ],
            EmptySnapshot(),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced)));
    }

    [Fact]
    public void Select_RejectsUndefinedPreferenceGoal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RouteSelectionCoordinator().Select(
            new RouteSelectionRequest(
                [Input(0, 100, Lane(LaneProjectionFailureReason.None))],
                EmptySnapshot(),
                TrafficPolicy.Disabled,
                (RoutePreferenceGoal)999,
                RoutePreferenceWeights.Balanced)));
    }

    [Fact]
    public void Select_DeduplicatesCanonicalRouteIdentityWithExplicitDisposition()
    {
        RouteSelectionResult result = new RouteSelectionCoordinator().Select(new RouteSelectionRequest(
            [
                Input(1, 90, Lane(LaneProjectionFailureReason.None), 42),
                Input(0, 100, Lane(LaneProjectionFailureReason.None), 42),
            ],
            EmptySnapshot(),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced));

        RouteSelectionRanking ranking = result.GetRanking(RoutePreferenceGoal.Fastest);

        Assert.Equal(0, ranking.Best!.Index);
        Assert.Single(ranking.OrderedCandidates);
        RouteSelectionDecision duplicate = Decision(ranking, 1);
        Assert.Equal(RouteSelectionDecisionKind.Excluded, duplicate.Kind);
        Assert.Equal(RouteSelectionDecisionReason.DuplicateCanonicalRoute, duplicate.Reason);
        Assert.Contains("duplicate", duplicate.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_ReportsUnverifiedLaneTopologyForFastestAndShortest()
    {
        RouteSelectionResult result = new RouteSelectionCoordinator().Select(new RouteSelectionRequest(
            [Input(0, 100, Lane(LaneProjectionFailureReason.MissingLaneConnectivity))],
            EmptySnapshot(),
            TrafficPolicy.Disabled,
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced));

        Assert.True(result.GetRanking(RoutePreferenceGoal.Fastest).UsesUnverifiedLaneTopology);
        Assert.True(result.GetRanking(RoutePreferenceGoal.Shortest).UsesUnverifiedLaneTopology);
        Assert.True(result.GetRanking(RoutePreferenceGoal.Easiest).UsesUnverifiedLaneTopology);
    }

    private static RouteSelectionDecision Decision(RouteSelectionRanking ranking, int index)
        => Assert.Single(ranking.Decisions, decision => decision.Candidate.Index == index);

    private static RouteSelectionCandidateInput Input(
        int index,
        int durationSeconds,
        RouteLaneFrictionProjection lane,
        ulong? edgeId = null)
        => new(
            index,
            Candidate(durationSeconds, edgeId ?? checked((ulong)(100 + index))),
            new ValhallaRouteTrafficControlCounts(0, 0, 0, []),
            lane,
            ProviderId: "valhalla",
            RouteLabels: [$"route-{index}"]);

    private static OsmRouteCandidate Candidate(int durationSeconds, ulong edgeId)
        => new(
            DistanceMeters: 1_000,
            DurationSeconds: durationSeconds,
            EncodedPolyline: null,
            RoutePoints: [new GeoCoordinate(36.1, -86.7), new GeoCoordinate(36.2, -86.8)],
            Maneuvers: [new OsmRouteManeuver(0, "Continue", 1_000, durationSeconds, 0, 1)],
            FrictionInputs: new OsmRouteFrictionInputs(1, 0, 0, 0, false, false, false))
        {
            DirectedEdgeIds = [edgeId],
            DurationSource = RouteDurationSource.FreeFlow,
        };

    private static RouteLaneFrictionProjection Lane(
        LaneProjectionFailureReason failureReason,
        int score = 0)
        => new(
            HasTopologyData: failureReason != LaneProjectionFailureReason.MissingGraphEdges,
            UsedFallbackConnectivity: failureReason == LaneProjectionFailureReason.MissingLaneConnectivity,
            RouteSegments: [],
            CanonicalPoints: [],
            Profile: new LaneFrictionProfile(
                score,
                score > 0 ? 1 : 0,
                score > 0 ? 1 : 0,
                0,
                score > 0
                    ? [new LaneFrictionContribution(
                        LaneFrictionContributionKind.RouteLaneChange,
                        score,
                        "segment",
                        1,
                        "verified lane change")]
                    : [],
                []),
            MissingDirectedEdgeIds: [])
        {
            FailureReason = failureReason,
        };

    private static NormalizedTrafficSnapshot EmptySnapshot()
        => Snapshot();

    private static NormalizedTrafficSnapshot Snapshot(params TrafficRouteModifierSource[] sources)
        => new(
            DateTimeOffset.UnixEpoch,
            [],
            sources.Select(static source => source.Impact).ToArray(),
            sources,
            sources.SelectMany(static source => source.AffectedEdges).ToArray(),
            null,
            [],
            []);

    private static TrafficRouteModifierSource TrafficSource(
        ulong edgeId,
        RouteModifierImpactKind kind,
        int? delaySeconds = null,
        bool hardDeny = false)
    {
        var edge = new ValhallaTrafficEdgeUpdate(
            TileId: 0,
            DirectedEdgeIndex: 0,
            Direction: TrafficDirection.Forward,
            CurrentSpeedKph: null,
            FreeFlowSpeedKph: null,
            DelaySeconds: delaySeconds,
            Closed: hardDeny,
            HasIncident: kind == RouteModifierImpactKind.Incident,
            DirectionResolved: true,
            Confidence: 1,
            SourceEventId: "event",
            ProviderId: "provider",
            GraphDirectedEdgeId: edgeId);
        return new TrafficRouteModifierSource(
            new RouteModifierImpact("source", kind, $"{kind} source", hardDeny),
            ["provider"],
            ["event"],
            [edge],
            delaySeconds,
            hardDeny ? TrafficSeverity.Closed : TrafficSeverity.Moderate);
    }
}
