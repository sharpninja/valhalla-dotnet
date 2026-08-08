using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;
using Xunit;

namespace SharpNinja.Valhalla.Tests.Nashville;

public sealed class NashvilleLaneFrictionIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public NashvilleLaneFrictionIntegrationTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public async Task CentennialParkToBna_FailsClosedWhenRealGraphLaneConnectivityIsIncomplete()
    {
        string repositoryRoot = FindRepositoryRoot();
        string pbf = Path.Combine(repositoryRoot, "artifacts", "nashville.osm.pbf");
        Assert.True(File.Exists(pbf), $"Nashville PBF fixture not found: '{pbf}'.");

        string tileDirectory = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Tests",
            "nashville-lane-friction",
            Guid.NewGuid().ToString("N"));
        try
        {
            TileBuilderResult build = TileBuilder.BuildTileSet(
                [pbf],
                tileDirectory,
                new TileBuilderConfig { Hierarchy = true, Shortcuts = true });
            Assert.True(build.Success);
            Assert.True(build.TileCount > 0);

            var client = new EmbeddedValhallaRoutingClient(
                new EmbeddedValhallaGraphReaderFactory(),
                new FixedTileDirectoryProvider(tileDirectory),
                NullLogger<EmbeddedValhallaRoutingClient>.Instance);
            OsmRouteResult result = await client.CalculateRouteAsync(
                new OsmRouteRequest(
                    Endpoint: null,
                    Origin: new GeoCoordinate(36.1497d, -86.8133d),
                    Destination: new GeoCoordinate(36.1196d, -86.6827d),
                    Costing: OsmRouteCostings.Auto,
                    ComputeAlternativeRoutes: true),
                TestContext.Current.CancellationToken);

            Assert.Null(result.Error);
            Assert.NotEmpty(result.Routes);
            Assert.All(result.Routes, static route => Assert.NotEmpty(route.DirectedEdgeIds ?? []));

            using var index = new GraphTileLaneTopologyIndex();
            var projector = new ValhallaRouteLaneFrictionProjector(index);
            var graphContext = new ValhallaGraphTrafficContext(
                $"nashville-real-{build.TileCount}-{build.WayCount}",
                tileDirectory);
            RouteGraphLaneEvidence[] graphEvidence = result.Routes
                .Select(route => InspectRouteLaneEvidence(
                    tileDirectory,
                    route.DirectedEdgeIds ?? []))
                .ToArray();
            var tagCollector = new RouteWayTagCollector(
                graphEvidence.SelectMany(static route => route.Edges)
                    .Select(static edge => edge.WayId)
                    .ToHashSet());
            new OsmPbfReader(tagCollector).Parse(pbf);

            // Route selection can legitimately move onto ways that have raw OSM connectivity relations.
            // Completeness is determined from the graph projection below, not from a zero-relation shortcut.
            _output.WriteLine(
                $"raw PBF connectivity relations touching routed ways={tagCollector.ConnectivityRelations.Count}");
            Assert.All(
                graphEvidence,
                routeEvidence =>
                {
                    Assert.DoesNotContain(
                        routeEvidence.Edges,
                        edge =>
                            TryGetDirectionalLaneCount(
                                edge,
                                tagCollector.TagsByWayId,
                                out uint sourceLaneCount) &&
                            edge.GraphLaneCount != sourceLaneCount);
                    Assert.DoesNotContain(
                        routeEvidence.Edges,
                        edge =>
                            TryGetDirectionalTurnLanes(
                                edge,
                                tagCollector.TagsByWayId,
                                out _) &&
                            edge.TurnLaneMasks.Count == 0);
                });

            var projections = new List<RouteLaneFrictionProjection>();
            for (var routeIndex = 0; routeIndex < result.Routes.Count; routeIndex++)
            {
                OsmRouteCandidate route = result.Routes[routeIndex];
                RouteLaneFrictionProjection projection = await projector.ProjectAsync(
                    route,
                    LaneFrictionVehicleClass.Truck,
                    graphContext,
                    TestContext.Current.CancellationToken);
                projections.Add(projection);

                string corridors = string.Join(
                    ", ",
                    GetNamedCorridors(tileDirectory, route.DirectedEdgeIds ?? []));
                RouteGraphLaneEvidence evidence = graphEvidence[routeIndex];
                ValhallaLaneTopologySnapshot topologySnapshot =
                    await index.ReadAsync(
                        graphContext,
                        route.DirectedEdgeIds ?? [],
                        TestContext.Current.CancellationToken);
                WriteEvidence(
                    routeIndex,
                    route,
                    projection,
                    corridors,
                    evidence,
                    topologySnapshot,
                    tagCollector.TagsByWayId,
                    tagCollector.ConnectivityRelations);

                if (projection.HasRouteLanePath)
                {
                    Assert.NotEmpty(projection.RouteSegments);
                    Assert.DoesNotContain(
                        projection.Profile.Guidance,
                        static point => string.IsNullOrWhiteSpace(point.Instruction));
                }
                else
                {
                    Assert.Contains(
                        projection.FailureReason,
                        new[]
                        {
                            LaneProjectionFailureReason.MissingLaneConnectivity,
                            LaneProjectionFailureReason.InfeasibleLaneChanges,
                        });
                    Assert.Empty(projection.Profile.Guidance);
                    Assert.Empty(projection.Profile.Contributions);
                }
            }

            Assert.Contains(
                projections,
                static projection =>
                    projection.FailureReason == LaneProjectionFailureReason.MissingLaneConnectivity);

            string exactGraphSignature =
                await ComputeGraphArtifactSignatureAsync(
                    tileDirectory,
                    TestContext.Current.CancellationToken);
            _output.WriteLine($"generated graph artifact signature={exactGraphSignature}");

            string overlayFixturePath = Path.Combine(
                repositoryRoot,
                "tests",
                "SharpNinja.Valhalla.Tests",
                "Nashville",
                "Fixtures",
                "centennial-park-to-bna-lane-overlay.v1.json");
            Assert.True(
                File.Exists(overlayFixturePath),
                $"Canonical lane overlay fixture not found: '{overlayFixturePath}'.");
            var overlayContext = new ValhallaGraphTrafficContext(
                exactGraphSignature,
                tileDirectory);
            var fileSource = new JsonFileLaneTopologyOverlaySource(overlayFixturePath);
            LaneTopologyOverlayLoadResult fixtureLoad = await fileSource.LoadAsync(
                new LaneTopologyOverlayRequest(
                    exactGraphSignature,
                    result.Routes
                        .SelectMany(static route => route.DirectedEdgeIds ?? [])
                        .Distinct()
                        .ToArray()),
                TestContext.Current.CancellationToken);
            Assert.Equal(LaneTopologyOverlayLoadStatus.Loaded, fixtureLoad.Status);
            CanonicalLaneTopologyOverlay validOverlay = Assert.IsType<CanonicalLaneTopologyOverlay>(
                fixtureLoad.Overlay);
            Assert.Equal(exactGraphSignature, validOverlay.Descriptor.GraphSignature);
            Assert.Equal(
                LaneTopologyOverlayProvenance.Test,
                validOverlay.Descriptor.Provenance);

            using (var identityIndex = new GraphTileLaneTopologyIndex())
            {
                ValhallaLaneTopologySnapshot identitySnapshot =
                    await identityIndex.ReadAsync(
                        overlayContext,
                        result.Routes
                            .SelectMany(static route => route.DirectedEdgeIds ?? [])
                            .Distinct()
                            .ToArray(),
                        TestContext.Current.CancellationToken);
                Assert.All(
                    validOverlay.Edges,
                    overlayEdge =>
                    {
                        if (!identitySnapshot.Edges.TryGetValue(
                                overlayEdge.CanonicalDirectedEdgeId,
                                out LaneTopologySegment? segment))
                        {
                            ulong[] replacementCandidates = identitySnapshot.Edges
                                .Where(pair =>
                                    pair.Value.GraphEvidence is LaneTopologyGraphEvidence evidence &&
                                    evidence.CanonicalStartNodeId == overlayEdge.CanonicalStartNodeId &&
                                    evidence.CanonicalEndNodeId == overlayEdge.CanonicalEndNodeId)
                                .Select(static pair => pair.Key)
                                .ToArray();
                            Assert.Fail(
                                $"Overlay edge {overlayEdge.CanonicalDirectedEdgeId} is absent; " +
                                $"same-node candidates=[{string.Join(",", replacementCandidates)}].");
                        }

                        LaneTopologyGraphEvidence graphIdentity =
                            Assert.IsType<LaneTopologyGraphEvidence>(
                                segment.GraphEvidence);
                        Assert.Equal(
                            overlayEdge.CanonicalStartNodeId,
                            graphIdentity.CanonicalStartNodeId);
                        Assert.Equal(
                            overlayEdge.CanonicalEndNodeId,
                            graphIdentity.CanonicalEndNodeId);
                        Assert.Equal(overlayEdge.LaneCount, segment.LaneCount);
                    });
            }

            RouteLaneFrictionProjection[] overlayProjections;
            using (var overlayIndex = new GraphTileLaneTopologyIndex(
                fileSource,
                GraphTileLaneTopologyIndexOptions.Default))
            {
                var overlayProjector = new ValhallaRouteLaneFrictionProjector(
                    overlayIndex);
                var projected = new List<RouteLaneFrictionProjection>();
                foreach (OsmRouteCandidate route in result.Routes)
                {
                    projected.Add(await overlayProjector.ProjectAsync(
                        route,
                        LaneFrictionVehicleClass.Truck,
                        overlayContext,
                        TestContext.Current.CancellationToken));
                }

                overlayProjections = projected.ToArray();
            }

            int[] containsI40 = result.Routes
                .Select((route, index) => new
                {
                    Index = index,
                    Corridors = GetNamedCorridors(
                        tileDirectory,
                        route.DirectedEdgeIds ?? []),
                })
                .Where(static candidate => candidate.Corridors.Any(
                    corridor => corridor.Contains("I 40", StringComparison.OrdinalIgnoreCase)))
                .Select(static candidate => candidate.Index)
                .ToArray();
            int i40CandidateIndex = Assert.Single(containsI40);
            int[] i440Only = result.Routes
                .Select((route, index) => new
                {
                    Index = index,
                    Corridors = GetNamedCorridors(
                        tileDirectory,
                        route.DirectedEdgeIds ?? []),
                })
                .Where(static candidate =>
                    candidate.Corridors.Any(corridor => corridor.Contains(
                        "I 440",
                        StringComparison.OrdinalIgnoreCase)) &&
                    candidate.Corridors.All(corridor => !corridor.Contains(
                        "I 40",
                        StringComparison.OrdinalIgnoreCase)))
                .Select(static candidate => candidate.Index)
                .ToArray();
            int i440OnlyCandidateIndex = Assert.Single(i440Only);

            RouteLaneFrictionProjection i40Projection =
                overlayProjections[i40CandidateIndex];
            RouteLaneFrictionProjection i440Projection =
                overlayProjections[i440OnlyCandidateIndex];
            Assert.Equal(
                LaneProjectionFailureReason.MissingLaneConnectivity,
                i40Projection.FailureReason);
            Assert.Equal(
                LaneProjectionFailureReason.MissingLaneConnectivity,
                i440Projection.FailureReason);
            Assert.Empty(i40Projection.Profile.Guidance);
            Assert.Empty(i440Projection.Profile.Guidance);
            Assert.True(i40Projection.Profile.Score > 0);
            Assert.True(i440Projection.Profile.Score > 0);
            Assert.True(
                i40Projection.Profile.Score > i440Projection.Profile.Score,
                $"Expected the candidate containing I-40 structural score " +
                $"({i40Projection.Profile.Score}) to exceed the I-440-only " +
                $"candidate ({i440Projection.Profile.Score}).");
            _output.WriteLine(
                $"canonical overlay comparison: contains-I-40 score=" +
                $"{i40Projection.Profile.Score}; I-440-only score=" +
                $"{i440Projection.Profile.Score}; traffic-contributions=0; " +
                $"guidance={i40Projection.Profile.Guidance.Count}/" +
                $"{i440Projection.Profile.Guidance.Count}");
            Assert.All(
                i40Projection.Profile.Contributions
                    .Concat(i440Projection.Profile.Contributions),
                contribution =>
                {
                    Assert.Equal(
                        validOverlay.Descriptor,
                        contribution.OverlaySource);
                    Assert.DoesNotContain(
                        "traffic",
                        contribution.Description,
                        StringComparison.OrdinalIgnoreCase);
                });

            async Task AssertHostileOverlayFailsClosedAsync(
                CanonicalLaneTopologyOverlay hostileOverlay,
                LaneTopologyOverlayDiagnosticCode expectedDiagnostic)
            {
                using var hostileIndex = new GraphTileLaneTopologyIndex(
                    new StaticLaneTopologyOverlaySource(hostileOverlay),
                    GraphTileLaneTopologyIndexOptions.Default);
                var hostileProjector = new ValhallaRouteLaneFrictionProjector(
                    hostileIndex);
                RouteLaneFrictionProjection hostileProjection =
                    await hostileProjector.ProjectAsync(
                        result.Routes[i40CandidateIndex],
                        LaneFrictionVehicleClass.Truck,
                        overlayContext,
                        TestContext.Current.CancellationToken);

                Assert.Equal(
                    LaneProjectionFailureReason.CanonicalOverlayMismatch,
                    hostileProjection.FailureReason);
                Assert.Equal(0, hostileProjection.Profile.Score);
                Assert.Empty(hostileProjection.Profile.Contributions);
                Assert.Empty(hostileProjection.Profile.Guidance);
                Assert.Contains(
                    hostileProjection.OverlayDiagnostics,
                    diagnostic => diagnostic.Code == expectedDiagnostic);
            }

            await AssertHostileOverlayFailsClosedAsync(
                validOverlay with
                {
                    Descriptor = validOverlay.Descriptor with
                    {
                        GraphSignature = exactGraphSignature + "-mutated",
                    },
                },
                LaneTopologyOverlayDiagnosticCode.GraphSignatureMismatch);
            await AssertHostileOverlayFailsClosedAsync(
                validOverlay with
                {
                    Edges = validOverlay.Edges
                        .Select((edge, index) => index == 0
                            ? edge with
                            {
                                CanonicalDirectedEdgeId =
                                    result.Routes[i40CandidateIndex]
                                        .DirectedEdgeIds![0],
                            }
                            : edge)
                        .ToArray(),
                },
                LaneTopologyOverlayDiagnosticCode.CanonicalNodeMismatch);
            await AssertHostileOverlayFailsClosedAsync(
                validOverlay with
                {
                    Transitions = validOverlay.Transitions
                        .Select((transition, index) => index == 0
                            ? transition with
                            {
                                SharedCanonicalNodeId =
                                    transition.SharedCanonicalNodeId + 1,
                            }
                            : transition)
                        .ToArray(),
                },
                LaneTopologyOverlayDiagnosticCode.SharedCanonicalNodeMismatch);
            await AssertHostileOverlayFailsClosedAsync(
                validOverlay with
                {
                    FrictionPoints = validOverlay.FrictionPoints
                        .Select((point, index) => index == 0
                            ? point with { LaneNumber = 0 }
                            : point)
                        .ToArray(),
                },
                LaneTopologyOverlayDiagnosticCode.LaneOutOfRange);

            string graphMutationPath = Path.Combine(
                tileDirectory,
                "hostile-build-config.identity");
            await File.WriteAllTextAsync(
                graphMutationPath,
                "Hierarchy=false;Shortcuts=true",
                TestContext.Current.CancellationToken);
            string mutatedGraphArtifactSignature =
                await ComputeGraphArtifactSignatureAsync(
                    tileDirectory,
                    TestContext.Current.CancellationToken);
            Assert.NotEqual(exactGraphSignature, mutatedGraphArtifactSignature);
            using (var mutatedGraphIndex = new GraphTileLaneTopologyIndex(
                fileSource,
                GraphTileLaneTopologyIndexOptions.Default))
            {
                var mutatedGraphProjector =
                    new ValhallaRouteLaneFrictionProjector(mutatedGraphIndex);
                RouteLaneFrictionProjection staleOverlayProjection =
                    await mutatedGraphProjector.ProjectAsync(
                        result.Routes[i40CandidateIndex],
                        LaneFrictionVehicleClass.Truck,
                        new ValhallaGraphTrafficContext(
                            mutatedGraphArtifactSignature,
                            tileDirectory),
                        TestContext.Current.CancellationToken);
                Assert.Equal(
                    LaneProjectionFailureReason.CanonicalOverlayMismatch,
                    staleOverlayProjection.FailureReason);
                Assert.Equal(0, staleOverlayProjection.Profile.Score);
                Assert.Empty(staleOverlayProjection.Profile.Contributions);
                Assert.Empty(staleOverlayProjection.Profile.Guidance);
                Assert.Contains(
                    staleOverlayProjection.OverlayDiagnostics,
                    static diagnostic =>
                        diagnostic.Code ==
                        LaneTopologyOverlayDiagnosticCode.GraphSignatureMismatch);
            }
        }
        finally
        {
            if (Directory.Exists(tileDirectory))
            {
                Directory.Delete(tileDirectory, recursive: true);
            }
        }
    }


    private static async Task<string> ComputeGraphArtifactSignatureAsync(
        string tileDirectory,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] separator = [0];
        byte[] buffer = new byte[64 * 1024];
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(
            "TileBuilderConfig:v1;Hierarchy=true;Shortcuts=true"));
        hash.AppendData(separator);
        foreach (string path in Directory.EnumerateFiles(
                     tileDirectory,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderBy(
                     path => Path.GetRelativePath(tileDirectory, path),
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(tileDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(separator);
            await using FileStream stream = File.OpenRead(path);
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            hash.AppendData(separator);
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "artifacts", "nashville.osm.pbf")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? string.Empty;
    }

    private static IReadOnlyList<string> GetNamedCorridors(
        string tileDirectory,
        IReadOnlyList<ulong> directedEdgeIds)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ulong canonicalId in directedEdgeIds)
        {
            var edgeId = new GraphId(canonicalId);
            GraphTile? tile = GraphTile.Create(tileDirectory, edgeId.TileBase());
            if (tile is null || edgeId.Id() >= tile.DirectedEdgeCount())
            {
                continue;
            }

            DirectedEdge edge = tile.DirectedEdge(edgeId);
            foreach (string name in tile.EdgeInfo(edge).GetNames())
            {
                if (name.Contains("I 40", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("I 440", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }

        return names.ToArray();
    }

    private RouteGraphLaneEvidence InspectRouteLaneEvidence(
        string tileDirectory,
        IReadOnlyList<ulong> directedEdgeIds)
    {
        var edges = new List<EdgeLaneEvidence>(directedEdgeIds.Count);
        foreach (ulong canonicalId in directedEdgeIds)
        {
            var edgeId = new GraphId(canonicalId);
            GraphTile? tile = GraphTile.Create(tileDirectory, edgeId.TileBase());
            Assert.NotNull(tile);
            Assert.True(edgeId.Id() < tile.DirectedEdgeCount());

            DirectedEdge edge = tile.DirectedEdge(edgeId);
            IReadOnlyList<ushort> turnMasks = edge.TurnLanes
                ? tile.TurnLanes(checked((uint)edgeId.Id())).ToArray()
                : Array.Empty<ushort>();
            IReadOnlyList<LaneConnectivityEvidence> incoming = tile
                .GetLaneConnectivity(checked((uint)edgeId.Id()))
                .Select(static connection => new LaneConnectivityEvidence(
                    connection.From,
                    connection.FromLanes,
                    connection.ToLanes))
                .ToArray();
            edges.Add(new EdgeLaneEvidence(
                canonicalId,
                tile.EdgeInfo(edge).WayId,
                edge.Forward,
                edge.LaneCount,
                turnMasks,
                incoming));
        }

        return new RouteGraphLaneEvidence(edges);
    }

    private static string FormatDistribution<T>(IEnumerable<T> values)
        where T : struct, Enum
    {
        string[] distribution = values
            .GroupBy(static value => value)
            .OrderBy(static group => group.Key)
            .Select(static group => $"{group.Key}:{group.Count()}")
            .ToArray();
        return distribution.Length == 0
            ? "<none>"
            : string.Join(",", distribution);
    }

    private void WriteEvidence(
        int routeIndex,
        OsmRouteCandidate route,
        RouteLaneFrictionProjection projection,
        string corridors,
        RouteGraphLaneEvidence evidence,
        ValhallaLaneTopologySnapshot topologySnapshot,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>> tagsByWayId,
        IReadOnlyList<ConnectivityRelationEvidence> connectivityRelations)
    {
        string laneDistribution = string.Join(
            ",",
            evidence.Edges
                .GroupBy(static edge => edge.GraphLaneCount)
                .OrderBy(static group => group.Key)
                .Select(static group => $"{group.Key}:{group.Count()}"));
        int taggedEdges = evidence.Edges.Count(
            edge => TryGetDirectionalLaneCount(edge, tagsByWayId, out _));
        int turnLaneEdges = evidence.Edges.Count(static edge => edge.TurnLaneMasks.Count > 0);
        int sourceTurnLaneEdges = evidence.Edges.Count(
            edge => TryGetDirectionalTurnLanes(edge, tagsByWayId, out _));
        EdgeLaneEvidence[] sourceTurnLanesMissingFromGraph = evidence.Edges
            .Where(edge =>
                TryGetDirectionalTurnLanes(edge, tagsByWayId, out _) &&
                edge.TurnLaneMasks.Count == 0)
            .ToArray();
        EdgeLaneEvidence[] graphTurnLanesWithoutDirectionalSource = evidence.Edges
            .Where(edge =>
                edge.TurnLaneMasks.Count > 0 &&
                !TryGetDirectionalTurnLanes(edge, tagsByWayId, out _))
            .ToArray();
        EdgeLaneEvidence[] laneCountMismatches = evidence.Edges
            .Where(edge =>
                TryGetDirectionalLaneCount(edge, tagsByWayId, out uint taggedCount) &&
                edge.GraphLaneCount != taggedCount)
            .ToArray();
        int connectivityEdges = evidence.Edges.Count(static edge => edge.IncomingConnections.Count > 0);
        string transitionProvenance = FormatDistribution(
            projection.TransitionDerivations.Select(static derivation => derivation.Provenance));
        string transitionConfidence = FormatDistribution(
            projection.TransitionDerivations.Select(static derivation => derivation.Confidence));
        int guidanceEligibleTransitions = projection.TransitionDerivations.Count(
            static derivation => derivation.CanDriveGuidance);

        var explicitTransitions = 0;
        var sameWayKnownEqualTransitions = 0;
        var explicitSingleLaneTransitions = 0;
        var unresolved = new List<string>();
        for (var index = 0; index + 1 < evidence.Edges.Count; index++)
        {
            EdgeLaneEvidence from = evidence.Edges[index];
            EdgeLaneEvidence to = evidence.Edges[index + 1];
            bool explicitConnection = to.IncomingConnections.Any(
                connection => connection.FromWayId == from.WayId &&
                              !string.IsNullOrWhiteSpace(connection.FromLanes) &&
                              !string.IsNullOrWhiteSpace(connection.ToLanes));
            if (explicitConnection)
            {
                explicitTransitions++;
                continue;
            }

            bool fromKnown = TryGetDirectionalLaneCount(from, tagsByWayId, out uint fromTaggedCount);
            bool toKnown = TryGetDirectionalLaneCount(to, tagsByWayId, out uint toTaggedCount);
            if (from.WayId == to.WayId &&
                fromKnown &&
                toKnown &&
                fromTaggedCount == toTaggedCount &&
                from.GraphLaneCount == to.GraphLaneCount)
            {
                sameWayKnownEqualTransitions++;
                continue;
            }

            if (fromKnown &&
                toKnown &&
                fromTaggedCount == 1 &&
                toTaggedCount == 1 &&
                from.GraphLaneCount == 1 &&
                to.GraphLaneCount == 1)
            {
                explicitSingleLaneTransitions++;
                continue;
            }

            unresolved.Add(string.Format(
                CultureInfo.InvariantCulture,
                "#{0} {1:X16}/way={2}/lanes={3}/tagged={4} -> {5:X16}/way={6}/lanes={7}/tagged={8}",
                index,
                from.CanonicalDirectedEdgeId,
                from.WayId,
                from.GraphLaneCount,
                fromKnown ? fromTaggedCount : 0,
                to.CanonicalDirectedEdgeId,
                to.WayId,
                to.GraphLaneCount,
                toKnown ? toTaggedCount : 0));
        }

        _output.WriteLine(
            $"route[{routeIndex}] {route.DistanceMeters:F0}m/{route.DurationSeconds}s corridors=[{corridors}] " +
            $"status={projection.FailureReason}; score={projection.Profile.Score}; " +
            $"canonical-points={projection.Profile.CanonicalPointCount}; " +
            $"route-lane-changes={projection.Profile.RouteLaneChangeCount}; " +
            $"adjacent-merges={projection.Profile.AdjacentMergeCount}; " +
            $"guidance={projection.Profile.Guidance.Count}; " +
            $"transition-derivations={projection.TransitionDerivations.Count}; " +
            $"guidance-eligible-transitions={guidanceEligibleTransitions}; " +
            $"provenance=[{transitionProvenance}]; confidence=[{transitionConfidence}]");
        _output.WriteLine(
            $"route[{routeIndex}] graph evidence edges={evidence.Edges.Count}; " +
            $"directionally-tagged-lane-edges={taggedEdges}; lane-counts=[{laneDistribution}]; " +
            $"source-turn-lane-edges={sourceTurnLaneEdges}; graph-turn-lane-edges={turnLaneEdges}; " +
            $"source-turn-lanes-missing-from-graph={sourceTurnLanesMissingFromGraph.Length}; " +
            $"graph-turn-lanes-without-directional-source={graphTurnLanesWithoutDirectionalSource.Length}; " +
            $"lane-count-mismatches={laneCountMismatches.Length}; " +
            $"incoming-connectivity-edges={connectivityEdges}; " +
            $"legacy-audit-transitions explicit={explicitTransitions}; " +
            $"same-way-known-equal={sameWayKnownEqualTransitions}; " +
            $"explicit-single-lane={explicitSingleLaneTransitions}; unresolved={unresolved.Count}");

        for (var transitionIndex = 0;
             transitionIndex < projection.TransitionDerivations.Count;
             transitionIndex++)
        {
            LaneTransitionDerivation derivation =
                projection.TransitionDerivations[transitionIndex];
            string evidenceKinds = string.Join(
                ",",
                derivation.Evidence
                    .Select(static item => item.Kind)
                    .Distinct()
                    .Order());
            _output.WriteLine(
                $"route[{routeIndex}] transition[{transitionIndex}] " +
                $"{derivation.FromSegmentId}->{derivation.ToSegmentId}; " +
                $"provenance={derivation.Provenance}; confidence={derivation.Confidence}; " +
                $"change={derivation.ChangeKind}; can-guide={derivation.CanDriveGuidance}; " +
                $"options={derivation.Options.Count}; evidence=[{evidenceKinds}]");
        }

        foreach (EdgeLaneEvidence edge in sourceTurnLanesMissingFromGraph)
        {
            TryGetDirectionalTurnLanes(edge, tagsByWayId, out string sourceTurnLanes);
            _output.WriteLine(
                $"route[{routeIndex}] BUILDER-TURN-LANE-GAP id={edge.CanonicalDirectedEdgeId:X16}; " +
                $"way={edge.WayId}; forward={edge.Forward}; source={sourceTurnLanes}");
        }

        foreach (EdgeLaneEvidence edge in laneCountMismatches)
        {
            TryGetDirectionalLaneCount(edge, tagsByWayId, out uint sourceLaneCount);
            _output.WriteLine(
                $"route[{routeIndex}] BUILDER-LANE-COUNT-GAP id={edge.CanonicalDirectedEdgeId:X16}; " +
                $"way={edge.WayId}; forward={edge.Forward}; source={sourceLaneCount}; graph={edge.GraphLaneCount}");
        }

        for (var edgeIndex = 0; edgeIndex < evidence.Edges.Count; edgeIndex++)
        {
            EdgeLaneEvidence edge = evidence.Edges[edgeIndex];
            string masks = edge.TurnLaneMasks.Count == 0
                ? "<none>"
                : string.Join(",", edge.TurnLaneMasks.Select(static mask => $"0x{mask:X4}"));
            string incoming = edge.IncomingConnections.Count == 0
                ? "<none>"
                : string.Join(
                    ",",
                    edge.IncomingConnections.Select(static connection =>
                        $"{connection.FromWayId}:{connection.FromLanes}>{connection.ToLanes}"));
            LaneTopologyGraphEvidence? graph = topologySnapshot
                .Edges[edge.CanonicalDirectedEdgeId]
                .GraphEvidence;
            string references = graph is null
                ? "<none>"
                : string.Join(",", graph.References);
            string destinations = graph is null
                ? "<none>"
                : string.Join(",", graph.Destinations);
            _output.WriteLine(
                $"route[{routeIndex}] edge[{edgeIndex}] id={edge.CanonicalDirectedEdgeId:X16}; " +
                $"start={(graph?.CanonicalStartNodeId ?? 0UL):X16}; " +
                $"end={(graph?.CanonicalEndNodeId ?? 0UL):X16}; " +
                $"way={edge.WayId}; forward={edge.Forward}; lanes={edge.GraphLaneCount}; " +
                $"refs=[{references}]; destinations=[{destinations}]; " +
                $"turn-masks=[{masks}]; incoming=[{incoming}]");
        }

        foreach (string transition in unresolved)
        {
            _output.WriteLine($"route[{routeIndex}] unresolved {transition}");
        }

        foreach (ulong wayId in evidence.Edges.Select(static edge => edge.WayId).Distinct().Order())
        {
            tagsByWayId.TryGetValue(wayId, out IReadOnlyDictionary<string, string>? tags);
            _output.WriteLine(
                $"route[{routeIndex}] way {wayId} tags: {FormatRelevantTags(tags)}");
        }

        HashSet<ulong> routeWayIds = evidence.Edges
            .Select(static edge => edge.WayId)
            .ToHashSet();
        ConnectivityRelationEvidence[] applicableRelations = connectivityRelations
            .Where(relation => relation.Members.Any(
                member => member.Type == OsmMemberType.Way && routeWayIds.Contains(member.Id)))
            .ToArray();
        _output.WriteLine(
            $"route[{routeIndex}] source connectivity relations touching route ways={applicableRelations.Length}");
        foreach (ConnectivityRelationEvidence relation in applicableRelations)
        {
            string members = string.Join(
                ",",
                relation.Members.Select(static member => $"{member.Role}:{member.Type}:{member.Id}"));
            string tags = string.Join(
                "; ",
                relation.Tags.OrderBy(static pair => pair.Key)
                    .Select(static pair => $"{pair.Key}={pair.Value}"));
            _output.WriteLine(
                $"route[{routeIndex}] relation {relation.Id} members=[{members}] tags=[{tags}]");
        }
    }

    private static bool TryGetDirectionalLaneCount(
        EdgeLaneEvidence edge,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>> tagsByWayId,
        out uint laneCount)
    {
        laneCount = 0;
        if (!tagsByWayId.TryGetValue(edge.WayId, out IReadOnlyDictionary<string, string>? tags))
        {
            return false;
        }

        string directionalKey = edge.Forward ? "lanes:forward" : "lanes:backward";
        if (tags.TryGetValue(directionalKey, out string? directional) &&
            uint.TryParse(directional, NumberStyles.Integer, CultureInfo.InvariantCulture, out laneCount) &&
            laneCount > 0)
        {
            return true;
        }

        bool oneWay = tags.TryGetValue("oneway", out string? oneWayValue) &&
            (string.Equals(oneWayValue, "yes", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(oneWayValue, "1", StringComparison.Ordinal));
        return oneWay &&
            tags.TryGetValue("lanes", out string? lanes) &&
            uint.TryParse(lanes, NumberStyles.Integer, CultureInfo.InvariantCulture, out laneCount) &&
            laneCount > 0;
    }

    private static bool TryGetDirectionalTurnLanes(
        EdgeLaneEvidence edge,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>> tagsByWayId,
        out string turnLanes)
    {
        turnLanes = string.Empty;
        if (!tagsByWayId.TryGetValue(edge.WayId, out IReadOnlyDictionary<string, string>? tags))
        {
            return false;
        }

        string directionalKey = edge.Forward ? "turn:lanes:forward" : "turn:lanes:backward";
        if (tags.TryGetValue(directionalKey, out string? directional) &&
            !string.IsNullOrWhiteSpace(directional))
        {
            turnLanes = directional;
            return true;
        }

        bool oneWay = tags.TryGetValue("oneway", out string? oneWayValue) &&
            (string.Equals(oneWayValue, "yes", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(oneWayValue, "1", StringComparison.Ordinal));
        if (oneWay &&
            tags.TryGetValue("turn:lanes", out string? lanes) &&
            !string.IsNullOrWhiteSpace(lanes))
        {
            turnLanes = lanes;
            return true;
        }

        return false;
    }

    private static string FormatRelevantTags(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return "<none in clipped PBF>";
        }

        string[] relevantKeys =
        [
            "highway",
            "ref",
            "oneway",
            "lanes",
            "lanes:forward",
            "lanes:backward",
            "turn:lanes",
            "turn:lanes:forward",
            "turn:lanes:backward",
            "destination:lanes",
            "connectivity",
        ];
        string[] relevant = relevantKeys
            .Where(tags.ContainsKey)
            .Select(key => $"{key}={tags[key]}")
            .ToArray();
        return relevant.Length == 0 ? "<no lane/connectivity tags>" : string.Join("; ", relevant);
    }


    private sealed class StaticLaneTopologyOverlaySource(
        CanonicalLaneTopologyOverlay overlay) : ILaneTopologyOverlaySource
    {
        public ValueTask<LaneTopologyOverlayLoadResult> LoadAsync(
            LaneTopologyOverlayRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                LaneTopologyOverlayLoadResult.Loaded(overlay));
        }
    }

    private sealed class RouteWayTagCollector(IReadOnlySet<ulong> targetWayIds) : IOsmPbfVisitor
    {
        public Dictionary<ulong, IReadOnlyDictionary<string, string>> TagsByWayId { get; } = [];

        public List<ConnectivityRelationEvidence> ConnectivityRelations { get; } = [];

        public void Header(
            double? minLat,
            double? minLon,
            double? maxLat,
            double? maxLon,
            IReadOnlyList<string> requiredFeatures)
        {
        }

        public void Node(
            ulong id,
            double lat,
            double lon,
            IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Way(
            ulong id,
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags)
        {
            if (targetWayIds.Contains(id))
            {
                TagsByWayId[id] = tags.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);
            }
        }

        public void Relation(
            ulong id,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> tags)
        {
            if (tags.TryGetValue("type", out string? type) &&
                string.Equals(type, "connectivity", StringComparison.OrdinalIgnoreCase) &&
                members.Any(member =>
                    member.Type == OsmMemberType.Way && targetWayIds.Contains(member.Id)))
            {
                ConnectivityRelations.Add(new ConnectivityRelationEvidence(
                    id,
                    members.ToArray(),
                    tags.ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.Ordinal)));
            }
        }
    }

    private sealed record ConnectivityRelationEvidence(
        ulong Id,
        IReadOnlyList<OsmRelationMember> Members,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record RouteGraphLaneEvidence(IReadOnlyList<EdgeLaneEvidence> Edges);

    private sealed record EdgeLaneEvidence(
        ulong CanonicalDirectedEdgeId,
        ulong WayId,
        bool Forward,
        uint GraphLaneCount,
        IReadOnlyList<ushort> TurnLaneMasks,
        IReadOnlyList<LaneConnectivityEvidence> IncomingConnections);

    private sealed record LaneConnectivityEvidence(
        ulong FromWayId,
        string FromLanes,
        string ToLanes);

    private sealed class FixedTileDirectoryProvider(string directory) : IOsmTileDirectoryProvider
    {
        public Task<string?> GetTileDirectoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(directory);
    }
}
