using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Generation.TimeZones;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class BoundedRoadTileWriterTests
{
    [Fact]
    public async Task WriteAsync_EmitsOneValidatedTileAtATimeWithoutGlobalTileBytes()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new StraightRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);

            string output = Path.Combine(root, "tiles");
            BoundedRoadTileWriteReceipt receipt =
                await BoundedRoadTileWriter.WriteAsync(
                    semanticStore,
                    graph,
                    new BoundedRoadTileWriterOptions(
                        output,
                        MemoryBudgetBytes: 8 * 1024 * 1024,
                        MaxDegreeOfParallelism: 1),
                    TestContext.Current.CancellationToken);

            string tilePath = Assert.Single(
                Directory.EnumerateFiles(output, "*.gph", SearchOption.AllDirectories));
            GraphId tileId = GraphTile.GetTileId(tilePath);
            GraphTile? tile = GraphTile.Create(output, tileId);
            Assert.NotNull(tile);

            Assert.Equal(1, receipt.TileCount);
            Assert.Equal(1, receipt.PeakActiveTileBuilders);
            Assert.True(receipt.PeakWorkerMemoryBytes <= 8 * 1024 * 1024);
            Assert.Equal(4U, tile.Header().Nodecount());
            Assert.Equal(6U, tile.Header().Directededgecount());
            DirectedEdge[] directedEdges = Enumerable.Range(0, 6)
                .Select(tile.DirectedEdge)
                .ToArray();
            Assert.All(
                directedEdges,
                edge =>
                {
                    Assert.True(edge.EndNode.IsValid());
                    Assert.NotEqual(0U, edge.ForwardAccess);
                    Assert.Equal((byte)RoadClass.Primary, (byte)edge.Classification);
                });
            Assert.Contains(
                directedEdges,
                edge => edge.EndNode == FindGraphId(graph, 2) && edge.StopSign);
            Assert.Contains(
                directedEdges,
                edge => edge.EndNode == FindGraphId(graph, 3) && edge.YieldSign);
            Assert.Contains(
                directedEdges,
                edge => edge.EndNode == FindGraphId(graph, 4) && edge.TrafficSignal);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories),
                path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_TimeZoneDatabaseWritesPinnedRoadNodeIndex()
    {
        string root = CreateRoot();
        try
        {
            string shape = FindRepositoryArtifact(
                "tests",
                "SharpNinja.Valhalla.Generation.Tests",
                "Fixtures",
                "Timezone",
                "2026c-jamaica",
                "timezone-2026c-jamaica.shp");
            string database = Path.Combine(root, "tz_world.sqlite");
            await new ManagedTimeZoneDatabaseBuilder().BuildAsync(
                new TimeZoneDatabaseBuildRequest(
                    shape,
                    "2026c",
                    Path.Combine(root, "timezone-work"),
                    database,
                    64 * 1024 * 1024),
                TestContext.Current.CancellationToken);
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new JamaicaRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string output = Path.Combine(root, "tiles");

            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    output,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1)
                {
                    TimeZoneDatabasePath = database,
                },
                TestContext.Current.CancellationToken);

            string tilePath = Assert.Single(
                Directory.EnumerateFiles(output, "*.gph", SearchOption.AllDirectories));
            GraphTile tile = GraphTile.Create(output, GraphTile.GetTileId(tilePath)) ??
                throw new InvalidDataException("The road timezone tile was not readable.");
            Assert.All(
                Enumerable.Range(0, checked((int)tile.Header().Nodecount())),
                nodeIndex => Assert.Equal(88U, tile.Node(nodeIndex).Timezone()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_CancelledBuildCannotExposePartialTile()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new StraightRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string output = Path.Combine(root, "tiles");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await BoundedRoadTileWriter.WriteAsync(
                    semanticStore,
                    graph,
                    new BoundedRoadTileWriterOptions(
                        output,
                        MemoryBudgetBytes: 8 * 1024 * 1024,
                        MaxDegreeOfParallelism: 1),
                    cancellation.Token));

            Assert.Empty(
                Directory.Exists(output)
                    ? Directory.EnumerateFiles(
                        output,
                        "*.gph",
                        SearchOption.AllDirectories)
                    : []);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_CrossTileEdgePreservesStableEndpointAndLeavesTileFlag()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new CrossTileRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string output = Path.Combine(root, "tiles");

            BoundedRoadTileWriteReceipt receipt =
                await BoundedRoadTileWriter.WriteAsync(
                    semanticStore,
                    graph,
                    new BoundedRoadTileWriterOptions(
                        output,
                        MemoryBudgetBytes: 8 * 1024 * 1024,
                        MaxDegreeOfParallelism: 1),
                    TestContext.Current.CancellationToken);

            string[] tilePaths = Directory
                .EnumerateFiles(output, "*.gph", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(2, receipt.TileCount);
            Assert.Equal(2, tilePaths.Length);
            GraphId[] stableNodeIds = Enumerable.Range(0, checked((int)graph.IdentityCount))
                .Select(ordinal => graph.ReadIdentity(ordinal).GraphId)
                .ToArray();
            foreach (string tilePath in tilePaths)
            {
                GraphId tileId = GraphTile.GetTileId(tilePath);
                GraphTile? tile = GraphTile.Create(output, tileId);
                Assert.NotNull(tile);
                Assert.Equal(1U, tile.Header().Nodecount());
                Assert.Equal(1U, tile.Header().Directededgecount());
                DirectedEdge edge = tile.DirectedEdge(0);
                Assert.True(edge.LeavesTile);
                Assert.NotEqual(tileId.TileBase(), edge.EndNode.TileBase());
                Assert.Contains(edge.EndNode, stableNodeIds);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task WriteAsync_SimpleRestrictionUsesDurableViaAndLocalEdgeMask()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new SimpleRestrictionRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string output = Path.Combine(root, "tiles");

            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    output,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);

            string tilePath = Assert.Single(
                Directory.EnumerateFiles(output, "*.gph", SearchOption.AllDirectories));
            GraphTile? tile = GraphTile.Create(output, GraphTile.GetTileId(tilePath));
            Assert.NotNull(tile);
            GraphId inboundNodeId = FindGraphId(graph, 1);
            GraphId viaNodeId = FindGraphId(graph, 2);
            GraphId restrictedExitId = FindGraphId(graph, 3);

            NodeInfo inboundNode = tile.Node(checked((int)inboundNodeId.Id()));
            DirectedEdge incoming = Enumerable
                .Range(0, checked((int)inboundNode.EdgeCount))
                .Select(localIndex =>
                    tile.DirectedEdge(
                        checked((int)(inboundNode.EdgeIndex + (uint)localIndex))))
                .Single(edge => edge.EndNode == viaNodeId);

            NodeInfo viaNode = tile.Node(checked((int)viaNodeId.Id()));
            int restrictedLocalIndex = Enumerable
                .Range(0, checked((int)viaNode.EdgeCount))
                .Single(localIndex =>
                    tile.DirectedEdge(
                        checked((int)(viaNode.EdgeIndex + (uint)localIndex)))
                    .EndNode == restrictedExitId);

            Assert.Equal(1U << restrictedLocalIndex, incoming.Restrictions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_ComplexViaWayMarksStartEndAndViaEdges()
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
            string output = Path.Combine(root, "tiles");

            await BoundedRoadTileWriter.WriteAsync(
                semanticStore,
                graph,
                new BoundedRoadTileWriterOptions(
                    output,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    MaxDegreeOfParallelism: 1),
                TestContext.Current.CancellationToken);

            uint expectedModes =
                (uint)(GraphConstants.AutoAccess |
                       GraphConstants.MopedAccess |
                       GraphConstants.TaxiAccess |
                       GraphConstants.BusAccess |
                       GraphConstants.BicycleAccess |
                       GraphConstants.TruckAccess |
                       GraphConstants.EmergencyAccess |
                       GraphConstants.MotorcycleAccess);
            GraphId firstViaNode = FindGraphId(graph, 11);
            GraphId lastViaNode = FindGraphId(graph, 12);
            GraphId fromNode = FindGraphId(graph, 10);
            GraphId toNode = FindGraphId(graph, 13);
            DirectedEdge inbound = FindDirectedEdge(
                output,
                fromNode,
                wayId: 20,
                endNode: firstViaNode);
            DirectedEdge fromReverse = FindDirectedEdge(
                output,
                firstViaNode,
                wayId: 20,
                endNode: fromNode);
            DirectedEdge outbound = FindDirectedEdge(
                output,
                lastViaNode,
                wayId: 22,
                endNode: toNode);
            DirectedEdge toReverse = FindDirectedEdge(
                output,
                toNode,
                wayId: 22,
                endNode: lastViaNode);
            DirectedEdge viaForward = FindDirectedEdge(
                output,
                firstViaNode,
                wayId: 21,
                endNode: lastViaNode);
            DirectedEdge viaReverse = FindDirectedEdge(
                output,
                lastViaNode,
                wayId: 21,
                endNode: firstViaNode);

            Assert.Equal(expectedModes, inbound.StartRestriction);
            Assert.Equal(expectedModes, outbound.EndRestriction);
            Assert.True(inbound.PartOfComplexRestriction);
            Assert.True(fromReverse.PartOfComplexRestriction);
            Assert.True(outbound.PartOfComplexRestriction);
            Assert.True(toReverse.PartOfComplexRestriction);
            Assert.True(viaForward.PartOfComplexRestriction);
            Assert.True(viaReverse.PartOfComplexRestriction);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }




    [Fact]
    public void ExecuteCleanupActions_RunsEveryActionAndPreservesFirstFailure()
    {
        var calls = new List<int>();
        var firstFailure = new IOException("first cleanup failure");
        var laterFailure = new UnauthorizedAccessException("later cleanup failure");

        Exception? actual = BoundedRoadTileWriter.ExecuteCleanupActions(
            () =>
            {
                calls.Add(1);
                throw firstFailure;
            },
            () => calls.Add(2),
            () =>
            {
                calls.Add(3);
                throw laterFailure;
            },
            () => calls.Add(4));

        Assert.Same(firstFailure, actual);
        Assert.Equal([1, 2, 3, 4], calls);
    }

    [Fact]
    public void ResolveWriteOutcome_OperationFailureRemainsPrimaryWhenCleanupFails()
    {
        var operationFailure = new InvalidDataException("generation failed");
        var cleanupFailure = new IOException("cleanup failed");

        InvalidDataException actual = Assert.Throws<InvalidDataException>(
            () => BoundedRoadTileWriter.ResolveWriteOutcome(
                receipt: null,
                operationFailure,
                cleanupFailure));

        Assert.Same(operationFailure, actual);
        Assert.Same(
            cleanupFailure,
            actual.Data["BoundedRoadTileWriter.CleanupFailure"]);
    }

    [Fact]
    public void ResolveWriteOutcome_CleanupFailureSurfacesAfterSuccessfulWrite()
    {
        var receipt = new BoundedRoadTileWriteReceipt(
            TileCount: 1,
            PeakActiveTileBuilders: 1,
            PeakWorkerMemoryBytes: 1024);
        var cleanupFailure = new IOException("cleanup failed");

        IOException actual = Assert.Throws<IOException>(
            () => BoundedRoadTileWriter.ResolveWriteOutcome(
                receipt,
                operationFailure: null,
                cleanupFailure));

        Assert.Same(cleanupFailure, actual);
    }

    [Fact]
    public async Task WriteAsync_InsufficientMemoryBudgetCannotPublishTile()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new StraightRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);
            string output = Path.Combine(root, "tiles");

            await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                async () => await BoundedRoadTileWriter.WriteAsync(
                    semanticStore,
                    graph,
                    new BoundedRoadTileWriterOptions(
                        output,
                        MemoryBudgetBytes: 1,
                        MaxDegreeOfParallelism: 1),
                    TestContext.Current.CancellationToken));

            Assert.Empty(
                Directory.Exists(output)
                    ? Directory.EnumerateFiles(
                        output,
                        "*.gph",
                        SearchOption.AllDirectories)
                    : []);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DirectedEdge FindDirectedEdge(
        string tileDirectory,
        GraphId startNode,
        ulong wayId,
        GraphId endNode)
    {
        GraphTile tile = GraphTile.Create(tileDirectory, startNode.TileBase()) ??
            throw new InvalidDataException(
                $"Graph tile {startNode.TileBase()} was not written.");
        NodeInfo node = tile.Node(startNode);
        for (uint localIndex = 0; localIndex < node.EdgeCount; localIndex++)
        {
            DirectedEdge edge = tile.DirectedEdge(
                checked((int)(node.EdgeIndex + localIndex)));
            if (edge.EndNode == endNode &&
                tile.EdgeInfo(edge).WayId == wayId)
            {
                return edge;
            }
        }

        throw new InvalidDataException(
            $"Way {wayId} from {startNode} to {endNode} was not written.");
    }


    private static GraphId FindGraphId(
        PooledRoadEdgeBuildResult graph,
        long osmNodeId)
    {
        for (long ordinal = 0; ordinal < graph.IdentityCount; ordinal++)
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

    private static CompactOsmSemanticStoreOptions SemanticOptions(string path) =>
        new(
            path,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 8 * 1024 * 1024,
            ScratchDiskBudgetBytes: 32 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static PooledRoadEdgeBuilderOptions BuilderOptions(string path) =>
        new(
            path,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 16 * 1024 * 1024,
            ScratchDiskBudgetBytes: 64 * 1024 * 1024,
            GridDivisions: 8,
            ArenaSlabCapacity: 8,
            ShapeBufferSizeBytes: 4096,
            SegmentSizeBytes: 64 * 1024);

    private static string FindRepositoryArtifact(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(parts);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-bounded-tile-writer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class ComplexRestrictionRoadSource : IOsmPbfEntitySource
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
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                visitor.Relation(
                    30,
                    [
                        new OsmRelationMember(20, OsmMemberType.Way, "from"),
                        new OsmRelationMember(21, OsmMemberType.Way, "via"),
                        new OsmRelationMember(22, OsmMemberType.Way, "to"),
                    ],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["type"] = "restriction",
                        ["restriction"] = "no_left_turn",
                    });
                return;
            }

            if (pass != OsmPbfEntityPass.Nodes)
            {
                return;
            }

            visitor.Node(10, 36.1000, -86.7030, EmptyTags());
            visitor.Node(11, 36.1000, -86.7020, EmptyTags());
            visitor.Node(12, 36.1000, -86.7010, EmptyTags());
            visitor.Node(13, 36.1000, -86.7000, EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> RoadTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = "primary",
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }


    private sealed class SimpleRestrictionRoadSource : IOsmPbfEntitySource
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
                visitor.Way(10, [1UL, 2UL], RoadTags("Inbound"));
                visitor.Way(12, [2UL, 3UL], RoadTags("Restricted Exit"));
                visitor.Way(13, [2UL, 4UL], RoadTags("Allowed Exit"));
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                visitor.Relation(
                    20,
                    [
                        new OsmRelationMember(10, OsmMemberType.Way, "from"),
                        new OsmRelationMember(2, OsmMemberType.Node, "via"),
                        new OsmRelationMember(12, OsmMemberType.Way, "to"),
                    ],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["type"] = "restriction",
                        ["restriction"] = "no_left_turn",
                    });
                return;
            }

            if (pass != OsmPbfEntityPass.Nodes)
            {
                return;
            }

            visitor.Node(1, 36.1000, -86.7020, EmptyTags());
            visitor.Node(2, 36.1000, -86.7000, EmptyTags());
            visitor.Node(3, 36.1020, -86.7000, EmptyTags());
            visitor.Node(4, 36.1000, -86.6980, EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> RoadTags(string name) =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = "primary",
                ["name"] = name,
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }


    private sealed class CrossTileRoadSource : IOsmPbfEntitySource
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
                visitor.Way(
                    92,
                    [10UL, 11UL],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["highway"] = "primary",
                        ["name"] = "Cross Tile Road",
                    });
                return;
            }

            if (pass != OsmPbfEntityPass.Nodes)
            {
                return;
            }

            visitor.Node(10, 36.1000, -86.7600, EmptyTags());
            visitor.Node(11, 36.1000, -86.7400, EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed class JamaicaRoadSource : IOsmPbfEntitySource
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
                visitor.Way(
                    100,
                    [1UL, 2UL],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["highway"] = "primary",
                    });
            }
            else if (pass == OsmPbfEntityPass.Nodes)
            {
                visitor.Node(1, 18.1000, -77.3000, EmptyTags());
                visitor.Node(2, 18.1010, -77.2990, EmptyTags());
            }
        }

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed class StraightRoadSource : IOsmPbfEntitySource
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
                visitor.Way(
                    91,
                    [1UL, 2UL, 3UL, 4UL],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["highway"] = "primary",
                        ["name"] = "Bounded Road",
                    });
                return;
            }

            if (pass != OsmPbfEntityPass.Nodes)
            {
                return;
            }

            visitor.Node(1, 36.1000, -86.7000, EmptyTags());
            visitor.Node(2, 36.1010, -86.6990, ControlTags("stop"));
            visitor.Node(3, 36.1020, -86.6980, ControlTags("give_way"));
            visitor.Node(4, 36.1030, -86.6970, ControlTags("traffic_signals"));
        }

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static IReadOnlyDictionary<string, string> ControlTags(
            string control) =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = control,
            };
    }
}
