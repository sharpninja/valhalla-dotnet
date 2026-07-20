using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class EmbeddedValhallaRoutingTrafficProvenanceBehaviorTests
{
    [Fact]
    public async Task MapCandidate_PinnedLiveTrafficGeneration_PreservesVersionAndDoesNotDoubleCountEta()
    {
        string graphRoot = FindMonacoFixture();
        (GraphId tileId, GraphTile tile, uint edgeIndex, DirectedEdge edge) = FindUsableEdge(graphRoot);
        string storeRoot = NewTempDirectory();
        try
        {
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            double currentSpeed = Math.Max(2d, Math.Floor(edge.Speed / 2d));
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            ValhallaTrafficWriteResult write = await writer.WriteAsync(
                new[]
                {
                    new ValhallaTrafficEdgeUpdate(
                        tileId.Value,
                        edgeIndex,
                        TrafficDirection.Forward,
                        currentSpeed,
                        edge.Speed,
                        null,
                        false,
                        false,
                        true,
                        1d,
                        "live-speed",
                        "behavior",
                        new GraphId(tileId.Tileid(), tileId.Level(), edgeIndex).Value),
                },
                new ValhallaTrafficWriteOptions(storeRoot)
                {
                    GraphTileDirectory = graphRoot,
                    GraphSha256 = graphSha,
                    CreatedAtUtc = created,
                    ExpiresAtUtc = created.AddMinutes(20),
                    Policy = TrafficSnapshotPolicy.Enabled,
                },
                TestContext.Current.CancellationToken);

            Assert.True(
                write.Succeeded,
                string.Join(
                    Environment.NewLine,
                    write.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            TrafficSnapshotReference snapshot = Assert.IsType<TrafficSnapshotReference>(write.Snapshot);
            await using var factory = new EmbeddedValhallaGraphReaderFactory(store);
            await using EmbeddedValhallaGraphReaderFactory.AsyncLease lease = await factory.AcquireAsync(
                graphRoot,
                snapshot,
                TestContext.Current.CancellationToken);

            GraphId directedEdgeId = new(tileId.Tileid(), tileId.Level(), edgeIndex);
            var trip = new TripLeg();
            trip.Edges.Add(new TripEdge
            {
                EdgeId = directedEdgeId,
                LengthKm = edge.Length / 1000f,
            });
            trip.Nodes.Add(new TripNode
            {
                ElapsedCost = new Cost(914f, 914f),
            });

            OsmRouteCandidate candidate = EmbeddedValhallaRoutingClient.MapCandidate(
                trip,
                new DirectionsLeg(),
                snapshot,
                lease.Reader,
                engineAppliedTrafficDelaySeconds: 14);

            Assert.Equal(RouteDurationSource.LiveTraffic, candidate.DurationSource);
            Assert.Equal(snapshot.Version, candidate.TrafficSnapshotVersion);
            Assert.Equal(14, candidate.EngineAppliedTrafficDelaySeconds);
            Assert.Equal(914, candidate.DurationSeconds);

            var metrics = new RouteCandidateMetrics(
                ProviderId: "valhalla",
                Index: 0,
                DistanceMeters: candidate.DistanceMeters,
                DurationSeconds: candidate.DurationSeconds,
                TrafficDelaySeconds: candidate.EngineAppliedTrafficDelaySeconds,
                DirectedEdgeIds: candidate.DirectedEdgeIds,
                DurationSource: candidate.DurationSource);
            Assert.Equal(
                candidate.DurationSeconds,
                TrafficAwareRerouteRanker.AdjustedEtaSeconds(metrics, TrafficPolicy.Enabled));
        }
        finally
        {
            if (Directory.Exists(storeRoot))
            {
                Directory.Delete(storeRoot, recursive: true);
            }
        }
    }

    internal async Task CalculateRouteAsync_LiveSnapshotFlowsThroughEngineAndProvenance()
    {
        string graphRoot = FindMonacoFixture();
        string storeRoot = NewTempDirectory();
        try
        {
            DateTimeOffset departure =
                DateTimeOffset.UtcNow.AddMinutes(5).ToOffset(TimeSpan.FromHours(-5));
            TimeInfo invariantTime = InvariantTrafficTime.Create(departure);
            Assert.True(invariantTime.Valid);
            Assert.Equal(0ul, invariantTime.TimezoneIndex);
            Assert.Equal(
                (ulong)departure.UtcDateTime.Subtract(
                    departure.UtcDateTime.Date.AddDays(
                        -(((int)departure.UtcDateTime.DayOfWeek + 6) % 7))).TotalSeconds,
                invariantTime.SecondOfWeek);

            var store = new TrafficSnapshotStore(storeRoot);
            await using var factory = new EmbeddedValhallaGraphReaderFactory(store);
            var logger = new RecordingRoutingLogger();
            var client = new EmbeddedValhallaRoutingClient(
                factory,
                new FixedTileDirectoryProvider(graphRoot),
                logger,
                new ThrowingTimeProvider());
            var request = new OsmRouteRequest(
                null,
                new GeoCoordinate(43.7305, 7.4160),
                new GeoCoordinate(43.7384, 7.4246),
                ComputeAlternativeRoutes: false)
            {
                DepartureTimeUtc = departure,
            };

            OsmRouteResult baselineResult = await client.CalculateRouteAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Null(baselineResult.Error);
            OsmRouteCandidate baseline = Assert.Single(baselineResult.Routes);
            Assert.NotNull(baseline.DirectedEdgeIds);
            Assert.NotEmpty(baseline.DirectedEdgeIds);

            HashSet<ulong> baselineEdges = baseline.DirectedEdgeIds.ToHashSet();
            var updates = new List<ValhallaTrafficEdgeUpdate>();
            ValhallaTrafficEdgeUpdate? decoyClosure = null;
            foreach (string file in Directory.EnumerateFiles(
                         graphRoot,
                         "*.gph",
                         SearchOption.AllDirectories).OrderBy(
                             static path => path,
                             StringComparer.Ordinal))
            {
                GraphId tileId = ParseGraphId(graphRoot, file);
                GraphTile? tile = GraphTile.Create(graphRoot, tileId);
                if (tile is null)
                {
                    continue;
                }

                for (uint edgeIndex = 0; edgeIndex < tile.DirectedEdgeCount(); edgeIndex++)
                {
                    DirectedEdge edge = tile.DirectedEdge((int)edgeIndex);
                    ulong canonicalId =
                        new GraphId(tileId.Tileid(), tileId.Level(), edgeIndex).Value;
                    byte nonCurrentFlowSources = 0;
                    uint nonCurrentSpeed = tile.GetSpeed(
                        edge,
                        edgeIndex,
                        (byte)(GraphConstants.DefaultFlowMask &
                               ~GraphConstants.CurrentFlowMask),
                        invariantTime.SecondOfWeek,
                        false,
                        out nonCurrentFlowSources,
                        0);
                    double slowerCurrentSpeed =
                        Math.Max(2d, 2d * Math.Floor(nonCurrentSpeed / 4d));
                    Assert.True(
                        slowerCurrentSpeed < nonCurrentSpeed,
                        $"Edge {canonicalId} has no representable strictly slower " +
                        $"current speed: non-current={nonCurrentSpeed}.");
                    updates.Add(new ValhallaTrafficEdgeUpdate(
                        tileId.Value,
                        edgeIndex,
                        TrafficDirection.Forward,
                        slowerCurrentSpeed,
                        nonCurrentSpeed,
                        null,
                        false,
                        false,
                        true,
                        1d,
                        "live-" + canonicalId,
                        "behavior",
                        canonicalId));
                    if (decoyClosure is null && !baselineEdges.Contains(canonicalId))
                    {
                        decoyClosure = new ValhallaTrafficEdgeUpdate(
                            tileId.Value,
                            edgeIndex,
                            TrafficDirection.Forward,
                            null,
                            edge.Speed,
                            null,
                            true,
                            false,
                            true,
                            1d,
                            "closed-decoy",
                            "behavior",
                            canonicalId);
                    }
                }
            }

            Assert.NotNull(decoyClosure);
            // The closure is validated through a separate closure-only snapshot below.
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            DateTimeOffset created = departure.ToUniversalTime().AddMinutes(-1);

            ValhallaTrafficWriteResult emptyWrite = await writer.WriteAsync(
                [],
                new ValhallaTrafficWriteOptions(storeRoot)
                {
                    GraphTileDirectory = graphRoot,
                    GraphSha256 = graphSha,
                    CreatedAtUtc = created,
                    ExpiresAtUtc = created.AddHours(1),
                    Policy = TrafficSnapshotPolicy.Enabled,
                },
                TestContext.Current.CancellationToken);
            Assert.True(emptyWrite.Succeeded);
            TrafficSnapshotReference emptySnapshot =
                Assert.IsType<TrafficSnapshotReference>(emptyWrite.Snapshot);
            OsmRouteResult emptyResult = await client.CalculateRouteAsync(
                request with { TrafficSnapshot = emptySnapshot },
                TestContext.Current.CancellationToken);
            Assert.True(
                emptyResult.Error is null,
                "Empty traffic snapshot route failed: " + emptyResult.Error +
                Environment.NewLine + logger.Exceptions);
            OsmRouteCandidate emptyCandidate = Assert.Single(emptyResult.Routes);
            Assert.Equal(RouteDurationSource.FreeFlow, emptyCandidate.DurationSource);
            Assert.Equal(0, emptyCandidate.EngineAppliedTrafficDelaySeconds);
            Assert.Equal(emptySnapshot.Version, emptyCandidate.TrafficSnapshotVersion);

            ValhallaTrafficEdgeUpdate[] routeUpdates = updates
                .Where(update => baselineEdges.Contains(
                    update.CanonicalDirectedEdgeId))
                .ToArray();
            Assert.NotEmpty(routeUpdates);

            ValhallaTrafficEdgeUpdate? neutralSourceUpdate = null;
            uint neutralCurrentSpeed = 0;
            foreach (ValhallaTrafficEdgeUpdate candidate in routeUpdates)
            {
                var candidateEdgeId = new GraphId(candidate.CanonicalDirectedEdgeId);
                GraphTile? candidateTile =
                    GraphTile.Create(graphRoot, candidateEdgeId.TileBase());
                Assert.NotNull(candidateTile);
                DirectedEdge candidateEdge =
                    candidateTile.DirectedEdge((int)candidateEdgeId.Id());
                byte nonCurrentSources = 0;
                uint candidateSpeed = candidateTile.GetSpeed(
                    candidateEdge,
                    candidateEdgeId.Id(),
                    (byte)(GraphConstants.DefaultFlowMask &
                           ~GraphConstants.CurrentFlowMask),
                    invariantTime.SecondOfWeek,
                    false,
                    out nonCurrentSources,
                    0);
                if (candidateSpeed > 0 && candidateSpeed <= 126 &&
                    candidateSpeed % 2 == 0)
                {
                    neutralSourceUpdate = candidate;
                    neutralCurrentSpeed = candidateSpeed;
                    break;
                }
            }

            Assert.NotNull(neutralSourceUpdate);
            ValhallaTrafficEdgeUpdate neutralUpdate = neutralSourceUpdate with
            {
                CurrentSpeedKph = neutralCurrentSpeed,
                FreeFlowSpeedKph = neutralCurrentSpeed,
            };
            DateTimeOffset neutralObservation = created.AddSeconds(1);
            ValhallaTrafficWriteResult neutralWrite = await writer.WriteAsync(
                [neutralUpdate],
                new ValhallaTrafficWriteOptions(storeRoot)
                {
                    GraphTileDirectory = graphRoot,
                    GraphSha256 = graphSha,
                    CreatedAtUtc = neutralObservation,
                    ExpiresAtUtc = neutralObservation.AddHours(1),
                    Policy = TrafficSnapshotPolicy.Enabled,
                },
                TestContext.Current.CancellationToken);
            Assert.True(neutralWrite.Succeeded);
            TrafficSnapshotReference neutralSnapshot =
                Assert.IsType<TrafficSnapshotReference>(neutralWrite.Snapshot);
            OsmRouteResult neutralResult = await client.CalculateRouteAsync(
                request with { TrafficSnapshot = neutralSnapshot },
                TestContext.Current.CancellationToken);

            var neutralExpansionCounts = new int[2, 3];
            List<List<PathInfo>> directNeutralPaths;
            await using (EmbeddedValhallaGraphReaderFactory.AsyncLease neutralLease =
                         await factory.AcquireAsync(
                             graphRoot,
                             neutralSnapshot,
                             TestContext.Current.CancellationToken))
            {
                var directCostingOptions = new Costing
                {
                    CostingType = Costing.Type.Auto,
                };
                directCostingOptions.Options.TopSpeed =
                    (int)GraphConstants.MaxAssumedSpeed;
                directCostingOptions.Options.FlowMask =
                    GraphConstants.DefaultFlowMask;
                directCostingOptions.Options.HasFlowMask = true;
                var directCosting = new AutoCost(directCostingOptions);
                var directOrigin = new PathLocation(
                    new Location(
                        new PointLL(7.4160, 43.7305),
                        Location.StopTypeValue.Break)
                    {
                        Radius = 50,
                    })
                {
                    TimeInfo = invariantTime,
                };
                var directDestination = new PathLocation(
                    new Location(
                        new PointLL(7.4246, 43.7384),
                        Location.StopTypeValue.Break)
                    {
                        Radius = 50,
                    })
                {
                    TimeInfo = invariantTime,
                };
                new Search(neutralLease.Reader).DoSearch(
                    [directOrigin, directDestination],
                    directCosting);
                Assert.NotEmpty(directOrigin.Edges);
                Assert.NotEmpty(directDestination.Edges);
                var directModeCosting = new ModeCosting
                {
                    [(int)directCosting.TravelMode()] = directCosting,
                };
                var directAlgorithm = new BidirectionalAStar();
                directAlgorithm.SetTrackExpansion(
                    (_, _, _, _, status, _, _, _, expansionType, _, _) =>
                        neutralExpansionCounts[
                            (int)expansionType,
                            (int)status]++);
                directCosting.SetAllowDestinationOnly(false);
                directCosting.SetPass(0);
                directNeutralPaths = directAlgorithm.GetBestPath(
                    directOrigin,
                    directDestination,
                    neutralLease.Reader,
                    directModeCosting,
                    directCosting.TravelMode(),
                    new Options
                    {
                        DateTimeType = DateTimeType.Invariant,
                        HasDateTimeType = true,
                    });
            }

            ValhallaTrafficEdgeUpdate singleNinetyPercentUpdate = routeUpdates[0] with
            {
                CurrentSpeedKph = Math.Max(
                    2d,
                    Math.Floor((routeUpdates[0].FreeFlowSpeedKph ?? 2d) * 0.9d)),
            };
            DateTimeOffset singleNinetyObservation = created.AddSeconds(2);
            ValhallaTrafficWriteResult singleNinetyWrite = await writer.WriteAsync(
                [singleNinetyPercentUpdate],
                new ValhallaTrafficWriteOptions(storeRoot)
                {
                    GraphTileDirectory = graphRoot,
                    GraphSha256 = graphSha,
                    CreatedAtUtc = singleNinetyObservation,
                    ExpiresAtUtc = singleNinetyObservation.AddHours(1),
                    Policy = TrafficSnapshotPolicy.Enabled,
                },
                TestContext.Current.CancellationToken);
            Assert.True(singleNinetyWrite.Succeeded);
            TrafficSnapshotReference singleNinetySnapshot =
                Assert.IsType<TrafficSnapshotReference>(singleNinetyWrite.Snapshot);
            OsmRouteResult singleNinetyResult = await client.CalculateRouteAsync(
                request with { TrafficSnapshot = singleNinetySnapshot },
                TestContext.Current.CancellationToken);

            ValhallaTrafficEdgeUpdate[] routeNinetyUpdates = routeUpdates
                .Select(static update => update with
                {
                    CurrentSpeedKph = Math.Max(
                        2d,
                        Math.Floor((update.FreeFlowSpeedKph ?? 2d) * 0.9d)),
                })
                .ToArray();
            DateTimeOffset routeNinetyObservation = created.AddSeconds(3);
            ValhallaTrafficWriteResult routeNinetyWrite = await writer.WriteAsync(
                routeNinetyUpdates,
                new ValhallaTrafficWriteOptions(storeRoot)
                {
                    GraphTileDirectory = graphRoot,
                    GraphSha256 = graphSha,
                    CreatedAtUtc = routeNinetyObservation,
                    ExpiresAtUtc = routeNinetyObservation.AddHours(1),
                    Policy = TrafficSnapshotPolicy.Enabled,
                },
                TestContext.Current.CancellationToken);
            Assert.True(routeNinetyWrite.Succeeded);
            TrafficSnapshotReference routeNinetySnapshot =
                Assert.IsType<TrafficSnapshotReference>(routeNinetyWrite.Snapshot);
            OsmRouteResult routeNinetyResult = await client.CalculateRouteAsync(
                request with { TrafficSnapshot = routeNinetySnapshot },
                TestContext.Current.CancellationToken);

            DateTimeOffset routeObservation = created.AddSeconds(4);
            ValhallaTrafficWriteResult routeWrite = await writer.WriteAsync(
                routeUpdates,
                new ValhallaTrafficWriteOptions(storeRoot)
                {
                    GraphTileDirectory = graphRoot,
                    GraphSha256 = graphSha,
                    CreatedAtUtc = routeObservation,
                    ExpiresAtUtc = routeObservation.AddHours(1),
                    Policy = TrafficSnapshotPolicy.Enabled,
                },
                TestContext.Current.CancellationToken);
            Assert.True(routeWrite.Succeeded);
            TrafficSnapshotReference routeSnapshot =
                Assert.IsType<TrafficSnapshotReference>(routeWrite.Snapshot);
            await using (EmbeddedValhallaGraphReaderFactory.AsyncLease routeLease =
                         await factory.AcquireAsync(
                             graphRoot,
                             routeSnapshot,
                             TestContext.Current.CancellationToken))
            {
                foreach (ulong directedEdgeId in baseline.DirectedEdgeIds)
                {
                    var routeEdgeId = new GraphId(directedEdgeId);
                    GraphTile? routeEdgeTileCandidate =
                        routeLease.Reader.GetGraphTile(routeEdgeId.TileBase());
                    Assert.NotNull(routeEdgeTileCandidate);
                    GraphTile routeEdgeTile = routeEdgeTileCandidate;
                    DirectedEdge routeDirectedEdge =
                        routeEdgeTile.DirectedEdge((int)routeEdgeId.Id());
                    TrafficSpeed routeTrafficSpeed =
                        routeEdgeTile.GetTrafficTile().TrafficSpeed(routeEdgeId.Id());
                    Assert.False(
                        routeEdgeTile.IsClosed(routeEdgeId.Id()),
                        $"Baseline edge {directedEdgeId} was unexpectedly closed.");
                    Assert.True(
                        routeTrafficSpeed.SpeedValid(),
                        $"Baseline edge {directedEdgeId} had invalid live speed.");
                    ValhallaTrafficEdgeUpdate expectedRouteUpdate =
                        Assert.Single(
                            routeUpdates,
                            update =>
                                update.CanonicalDirectedEdgeId == directedEdgeId);
                    Assert.True(expectedRouteUpdate.CurrentSpeedKph.HasValue);
                    uint expectedRouteSpeed = checked(
                        (uint)expectedRouteUpdate.CurrentSpeedKph.Value);
                    Assert.Equal(
                        expectedRouteSpeed,
                        routeEdgeTile.GetSpeed(
                            routeDirectedEdge,
                            routeEdgeId.Id(),
                            GraphConstants.CurrentFlowMask,
                            invariantTime.SecondOfWeek));
                }
            }

            OsmRouteResult routeOnlyResult = await client.CalculateRouteAsync(
                request with { TrafficSnapshot = routeSnapshot },
                TestContext.Current.CancellationToken);

            DateTimeOffset allObservation = created.AddSeconds(5);
            ValhallaTrafficWriteResult write = await writer.WriteAsync(
                updates,
                new ValhallaTrafficWriteOptions(storeRoot)
                {
                    GraphTileDirectory = graphRoot,
                    GraphSha256 = graphSha,
                    CreatedAtUtc = allObservation,
                    ExpiresAtUtc = allObservation.AddHours(1),
                    Policy = TrafficSnapshotPolicy.Enabled,
                },
                TestContext.Current.CancellationToken);
            Assert.True(
                write.Succeeded,
                string.Join(
                    Environment.NewLine,
                    write.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            TrafficSnapshotReference snapshot =
                Assert.IsType<TrafficSnapshotReference>(write.Snapshot);

            await using (EmbeddedValhallaGraphReaderFactory.AsyncLease lease =
                         await factory.AcquireAsync(
                             graphRoot,
                             snapshot,
                             TestContext.Current.CancellationToken))
            {
                var routeEdge = new GraphId(baseline.DirectedEdgeIds[0]);
                GraphTile? routeTileCandidate =
                    lease.Reader.GetGraphTile(routeEdge.TileBase());
                Assert.NotNull(routeTileCandidate);
                GraphTile routeTile = routeTileCandidate;
                DirectedEdge edge = routeTile.DirectedEdge((int)routeEdge.Id());
                ValhallaTrafficEdgeUpdate expectedLiveUpdate =
                    Assert.Single(
                        updates,
                        update =>
                            update.CanonicalDirectedEdgeId == routeEdge.Value);
                Assert.True(expectedLiveUpdate.CurrentSpeedKph.HasValue);
                uint expectedLiveSpeed = checked(
                    (uint)expectedLiveUpdate.CurrentSpeedKph.Value);
                Assert.Equal(
                    expectedLiveSpeed,
                    routeTile.GetSpeed(
                        edge,
                        routeEdge.Id(),
                        GraphConstants.CurrentFlowMask,
                        invariantTime.SecondOfWeek));
                Assert.NotEqual(
                    TrafficSpeed.Invalid.RawBits,
                    routeTile.GetTrafficTile().TrafficSpeed(routeEdge.Id()).RawBits);
            }

            OsmRouteResult activeResult = await client.CalculateRouteAsync(
                request with { TrafficSnapshot = snapshot },
                TestContext.Current.CancellationToken);
            Assert.True(
                activeResult.Error is null &&
                neutralResult.Error is null &&
                directNeutralPaths.Count != 0 &&
                singleNinetyResult.Error is null &&
                routeNinetyResult.Error is null &&
                routeOnlyResult.Error is null,
                "All-edge-50=" + (activeResult.Error ?? "ok") + Environment.NewLine +
                "Single-baseline-edge-cost-neutral-current=" +
                (neutralResult.Error ?? "ok") + Environment.NewLine +
                "Single-baseline-edge-90=" + (singleNinetyResult.Error ?? "ok") +
                Environment.NewLine +
                "All-baseline-edges-90=" + (routeNinetyResult.Error ?? "ok") +
                Environment.NewLine +
                "All-baseline-edges-50=" + (routeOnlyResult.Error ?? "ok") +
                Environment.NewLine + logger.Exceptions);
            OsmRouteCandidate neutralCandidate =
                Assert.Single(neutralResult.Routes);
            Assert.NotNull(neutralCandidate.DirectedEdgeIds);
            Assert.Equal(
                baseline.DirectedEdgeIds!.ToArray(),
                neutralCandidate.DirectedEdgeIds.ToArray());
            Assert.Equal(
                baseline.DurationSeconds,
                neutralCandidate.DurationSeconds);
            Assert.Equal(
                RouteDurationSource.LiveTraffic,
                neutralCandidate.DurationSource);
            Assert.Equal(
                neutralSnapshot.Version,
                neutralCandidate.TrafficSnapshotVersion);
            Assert.Equal(
                0,
                neutralCandidate.EngineAppliedTrafficDelaySeconds);

            Assert.NotEmpty(singleNinetyResult.Routes);
            Assert.NotEmpty(routeNinetyResult.Routes);
            Assert.NotEmpty(routeOnlyResult.Routes);
            OsmRouteCandidate active = Assert.Single(activeResult.Routes);
            Assert.NotNull(active.DirectedEdgeIds);
            IReadOnlyList<ulong> activeDirectedEdgeIds = active.DirectedEdgeIds;
            int recostedLiveDurationSeconds;
            int recostedNonCurrentDurationSeconds;
            await using (EmbeddedValhallaGraphReaderFactory.AsyncLease recostLease =
                         await factory.AcquireAsync(
                             graphRoot,
                             snapshot,
                             TestContext.Current.CancellationToken))
            {
                var recostOptions = new Costing
                {
                    CostingType = Costing.Type.Auto,
                };
                recostOptions.Options.TopSpeed =
                    (int)GraphConstants.MaxAssumedSpeed;
                recostOptions.Options.FlowMask =
                    GraphConstants.DefaultFlowMask;
                recostOptions.Options.HasFlowMask = true;
                var recostCosting = new AutoCost(recostOptions);
                var recostOrigin = new PathLocation(
                    new Location(
                        new PointLL(7.4160, 43.7305),
                        Location.StopTypeValue.Break)
                    {
                        Radius = 50,
                    })
                {
                    TimeInfo = invariantTime,
                };
                var recostDestination = new PathLocation(
                    new Location(
                        new PointLL(7.4246, 43.7384),
                        Location.StopTypeValue.Break)
                    {
                        Radius = 50,
                    })
                {
                    TimeInfo = invariantTime,
                };
                new Search(recostLease.Reader).DoSearch(
                    [recostOrigin, recostDestination],
                    recostCosting);
                var recostEdgeIds = activeDirectedEdgeIds
                    .Select(static value => new GraphId(value))
                    .ToArray();
                float sourcePct =
                    Recost.FindPercentAlong(recostOrigin, recostEdgeIds[0]);
                float targetPct =
                    Recost.FindPercentAlong(recostDestination, recostEdgeIds[^1]);
                var recostLabels = new List<PathEdgeLabel>(recostEdgeIds.Length);
                int recostIndex = 0;
                GraphId NextRecostEdge() =>
                    recostIndex < recostEdgeIds.Length
                        ? recostEdgeIds[recostIndex++]
                        : GraphId.Invalid;
                Recost.Forward(
                    recostLease.Reader,
                    recostCosting,
                    NextRecostEdge,
                    recostLabels.Add,
                    sourcePct,
                    targetPct,
                    invariantTime,
                    invariant: true,
                    ignoreAccess: false);
                Assert.Equal(recostEdgeIds.Length, recostLabels.Count);
                recostedLiveDurationSeconds = (int)Math.Round(
                    recostLabels[^1].Cost().Secs,
                    MidpointRounding.AwayFromZero);

                var nonCurrentRecostOptions = new Costing
                {
                    CostingType = Costing.Type.Auto,
                };
                nonCurrentRecostOptions.Options.TopSpeed =
                    (int)GraphConstants.MaxAssumedSpeed;
                nonCurrentRecostOptions.Options.FlowMask =
                    (byte)(GraphConstants.DefaultFlowMask &
                           ~GraphConstants.CurrentFlowMask);
                nonCurrentRecostOptions.Options.HasFlowMask = true;
                var nonCurrentRecostCosting =
                    new AutoCost(nonCurrentRecostOptions);
                var nonCurrentRecostLabels =
                    new List<PathEdgeLabel>(recostEdgeIds.Length);
                int nonCurrentRecostIndex = 0;
                GraphId NextNonCurrentRecostEdge() =>
                    nonCurrentRecostIndex < recostEdgeIds.Length
                        ? recostEdgeIds[nonCurrentRecostIndex++]
                        : GraphId.Invalid;
                Recost.Forward(
                    recostLease.Reader,
                    nonCurrentRecostCosting,
                    NextNonCurrentRecostEdge,
                    nonCurrentRecostLabels.Add,
                    sourcePct,
                    targetPct,
                    invariantTime,
                    invariant: true,
                    ignoreAccess: false);
                Assert.Equal(
                    recostEdgeIds.Length,
                    nonCurrentRecostLabels.Count);
                recostedNonCurrentDurationSeconds = (int)Math.Round(
                    nonCurrentRecostLabels[^1].Cost().Secs,
                    MidpointRounding.AwayFromZero);
            }

            Assert.True(
                recostedLiveDurationSeconds > recostedNonCurrentDurationSeconds,
                $"Access-valid live recost duration={recostedLiveDurationSeconds}; " +
                $"same-path non-current recost duration={recostedNonCurrentDurationSeconds}; " +
                $"baseline route duration={baseline.DurationSeconds}.");
            string provenanceReceipt =
                $"neutral baseline={baseline.DurationSeconds}; " +
                $"neutral surfaced={neutralCandidate.DurationSeconds}; " +
                $"neutral delay={neutralCandidate.EngineAppliedTrafficDelaySeconds}; " +
                $"slower surfaced={active.DurationSeconds}; " +
                $"slower live recost={recostedLiveDurationSeconds}; " +
                $"slower same-path non-current recost={recostedNonCurrentDurationSeconds}; " +
                $"slower engine delay={active.EngineAppliedTrafficDelaySeconds}.";
            Assert.True(
                active.DurationSeconds == recostedLiveDurationSeconds,
                "Surfaced ETA must be the authoritative live Recost duration. " +
                provenanceReceipt);
            int expectedEngineAppliedTrafficDelaySeconds =
                recostedLiveDurationSeconds - recostedNonCurrentDurationSeconds;
            Assert.True(
                active.EngineAppliedTrafficDelaySeconds ==
                expectedEngineAppliedTrafficDelaySeconds,
                "Engine-applied delay must be the live-vs-non-current Recost delta " +
                "for the exact active edge sequence, never post-hoc edge arithmetic. " +
                provenanceReceipt);
            Assert.True(
                active.DurationSeconds > baseline.DurationSeconds,
                "Live traffic must increase this route's duration. " +
                provenanceReceipt);
            Assert.Equal(RouteDurationSource.LiveTraffic, active.DurationSource);
            Assert.Equal(snapshot.Version, active.TrafficSnapshotVersion);
            Assert.True(active.EngineAppliedTrafficDelaySeconds > 0);

            var metrics = new RouteCandidateMetrics(
                "valhalla",
                0,
                active.DistanceMeters,
                active.DurationSeconds,
                TrafficDelaySeconds: active.EngineAppliedTrafficDelaySeconds,
                DirectedEdgeIds: active.DirectedEdgeIds,
                DurationSource: active.DurationSource);
            Assert.Equal(
                active.DurationSeconds,
                TrafficAwareRerouteRanker.AdjustedEtaSeconds(
                    metrics,
                    TrafficPolicy.Enabled));

            var closureEdge = new GraphId(
                baseline.DirectedEdgeIds![baseline.DirectedEdgeIds.Count / 2]);
            GraphTile? closureSourceTile =
                GraphTile.Create(graphRoot, closureEdge.TileBase());
            Assert.NotNull(closureSourceTile);
            DirectedEdge closureSource =
                closureSourceTile.DirectedEdge((int)closureEdge.Id());
            DateTimeOffset closureObservation = created.AddSeconds(6);
            ValhallaTrafficWriteResult closureWrite = await writer.WriteAsync(
                [
                    new ValhallaTrafficEdgeUpdate(
                        closureEdge.TileBase().Value,
                        closureEdge.Id(),
                        TrafficDirection.Forward,
                        null,
                        closureSource.Speed,
                        null,
                        true,
                        false,
                        true,
                        1d,
                        "closure-only",
                        "behavior",
                        closureEdge.Value),
                ],
                new ValhallaTrafficWriteOptions(storeRoot)
                {
                    GraphTileDirectory = graphRoot,
                    GraphSha256 = graphSha,
                    CreatedAtUtc = closureObservation,
                    ExpiresAtUtc = closureObservation.AddHours(1),
                    Policy = TrafficSnapshotPolicy.ClosureOnly,
                },
                TestContext.Current.CancellationToken);
            Assert.True(
                closureWrite.Succeeded,
                string.Join(
                    Environment.NewLine,
                    closureWrite.Diagnostics.Select(
                        static diagnostic => diagnostic.Message)));
            TrafficSnapshotReference closureSnapshot =
                Assert.IsType<TrafficSnapshotReference>(closureWrite.Snapshot);

            await using (EmbeddedValhallaGraphReaderFactory.AsyncLease closureLease =
                         await factory.AcquireAsync(
                             graphRoot,
                             closureSnapshot,
                             TestContext.Current.CancellationToken))
            {
                GraphTile? closureTile =
                    closureLease.Reader.GetGraphTile(closureEdge.TileBase());
                Assert.NotNull(closureTile);
                Assert.True(closureTile.IsClosed(closureEdge.Id()));
                var unaffectedEdge = new GraphId(
                    baseline.DirectedEdgeIds.First(
                        edgeId => edgeId != closureEdge.Value));
                GraphTile? unaffectedTile =
                    closureLease.Reader.GetGraphTile(unaffectedEdge.TileBase());
                Assert.NotNull(unaffectedTile);
                Assert.Equal(
                    TrafficSpeed.Invalid.RawBits,
                    unaffectedTile.GetTrafficTile()
                        .TrafficSpeed(unaffectedEdge.Id()).RawBits);
            }

            OsmRouteResult closureOnlyResult = await client.CalculateRouteAsync(
                request with { TrafficSnapshot = closureSnapshot },
                TestContext.Current.CancellationToken);
            Assert.True(
                closureOnlyResult.Error is null,
                "Closure-only route failed: " + closureOnlyResult.Error +
                Environment.NewLine + logger.Exceptions);
            OsmRouteCandidate closureOnly = Assert.Single(closureOnlyResult.Routes);
            Assert.NotNull(closureOnly.DirectedEdgeIds);
            Assert.DoesNotContain(closureEdge.Value, closureOnly.DirectedEdgeIds);
            Assert.Equal(0, closureOnly.EngineAppliedTrafficDelaySeconds);
            Assert.Equal(RouteDurationSource.FreeFlow, closureOnly.DurationSource);
            Assert.Equal(closureSnapshot.Version, closureOnly.TrafficSnapshotVersion);
            var closureMetrics = new RouteCandidateMetrics(
                "valhalla",
                0,
                closureOnly.DistanceMeters,
                closureOnly.DurationSeconds,
                TrafficDelaySeconds: closureOnly.EngineAppliedTrafficDelaySeconds,
                DirectedEdgeIds: closureOnly.DirectedEdgeIds,
                DurationSource: closureOnly.DurationSource);
            Assert.Equal(
                closureOnly.DurationSeconds,
                TrafficAwareRerouteRanker.AdjustedEtaSeconds(
                    closureMetrics,
                    TrafficPolicy.Disabled));
        }
        finally
        {
            if (Directory.Exists(storeRoot))
            {
                Directory.Delete(storeRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WritePairAsync_SecondGenerationFailure_LeavesPreviousPairAtomicallyCurrent()
    {
        string graphRoot = FindMonacoFixture();
        (GraphId tileId, _, uint edgeIndex, DirectedEdge edge) = FindUsableEdge(graphRoot);
        string storeRoot = NewTempDirectory();
        try
        {
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            ValhallaTrafficWriteOptions enabledOptions = new(storeRoot)
            {
                GraphTileDirectory = graphRoot,
                GraphSha256 = graphSha,
                CreatedAtUtc = created,
                ExpiresAtUtc = created.AddMinutes(20),
                Policy = TrafficSnapshotPolicy.Enabled,
            };
            ValhallaTrafficWriteOptions closureOptions = enabledOptions with
            {
                Policy = TrafficSnapshotPolicy.ClosureOnly,
            };
            ValhallaTrafficEdgeUpdate firstUpdate = SpeedUpdate(
                tileId,
                edgeIndex,
                edge,
                Math.Max(2d, Math.Floor(edge.Speed / 2d)),
                "pair-a");

            ValhallaTrafficSnapshotPairWriteResult first = await writer.WritePairAsync(
                new[] { firstUpdate },
                enabledOptions,
                Array.Empty<ValhallaTrafficEdgeUpdate>(),
                closureOptions,
                TestContext.Current.CancellationToken);
            Assert.True(first.Succeeded);
            TrafficSnapshotReference firstEnabled =
                Assert.IsType<TrafficSnapshotReference>(first.Enabled.Snapshot);
            TrafficSnapshotReference firstClosure =
                Assert.IsType<TrafficSnapshotReference>(first.ClosureOnly.Snapshot);

            ValhallaTrafficEdgeUpdate secondUpdate = SpeedUpdate(
                tileId,
                edgeIndex,
                edge,
                Math.Max(2d, Math.Floor(edge.Speed / 3d)),
                "pair-b");
            ValhallaTrafficSnapshotPairWriteResult failed = await writer.WritePairAsync(
                new[] { secondUpdate },
                enabledOptions with { CreatedAtUtc = created.AddSeconds(1) },
                Array.Empty<ValhallaTrafficEdgeUpdate>(),
                closureOptions with
                {
                    CreatedAtUtc = created.AddSeconds(1),
                    GraphSha256 = new string('A', 64),
                },
                TestContext.Current.CancellationToken);
            Assert.False(failed.Succeeded);

            TrafficSnapshotReference currentEnabled = Assert.IsType<TrafficSnapshotReference>(
                await store.GetCurrentAsync(
                    graphSha,
                    TrafficSnapshotPolicy.Enabled,
                    TestContext.Current.CancellationToken));
            TrafficSnapshotReference currentClosure = Assert.IsType<TrafficSnapshotReference>(
                await store.GetCurrentAsync(
                    graphSha,
                    TrafficSnapshotPolicy.ClosureOnly,
                    TestContext.Current.CancellationToken));
            Assert.Equal(firstEnabled.Version, currentEnabled.Version);
            Assert.Equal(firstClosure.Version, currentClosure.Version);

            await store.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.True(Directory.Exists(firstEnabled.GenerationDirectory));
            Assert.True(Directory.Exists(firstClosure.GenerationDirectory));
        }
        finally
        {
            if (Directory.Exists(storeRoot))
            {
                Directory.Delete(storeRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CalculateRouteAsync_InvalidSnapshots_ReturnTypedFailureCodes()
    {
        string graphRoot = FindMonacoFixture();
        string storeRoot = NewTempDirectory();
        try
        {
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TrafficSnapshotReference Snapshot(
                char versionCharacter,
                string targetGraphSha,
                DateTimeOffset expiresAtUtc) =>
                new(
                    targetGraphSha,
                    new string(versionCharacter, 64),
                    Path.Combine(
                        storeRoot,
                        "graphs",
                        targetGraphSha.ToUpperInvariant(),
                        "generations",
                        new string(versionCharacter, 64)),
                    now.AddMinutes(-1),
                    expiresAtUtc,
                    TrafficSnapshotPolicy.Enabled);

            TrafficSnapshotReference missing = Snapshot('1', graphSha, now.AddMinutes(20));
            TrafficSnapshotReference unreadable = Snapshot('2', graphSha, now.AddMinutes(20));
            Directory.CreateDirectory(unreadable.GenerationDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(unreadable.GenerationDirectory, "manifest.json"),
                "{not-json",
                TestContext.Current.CancellationToken);

            TrafficSnapshotReference incomplete = Snapshot('3', graphSha, now.AddMinutes(20));
            Directory.CreateDirectory(incomplete.GenerationDirectory);
            var incompleteManifest = new TrafficSnapshotManifest(
                graphSha,
                incomplete.Version,
                TrafficSnapshotPolicy.Enabled,
                now.AddMinutes(-1),
                now.AddMinutes(20),
                false,
                Array.Empty<TrafficSnapshotTileManifest>());
            await File.WriteAllTextAsync(
                Path.Combine(incomplete.GenerationDirectory, "manifest.json"),
                JsonSerializer.Serialize(incompleteManifest),
                TestContext.Current.CancellationToken);

            TrafficSnapshotReference expired = Snapshot('4', graphSha, now.AddSeconds(-1));
            string mismatchedGraphSha =
                (graphSha[0] == 'A' ? "B" : "A") + graphSha[1..];
            TrafficSnapshotReference graphMismatch =
                Snapshot('5', mismatchedGraphSha, now.AddMinutes(20));

            var store = new TrafficSnapshotStore(storeRoot);
            await using var factory = new EmbeddedValhallaGraphReaderFactory(store);
            var client = new EmbeddedValhallaRoutingClient(
                factory,
                new FixedTileDirectoryProvider(graphRoot),
                NullLogger<EmbeddedValhallaRoutingClient>.Instance);
            var failures = new[]
            {
                (Snapshot: missing, Code: TrafficSnapshotFailureCode.Missing),
                (Snapshot: unreadable, Code: TrafficSnapshotFailureCode.Unreadable),
                (Snapshot: incomplete, Code: TrafficSnapshotFailureCode.Incomplete),
                (Snapshot: expired, Code: TrafficSnapshotFailureCode.Expired),
                (Snapshot: graphMismatch, Code: TrafficSnapshotFailureCode.GraphMismatch),
            };

            foreach ((TrafficSnapshotReference snapshot, TrafficSnapshotFailureCode code) in failures)
            {
                var request = new OsmRouteRequest(
                    null,
                    new GeoCoordinate(43.7305, 7.4160),
                    new GeoCoordinate(43.7384, 7.4246),
                    ComputeAlternativeRoutes: false)
                {
                    TrafficSnapshot = snapshot,
                    DepartureTimeUtc = now,
                };
                OsmRouteResult result = await client.CalculateRouteAsync(
                    request,
                    TestContext.Current.CancellationToken);

                Assert.Empty(result.Routes);
                TrafficSnapshotFailure failure =
                    Assert.IsType<TrafficSnapshotFailure>(result.TrafficSnapshotFailure);
                Assert.Equal(code, failure.Code);
                Assert.Equal(snapshot.Version, failure.SnapshotVersion);
                Assert.Equal("traffic_snapshot_invalid", result.Error);
            }
        }
        finally
        {
            if (Directory.Exists(storeRoot))
            {
                Directory.Delete(storeRoot, recursive: true);
            }
        }
    }

    private static ValhallaTrafficEdgeUpdate SpeedUpdate(
        GraphId tileId,
        uint edgeIndex,
        DirectedEdge edge,
        double currentSpeed,
        string sourceEventId) =>
        new(
            tileId.Value,
            edgeIndex,
            TrafficDirection.Forward,
            currentSpeed,
            edge.Speed,
            null,
            false,
            false,
            true,
            1d,
            sourceEventId,
            "behavior",
            new GraphId(tileId.Tileid(), tileId.Level(), edgeIndex).Value);

    private static (GraphId TileId, GraphTile Tile, uint EdgeIndex, DirectedEdge Edge) FindUsableEdge(
        string graphRoot)
    {
        foreach (string file in Directory.EnumerateFiles(graphRoot, "*.gph", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            GraphId tileId = ParseGraphId(graphRoot, file);
            GraphTile? tile = GraphTile.Create(graphRoot, tileId);
            if (tile is null)
            {
                continue;
            }

            for (uint index = 0; index < tile.DirectedEdgeCount(); index++)
            {
                DirectedEdge edge = tile.DirectedEdge((int)index);
                if (edge.Length > 0 && edge.Speed >= 6)
                {
                    return (tileId, tile, index, edge);
                }
            }
        }

        throw new Xunit.Sdk.XunitException("Tracked graph fixture contains no usable directed edge.");
    }

    private static string FindMonacoFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "artifacts", "valhalla-monaco-tiles");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Tracked Monaco graph fixture was not found.");
    }

    private static GraphId ParseGraphId(string root, string file)
    {
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        string[] parts = relative.Split('/');
        byte level = byte.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        string digits = string.Concat(
            parts.Skip(1).Select(static part => Path.GetFileNameWithoutExtension(part)));
        return new GraphId(
            uint.Parse(digits, System.Globalization.CultureInfo.InvariantCulture),
            level,
            0);
    }

    private sealed class RecordingRoutingLogger :
        Microsoft.Extensions.Logging.ILogger<EmbeddedValhallaRoutingClient>
    {
        private readonly List<Exception> _exceptions = [];

        public string Exceptions => string.Join(
            Environment.NewLine,
            _exceptions.Select(static exception => exception.ToString()));

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                _exceptions.Add(exception);
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            throw new Xunit.Sdk.XunitException(
                "Explicit DepartureTimeUtc must be used instead of the ambient clock.");
    }

    private sealed class FixedTileDirectoryProvider(string path) : IOsmTileDirectoryProvider
    {
        public Task<string?> GetTileDirectoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(path);
        }
    }

    private static string NewTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Tests",
            "routing-traffic-provenance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
