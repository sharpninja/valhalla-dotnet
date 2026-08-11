using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class ComplexRestrictionMarkerIndexTests
{
    private const uint DefaultRestrictionModes =
        (uint)(GraphConstants.AutoAccess |
               GraphConstants.MopedAccess |
               GraphConstants.TaxiAccess |
               GraphConstants.BusAccess |
               GraphConstants.BicycleAccess |
               GraphConstants.TruckAccess |
               GraphConstants.EmergencyAccess |
               GraphConstants.MotorcycleAccess);

    [Fact]
    public void MarkerRecords_AreUnmanagedReferenceFree()
    {
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<
                RestrictionWayEndpointRecord>());
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<
                ComplexRestrictionMarkerRecord>());
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<
                ComplexRestrictionEdgeMarker>());
    }

    [Fact]
    public async Task BuildAsync_ViaWayRestrictionProducesStartEndAndPartMarkers()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.ViaWay()]);
        using ComplexRestrictionMarkerIndex index =
            await fixture.BuildIndexAsync(TestContext.Current.CancellationToken);

        EdgeKey inbound = fixture.GetEdgeKey(10, 1, 2);
        EdgeKey fromReverse = fixture.GetEdgeKey(10, 2, 1);
        EdgeKey outbound = fixture.GetEdgeKey(12, 3, 4);
        EdgeKey toReverse = fixture.GetEdgeKey(12, 4, 3);
        EdgeKey viaForward = fixture.GetEdgeKey(11, 2, 3);
        EdgeKey viaReverse = fixture.GetEdgeKey(11, 3, 2);

        ComplexRestrictionEdgeMarker start = AssertMarker(index, inbound);
        ComplexRestrictionEdgeMarker end = AssertMarker(index, outbound);

        Assert.Equal(DefaultRestrictionModes, start.StartModes);
        Assert.Equal(0U, start.EndModes);
        Assert.True(start.PartOfComplexRestriction);
        Assert.Equal(0U, end.StartModes);
        Assert.Equal(DefaultRestrictionModes, end.EndModes);
        Assert.True(end.PartOfComplexRestriction);
        Assert.True(AssertMarker(index, fromReverse).PartOfComplexRestriction);
        Assert.True(AssertMarker(index, toReverse).PartOfComplexRestriction);
        Assert.True(AssertMarker(index, viaForward).PartOfComplexRestriction);
        Assert.True(AssertMarker(index, viaReverse).PartOfComplexRestriction);
    }

    [Fact]
    public async Task BuildAsync_TypeSpecificNodeViaProducesTruckOnlyMarkers()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.HgvNodeVia()]);
        using ComplexRestrictionMarkerIndex index =
            await fixture.BuildIndexAsync(TestContext.Current.CancellationToken);

        EdgeKey inbound = fixture.GetEdgeKey(10, 1, 2);
        EdgeKey fromReverse = fixture.GetEdgeKey(10, 2, 1);
        EdgeKey outbound = fixture.GetEdgeKey(13, 2, 5);
        EdgeKey toReverse = fixture.GetEdgeKey(13, 5, 2);

        ComplexRestrictionEdgeMarker start = AssertMarker(index, inbound);
        ComplexRestrictionEdgeMarker end = AssertMarker(index, outbound);

        Assert.Equal((uint)GraphConstants.TruckAccess, start.StartModes);
        Assert.Equal((uint)GraphConstants.TruckAccess, end.EndModes);
        Assert.True(start.PartOfComplexRestriction);
        Assert.True(end.PartOfComplexRestriction);
        Assert.True(AssertMarker(index, fromReverse).PartOfComplexRestriction);
        Assert.True(AssertMarker(index, toReverse).PartOfComplexRestriction);
    }

    [Fact]
    public async Task BuildAsync_ConditionalNodeViaMatchesUpstreamPartOfWays()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.ConditionalNodeVia()]);
        using ComplexRestrictionMarkerIndex index =
            await fixture.BuildIndexAsync(TestContext.Current.CancellationToken);

        EdgeKey inbound = fixture.GetEdgeKey(10, 1, 2);
        EdgeKey fromReverse = fixture.GetEdgeKey(10, 2, 1);
        EdgeKey outbound = fixture.GetEdgeKey(13, 2, 5);
        EdgeKey toReverse = fixture.GetEdgeKey(13, 5, 2);

        ComplexRestrictionEdgeMarker start = AssertMarker(index, inbound);
        ComplexRestrictionEdgeMarker end = AssertMarker(index, outbound);

        Assert.Equal(DefaultRestrictionModes, start.StartModes);
        Assert.False(start.PartOfComplexRestriction);
        Assert.False(
            index.TryGetMarker(
                fromReverse.StartNode,
                fromReverse.EdgeRecordId,
                fromReverse.Forward,
                out _));
        Assert.Equal(DefaultRestrictionModes, end.EndModes);
        Assert.True(end.PartOfComplexRestriction);
        Assert.True(AssertMarker(index, toReverse).PartOfComplexRestriction);
    }

    [Fact]
    public async Task BuildAsync_ExceptHgvRemovesTruckFromGenericModes()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.ExceptHgvNodeVia()]);
        using ComplexRestrictionMarkerIndex index =
            await fixture.BuildIndexAsync(TestContext.Current.CancellationToken);

        EdgeKey inbound = fixture.GetEdgeKey(10, 1, 2);
        EdgeKey outbound = fixture.GetEdgeKey(13, 2, 5);
        uint expected = DefaultRestrictionModes & ~(uint)GraphConstants.TruckAccess;

        Assert.Equal(expected, AssertMarker(index, inbound).StartModes);
        Assert.Equal(expected, AssertMarker(index, outbound).EndModes);
    }

    [Fact]
    public async Task BuildAsync_SimpleGenericNodeViaProducesNoComplexMarkers()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.SimpleNodeVia()]);
        using ComplexRestrictionMarkerIndex index =
            await fixture.BuildIndexAsync(TestContext.Current.CancellationToken);

        EdgeKey inbound = fixture.GetEdgeKey(10, 1, 2);
        Assert.Equal(0, index.Count);
        Assert.False(
            index.TryGetMarker(
                inbound.StartNode,
                inbound.EdgeRecordId,
                inbound.Forward,
                out ComplexRestrictionEdgeMarker marker));
        Assert.Equal(default, marker);
    }

    [Fact]
    public async Task BuildAsync_MultipleQualifiedRestrictionsOrModesDeterministically()
    {
        RestrictionSpec hgv = RestrictionSpec.HgvNodeVia();
        RestrictionSpec bicycle = RestrictionSpec.BicycleNodeVia();
        using GraphFixture ordered = await GraphFixture.CreateAsync([hgv, bicycle]);
        using GraphFixture reversed = await GraphFixture.CreateAsync([bicycle, hgv]);
        using ComplexRestrictionMarkerIndex orderedIndex =
            await ordered.BuildIndexAsync(TestContext.Current.CancellationToken);
        using ComplexRestrictionMarkerIndex reversedIndex =
            await reversed.BuildIndexAsync(TestContext.Current.CancellationToken);

        EdgeKey orderedInbound = ordered.GetEdgeKey(10, 1, 2);
        EdgeKey reversedInbound = reversed.GetEdgeKey(10, 1, 2);
        uint expected =
            (uint)(GraphConstants.TruckAccess | GraphConstants.BicycleAccess);

        Assert.Equal(expected, AssertMarker(orderedIndex, orderedInbound).StartModes);
        Assert.Equal(expected, AssertMarker(reversedIndex, reversedInbound).StartModes);
    }

    [Fact]
    public async Task BuildAsync_CancellationAndResourceLimitsFailSafely()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.ViaWay()]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        string cancelledRoot = Path.Combine(fixture.Root, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ComplexRestrictionMarkerIndex.BuildAsync(
                fixture.SemanticStore,
                fixture.Graph,
                new ComplexRestrictionMarkerIndexOptions(
                    cancelledRoot,
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    ScratchDiskBudgetBytes: 32 * 1024 * 1024,
                    SegmentSizeBytes: 64 * 1024),
                cancellation.Token));
        Assert.False(Directory.Exists(cancelledRoot));

        string failedRoot = Path.Combine(fixture.Root, "resource-failure");
        await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
            async () => await ComplexRestrictionMarkerIndex.BuildAsync(
                fixture.SemanticStore,
                fixture.Graph,
                new ComplexRestrictionMarkerIndexOptions(
                    failedRoot,
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: 1,
                    ScratchDiskBudgetBytes: 1,
                    SegmentSizeBytes: 64 * 1024),
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(failedRoot));
    }

    private static ComplexRestrictionEdgeMarker AssertMarker(
        ComplexRestrictionMarkerIndex index,
        EdgeKey key)
    {
        Assert.True(
            index.TryGetMarker(
                key.StartNode,
                key.EdgeRecordId,
                key.Forward,
                out ComplexRestrictionEdgeMarker marker));
        return marker;
    }

    private readonly record struct EdgeKey(
        GraphId StartNode,
        long EdgeRecordId,
        bool Forward);

    private sealed class GraphFixture : IDisposable
    {
        private GraphFixture(
            string root,
            CompactOsmSemanticStore semanticStore,
            PooledRoadEdgeBuildResult graph)
        {
            Root = root;
            SemanticStore = semanticStore;
            Graph = graph;
        }

        internal string Root { get; }

        internal CompactOsmSemanticStore SemanticStore { get; }

        internal PooledRoadEdgeBuildResult Graph { get; }

        internal static async Task<GraphFixture> CreateAsync(
            IReadOnlyList<RestrictionSpec> restrictions)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "valhalla-complex-restriction-index-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            CompactOsmSemanticStore? semanticStore = null;
            PooledRoadEdgeBuildResult? graph = null;
            try
            {
                semanticStore = await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionRoadSource(restrictions),
                    new CompactOsmSemanticStoreOptions(
                        Path.Combine(root, "semantic"),
                        IntermediateStorageMode.Auto,
                        MemoryBudgetBytes: 8 * 1024 * 1024,
                        ScratchDiskBudgetBytes: 32 * 1024 * 1024,
                        SegmentSizeBytes: 64 * 1024),
                    TestContext.Current.CancellationToken);
                graph = await PooledRoadEdgeBuilder.BuildAsync(
                    semanticStore,
                    new PooledRoadEdgeBuilderOptions(
                        Path.Combine(root, "graph"),
                        IntermediateStorageMode.Auto,
                        MemoryBudgetBytes: 16 * 1024 * 1024,
                        ScratchDiskBudgetBytes: 64 * 1024 * 1024,
                        GridDivisions: 8,
                        ArenaSlabCapacity: 8,
                        ShapeBufferSizeBytes: 4096,
                        SegmentSizeBytes: 64 * 1024),
                    TestContext.Current.CancellationToken);
                var result = new GraphFixture(root, semanticStore, graph);
                semanticStore = null;
                graph = null;
                return result;
            }
            catch
            {
                graph?.Dispose();
                semanticStore?.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        internal ValueTask<ComplexRestrictionMarkerIndex> BuildIndexAsync(
            CancellationToken cancellationToken) =>
            ComplexRestrictionMarkerIndex.BuildAsync(
                SemanticStore,
                Graph,
                new ComplexRestrictionMarkerIndexOptions(
                    Path.Combine(Root, $"index-{Guid.NewGuid():N}"),
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    ScratchDiskBudgetBytes: 32 * 1024 * 1024,
                    SegmentSizeBytes: 64 * 1024),
                cancellationToken);

        internal EdgeKey GetEdgeKey(long wayId, long startNodeId, long endNodeId)
        {
            Assert.True(Graph.TryGetGraphId(startNodeId, out GraphId startNode));
            Assert.True(Graph.TryGetGraphId(endNodeId, out GraphId endNode));
            for (long ordinal = 0; ordinal < Graph.EdgeCount; ordinal++)
            {
                GenerationEdgeRecord edge = Graph.ReadEdge(ordinal);
                if (edge.WayId != wayId)
                {
                    continue;
                }

                if (edge.SourceNode == startNode && edge.TargetNode == endNode)
                {
                    return new EdgeKey(startNode, edge.EdgeRecordId, Forward: true);
                }

                if (edge.SourceNode == endNode && edge.TargetNode == startNode)
                {
                    return new EdgeKey(startNode, edge.EdgeRecordId, Forward: false);
                }
            }

            throw new InvalidDataException(
                $"The fixture has no {wayId} edge from {startNodeId} to {endNodeId}.");
        }

        public void Dispose()
        {
            Graph.Dispose();
            SemanticStore.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RestrictionRoadSource(
        IReadOnlyList<RestrictionSpec> restrictions) : IOsmPbfEntitySource
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
                visitor.Way(10, [1UL, 2UL], RoadTags());
                visitor.Way(11, [2UL, 3UL], RoadTags());
                visitor.Way(12, [3UL, 4UL], RoadTags());
                visitor.Way(13, [2UL, 5UL], RoadTags());
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                for (int ordinal = 0; ordinal < restrictions.Count; ordinal++)
                {
                    RestrictionSpec restriction = restrictions[ordinal];
                    visitor.Relation(
                        20UL + checked((ulong)ordinal),
                        restriction.Members,
                        restriction.Tags);
                }

                return;
            }

            if (pass != OsmPbfEntityPass.Nodes)
            {
                return;
            }

            visitor.Node(1, 36.1000, -86.7040, EmptyTags());
            visitor.Node(2, 36.1000, -86.7020, EmptyTags());
            visitor.Node(3, 36.1000, -86.7000, EmptyTags());
            visitor.Node(4, 36.1000, -86.6980, EmptyTags());
            visitor.Node(5, 36.1020, -86.7020, EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> RoadTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = "primary",
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed record RestrictionSpec(
        IReadOnlyList<OsmRelationMember> Members,
        IReadOnlyDictionary<string, string> Tags)
    {
        internal static RestrictionSpec ViaWay() =>
            Create(
                [
                    new OsmRelationMember(10, OsmMemberType.Way, "from"),
                    new OsmRelationMember(11, OsmMemberType.Way, "via"),
                    new OsmRelationMember(12, OsmMemberType.Way, "to"),
                ],
                new KeyValuePair<string, string>("restriction", "no_left_turn"));

        internal static RestrictionSpec SimpleNodeVia() =>
            NodeVia(new KeyValuePair<string, string>("restriction", "no_left_turn"));

        internal static RestrictionSpec HgvNodeVia() =>
            NodeVia(new KeyValuePair<string, string>(
                "restriction:hgv",
                "no_left_turn"));

        internal static RestrictionSpec BicycleNodeVia() =>
            NodeVia(new KeyValuePair<string, string>(
                "restriction:bicycle",
                "no_left_turn"));

        internal static RestrictionSpec ConditionalNodeVia() =>
            NodeVia(new KeyValuePair<string, string>(
                "restriction:conditional",
                "no_left_turn @ (Mo-Fr 07:00-09:00)"));

        internal static RestrictionSpec ExceptHgvNodeVia() =>
            NodeVia(
                new KeyValuePair<string, string>("restriction", "no_left_turn"),
                new KeyValuePair<string, string>("except", "hgv"));

        private static RestrictionSpec NodeVia(
            params KeyValuePair<string, string>[] tags) =>
            Create(
                [
                    new OsmRelationMember(10, OsmMemberType.Way, "from"),
                    new OsmRelationMember(2, OsmMemberType.Node, "via"),
                    new OsmRelationMember(13, OsmMemberType.Way, "to"),
                ],
                tags);

        private static RestrictionSpec Create(
            IReadOnlyList<OsmRelationMember> members,
            params KeyValuePair<string, string>[] restrictionTags)
        {
            var tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "restriction",
            };
            foreach ((string key, string value) in restrictionTags)
            {
                tags[key] = value;
            }

            return new RestrictionSpec(members, tags);
        }
    }
}
