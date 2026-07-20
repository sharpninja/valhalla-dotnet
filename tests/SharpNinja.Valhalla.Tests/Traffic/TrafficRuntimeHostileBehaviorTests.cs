using System.IO.Compression;
using System.Security.Cryptography;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficRuntimeHostileBehaviorTests
{
    [Fact]
    public async Task AcquireAsync_ForgedGenerationOutsideStore_IsRejectedBeforeRead()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        string outside = NewTempDirectory("outside");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            string graphSha = await GraphFingerprint.ComputeSha256Async(graph.Directory, TestContext.Current.CancellationToken);
            var forged = new TrafficSnapshotReference(
                graphSha,
                new string('a', 64),
                outside,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(5));

            TrafficSnapshotStoreException error = await Assert.ThrowsAsync<TrafficSnapshotStoreException>(
                () => store.AcquireAsync(forged, TestContext.Current.CancellationToken));

            Assert.Equal(TrafficSnapshotFailureCode.GraphMismatch, error.Code);
        }
        finally
        {
            DeleteDirectory(storeRoot);
            DeleteDirectory(outside);
        }
    }

    [Fact]
    public async Task PublishAsync_StagingOutsideStore_IsRejected()
    {
        string storeRoot = NewTempDirectory("store");
        string outside = NewTempDirectory("outside");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var manifest = new TrafficSnapshotManifest(
                new string('1', 64),
                string.Empty,
                TrafficSnapshotPolicy.Enabled,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(5),
                false,
                Array.Empty<TrafficSnapshotTileManifest>());

            await Assert.ThrowsAsync<ArgumentException>(() => store.PublishAsync(outside, manifest, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(storeRoot);
            DeleteDirectory(outside);
        }
    }

    [Fact]
    public async Task WriteAsync_GraphFingerprintMismatch_FailsWithoutPublishingCurrent()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            ValhallaTrafficWriteResult result = await writer.WriteAsync(
                [SpeedUpdate(graph, edgeIndex: 0, speedKph: 52)],
                WriteOptions(graph, new string('f', 64), created, created.AddMinutes(10)),
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Null(result.Snapshot);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ValhallaTileWriteFailed");
            Assert.Null(await store.GetCurrentAsync(
                new string('f', 64),
                TrafficSnapshotPolicy.Enabled,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task WriteAsync_NativeBytes_EncodeHeaderInvalidSpeedClosureAndIncidentDelay()
    {
        using var graph = TestGraphFixture.Create(minimumDirectedEdges: 4);
        string storeRoot = NewTempDirectory("store");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(graph.Directory, TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            DateTimeOffset expires = created.AddMinutes(15);
            DirectedEdge incidentEdge = graph.Tile.DirectedEdge(2);
            const double freeFlowKph = 72;
            const int delaySeconds = 60;

            ValhallaTrafficWriteResult result = await writer.WriteAsync(
                [
                    SpeedUpdate(graph, 0, 51),
                    new ValhallaTrafficEdgeUpdate(
                        graph.TileId.Value, 1, TrafficDirection.Forward, null, null, null,
                        true, true, true, 1, "closure", "hostile"),
                    new ValhallaTrafficEdgeUpdate(
                        graph.TileId.Value, 2, TrafficDirection.Forward, null, freeFlowKph, delaySeconds,
                        false, true, true, 1, "incident", "hostile"),
                ],
                WriteOptions(graph, graphSha, created, expires),
                TestContext.Current.CancellationToken);

            TrafficSnapshotReference snapshot = AssertSucceeded(result);
            string tilePath = Path.Combine(snapshot.GenerationDirectory, GraphTile.FileSuffix(graph.TileId));
            byte[] bytes = await File.ReadAllBytesAsync(tilePath, TestContext.Current.CancellationToken);
            Assert.Equal(TrafficTile.HeaderSize + ((long)graph.DirectedEdgeCount * TrafficTile.SpeedSize), bytes.LongLength);

            var tile = new TrafficTile(bytes);
            TrafficTileHeader header = Assert.NotNull(tile.Header);
            Assert.Equal(graph.TileId.Value, header.TileId);
            Assert.Equal(graph.DirectedEdgeCount, header.DirectedEdgeCount);
            Assert.Equal((uint)TrafficTileConstants.TrafficTileVersion, header.TrafficTileVersion);
            Assert.Equal((ulong)created.ToUnixTimeSeconds(), header.LastUpdate);
            Assert.NotEqual((ulong)expires.ToUnixTimeSeconds(), header.LastUpdate);
            Assert.Equal(0u, header.Spare2);
            Assert.Equal(0u, header.Spare3);

            TrafficSpeed speed = tile.TrafficSpeed(0);
            Assert.True(speed.SpeedValid());
            Assert.Equal(52u, speed.GetOverallSpeed());

            TrafficSpeed closure = tile.TrafficSpeed(1);
            Assert.True(closure.SpeedValid());
            Assert.True(closure.Closed());
            Assert.True(closure.HasIncidents);
            Assert.Equal(255u, closure.Breakpoint1);

            TrafficSpeed incident = tile.TrafficSpeed(2);
            double freeFlowSeconds = incidentEdge.Length / (freeFlowKph / 3.6d);
            double expectedKph = (incidentEdge.Length / (freeFlowSeconds + delaySeconds)) * 3.6d;
            uint expectedQuantized = checked((uint)(Math.Clamp(
                Math.Round(expectedKph / 2d, MidpointRounding.AwayFromZero),
                1d,
                126d) * 2d));
            Assert.True(incident.SpeedValid());
            Assert.True(incident.HasIncidents);
            Assert.False(incident.Closed());
            Assert.Equal(expectedQuantized, incident.GetOverallSpeed());

            TrafficSpeed sentinel = tile.TrafficSpeed(3);
            Assert.Equal(TrafficSpeed.Invalid.RawBits, sentinel.RawBits);
            Assert.False(sentinel.SpeedValid());
            Assert.False(sentinel.Closed());
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task WriteAsync_UnresolvedClosure_DoesNotWriteUnsafeDirection()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(graph.Directory, TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);

            ValhallaTrafficWriteResult result = await writer.WriteAsync(
                [
                    new ValhallaTrafficEdgeUpdate(
                        graph.TileId.Value, 0, TrafficDirection.Unknown, null, null, null,
                        true, true, false, 1, "ambiguous", "hostile"),
                ],
                WriteOptions(graph, graphSha, created, created.AddMinutes(10)),
                TestContext.Current.CancellationToken);

            TrafficSnapshotReference snapshot = AssertSucceeded(result);
            Assert.Equal(0, result.UpdateCount);
            Assert.False(File.Exists(Path.Combine(snapshot.GenerationDirectory, GraphTile.FileSuffix(graph.TileId))));
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task WriteAsync_IdenticalNativeContentReusesVersion_ButChangedObservationCreatesNewVersion()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(
                graph.Directory,
                TestContext.Current.CancellationToken);
            DateTimeOffset firstCreated = DateTimeOffset.UtcNow.AddMinutes(-20);
            DateTimeOffset secondCreated = firstCreated.AddMinutes(10);
            DateTimeOffset expires = firstCreated.AddMinutes(30);
            ValhallaTrafficEdgeUpdate[] updates = [SpeedUpdate(graph, 0, 64)];

            TrafficSnapshotReference first = AssertSucceeded(await writer.WriteAsync(
                updates,
                WriteOptions(graph, graphSha, firstCreated, expires),
                TestContext.Current.CancellationToken));
            TrafficSnapshotReference identical = AssertSucceeded(await writer.WriteAsync(
                updates,
                WriteOptions(graph, graphSha, firstCreated, expires),
                TestContext.Current.CancellationToken));
            TrafficSnapshotReference changedObservation = AssertSucceeded(await writer.WriteAsync(
                updates,
                WriteOptions(graph, graphSha, secondCreated, expires),
                TestContext.Current.CancellationToken));

            Assert.Equal(first.Version, identical.Version);
            Assert.Equal(first.GenerationDirectory, identical.GenerationDirectory);
            Assert.Equal(first.CreatedAtUtc, identical.CreatedAtUtc);
            Assert.NotEqual(first.Version, changedObservation.Version);
            Assert.NotEqual(first.GenerationDirectory, changedObservation.GenerationDirectory);
            Assert.Equal(secondCreated, changedObservation.CreatedAtUtc);

            string firstTilePath = Path.Combine(
                first.GenerationDirectory,
                GraphTile.FileSuffix(graph.TileId));
            string changedTilePath = Path.Combine(
                changedObservation.GenerationDirectory,
                GraphTile.FileSuffix(graph.TileId));
            var firstTile = new TrafficTile(await File.ReadAllBytesAsync(
                firstTilePath,
                TestContext.Current.CancellationToken));
            var changedTile = new TrafficTile(await File.ReadAllBytesAsync(
                changedTilePath,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                (ulong)firstCreated.ToUnixTimeSeconds(),
                Assert.NotNull(firstTile.Header).LastUpdate);
            Assert.Equal(
                (ulong)secondCreated.ToUnixTimeSeconds(),
                Assert.NotNull(changedTile.Header).LastUpdate);
            Assert.Equal(2, Directory.EnumerateDirectories(
                Path.Combine(storeRoot, "graphs", graphSha, "generations")).Count());
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task WriteAsync_CorruptPreexistingContentAddress_IsRejectedInsteadOfReused()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(graph.Directory, TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            DateTimeOffset expires = created.AddMinutes(20);
            ValhallaTrafficWriteOptions options = WriteOptions(graph, graphSha, created, expires);
            ValhallaTrafficEdgeUpdate[] updates = [SpeedUpdate(graph, 0, 70)];

            TrafficSnapshotReference first = AssertSucceeded(
                await writer.WriteAsync(updates, options, TestContext.Current.CancellationToken));
            string tilePath = Path.Combine(first.GenerationDirectory, GraphTile.FileSuffix(graph.TileId));
            byte[] corrupted = await File.ReadAllBytesAsync(tilePath, TestContext.Current.CancellationToken);
            corrupted[^1] ^= 0x7f;
            await File.WriteAllBytesAsync(tilePath, corrupted, TestContext.Current.CancellationToken);

            ValhallaTrafficWriteResult second = await writer.WriteAsync(updates, options, TestContext.Current.CancellationToken);

            Assert.False(second.Succeeded);
            Assert.Null(second.Snapshot);
            Assert.Contains(second.Diagnostics, diagnostic => diagnostic.Code == "ValhallaTileWriteFailed");
            TrafficSnapshotStoreException acquireError = await Assert.ThrowsAsync<TrafficSnapshotStoreException>(
                () => store.AcquireAsync(first, TestContext.Current.CancellationToken));
            Assert.Equal(TrafficSnapshotFailureCode.Incomplete, acquireError.Code);
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task Lease_OpenTrafficMemory_DetectsSameLengthMutationAfterAcquire()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            TrafficSnapshotReference snapshot = await WriteSnapshotAsync(
                graph,
                storeRoot,
                [SpeedUpdate(graph, 0, 60)]);
            var store = new TrafficSnapshotStore(storeRoot);
            await using ITrafficSnapshotLease lease = await store.AcquireAsync(snapshot, TestContext.Current.CancellationToken);
            string tilePath = Path.Combine(snapshot.GenerationDirectory, GraphTile.FileSuffix(graph.TileId));
            byte[] bytes = await File.ReadAllBytesAsync(tilePath, TestContext.Current.CancellationToken);
            bytes[^1] ^= 0x40;
            await File.WriteAllBytesAsync(tilePath, bytes, TestContext.Current.CancellationToken);

            TrafficSnapshotStoreException error = Assert.Throws<TrafficSnapshotStoreException>(
                () => lease.OpenTrafficMemory(graph.TileId));

            Assert.Equal(TrafficSnapshotFailureCode.Incomplete, error.Code);
            Assert.Contains("checksum changed", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task CleanupAsync_RetainsBothPolicyCurrentPointers_EvenBeyondRetentionCount()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot, maxRetainedGenerations: 1);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(graph.Directory, TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            DateTimeOffset expires = created.AddMinutes(20);

            TrafficSnapshotReference enabled = AssertSucceeded(await writer.WriteAsync(
                [SpeedUpdate(graph, 0, 66)],
                WriteOptions(graph, graphSha, created, expires, TrafficSnapshotPolicy.Enabled),
                TestContext.Current.CancellationToken));
            TrafficSnapshotReference closureOnly = AssertSucceeded(await writer.WriteAsync(
                [
                    new ValhallaTrafficEdgeUpdate(
                        graph.TileId.Value, 0, TrafficDirection.Forward, null, null, null,
                        true, false, true, 1, "closure", "hostile"),
                ],
                WriteOptions(graph, graphSha, created, expires, TrafficSnapshotPolicy.ClosureOnly),
                TestContext.Current.CancellationToken));

            await store.CleanupAsync(TestContext.Current.CancellationToken);

            Assert.True(Directory.Exists(enabled.GenerationDirectory));
            Assert.True(Directory.Exists(closureOnly.GenerationDirectory));
            Assert.Equal(enabled.Version, (await store.GetCurrentAsync(graphSha, TrafficSnapshotPolicy.Enabled, TestContext.Current.CancellationToken))?.Version);
            Assert.Equal(closureOnly.Version, (await store.GetCurrentAsync(graphSha, TrafficSnapshotPolicy.ClosureOnly, TestContext.Current.CancellationToken))?.Version);
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task CleanupAsync_CrossStoreLeasePinsRetiredGenerationUntilDisposal()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            var firstStore = new TrafficSnapshotStore(storeRoot, maxRetainedGenerations: 1);
            var secondStore = new TrafficSnapshotStore(storeRoot, maxRetainedGenerations: 1);
            string graphSha = await GraphFingerprint.ComputeSha256Async(graph.Directory, TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            var firstWriter = new DirectoryValhallaTrafficTileWriter(firstStore);
            var secondWriter = new DirectoryValhallaTrafficTileWriter(secondStore);

            TrafficSnapshotReference first = AssertSucceeded(await firstWriter.WriteAsync(
                [SpeedUpdate(graph, 0, 40)],
                WriteOptions(graph, graphSha, created, created.AddMinutes(10)),
                TestContext.Current.CancellationToken));
            ITrafficSnapshotLease pin = await firstStore.AcquireAsync(first, TestContext.Current.CancellationToken);
            TrafficSnapshotReference second = AssertSucceeded(await secondWriter.WriteAsync(
                [SpeedUpdate(graph, 0, 80)],
                WriteOptions(graph, graphSha, created.AddSeconds(1), created.AddMinutes(11)),
                TestContext.Current.CancellationToken));

            Assert.NotEqual(first.Version, second.Version);
            Assert.True(Directory.Exists(first.GenerationDirectory));
            await pin.DisposeAsync();
            await secondStore.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(first.GenerationDirectory));
            Assert.True(Directory.Exists(second.GenerationDirectory));
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task GraphReader_GzipGraphTile_AttachesPinnedTrafficAndHonorsClosure()
    {
        using var graph = TestGraphFixture.Create(gzip: true);
        string storeRoot = NewTempDirectory("store");
        try
        {
            TrafficSnapshotReference snapshot = await WriteSnapshotAsync(
                graph,
                storeRoot,
                [
                    new ValhallaTrafficEdgeUpdate(
                        graph.TileId.Value, 0, TrafficDirection.Forward, null, null, null,
                        true, false, true, 1, "closed", "hostile"),
                ]);
            var store = new TrafficSnapshotStore(storeRoot);
            await using var factory = new EmbeddedValhallaGraphReaderFactory(store);
            await using EmbeddedValhallaGraphReaderFactory.AsyncLease lease =
                await factory.AcquireAsync(graph.Directory, snapshot, TestContext.Current.CancellationToken);

            GraphTile? tile = lease.Reader.GetGraphTile(graph.TileId);
            Assert.NotNull(tile);
            Assert.Equal(snapshot.Version, lease.TrafficSnapshot?.Version);
            Assert.True(tile.IsClosed(0));
            Assert.True(tile.GetTrafficTile().TrafficSpeed(0).SpeedValid());
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task ReaderFactory_SameVersionConcurrentLeases_ShareReaderAndDisposeExactlyOnce()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            TrafficSnapshotReference snapshot = await WriteSnapshotAsync(
                graph,
                storeRoot,
                [SpeedUpdate(graph, 0, 58)]);
            var store = new TrafficSnapshotStore(storeRoot);
            var factory = new EmbeddedValhallaGraphReaderFactory(store);
            EmbeddedValhallaGraphReaderFactory.AsyncLease first =
                await factory.AcquireAsync(graph.Directory, snapshot, TestContext.Current.CancellationToken);
            EmbeddedValhallaGraphReaderFactory.AsyncLease second =
                await factory.AcquireAsync(graph.Directory, snapshot, TestContext.Current.CancellationToken);

            Assert.Same(first.Reader, second.Reader);
            Assert.Equal(0, factory.CacheClearCount);
            await factory.DisposeAsync();
            Assert.NotNull(first.Reader.GetGraphTile(graph.TileId));
            Assert.Equal(0, factory.CacheClearCount);

            await Task.WhenAll(
                first.DisposeAsync().AsTask(),
                second.DisposeAsync().AsTask());

            Assert.Equal(1, factory.CacheClearCount);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await factory.AcquireAsync(graph.Directory, snapshot, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public async Task ReaderFactory_VersionChange_RetiresOldReaderOnlyAfterItsLeaseEnds()
    {
        using var graph = TestGraphFixture.Create();
        string storeRoot = NewTempDirectory("store");
        try
        {
            var store = new TrafficSnapshotStore(storeRoot, maxRetainedGenerations: 3);
            var writer = new DirectoryValhallaTrafficTileWriter(store);
            string graphSha = await GraphFingerprint.ComputeSha256Async(graph.Directory, TestContext.Current.CancellationToken);
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
            TrafficSnapshotReference firstSnapshot = AssertSucceeded(await writer.WriteAsync(
                [SpeedUpdate(graph, 0, 44)],
                WriteOptions(graph, graphSha, created, created.AddMinutes(10)),
                TestContext.Current.CancellationToken));
            TrafficSnapshotReference secondSnapshot = AssertSucceeded(await writer.WriteAsync(
                [SpeedUpdate(graph, 0, 88)],
                WriteOptions(graph, graphSha, created.AddSeconds(1), created.AddMinutes(11)),
                TestContext.Current.CancellationToken));

            await using var factory = new EmbeddedValhallaGraphReaderFactory(store);
            EmbeddedValhallaGraphReaderFactory.AsyncLease first =
                await factory.AcquireAsync(graph.Directory, firstSnapshot, TestContext.Current.CancellationToken);
            EmbeddedValhallaGraphReaderFactory.AsyncLease second =
                await factory.AcquireAsync(graph.Directory, secondSnapshot, TestContext.Current.CancellationToken);

            Assert.NotSame(first.Reader, second.Reader);
            Assert.Equal(firstSnapshot.Version, first.TrafficSnapshot?.Version);
            Assert.Equal(secondSnapshot.Version, second.TrafficSnapshot?.Version);
            Assert.Equal(0, factory.CacheClearCount);
            await first.DisposeAsync();
            Assert.Equal(1, factory.CacheClearCount);
            Assert.Equal(88u, second.Reader.GetGraphTile(graph.TileId)!.GetTrafficTile().TrafficSpeed(0).GetOverallSpeed());
            await second.DisposeAsync();
        }
        finally
        {
            DeleteDirectory(storeRoot);
        }
    }

    [Fact]
    public void InvariantTrafficTime_EquivalentInstantsProduceDeterministicUtcSecondsOfWeek()
    {
        DateTimeOffset utc = new(2026, 7, 20, 0, 0, 1, TimeSpan.Zero);
        DateTimeOffset offset = utc.ToOffset(TimeSpan.FromHours(-5));

        TimeInfo fromUtc = InvariantTrafficTime.Create(utc);
        TimeInfo fromOffset = InvariantTrafficTime.Create(offset);

        Assert.True(fromUtc.Valid);
        Assert.Equal(0ul, fromUtc.TimezoneIndex);
        Assert.Equal(1ul, fromUtc.SecondOfWeek);
        Assert.Equal((ulong)utc.ToUnixTimeSeconds(), fromUtc.LocalTime);
        Assert.Equal(fromUtc.Valid, fromOffset.Valid);
        Assert.Equal(fromUtc.TimezoneIndex, fromOffset.TimezoneIndex);
        Assert.Equal(fromUtc.SecondOfWeek, fromOffset.SecondOfWeek);
        Assert.Equal(fromUtc.LocalTime, fromOffset.LocalTime);
        Assert.Equal(0ul, fromUtc.SecondsFromNow);
        Assert.False(fromUtc.NegativeSecondsFromNow);
    }

    private static async Task<TrafficSnapshotReference> WriteSnapshotAsync(
        TestGraphFixture graph,
        string storeRoot,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates)
    {
        var store = new TrafficSnapshotStore(storeRoot, maxRetainedGenerations: 3);
        var writer = new DirectoryValhallaTrafficTileWriter(store);
        string graphSha = await GraphFingerprint.ComputeSha256Async(graph.Directory, TestContext.Current.CancellationToken);
        DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);
        return AssertSucceeded(await writer.WriteAsync(
            updates,
            WriteOptions(graph, graphSha, created, created.AddMinutes(20)),
            TestContext.Current.CancellationToken));
    }

    private static ValhallaTrafficWriteOptions WriteOptions(
        TestGraphFixture graph,
        string graphSha,
        DateTimeOffset created,
        DateTimeOffset expires,
        TrafficSnapshotPolicy policy = TrafficSnapshotPolicy.Enabled) =>
        new(graph.Directory)
        {
            GraphTileDirectory = graph.Directory,
            GraphSha256 = graphSha,
            CreatedAtUtc = created,
            ExpiresAtUtc = expires,
            Policy = policy,
        };

    private static ValhallaTrafficEdgeUpdate SpeedUpdate(
        TestGraphFixture graph,
        uint edgeIndex,
        double speedKph) =>
        new(
            graph.TileId.Value,
            edgeIndex,
            TrafficDirection.Forward,
            speedKph,
            100,
            null,
            false,
            false,
            true,
            1,
            $"speed-{edgeIndex}",
            "hostile");

    private static TrafficSnapshotReference AssertSucceeded(ValhallaTrafficWriteResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(
                static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.NotNull(result.Snapshot);
        return result.Snapshot;
    }

    private static string NewTempDirectory(string leaf)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Tests",
            "traffic-runtime-hostile",
            leaf + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class TestGraphFixture : IDisposable
    {
        private TestGraphFixture(
            string directory,
            GraphId tileId,
            GraphTile tile,
            bool gzip)
        {
            Directory = directory;
            TileId = tileId;
            Tile = tile;
            DirectedEdgeCount = tile.DirectedEdgeCount();
            IsGzip = gzip;
        }

        public string Directory { get; }
        public GraphId TileId { get; }
        public GraphTile Tile { get; }
        public uint DirectedEdgeCount { get; }
        public bool IsGzip { get; }

        public static TestGraphFixture Create(
            bool gzip = false,
            uint minimumDirectedEdges = 1)
        {
            string sourceRoot = FindMonacoFixture();
            (string source, GraphId tileId, GraphTile tile) = System.IO.Directory
                .EnumerateFiles(sourceRoot, "*.gph", SearchOption.AllDirectories)
                .OrderBy(static file => file, StringComparer.Ordinal)
                .Select(file =>
                {
                    GraphId id = ParseGraphId(sourceRoot, file);
                    return (File: file, Id: id, Tile: GraphTile.Create(sourceRoot, id));
                })
                .Where(candidate => candidate.Tile is not null
                    && candidate.Tile.DirectedEdgeCount() >= minimumDirectedEdges)
                .Select(candidate => (candidate.File, candidate.Id, candidate.Tile!))
                .First();

            string destinationRoot = NewTempDirectory(gzip ? "graph-gzip" : "graph");
            string relative = Path.GetRelativePath(sourceRoot, source);
            string target = Path.Combine(destinationRoot, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (gzip)
            {
                using FileStream input = File.OpenRead(source);
                using FileStream output = File.Create(target + ".gz");
                using var compressor = new GZipStream(output, CompressionLevel.SmallestSize);
                input.CopyTo(compressor);
            }
            else
            {
                File.Copy(source, target);
            }

            GraphTile copied = GraphTile.Create(destinationRoot, tileId)
                ?? throw new Xunit.Sdk.XunitException("Copied graph tile could not be opened.");
            return new TestGraphFixture(destinationRoot, tileId, copied, gzip);
        }

        public void Dispose() => DeleteDirectory(Directory);

        private static string FindMonacoFixture()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "artifacts", "valhalla-monaco-tiles");
                if (System.IO.Directory.Exists(candidate))
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
            string digits = string.Concat(parts
                .Skip(1)
                .Select(static part => Path.GetFileNameWithoutExtension(part)));
            return new GraphId(
                uint.Parse(digits, System.Globalization.CultureInfo.InvariantCulture),
                level,
                0);
        }
    }
}
