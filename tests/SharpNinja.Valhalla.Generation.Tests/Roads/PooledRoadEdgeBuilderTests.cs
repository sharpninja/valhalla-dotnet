using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledRoadEdgeBuilderTests
{
    [Fact]
    public async Task BuildAsync_StreamsCompactWaysIntoDurableEdgesWithoutLegacyGraphs()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new BranchedRoadSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using PooledRoadEdgeBuildResult result =
                await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(3, result.EdgeCount);
            Assert.Equal(4, result.GraphNodeCount);
            Assert.Equal(7, result.FrontierMetrics.WayNodeOccurrencesProcessed);
            Assert.Equal(2, result.FrontierMetrics.SecondaryNodesProcessed);
            Assert.Equal(2, result.FrontierMetrics.SecondarySlotsReleased);
            Assert.True(result.FrontierMetrics.PeakLiveSlots <= 3);
            Assert.Equal(0, result.FrontierMetrics.StaleHandleRejections);

            GenerationEdgeRecord first = result.ReadEdge(0);
            GenerationEdgeRecord second = result.ReadEdge(1);
            GenerationEdgeRecord third = result.ReadEdge(2);
            Assert.Equal(10, first.WayId);
            Assert.Equal(10, second.WayId);
            Assert.Equal(11, third.WayId);
            Assert.Equal(
                semanticStore.ReadWay(0).TagReference,
                first.AttributeReference);
            Assert.NotEqual(0U, first.ForwardAccess);
            Assert.NotEqual(0U, first.ReverseAccess);
            Assert.Equal(
                (byte)SharpNinja.Valhalla.Baldr.RoadClass.Residential,
                first.Importance);
            Assert.Equal(3, result.ReadShape(first.Shape).Length);
            Assert.Equal(3, result.ReadShape(second.Shape).Length);
            Assert.Equal(2, result.ReadShape(third.Shape).Length);
            Assert.Equal(
                result.ReadShape(first.Shape)[^1].OsmNodeId,
                result.ReadShape(second.Shape)[0].OsmNodeId);
            Assert.Equal(3, result.ReadShape(first.Shape)[^1].OsmNodeId);
            Assert.All(
                Enumerable.Range(0, checked((int)result.EdgeCount)),
                ordinal =>
                {
                    GenerationEdgeRecord edge = result.ReadEdge(ordinal);
                    Assert.True(edge.SourceNode.IsValid());
                    Assert.True(edge.TargetNode.IsValid());
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_MissingReferencedNodeFailsBeforeEdgePublication()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new MissingNodeSource(),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    BuilderOptions(Path.Combine(root, "pooled")),
                    TestContext.Current.CancellationToken));

            Assert.Contains("OSM node 99", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PooledBuilder_HasNoLegacyGraphOrWayNodeCollectionFields()
    {
        Type[] forbiddenTypes =
        [
            typeof(OSMWay),
            typeof(OSMWayNode),
            typeof(GraphBuilder.Graph),
        ];

        Assert.DoesNotContain(
            typeof(PooledRoadEdgeBuilder).Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(PooledRoadEdgeBuilder).Namespace)
                .Where(type => type.Name.StartsWith("PooledRoad", StringComparison.Ordinal))
                .SelectMany(type => type.GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public)),
            field => forbiddenTypes.Any(
                forbidden =>
                    field.FieldType == forbidden ||
                    field.FieldType.IsGenericType &&
                    field.FieldType.GetGenericArguments().Contains(forbidden)));
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<GenerationEdgeRecord>());
    }

    private static CompactOsmSemanticStoreOptions SemanticOptions(string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 16 * 1024 * 1024,
            ScratchDiskBudgetBytes: 64 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static PooledRoadEdgeBuilderOptions BuilderOptions(string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 32 * 1024 * 1024,
            ScratchDiskBudgetBytes: 128 * 1024 * 1024,
            GridDivisions: 0,
            ArenaSlabCapacity: 8,
            ShapeBufferSizeBytes: 64 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Generation.Tests",
            nameof(PooledRoadEdgeBuilderTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class BranchedRoadSource : IOsmPbfEntitySource
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
                visitor.Way(10, [1UL, 2UL, 3UL, 4UL, 5UL], RoadTags());
                visitor.Way(11, [3UL, 6UL], RoadTags());
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                return;
            }

            for (ulong id = 1; id <= 6; id++)
            {
                visitor.Node(
                    id,
                    36.10 + (id * 0.001),
                    -86.70 - (id * 0.001),
                    EmptyTags());
            }
        }
    }

    private sealed class MissingNodeSource : IOsmPbfEntitySource
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
                visitor.Way(20, [1UL, 99UL], RoadTags());
                return;
            }

            if (pass == OsmPbfEntityPass.Nodes)
            {
                visitor.Node(1, 36.10, -86.70, EmptyTags());
            }
        }
    }

    private static IReadOnlyDictionary<string, string> RoadTags() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["highway"] = "residential",
        };

    private static IReadOnlyDictionary<string, string> EmptyTags() =>
        new Dictionary<string, string>(StringComparer.Ordinal);
}
