using System.Runtime.CompilerServices;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class NodeEdgeIncidenceIndexTests
{
    [Fact]
    public async Task BuildAsync_EmitsTwoEndpointIncidencesAndStableNodeRanges()
    {
        string root = CreateWorkingDirectory();
        try
        {
            GraphId first = new(10, 2, 0);
            GraphId second = new(10, 2, 1);
            GraphId third = new(11, 2, 0);
            var source = new TestEdgeSource(
            [
                Edge(100, first, second, importance: 2, hasNames: true),
                Edge(101, second, third, importance: 4, hasNames: false),
            ]);

            using NodeEdgeIncidenceIndex index = await NodeEdgeIncidenceIndex.BuildAsync(
                source,
                Options(root),
                TestContext.Current.CancellationToken);

            Assert.Equal(4, index.IncidenceCount);
            Assert.Equal(3, index.GraphNodeCount);
            Assert.Equal((first, 0L, 1), Range(index.ReadGraphNode(0)));
            Assert.Equal((second, 1L, 2), Range(index.ReadGraphNode(1)));
            Assert.Equal((third, 3L, 1), Range(index.ReadGraphNode(2)));
            Assert.Equal(EdgeEndpointRole.Source, index.ReadIncidence(0).Role);
            Assert.Equal(EdgeEndpointRole.Target, index.ReadIncidence(3).Role);
            Assert.Equal(4, index.IncidenceManifest.RecordCount);
            Assert.Equal(3, index.GraphNodeManifest.RecordCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_OrdersNodeEdgesLikeLegacyNodeBundleAcrossInputOrder()
    {
        string firstRoot = CreateWorkingDirectory();
        string secondRoot = CreateWorkingDirectory();
        try
        {
            GraphId center = new(17, 2, 0);
            GenerationEdgeRecord[] edges =
            [
                Edge(
                    200,
                    center,
                    new GraphId(17, 2, 1),
                    importance: 5,
                    hasNames: false,
                    forwardAccess: GraphConstants.AutoAccess),
                Edge(
                    201,
                    new GraphId(17, 2, 2),
                    center,
                    importance: 2,
                    hasNames: true,
                    reverseAccess: GraphConstants.AutoAccess),
                Edge(
                    202,
                    center,
                    new GraphId(17, 2, 3),
                    importance: 0,
                    hasNames: true),
            ];

            using NodeEdgeIncidenceIndex first = await NodeEdgeIncidenceIndex.BuildAsync(
                new TestEdgeSource(edges),
                Options(firstRoot),
                TestContext.Current.CancellationToken);
            using NodeEdgeIncidenceIndex second = await NodeEdgeIncidenceIndex.BuildAsync(
                new TestEdgeSource(edges.Reverse().ToArray()),
                Options(secondRoot),
                TestContext.Current.CancellationToken);

            long[] expected = [201, 200, 202];
            Assert.Equal(expected, ReadEdgeIds(first, center));
            Assert.Equal(expected, ReadEdgeIds(second, center));
            Assert.Equal(
                first.IncidenceManifest.ContentSha256,
                second.IncidenceManifest.ContentSha256);
            Assert.Equal(
                first.GraphNodeManifest.ContentSha256,
                second.GraphNodeManifest.ContentSha256);
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_SelfLoopPreservesBothEndpointRolesWithoutPooledHandles()
    {
        string root = CreateWorkingDirectory();
        try
        {
            GraphId node = new(42, 2, 7);
            using NodeEdgeIncidenceIndex index = await NodeEdgeIncidenceIndex.BuildAsync(
                new TestEdgeSource(
                [
                    Edge(
                        300,
                        node,
                        node,
                        importance: 1,
                        hasNames: true,
                        forwardAccess: GraphConstants.AutoAccess,
                        reverseAccess: GraphConstants.AutoAccess),
                ]),
                Options(root),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, index.IncidenceCount);
            Assert.Equal(1, index.GraphNodeCount);
            Assert.Equal(
                [EdgeEndpointRole.Source, EdgeEndpointRole.Target],
                Enumerable.Range(0, 2)
                    .Select(ordinal => index.ReadIncidence(ordinal).Role)
                    .Order()
                    .ToArray());
            Assert.Equal(2, index.ReadGraphNode(0).IncidentEdgeCount);
            Assert.False(
                RuntimeHelpers.IsReferenceOrContainsReferences<NodeEdgeIncidenceRecord>());
            Assert.DoesNotContain(
                typeof(NodeEdgeIncidenceRecord).GetFields(),
                field => field.FieldType == typeof(NodeHandle));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (GraphId NodeId, long Offset, int Count) Range(
        GenerationGraphNodeRecord node) =>
        (node.NodeId, node.IncidentEdgeOffset, node.IncidentEdgeCount);

    private static long[] ReadEdgeIds(
        NodeEdgeIncidenceIndex index,
        GraphId nodeId)
    {
        for (long ordinal = 0; ordinal < index.GraphNodeCount; ordinal++)
        {
            GenerationGraphNodeRecord node = index.ReadGraphNode(ordinal);
            if (node.NodeId != nodeId)
            {
                continue;
            }

            return Enumerable.Range(0, node.IncidentEdgeCount)
                .Select(indexOffset =>
                    index.ReadIncidence(node.IncidentEdgeOffset + indexOffset).EdgeRecordId)
                .ToArray();
        }

        return [];
    }

    private static GenerationEdgeRecord Edge(
        long edgeRecordId,
        GraphId source,
        GraphId target,
        byte importance,
        bool hasNames,
        uint forwardAccess = 0,
        uint reverseAccess = 0) =>
        new(
            edgeRecordId,
            source,
            target,
            WayId: edgeRecordId + 1_000,
            new EdgeShapeReference(
                Offset: edgeRecordId * 100,
                PointCount: 2,
                ByteLength: 32),
            EdgeSemanticFlags.None,
            forwardAccess,
            reverseAccess,
            AttributeReference: 0,
            importance,
            hasNames,
            CanonicalOrdinal: edgeRecordId);

    private static NodeEdgeIncidenceIndexOptions Options(string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 4 * 1024,
            ScratchDiskBudgetBytes: 16 * 1024 * 1024,
            SegmentSizeBytes: 256);

    private static string CreateWorkingDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-node-edge-incidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestEdgeSource(
        IReadOnlyList<GenerationEdgeRecord> edges) : IFrontierEdgeSource
    {
        public long EdgeCount => edges.Count;

        public GenerationEdgeRecord ReadEdge(long ordinal) =>
            edges[checked((int)ordinal)];
    }
}
