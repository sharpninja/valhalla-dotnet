using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

internal static class TrafficRuntimeExactContractBehavior
{
    public static async Task WriteAsync_RejectsGraphTileAndEdgeIdentityMismatches()
    {
        string graphRoot = FindMonacoFixture();
        (GraphId tileId, GraphTile tile) = FindTile(graphRoot, minimumDirectedEdges: 1);
        string storeRoot = NewTempDirectory();
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            ValhallaTrafficWriteOptions options = WriteOptions(
                storeRoot,
                graphRoot,
                graphSha,
                created,
                created.AddMinutes(20));

            TrafficSnapshotReference baseline = AssertSucceeded(await writer.WriteAsync(
                [SpeedUpdate(tileId, 0, 50, "baseline", "preferred", confidence: 1d)],
                options,
                TestContext.Current.CancellationToken));

            await AssertGraphMismatchAsync(
                [SpeedUpdate(tileId, 0, 60, "wrong-sha", "preferred", confidence: 1d)],
                options with { GraphSha256 = new string('A', 64) });

            GraphId missingTile = FindMissingTile(graphRoot, checked((byte)tileId.Level()));
            await AssertGraphMismatchAsync(
                [SpeedUpdate(missingTile, 0, 60, "wrong-tile", "preferred", confidence: 1d)],
                options);

            await AssertGraphMismatchAsync(
                [SpeedUpdate(
                    tileId,
                    tile.DirectedEdgeCount(),
                    60,
                    "wrong-edge-count",
                    "preferred",
                    confidence: 1d)],
                options);

            async Task AssertGraphMismatchAsync(
                IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
                ValhallaTrafficWriteOptions invalidOptions)
            {
                ValhallaTrafficWriteResult result = await writer.WriteAsync(
                    updates,
                    invalidOptions,
                    TestContext.Current.CancellationToken);
                Assert.False(result.Succeeded);
                Assert.Null(result.Snapshot);
                Assert.Contains(
                    result.Diagnostics,
                    diagnostic => string.Equals(
                        diagnostic.Code,
                        TrafficSnapshotFailureCode.GraphMismatch.ToString(),
                        StringComparison.Ordinal));

                TrafficSnapshotReference current = Assert.IsType<TrafficSnapshotReference>(
                    await store.GetCurrentAsync(
                        graphSha,
                        TrafficSnapshotPolicy.Enabled,
                        TestContext.Current.CancellationToken));
                Assert.Equal(baseline.Version, current.Version);
            }
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    public static async Task PublishAsync_PartialAndCancelledWritesNeverBecomeCurrent()
    {
        string graphRoot = FindMonacoFixture();
        (GraphId tileId, _) = FindTile(graphRoot, minimumDirectedEdges: 1);
        string storeRoot = NewTempDirectory();
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            ValhallaTrafficWriteOptions options = WriteOptions(
                storeRoot,
                graphRoot,
                graphSha,
                created,
                created.AddMinutes(20));

            TrafficSnapshotReference baseline = AssertSucceeded(await writer.WriteAsync(
                [SpeedUpdate(tileId, 0, 50, "baseline", "provider", confidence: 1d)],
                options,
                TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(baseline.GenerationDirectory, "manifest.json")));

            string partial = store.CreateStagingDirectory();
            var incompleteManifest = new TrafficSnapshotManifest(
                graphSha,
                string.Empty,
                TrafficSnapshotPolicy.Enabled,
                created.AddSeconds(1),
                created.AddMinutes(20),
                false,
                [
                    new TrafficSnapshotTileManifest(
                        tileId.Value,
                        1,
                        GraphTile.FileSuffix(tileId),
                        TrafficTile.HeaderSize + TrafficTile.SpeedSize,
                        new string('0', 64)),
                ]);
            TrafficSnapshotStoreException partialFailure = await Assert.ThrowsAsync<TrafficSnapshotStoreException>(
                () => store.PublishAsync(
                    partial,
                    incompleteManifest,
                    TestContext.Current.CancellationToken));
            Assert.Equal(TrafficSnapshotFailureCode.Incomplete, partialFailure.Code);
            await AssertCurrentVersionAsync(baseline.Version);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => writer.WriteAsync(
                    [SpeedUpdate(tileId, 0, 80, "cancelled", "provider", confidence: 1d)],
                    options with { CreatedAtUtc = created.AddSeconds(2) },
                    cancelled.Token));
            await AssertCurrentVersionAsync(baseline.Version);

            DateTimeOffset refreshedObservation = created.AddSeconds(3);
            TrafficSnapshotReference refreshed = AssertSucceeded(await writer.WriteAsync(
                [SpeedUpdate(tileId, 0, 50, "baseline", "provider", confidence: 1d)],
                options with { CreatedAtUtc = refreshedObservation },
                TestContext.Current.CancellationToken));
            Assert.NotEqual(baseline.Version, refreshed.Version);
            Assert.NotEqual(
                Path.GetFullPath(baseline.GenerationDirectory),
                Path.GetFullPath(refreshed.GenerationDirectory));
            await AssertCurrentVersionAsync(refreshed.Version);
            string refreshedTilePath = Path.Combine(
                refreshed.GenerationDirectory,
                GraphTile.FileSuffix(tileId));
            var refreshedTile = new TrafficTile(await File.ReadAllBytesAsync(
                refreshedTilePath,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                (ulong)refreshedObservation.ToUnixTimeSeconds(),
                Assert.IsType<TrafficTileHeader>(refreshedTile.Header).LastUpdate);

            async Task AssertCurrentVersionAsync(string expected)
            {
                TrafficSnapshotReference current = Assert.IsType<TrafficSnapshotReference>(
                    await store.GetCurrentAsync(
                        graphSha,
                        TrafficSnapshotPolicy.Enabled,
                        TestContext.Current.CancellationToken));
                Assert.Equal(expected, current.Version);
                Assert.True(File.Exists(Path.Combine(current.GenerationDirectory, "manifest.json")));
            }
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    public static async Task CleanupAsync_RetainsThreePinsLeasesAndRemovesAbandonedStaging()
    {
        string graphRoot = FindMonacoFixture();
        (GraphId tileId, _) = FindTile(graphRoot, minimumDirectedEdges: 1);
        string storeRoot = NewTempDirectory();
        try
        {
            var store = new TrafficSnapshotStore(storeRoot, maxRetainedGenerations: 3);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            var snapshots = new List<TrafficSnapshotReference>();

            for (int index = 0; index < 4; index++)
            {
                TrafficSnapshotReference snapshot = AssertSucceeded(await writer.WriteAsync(
                    [
                        SpeedUpdate(
                            tileId,
                            0,
                            30 + (index * 20),
                            "generation-" + index,
                            "provider",
                            confidence: 1d),
                    ],
                    WriteOptions(
                        storeRoot,
                        graphRoot,
                        graphSha,
                        created.AddSeconds(index),
                        created.AddMinutes(20)),
                    TestContext.Current.CancellationToken));
                snapshots.Add(snapshot);

                if (index == 0)
                {
                    break;
                }
            }

            await using ITrafficSnapshotLease pin = await store.AcquireAsync(
                snapshots[0],
                TestContext.Current.CancellationToken);
            for (int index = 1; index < 4; index++)
            {
                TrafficSnapshotReference snapshot = AssertSucceeded(await writer.WriteAsync(
                    [
                        SpeedUpdate(
                            tileId,
                            0,
                            30 + (index * 20),
                            "generation-" + index,
                            "provider",
                            confidence: 1d),
                    ],
                    WriteOptions(
                        storeRoot,
                        graphRoot,
                        graphSha,
                        created.AddSeconds(index),
                        created.AddMinutes(20)),
                    TestContext.Current.CancellationToken));
                snapshots.Add(snapshot);
            }

            Assert.True(Directory.Exists(snapshots[0].GenerationDirectory));
            string generationsRoot = Path.GetDirectoryName(snapshots[^1].GenerationDirectory)!;
            Assert.Equal(4, Directory.EnumerateDirectories(generationsRoot).Count());

            string abandoned = Path.Combine(storeRoot, ".tmp-abandoned-startup");
            Directory.CreateDirectory(abandoned);
            await File.WriteAllTextAsync(
                Path.Combine(abandoned, "partial"),
                "not a generation",
                TestContext.Current.CancellationToken);

            var restartedStore = new TrafficSnapshotStore(storeRoot, maxRetainedGenerations: 3);
            await restartedStore.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(abandoned));
            Assert.True(Directory.Exists(snapshots[0].GenerationDirectory));

            await pin.DisposeAsync();
            await restartedStore.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(snapshots[0].GenerationDirectory));
            Assert.True(Directory.Exists(snapshots[^1].GenerationDirectory));
            Assert.True(Directory.EnumerateDirectories(generationsRoot).Count() <= 3);
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    public static async Task WriteAsync_GroupsClampsQuantizesAndPreservesDeterministicPrecedence()
    {
        string graphRoot = FindMonacoFixture();
        (GraphId tileId, _) = FindTile(graphRoot, minimumDirectedEdges: 6);
        string storeRoot = NewTempDirectory();
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            ulong edge0 = new GraphId(tileId.Tileid(), tileId.Level(), 0).Value;
            ulong edge3 = new GraphId(tileId.Tileid(), tileId.Level(), 3).Value;
            ulong edge4 = new GraphId(tileId.Tileid(), tileId.Level(), 4).Value;

            ValhallaTrafficWriteResult result = await writer.WriteAsync(
                [
                    SpeedUpdate(tileId, 0, 51, "source-z", "z-provider", confidence: 0.9d),
                    SpeedUpdate(tileId, 0, 73, "source-z", "a-provider", confidence: 0.9d),
                    SpeedUpdate(tileId, 0, 91, "source-a", "a-provider", confidence: 0.9d),
                    new ValhallaTrafficEdgeUpdate(
                        tileId.Value,
                        0,
                        TrafficDirection.Forward,
                        null,
                        70,
                        60,
                        false,
                        true,
                        true,
                        0.1d,
                        "incident-sibling",
                        "incident-provider",
                        edge0),
                    SpeedUpdate(tileId, 1, -50, "low-clamp", "provider", confidence: 1d),
                    SpeedUpdate(tileId, 2, 1000, "high-clamp", "provider", confidence: 1d),
                    new ValhallaTrafficEdgeUpdate(
                        tileId.Value,
                        3,
                        TrafficDirection.Forward,
                        null,
                        null,
                        null,
                        true,
                        false,
                        true,
                        1d,
                        "closure",
                        "closure-provider",
                        edge3),
                    new ValhallaTrafficEdgeUpdate(
                        tileId.Value,
                        3,
                        TrafficDirection.Forward,
                        null,
                        70,
                        30,
                        false,
                        true,
                        true,
                        0.1d,
                        "closure-incident-sibling",
                        "incident-provider",
                        edge3),
                    SpeedUpdate(
                        tileId,
                        4,
                        55,
                        "canonical-storage",
                        "canonical-provider",
                        confidence: 1d),
                    new ValhallaTrafficEdgeUpdate(
                        tileId.Value,
                        5,
                        TrafficDirection.Forward,
                        null,
                        70,
                        30,
                        false,
                        true,
                        true,
                        0.1d,
                        "alternate-storage",
                        "incident-provider",
                        edge4),
                ],
                WriteOptions(
                    storeRoot,
                    graphRoot,
                    graphSha,
                    created,
                    created.AddMinutes(20)),
                TestContext.Current.CancellationToken);

            TrafficSnapshotReference snapshot = AssertSucceeded(result);
            Assert.Equal(5, result.UpdateCount);
            string path = Path.Combine(snapshot.GenerationDirectory, GraphTile.FileSuffix(tileId));
            var traffic = new TrafficTile(await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken));

            TrafficSpeed composedSpeed = traffic.TrafficSpeed(0);
            Assert.Equal(92u, composedSpeed.GetOverallSpeed());
            Assert.True(composedSpeed.HasIncidents);
            Assert.Equal(2u, traffic.TrafficSpeed(1).GetOverallSpeed());
            Assert.Equal(252u, traffic.TrafficSpeed(2).GetOverallSpeed());
            TrafficSpeed composedClosure = traffic.TrafficSpeed(3);
            Assert.True(composedClosure.Closed());
            Assert.True(composedClosure.HasIncidents);
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    public static async Task AcquireAsync_CancellationPublicationAndDisposalRemainLeaseSafe()
    {
        string graphRoot = FindMonacoFixture();
        (GraphId tileId, _) = FindTile(graphRoot, minimumDirectedEdges: 1);
        string storeRoot = NewTempDirectory();
        try
        {
            var store = new TrafficSnapshotStore(storeRoot, maxRetainedGenerations: 1);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graphRoot,
                TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            TrafficSnapshotReference firstSnapshot = AssertSucceeded(await writer.WriteAsync(
                [SpeedUpdate(tileId, 0, 40, "first", "provider", confidence: 1d)],
                WriteOptions(
                    storeRoot,
                    graphRoot,
                    graphSha,
                    created,
                    created.AddMinutes(20)),
                TestContext.Current.CancellationToken));

            var factory = new EmbeddedValhallaGraphReaderFactory(store);
            EmbeddedValhallaGraphReaderFactory.AsyncLease first = await factory.AcquireAsync(
                graphRoot,
                firstSnapshot,
                TestContext.Current.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Task cancelledAcquire = Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await factory.AcquireAsync(graphRoot, firstSnapshot, cancelled.Token));

            Task<ValhallaTrafficWriteResult> publish = writer.WriteAsync(
                [SpeedUpdate(tileId, 0, 90, "second", "provider", confidence: 1d)],
                WriteOptions(
                    storeRoot,
                    graphRoot,
                    graphSha,
                    created.AddSeconds(1),
                    created.AddMinutes(20)),
                TestContext.Current.CancellationToken);
            await Task.WhenAll(cancelledAcquire, publish);

            TrafficSnapshotReference secondSnapshot = AssertSucceeded(await publish);
            EmbeddedValhallaGraphReaderFactory.AsyncLease second = await factory.AcquireAsync(
                graphRoot,
                secondSnapshot,
                TestContext.Current.CancellationToken);
            await factory.DisposeAsync();

            Assert.Equal(
                40u,
                first.Reader.GetGraphTile(tileId)!.GetTrafficTile().TrafficSpeed(0).GetOverallSpeed());
            Assert.Equal(
                90u,
                second.Reader.GetGraphTile(tileId)!.GetTrafficTile().TrafficSpeed(0).GetOverallSpeed());
            Assert.True(Directory.Exists(firstSnapshot.GenerationDirectory));
            Assert.True(Directory.Exists(secondSnapshot.GenerationDirectory));

            await first.DisposeAsync();
            await store.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(firstSnapshot.GenerationDirectory));
            Assert.True(Directory.Exists(secondSnapshot.GenerationDirectory));
            Assert.Equal(
                90u,
                second.Reader.GetGraphTile(tileId)!.GetTrafficTile().TrafficSpeed(0).GetOverallSpeed());

            await second.DisposeAsync();
            await second.DisposeAsync();
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    public static void Route_WithTrafficSnapshot_PassesInvariantTimeIntoAStarCosting()
    {
        string graphRoot = FindMonacoFixture();
        var reader = new GraphReader(new GraphReader.Config { TileDir = graphRoot });
        var costingOptions = new Costing { CostingType = Costing.Type.Auto };
        costingOptions.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        var costing = new RecordingAutoCost(costingOptions);
        (PointLL originPoint, PointLL destinationPoint) = PickTwoOnRoadPoints(graphRoot, costing);

        var origin = new PathLocation(new Location(originPoint) { Radius = 50 });
        var destination = new PathLocation(new Location(destinationPoint) { Radius = 50 });
        var search = new Search(reader);
        search.DoSearch([origin, destination], costing);
        Assert.NotEmpty(origin.Edges);
        Assert.NotEmpty(destination.Edges);

        DateTimeOffset departure = new(2026, 7, 20, 0, 0, 1, TimeSpan.Zero);
        TimeInfo invariant = InvariantTrafficTime.Create(departure);
        origin.TimeInfo = invariant;
        destination.TimeInfo = invariant;

        var modeCosting = new ModeCosting();
        modeCosting[(int)costing.TravelMode()] = costing;
        var options = new Options { DateTimeType = DateTimeType.Invariant };
        var algorithm = new BidirectionalAStar();
        List<List<PathInfo>> paths = algorithm.GetBestPath(
            origin,
            destination,
            reader,
            modeCosting,
            costing.TravelMode(),
            options);

        Assert.NotEmpty(paths);
        Assert.NotEmpty(costing.ObservedTimes);
        Assert.All(costing.ObservedTimes, observed => Assert.True(observed.Valid));
        Assert.Contains(
            costing.ObservedTimes,
            observed => observed.LocalTime == invariant.LocalTime
                && observed.SecondOfWeek == invariant.SecondOfWeek
                && observed.TimezoneIndex == invariant.TimezoneIndex);
    }

    private static ValhallaTrafficWriteOptions WriteOptions(
        string storeRoot,
        string graphRoot,
        string graphSha,
        DateTimeOffset created,
        DateTimeOffset expires) =>
        new(storeRoot)
        {
            GraphTileDirectory = graphRoot,
            GraphSha256 = graphSha,
            CreatedAtUtc = created,
            ExpiresAtUtc = expires,
            Policy = TrafficSnapshotPolicy.Enabled,
        };

    private static ValhallaTrafficEdgeUpdate SpeedUpdate(
        GraphId tileId,
        uint edgeIndex,
        double speedKph,
        string sourceEventId,
        string providerId,
        double confidence) =>
        new(
            tileId.Value,
            edgeIndex,
            TrafficDirection.Forward,
            speedKph,
            null,
            null,
            false,
            false,
            true,
            confidence,
            sourceEventId,
            providerId,
            new GraphId(tileId.Tileid(), tileId.Level(), edgeIndex).Value);

    private static TrafficSnapshotReference AssertSucceeded(ValhallaTrafficWriteResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<TrafficSnapshotReference>(result.Snapshot);
    }

    private static (GraphId TileId, GraphTile Tile) FindTile(
        string graphRoot,
        uint minimumDirectedEdges)
    {
        foreach (string file in Directory.EnumerateFiles(
                     graphRoot,
                     "*.gph",
                     SearchOption.AllDirectories).OrderBy(static value => value, StringComparer.Ordinal))
        {
            GraphId tileId = ParseGraphId(graphRoot, file);
            GraphTile? tile = GraphTile.Create(graphRoot, tileId);
            if (tile is not null && tile.DirectedEdgeCount() >= minimumDirectedEdges)
            {
                return (tileId, tile);
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Tracked graph fixture contains no tile with {minimumDirectedEdges} directed edges.");
    }

    private static GraphId FindMissingTile(string graphRoot, byte level)
    {
        for (uint tileIndex = 0; tileIndex < 100_000; tileIndex++)
        {
            var candidate = new GraphId(tileIndex, level, 0);
            if (GraphTile.Create(graphRoot, candidate) is null)
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException("Could not find a missing graph tile identity.");
    }

    private static GraphId ParseGraphId(string graphRoot, string file)
    {
        string relative = Path.GetRelativePath(graphRoot, file).Replace(Path.DirectorySeparatorChar, '/');
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        byte level = byte.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        string digits = string.Concat(
            parts.Skip(1).Select(static part => part.Replace(".gph", string.Empty)));
        return new GraphId(
            uint.Parse(digits, System.Globalization.CultureInfo.InvariantCulture),
            level,
            0);
    }

    private static string FindMonacoFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
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

        throw new Xunit.Sdk.XunitException(
            "Tracked Monaco graph fixture was not copied to the test output.");
    }

    private static (PointLL A, PointLL B) PickTwoOnRoadPoints(
        string graphRoot,
        AutoCost costing)
    {
        byte topLevel = TileHierarchy.Levels()[^1].Level;
        var midpoints = new List<PointLL>();
        foreach (string file in Directory.EnumerateFiles(
                     graphRoot,
                     "*.gph",
                     SearchOption.AllDirectories))
        {
            GraphId tileId = ParseGraphId(graphRoot, file);
            if (tileId.Level() != topLevel)
            {
                continue;
            }

            GraphTile? tile = GraphTile.Create(graphRoot, tileId);
            if (tile is null)
            {
                continue;
            }

            for (uint nodeIndex = 0;
                 nodeIndex < tile.Header().Nodecount() && midpoints.Count < 2;
                 nodeIndex++)
            {
                NodeInfo node = tile.Node((int)nodeIndex);
                for (uint edgeOffset = 0; edgeOffset < node.EdgeCount; edgeOffset++)
                {
                    DirectedEdge edge = tile.DirectedEdge((int)(node.EdgeIndex + edgeOffset));
                    if (!costing.Allowed(edge, tile, DynamicCost.DisallowShortcut))
                    {
                        continue;
                    }

                    IReadOnlyList<PointLL> shape = tile.EdgeInfo(edge).Shape();
                    if (shape.Count >= 2 && edge.Length > 30)
                    {
                        midpoints.Add(shape[0].PointAlongSegment(shape[^1], 0.5));
                        break;
                    }
                }
            }

            if (midpoints.Count >= 2)
            {
                break;
            }
        }

        if (midpoints.Count < 2)
        {
            throw new Xunit.Sdk.XunitException(
                "Could not find two routable edges in the Monaco fixture.");
        }

        return (midpoints[0], midpoints[1]);
    }

    private static string NewTempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-runtime-exact",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingAutoCost(Costing costing) : AutoCost(costing)
    {
        public List<TimeInfo> ObservedTimes { get; } = [];

        public override Cost EdgeCost(
            DirectedEdge edge,
            GraphId edgeid,
            GraphTile tile,
            TimeInfo timeInfo,
            ref byte flowSources)
        {
            ObservedTimes.Add(timeInfo);
            return base.EdgeCost(edge, edgeid, tile, timeInfo, ref flowSources);
        }
    }
}
