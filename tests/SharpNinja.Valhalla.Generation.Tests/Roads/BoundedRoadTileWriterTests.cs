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
            Assert.Equal(2U, tile.Header().Nodecount());
            Assert.Equal(2U, tile.Header().Directededgecount());
            Assert.All(
                Enumerable.Range(0, 2),
                index =>
                {
                    DirectedEdge edge = tile.DirectedEdge(index);
                    Assert.True(edge.EndNode.IsValid());
                    Assert.NotEqual(0U, edge.ForwardAccess);
                    Assert.Equal((byte)RoadClass.Primary, (byte)edge.Classification);
                });
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
                    [1UL, 2UL],
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
            visitor.Node(2, 36.1010, -86.6990, EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
