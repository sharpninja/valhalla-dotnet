using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class GraphTileLaneFrictionProjectionTests
{
    [Fact]
    public async Task ReadAsync_GeneratedGraphTile_ExtractsLaneDataByCanonicalDirectedEdgeId()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        using var index = new GraphTileLaneTopologyIndex();
        var context = new ValhallaGraphTrafficContext("generated-lane-graph", fixture.Directory);

        ValhallaLaneTopologySnapshot snapshot = await index.ReadAsync(
            context,
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);

        Assert.Equal(fixture.DirectedEdgeIds, snapshot.Edges.Keys);
        LaneTopologySegment middle = snapshot.Edges[fixture.DirectedEdgeIds[1]];
        Assert.Equal(3, middle.LaneCount);
        Assert.Equal(200UL, middle.OsmWayId);
        Assert.Equal([LaneTurnIntent.Right, LaneTurnIntent.Through, LaneTurnIntent.Through], middle.LaneIntents);
        LaneTopologyConnection incoming = Assert.Single(middle.IncomingConnections);
        Assert.Equal("100", incoming.FromSegmentId);
        Assert.Equal([1], incoming.FromLanes);
        Assert.Equal([1], incoming.ToLanes);
    }

    [Fact]
    public async Task ReadAsync_ReusesEntriesForTheExactGraphSignature()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        using var index = new GraphTileLaneTopologyIndex();
        var context = new ValhallaGraphTrafficContext("same-signature", fixture.Directory);

        await index.ReadAsync(context, fixture.DirectedEdgeIds, TestContext.Current.CancellationToken);
        await index.ReadAsync(context, fixture.DirectedEdgeIds.Reverse().ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(1, index.CachedGraphSignatureCount);
        Assert.Equal(3, index.CachedDirectedEdgeCount);
    }

    [Fact]
    public async Task ReadAsync_BoundedCachesEnforceCapsAndGraphSignaturesUseStrictFifo()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        var options = new GraphTileLaneTopologyIndexOptions
        {
            MaximumGraphSignatures = 2,
            MaximumDirectedEdgesPerGraph = 2,
            MaximumTiles = 2,
            MaximumTransitionContexts = 1,
            MaximumConcurrentBuilds = 1,
        };
        var tileLoads = 0;
        using var index = new GraphTileLaneTopologyIndex(
            (directory, tileId) =>
            {
                Interlocked.Increment(ref tileLoads);
                return GraphTile.Create(directory, tileId);
            },
            options);

        await index.ReadAsync(
            new ValhallaGraphTrafficContext("fifo-graph-1", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
        await index.ReadAsync(
            new ValhallaGraphTrafficContext("fifo-graph-2", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
        await index.ReadAsync(
            new ValhallaGraphTrafficContext("fifo-graph-1", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
        await index.ReadAsync(
            new ValhallaGraphTrafficContext("fifo-graph-3", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, index.CachedGraphSignatureCount);
        Assert.Equal(4, index.CachedDirectedEdgeCount);
        Assert.Equal(2, index.CachedTileCount);
        Assert.Equal(1, index.CachedTransitionContextCount);
        Assert.Equal(3, tileLoads);

        await index.ReadAsync(
            new ValhallaGraphTrafficContext("fifo-graph-1", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, tileLoads);
        Assert.Equal(2, index.CachedGraphSignatureCount);
        Assert.Equal(4, index.CachedDirectedEdgeCount);
        Assert.Equal(2, index.CachedTileCount);
        Assert.Equal(1, index.CachedTransitionContextCount);
    }

    [Fact]
    public async Task ProjectAsync_ProductionCandidateDirectedEdgesBuildsLaneProfileAndGuidance()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);
        OsmRouteCandidate candidate = CreateCandidate(fixture.DirectedEdgeIds);
        var context = new ValhallaGraphTrafficContext("generated-lane-graph", fixture.Directory);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            candidate,
            LaneFrictionVehicleClass.Truck,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(projection.HasTopologyData);
        Assert.Empty(projection.MissingDirectedEdgeIds);
        Assert.Equal(3, projection.RouteSegments.Count);
        Assert.True(projection.Profile.RouteLaneChangeCount >= 1);
        Assert.True(projection.Profile.Score > 0);
        Assert.Contains(
            projection.Profile.Guidance,
            static point => point.Instruction.Contains("Move from lane", StringComparison.Ordinal));
        Assert.Contains(
            projection.Profile.Contributions,
            static contribution => contribution.Kind == LaneFrictionContributionKind.AdjacentMerge);
    }

    [Fact]
    public async Task ReadAsync_RealMonacoFixtureMatchesGraphTileLaneCountAndWayId()
    {
        string root = FindMonacoFixture();
        Assert.True(
            Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        (ulong canonicalId, uint laneCount, ulong wayId) = FindRealEdge(root);
        using var index = new GraphTileLaneTopologyIndex();

        ValhallaLaneTopologySnapshot snapshot = await index.ReadAsync(
            new ValhallaGraphTrafficContext("monaco-real-graph", root),
            [canonicalId],
            TestContext.Current.CancellationToken);

        LaneTopologySegment edge = snapshot.Edges[canonicalId];
        Assert.Equal((int)Math.Max(1u, laneCount), edge.LaneCount);
        Assert.Equal(wayId, edge.OsmWayId);
    }

    [Fact]
    public async Task ProjectAsync_WhenConnectivityOffersChoices_SelectsLowerFrictionLane()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(allowLowerFrictionLaneChoice: true);
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Truck,
            new ValhallaGraphTrafficContext("generated-choice-graph", fixture.Directory),
            TestContext.Current.CancellationToken);

        RouteLaneSegment last = projection.RouteSegments[^1];
        Assert.Equal(1, last.EntryLane);
        Assert.DoesNotContain(
            projection.Profile.Contributions,
            static contribution => contribution.Kind == LaneFrictionContributionKind.AdjacentMerge);
    }

    [Fact]
    public async Task ProjectAsync_SingleEdge_DoesNotPenalizeTerminalTurnLane()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate([fixture.DirectedEdgeIds[1]]),
            LaneFrictionVehicleClass.Truck,
            new ValhallaGraphTrafficContext("generated-single-edge-graph", fixture.Directory),
            TestContext.Current.CancellationToken);

        RouteLaneSegment segment = Assert.Single(projection.RouteSegments);
        Assert.Equal(1, segment.EntryLane);
        Assert.Equal(1, segment.ExitLane);
        Assert.DoesNotContain(
            projection.Profile.Contributions,
            static contribution => contribution.Kind == LaneFrictionContributionKind.ExitOnlyLane);
    }

    [Fact]
    public async Task ReadAsync_CancelledWaiter_DoesNotPoisonSharedTileLoad()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        using var loaderStarted = new ManualResetEventSlim();
        using var releaseLoader = new ManualResetEventSlim();
        var loadCount = 0;
        using var index = new GraphTileLaneTopologyIndex((directory, tileId) =>
        {
            Interlocked.Increment(ref loadCount);
            loaderStarted.Set();
            Assert.True(releaseLoader.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return GraphTile.Create(directory, tileId);
        });
        var context = new ValhallaGraphTrafficContext("cancel-isolation-graph", fixture.Directory);
        using var cancelled = new CancellationTokenSource();

        Task<ValhallaLaneTopologySnapshot> first = index.ReadAsync(
            context,
            [fixture.DirectedEdgeIds[0]],
            cancelled.Token);
        Assert.True(loaderStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Task<ValhallaLaneTopologySnapshot> survivor = index.ReadAsync(
            context,
            [fixture.DirectedEdgeIds[0]],
            TestContext.Current.CancellationToken);

        cancelled.Cancel();
        releaseLoader.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        ValhallaLaneTopologySnapshot snapshot = await survivor;
        Assert.Contains(fixture.DirectedEdgeIds[0], snapshot.Edges);
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task ReadAsync_MultipleEdgesInOneTile_LoadsGraphTileOnce()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        var loadCount = 0;
        using var index = new GraphTileLaneTopologyIndex((directory, tileId) =>
        {
            Interlocked.Increment(ref loadCount);
            return GraphTile.Create(directory, tileId);
        });

        ValhallaLaneTopologySnapshot snapshot = await index.ReadAsync(
            new ValhallaGraphTrafficContext("single-tile-cache-graph", fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, snapshot.Edges.Count);
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task ProjectAsync_RouteTakingNonThroughLane_DoesNotPenalizeRequiredBranchAsExitOnly()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(routeUsesNonThroughLane: true);
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds.Take(2).ToArray()),
            LaneFrictionVehicleClass.Car,
            new ValhallaGraphTrafficContext("route-aware-turn-graph", fixture.Directory),
            TestContext.Current.CancellationToken);

        Assert.True(projection.HasRouteLanePath);
        Assert.Equal(1, projection.RouteSegments[0].ExitLane);
        Assert.DoesNotContain(
            projection.CanonicalPoints,
            point =>
                point.SegmentId == projection.RouteSegments[0].SegmentId &&
                point.LaneNumber == 1 &&
                point.Kind == LaneFrictionContributionKind.ExitOnlyLane);
    }

    [Fact]
    public async Task ProjectAsync_RepeatedDirectedEdge_AppliesCanonicalPointOncePerOccurrence()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(includeSelfConnectivity: true);
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);
        ulong repeated = fixture.DirectedEdgeIds[1];

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate([repeated, repeated]),
            LaneFrictionVehicleClass.Car,
            new ValhallaGraphTrafficContext("repeated-edge-graph", fixture.Directory),
            TestContext.Current.CancellationToken);

        Assert.True(projection.HasRouteLanePath);
        Assert.Single(
            projection.CanonicalPoints,
            point => point.Kind == LaneFrictionContributionKind.RouteSplit && point.LaneNumber == 2);
        Assert.Equal(
            2,
            projection.Profile.Contributions.Count(
                contribution => contribution.Kind == LaneFrictionContributionKind.RouteSplit));
    }

    [Fact]
    public async Task ProjectAsync_RepeatedEdgeWeave_AppliesOnlyToTheChangingOccurrence()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(
            includeSelfConnectivity: true,
            includeWeaveTopology: true);
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);
        ulong first = fixture.DirectedEdgeIds[0];
        ulong repeated = fixture.DirectedEdgeIds[1];

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate([first, repeated, repeated]),
            LaneFrictionVehicleClass.Truck,
            new ValhallaGraphTrafficContext("repeated-weave-graph", fixture.Directory),
            TestContext.Current.CancellationToken);

        RouteLaneFrictionModifier modifier = Assert.Single(
            projection.RouteModifiers,
            static candidate => candidate.Kind == LaneFrictionContributionKind.Weave);
        Assert.Equal(1, modifier.RouteSegmentOccurrenceIndex);
        Assert.Single(
            projection.Profile.Contributions,
            static contribution => contribution.Kind == LaneFrictionContributionKind.Weave);
    }

    [Fact]
    public async Task ProjectAsync_MissingLaneConnectivity_SuppressesScoredPathAndGuidance()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(omitSecondTransitionConnectivity: true);
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Truck,
            new ValhallaGraphTrafficContext("missing-connectivity-graph", fixture.Directory),
            TestContext.Current.CancellationToken);

        Assert.True(projection.HasTopologyData);
        Assert.False(projection.HasRouteLanePath);
        Assert.True(projection.UsedFallbackConnectivity);
        Assert.Equal(LaneProjectionFailureReason.MissingLaneConnectivity, projection.FailureReason);
        Assert.Empty(projection.RouteSegments);
        Assert.Empty(projection.Profile.Contributions);
        Assert.Empty(projection.Profile.Guidance);
    }

    [Fact]
    public async Task ReadAsync_UnreadableCompetingOutboundEdge_MarksContextIncompleteAndSuppressesUniqueness()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(
            declareMissingOutboundEdge: true,
            sameWayFirstTransition: true,
            omitFirstTransitionConnectivity: true);
        using var index = new GraphTileLaneTopologyIndex();
        var context = new ValhallaGraphTrafficContext(
            "incomplete-outbound-graph",
            fixture.Directory);

        ValhallaLaneTopologySnapshot snapshot = await index.ReadAsync(
            context,
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
        var transitionKey = new LaneTransitionKey(
            fixture.DirectedEdgeIds[0],
            fixture.DirectedEdgeIds[1]);
        LaneTransitionTopologyContext topologyContext = snapshot.TransitionContexts[transitionKey];

        Assert.False(topologyContext.OutboundEdgesComplete);
        Assert.Equal(
            LaneTransitionTopologyContextSource.IncompleteGraphTile,
            topologyContext.Source);

        var projector = new ValhallaRouteLaneFrictionProjector(index);
        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Car,
            context,
            TestContext.Current.CancellationToken);

        Assert.False(projection.HasRouteLanePath);
        Assert.Equal(
            LaneProjectionFailureReason.MissingLaneConnectivity,
            projection.FailureReason);
        LaneTransitionDerivation firstDerivation = projection.TransitionDerivations[0];
        Assert.False(firstDerivation.CanDriveGuidance);
        Assert.Equal(
            LaneTransitionProvenance.Unavailable,
            firstDerivation.Provenance);
    }

    [Fact]
    public async Task ReadAsync_UnreadableOpposingEdge_MarksInboundContextIncomplete()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(
            unreadableOpposingEdge: true);
        using var index = new GraphTileLaneTopologyIndex();

        ValhallaLaneTopologySnapshot snapshot = await index.ReadAsync(
            new ValhallaGraphTrafficContext(
                "incomplete-opposing-graph",
                fixture.Directory),
            fixture.DirectedEdgeIds,
            TestContext.Current.CancellationToken);
        var transitionKey = new LaneTransitionKey(
            fixture.DirectedEdgeIds[0],
            fixture.DirectedEdgeIds[1]);
        LaneTransitionTopologyContext topologyContext = snapshot.TransitionContexts[transitionKey];

        Assert.False(topologyContext.InboundEdgesComplete);
        Assert.Equal(
            LaneTransitionTopologyContextSource.IncompleteGraphTile,
            topologyContext.Source);
    }

    [Fact]
    public async Task ProjectAsync_GraphDerivedCanonicalPoints_AreTruckSensitive()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);
        OsmRouteCandidate candidate = CreateCandidate([fixture.DirectedEdgeIds[1]]);
        var context = new ValhallaGraphTrafficContext("truck-sensitive-graph", fixture.Directory);

        RouteLaneFrictionProjection car = await projector.ProjectAsync(
            candidate,
            LaneFrictionVehicleClass.Car,
            context,
            TestContext.Current.CancellationToken);
        RouteLaneFrictionProjection truck = await projector.ProjectAsync(
            candidate,
            LaneFrictionVehicleClass.Truck,
            context,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(car.CanonicalPoints);
        Assert.All(car.CanonicalPoints, static point => Assert.True(point.TruckSensitive));
        Assert.True(truck.Profile.Score > car.Profile.Score);
    }

    [Fact]
    public async Task ProjectAsync_LaneChangeAcrossGraphMerge_ProducesWeaveModifier()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(includeWeaveTopology: true);
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            CreateCandidate(fixture.DirectedEdgeIds),
            LaneFrictionVehicleClass.Truck,
            new ValhallaGraphTrafficContext("weave-graph", fixture.Directory),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            projection.RouteModifiers,
            static modifier => modifier.Kind == LaneFrictionContributionKind.Weave);
        Assert.Contains(
            projection.Profile.Contributions,
            static contribution => contribution.Kind == LaneFrictionContributionKind.Weave);
        Assert.Contains(
            projection.CanonicalPoints,
            static point => point.Kind == LaneFrictionContributionKind.RouteSplit);
    }

    [Fact]
    public async Task ProjectAsync_ShortMultiLaneCrossing_IsRejectedForTruckButAllowedForCar()
    {
        using var fixture = GeneratedLaneGraphFixture.Create(
            middleExitLane: 3,
            middleLengthMeters: 120);
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);
        OsmRouteCandidate candidate = CreateCandidate(fixture.DirectedEdgeIds);
        var context = new ValhallaGraphTrafficContext("lane-change-feasibility-graph", fixture.Directory);

        RouteLaneFrictionProjection car = await projector.ProjectAsync(
            candidate,
            LaneFrictionVehicleClass.Car,
            context,
            TestContext.Current.CancellationToken);
        RouteLaneFrictionProjection truck = await projector.ProjectAsync(
            candidate,
            LaneFrictionVehicleClass.Truck,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(car.HasRouteLanePath);
        Assert.False(truck.HasRouteLanePath);
        Assert.Equal(LaneProjectionFailureReason.InfeasibleLaneChanges, truck.FailureReason);
        Assert.Empty(truck.Profile.Guidance);
    }

    [Fact]
    public async Task ProjectAsync_MissingCanonicalEdgeReturnsUnavailableInsteadOfStitchingNonAdjacentEdges()
    {
        using var fixture = GeneratedLaneGraphFixture.Create();
        using var index = new GraphTileLaneTopologyIndex();
        var projector = new ValhallaRouteLaneFrictionProjector(index);
        var first = new GraphId(fixture.DirectedEdgeIds[0]);
        ulong missingId = new GraphId(first.Tileid(), first.Level(), 99).Value;
        OsmRouteCandidate candidate = CreateCandidate(
            [fixture.DirectedEdgeIds[0], missingId, fixture.DirectedEdgeIds[2]]);

        RouteLaneFrictionProjection projection = await projector.ProjectAsync(
            candidate,
            LaneFrictionVehicleClass.Car,
            new ValhallaGraphTrafficContext("generated-lane-graph", fixture.Directory),
            TestContext.Current.CancellationToken);

        Assert.False(projection.HasTopologyData);
        Assert.Empty(projection.RouteSegments);
        Assert.Equal([missingId], projection.MissingDirectedEdgeIds);
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

    private static string FindMonacoFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "artifacts", "valhalla-monaco-tiles")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? string.Empty
            : Path.Combine(directory.FullName, "artifacts", "valhalla-monaco-tiles");
    }

    private static (ulong CanonicalId, uint LaneCount, ulong WayId) FindRealEdge(string root)
    {
        foreach (string file in Directory.GetFiles(root, "*.gph", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string[] parts = relative.Split('/');
            if (!byte.TryParse(parts[0], out byte level))
            {
                continue;
            }

            string digits = string.Concat(parts.Skip(1).Select(static part => part.Replace(".gph", string.Empty, StringComparison.Ordinal)));
            var tileId = new GraphId(uint.Parse(digits, System.Globalization.CultureInfo.InvariantCulture), level, 0);
            GraphTile? tile = GraphTile.Create(root, tileId);
            if (tile is null || tile.DirectedEdgeCount() == 0)
            {
                continue;
            }

            DirectedEdge edge = tile.DirectedEdge(0);
            return (
                new GraphId(tileId.Tileid(), tileId.Level(), 0).Value,
                edge.LaneCount,
                tile.EdgeInfo(edge).WayId);
        }

        throw new Xunit.Sdk.XunitException("Could not find a directed edge in the Monaco graph fixture.");
    }

    internal sealed class GeneratedLaneGraphFixture : IDisposable
    {
        private GeneratedLaneGraphFixture(string directory, IReadOnlyList<ulong> directedEdgeIds)
        {
            Directory = directory;
            DirectedEdgeIds = directedEdgeIds;
        }

        public string Directory { get; }

        public IReadOnlyList<ulong> DirectedEdgeIds { get; }

        public static GeneratedLaneGraphFixture Create(
            bool allowLowerFrictionLaneChoice = false,
            bool routeUsesNonThroughLane = false,
            bool includeSelfConnectivity = false,
            bool omitSecondTransitionConnectivity = false,
            bool includeWeaveTopology = false,
            int middleExitLane = 2,
            uint middleLengthMeters = 700,
            bool declareMissingOutboundEdge = false,
            bool unreadableOpposingEdge = false,
            bool sameWayFirstTransition = false,
            bool omitFirstTransitionConnectivity = false)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "SharpNinja.Valhalla.Tests",
                "lane-topology",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var tileId = new GraphId(769709, 2, 0);
            var builder = new GraphTileBuilder(tileId);

            AddNode(builder, edgeIndex: 0, headings: [90u]);
            AddNode(
                builder,
                edgeIndex: 1,
                headings: declareMissingOutboundEdge
                    ? [270u, 90u, 270u, 90u, 180u, 180u]
                    : [270u, 90u, 270u, 90u]);
            AddNode(builder, edgeIndex: 5, headings: [270u]);

            AddEdge(
                builder,
                tileId,
                edgeIndex: 0,
                wayId: 100,
                laneCount: 3,
                lengthMeters: 500,
                startNodeIndex: 0,
                endNodeIndex: 1,
                localEdgeIndex: 0,
                opposingLocalEdgeIndex: 0,
                forward: true,
                hasTurnLanes: routeUsesNonThroughLane,
                hasLaneConnectivity: false);
            AddEdge(
                builder,
                tileId,
                edgeIndex: 1,
                wayId: 100,
                laneCount: 3,
                lengthMeters: 500,
                startNodeIndex: 1,
                endNodeIndex: 0,
                localEdgeIndex: 0,
                opposingLocalEdgeIndex: 0,
                forward: false,
                hasTurnLanes: false,
                hasLaneConnectivity: false);
            AddEdge(
                builder,
                tileId,
                edgeIndex: 2,
                wayId: sameWayFirstTransition ? 100UL : 200UL,
                laneCount: 3,
                lengthMeters: middleLengthMeters,
                startNodeIndex: 1,
                endNodeIndex: 1,
                localEdgeIndex: 1,
                opposingLocalEdgeIndex: 2,
                forward: true,
                hasTurnLanes: true,
                hasLaneConnectivity: true);
            AddEdge(
                builder,
                tileId,
                edgeIndex: 3,
                wayId: sameWayFirstTransition ? 100UL : 200UL,
                laneCount: 3,
                lengthMeters: middleLengthMeters,
                startNodeIndex: 1,
                endNodeIndex: 1,
                localEdgeIndex: 2,
                opposingLocalEdgeIndex: 1,
                forward: false,
                hasTurnLanes: false,
                hasLaneConnectivity: false);
            AddEdge(
                builder,
                tileId,
                edgeIndex: 4,
                wayId: 300,
                laneCount: 3,
                lengthMeters: 600,
                startNodeIndex: 1,
                endNodeIndex: 2,
                localEdgeIndex: 3,
                opposingLocalEdgeIndex: unreadableOpposingEdge ? 7u : 0u,
                forward: true,
                hasTurnLanes: false,
                hasLaneConnectivity: true);
            AddEdge(
                builder,
                tileId,
                edgeIndex: 5,
                wayId: 300,
                laneCount: 3,
                lengthMeters: 600,
                startNodeIndex: 2,
                endNodeIndex: 1,
                localEdgeIndex: 0,
                opposingLocalEdgeIndex: 3,
                forward: false,
                hasTurnLanes: false,
                hasLaneConnectivity: false);

            if (routeUsesNonThroughLane)
            {
                builder.AddTurnLanes(0, TurnLanes.GetTurnLaneString("right|through|through"));
            }

            builder.AddTurnLanes(2, TurnLanes.GetTurnLaneString("right|through|through"));
            var laneConnectivity = new List<LaneConnectivity>
            {
                new(4, 999, middleExitLane.ToString(System.Globalization.CultureInfo.InvariantCulture), "1"),
            };
            if (!omitFirstTransitionConnectivity)
            {
                laneConnectivity.Add(new LaneConnectivity(2, 100, "1", "1"));
            }

            if (!omitSecondTransitionConnectivity)
            {
                laneConnectivity.Add(
                    allowLowerFrictionLaneChoice
                        ? new LaneConnectivity(
                            4,
                            sameWayFirstTransition ? 100UL : 200UL,
                            "1|2",
                            "1|2")
                        : new LaneConnectivity(
                            4,
                            sameWayFirstTransition ? 100UL : 200UL,
                            middleExitLane.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            middleExitLane.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            if (includeSelfConnectivity)
            {
                laneConnectivity.Add(new LaneConnectivity(2, 200, "2", "2"));
            }

            if (includeWeaveTopology)
            {
                laneConnectivity.Add(new LaneConnectivity(2, 998, "1", "2"));
            }

            builder.AddLaneConnectivity(laneConnectivity);
            builder.StoreTileData(directory);

            ulong[] edgeIds =
            [
                new GraphId(tileId.Tileid(), tileId.Level(), 0).Value,
                new GraphId(tileId.Tileid(), tileId.Level(), 2).Value,
                new GraphId(tileId.Tileid(), tileId.Level(), 4).Value,
            ];
            return new GeneratedLaneGraphFixture(directory, edgeIds);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private static void AddNode(
            GraphTileBuilder builder,
            uint edgeIndex,
            IReadOnlyList<uint> headings)
        {
            var node = default(NodeInfo);
            node.SetEdgeIndex(edgeIndex);
            node.SetEdgeCount(checked((uint)headings.Count));
            node.SetLocalEdgeCount(checked((uint)headings.Count));
            for (var index = 0; index < headings.Count; index++)
            {
                node.SetHeading(checked((uint)index), headings[index]);
            }

            builder.Nodes.Add(node);
        }

        private static void AddEdge(
            GraphTileBuilder builder,
            GraphId tileId,
            uint edgeIndex,
            ulong wayId,
            uint laneCount,
            uint lengthMeters,
            uint startNodeIndex,
            uint endNodeIndex,
            uint localEdgeIndex,
            uint opposingLocalEdgeIndex,
            bool forward,
            bool hasTurnLanes,
            bool hasLaneConnectivity)
        {
            var startNode = new GraphId(tileId.Tileid(), tileId.Level(), startNodeIndex);
            var endNode = new GraphId(tileId.Tileid(), tileId.Level(), endNodeIndex);
            uint edgeInfoOffset = builder.AddEdgeInfo(
                edgeIndex,
                startNode,
                endNode,
                wayId,
                0,
                0,
                90,
                [
                    new PointLL(-86.8d + (startNodeIndex * 0.01d), 36.1d),
                    new PointLL(-86.8d + (endNodeIndex * 0.01d), 36.1d),
                ],
                [],
                [],
                [],
                0,
                out _);

            var edge = new DirectedEdge();
            edge.SetEdgeInfoOffset(edgeInfoOffset);
            edge.SetEndNode(endNode);
            edge.SetOppIndex(opposingLocalEdgeIndex);
            edge.SetLocalEdgeIdx(localEdgeIndex);
            edge.SetLaneCount(laneCount);
            edge.SetLength(lengthMeters);
            edge.SetForward(forward);
            edge.SetUse(Use.Road);
            edge.SetTurnLanes(hasTurnLanes);
            edge.SetLaneConnectivity(hasLaneConnectivity);
            builder.DirectedEdges.Add(edge);
        }
    }
}
