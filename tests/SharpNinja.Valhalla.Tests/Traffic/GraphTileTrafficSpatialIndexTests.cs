using System.IO.Compression;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class GraphTileTrafficSpatialIndexTests
{
    [Fact]
    public async Task MatchAsync_LineGeometry_PreservesCanonicalGraphIdAndDirection()
    {
        ulong tileBaseId = new GraphId(1234, 2, 0).Value;
        ulong canonicalEdgeId = new GraphId(1234, 2, 17).Value;
        var source = new StubGraphSource([
            Edge(
                tileBaseId,
                17,
                canonicalEdgeId,
                TrafficDirection.Forward,
                (36.1000, -86.7000),
                (36.1010, -86.7000),
                (36.1020, -86.6990)),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((36.1000, -86.7000), (36.1004, -86.7000)),
            new ValhallaGraphTrafficContext("graph-a"),
            TestContext.Current.CancellationToken);

        TrafficEdgeMatchCandidate match = Assert.Single(matches);
        Assert.Equal(tileBaseId, match.Edge.TileId);
        Assert.Equal((uint)17, match.Edge.DirectedEdgeIndex);
        Assert.Equal(canonicalEdgeId, match.Edge.GraphDirectedEdgeId);
        Assert.Equal(canonicalEdgeId, match.Edge.CanonicalDirectedEdgeId);
        Assert.Equal(TrafficDirection.Forward, match.Direction);
        Assert.True(
            match.DirectionResolved,
            string.Join(
                Environment.NewLine,
                matches.Select(candidate =>
                    $"{candidate.Edge.DirectedEdgeIndex}: " +
                    $"canonical={candidate.Edge.CanonicalDirectedEdgeId}, " +
                    $"direction={candidate.Direction}, " +
                    $"distance={candidate.DistanceMeters:F3}, " +
                    $"resolved={candidate.DirectionResolved}")));
    }

    [Fact]
    public async Task MatchAsync_LineGeometry_UsesDirectedShapeInsteadOfEdgeMidpoint()
    {
        ulong tileBaseId = new GraphId(4321, 2, 0).Value;
        var source = new StubGraphSource([
            Edge(
                tileBaseId,
                9,
                new GraphId(4321, 2, 9).Value,
                TrafficDirection.Forward,
                (36.1000, -86.7000),
                (36.1000, -86.6900),
                (36.1100, -86.6900)),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((36.1000, -86.7000), (36.1000, -86.6997)),
            new ValhallaGraphTrafficContext("curved-graph"),
            TestContext.Current.CancellationToken);

        Assert.Single(matches);
    }

    [Fact]
    public async Task MatchAsync_LineDirection_RejectsOppositeDirectedEdge()
    {
        ulong tileBaseId = new GraphId(2000, 2, 0).Value;
        IReadOnlyList<GeoCoordinate> northbound = Coordinates(
            (36.1000, -86.7000),
            (36.1010, -86.7000));
        IReadOnlyList<GeoCoordinate> southbound = northbound.Reverse().ToArray();
        var source = new StubGraphSource([
            new TrafficSpatialGraphEdge(
                tileBaseId,
                2,
                new GraphId(2000, 2, 2).Value,
                TrafficDirection.Forward,
                northbound),
            new TrafficSpatialGraphEdge(
                tileBaseId,
                3,
                new GraphId(2000, 2, 3).Value,
                TrafficDirection.Reverse,
                southbound),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            new TrafficGeometry(
                TrafficGeometryKind.LineString,
                northbound,
                TrafficGeometryDirection.AlongCoordinates),
            new ValhallaGraphTrafficContext("bidirectional-graph"),
            TestContext.Current.CancellationToken);

        TrafficEdgeMatchCandidate match = Assert.Single(matches);
        Assert.Equal((uint)2, match.Edge.DirectedEdgeIndex);
        Assert.Equal(TrafficDirection.Forward, match.Direction);
        Assert.True(
            match.DirectionResolved,
            string.Join(
                Environment.NewLine,
                matches.Select(candidate =>
                    $"{candidate.Edge.DirectedEdgeIndex}: " +
                    $"canonical={candidate.Edge.CanonicalDirectedEdgeId}, " +
                    $"direction={candidate.Direction}, " +
                    $"distance={candidate.DistanceMeters:F3}, " +
                    $"resolved={candidate.DirectionResolved}")));
    }

    [Fact]
    public async Task MatchAsync_PointOnBidirectionalShape_LeavesClosureDirectionAmbiguous()
    {
        ulong tileBaseId = new GraphId(3000, 2, 0).Value;
        IReadOnlyList<GeoCoordinate> forward = Coordinates(
            (36.1000, -86.7000),
            (36.1010, -86.7000));
        var source = new StubGraphSource([
            new TrafficSpatialGraphEdge(
                tileBaseId,
                4,
                new GraphId(3000, 2, 4).Value,
                TrafficDirection.Forward,
                forward),
            new TrafficSpatialGraphEdge(
                tileBaseId,
                5,
                new GraphId(3000, 2, 5).Value,
                TrafficDirection.Reverse,
                forward.Reverse().ToArray()),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Point(36.1005, -86.7000),
            new ValhallaGraphTrafficContext("ambiguous-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, match => match.Direction == TrafficDirection.Forward);
        Assert.Contains(matches, match => match.Direction == TrafficDirection.Reverse);
        Assert.All(matches, match => Assert.False(match.DirectionResolved));

        var matcher = new ValhallaTrafficEdgeMatcher(index);
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates = await matcher.MatchAsync(
            Closure(Point(36.1005, -86.7000)),
            new ValhallaGraphTrafficContext("ambiguous-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, updates.Count);
        Assert.All(updates, update =>
        {
            Assert.False(update.Closed);
            Assert.False(update.DirectionResolved);
        });
    }

    [Fact]
    public async Task MatchAsync_NarrowTolerance_AvoidsParallelCarriagewayContamination()
    {
        ulong tileBaseId = new GraphId(4000, 2, 0).Value;
        var source = new StubGraphSource([
            Edge(
                tileBaseId,
                6,
                new GraphId(4000, 2, 6).Value,
                TrafficDirection.Forward,
                (36.1000, -86.7000),
                (36.1010, -86.7000)),
            Edge(
                tileBaseId,
                7,
                new GraphId(4000, 2, 7).Value,
                TrafficDirection.Forward,
                (36.1000, -86.69995),
                (36.1010, -86.69995)),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((36.1000, -86.7000), (36.1010, -86.7000)),
            new ValhallaGraphTrafficContext("parallel-graph"),
            TestContext.Current.CancellationToken);

        TrafficEdgeMatchCandidate match = Assert.Single(matches);
        Assert.Equal((uint)6, match.Edge.DirectedEdgeIndex);
    }

    [Fact]
    public async Task MatchAsync_EquidistantParallelSameDirectionEdges_RemainAdvisory()
    {
        ulong tileBaseId = new GraphId(4500, 2, 0).Value;
        var source = new StubGraphSource([
            Edge(
                tileBaseId,
                6,
                new GraphId(4500, 2, 6).Value,
                TrafficDirection.Forward,
                (36.1000, -86.70005),
                (36.1010, -86.70005)),
            Edge(
                tileBaseId,
                7,
                new GraphId(4500, 2, 7).Value,
                TrafficDirection.Forward,
                (36.1000, -86.69995),
                (36.1010, -86.69995)),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        TrafficGeometry geometry =
            Line((36.1000, -86.7000), (36.1010, -86.7000));

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            geometry,
            new ValhallaGraphTrafficContext("equidistant-parallel-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.False(match.DirectionResolved));

        var matcher = new ValhallaTrafficEdgeMatcher(index);
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates = await matcher.MatchAsync(
            Closure(geometry),
            new ValhallaGraphTrafficContext("equidistant-parallel-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, updates.Count);
        Assert.All(updates, update =>
        {
            Assert.False(update.DirectionResolved);
            Assert.False(update.Closed);
        });
    }

    [Fact]
    public async Task MatchAsync_MultiSegmentParallelAlternativesWithDifferentClosestSegments_RemainAdvisory()
    {
        ulong tileBaseId = new GraphId(4750, 2, 0).Value;
        var source = new StubGraphSource([
            new TrafficSpatialGraphEdge(
                tileBaseId,
                8,
                new GraphId(4750, 2, 8).Value,
                TrafficDirection.Forward,
                Coordinates(
                    (36.1000, -86.70004),
                    (36.1010, -86.70004),
                    (36.1020, -86.70006))),
            new TrafficSpatialGraphEdge(
                tileBaseId,
                9,
                new GraphId(4750, 2, 9).Value,
                TrafficDirection.Forward,
                Coordinates(
                    (36.1000, -86.69994),
                    (36.1010, -86.69996),
                    (36.1020, -86.69996))),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        TrafficGeometry geometry = Line(
            (36.1000, -86.7000),
            (36.1010, -86.7000),
            (36.1020, -86.7000));

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            geometry,
            new ValhallaGraphTrafficContext("multi-segment-parallel-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.False(match.DirectionResolved));

        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates =
            await new ValhallaTrafficEdgeMatcher(index).MatchAsync(
                Closure(geometry),
                new ValhallaGraphTrafficContext("multi-segment-parallel-graph"),
                TestContext.Current.CancellationToken);

        Assert.Equal(2, updates.Count);
        Assert.All(updates, update =>
        {
            Assert.False(update.DirectionResolved);
            Assert.False(update.Closed);
        });
    }

    [Fact]
    public async Task MatchAsync_NearCoincidentDisconnectedEdges_RemainAdvisory()
    {
        ulong tileBaseId = new GraphId(4900, 2, 0).Value;
        var source = new StubGraphSource([
            EdgeWithNodes(
                tileBaseId,
                8,
                new GraphId(4900, 2, 8).Value,
                TrafficDirection.Forward,
                new GraphId(4900, 2, 100).Value,
                new GraphId(4900, 2, 101).Value,
                (36.1000, -86.7000),
                (36.1010, -86.7000)),
            EdgeWithNodes(
                tileBaseId,
                9,
                new GraphId(4900, 2, 9).Value,
                TrafficDirection.Forward,
                new GraphId(4900, 2, 200).Value,
                new GraphId(4900, 2, 201).Value,
                (36.1010, -86.7000),
                (36.1009, -86.7000),
                (36.1020, -86.7000)),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        TrafficGeometry geometry = Line(
            (36.1000, -86.7000),
            (36.1020, -86.7000));

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            geometry,
            new ValhallaGraphTrafficContext("disconnected-grade-separated-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.False(match.DirectionResolved));

        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates =
            await new ValhallaTrafficEdgeMatcher(index).MatchAsync(
                Closure(geometry),
                new ValhallaGraphTrafficContext("disconnected-grade-separated-graph"),
                TestContext.Current.CancellationToken);

        Assert.Equal(2, updates.Count);
        Assert.All(updates, update =>
        {
            Assert.False(update.DirectionResolved);
            Assert.False(update.Closed);
        });
    }

    [Fact]
    public async Task MatchAsync_SplitEdgesOnProviderSegment_AreNotCollapsed()
    {
        ulong tileBaseId = new GraphId(5000, 2, 0).Value;
        var source = new StubGraphSource([
            EdgeWithNodes(
                tileBaseId,
                8,
                new GraphId(5000, 2, 8).Value,
                TrafficDirection.Forward,
                new GraphId(5000, 2, 100).Value,
                new GraphId(5000, 2, 101).Value,
                (36.1000, -86.7000),
                (36.1005, -86.7000)),
            EdgeWithNodes(
                tileBaseId,
                9,
                new GraphId(5000, 2, 9).Value,
                TrafficDirection.Forward,
                new GraphId(5000, 2, 101).Value,
                new GraphId(5000, 2, 102).Value,
                (36.1005, -86.7000),
                (36.1010, -86.7000)),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((36.1000, -86.7000), (36.1010, -86.7000)),
            new ValhallaGraphTrafficContext("split-edge-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, match => match.Edge.DirectedEdgeIndex == 8);
        Assert.Contains(matches, match => match.Edge.DirectedEdgeIndex == 9);
        Assert.All(matches, match => Assert.True(match.DirectionResolved));

        var matcher = new ValhallaTrafficEdgeMatcher(index);
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates = await matcher.MatchAsync(
            Closure(Line((36.1000, -86.7000), (36.1010, -86.7000))),
            new ValhallaGraphTrafficContext("split-edge-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, updates.Count);
        Assert.All(updates, update =>
        {
            Assert.True(update.DirectionResolved);
            Assert.True(update.Closed);
        });
    }

    [Fact]
    public async Task MatchAsync_BothDirections_ClosesBothDirectedEdges()
    {
        ulong tileBaseId = new GraphId(5900, 2, 0).Value;
        IReadOnlyList<GeoCoordinate> forward = Coordinates(
            (36.1000, -86.7000),
            (36.1010, -86.7000));
        var source = new StubGraphSource([
            new TrafficSpatialGraphEdge(
                tileBaseId,
                10,
                new GraphId(5900, 2, 10).Value,
                TrafficDirection.Forward,
                forward),
            new TrafficSpatialGraphEdge(
                tileBaseId,
                11,
                new GraphId(5900, 2, 11).Value,
                TrafficDirection.Reverse,
                forward.Reverse().ToArray()),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        TrafficGeometry bothDirections = new(
            TrafficGeometryKind.LineString,
            forward,
            TrafficGeometryDirection.BothDirections);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            bothDirections,
            new ValhallaGraphTrafficContext("both-directions-graph"),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.True(match.DirectionResolved));

        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates =
            await new ValhallaTrafficEdgeMatcher(index).MatchAsync(
                Closure(bothDirections),
                new ValhallaGraphTrafficContext("both-directions-graph"),
                TestContext.Current.CancellationToken);
        Assert.Equal(2, updates.Count);
        Assert.All(updates, update => Assert.True(update.Closed));
    }

    [Fact]
    public async Task MatchAsync_UnknownLineOrder_LeavesClosureDirectionAmbiguous()
    {
        ulong tileBaseId = new GraphId(6000, 2, 0).Value;
        IReadOnlyList<GeoCoordinate> forward = Coordinates(
            (36.1000, -86.7000),
            (36.1010, -86.7000));
        var source = new StubGraphSource([
            new TrafficSpatialGraphEdge(
                tileBaseId,
                10,
                new GraphId(6000, 2, 10).Value,
                TrafficDirection.Forward,
                forward),
            new TrafficSpatialGraphEdge(
                tileBaseId,
                11,
                new GraphId(6000, 2, 11).Value,
                TrafficDirection.Reverse,
                forward.Reverse().ToArray()),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        TrafficGeometry unknownOrder = new(
            TrafficGeometryKind.LineString,
            forward,
            TrafficGeometryDirection.Unknown);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            unknownOrder,
            new ValhallaGraphTrafficContext("unknown-order-graph"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.False(match.DirectionResolved));

        var matcher = new ValhallaTrafficEdgeMatcher(index);
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates = await matcher.MatchAsync(
            Closure(unknownOrder),
            new ValhallaGraphTrafficContext("unknown-order-graph"),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, updates.Count);
        Assert.All(updates, update => Assert.False(update.Closed));
    }

    [Theory]
    [InlineData(1d, 20d, true)]
    [InlineData(5d, 20d, true)]
    [InlineData(15d, 20d, true)]
    [InlineData(100d, 20d, false)]
    [InlineData(100d, 10d, false)]
    [InlineData(100d, 5d, true)]
    [InlineData(100d, 2d, true)]
    public async Task MatchAsync_AlongCoordinatesAtShallowSplit_DoesNotHardCloseBranch(
        double providerLengthMeters,
        double branchAngleDegrees,
        bool expectSpatialAmbiguity)
    {
        const double originLatitude = 36.1d;
        const double originLongitude = -86.7d;
        const double edgeLengthMeters = 100d;
        double longitudeMetersPerDegree =
            111_320d * Math.Cos(originLatitude * Math.PI / 180d);
        double branchAngleRadians = branchAngleDegrees * Math.PI / 180d;
        double providerLatitudeDelta = providerLengthMeters / 111_320d;
        double edgeLatitudeDelta = edgeLengthMeters / 111_320d;
        double branchLatitudeDelta =
            edgeLengthMeters * Math.Cos(branchAngleRadians) / 111_320d;
        double branchLongitudeDelta =
            edgeLengthMeters * Math.Sin(branchAngleRadians) / longitudeMetersPerDegree;
        ulong tileBaseId = new GraphId(6500, 2, 0).Value;
        ulong mainEdgeId = new GraphId(6500, 2, 20).Value;
        ulong branchEdgeId = new GraphId(6500, 2, 21).Value;
        var source = new StubGraphSource([
            Edge(
                tileBaseId,
                20,
                mainEdgeId,
                TrafficDirection.Forward,
                (originLatitude, originLongitude),
                (originLatitude + edgeLatitudeDelta, originLongitude)),
            Edge(
                tileBaseId,
                21,
                branchEdgeId,
                TrafficDirection.Forward,
                (originLatitude, originLongitude),
                (
                    originLatitude + branchLatitudeDelta,
                    originLongitude + branchLongitudeDelta)),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        TrafficGeometry geometry = new(
            TrafficGeometryKind.LineString,
            [
                new GeoCoordinate(originLatitude, originLongitude),
                new GeoCoordinate(
                    originLatitude + providerLatitudeDelta,
                    originLongitude),
            ],
            TrafficGeometryDirection.AlongCoordinates);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            geometry,
            new ValhallaGraphTrafficContext($"short-split-{providerLengthMeters}"),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(matches);
        Assert.Contains(
            matches,
            match => match.Edge.CanonicalDirectedEdgeId == mainEdgeId);

        var matcher = new ValhallaTrafficEdgeMatcher(index);
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates = await matcher.MatchAsync(
            Closure(geometry),
            new ValhallaGraphTrafficContext($"short-split-{providerLengthMeters}"),
            TestContext.Current.CancellationToken);
        if (providerLengthMeters < 20d || expectSpatialAmbiguity)
        {
            if (expectSpatialAmbiguity && providerLengthMeters >= 20d)
            {
                Assert.True(matches.Count > 1);
            }

            Assert.All(matches, match => Assert.False(match.DirectionResolved));
            Assert.DoesNotContain(updates, update => update.Closed);
        }
        else
        {
            TrafficEdgeMatchCandidate match = Assert.Single(matches);
            Assert.Equal(mainEdgeId, match.Edge.CanonicalDirectedEdgeId);
            Assert.True(
            match.DirectionResolved,
            string.Join(
                Environment.NewLine,
                matches.Select(candidate =>
                    $"{candidate.Edge.DirectedEdgeIndex}: " +
                    $"canonical={candidate.Edge.CanonicalDirectedEdgeId}, " +
                    $"direction={candidate.Direction}, " +
                    $"distance={candidate.DistanceMeters:F3}, " +
                    $"resolved={candidate.DirectionResolved}")));
            ValhallaTrafficEdgeUpdate update = Assert.Single(updates);
            Assert.Equal(mainEdgeId, update.CanonicalDirectedEdgeId);
            Assert.True(update.Closed);
        }
    }

    [Fact]
    public async Task MatchAsync_SharedEndpoint_DoesNotMatchDivergingRamp()
    {
        ulong tileBaseId = new GraphId(7000, 2, 0).Value;
        var source = new StubGraphSource([
            Edge(
                tileBaseId,
                12,
                new GraphId(7000, 2, 12).Value,
                TrafficDirection.Forward,
                (36.1000, -86.7000),
                (36.1020, -86.7000)),
            Edge(
                tileBaseId,
                13,
                new GraphId(7000, 2, 13).Value,
                TrafficDirection.Forward,
                (36.1010, -86.7000),
                (36.1020, -86.6994)),
        ]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((36.1000, -86.7000), (36.1020, -86.7000)),
            new ValhallaGraphTrafficContext("split-graph"),
            TestContext.Current.CancellationToken);

        TrafficEdgeMatchCandidate match = Assert.Single(matches);
        Assert.Equal((uint)12, match.Edge.DirectedEdgeIndex);
    }

    [Fact]
    public async Task MatchAsync_CrossTileEdgeOwnedByAdjacentStartNodeTile_IsIncludedByBoundedHalo()
    {
        GraphId ownerTile = TileHierarchy.GetGraphId(
            PointLL.Create(-86.7505, 36.0000),
            level: 2);
        ulong canonicalEdgeId = new GraphId(ownerTile.Tileid(), 2, 7).Value;
        var edge = new TrafficSpatialGraphEdge(
            ownerTile.TileBase().Value,
            7,
            canonicalEdgeId,
            TrafficDirection.Forward,
            [
                new GeoCoordinate(36.0000, -86.7505),
                new GeoCoordinate(36.0000, -86.7490),
            ]);
        var source = new OwnerTileGraphSource(ownerTile.TileBase(), edge);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((36.0000, -86.7498), (36.0000, -86.7490)),
            new ValhallaGraphTrafficContext("cross-tile-owner-halo"),
            TestContext.Current.CancellationToken);

        TrafficEdgeMatchCandidate match = Assert.Single(matches);
        Assert.Equal(canonicalEdgeId, match.Edge.CanonicalDirectedEdgeId);
        Assert.Contains(
            source.LastQueryTileIds,
            tile => tile.TileBase().Value == ownerTile.TileBase().Value);
        Assert.InRange(source.LastQueryTileIds.Count, 1, 48);
    }

    [Fact]
    public async Task MatchAsync_LastCancelledWaiter_CancelsBuildAndAllowsRetry()
    {
        var source = new CancelOnFirstGraphSource();
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        var context = new ValhallaGraphTrafficContext("cancelled-build-graph");
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<IReadOnlyList<TrafficEdgeMatchCandidate>> first =
            index.MatchAsync(Point(36.1, -86.7), context, cancellation.Token).AsTask();
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await source.Cancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        _ = await index.MatchAsync(
            Point(36.1, -86.7),
            context,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public async Task MatchAsync_CancellationDuringIndexBuild_StopsBeforeCaching()
    {
        var source = new LargeGraphSource(edgeCount: 30_000);
        var indexBuildStarted =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new GraphTileTrafficSpatialIndex(
            source,
            matchToleranceMeters: 8,
            GraphTileTrafficSpatialIndex.DefaultDirectionToleranceDegrees,
            () => indexBuildStarted.TrySetResult());
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<IReadOnlyList<TrafficEdgeMatchCandidate>> match = index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext("large-index-graph"),
                cancellation.Token)
            .AsTask();
        await indexBuildStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => match);
        Assert.Equal(0, index.CachedSnapshotCount);
    }

    [Fact]
    public async Task MatchAsync_CapacityPressureDoesNotEvictActiveSharedBuilds()
    {
        int capacity = GraphTileTrafficSpatialIndex.MaximumCachedSnapshots;
        var source = new CapacityPressureGraphSource(capacity);
        using var index =
            new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        Task<IReadOnlyList<TrafficEdgeMatchCandidate>>[] activeMatches = Enumerable
            .Range(0, capacity)
            .Select(signature => index.MatchAsync(
                    Point(36.1, -86.7),
                    new ValhallaGraphTrafficContext($"active-graph-{signature}"),
                    TestContext.Current.CancellationToken)
                .AsTask())
            .ToArray();

        await source.AllStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(capacity, source.StartedCount);
        Assert.Equal(capacity, index.CachedSnapshotCount);

        using var overflowCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
        Task<IReadOnlyList<TrafficEdgeMatchCandidate>> overflow = index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext("overflow-graph"),
                overflowCancellation.Token)
            .AsTask();
        await Task.Yield();

        Assert.Equal(capacity, source.StartedCount);
        Assert.Equal(capacity, index.CachedSnapshotCount);
        await overflowCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => overflow);
        Assert.Empty(source.CancelledSignatures);

        source.CompleteAll();
        await Task.WhenAll(activeMatches)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        IReadOnlyList<TrafficEdgeMatchCandidate> retry = await index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext("overflow-graph"),
                TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(retry);
        Assert.Equal(capacity + 1, source.StartedCount);
        Assert.InRange(index.CachedSnapshotCount, 0, capacity);
        Assert.Empty(source.CancelledSignatures);
    }

    [Fact]
    public async Task MatchAsync_OverflowCannotEvictEntryBetweenPublicationAndFirstWaiter()
    {
        int capacity = GraphTileTrafficSpatialIndex.MaximumCachedSnapshots;
        using var published = new ManualResetEventSlim();
        using var allowFirstWaiter = new ManualResetEventSlim();
        int publicationCount = 0;
        var source = new CapacityPressureGraphSource(capacity);
        using var index = new GraphTileTrafficSpatialIndex(
            source,
            matchToleranceMeters: 8,
            cacheEntryPublished: () =>
            {
                if (Interlocked.Increment(ref publicationCount) == capacity)
                {
                    published.Set();
                    Assert.True(allowFirstWaiter.Wait(TimeSpan.FromSeconds(5)));
                }
            });

        Task<IReadOnlyList<TrafficEdgeMatchCandidate>>[] active = Enumerable
            .Range(0, capacity - 1)
            .Select(signature => index.MatchAsync(
                    Point(36.1, -86.7),
                    new ValhallaGraphTrafficContext($"race-active-{signature}"),
                    TestContext.Current.CancellationToken)
                .AsTask())
            .ToArray();
        using var startTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
        startTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (source.StartedCount < capacity - 1)
        {
            await Task.Delay(10, startTimeout.Token);
        }

        Task<IReadOnlyList<TrafficEdgeMatchCandidate>> protectedCaller = Task.Run(
            async () => await index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext("race-protected"),
                TestContext.Current.CancellationToken));
        Assert.True(
            await Task.Run(
                () => published.Wait(TimeSpan.FromSeconds(5)),
                TestContext.Current.CancellationToken));

        using var overflowCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
        Task<IReadOnlyList<TrafficEdgeMatchCandidate>> overflow = index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext("race-overflow"),
                overflowCancellation.Token)
            .AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(capacity - 1, source.StartedCount);
        Assert.Empty(source.CancelledSignatures);
        await overflowCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => overflow);

        allowFirstWaiter.Set();
        await source.AllStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        source.CompleteAll();
        await Task.WhenAll(active.Append(protectedCaller))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(source.CancelledSignatures);
    }

    [Fact]
    public async Task MatchAsync_FillCapacityThenClear_AllowsImmediateReuse()
    {
        int capacity = GraphTileTrafficSpatialIndex.MaximumCachedSnapshots;
        var source = new StubGraphSource([]);
        using var index =
            new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        for (int signature = 0; signature < capacity; signature++)
        {
            _ = await index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext($"clear-graph-{signature}"),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(capacity, index.CachedSnapshotCount);
        index.Clear();
        Assert.Equal(0, index.CachedSnapshotCount);

        IReadOnlyList<TrafficEdgeMatchCandidate> result = await index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext("after-clear"),
                TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(1, index.CachedSnapshotCount);
    }

    [Fact]
    public async Task MatchAsync_FillCapacityThenInvalidate_AllowsImmediateReuse()
    {
        int capacity = GraphTileTrafficSpatialIndex.MaximumCachedSnapshots;
        var source = new StubGraphSource([]);
        using var index =
            new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        for (int query = 0; query < capacity; query++)
        {
            _ = await index.MatchAsync(
                Point(36.1, -86.7 + query),
                new ValhallaGraphTrafficContext("invalidate-graph"),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(capacity, index.CachedSnapshotCount);
        index.Invalidate("invalidate-graph");
        Assert.Equal(0, index.CachedSnapshotCount);

        IReadOnlyList<TrafficEdgeMatchCandidate> result = await index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext("after-invalidate"),
                TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(1, index.CachedSnapshotCount);
    }

    [Fact]
    public async Task Dispose_FullCapacityWithManyAdmissionWaiters_CompletesEveryCaller()
    {
        int capacity = GraphTileTrafficSpatialIndex.MaximumCachedSnapshots;
        var source = new CapacityPressureGraphSource(capacity);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        Task<IReadOnlyList<TrafficEdgeMatchCandidate>>[] callers = Enumerable
            .Range(0, capacity * 3)
            .Select(signature => index.MatchAsync(
                    Point(36.1, -86.7),
                    new ValhallaGraphTrafficContext($"dispose-graph-{signature}"),
                    TestContext.Current.CancellationToken)
                .AsTask())
            .ToArray();

        await source.AllStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(capacity, source.StartedCount);
        Assert.Equal(capacity, index.CachedSnapshotCount);

        index.Dispose();

        Task observeAll = Task.WhenAll(callers.Select(async caller =>
        {
            Exception? exception = await Record.ExceptionAsync(() => caller);
            Assert.NotNull(exception);
            Assert.True(
                exception is OperationCanceledException or ObjectDisposedException,
                $"Unexpected disposal exception: {exception}");
        }));
        await observeAll.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, index.CachedSnapshotCount);
        Assert.Equal(capacity, source.CancelledSignatures.Count);
    }

    [Fact]
    public async Task Dispose_BetweenAdmissionAndInsertion_RejectsLateCandidate()
    {
        using var admissionReached = new ManualResetEventSlim();
        using var allowInsertion = new ManualResetEventSlim();
        var source = new StubGraphSource([]);
        var index = new GraphTileTrafficSpatialIndex(
            source,
            matchToleranceMeters: 8,
            cacheAdmissionAcquired: () =>
            {
                admissionReached.Set();
                Assert.True(allowInsertion.Wait(TimeSpan.FromSeconds(5)));
            });
        try
        {
            Task<IReadOnlyList<TrafficEdgeMatchCandidate>> caller = Task.Run(
                async () => await index.MatchAsync(
                    Point(36.1, -86.7),
                    new ValhallaGraphTrafficContext("dispose-insert-race"),
                    TestContext.Current.CancellationToken));

            Assert.True(
                await Task.Run(
                    () => admissionReached.Wait(TimeSpan.FromSeconds(5)),
                    TestContext.Current.CancellationToken));
            index.Dispose();
            allowInsertion.Set();

            Exception? exception = await Record.ExceptionAsync(() => caller)
                .AsTask()
                .WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
            Assert.IsType<ObjectDisposedException>(exception);
            Assert.Equal(0, index.CachedSnapshotCount);
            Assert.Equal(0, source.ReadCount);
        }
        finally
        {
            allowInsertion.Set();
            index.Dispose();
        }
    }

    [Fact]
    public async Task MatchAsync_BoundsSignatureQueryCache()
    {
        var source = new StubGraphSource([]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        for (int signature = 0; signature < 40; signature++)
        {
            _ = await index.MatchAsync(
                Point(36.1, -86.7),
                new ValhallaGraphTrafficContext($"graph-{signature}"),
                TestContext.Current.CancellationToken);
        }

        Assert.InRange(index.CachedSnapshotCount, 0, 32);
    }

    [Fact]
    public async Task MatchAsync_LargeGeometryRetainsOnlyBoundedTileBatchSnapshots()
    {
        var source = new RecordingTileBatchGraphSource();
        using var index =
            new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((36.1, -90.0), (36.1, -82.0)),
            new ValhallaGraphTrafficContext("large-provider-geometry"),
            TestContext.Current.CancellationToken);

        Assert.Empty(matches);
        Assert.True(source.ReadCount > 1);
        Assert.InRange(
            source.MaximumObservedTileCount,
            1,
            GraphTileTrafficSpatialIndex.MaximumTilesPerSnapshot);
        Assert.InRange(
            index.CachedSnapshotCount,
            0,
            GraphTileTrafficSpatialIndex.MaximumCachedSnapshots);
    }

    [Fact]
    public async Task MatchAsync_DiagonalGeometryQueriesSegmentCorridorInsteadOfBoundingRectangle()
    {
        var source = new RecordingTileBatchGraphSource();
        using var index =
            new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((0.0, -10.0), (10.0, 0.0)),
            new ValhallaGraphTrafficContext("diagonal-provider-geometry"),
            TestContext.Current.CancellationToken);

        Assert.Empty(matches);
        Assert.InRange(source.DistinctObservedTileCount, 1, 600);
    }

    [Fact]
    public async Task MatchAsync_AntimeridianGeometryUsesShortestCrossingCorridor()
    {
        var source = new RecordingTileBatchGraphSource();
        using var index =
            new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            Line((10.0, 179.9), (10.0, -179.9)),
            new ValhallaGraphTrafficContext("antimeridian-provider-geometry"),
            TestContext.Current.CancellationToken);

        Assert.Empty(matches);
        Assert.InRange(source.DistinctObservedTileCount, 1, 64);
    }

    [Fact]
    public async Task MatchAsync_ExcessiveCorridorWorkFailsBeforeGraphRead()
    {
        var source = new RecordingTileBatchGraphSource();
        using var index =
            new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);

        await Assert.ThrowsAsync<TrafficSpatialQueryLimitExceededException>(
            async () => await index.MatchAsync(
                Line((-80.0, -170.0), (80.0, 170.0)),
                new ValhallaGraphTrafficContext("excessive-provider-geometry"),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, source.ReadCount);
        Assert.Equal(0, source.DistinctObservedTileCount);
    }

    [Fact]
    public async Task MatchAsync_EvictsLeastRecentlyUsedCompletedEntryDeterministically()
    {
        int capacity = GraphTileTrafficSpatialIndex.MaximumCachedSnapshots;
        var source = new SignatureRecordingGraphSource();
        using var index =
            new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        TrafficGeometry point = Point(36.1, -86.7);

        for (int signature = 0; signature < capacity; signature++)
        {
            _ = await index.MatchAsync(
                point,
                new ValhallaGraphTrafficContext($"lru-{signature:D2}"),
                TestContext.Current.CancellationToken);
        }

        _ = await index.MatchAsync(
            point,
            new ValhallaGraphTrafficContext("lru-00"),
            TestContext.Current.CancellationToken);
        _ = await index.MatchAsync(
            point,
            new ValhallaGraphTrafficContext("lru-overflow"),
            TestContext.Current.CancellationToken);
        _ = await index.MatchAsync(
            point,
            new ValhallaGraphTrafficContext("lru-00"),
            TestContext.Current.CancellationToken);
        _ = await index.MatchAsync(
            point,
            new ValhallaGraphTrafficContext("lru-01"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, source.ReadCount("lru-00"));
        Assert.Equal(2, source.ReadCount("lru-01"));
        Assert.Equal(1, source.ReadCount("lru-overflow"));
    }

    [Fact]
    public async Task MatchAsync_CachesGraphReadByExactSignature()
    {
        var source = new StubGraphSource([]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        TrafficGeometry point = Point(36.1000, -86.7000);

        _ = await index.MatchAsync(
            point,
            new ValhallaGraphTrafficContext("graph-a", @"C:\first"),
            TestContext.Current.CancellationToken);
        _ = await index.MatchAsync(
            point,
            new ValhallaGraphTrafficContext("graph-a", @"C:\second"),
            TestContext.Current.CancellationToken);
        _ = await index.MatchAsync(
            point,
            new ValhallaGraphTrafficContext("graph-b", @"C:\first"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public async Task InvalidateAndDispose_ReleaseCachedSnapshots()
    {
        var source = new StubGraphSource([]);
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        var context = new ValhallaGraphTrafficContext("invalidate-graph");

        _ = await index.MatchAsync(
            Point(36.1, -86.7),
            context,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, index.CachedSnapshotCount);

        index.Invalidate("invalidate-graph");
        Assert.Equal(0, index.CachedSnapshotCount);
        _ = await index.MatchAsync(
            Point(36.1, -86.7),
            context,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, source.ReadCount);

        index.Dispose();
        Assert.Equal(0, index.CachedSnapshotCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => index.MatchAsync(
                    Point(36.1, -86.7),
                    context,
                    TestContext.Current.CancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task MatchAsync_CancelledWaiter_DoesNotPoisonSharedSignatureBuild()
    {
        var source = new ControlledGraphSource();
        var index = new GraphTileTrafficSpatialIndex(source, matchToleranceMeters: 8);
        var context = new ValhallaGraphTrafficContext("shared-graph");
        TrafficGeometry point = Point(36.1000, -86.7000);

        ValueTask<IReadOnlyList<TrafficEdgeMatchCandidate>> ownerValueTask = index.MatchAsync(
            point,
            context,
            TestContext.Current.CancellationToken);
        Task<IReadOnlyList<TrafficEdgeMatchCandidate>> owner = ownerValueTask.AsTask();
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var waiterCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<IReadOnlyList<TrafficEdgeMatchCandidate>> waiter =
            index.MatchAsync(point, context, waiterCancellation.Token).AsTask();
        await waiterCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        source.Complete([]);
        _ = await owner;
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task MatchAsync_WithMonacoGraphFixture_ReadsExactDirectedShapeAndCanonicalId()
    {
        string tileDirectory = FindMonacoTileDirectory();
        const uint tileId = 769709;
        const uint level = 2;
        GraphTile tile = GraphTile.Create(tileDirectory, new GraphId(tileId, level, 0))
            ?? throw new Xunit.Sdk.XunitException("Expected Monaco graph tile was not readable.");
        (uint edgeIndex, DirectedEdge edge, IReadOnlyList<PointLL> shape) =
            FindFixtureEdge(tile);
        var orientedShape = new List<PointLL>(shape);
        if (!edge.Forward)
        {
            orientedShape.Reverse();
        }

        TrafficGeometry geometry = new(
            TrafficGeometryKind.LineString,
            orientedShape.Select(point => new GeoCoordinate(point.Lat, point.Lng)).ToArray(),
            TrafficGeometryDirection.AlongCoordinates);
        var index = new GraphTileTrafficSpatialIndex(matchToleranceMeters: 8);

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
            geometry,
            new ValhallaGraphTrafficContext("monaco-spatial-fixture", tileDirectory),
            TestContext.Current.CancellationToken);

        ulong expectedCanonicalId = new GraphId(tileId, level, edgeIndex).Value;
        TrafficEdgeMatchCandidate match = Assert.Single(
            matches,
            candidate => candidate.Edge.CanonicalDirectedEdgeId == expectedCanonicalId);
        Assert.Equal(new GraphId(tileId, level, 0).Value, match.Edge.TileId);
        Assert.Equal(edgeIndex, match.Edge.DirectedEdgeIndex);
        Assert.Equal(expectedCanonicalId, match.Edge.GraphDirectedEdgeId);
        Assert.Equal(
            edge.Forward ? TrafficDirection.Forward : TrafficDirection.Reverse,
            match.Direction);
        Assert.True(
            match.DirectionResolved,
            string.Join(
                Environment.NewLine,
                matches.Select(candidate =>
                    $"{candidate.Edge.DirectedEdgeIndex}: " +
                    $"canonical={candidate.Edge.CanonicalDirectedEdgeId}, " +
                    $"direction={candidate.Direction}, " +
                    $"distance={candidate.DistanceMeters:F3}, " +
                    $"resolved={candidate.DirectionResolved}")));
    }

    [Fact]
    public async Task MatchAsync_WithCompressedGraphTile_ReadsGphGzCanonicalId()
    {
        string sourceDirectory = FindMonacoTileDirectory();
        var tileId = new GraphId(769709, 2, 0);
        GraphTile tile = GraphTile.Create(sourceDirectory, tileId)
            ?? throw new Xunit.Sdk.XunitException("Expected Monaco graph tile was not readable.");
        (uint edgeIndex, DirectedEdge edge, IReadOnlyList<PointLL> shape) =
            FindFixtureEdge(tile);
        var orientedShape = new List<PointLL>(shape);
        if (!edge.Forward)
        {
            orientedShape.Reverse();
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-spatial-gzip-{Guid.NewGuid():N}");
        try
        {
            string relativeTilePath = GraphTile.FileSuffix(tileId);
            string sourceTilePath = Path.Combine(sourceDirectory, relativeTilePath);
            string compressedTilePath = Path.Combine(
                temporaryDirectory,
                relativeTilePath[..^GraphTile.SuffixNonCompressed.Length] +
                GraphTile.SuffixCompressed);
            Directory.CreateDirectory(
                Path.GetDirectoryName(compressedTilePath)
                ?? throw new InvalidOperationException("Compressed tile path has no directory."));
            await using (FileStream input = File.OpenRead(sourceTilePath))
            await using (FileStream output = File.Create(compressedTilePath))
            await using (var gzip = new GZipStream(
                             output,
                             CompressionLevel.SmallestSize,
                             leaveOpen: false))
            {
                await input.CopyToAsync(
                    gzip,
                    TestContext.Current.CancellationToken);
            }

            TrafficGeometry geometry = new(
                TrafficGeometryKind.LineString,
                orientedShape
                    .Select(point => new GeoCoordinate(point.Lat, point.Lng))
                    .ToArray(),
                TrafficGeometryDirection.AlongCoordinates);
            var index = new GraphTileTrafficSpatialIndex(matchToleranceMeters: 8);

            IReadOnlyList<TrafficEdgeMatchCandidate> matches = await index.MatchAsync(
                geometry,
                new ValhallaGraphTrafficContext(
                    "monaco-compressed-spatial-fixture",
                    temporaryDirectory),
                TestContext.Current.CancellationToken);

            ulong expectedCanonicalId = new GraphId(769709, 2, edgeIndex).Value;
            Assert.Contains(
                matches,
                candidate =>
                    candidate.Edge.CanonicalDirectedEdgeId == expectedCanonicalId);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static (uint EdgeIndex, DirectedEdge Edge, IReadOnlyList<PointLL> Shape)
        FindFixtureEdge(GraphTile tile)
    {
        for (uint edgeIndex = 0; edgeIndex < tile.Header().Directededgecount(); edgeIndex++)
        {
            DirectedEdge edge = tile.DirectedEdge((int)edgeIndex);
            IReadOnlyList<PointLL> shape = tile.EdgeInfo(edge).Shape();
            if (shape.Count >= 2 && edge.Length >= 40)
            {
                return (edgeIndex, edge, shape);
            }
        }

        throw new Xunit.Sdk.XunitException(
            "No suitable directed edge was found in the Monaco graph fixture.");
    }

    private static TrafficSpatialGraphEdge Edge(
        ulong tileBaseId,
        uint directedEdgeIndex,
        ulong canonicalDirectedEdgeId,
        TrafficDirection direction,
        params (double Latitude, double Longitude)[] points)
        => new(
            tileBaseId,
            directedEdgeIndex,
            canonicalDirectedEdgeId,
            direction,
            Coordinates(points));

    private static TrafficSpatialGraphEdge EdgeWithNodes(
        ulong tileBaseId,
        uint directedEdgeIndex,
        ulong canonicalDirectedEdgeId,
        TrafficDirection direction,
        ulong startNodeId,
        ulong endNodeId,
        params (double Latitude, double Longitude)[] points)
        => new(
            tileBaseId,
            directedEdgeIndex,
            canonicalDirectedEdgeId,
            direction,
            Coordinates(points),
            startNodeId,
            endNodeId);

    private static IReadOnlyList<GeoCoordinate> Coordinates(
        params (double Latitude, double Longitude)[] points)
        => points
            .Select(point => new GeoCoordinate(point.Latitude, point.Longitude))
            .ToArray();

    private static TrafficGeometry Line(
        params (double Latitude, double Longitude)[] points)
        => new(
            TrafficGeometryKind.LineString,
            Coordinates(points),
            TrafficGeometryDirection.AlongCoordinates);

    private static TrafficGeometry Point(double latitude, double longitude)
        => new(TrafficGeometryKind.Point, [new GeoCoordinate(latitude, longitude)]);

    private static NormalizedTrafficEvent Closure(TrafficGeometry geometry)
        => new(
            id: "closure-1",
            providerId: "provider",
            kind: NormalizedTrafficEventKind.Closure,
            geometry: geometry,
            currentSpeedKph: null,
            freeFlowSpeedKph: null,
            currentTravelTimeSeconds: null,
            freeFlowTravelTimeSeconds: null,
            delaySeconds: null,
            roadClosure: true,
            severity: TrafficSeverity.Closed,
            confidence: 1,
            description: null,
            observedAtUtc: null,
            updatedAtUtc: null,
            fetchedAtUtc: DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            validFromUtc: null,
            validUntilUtc: null,
            sourceUri: null,
            providerReferences: new Dictionary<string, string>());

    private static string FindMonacoTileDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "artifacts",
                "valhalla-monaco-tiles");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Tracked Monaco graph tile fixture was not found.");
    }

    private sealed class OwnerTileGraphSource(
        GraphId ownerTile,
        TrafficSpatialGraphEdge edge) : IValhallaTrafficSpatialGraphSource
    {
        public IReadOnlyList<GraphId> LastQueryTileIds { get; private set; } = [];

        public Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQueryTileIds = query.TileIds;
            IReadOnlyList<TrafficSpatialGraphEdge> result = query.TileIds.Any(
                tile => tile.TileBase().Value == ownerTile.TileBase().Value)
                ? [edge]
                : [];
            return Task.FromResult(result);
        }
    }

    private sealed class StubGraphSource(IReadOnlyList<TrafficSpatialGraphEdge> edges)
        : IValhallaTrafficSpatialGraphSource
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(edges);
        }
    }

    private sealed class SignatureRecordingGraphSource
        : IValhallaTrafficSpatialGraphSource
    {
        private readonly Dictionary<string, int> _reads =
            new(StringComparer.Ordinal);

        public int ReadCount(string graphSignature)
            => _reads.GetValueOrDefault(graphSignature);

        public Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads[context.GraphSignature] =
                _reads.GetValueOrDefault(context.GraphSignature) + 1;
            return Task.FromResult<IReadOnlyList<TrafficSpatialGraphEdge>>([]);
        }
    }

    private sealed class RecordingTileBatchGraphSource
        : IValhallaTrafficSpatialGraphSource
    {
        private readonly HashSet<ulong> _observedTileIds = [];

        public int ReadCount { get; private set; }

        public int MaximumObservedTileCount { get; private set; }

        public int DistinctObservedTileCount => _observedTileIds.Count;

        public Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            MaximumObservedTileCount =
                Math.Max(MaximumObservedTileCount, query.TileIds.Count);
            _observedTileIds.UnionWith(
                query.TileIds.Select(static tileId => tileId.TileBase().Value));
            return Task.FromResult<IReadOnlyList<TrafficSpatialGraphEdge>>([]);
        }
    }

    private sealed class LargeGraphSource : IValhallaTrafficSpatialGraphSource
    {
        private readonly IReadOnlyList<TrafficSpatialGraphEdge> _edges;

        public LargeGraphSource(int edgeCount)
        {
            ulong tileBaseId = new GraphId(8000, 2, 0).Value;
            _edges = Enumerable
                .Range(0, edgeCount)
                .Select(index => Edge(
                    tileBaseId,
                    (uint)index,
                    new GraphId(8000, 2, (uint)index).Value,
                    TrafficDirection.Forward,
                    (36.1000, -86.7000),
                    (36.1005, -86.7000)))
                .ToArray();
        }

        public TaskCompletionSource Returned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Returned.TrySetResult();
            return Task.FromResult(_edges);
        }
    }

    private sealed class CapacityPressureGraphSource(int expectedBuildCount)
        : IValhallaTrafficSpatialGraphSource
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<TrafficSpatialGraphEdge>>>
            _completions = new(StringComparer.Ordinal);
        private readonly List<string> _cancelledSignatures = [];
        private bool _completeFutureReads;

        public TaskCompletionSource AllStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> CancelledSignatures
        {
            get
            {
                lock (_gate)
                {
                    return _cancelledSignatures.ToArray();
                }
            }
        }

        public int StartedCount
        {
            get
            {
                lock (_gate)
                {
                    return _completions.Count;
                }
            }
        }

        public async Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            var completion =
                new TaskCompletionSource<IReadOnlyList<TrafficSpatialGraphEdge>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _completions.Add(context.GraphSignature, completion);
                if (_completions.Count == expectedBuildCount)
                {
                    AllStarted.TrySetResult();
                }

                if (_completeFutureReads)
                {
                    completion.TrySetResult([]);
                }
            }

            try
            {
                return await completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _cancelledSignatures.Add(context.GraphSignature);
                }

                throw;
            }
        }

        public void CompleteAll()
        {
            TaskCompletionSource<IReadOnlyList<TrafficSpatialGraphEdge>>[] completions;
            lock (_gate)
            {
                _completeFutureReads = true;
                completions = _completions.Values.ToArray();
            }

            foreach (TaskCompletionSource<IReadOnlyList<TrafficSpatialGraphEdge>> completion
                     in completions)
            {
                completion.TrySetResult([]);
            }
        }
    }

    private sealed class CancelOnFirstGraphSource : IValhallaTrafficSpatialGraphSource
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount { get; private set; }

        public async Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            if (ReadCount > 1)
            {
                return [];
            }

            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return [];
        }
    }

    private sealed class ControlledGraphSource : IValhallaTrafficSpatialGraphSource
    {
        private readonly TaskCompletionSource<IReadOnlyList<TrafficSpatialGraphEdge>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            Started.TrySetResult();
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(IReadOnlyList<TrafficSpatialGraphEdge> edges)
            => _completion.TrySetResult(edges);
    }
}
