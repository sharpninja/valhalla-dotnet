using System.Reflection;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class LaneTopologyOverlayProjectionTests
{
    [Fact]
    public async Task ProjectAsync_ExactOverlayTransition_PrecedesInferenceAndRetainsProvenance()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create(
                omitFirstTransitionConnectivity: true);
        var context = new ValhallaGraphTrafficContext(
            "overlay-precedence-graph",
            fixture.Directory);
        CanonicalLaneTopologyOverlay overlay = await CreateOverlayAsync(
            fixture,
            context,
            transitionIndexes: [0]);

        var source = new DelegateOverlaySource(
            _ => LaneTopologyOverlayLoadResult.Loaded(overlay));
        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(source);
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Car,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(projection.HasRouteLanePath);
        Assert.Equal(
            "CanonicalOverlay",
            projection.TransitionDerivations[0].Provenance.ToString());
        Assert.NotEqual(
            "CanonicalOverlay",
            projection.TransitionDerivations[1].Provenance.ToString());
        LaneTopologyOverlayDescriptor sourceDescriptor =
            ReadOverlaySource(projection.TransitionDerivations[0]);
        Assert.Equal("test-overlay", sourceDescriptor.DatasetId);
        Assert.Equal("2026.07.18", sourceDescriptor.DatasetVersion);
        Assert.Equal(LaneTopologyOverlayProvenance.Test, sourceDescriptor.Provenance);
        Assert.Single(
            projection.TransitionDerivations[0].Evidence,
            evidence => evidence.Kind.ToString() == "CanonicalOverlayDataset");
    }

    [Fact]
    public async Task ProjectAsync_OverlaySignatureMismatchFailsClosedWithTypedDiagnostic()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create();
        var context = new ValhallaGraphTrafficContext(
            "active-graph-signature",
            fixture.Directory);
        CanonicalLaneTopologyOverlay valid = await CreateOverlayAsync(
            fixture,
            context,
            transitionIndexes: [0]);
        CanonicalLaneTopologyOverlay mismatched = valid with
        {
            Descriptor = valid.Descriptor with { GraphSignature = "stale-graph-signature" },
        };

        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(
            new DelegateOverlaySource(
                _ => LaneTopologyOverlayLoadResult.Loaded(mismatched)));
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Truck,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("CanonicalOverlayMismatch", projection.FailureReason.ToString());
        Assert.Equal(0, projection.Profile.Score);
        Assert.Empty(projection.Profile.Guidance);
        Assert.Contains(
            ReadOverlayDiagnostics(projection),
            diagnostic =>
                diagnostic.Code == LaneTopologyOverlayDiagnosticCode.GraphSignatureMismatch);
    }

    [Fact]
    public async Task ProjectAsync_DuplicateOverlayFrictionPointFailsClosed()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create();
        var context = new ValhallaGraphTrafficContext(
            "duplicate-friction-graph",
            fixture.Directory);
        CanonicalLaneTopologyOverlay baseline = await CreateOverlayAsync(
            fixture,
            context,
            transitionIndexes: []);
        LaneTopologySegment segment = await ReadSegmentAsync(
            fixture,
            context,
            fixture.DirectedEdgeIds[1]);
        var point = new CanonicalLaneFrictionOverlay(
            fixture.DirectedEdgeIds[1],
            LaneNumber: 2,
            DistanceAlongEdgeMeters: Math.Min(100d, segment.LengthMeters),
            LaneFrictionContributionKind.AdjacentMerge,
            Severity: 7,
            TruckSensitive: true,
            Rationale: "duplicate hostile point");
        CanonicalLaneTopologyOverlay duplicate = baseline with
        {
            FrictionPoints = [point, point],
        };

        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(
            new DelegateOverlaySource(
                _ => LaneTopologyOverlayLoadResult.Loaded(duplicate)));
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Car,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("CanonicalOverlayMismatch", projection.FailureReason.ToString());
        Assert.Equal(0, projection.Profile.Score);
        Assert.Empty(projection.Profile.Guidance);
        Assert.Contains(
            ReadOverlayDiagnostics(projection),
            diagnostic => diagnostic.Code.ToString() == "DuplicateCanonicalFrictionPoint");
    }

    [Fact]
    public async Task ProjectAsync_OverlayDuplicateOfGraphPointIsCountedOnceAndRetainsSource()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create(
                routeUsesNonThroughLane: true);
        var context = new ValhallaGraphTrafficContext(
            "overlay-deduplication-graph",
            fixture.Directory);
        OsmRouteCandidate candidate = CreateCandidate(fixture.DirectedEdgeIds);

        using var baselineIndex = new GraphTileLaneTopologyIndex();
        var baselineProjector = new ValhallaRouteLaneFrictionProjector(baselineIndex);
        RouteLaneFrictionProjection baseline = await baselineProjector.ProjectAsync(
            candidate,
            LaneFrictionVehicleClass.Car,
            context,
            TestContext.Current.CancellationToken);
        CanonicalLaneFrictionPoint graphPoint = baseline.CanonicalPoints.First(
            point => baseline.Profile.Contributions.Any(contribution =>
                contribution.Kind == point.Kind &&
                contribution.SegmentId == point.SegmentId &&
                contribution.LaneNumber == point.LaneNumber));

        CanonicalLaneTopologyOverlay overlay = await CreateOverlayAsync(
            fixture,
            context,
            transitionIndexes: []);
        CanonicalLaneTopologyOverlay withDuplicate = overlay with
        {
            FrictionPoints =
            [
                new CanonicalLaneFrictionOverlay(
                    Convert.ToUInt64(graphPoint.SegmentId, 16),
                    graphPoint.LaneNumber,
                    graphPoint.DistanceAlongSegmentMeters,
                    graphPoint.Kind,
                    graphPoint.Severity,
                    graphPoint.TruckSensitive,
                    graphPoint.Description),
            ],
        };

        using GraphTileLaneTopologyIndex overlayIndex = CreateOverlayIndex(
            new DelegateOverlaySource(
                _ => LaneTopologyOverlayLoadResult.Loaded(withDuplicate)));
        var overlayProjector = new ValhallaRouteLaneFrictionProjector(overlayIndex);

        RouteLaneFrictionProjection projected = await overlayProjector.ProjectAsync(
            candidate,
            LaneFrictionVehicleClass.Car,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(baseline.CanonicalPoints.Count, projected.CanonicalPoints.Count);
        Assert.Equal(baseline.Profile.Score, projected.Profile.Score);
        CanonicalLaneFrictionPoint retainedPoint = Assert.Single(
            projected.CanonicalPoints,
            point =>
                point.SegmentId == graphPoint.SegmentId &&
                point.LaneNumber == graphPoint.LaneNumber &&
                point.Kind == graphPoint.Kind);
        Assert.Equal("test-overlay", ReadOverlaySource(retainedPoint).DatasetId);
        LaneFrictionContribution contribution = Assert.Single(
            projected.Profile.Contributions,
            item =>
                item.SegmentId == graphPoint.SegmentId &&
                item.LaneNumber == graphPoint.LaneNumber &&
                item.Kind == graphPoint.Kind);
        Assert.Equal("test-overlay", ReadOverlaySource(contribution).DatasetId);
    }

    [Fact]
    public async Task ReadAsync_OverlaySnapshotCacheEvictsWithGraphSignatureAndReloadsCleanly()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create();
        ValhallaLaneTopologySnapshot graph = await ReadSnapshotAsync(
            fixture,
            new ValhallaGraphTrafficContext("seed", fixture.Directory));
        var source = new DelegateOverlaySource(request =>
            LaneTopologyOverlayLoadResult.Loaded(
                CreateOverlay(graph, request.GraphSignature, transitionIndexes: [])));
        var options = new GraphTileLaneTopologyIndexOptions(
            MaximumGraphSignatures: 1,
            MaximumDirectedEdgesPerGraph: 32,
            MaximumTiles: 4,
            MaximumTransitionContexts: 32,
            MaximumConcurrentBuilds: 2);
        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(source, options);

        await index.ReadAsync(
            new ValhallaGraphTrafficContext("graph-a", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
        await index.ReadAsync(
            new ValhallaGraphTrafficContext("graph-a", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
        await index.ReadAsync(
            new ValhallaGraphTrafficContext("graph-b", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
        await index.ReadAsync(
            new ValhallaGraphTrafficContext("graph-a", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, source.CallCount);
        Assert.Equal(1, index.CachedGraphSignatureCount);
    }

    [Fact]
    public async Task ReadAsync_OverlayLoadHonorsCallerCancellation()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create();
        var source = new CancelingOverlaySource();
        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            index.ReadAsync(
                new ValhallaGraphTrafficContext(
                    "cancelled-overlay-graph",
                    fixture.Directory),
                fixture.DirectedEdgeIds,
                cancellation.Token));
    }


    [Fact]
    public async Task ProjectAsync_IncompleteConnectivityScoresOnlyOverlayEventProvenAcrossEveryLane()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create(
                omitFirstTransitionConnectivity: true);
        var context = new ValhallaGraphTrafficContext(
            "partial-overlay-lower-bound-graph",
            fixture.Directory);
        CanonicalLaneTopologyOverlay baseline = await CreateOverlayAsync(
            fixture,
            context,
            transitionIndexes: []);
        LaneTopologySegment segment = await ReadSegmentAsync(
            fixture,
            context,
            fixture.DirectedEdgeIds[0]);
        CanonicalLaneFrictionOverlay[] points = Enumerable.Range(1, segment.LaneCount)
            .Select(lane => new CanonicalLaneFrictionOverlay(
                fixture.DirectedEdgeIds[0],
                lane,
                DistanceAlongEdgeMeters: 75d,
                LaneFrictionContributionKind.AdjacentMerge,
                Severity: 5,
                TruckSensitive: true,
                Rationale: "curated event affects every lane"))
            .ToArray();
        CanonicalLaneTopologyOverlay overlay = baseline with { FrictionPoints = points };
        var source = new DelegateOverlaySource(
            _ => LaneTopologyOverlayLoadResult.Loaded(overlay));
        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(source);
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Truck,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            LaneProjectionFailureReason.MissingLaneConnectivity,
            projection.FailureReason);
        Assert.False(projection.HasRouteLanePath);
        Assert.Empty(projection.RouteSegments);
        Assert.Empty(projection.Profile.Guidance);
        Assert.Equal(9, projection.Profile.Score);
        Assert.Equal(1, projection.Profile.CanonicalPointCount);
        Assert.Equal(1, projection.Profile.AdjacentMergeCount);
        LaneFrictionContribution contribution = Assert.Single(
            projection.Profile.Contributions);
        Assert.Equal(LaneFrictionContributionKind.AdjacentMerge, contribution.Kind);
        Assert.Equal(overlay.Descriptor, ReadOverlaySource(contribution));
    }

    [Fact]
    public async Task ProjectAsync_IncompleteConnectivityDoesNotScoreLaneSpecificOverlayEvent()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create(
                omitFirstTransitionConnectivity: true);
        var context = new ValhallaGraphTrafficContext(
            "partial-overlay-lane-specific-graph",
            fixture.Directory);
        CanonicalLaneTopologyOverlay baseline = await CreateOverlayAsync(
            fixture,
            context,
            transitionIndexes: []);
        CanonicalLaneTopologyOverlay overlay = baseline with
        {
            FrictionPoints =
            [
                new CanonicalLaneFrictionOverlay(
                    fixture.DirectedEdgeIds[0],
                    LaneNumber: 1,
                    DistanceAlongEdgeMeters: 75d,
                    LaneFrictionContributionKind.AdjacentMerge,
                    Severity: 5,
                    TruckSensitive: true,
                    Rationale: "lane-specific event"),
            ],
        };
        var source = new DelegateOverlaySource(
            _ => LaneTopologyOverlayLoadResult.Loaded(overlay));
        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(source);
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Truck,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            LaneProjectionFailureReason.MissingLaneConnectivity,
            projection.FailureReason);
        Assert.Empty(projection.Profile.Guidance);
        Assert.Empty(projection.Profile.Contributions);
        Assert.Equal(0, projection.Profile.Score);
    }


    [Fact]
    public async Task ProjectAsync_HugeDistanceAndSeverityFailClosedWithoutScoring()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create();
        var context = new ValhallaGraphTrafficContext(
            "hostile-overlay-values-graph",
            fixture.Directory);
        CanonicalLaneTopologyOverlay baseline = await CreateOverlayAsync(
            fixture,
            context,
            transitionIndexes: []);
        CanonicalLaneTopologyOverlay hostile = baseline with
        {
            FrictionPoints =
            [
                new CanonicalLaneFrictionOverlay(
                    fixture.DirectedEdgeIds[0],
                    LaneNumber: 1,
                    DistanceAlongEdgeMeters: 1e308,
                    LaneFrictionContributionKind.Weave,
                    Severity: int.MaxValue,
                    TruckSensitive: true,
                    Rationale: "hostile values"),
            ],
        };
        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(
            new DelegateOverlaySource(
                _ => LaneTopologyOverlayLoadResult.Loaded(hostile)));
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Truck,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            LaneProjectionFailureReason.CanonicalOverlayMismatch,
            projection.FailureReason);
        Assert.Equal(0, projection.Profile.Score);
        Assert.Empty(projection.Profile.Contributions);
        Assert.Empty(projection.Profile.Guidance);
        Assert.Contains(
            projection.OverlayDiagnostics,
            static diagnostic =>
                diagnostic.Code == LaneTopologyOverlayDiagnosticCode.LaneOutOfRange);
        Assert.Contains(
            projection.OverlayDiagnostics,
            static diagnostic =>
                diagnostic.Code == LaneTopologyOverlayDiagnosticCode.InvalidMetadata);
    }

    [Fact]
    public async Task ReadAsync_InvalidOverlayEntryOutsideRequestedRouteStillFailsClosed()
    {
        using var fixture =
            GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture.Create();
        var context = new ValhallaGraphTrafficContext(
            "validate-before-scope-graph",
            fixture.Directory);
        CanonicalLaneTopologyOverlay baseline = await CreateOverlayAsync(
            fixture,
            context,
            transitionIndexes: []);
        ulong outsideRouteEdge = fixture.DirectedEdgeIds[^1];
        CanonicalLaneTopologyOverlay hostile = baseline with
        {
            Edges = baseline.Edges
                .Select(edge => edge.CanonicalDirectedEdgeId == outsideRouteEdge
                    ? edge with
                    {
                        CanonicalStartNodeId = edge.CanonicalStartNodeId + 1,
                    }
                    : edge)
                .ToArray(),
        };
        using GraphTileLaneTopologyIndex index = CreateOverlayIndex(
            new DelegateOverlaySource(
                _ => LaneTopologyOverlayLoadResult.Loaded(hostile)));

        ValhallaLaneTopologySnapshot snapshot = await index.ReadAsync(
            context,
            fixture.DirectedEdgeIds.Take(2).ToArray(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            LaneTopologyOverlayLoadStatus.Invalid,
            snapshot.OverlayLoadResult.Status);
        Assert.Contains(
            snapshot.OverlayLoadResult.Diagnostics,
            static diagnostic =>
                diagnostic.Code == LaneTopologyOverlayDiagnosticCode.CanonicalNodeMismatch);
    }

    private static GraphTileLaneTopologyIndex CreateOverlayIndex(
        ILaneTopologyOverlaySource source,
        GraphTileLaneTopologyIndexOptions? options = null)
    {
        ConstructorInfo? constructor = typeof(GraphTileLaneTopologyIndex).GetConstructor(
            [typeof(ILaneTopologyOverlaySource), typeof(GraphTileLaneTopologyIndexOptions)]);
        Assert.NotNull(constructor);
        return Assert.IsType<GraphTileLaneTopologyIndex>(
            constructor.Invoke([source, options ?? GraphTileLaneTopologyIndexOptions.Default]));
    }

    private static IReadOnlyList<LaneTopologyOverlayDiagnostic> ReadOverlayDiagnostics(
        RouteLaneFrictionProjection projection)
    {
        PropertyInfo? property = typeof(RouteLaneFrictionProjection).GetProperty(
            "OverlayDiagnostics");
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<IReadOnlyList<LaneTopologyOverlayDiagnostic>>(
            property.GetValue(projection));
    }

    private static LaneTopologyOverlayDescriptor ReadOverlaySource(object value)
    {
        PropertyInfo? property = value.GetType().GetProperty("OverlaySource");
        Assert.NotNull(property);
        return Assert.IsType<LaneTopologyOverlayDescriptor>(property.GetValue(value));
    }

    private static async Task<CanonicalLaneTopologyOverlay> CreateOverlayAsync(
        GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture fixture,
        ValhallaGraphTrafficContext context,
        IReadOnlyList<int> transitionIndexes)
    {
        ValhallaLaneTopologySnapshot snapshot = await ReadSnapshotAsync(fixture, context);
        return CreateOverlay(snapshot, context.GraphSignature, transitionIndexes);
    }

    private static CanonicalLaneTopologyOverlay CreateOverlay(
        ValhallaLaneTopologySnapshot snapshot,
        string graphSignature,
        IReadOnlyList<int> transitionIndexes)
    {
        ulong[] orderedIds = snapshot.Edges.Keys.Order().ToArray();
        CanonicalLaneEdgeOverlay[] edges = snapshot.Edges
            .OrderBy(static pair => pair.Key)
            .Select(static pair =>
            {
                LaneTopologyGraphEvidence evidence = Assert.IsType<LaneTopologyGraphEvidence>(
                    pair.Value.GraphEvidence);
                return new CanonicalLaneEdgeOverlay(
                    pair.Key,
                    evidence.CanonicalStartNodeId,
                    evidence.CanonicalEndNodeId,
                    pair.Value.LaneCount);
            })
            .ToArray();
        CanonicalLaneTransitionOverlay[] transitions = transitionIndexes
            .Select(index =>
            {
                ulong fromId = orderedIds[index];
                ulong toId = orderedIds[index + 1];
                LaneTopologySegment from = snapshot.Edges[fromId];
                LaneTopologySegment to = snapshot.Edges[toId];
                LaneTopologyGraphEvidence fromEvidence =
                    Assert.IsType<LaneTopologyGraphEvidence>(from.GraphEvidence);
                int laneCount = Math.Min(from.LaneCount, to.LaneCount);
                return new CanonicalLaneTransitionOverlay(
                    fromId,
                    toId,
                    fromEvidence.CanonicalEndNodeId,
                    Enumerable.Range(1, laneCount)
                        .Select(static lane => new LaneTransitionOption(lane, lane))
                        .ToArray(),
                    LaneTopologyChangeKind.Continuation,
                    TruckSensitive: false,
                    Rationale: "test exact canonical transition");
            })
            .ToArray();

        return new CanonicalLaneTopologyOverlay(
            new LaneTopologyOverlayDescriptor(
                SchemaVersion: 1,
                DatasetId: "test-overlay",
                DatasetVersion: "2026.07.18",
                GraphSignature: graphSignature,
                Provenance: LaneTopologyOverlayProvenance.Test,
                SourceReference: "test://lane-overlay"),
            edges,
            transitions,
            []);
    }

    private static async Task<ValhallaLaneTopologySnapshot> ReadSnapshotAsync(
        GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture fixture,
        ValhallaGraphTrafficContext context)
    {
        using var index = new GraphTileLaneTopologyIndex();
        return await index.ReadAsync(
            context,
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
    }

    private static async Task<LaneTopologySegment> ReadSegmentAsync(
        GraphTileLaneFrictionProjectionTests.GeneratedLaneGraphFixture fixture,
        ValhallaGraphTrafficContext context,
        ulong edgeId)
    {
        ValhallaLaneTopologySnapshot snapshot = await ReadSnapshotAsync(fixture, context);
        return snapshot.Edges[edgeId];
    }

    private static OsmRouteCandidate CreateCandidate(IReadOnlyList<ulong> directedEdgeIds)
        => new(
            DistanceMeters: 1_800d,
            DurationSeconds: 180,
            EncodedPolyline: null,
            RoutePoints: [],
            Maneuvers: [],
            FrictionInputs: new OsmRouteFrictionInputs(0, 0, 0, 0, false, false, false))
        {
            DirectedEdgeIds = directedEdgeIds,
        };

    private sealed class DelegateOverlaySource : ILaneTopologyOverlaySource
    {
        private readonly Func<LaneTopologyOverlayRequest, LaneTopologyOverlayLoadResult> _load;

        public DelegateOverlaySource(
            Func<LaneTopologyOverlayRequest, LaneTopologyOverlayLoadResult> load)
            => _load = load;

        public int CallCount { get; private set; }

        public ValueTask<LaneTopologyOverlayLoadResult> LoadAsync(
            LaneTopologyOverlayRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(_load(request));
        }
    }

    private sealed class CancelingOverlaySource : ILaneTopologyOverlaySource
    {
        public async ValueTask<LaneTopologyOverlayLoadResult> LoadAsync(
            LaneTopologyOverlayRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return LaneTopologyOverlayLoadResult.NotFound("unreachable");
        }
    }
}
