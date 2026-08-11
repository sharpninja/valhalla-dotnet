using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class SimpleRestrictionMaskIndexTests
{
    [Fact]
    public void MaskRecord_IsUnmanagedReferenceFree()
    {
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<SimpleRestrictionMaskRecord>());
    }

    [Fact]
    public async Task BuildAsync_NoTurnRestrictionMasksOnlyTheToWay()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.NoTurn(12)]);
        using SimpleRestrictionMaskIndex index =
            await fixture.BuildIndexAsync(TestContext.Current.CancellationToken);

        IncomingEdgeKey incoming = fixture.GetIncomingEdge();
        Assert.True(
            index.TryGetMask(
                incoming.StartNode,
                incoming.EdgeRecordId,
                incoming.Forward,
                out uint mask));
        Assert.Equal(fixture.MaskForWaysAtVia(12), mask);
    }

    [Fact]
    public async Task BuildAsync_OnlyTurnRestrictionMasksEveryOtherLocalEdge()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.OnlyTurn(12)]);
        using SimpleRestrictionMaskIndex index =
            await fixture.BuildIndexAsync(TestContext.Current.CancellationToken);

        IncomingEdgeKey incoming = fixture.GetIncomingEdge();
        Assert.True(
            index.TryGetMask(
                incoming.StartNode,
                incoming.EdgeRecordId,
                incoming.Forward,
                out uint mask));
        Assert.Equal(fixture.MaskForAllWaysExceptAtVia(12), mask);
    }

    [Fact]
    public async Task BuildAsync_QualifiedConditionalAndExceptRestrictionsAreNotSimpleMasks()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [
                RestrictionSpec.Except(12),
                RestrictionSpec.Qualified(12),
                RestrictionSpec.Conditional(12),
            ]);
        using SimpleRestrictionMaskIndex index =
            await fixture.BuildIndexAsync(TestContext.Current.CancellationToken);

        IncomingEdgeKey incoming = fixture.GetIncomingEdge();
        Assert.Equal(0, index.Count);
        Assert.False(
            index.TryGetMask(
                incoming.StartNode,
                incoming.EdgeRecordId,
                incoming.Forward,
                out uint mask));
        Assert.Equal(0U, mask);
    }

    [Fact]
    public async Task BuildAsync_MultipleRestrictionsOrMasksDeterministicallyAcrossInputOrder()
    {
        RestrictionSpec first = RestrictionSpec.NoTurn(12);
        RestrictionSpec second = RestrictionSpec.NoTurn(13);
        using GraphFixture ordered = await GraphFixture.CreateAsync([first, second]);
        using GraphFixture reversed = await GraphFixture.CreateAsync([second, first]);
        using SimpleRestrictionMaskIndex orderedIndex =
            await ordered.BuildIndexAsync(TestContext.Current.CancellationToken);
        using SimpleRestrictionMaskIndex reversedIndex =
            await reversed.BuildIndexAsync(TestContext.Current.CancellationToken);

        IncomingEdgeKey orderedIncoming = ordered.GetIncomingEdge();
        IncomingEdgeKey reversedIncoming = reversed.GetIncomingEdge();
        Assert.True(
            orderedIndex.TryGetMask(
                orderedIncoming.StartNode,
                orderedIncoming.EdgeRecordId,
                orderedIncoming.Forward,
                out uint orderedMask));
        Assert.True(
            reversedIndex.TryGetMask(
                reversedIncoming.StartNode,
                reversedIncoming.EdgeRecordId,
                reversedIncoming.Forward,
                out uint reversedMask));
        Assert.Equal(ordered.MaskForWaysAtVia(12, 13), orderedMask);
        Assert.Equal(orderedMask, reversedMask);
    }

    [Fact]
    public async Task BuildAsync_CancellationAndResourceLimitsFailSafely()
    {
        using GraphFixture fixture = await GraphFixture.CreateAsync(
            [RestrictionSpec.NoTurn(12)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await fixture.BuildIndexAsync(cancellation.Token));
        await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
            async () => await SimpleRestrictionMaskIndex.BuildAsync(
                fixture.SemanticStore,
                fixture.Graph,
                new SimpleRestrictionMaskIndexOptions(
                    Path.Combine(fixture.Root, "resource-failure"),
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: 1,
                    ScratchDiskBudgetBytes: 1,
                    SegmentSizeBytes: 64 * 1024),
                TestContext.Current.CancellationToken));
        Assert.False(
            Directory.Exists(Path.Combine(fixture.Root, "resource-failure")));
    }

    private readonly record struct IncomingEdgeKey(
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
                "valhalla-simple-restriction-index-tests",
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

        internal ValueTask<SimpleRestrictionMaskIndex> BuildIndexAsync(
            CancellationToken cancellationToken) =>
            SimpleRestrictionMaskIndex.BuildAsync(
                SemanticStore,
                Graph,
                new SimpleRestrictionMaskIndexOptions(
                    Path.Combine(Root, $"index-{Guid.NewGuid():N}"),
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: 8 * 1024 * 1024,
                    ScratchDiskBudgetBytes: 32 * 1024 * 1024,
                    SegmentSizeBytes: 64 * 1024),
                cancellationToken);

        internal IncomingEdgeKey GetIncomingEdge()
        {
            Assert.True(Graph.TryGetGraphId(2, out GraphId viaNode));
            for (long ordinal = 0; ordinal < Graph.EdgeCount; ordinal++)
            {
                GenerationEdgeRecord edge = Graph.ReadEdge(ordinal);
                if (edge.WayId != 10)
                {
                    continue;
                }

                if (edge.TargetNode == viaNode)
                {
                    return new IncomingEdgeKey(
                        edge.SourceNode,
                        edge.EdgeRecordId,
                        Forward: true);
                }

                if (edge.SourceNode == viaNode)
                {
                    return new IncomingEdgeKey(
                        edge.TargetNode,
                        edge.EdgeRecordId,
                        Forward: false);
                }
            }

            throw new InvalidDataException("The fixture has no inbound way edge.");
        }

        internal uint MaskForWaysAtVia(params long[] wayIds) =>
            MaskForLocalEdgesAtVia(edge => wayIds.Contains(edge.WayId));

        internal uint MaskForAllWaysExceptAtVia(long wayId) =>
            MaskForLocalEdgesAtVia(edge => edge.WayId != wayId);

        private uint MaskForLocalEdgesAtVia(
            Func<GenerationEdgeRecord, bool> include)
        {
            Assert.True(Graph.TryGetGraphId(2, out GraphId viaNode));
            Assert.True(
                Graph.TryGetGraphNode(
                    viaNode,
                    out GenerationGraphNodeRecord graphNode));
            uint mask = 0;
            for (int localIndex = 0; localIndex < graphNode.IncidentEdgeCount; localIndex++)
            {
                NodeEdgeIncidenceRecord incidence = Graph.ReadIncidence(
                    checked(graphNode.IncidentEdgeOffset + localIndex));
                Assert.True(
                    Graph.TryReadEdgeByRecordId(
                        incidence.EdgeRecordId,
                        out GenerationEdgeRecord edge));
                if (localIndex < checked((int)GraphConstants.MaxTurnRestrictionEdges) &&
                    include(edge))
                {
                    mask |= 1U << localIndex;
                }
            }

            return mask;
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
                visitor.Way(12, [2UL, 3UL], RoadTags());
                visitor.Way(13, [2UL, 4UL], RoadTags());
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                for (int ordinal = 0; ordinal < restrictions.Count; ordinal++)
                {
                    RestrictionSpec restriction = restrictions[ordinal];
                    visitor.Relation(
                        20UL + checked((ulong)ordinal),
                        [
                            new OsmRelationMember(10, OsmMemberType.Way, "from"),
                            new OsmRelationMember(2, OsmMemberType.Node, "via"),
                            new OsmRelationMember(
                                checked((ulong)restriction.ToWayId),
                                OsmMemberType.Way,
                                "to"),
                        ],
                        restriction.Tags);
                }

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

        private static IReadOnlyDictionary<string, string> RoadTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = "primary",
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed record RestrictionSpec(
        long ToWayId,
        IReadOnlyDictionary<string, string> Tags)
    {
        internal static RestrictionSpec NoTurn(long toWayId) =>
            Create(toWayId, "no_left_turn");

        internal static RestrictionSpec OnlyTurn(long toWayId) =>
            Create(toWayId, "only_right_turn");

        internal static RestrictionSpec Except(long toWayId) =>
            Create(
                toWayId,
                "no_left_turn",
                new KeyValuePair<string, string>("except", "hgv"));

        internal static RestrictionSpec Qualified(long toWayId) =>
            Create(
                toWayId,
                "no_left_turn",
                new KeyValuePair<string, string>(
                    "restriction:hgv",
                    "no_left_turn"));

        internal static RestrictionSpec Conditional(long toWayId) =>
            Create(
                toWayId,
                "no_left_turn",
                new KeyValuePair<string, string>(
                    "restriction:conditional",
                    "no_left_turn @ (Mo-Fr 07:00-09:00)"));

        private static RestrictionSpec Create(
            long toWayId,
            string restriction,
            params KeyValuePair<string, string>[] additionalTags)
        {
            var tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "restriction",
                ["restriction"] = restriction,
            };
            foreach ((string key, string value) in additionalTags)
            {
                tags[key] = value;
            }

            return new RestrictionSpec(toWayId, tags);
        }
    }
}
