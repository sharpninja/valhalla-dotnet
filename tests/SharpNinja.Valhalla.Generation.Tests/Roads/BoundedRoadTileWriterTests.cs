using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
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

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-bounded-tile-writer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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
