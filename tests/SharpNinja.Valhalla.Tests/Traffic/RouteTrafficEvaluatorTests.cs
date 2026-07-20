using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class RouteTrafficEvaluatorTests
{
    [Fact]
    public void Evaluate_UsesExactDirectedEdgeIdsAndNeverGeometryProximity()
    {
        RouteCandidateMetrics candidate = Candidate([10]);
        TrafficRouteModifierSource nearbyButDifferentEdge = Source(
            RouteModifierImpactKind.TrafficDelay,
            delaySeconds: 240,
            hardDeny: false,
            [Update(edgeId: 20, eventId: "nearby")],
            "nearby");

        RouteTrafficEvaluation evaluation = RouteTrafficEvaluator.Evaluate(
            candidate,
            Snapshot([nearbyButDifferentEdge]),
            TrafficPolicy.Enabled);

        Assert.Equal(0, evaluation.ObservedTrafficDelaySeconds);
        Assert.Equal(0, evaluation.TrafficDelaySeconds);
        Assert.Empty(evaluation.Sources);
        Assert.Empty(evaluation.AffectedEdges);
    }

    [Fact]
    public void Evaluate_DeduplicatesIncidentAcrossEdgesAndAddsItsDelayToEtaExactlyOnce()
    {
        RouteCandidateMetrics candidate = Candidate([10, 11]);
        TrafficRouteModifierSource incident = Source(
            RouteModifierImpactKind.Incident,
            delaySeconds: 120,
            hardDeny: false,
            [
                Update(edgeId: 10, eventId: "incident-1"),
                Update(edgeId: 10, eventId: "incident-1"),
                Update(edgeId: 11, eventId: "incident-1"),
            ],
            "incident-1");

        RouteTrafficEvaluation evaluation = RouteTrafficEvaluator.Evaluate(
            candidate,
            Snapshot([incident, incident]),
            TrafficPolicy.Enabled);

        Assert.Equal(120, evaluation.ObservedTrafficDelaySeconds);
        Assert.Equal(120, evaluation.TrafficDelaySeconds);
        Assert.Equal(1, evaluation.ObservedIncidentCount);
        Assert.Equal(1, evaluation.IncidentCount);
        Assert.Equal(720, evaluation.AdjustedEtaSeconds(candidate));
        RouteCandidateMetrics adjusted = evaluation.ApplyTo(candidate);
        Assert.Equal(120, adjusted.TrafficDelaySeconds);
        Assert.Equal(1, adjusted.IncidentCount);
        Assert.Equal(720, TrafficAwareRerouteRanker.AdjustedEtaSeconds(adjusted, TrafficPolicy.Enabled));
        Assert.Single(evaluation.Sources);
        Assert.Equal(2, evaluation.AffectedEdges.Count);
    }

    [Fact]
    public void TrafficDisabled_OmitsDynamicMetricsButRetainsTrueClosureHardDeny()
    {
        RouteCandidateMetrics candidate = Candidate([10]);
        TrafficRouteModifierSource flow = Source(
            RouteModifierImpactKind.TrafficDelay,
            delaySeconds: 300,
            hardDeny: false,
            [Update(edgeId: 10, eventId: "flow-1")],
            "flow-1");
        TrafficRouteModifierSource closure = Source(
            RouteModifierImpactKind.RoadClosure,
            delaySeconds: null,
            hardDeny: true,
            [Update(edgeId: 10, eventId: "closure-1", closed: true)],
            "closure-1");

        RouteTrafficEvaluation evaluation = RouteTrafficEvaluator.Evaluate(
            candidate,
            Snapshot([flow, closure]),
            TrafficPolicy.Disabled);

        Assert.Equal(300, evaluation.ObservedTrafficDelaySeconds);
        Assert.Equal(0, evaluation.TrafficDelaySeconds);
        Assert.Equal(600, evaluation.AdjustedEtaSeconds(candidate));
        Assert.True(evaluation.HasHardDeny);
        Assert.True(evaluation.HasClosureHardDeny);
        Assert.False(evaluation.HasRestrictionHardDeny);
        Assert.Contains(evaluation.Impacts, impact =>
            impact.Kind == RouteModifierImpactKind.RoadClosure && impact.HardDeny);
    }

    [Fact]
    public void Evaluate_ReturnsRouteSpecificRestrictionHardDenyAndSourceMetadata()
    {
        RouteCandidateMetrics candidate = Candidate([42]);
        TrafficRouteModifierSource restriction = Source(
            RouteModifierImpactKind.Restriction,
            delaySeconds: null,
            hardDeny: true,
            [Update(edgeId: 42, eventId: "restriction-1", closed: true)],
            "restriction-1");

        RouteTrafficEvaluation evaluation = RouteTrafficEvaluator.Evaluate(
            candidate,
            Snapshot([restriction]),
            TrafficPolicy.Disabled);

        Assert.True(evaluation.HasHardDeny);
        Assert.False(evaluation.HasClosureHardDeny);
        Assert.True(evaluation.HasRestrictionHardDeny);
        TrafficRouteModifierSource applicableSource = Assert.Single(evaluation.Sources);
        Assert.Equal("restriction-1", Assert.Single(applicableSource.SourceEventIds));
        Assert.Equal(RouteIdentity.Create(candidate), Assert.Single(evaluation.Impacts).RouteKey);
    }

    [Fact]
    public void UnresolvedRestrictionDirection_DoesNotApplyUnsafeHardDeny()
    {
        RouteCandidateMetrics candidate = Candidate([42]);
        ValhallaTrafficEdgeUpdate unresolved = Update(
            edgeId: 42,
            eventId: "restriction-ambiguous",
            closed: true) with
        {
            DirectionResolved = false,
            Direction = TrafficDirection.Unknown,
        };
        TrafficRouteModifierSource restriction = Source(
            RouteModifierImpactKind.Restriction,
            delaySeconds: null,
            hardDeny: true,
            [unresolved],
            "restriction-ambiguous");

        RouteTrafficEvaluation evaluation = RouteTrafficEvaluator.Evaluate(
            candidate,
            Snapshot([restriction]),
            TrafficPolicy.Disabled);

        Assert.False(evaluation.HasHardDeny);
        Assert.False(evaluation.HasRestrictionHardDeny);
        Assert.Single(evaluation.Sources);
    }


    [Fact]
    public void SplitSourceIdentity_UnionsExactEdgesBeforeResolvingClosureHardDeny()
    {
        RouteCandidateMetrics candidate = Candidate([42]);
        ValhallaTrafficEdgeUpdate unresolved = Update(
            edgeId: 42,
            eventId: "closure-split",
            closed: true) with
        {
            DirectionResolved = false,
            Direction = TrafficDirection.Unknown,
        };
        ValhallaTrafficEdgeUpdate resolved = Update(
            edgeId: 42,
            eventId: "closure-split",
            closed: true);
        TrafficRouteModifierSource first = Source(
            RouteModifierImpactKind.RoadClosure,
            delaySeconds: null,
            hardDeny: true,
            [unresolved],
            "closure-split");
        TrafficRouteModifierSource second = Source(
            RouteModifierImpactKind.RoadClosure,
            delaySeconds: null,
            hardDeny: true,
            [resolved],
            "closure-split");

        RouteTrafficEvaluation evaluation = RouteTrafficEvaluator.Evaluate(
            candidate,
            Snapshot([first, second]),
            TrafficPolicy.Disabled);

        Assert.True(evaluation.HasClosureHardDeny);
        Assert.Single(evaluation.Sources);
        ValhallaTrafficEdgeUpdate edge = Assert.Single(evaluation.AffectedEdges);
        Assert.True(edge.DirectionResolved);
        Assert.True(edge.Closed);
    }

    [Fact]
    public void AdjustedEta_SaturatesWhenProviderDelayExceedsIntRange()
    {
        RouteCandidateMetrics candidate = Candidate([10]);
        TrafficRouteModifierSource flow = Source(
            RouteModifierImpactKind.TrafficDelay,
            delaySeconds: int.MaxValue,
            hardDeny: false,
            [Update(edgeId: 10, eventId: "extreme-delay")],
            "extreme-delay");

        RouteTrafficEvaluation evaluation = RouteTrafficEvaluator.Evaluate(
            candidate,
            Snapshot([flow]),
            TrafficPolicy.Enabled);

        Assert.Equal(int.MaxValue, evaluation.ObservedTrafficDelaySeconds);
        Assert.Equal(int.MaxValue, evaluation.AdjustedEtaSeconds(candidate));
    }


    private static RouteCandidateMetrics Candidate(IReadOnlyList<ulong> edges)
        => new(
            ProviderId: "valhalla",
            Index: 0,
            DistanceMeters: 10_000,
            DurationSeconds: 600,
            DirectedEdgeIds: edges);

    private static NormalizedTrafficSnapshot Snapshot(
        IReadOnlyList<TrafficRouteModifierSource> sources)
        => new(
            DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            [],
            sources.Select(static source => source.Impact).ToArray(),
            sources,
            sources.SelectMany(static source => source.AffectedEdges).ToArray(),
            null,
            [],
            []);

    private static TrafficRouteModifierSource Source(
        RouteModifierImpactKind kind,
        int? delaySeconds,
        bool hardDeny,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> edges,
        string eventId)
        => new(
            new RouteModifierImpact(
                $"traffic-event:provider:{eventId}",
                kind,
                $"{kind} from provider",
                hardDeny),
            ["provider"],
            [eventId],
            edges,
            delaySeconds,
            hardDeny ? TrafficSeverity.Closed : TrafficSeverity.Heavy);

    private static ValhallaTrafficEdgeUpdate Update(
        ulong edgeId,
        string eventId,
        bool closed = false)
        => new(
            TileId: 1,
            DirectedEdgeIndex: checked((uint)edgeId),
            Direction: TrafficDirection.Forward,
            CurrentSpeedKph: closed ? null : 30,
            FreeFlowSpeedKph: closed ? null : 80,
            DelaySeconds: closed ? null : 120,
            Closed: closed,
            HasIncident: !closed,
            DirectionResolved: true,
            Confidence: 0.9,
            SourceEventId: eventId,
            ProviderId: "provider",
            GraphDirectedEdgeId: edgeId);
}
