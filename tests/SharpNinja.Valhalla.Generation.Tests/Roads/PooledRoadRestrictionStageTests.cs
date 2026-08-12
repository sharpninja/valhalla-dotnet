using System.Collections;
using System.Security.Cryptography;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledRoadRestrictionStageTests
{
    [Fact]
    public async Task ApplyAsync_SerializesForwardAndReversePayloadFromBoundedTiles()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            IReadOnlyDictionary<string, string> sourceBefore =
                HashTileTree(tileDirectory);

            GraphId fromNode = FindGraphId(graph, 10);
            GraphId firstViaNode = FindGraphId(graph, 11);
            GraphId lastViaNode = FindGraphId(graph, 12);
            GraphId toNode = FindGraphId(graph, 13);
            GraphId fromEdge = FindDirectedEdgeId(
                tileDirectory,
                fromNode,
                wayId: 20,
                endNode: firstViaNode);
            GraphId viaEdge = FindDirectedEdgeId(
                tileDirectory,
                firstViaNode,
                wayId: 21,
                endNode: lastViaNode);
            GraphId toEdge = FindDirectedEdgeId(
                tileDirectory,
                lastViaNode,
                wayId: 22,
                endNode: toNode);
            GraphId reverseFromEdge = FindDirectedEdgeId(
                tileDirectory,
                firstViaNode,
                wayId: 20,
                endNode: fromNode);
            GraphId reverseViaEdge = FindDirectedEdgeId(
                tileDirectory,
                lastViaNode,
                wayId: 21,
                endNode: firstViaNode);
            GraphId reverseToEdge = FindDirectedEdgeId(
                tileDirectory,
                toNode,
                wayId: 22,
                endNode: lastViaNode);
            string restrictedTileDirectory =
                Path.Combine(root, "restricted-tiles");
            string workingDirectory =
                Path.Combine(root, "restriction-stage");
            Directory.CreateDirectory(workingDirectory);
            string sentinelPath =
                Path.Combine(workingDirectory, "sentinel.txt");
            File.WriteAllText(sentinelPath, "preserve");

            PooledRoadRestrictionStageReceipt receipt =
                await PooledRoadRestrictionStage.ApplyAsync(
                    tileDirectory,
                    restrictedTileDirectory,
                    semanticStore,
                    RestrictionOptions(Path.Combine(root, "restriction-stage")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(sourceBefore, HashTileTree(tileDirectory));
            Assert.Equal("preserve", File.ReadAllText(sentinelPath));
            Assert.True(Directory.Exists(restrictedTileDirectory));
            Assert.Equal(1, receipt.ProjectedForwardCount);
            Assert.Equal(1, receipt.ProjectedReverseCount);
            Assert.Equal(1U, receipt.SerializedForwardCount);
            Assert.Equal(1U, receipt.SerializedReverseCount);

            var freshReader = new GraphReader(
                new GraphReader.Config
                {
                    TileDir = restrictedTileDirectory,
                });
            GraphTile tile = freshReader.GetGraphTile(toEdge) ??
                throw new InvalidDataException(
                    $"Tile for edge {toEdge} was not readable.");

            (ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias)
                forward = GetFirst(
                    tile.GetComplexRestrictions(
                        forward: true,
                        toEdge,
                        GraphConstants.AutoAccess));
            Assert.Equal(fromEdge, forward.Restriction.FromGraphId());
            Assert.Equal(toEdge, forward.Restriction.ToGraphId());
            Assert.Equal(RestrictionType.NoLeftTurn, forward.Restriction.Type());
            Assert.Equal([viaEdge], forward.Vias);

            GraphTile reverseTile = freshReader.GetGraphTile(reverseFromEdge) ??
                throw new InvalidDataException(
                    $"Tile for reverse edge {reverseFromEdge} was not readable.");
            (ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias)
                reverse = GetFirst(
                    reverseTile.GetComplexRestrictions(
                        forward: false,
                        reverseFromEdge,
                        GraphConstants.AutoAccess));
            Assert.Equal(
                reverseFromEdge,
                reverse.Restriction.FromGraphId());
            Assert.Equal(
                reverseToEdge,
                reverse.Restriction.ToGraphId());
            Assert.Equal(
                RestrictionType.NoLeftTurn,
                reverse.Restriction.Type());
            Assert.Equal([reverseViaEdge], reverse.Vias);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SerializedNoTurnRestriction_RejectsOnlyAvailableRoute()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);

            GraphId preEdge = FindDirectedEdgeId(
                tileDirectory,
                FindGraphId(graph, 9),
                wayId: 19,
                endNode: FindGraphId(graph, 10));
            GraphId postEdge = FindDirectedEdgeId(
                tileDirectory,
                FindGraphId(graph, 13),
                wayId: 23,
                endNode: FindGraphId(graph, 14));

            var originPoint = new PointLL(-86.7035, 36.1000);
            var destinationPoint = new PointLL(-86.6995, 36.1000);
            PathLocation origin = CreatePathLocation(
                originPoint,
                preEdge,
                percentAlong: 0.5);
            PathLocation destination = CreatePathLocation(
                destinationPoint,
                postEdge,
                percentAlong: 0.5);

            AutoCost costing = MakeAutoCosting();
            List<List<PathInfo>> before = UnidirectionalAStar
                .TimeDepForward()
                .GetBestPath(
                    origin,
                    destination,
                    new GraphReader(
                        new GraphReader.Config
                        {
                            TileDir = tileDirectory,
                        }),
                    MakeModeCosting(costing),
                    costing.TravelMode());
            Assert.Single(before);
            Assert.NotEmpty(before[0]);
            string restrictedTileDirectory =
                Path.Combine(root, "restricted-tiles");

            await PooledRoadRestrictionStage.ApplyAsync(
                tileDirectory,
                restrictedTileDirectory,
                semanticStore,
                RestrictionOptions(
                    Path.Combine(root, "restriction-stage")),
                TestContext.Current.CancellationToken);

            List<List<PathInfo>> after = UnidirectionalAStar
                .TimeDepForward()
                .GetBestPath(
                    origin,
                    destination,
                    new GraphReader(
                        new GraphReader.Config
                        {
                            TileDir = restrictedTileDirectory,
                        }),
                    MakeModeCosting(costing),
                    costing.TravelMode());
            Assert.Empty(after);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MultiViaRestriction_SerializesOrderedViasAndRejectsMatchingChain()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new MultiViaRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);

            GraphId a = FindGraphId(graph, 10);
            GraphId b = FindGraphId(graph, 11);
            GraphId c = FindGraphId(graph, 12);
            GraphId d = FindGraphId(graph, 13);
            GraphId e = FindGraphId(graph, 14);
            GraphId ab = FindDirectedEdgeId(
                tileDirectory,
                a,
                wayId: 20,
                endNode: b);
            GraphId bc = FindDirectedEdgeId(
                tileDirectory,
                b,
                wayId: 21,
                endNode: c);
            GraphId cd = FindDirectedEdgeId(
                tileDirectory,
                c,
                wayId: 22,
                endNode: d);
            GraphId de = FindDirectedEdgeId(
                tileDirectory,
                d,
                wayId: 23,
                endNode: e);
            GraphId ba = FindDirectedEdgeId(
                tileDirectory,
                b,
                wayId: 20,
                endNode: a);
            GraphId cb = FindDirectedEdgeId(
                tileDirectory,
                c,
                wayId: 21,
                endNode: b);
            GraphId dc = FindDirectedEdgeId(
                tileDirectory,
                d,
                wayId: 22,
                endNode: c);
            GraphId ed = FindDirectedEdgeId(
                tileDirectory,
                e,
                wayId: 23,
                endNode: d);
            string restrictedTileDirectory =
                Path.Combine(root, "restricted-tiles");

            await PooledRoadRestrictionStage.ApplyAsync(
                tileDirectory,
                restrictedTileDirectory,
                semanticStore,
                RestrictionOptions(
                    Path.Combine(root, "restriction-stage")),
                TestContext.Current.CancellationToken);

            var freshReader = new GraphReader(
                new GraphReader.Config
                {
                    TileDir = restrictedTileDirectory,
                });
            GraphTile forwardTile =
                freshReader.GetGraphTile(de) ??
                throw new InvalidDataException(
                    $"Tile for edge {de} was not readable.");
            (ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias)
                forward = GetFirst(
                    forwardTile.GetComplexRestrictions(
                        forward: true,
                        de,
                        GraphConstants.AutoAccess));
            Assert.Equal(ab, forward.Restriction.FromGraphId());
            Assert.Equal(de, forward.Restriction.ToGraphId());
            Assert.Equal(
                RestrictionType.NoTurn,
                forward.Restriction.Type());
            Assert.Equal([cd, bc], forward.Vias);

            GraphTile reverseTile =
                freshReader.GetGraphTile(ba) ??
                throw new InvalidDataException(
                    $"Tile for edge {ba} was not readable.");
            (ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias)
                reverse = GetFirst(
                    reverseTile.GetComplexRestrictions(
                        forward: false,
                        ba,
                        GraphConstants.AutoAccess));
            Assert.Equal(ba, reverse.Restriction.FromGraphId());
            Assert.Equal(ed, reverse.Restriction.ToGraphId());
            Assert.Equal(
                RestrictionType.NoTurn,
                reverse.Restriction.Type());
            Assert.Equal([cb, dc], reverse.Vias);

            PointLL originPoint = new(-86.7029, 36.1000);
            PointLL destinationPoint = new(-86.6991, 36.1000);
            PathLocation origin = CreatePathLocation(
                originPoint,
                ab,
                percentAlong: 0.1);
            PathLocation destination = CreatePathLocation(
                destinationPoint,
                de,
                percentAlong: 0.9);
            AutoCost unrestrictedCosting =
                MakeAutoCosting(ignoreRestrictions: true);
            List<List<PathInfo>> controlPaths =
                UnidirectionalAStar
                    .TimeDepForward()
                    .GetBestPath(
                        origin,
                        destination,
                        new GraphReader(
                            new GraphReader.Config
                            {
                                TileDir = restrictedTileDirectory,
                            }),
                        MakeModeCosting(unrestrictedCosting),
                        unrestrictedCosting.TravelMode());
            Assert.Single(controlPaths);
            Assert.Equal(
                [ab, bc, cd, de],
                controlPaths[0]
                    .Select(pathInfo => pathInfo.Edgeid)
                    .ToArray());

            AutoCost restrictedCosting =
                MakeAutoCosting(ignoreRestrictions: false);
            List<List<PathInfo>> restrictedPaths =
                UnidirectionalAStar
                    .TimeDepForward()
                    .GetBestPath(
                        origin,
                        destination,
                        new GraphReader(
                            new GraphReader.Config
                            {
                                TileDir = restrictedTileDirectory,
                            }),
                        MakeModeCosting(restrictedCosting),
                        restrictedCosting.TravelMode());
            Assert.Empty(restrictedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LazyAndEagerRestrictionInputs_ProduceEquivalentTiles()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string sourceDirectory = Path.Combine(root, "source-tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    sourceDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            string lazyDirectory = Path.Combine(root, "lazy-tiles");
            string eagerDirectory = Path.Combine(root, "eager-tiles");
            CopyTileTree(sourceDirectory, lazyDirectory);
            CopyTileTree(sourceDirectory, eagerDirectory);

            using ComplexRestrictionSequenceSet restrictions =
                await ComplexRestrictionSequenceSet.BuildAsync(
                    semanticStore,
                    new ComplexRestrictionSequenceSetOptions(
                        Path.Combine(root, "restriction-sequences"),
                        IntermediateStorageMode.Auto,
                        MemoryBudgetBytes: 8 * 1024 * 1024,
                        ScratchDiskBudgetBytes: 32 * 1024 * 1024,
                        SegmentSizeBytes: 64 * 1024),
                    TestContext.Current.CancellationToken);
            RestrictionBuilder.Build(
                new GraphReader(
                    new GraphReader.Config
                    {
                        TileDir = lazyDirectory,
                    }),
                restrictions.Forward,
                restrictions.Reverse,
                TestContext.Current.CancellationToken);
            RestrictionBuilder.Build(
                new GraphReader(
                    new GraphReader.Config
                    {
                        TileDir = eagerDirectory,
                    }),
                restrictions.Forward.ToArray(),
                restrictions.Reverse.ToArray(),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                HashTileTree(lazyDirectory),
                HashTileTree(eagerDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledBeforeProjection_DoesNotMutateTilesOrLeaveWorkingStores()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);

            IReadOnlyDictionary<string, string> before =
                HashTileTree(tileDirectory);
            string workDirectory =
                Path.Combine(root, "restriction-stage");
            string restrictedTileDirectory =
                Path.Combine(root, "restricted-tiles");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PooledRoadRestrictionStage
                    .ApplyAsync(
                        tileDirectory,
                        restrictedTileDirectory,
                        semanticStore,
                        RestrictionOptions(workDirectory),
                        cancellation.Token)
                    .AsTask());

            Assert.Equal(before, HashTileTree(tileDirectory));
            Assert.False(Directory.Exists(restrictedTileDirectory));
            Assert.False(Directory.Exists(workDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestrictionBuilder_CancellationDuringLookupStopsBeforeTilePublication()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            using ComplexRestrictionSequenceSet restrictions =
                await ComplexRestrictionSequenceSet.BuildAsync(
                    semanticStore,
                    new ComplexRestrictionSequenceSetOptions(
                        Path.Combine(root, "restriction-sequences"),
                        IntermediateStorageMode.Auto,
                        MemoryBudgetBytes: 8 * 1024 * 1024,
                        ScratchDiskBudgetBytes: 32 * 1024 * 1024,
                        SegmentSizeBytes: 64 * 1024),
                    TestContext.Current.CancellationToken);

            IReadOnlyDictionary<string, string> before =
                HashTileTree(tileDirectory);
            using var cancellation = new CancellationTokenSource();
            var cancelingRestrictions =
                new CancelOnFirstReadRestrictionList(
                    restrictions.Forward,
                    cancellation);

            Assert.ThrowsAny<OperationCanceledException>(
                () => RestrictionBuilder.Build(
                    new GraphReader(
                        new GraphReader.Config
                        {
                            TileDir = tileDirectory,
                        }),
                    cancelingRestrictions,
                    restrictions.Reverse,
                    cancellation.Token));

            Assert.Equal(before, HashTileTree(tileDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BoundedRestrictionTileCatalog_OrdersDeterministicallyAndEnforcesCapacity()
    {
        string root = CreateRoot();
        try
        {
            GraphId[] tileIds =
            [
                new GraphId(3, 2, 0),
                new GraphId(1, 2, 0),
                new GraphId(2, 2, 0),
            ];
            foreach (GraphId tileId in tileIds)
            {
                string path = Path.Combine(
                    root,
                    GraphTile.FileSuffix(tileId));
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [0x01]);
            }

            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    root,
                    maxTileCount: 3);
            Assert.Equal(
                tileIds.OrderBy(static id => id.Value),
                catalog.GetLevel(2));
            InvalidOperationException failure =
                Assert.Throws<InvalidOperationException>(
                    () => BoundedRestrictionTileCatalog.Build(
                        root,
                        maxTileCount: 2));
            Assert.Contains(
                "capacity",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveStageOutcome_OperationFailureRemainsPrimaryWhenCleanupFails()
    {
        var operationFailure = new InvalidOperationException("operation");
        var cleanupFailure = new IOException("cleanup");

        Exception actual = Assert.Throws<InvalidOperationException>(
            () => PooledRoadRestrictionStage.ResolveStageOutcome(
                receipt: null,
                operationFailure,
                cleanupFailure));

        Assert.Same(operationFailure, actual);
        Assert.Same(
            cleanupFailure,
            actual.Data["PooledRoadRestrictionStage.CleanupFailure"]);
    }

    [Fact]
    public void ResolveStageOutcome_CleanupFailureSurfacesAfterSuccessfulStage()
    {
        var cleanupFailure = new IOException("cleanup");
        var receipt = new PooledRoadRestrictionStageReceipt(
            ProjectedForwardCount: 0,
            ProjectedReverseCount: 0,
            SerializedForwardCount: 0,
            SerializedReverseCount: 0);

        Exception actual = Assert.Throws<IOException>(
            () => PooledRoadRestrictionStage.ResolveStageOutcome(
                receipt,
                operationFailure: null,
                cleanupFailure));

        Assert.Same(cleanupFailure, actual);
    }

    [Fact]
    public async Task ApplyAsync_BudgetTooSmallForReaderAndBookkeepingFailsBeforeMutation()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            IReadOnlyDictionary<string, string> before =
                HashTileTree(tileDirectory);
            string restrictedTileDirectory =
                Path.Combine(root, "restricted-tiles");
            string workingDirectory =
                Path.Combine(root, "restriction-stage");
            Directory.CreateDirectory(workingDirectory);
            string sentinelPath =
                Path.Combine(workingDirectory, "sentinel.txt");
            File.WriteAllText(sentinelPath, "preserve");

            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(workingDirectory) with
                {
                    MemoryBudgetBytes = 1024,
                };

            ValhallaGenerationResourceLimitException failure =
                await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                    () => PooledRoadRestrictionStage.ApplyAsync(
                            tileDirectory,
                            restrictedTileDirectory,
                            semanticStore,
                            options,
                            TestContext.Current.CancellationToken)
                        .AsTask());

            Assert.Contains(
                "memory budget",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, HashTileTree(tileDirectory));
            Assert.Equal("preserve", File.ReadAllText(sentinelPath));
            Assert.Empty(Directory.EnumerateDirectories(workingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_InvalidSourceBuildIdentityFailsBeforeMutation()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);

            foreach (string tilePath in Directory.EnumerateFiles(
                         tileDirectory,
                         "*.gph",
                         SearchOption.AllDirectories))
            {
                RewriteTileBuildId(
                    tilePath,
                    unchecked((ushort)(ReadTileBuildId(tilePath) + 1)));
            }

            IReadOnlyDictionary<string, string> sourceBefore =
                HashTileTree(tileDirectory);
            string destinationDirectory =
                Path.Combine(root, "restricted-tiles");
            string workingDirectory =
                Path.Combine(root, "restriction-stage");
            var tileWriteCount = 0;
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(workingDirectory) with
                {
                    TileWrittenObserver =
                        _ => Interlocked.Increment(ref tileWriteCount),
                };

            InvalidDataException failure =
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => PooledRoadRestrictionStage.ApplyAsync(
                            tileDirectory,
                            destinationDirectory,
                            semanticStore,
                            options,
                            TestContext.Current.CancellationToken)
                        .AsTask());

            Assert.Contains(
                "derived build ID",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, Volatile.Read(ref tileWriteCount));
            Assert.Equal(sourceBefore, HashTileTree(tileDirectory));
            Assert.False(Directory.Exists(destinationDirectory));
            Assert.False(Directory.Exists(workingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public void BoundedTilesetRestamper_CompensatingChecksumChangesFailExactPerTileIdentity()
    {
        string root = CreateRoot();
        try
        {
            string tileDirectory = Path.Combine(root, "tiles");
            string firstPath = WriteRestampFixtureTile(
                tileDirectory,
                new GraphId(1, 0, 0),
                lowHash: 100);
            string secondPath = WriteRestampFixtureTile(
                tileDirectory,
                new GraphId(2, 0, 0),
                lowHash: 200);
            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    tileDirectory,
                    maxTileCount: 2);
            var mutated = false;

            InvalidDataException failure =
                Assert.Throws<InvalidDataException>(
                    () => BoundedTilesetRestamper.Restamp(
                        tileDirectory,
                        catalog,
                        CancellationToken.None,
                        (pass, _) =>
                        {
                            if (pass != 2 || mutated)
                            {
                                return;
                            }

                            mutated = true;
                            RewriteTileLowHash(firstPath, 101);
                            RewriteTileLowHash(secondPath, 199);
                        }));

            Assert.True(mutated);
            Assert.Contains(
                "changed checksum",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal((ulong)101, ReadTileLowHash(firstPath));
            Assert.Equal((ulong)199, ReadTileLowHash(secondPath));
            Assert.Equal((ushort)0, ReadTileBuildId(firstPath));
            Assert.Equal((ushort)0, ReadTileBuildId(secondPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BoundedTilesetRestamper_SourceChangeBeforeWriteFailsWithoutStampingTile()
    {
        string root = CreateRoot();
        try
        {
            string tileDirectory = Path.Combine(root, "tiles");
            string tilePath = WriteRestampFixtureTile(
                tileDirectory,
                new GraphId(1, 0, 0),
                lowHash: 400);
            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    tileDirectory,
                    maxTileCount: 1);
            var mutated = false;

            InvalidDataException failure =
                Assert.Throws<InvalidDataException>(
                    () => BoundedTilesetRestamper.Restamp(
                        tileDirectory,
                        catalog,
                        CancellationToken.None,
                        (pass, _) =>
                        {
                            if (pass != 3 || mutated)
                            {
                                return;
                            }

                            mutated = true;
                            RewriteTileLowHash(tilePath, 401);
                        }));

            Assert.True(mutated);
            Assert.Contains(
                "changed checksum",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal((ulong)401, ReadTileLowHash(tilePath));
            Assert.Equal((ushort)0, ReadTileBuildId(tilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BoundedTilesetRestamper_ValidationAndWriteShareExclusiveHandle()
    {
        string root = CreateRoot();
        try
        {
            string tileDirectory = Path.Combine(root, "tiles");
            string tilePath = WriteRestampFixtureTile(
                tileDirectory,
                new GraphId(1, 0, 0),
                lowHash: 600);
            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    tileDirectory,
                    maxTileCount: 1);
            var interleavingBlocked = false;

            ushort buildId = BoundedTilesetRestamper.Restamp(
                tileDirectory,
                catalog,
                CancellationToken.None,
                (pass, _) =>
                {
                    if (pass != 4)
                    {
                        return;
                    }

                    try
                    {
                        RewriteTileLowHash(tilePath, 601);
                    }
                    catch (IOException)
                    {
                        interleavingBlocked = true;
                    }
                });

            Assert.True(interleavingBlocked);
            Assert.Equal((ulong)600, ReadTileLowHash(tilePath));
            Assert.Equal(buildId, ReadTileBuildId(tilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BoundedTilesetRestamper_HashReservationIsStrict()
    {
        string root = CreateRoot();
        try
        {
            string tileDirectory = Path.Combine(root, "tiles");
            string tilePath = WriteRestampFixtureTile(
                tileDirectory,
                new GraphId(1, 0, 0),
                lowHash: 700);
            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    tileDirectory,
                    maxTileCount: 1);
            long required =
                BoundedTilesetRestamper.GetHashReservationBytes(1);

            Assert.Throws<ValhallaGenerationResourceLimitException>(
                () => BoundedTilesetRestamper.Restamp(
                    tileDirectory,
                    catalog,
                    CancellationToken.None,
                    hashMemoryBudgetBytes: required - 1));
            Assert.Equal((ushort)0, ReadTileBuildId(tilePath));

            ushort buildId = BoundedTilesetRestamper.Restamp(
                tileDirectory,
                catalog,
                CancellationToken.None,
                hashMemoryBudgetBytes: required);

            Assert.Equal(buildId, ReadTileBuildId(tilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }



    [Fact]
    public void ValidateReadableTileTree_LargeTileUsesBoundedIntegrityValidation()
    {
        string root = CreateRoot();
        try
        {
            const int tileLength = 1024 * 1024;
            var tileId = new GraphId(1, 0, 0);
            string tileDirectory = Path.Combine(root, "tiles");
            string tilePath = Path.Combine(
                tileDirectory,
                GraphTile.FileSuffix(tileId));
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);

            byte[] tileBytes = new byte[tileLength];
            var header = new GraphTileHeader();
            header.SetGraphid(tileId);
            header.SetComplexRestrictionForwardOffset(
                GraphTileHeader.HeaderSize);
            header.SetComplexRestrictionReverseOffset(
                GraphTileHeader.HeaderSize);
            header.SetEdgeinfoOffset(GraphTileHeader.HeaderSize);
            header.SetTextlistOffset(GraphTileHeader.HeaderSize);
            header.SetLaneConnectivityOffset(tileLength);
            header.SetPredictedspeedsOffset(tileLength);
            header.SetEndOffset(tileLength);
            header.SetRawChecksum(
                GraphTileChecksum.ComputeTileHash(
                    tileBytes.AsSpan(GraphTileHeader.HeaderSize)));
            header.AsSpan().CopyTo(tileBytes);
            File.WriteAllBytes(tilePath, tileBytes);

            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    tileDirectory,
                    maxTileCount: 1);

            long allocationStart =
                GC.GetAllocatedBytesForCurrentThread();
            PooledRoadRestrictionStage.ValidateReadableTileTree(
                tileDirectory,
                catalog,
                CancellationToken.None);
            long allocatedBytes =
                GC.GetAllocatedBytesForCurrentThread() -
                allocationStart;

            Assert.True(
                tileLength >
                PooledRoadRestrictionStage.ValidationMemoryBytes);
            Assert.InRange(
                allocatedBytes,
                0,
                PooledRoadRestrictionStage.ValidationMemoryBytes * 4L);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidateReadableTileTree_DerivedBuildIdMismatchFailsClosed()
    {
        string root = CreateRoot();
        try
        {
            const int TileLength = 4096;
            var tileId = new GraphId(1, 0, 0);
            string tileDirectory = Path.Combine(root, "tiles");
            string tilePath = Path.Combine(
                tileDirectory,
                GraphTile.FileSuffix(tileId));
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);

            byte[] tileBytes = new byte[TileLength];
            var header = new GraphTileHeader();
            header.SetGraphid(tileId);
            header.SetComplexRestrictionForwardOffset(
                GraphTileHeader.HeaderSize);
            header.SetComplexRestrictionReverseOffset(
                GraphTileHeader.HeaderSize);
            header.SetEdgeinfoOffset(GraphTileHeader.HeaderSize);
            header.SetTextlistOffset(GraphTileHeader.HeaderSize);
            header.SetLaneConnectivityOffset(TileLength);
            header.SetEndOffset(TileLength);
            ulong tileHash = GraphTileChecksum.ComputeTileHash(
                tileBytes.AsSpan(GraphTileHeader.HeaderSize));
            ushort incorrectBuildId = unchecked((ushort)(
                GraphTileChecksum.ComputeTilesetBuildId([tileHash]) + 1));
            header.SetRawChecksum(
                ((ulong)incorrectBuildId << GraphTileHeader.TileHashBits) |
                tileHash);
            header.AsSpan().CopyTo(tileBytes);
            File.WriteAllBytes(tilePath, tileBytes);

            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    tileDirectory,
                    maxTileCount: 1);

            InvalidDataException failure =
                Assert.Throws<InvalidDataException>(
                    () => PooledRoadRestrictionStage
                        .ValidateReadableTileTree(
                            tileDirectory,
                            catalog,
                            CancellationToken.None,
                            requireDerivedBuildId: true));

            Assert.Contains(
                "derived build ID",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidateReadableTileTree_CorruptedBodyFailsChecksumValidation()
    {
        string root = CreateRoot();
        try
        {
            const int tileLength = 4096;
            var tileId = new GraphId(2, 0, 0);
            string tileDirectory = Path.Combine(root, "tiles");
            string tilePath = Path.Combine(
                tileDirectory,
                GraphTile.FileSuffix(tileId));
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);

            byte[] tileBytes = new byte[tileLength];
            var header = new GraphTileHeader();
            header.SetGraphid(tileId);
            header.SetComplexRestrictionForwardOffset(
                GraphTileHeader.HeaderSize);
            header.SetComplexRestrictionReverseOffset(
                GraphTileHeader.HeaderSize);
            header.SetEdgeinfoOffset(GraphTileHeader.HeaderSize);
            header.SetTextlistOffset(GraphTileHeader.HeaderSize);
            header.SetLaneConnectivityOffset(tileLength);
            header.SetEndOffset(tileLength);
            header.SetRawChecksum(
                GraphTileChecksum.ComputeTileHash(
                    tileBytes.AsSpan(GraphTileHeader.HeaderSize)));
            header.AsSpan().CopyTo(tileBytes);
            File.WriteAllBytes(tilePath, tileBytes);
            using (FileStream tile = File.OpenWrite(tilePath))
            {
                tile.Position = tileLength - 1;
                tile.WriteByte(1);
            }

            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    tileDirectory,
                    maxTileCount: 1);

            InvalidDataException failure =
                Assert.Throws<InvalidDataException>(
                    () => PooledRoadRestrictionStage
                        .ValidateReadableTileTree(
                            tileDirectory,
                            catalog,
                            CancellationToken.None));

            Assert.Contains(
                "checksum",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidateReadableTileTree_ImpossibleFixedSectionCountsFail()
    {
        string root = CreateRoot();
        try
        {
            const int tileLength = 4096;
            var tileId = new GraphId(3, 0, 0);
            string tileDirectory = Path.Combine(root, "tiles");
            string tilePath = Path.Combine(
                tileDirectory,
                GraphTile.FileSuffix(tileId));
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);

            byte[] tileBytes = new byte[tileLength];
            var header = new GraphTileHeader();
            header.SetGraphid(tileId);
            header.SetNodecount(1000);
            header.SetComplexRestrictionForwardOffset(
                GraphTileHeader.HeaderSize);
            header.SetComplexRestrictionReverseOffset(
                GraphTileHeader.HeaderSize);
            header.SetEdgeinfoOffset(GraphTileHeader.HeaderSize);
            header.SetTextlistOffset(GraphTileHeader.HeaderSize);
            header.SetLaneConnectivityOffset(tileLength);
            header.SetEndOffset(tileLength);
            header.SetRawChecksum(
                GraphTileChecksum.ComputeTileHash(
                    tileBytes.AsSpan(GraphTileHeader.HeaderSize)));
            header.AsSpan().CopyTo(tileBytes);
            File.WriteAllBytes(tilePath, tileBytes);

            BoundedRestrictionTileCatalog catalog =
                BoundedRestrictionTileCatalog.Build(
                    tileDirectory,
                    maxTileCount: 1);

            InvalidDataException failure =
                Assert.Throws<InvalidDataException>(
                    () => PooledRoadRestrictionStage
                        .ValidateReadableTileTree(
                            tileDirectory,
                            catalog,
                            CancellationToken.None));

            Assert.Contains(
                "fixed sections",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_LocalRestrictionsDoNotConsumeDeferredCrossTileCapacity()
    {
        string root = CreateRoot();
        try
        {
            const int restrictionCount = 200;
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ManyLocalRestrictionsRoadSource(restrictionCount),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            string restrictedTileDirectory =
                Path.Combine(root, "restricted-tiles");
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(
                    Path.Combine(root, "restriction-stage")) with
                {
                    MemoryBudgetBytes = 32 * 1024 * 1024,
                };

            PooledRoadRestrictionStageReceipt receipt =
                await PooledRoadRestrictionStage.ApplyAsync(
                    tileDirectory,
                    restrictedTileDirectory,
                    semanticStore,
                    options,
                    TestContext.Current.CancellationToken);

            Assert.Equal(restrictionCount, receipt.ProjectedForwardCount);
            Assert.Equal(0U, receipt.SerializedCrossTileForwardCount);
            Assert.Equal(
                (uint)restrictionCount,
                receipt.SerializedForwardCount);
            Assert.Equal(
                options.MemoryBudgetBytes,
                receipt.SequenceMemoryBudgetBytes +
                receipt.ReaderCacheBudgetBytes +
                receipt.BookkeepingMemoryBudgetBytes +
                receipt.RestampHashMemoryBudgetBytes +
                receipt.MutationMemoryBudgetBytes +
                receipt.CopyBufferBudgetBytes +
                receipt.SourceManifestMemoryBudgetBytes +
                receipt.ValidationMemoryBudgetBytes);
            Assert.InRange(
                receipt.PeakTileMutationAllocatedBytes,
                1,
                receipt.MutationMemoryBudgetBytes);
            Assert.InRange(
                receipt.CopyBufferBudgetBytes,
                4 * 1024,
                64 * 1024);
            Assert.Equal(
                PooledRoadRestrictionStage.ValidationMemoryBytes,
                receipt.ValidationMemoryBudgetBytes);
            Assert.Equal(
                BoundedTilesetRestamper.GetHashReservationBytes(1),
                receipt.RestampHashMemoryBudgetBytes);

            Assert.Equal(
                options.ScratchDiskBudgetBytes,
                receipt.StagedScratchBytes +
                receipt.ProjectionScratchBudgetBytes +
                receipt.MutationPlanScratchBudgetBytes);
            Assert.InRange(
                receipt.PeakMutationPlanMemoryBytes,
                1,
                receipt.MutationMemoryBudgetBytes);
            Assert.InRange(
                receipt.PeakMutationPlanScratchBytes,
                1,
                receipt.MutationPlanScratchBudgetBytes);
            Assert.InRange(
                receipt.PeakAggregateStageMemoryBytes,
                1,
                options.MemoryBudgetBytes);
            Assert.InRange(
                receipt.PeakAggregateStageScratchBytes,
                1,
                options.ScratchDiskBudgetBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_MutationBudgetTooSmallFailsBeforeTileWriteAndPreservesSource()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            IReadOnlyDictionary<string, string> before =
                HashTileTree(tileDirectory);
            string destinationDirectory =
                Path.Combine(root, "restricted-tiles");
            int tileWrites = 0;
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(
                    Path.Combine(root, "restriction-stage")) with
                {
                    MemoryBudgetBytes = 8 * 1024 * 1024,
                    MutationMemoryBudgetBytesOverride = 64 * 1024,
                    TileWrittenObserver = _ => tileWrites++,
                };

            ValhallaGenerationResourceLimitException failure =
                await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                    () => PooledRoadRestrictionStage.ApplyAsync(
                            tileDirectory,
                            destinationDirectory,
                            semanticStore,
                            options,
                            TestContext.Current.CancellationToken)
                        .AsTask());

            Assert.Contains(
                "reservation",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, tileWrites);
            Assert.Equal(before, HashTileTree(tileDirectory));
            Assert.False(Directory.Exists(destinationDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task ApplyAsync_TinyBudgetRejectsBeforeManifestConstruction()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string sourceDirectory = Path.Combine(root, "tiles");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(Path.Combine(sourceDirectory, "input.bin"), [1]);
            bool manifestStarted = false;
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(Path.Combine(root, "restriction-stage")) with
                {
                    MemoryBudgetBytes = 1024,
                    SourceHashProgressObserver = (_, _) =>
                        manifestStarted = true,
                };

            await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                () => PooledRoadRestrictionStage.ApplyAsync(
                        sourceDirectory,
                        Path.Combine(root, "restricted-tiles"),
                        semanticStore,
                        options,
                        TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.False(manifestStarted);
            Assert.False(Directory.Exists(options.WorkingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(64 * 1024)]
    [InlineData((64 * 1024) + 4095)]
    public async Task ApplyAsync_ManifestThresholdBudgetRejectsBeforeHash(
        long memoryBudgetBytes)
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string sourceDirectory = Path.Combine(root, "tiles");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(Path.Combine(sourceDirectory, "input.bin"), [1]);
            bool manifestHashStarted = false;
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(Path.Combine(root, "restriction-stage")) with
                {
                    MemoryBudgetBytes = memoryBudgetBytes,
                    SourceHashProgressObserver = (_, _) =>
                        manifestHashStarted = true,
                };

            await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                () => PooledRoadRestrictionStage.ApplyAsync(
                        sourceDirectory,
                        Path.Combine(root, "restricted-tiles"),
                        semanticStore,
                        options,
                        TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.False(manifestHashStarted);
            Assert.False(Directory.Exists(options.WorkingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_CancellationDuringSourceHashStopsBeforeClone()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string sourceDirectory = Path.Combine(root, "tiles");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(
                Path.Combine(sourceDirectory, "large.bin"),
                new byte[256 * 1024]);
            using var cancellation = new CancellationTokenSource();
            long observedBytes = 0;
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(Path.Combine(root, "restriction-stage")) with
                {
                    SourceHashProgressObserver = (_, bytes) =>
                    {
                        observedBytes = bytes;
                        cancellation.Cancel();
                    },
                };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PooledRoadRestrictionStage.ApplyAsync(
                        sourceDirectory,
                        Path.Combine(root, "restricted-tiles"),
                        semanticStore,
                        options,
                        cancellation.Token)
                    .AsTask());

            Assert.InRange(observedBytes, 1, 4 * 1024);
            Assert.False(Directory.Exists(
                Path.Combine(root, "restricted-tiles")));
            Assert.False(Directory.Exists(options.WorkingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_CancellationDuringSourcePathRevalidationStopsBeforeTileValidation()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string sourceDirectory = Path.Combine(root, "tiles");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(Path.Combine(sourceDirectory, "input.bin"), [1]);
            using var cancellation = new CancellationTokenSource();
            int validatedPaths = 0;
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(Path.Combine(root, "restriction-stage")) with
                {
                    SourceManifestPathValidatedObserver = _ =>
                    {
                        validatedPaths++;
                        cancellation.Cancel();
                    },
                };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PooledRoadRestrictionStage.ApplyAsync(
                        sourceDirectory,
                        Path.Combine(root, "restricted-tiles"),
                        semanticStore,
                        options,
                        cancellation.Token)
                    .AsTask());

            Assert.Equal(1, validatedPaths);
            Assert.False(Directory.Exists(
                Path.Combine(root, "restricted-tiles")));
            Assert.False(Directory.Exists(options.WorkingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_LargeSourcePathSetUsesLogarithmicManifestLookup()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string sourceDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    sourceDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            const int extraFileCount = 1023;
            for (int index = 0; index < extraFileCount; index++)
            {
                File.WriteAllBytes(
                    Path.Combine(
                        sourceDirectory,
                        $"lookup-{index:D4}.bin"),
                    [1]);
            }

            int maximumComparisons = 0;
            int observedFiles = 0;
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(Path.Combine(root, "restriction-stage")) with
                {
                    SourceManifestPathLookupObserver = (_, comparisons) =>
                    {
                        observedFiles++;
                        maximumComparisons = Math.Max(
                            maximumComparisons,
                            comparisons);
                    },
                };

            await PooledRoadRestrictionStage.ApplyAsync(
                sourceDirectory,
                Path.Combine(root, "restricted-tiles"),
                semanticStore,
                options,
                TestContext.Current.CancellationToken);

            int expectedFiles =
                Directory.EnumerateFiles(
                    sourceDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Count();
            int logarithmicMaximum =
                (int)Math.Ceiling(Math.Log2(expectedFiles + 1));
            Assert.Equal(expectedFiles, observedFiles);
            Assert.InRange(
                maximumComparisons,
                1,
                logarithmicMaximum);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public async Task ApplyAsync_OversizedSourceManifestFailsBeforeOperationDirectory()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string sourceDirectory = Path.Combine(root, "tiles");
            Directory.CreateDirectory(sourceDirectory);
            for (int index = 0; index < 400; index++)
            {
                string path = Path.Combine(
                    sourceDirectory,
                    $"manifest-entry-{index:D4}.bin");
                File.WriteAllBytes(path, [1]);
            }

            string destinationDirectory =
                Path.Combine(root, "restricted-tiles");
            string workingDirectory =
                Path.Combine(root, "restriction-stage");
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(workingDirectory) with
                {
                    MemoryBudgetBytes = 512 * 1024,
                };

            await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                () => PooledRoadRestrictionStage.ApplyAsync(
                        sourceDirectory,
                        destinationDirectory,
                        semanticStore,
                        options,
                        TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.False(Directory.Exists(destinationDirectory));
            Assert.False(Directory.Exists(workingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }



    [Theory]
    [InlineData("grow")]
    [InlineData("new-file")]
    [InlineData("same-length-replace")]
    [InlineData("reparse")]
    public async Task ApplyAsync_SourceManifestAttackFailsBeforeDestinationPublication(
        string attack)
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new ComplexRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            string tilePath = Directory.EnumerateFiles(
                    tileDirectory,
                    "*.gph",
                    SearchOption.AllDirectories)
                .First();
            string destinationDirectory =
                Path.Combine(root, "restricted-tiles");
            string injectedPath = Path.Combine(
                tileDirectory,
                attack == "new-file" ? "injected.bin" : "linked.gph");
            string? expectedTileHash = null;

            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(Path.Combine(root, "restriction-stage"));
            if (attack == "reparse")
            {
                string targetPath = Path.Combine(root, "link-target.bin");
                File.WriteAllBytes(targetPath, [1, 2, 3, 4]);
                File.CreateSymbolicLink(injectedPath, targetPath);
            }
            else
            {
                options = options with
                {
                    SourceManifestCreatedObserver = _ =>
                    {
                        if (attack == "grow")
                        {
                            using FileStream stream = new(
                                tilePath,
                                FileMode.Append,
                                FileAccess.Write,
                                FileShare.Read);
                            stream.WriteByte(0x5A);
                            stream.Flush(flushToDisk: true);
                            using FileStream hashStream = new(
                                tilePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite);
                            expectedTileHash = Convert.ToHexString(
                                SHA256.HashData(hashStream));
                        }
                        else if (attack == "same-length-replace")
                        {
                            byte[] replacement = File.ReadAllBytes(tilePath);
                            replacement[^1] ^= 0x5A;
                            File.WriteAllBytes(tilePath, replacement);
                            expectedTileHash = Convert.ToHexString(
                                SHA256.HashData(replacement));
                        }
                        else
                        {
                            File.WriteAllBytes(injectedPath, [5, 6, 7, 8]);
                        }
                    },
                };
            }

            await Assert.ThrowsAsync<InvalidDataException>(
                () => PooledRoadRestrictionStage.ApplyAsync(
                        tileDirectory,
                        destinationDirectory,
                        semanticStore,
                        options,
                        TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.False(Directory.Exists(destinationDirectory));
            if (attack is "grow" or "same-length-replace")
            {
                using FileStream hashStream = new(
                    tilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                Assert.Equal(
                    expectedTileHash,
                    Convert.ToHexString(SHA256.HashData(hashStream)));
            }
            else
            {
                Assert.True(File.Exists(injectedPath));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public async Task ApplyAsync_CancellationAfterFirstStagedTileLeavesOriginalGenerationUnchanged()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new CrossTileOnlyRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            IReadOnlyDictionary<string, string> before =
                HashTileTree(tileDirectory);
            string restrictedTileDirectory =
                Path.Combine(root, "restricted-tiles");
            using var cancellation = new CancellationTokenSource();
            int tileWrites = 0;
            string workingDirectory =
                Path.Combine(root, "restriction-stage");
            Directory.CreateDirectory(workingDirectory);
            string sentinelPath =
                Path.Combine(workingDirectory, "sentinel.txt");
            File.WriteAllText(sentinelPath, "preserve");
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(workingDirectory) with
                {
                    TileWrittenObserver = _ =>
                    {
                        if (Interlocked.Increment(ref tileWrites) == 1)
                        {
                            cancellation.Cancel();
                        }
                    },
                };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PooledRoadRestrictionStage.ApplyAsync(
                        tileDirectory,
                        restrictedTileDirectory,
                        semanticStore,
                        options,
                        cancellation.Token)
                    .AsTask());

            Assert.Equal(1, tileWrites);
            Assert.Equal(before, HashTileTree(tileDirectory));
            Assert.False(Directory.Exists(restrictedTileDirectory));
            Assert.Equal("preserve", File.ReadAllText(sentinelPath));
            Assert.Empty(Directory.EnumerateDirectories(workingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_InjectedTileAfterCatalogCreationFailsClosed()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new CrossTileOnlyRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            IReadOnlyDictionary<string, string> sourceBefore =
                HashTileTree(tileDirectory);
            string destinationDirectory =
                Path.Combine(root, "restricted-tiles");
            string workingDirectory =
                Path.Combine(root, "restriction-stage");
            bool injected = false;
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(workingDirectory) with
                {
                    TileWrittenObserver = _ =>
                    {
                        if (injected)
                        {
                            return;
                        }

                        string operationDirectory =
                            Directory.EnumerateDirectories(
                                workingDirectory,
                                "pooled-restrictions-*")
                            .Single();
                        WriteRestampFixtureTile(
                            Path.Combine(operationDirectory, "incoming"),
                            new GraphId(999999U, 2, 0),
                            123UL);
                        injected = true;
                    },
                };

            InvalidDataException failure =
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => PooledRoadRestrictionStage.ApplyAsync(
                            tileDirectory,
                            destinationDirectory,
                            semanticStore,
                            options,
                            TestContext.Current.CancellationToken)
                        .AsTask());

            Assert.True(injected);
            Assert.Contains(
                "tile cohort changed",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourceBefore, HashTileTree(tileDirectory));
            Assert.False(Directory.Exists(destinationDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public async Task ApplyAsync_InjectedTileAtPublicationBoundaryFailsClosed()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new CrossTileOnlyRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            IReadOnlyDictionary<string, string> sourceBefore =
                HashTileTree(tileDirectory);
            string destinationDirectory =
                Path.Combine(root, "restricted-tiles");
            PooledRoadRestrictionStageOptions options =
                RestrictionOptions(Path.Combine(root, "restriction-stage")) with
                {
                    BeforeFinalCohortSealObserver = incomingDirectory =>
                        WriteRestampFixtureTile(
                            incomingDirectory,
                            new GraphId(999998U, 2, 0),
                            456UL),
                };

            InvalidDataException failure =
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => PooledRoadRestrictionStage.ApplyAsync(
                            tileDirectory,
                            destinationDirectory,
                            semanticStore,
                            options,
                            TestContext.Current.CancellationToken)
                        .AsTask());

            Assert.Contains(
                "publication boundary",
                failure.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourceBefore, HashTileTree(tileDirectory));
            Assert.False(Directory.Exists(destinationDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_CrossTileOnlyRestrictionPreservesPayloadsMarkersAndReceiptCounts()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new CrossTileOnlyRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string tileDirectory = Path.Combine(root, "tiles");
            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    tileDirectory,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);
            IReadOnlyDictionary<string, string> sourceBefore =
                HashTileTree(tileDirectory);

            GraphId fromNode = FindGraphId(graph, 10);
            GraphId viaStartNode = FindGraphId(graph, 11);
            GraphId viaEndNode = FindGraphId(graph, 12);
            GraphId allowedEndNode = FindGraphId(graph, 13);
            Assert.NotEqual(
                fromNode.TileBase(),
                allowedEndNode.TileBase());
            GraphId viaEdge = FindDirectedEdgeId(
                tileDirectory,
                viaStartNode,
                wayId: 21,
                endNode: viaEndNode);
            string restrictedTileDirectory =
                Path.Combine(root, "restricted-tiles");
            string workingDirectory =
                Path.Combine(root, "restriction-stage");
            Directory.CreateDirectory(workingDirectory);
            string sentinelPath =
                Path.Combine(workingDirectory, "sentinel.txt");
            File.WriteAllText(sentinelPath, "preserve");

            PooledRoadRestrictionStageReceipt receipt =
                await PooledRoadRestrictionStage.ApplyAsync(
                    tileDirectory,
                    restrictedTileDirectory,
                    semanticStore,
                    RestrictionOptions(
                        Path.Combine(root, "restriction-stage")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(sourceBefore, HashTileTree(tileDirectory));
            Assert.Equal("preserve", File.ReadAllText(sentinelPath));
            Assert.True(Directory.Exists(restrictedTileDirectory));
            var freshReader = new GraphReader(
                new GraphReader.Config
                {
                    TileDir = restrictedTileDirectory,
                    MaxCacheSize = 1024 * 1024,
                    UseLruMemCache = true,
                    LruMemCacheHardControl = true,
                });
            (uint Forward, uint Reverse) actual =
                CountSerializedRestrictions(freshReader);
            Assert.True(actual.Forward > 0);
            Assert.Equal(
                actual.Forward,
                receipt.SerializedForwardCount);
            Assert.Equal(
                actual.Reverse,
                receipt.SerializedReverseCount);

            GraphTile viaTile =
                freshReader.GetGraphTile(viaEdge) ??
                throw new InvalidDataException(
                    $"Tile for edge {viaEdge} was not readable.");
            Assert.True(
                viaTile.DirectedEdge((int)viaEdge.Id())
                    .PartOfComplexRestriction);
            Assert.Equal(0U, receipt.SerializedCrossTileForwardCount);
            Assert.Equal(1U, receipt.MarkedCrossTileEdgeCount);
            Assert.Equal(0U, receipt.MissingCrossTileDestinationCount);

            var checksums = new List<ulong>();
            foreach (GraphId tileId in freshReader.GetTileSet())
            {
                GraphTile tile =
                    freshReader.GetGraphTile(tileId) ??
                    throw new InvalidDataException(
                        $"Tile {tileId} was not readable.");
                checksums.Add(tile.Header().TileChecksum());
                Assert.Equal(
                    receipt.TilesetBuildId,
                    tile.Header().BuildId());
            }

            Assert.Equal(
                GraphTileChecksum.ComputeTilesetBuildId(checksums),
                receipt.TilesetBuildId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (uint Forward, uint Reverse)
        CountSerializedRestrictions(GraphReader reader)
    {
        uint forward = 0;
        uint reverse = 0;
        foreach (GraphId tileId in reader.GetTileSet())
        {
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            for (uint edgeIndex = 0;
                 edgeIndex < tile.DirectedEdgeCount();
                 edgeIndex++)
            {
                GraphId edgeId = new(
                    tileId.Tileid(),
                    tileId.Level(),
                    edgeIndex);
                foreach (var unused in tile
                             .GetComplexRestrictions(
                                 forward: true,
                                 edgeId,
                                 GraphConstants.AutoAccess)
                             .WithVias())
                {
                    _ = unused;
                    forward++;
                }

                foreach (var unused in tile
                             .GetComplexRestrictions(
                                 forward: false,
                                 edgeId,
                                 GraphConstants.AutoAccess)
                             .WithVias())
                {
                    _ = unused;
                    reverse++;
                }
            }
        }

        return (forward, reverse);
    }

    private static void CopyTileTree(
        string sourceDirectory,
        string destinationDirectory)
    {
        foreach (string sourcePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(
                sourceDirectory,
                sourcePath);
            string destinationPath = Path.Combine(
                destinationDirectory,
                relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)!);
            File.Copy(
                sourcePath,
                destinationPath,
                overwrite: false);
        }
    }

    private static IReadOnlyDictionary<string, string> HashTileTree(
        string tileDirectory)
    {
        var result = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(
                     tileDirectory,
                     "*.gph",
                     SearchOption.AllDirectories))
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            result[Path.GetRelativePath(tileDirectory, path)] =
                Convert.ToHexString(SHA256.HashData(stream));
        }

        return result;
    }

    private static PathLocation CreatePathLocation(
        PointLL point,
        GraphId edgeId,
        double percentAlong)
    {
        var location = new PathLocation(
            new Location(point)
            {
                Radius = 50,
            });
        location.Edges.Add(
            new PathLocation.PathEdge(
                edgeId,
                percentAlong,
                point,
                0));
        return location;
    }

    private static void RewriteTileBuildId(
        string tilePath,
        ushort buildId)
    {
        byte[] tileBytes = File.ReadAllBytes(tilePath);
        GraphTileHeader header = GraphTileHeader.FromBytes(tileBytes);
        ulong lowHash =
            header.TileChecksum() & GraphTileHeader.TileHashMask;
        header.SetRawChecksum(
            ((ulong)buildId << GraphTileHeader.TileHashBits) | lowHash);
        header.AsSpan().CopyTo(tileBytes);
        File.WriteAllBytes(tilePath, tileBytes);
    }


    private static AutoCost MakeAutoCosting(
        bool ignoreRestrictions = false)
    {
        var costing = new Costing
        {
            CostingType = Costing.Type.Auto,
        };
        costing.Options.TopSpeed =
            (int)GraphConstants.MaxAssumedSpeed;
        costing.Options.IgnoreRestrictions =
            ignoreRestrictions;
        return new AutoCost(costing);
    }

    private static ModeCosting MakeModeCosting(
        AutoCost costing)
    {
        var modeCosting = new ModeCosting();
        modeCosting[(int)costing.TravelMode()] = costing;
        return modeCosting;
    }

    private static string WriteRestampFixtureTile(
        string tileDirectory,
        GraphId tileId,
        ulong lowHash)
    {
        string tilePath = Path.Combine(
            tileDirectory,
            GraphTile.FileSuffix(tileId));
        Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);
        byte[] tileBytes = new byte[GraphTileHeader.HeaderSize];
        var header = new GraphTileHeader();
        header.SetGraphid(tileId);
        header.SetComplexRestrictionForwardOffset(
            GraphTileHeader.HeaderSize);
        header.SetComplexRestrictionReverseOffset(
            GraphTileHeader.HeaderSize);
        header.SetEdgeinfoOffset(GraphTileHeader.HeaderSize);
        header.SetTextlistOffset(GraphTileHeader.HeaderSize);
        header.SetLaneConnectivityOffset(GraphTileHeader.HeaderSize);
        header.SetEndOffset(GraphTileHeader.HeaderSize);
        header.SetRawChecksum(lowHash & GraphTileHeader.TileHashMask);
        header.AsSpan().CopyTo(tileBytes);
        File.WriteAllBytes(tilePath, tileBytes);
        return tilePath;
    }

    private static void RewriteTileLowHash(
        string tilePath,
        ulong lowHash)
    {
        byte[] tileBytes = File.ReadAllBytes(tilePath);
        GraphTileHeader header = GraphTileHeader.FromBytes(tileBytes);
        header.SetRawChecksum(lowHash & GraphTileHeader.TileHashMask);
        header.AsSpan().CopyTo(tileBytes);
        File.WriteAllBytes(tilePath, tileBytes);
    }

    private static ulong ReadTileLowHash(string tilePath)
    {
        byte[] headerBytes =
            File.ReadAllBytes(tilePath)[..GraphTileHeader.HeaderSize];
        return GraphTileHeader
            .FromBytes(headerBytes)
            .TileChecksum() &
            GraphTileHeader.TileHashMask;
    }

    private static ushort ReadTileBuildId(string tilePath)
    {
        byte[] headerBytes =
            File.ReadAllBytes(tilePath)[..GraphTileHeader.HeaderSize];
        return GraphTileHeader.FromBytes(headerBytes).BuildId();
    }


    private static (
        ComplexRestriction Restriction,
        IReadOnlyList<GraphId> Vias) GetFirst(
        ComplexRestrictionView view)
    {
        foreach ((
                     ComplexRestriction Restriction,
                     IReadOnlyList<GraphId> Vias) entry in view.WithVias())
        {
            return entry;
        }

        throw new InvalidDataException("Complex restriction view was empty.");
    }

    private static GraphId FindDirectedEdgeId(
        string tileDirectory,
        GraphId startNode,
        ulong wayId,
        GraphId endNode)
    {
        GraphTile tile = GraphTile.Create(
                tileDirectory,
                startNode.TileBase()) ??
            throw new InvalidDataException(
                $"Graph tile {startNode.TileBase()} was not written.");
        NodeInfo node = tile.Node(startNode);
        for (uint localIndex = 0;
             localIndex < node.EdgeCount;
             localIndex++)
        {
            uint directedEdgeIndex = node.EdgeIndex + localIndex;
            DirectedEdge edge = tile.DirectedEdge(
                checked((int)directedEdgeIndex));
            if (edge.EndNode == endNode &&
                tile.EdgeInfo(edge).WayId == wayId)
            {
                return new GraphId(
                    startNode.Tileid(),
                    startNode.Level(),
                    directedEdgeIndex);
            }
        }

        throw new InvalidDataException(
            $"Way {wayId} from {startNode} to {endNode} was not written.");
    }

    private static GraphId FindGraphId(
        PooledRoadEdgeBuildResult graph,
        long osmNodeId)
    {
        for (long ordinal = 0;
             ordinal < graph.IdentityCount;
             ordinal++)
        {
            StableGraphNodeIdentity identity = graph.ReadIdentity(ordinal);
            if (identity.OsmNodeId == osmNodeId)
            {
                return identity.GraphId;
            }
        }

        throw new InvalidDataException(
            $"OSM node {osmNodeId} has no stable graph identity.");
    }

    private static CompactOsmSemanticStoreOptions SemanticOptions(
        string path) =>
        new(
            path,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 8 * 1024 * 1024,
            ScratchDiskBudgetBytes: 32 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static PooledRoadEdgeBuilderOptions BuilderOptions(
        string path) =>
        new(
            path,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 16 * 1024 * 1024,
            ScratchDiskBudgetBytes: 64 * 1024 * 1024,
            GridDivisions: 8,
            ArenaSlabCapacity: 8,
            ShapeBufferSizeBytes: 4096,
            SegmentSizeBytes: 64 * 1024);

    private static PooledRoadRestrictionStageOptions RestrictionOptions(
        string path) =>
        new(
            path,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 8 * 1024 * 1024,
            ScratchDiskBudgetBytes: 32 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-pooled-road-restriction-stage-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class CancelOnFirstReadRestrictionList :
        IReadOnlyList<OSMRestriction>
    {
        private readonly IReadOnlyList<OSMRestriction> inner;
        private readonly CancellationTokenSource cancellation;
        private int readCount;

        internal CancelOnFirstReadRestrictionList(
            IReadOnlyList<OSMRestriction> inner,
            CancellationTokenSource cancellation)
        {
            this.inner = inner;
            this.cancellation = cancellation;
        }

        public int Count => inner.Count;

        public OSMRestriction this[int index]
        {
            get
            {
                OSMRestriction restriction = inner[index];
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    cancellation.Cancel();
                }

                return restriction;
            }
        }

        public IEnumerator<OSMRestriction> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class CrossTileOnlyRestrictionRoadSource :
        IOsmPbfEntitySource
    {
        public int FileCount => 1;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, fileOrdinal);
            cancellationToken.ThrowIfCancellationRequested();
            if (pass == OsmPbfEntityPass.Ways)
            {
                visitor.Way(20, [10UL, 11UL], RoadTags());
                visitor.Way(21, [11UL, 12UL], RoadTags());
                visitor.Way(22, [12UL, 13UL], RoadTags());
                visitor.Way(23, [12UL, 14UL], RoadTags());
                visitor.Way(24, [13UL, 15UL], RoadTags());
                visitor.Way(25, [14UL, 16UL], RoadTags());
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                visitor.Relation(
                    40,
                    [
                        new OsmRelationMember(
                            20,
                            OsmMemberType.Way,
                            "from"),
                        new OsmRelationMember(
                            21,
                            OsmMemberType.Way,
                            "via"),
                        new OsmRelationMember(
                            22,
                            OsmMemberType.Way,
                            "to"),
                    ],
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["type"] = "restriction",
                        ["restriction"] = "only_straight_on",
                    });
                return;
            }

            if (pass != OsmPbfEntityPass.Nodes)
            {
                return;
            }

            visitor.Node(
                10,
                36.1000,
                -86.7515,
                EmptyTags());
            visitor.Node(
                11,
                36.1000,
                -86.7505,
                EmptyTags());
            visitor.Node(
                12,
                36.1000,
                -86.7495,
                EmptyTags());
            visitor.Node(
                13,
                36.1000,
                -86.7485,
                EmptyTags());
            visitor.Node(
                14,
                36.1010,
                -86.7495,
                EmptyTags());
            visitor.Node(
                15,
                36.1000,
                -86.7475,
                EmptyTags());
            visitor.Node(
                16,
                36.1020,
                -86.7495,
                EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> RoadTags() =>
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["highway"] = "primary",
                ["oneway"] = "yes",
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(
                StringComparer.Ordinal);
    }

    private sealed class MultiViaRestrictionRoadSource :
        IOsmPbfEntitySource
    {
        public int FileCount => 1;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, fileOrdinal);
            cancellationToken.ThrowIfCancellationRequested();
            if (pass == OsmPbfEntityPass.Ways)
            {
                visitor.Way(20, [10UL, 11UL], RoadTags());
                visitor.Way(21, [11UL, 12UL], RoadTags());
                visitor.Way(22, [12UL, 13UL], RoadTags());
                visitor.Way(23, [13UL, 14UL], RoadTags());
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                visitor.Relation(
                    40,
                    [
                        new OsmRelationMember(
                            20,
                            OsmMemberType.Way,
                            "from"),
                        new OsmRelationMember(
                            21,
                            OsmMemberType.Way,
                            "via"),
                        new OsmRelationMember(
                            22,
                            OsmMemberType.Way,
                            "via"),
                        new OsmRelationMember(
                            23,
                            OsmMemberType.Way,
                            "to"),
                    ],
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["type"] = "restriction",
                        ["restriction"] = "no_turn",
                    });
                return;
            }

            if (pass != OsmPbfEntityPass.Nodes)
            {
                return;
            }

            visitor.Node(
                10,
                36.1000,
                -86.7030,
                EmptyTags());
            visitor.Node(
                11,
                36.1000,
                -86.7020,
                EmptyTags());
            visitor.Node(
                12,
                36.1000,
                -86.7010,
                EmptyTags());
            visitor.Node(
                13,
                36.1000,
                -86.7000,
                EmptyTags());
            visitor.Node(
                14,
                36.1000,
                -86.6990,
                EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> RoadTags() =>
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["highway"] = "primary",
                ["oneway"] = "yes",
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(
                StringComparer.Ordinal);
    }

    private sealed class ManyLocalRestrictionsRoadSource :
        IOsmPbfEntitySource
    {
        private readonly int restrictionCount;

        internal ManyLocalRestrictionsRoadSource(int restrictionCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                restrictionCount);
            this.restrictionCount = restrictionCount;
        }

        public int FileCount => 1;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, fileOrdinal);
            for (var index = 0; index < restrictionCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong nodeBase = checked(
                    10_000UL + ((ulong)index * 5UL));
                ulong wayBase = checked(
                    20_000UL + ((ulong)index * 5UL));
                if (pass == OsmPbfEntityPass.Ways)
                {
                    visitor.Way(
                        wayBase,
                        [nodeBase, nodeBase + 1],
                        RoadTags());
                    visitor.Way(
                        wayBase + 1,
                        [nodeBase + 1, nodeBase + 2],
                        RoadTags());
                    visitor.Way(
                        wayBase + 2,
                        [nodeBase + 2, nodeBase + 3],
                        RoadTags());
                    visitor.Way(
                        wayBase + 3,
                        [nodeBase + 3, nodeBase + 4],
                        RoadTags());
                    continue;
                }

                if (pass == OsmPbfEntityPass.Relations)
                {
                    visitor.Relation(
                        checked(30_000UL + (ulong)index),
                        [
                            new OsmRelationMember(
                                wayBase + 1,
                                OsmMemberType.Way,
                                "from"),
                            new OsmRelationMember(
                                wayBase + 2,
                                OsmMemberType.Way,
                                "via"),
                            new OsmRelationMember(
                                wayBase + 3,
                                OsmMemberType.Way,
                                "to"),
                        ],
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["type"] = "restriction",
                            ["restriction"] = "no_left_turn",
                        });
                    continue;
                }

                if (pass != OsmPbfEntityPass.Nodes)
                {
                    continue;
                }

                double longitude =
                    -86.7000 + (index * 0.00001);
                for (var offset = 0; offset < 5; offset++)
                {
                    visitor.Node(
                        nodeBase + (ulong)offset,
                        36.1000 + (offset * 0.000001),
                        longitude + (offset * 0.000001),
                        EmptyTags());
                }
            }
        }

        private static IReadOnlyDictionary<string, string> RoadTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = "primary",
                ["oneway"] = "yes",
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed class ComplexRestrictionRoadSource :
        IOsmPbfEntitySource
    {
        private readonly int restrictionCount;

        internal ComplexRestrictionRoadSource(int restrictionCount = 1)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                restrictionCount);
            this.restrictionCount = restrictionCount;
        }

        public int FileCount => 1;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, fileOrdinal);
            cancellationToken.ThrowIfCancellationRequested();
            if (pass == OsmPbfEntityPass.Ways)
            {
                visitor.Way(19, [9UL, 10UL], RoadTags());
                visitor.Way(20, [10UL, 11UL], RoadTags());
                visitor.Way(21, [11UL, 12UL], RoadTags());
                visitor.Way(22, [12UL, 13UL], RoadTags());
                visitor.Way(23, [13UL, 14UL], RoadTags());
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                for (var index = 0; index < restrictionCount; index++)
                {
                    visitor.Relation(
                        checked((ulong)(30 + index)),
                        [
                            new OsmRelationMember(
                                20,
                                OsmMemberType.Way,
                                "from"),
                            new OsmRelationMember(
                                21,
                                OsmMemberType.Way,
                                "via"),
                            new OsmRelationMember(
                                22,
                                OsmMemberType.Way,
                                "to"),
                        ],
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["type"] = "restriction",
                            ["restriction"] = "no_left_turn",
                        });
                }

                return;
            }

            if (pass != OsmPbfEntityPass.Nodes)
            {
                return;
            }

            visitor.Node(9, 36.1000, -86.7040, EmptyTags());
            visitor.Node(10, 36.1000, -86.7030, EmptyTags());
            visitor.Node(11, 36.1000, -86.7020, EmptyTags());
            visitor.Node(12, 36.1000, -86.7010, EmptyTags());
            visitor.Node(13, 36.1000, -86.7000, EmptyTags());
            visitor.Node(14, 36.1000, -86.6990, EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> RoadTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = "primary",
                ["oneway"] = "yes",
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
