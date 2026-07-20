using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficAwareRouteSetPlannerBehaviorTests
{
    private static readonly DateTimeOffset Departure =
        new(2026, 7, 19, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task PlanAsync_PerformsTwoPassesWithStableIdentities()
    {
        OsmRouteCandidate baselineA = Candidate(900, 10_000d, 1, 2);
        OsmRouteCandidate baselineB = Candidate(950, 9_500d, 3, 4);
        OsmRouteCandidate activeA = Candidate(
            1_020,
            10_000d,
            RouteDurationSource.LiveTraffic,
            120,
            1,
            2);
        OsmRouteCandidate activeB = Candidate(
            930,
            9_500d,
            RouteDurationSource.LiveTraffic,
            15,
            3,
            4);
        var client = new RecordingRoutingClient(
            new OsmRouteResult([baselineA, baselineB], null),
            new OsmRouteResult([activeA, activeB], null));
        var planner = new TrafficAwareRouteSetPlanner(client, new StaticTimeProvider(Departure));
        TrafficSnapshotReference snapshot = Snapshot();

        TrafficAwareRouteSetPlan result = await planner.PlanAsync(
            new TrafficAwareRouteSetRequest(RouteRequest(), snapshot, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(TrafficAwareRouteSetStatus.Success, result.Status);
        Assert.True(result.ActivePassSucceeded);
        Assert.Equal(2, client.Requests.Count);
        Assert.Null(client.Requests[0].TrafficSnapshot);
        Assert.Same(snapshot, client.Requests[1].TrafficSnapshot);
        Assert.Equal(Departure, client.Requests[0].DepartureTimeUtc);
        Assert.Equal(Departure, client.Requests[1].DepartureTimeUtc);
        Assert.Equal(
            result.BaselineCandidates.Select(Identity).OrderBy(static value => value),
            result.ActiveCandidates.Select(Identity).OrderBy(static value => value));
        Assert.Equal(Identity(activeB), result.SelectedRouteIdentity);
    }

    [Fact]
    public async Task PlanAsync_ExplainsExcludedBaselineCandidateAndActiveFailure()
    {
        OsmRouteCandidate normal = Candidate(900, 10_000d, 1, 2);
        OsmRouteCandidate alternate = Candidate(
            1_100,
            11_000d,
            RouteDurationSource.LiveTraffic,
            0,
            3,
            4);
        TrafficRouteModifierSource closure = SourceFor(
            normal,
            RouteModifierImpactKind.RoadClosure,
            hardDeny: true,
            "tomtom",
            "closure-42");
        var successfulClient = new RecordingRoutingClient(
            new OsmRouteResult([normal, alternate], null),
            new OsmRouteResult([alternate], null));
        var successfulPlanner = new TrafficAwareRouteSetPlanner(
            successfulClient,
            new StaticTimeProvider(Departure));

        TrafficAwareRouteSetPlan successful = await successfulPlanner.PlanAsync(
            new TrafficAwareRouteSetRequest(
                RouteRequest(),
                Snapshot(),
                [closure]),
            TestContext.Current.CancellationToken);

        TrafficAwareRouteAdvisory advisory = Assert.Single(successful.Advisories);
        Assert.Equal(Identity(normal), advisory.RouteIdentity);
        Assert.Contains("excluded", advisory.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tomtom", advisory.ProviderIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("closure-42", advisory.SourceEventIds);
        Assert.Contains("tomtom", advisory.Message, StringComparison.OrdinalIgnoreCase);

        var failureClient = new RecordingRoutingClient(
            new OsmRouteResult([normal], null),
            OsmRouteResult.TrafficFailure(new TrafficSnapshotFailure(
                TrafficSnapshotFailureCode.Expired,
                "expired",
                Snapshot().Version)));
        var failurePlanner = new TrafficAwareRouteSetPlanner(
            failureClient,
            new StaticTimeProvider(Departure));

        TrafficAwareRouteSetPlan failed = await failurePlanner.PlanAsync(
            new TrafficAwareRouteSetRequest(
                RouteRequest(),
                Snapshot(),
                [closure],
                CurrentRouteIdentity: Identity(normal)),
            TestContext.Current.CancellationToken);

        Assert.Equal(TrafficAwareRouteSetStatus.ActivePassFailed, failed.Status);
        Assert.False(failed.ActivePassSucceeded);
        Assert.False(failed.AutomaticReplacement);
        Assert.NotNull(failed.ActiveSnapshotFailure);
        Assert.Equal("traffic_snapshot_invalid", failed.ActiveError);
    }

    [Fact]
    public async Task PlanAsync_ExplainsMateriallyDeprioritizedCandidateThatRemainsPresent()
    {
        OsmRouteCandidate normal = Candidate(900, 10_000d, 1, 2);
        OsmRouteCandidate alternate = Candidate(1_000, 11_000d, 3, 4);
        OsmRouteCandidate activeNormal = Candidate(
            1_200,
            10_000d,
            RouteDurationSource.LiveTraffic,
            300,
            1,
            2);
        OsmRouteCandidate activeAlternate = Candidate(
            950,
            11_000d,
            RouteDurationSource.LiveTraffic,
            0,
            3,
            4);
        TrafficRouteModifierSource delay = SourceFor(
            normal,
            RouteModifierImpactKind.TrafficDelay,
            hardDeny: false,
            "here",
            "flow-deprioritized-42");
        var client = new RecordingRoutingClient(
            new OsmRouteResult([normal, alternate], null),
            new OsmRouteResult([activeNormal, activeAlternate], null));
        var planner = new TrafficAwareRouteSetPlanner(
            client,
            new StaticTimeProvider(Departure));

        TrafficAwareRouteSetPlan result = await planner.PlanAsync(
            new TrafficAwareRouteSetRequest(
                RouteRequest(),
                Snapshot(),
                [delay]),
            TestContext.Current.CancellationToken);

        string normalIdentity = Identity(normal);
        Assert.Equal(TrafficAwareRouteSetStatus.Success, result.Status);
        Assert.Contains(
            result.ActiveCandidates,
            candidate => string.Equals(
                Identity(candidate),
                normalIdentity,
                StringComparison.Ordinal));
        Assert.Equal(Identity(activeAlternate), result.SelectedRouteIdentity);

        TrafficAwareRouteAdvisory advisory = Assert.Single(result.Advisories);
        Assert.Equal(normalIdentity, advisory.RouteIdentity);
        Assert.Contains("deprioritized to rank 2", advisory.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("here", advisory.ProviderIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("flow-deprioritized-42", advisory.SourceEventIds);
        Assert.Contains("here", advisory.Message, StringComparison.OrdinalIgnoreCase);
        RouteModifierImpact impact = Assert.Single(advisory.Impacts);
        Assert.Equal(RouteModifierImpactKind.TrafficDelay, impact.Kind);
        Assert.Equal(normalIdentity, impact.RouteKey);
    }

    [Fact]
    public async Task PlanAsync_AppliesClosureAndDelaySwitchPolicy()
    {
        OsmRouteCandidate normal = Candidate(900, 10_000d, 1, 2);
        OsmRouteCandidate alternate = Candidate(1_100, 11_000d, 3, 4);
        string normalIdentity = Identity(normal);

        TrafficAwareRouteSetPlan closureReroute = await Plan(
            [normal, alternate],
            [
                Candidate(1_250, 10_000d, RouteDurationSource.LiveTraffic, 350, 1, 2),
                Candidate(1_300, 11_000d, RouteDurationSource.LiveTraffic, 0, 3, 4),
            ],
            [SourceFor(normal, RouteModifierImpactKind.RoadClosure, true, "here", "closure-1")],
            normalIdentity);
        Assert.Equal(TrafficAwareRouteSetStatus.Success, closureReroute.Status);
        Assert.True(closureReroute.AutomaticReplacement);
        Assert.Equal(Identity(alternate), closureReroute.SelectedRouteIdentity);

        TrafficAwareRouteSetPlan noSafeRoute = await Plan(
            [normal],
            [],
            [SourceFor(normal, RouteModifierImpactKind.RoadClosure, true, "here", "closure-2")],
            normalIdentity);
        Assert.Equal(TrafficAwareRouteSetStatus.NoSafeRouteAvailable, noSafeRoute.Status);
        Assert.Null(noSafeRoute.SelectedCandidate);

        TrafficAwareRouteSetPlan belowAbsoluteThreshold = await Plan(
            [normal, alternate],
            [
                Candidate(1_000, 10_000d, RouteDurationSource.LiveTraffic, 100, 1, 2),
                Candidate(890, 11_000d, RouteDurationSource.LiveTraffic, 0, 3, 4),
            ],
            [SourceFor(normal, RouteModifierImpactKind.TrafficDelay, false, "tomtom", "flow-1")],
            normalIdentity);
        Assert.Equal(TrafficAwareRouteSetStatus.AdvisoryOnly, belowAbsoluteThreshold.Status);
        Assert.False(belowAbsoluteThreshold.AutomaticReplacement);
        Assert.Equal(normalIdentity, belowAbsoluteThreshold.SelectedRouteIdentity);

        TrafficAwareRouteSetPlan meetsBothThresholds = await Plan(
            [normal, alternate],
            [
                Candidate(1_000, 10_000d, RouteDurationSource.LiveTraffic, 100, 1, 2),
                Candidate(850, 11_000d, RouteDurationSource.LiveTraffic, 0, 3, 4),
            ],
            [SourceFor(normal, RouteModifierImpactKind.TrafficDelay, false, "tomtom", "flow-2")],
            normalIdentity);
        Assert.Equal(TrafficAwareRouteSetStatus.Success, meetsBothThresholds.Status);
        Assert.True(meetsBothThresholds.AutomaticReplacement);
        Assert.Equal(Identity(alternate), meetsBothThresholds.SelectedRouteIdentity);

        TrafficAwareRouteSetPlan belowRatioThreshold = await Plan(
            [Candidate(3_000, 10_000d, 1, 2), alternate],
            [
                Candidate(3_000, 10_000d, RouteDurationSource.LiveTraffic, 300, 1, 2),
                Candidate(2_850, 11_000d, RouteDurationSource.LiveTraffic, 0, 3, 4),
            ],
            [SourceFor(normal, RouteModifierImpactKind.TrafficDelay, false, "tomtom", "flow-3")],
            normalIdentity);
        Assert.Equal(TrafficAwareRouteSetStatus.AdvisoryOnly, belowRatioThreshold.Status);
        Assert.False(belowRatioThreshold.AutomaticReplacement);
        Assert.Equal(normalIdentity, belowRatioThreshold.SelectedRouteIdentity);
    }

    private static async Task<TrafficAwareRouteSetPlan> Plan(
        IReadOnlyList<OsmRouteCandidate> baseline,
        IReadOnlyList<OsmRouteCandidate> active,
        IReadOnlyList<TrafficRouteModifierSource> sources,
        string currentRouteIdentity)
    {
        var client = new RecordingRoutingClient(
            new OsmRouteResult(baseline, null),
            new OsmRouteResult(active, null));
        var planner = new TrafficAwareRouteSetPlanner(client, new StaticTimeProvider(Departure));
        return await planner.PlanAsync(
            new TrafficAwareRouteSetRequest(
                RouteRequest(),
                Snapshot(),
                sources,
                CurrentRouteIdentity: currentRouteIdentity),
            TestContext.Current.CancellationToken);
    }

    private static OsmRouteRequest RouteRequest() =>
        new(
            null,
            new GeoCoordinate(36.1263d, -86.6774d),
            new GeoCoordinate(36.1627d, -86.7816d));

    private static TrafficSnapshotReference Snapshot() =>
        new(
            new string('A', 64),
            new string('B', 64),
            Path.Combine(Path.GetTempPath(), "traffic-planner-fixture"),
            Departure.AddMinutes(-1),
            Departure.AddMinutes(10),
            TrafficSnapshotPolicy.Enabled);

    private static OsmRouteCandidate Candidate(
        int durationSeconds,
        double distanceMeters,
        params ulong[] directedEdges) =>
        Candidate(
            durationSeconds,
            distanceMeters,
            RouteDurationSource.FreeFlow,
            0,
            directedEdges);

    private static OsmRouteCandidate Candidate(
        int durationSeconds,
        double distanceMeters,
        RouteDurationSource durationSource,
        int engineAppliedDelaySeconds,
        params ulong[] directedEdges) =>
        new(
            distanceMeters,
            durationSeconds,
            null,
            [],
            [],
            new OsmRouteFrictionInputs(4, 0, 0, 0, false, false, false))
        {
            DirectedEdgeIds = Array.AsReadOnly(directedEdges),
            DurationSource = durationSource,
            TrafficSnapshotVersion = durationSource == RouteDurationSource.LiveTraffic
                ? new string('B', 64)
                : null,
            EngineAppliedTrafficDelaySeconds = engineAppliedDelaySeconds,
        };

    private static string Identity(OsmRouteCandidate candidate) =>
        RouteIdentity.Create(new RouteCandidateMetrics(
            "test",
            0,
            candidate.DistanceMeters,
            candidate.DurationSeconds,
            DirectedEdgeIds: candidate.DirectedEdgeIds,
            DurationSource: candidate.DurationSource));

    private static TrafficRouteModifierSource SourceFor(
        OsmRouteCandidate candidate,
        RouteModifierImpactKind kind,
        bool hardDeny,
        string providerId,
        string sourceEventId)
    {
        ulong edge = candidate.DirectedEdgeIds![0];
        return new TrafficRouteModifierSource(
            new RouteModifierImpact(string.Empty, kind, $"{providerId}:{sourceEventId}", hardDeny),
            [providerId],
            [sourceEventId],
            [
                new ValhallaTrafficEdgeUpdate(
                    0,
                    0,
                    TrafficDirection.Forward,
                    null,
                    null,
                    null,
                    hardDeny,
                    kind == RouteModifierImpactKind.Incident,
                    true,
                    1d,
                    sourceEventId,
                    providerId,
                    edge),
            ],
            kind == RouteModifierImpactKind.TrafficDelay ? 120 : null,
            hardDeny ? TrafficSeverity.Closed : TrafficSeverity.Heavy);
    }

    private sealed class RecordingRoutingClient(params OsmRouteResult[] results) : IOsmRoutingClient
    {
        private readonly Queue<OsmRouteResult> _results = new(results);

        public List<OsmRouteRequest> Requests { get; } = [];

        public Task<OsmRouteResult> CalculateRouteAsync(
            OsmRouteRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class StaticTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
