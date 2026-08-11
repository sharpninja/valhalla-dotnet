using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class DurableFrontierEdgeSinkTests
{
    [Fact]
    public async Task ProcessLongWay_SpillsShapesAndEdgesWithoutGlobalTileCollections()
    {
        const int secondaryCount = 100_000;
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-frontier-edge-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var nodes = new PooledPathNode[secondaryCount + 2];
            nodes[0] = Anchor(1, new GraphId(7, 0, 0));
            for (int index = 0; index < secondaryCount; index++)
            {
                nodes[index + 1] = new PooledPathNode(
                    new GenerationNodeRecord(
                        index + 2L,
                        360000000 + index,
                        -860000000 + index,
                        NodeSemanticFlags.None,
                        TagReference: 0),
                    IsGraphAnchor: false,
                    GraphId.Invalid);
            }

            nodes[^1] = Anchor(secondaryCount + 2L, new GraphId(7, 0, 1));

            using var arena = new PooledNodeArena(
                slabCapacity: 8,
                memoryBudgetBytes: 4096);
            using var sink = new DurableFrontierEdgeSink(
                new DurableFrontierEdgeSinkOptions(
                    root,
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: 64 * 1024,
                    ScratchDiskBudgetBytes: 64 * 1024 * 1024,
                    ShapeBufferSizeBytes: 4096,
                    SegmentSizeBytes: 16 * 1024));
            var frontier = new PooledPathFrontier(arena, sink);

            PooledPathFrontierResult result = frontier.ProcessWay(
                8001,
                nodes,
                TestContext.Current.CancellationToken);
            DurableFrontierEdgeStoreReceipt receipt = await sink.CompleteAsync(
                TestContext.Current.CancellationToken);

            GenerationEdgeRecord edge = sink.ReadEdge(0);
            GenerationNodeRecord[] shape = sink.ReadShape(edge.Shape);
            Assert.Equal(1, sink.EdgeCount);
            Assert.Equal(secondaryCount + 2, shape.Length);
            Assert.Equal(1, shape[0].OsmNodeId);
            Assert.Equal(secondaryCount + 2L, shape[^1].OsmNodeId);
            Assert.Equal(secondaryCount, result.SecondarySlotsReleased);
            Assert.True(result.PeakLiveSlots <= 3);
            Assert.Equal(
                IntermediateStorageMode.MemoryMapped,
                receipt.ShapeManifest.StorageMode);
            Assert.Equal(1, receipt.EdgeManifest.RecordCount);
            Assert.DoesNotContain(
                typeof(DurableFrontierEdgeSink).GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic),
                field => field.FieldType == typeof(Dictionary<GraphId, byte[]>));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static PooledPathNode Anchor(long osmNodeId, GraphId graphId) => new(
        new GenerationNodeRecord(
            osmNodeId,
            LatitudeE7: 360000000,
            LongitudeE7: -860000000,
            NodeSemanticFlags.None,
            TagReference: 0),
        IsGraphAnchor: true,
        graphId);
}
