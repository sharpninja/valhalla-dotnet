using System.Runtime.CompilerServices;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledNodeFrontierTests
{
    [Fact]
    public void NodeArena_UsesReusableValueTypeSlabsWithoutPerNodeObjects()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<NodeWorkItem>());

        using var arena = new PooledNodeArena(slabCapacity: 4, memoryBudgetBytes: 4096);
        NodeHandle first = arena.Rent(CompletedNode(1001));
        arena.Release(first);
        NodeHandle reused = arena.Rent(CompletedNode(1002));

        Assert.Equal(first.ArenaId, reused.ArenaId);
        Assert.Equal(first.SlabIndex, reused.SlabIndex);
        Assert.Equal(first.SlotIndex, reused.SlotIndex);
        Assert.NotEqual(first.Generation, reused.Generation);
        Assert.Equal(2, arena.Metrics.TotalSlotRents);
        Assert.Equal(1, arena.Metrics.SlotReuseCount);
        Assert.Equal(1, arena.Metrics.TotalSlabsRented);
    }

    [Fact]
    public void NodeRemainsLiveUntilEveryIncidenceAndPathReferenceResolves()
    {
        NodeIncidenceRecord[] incidences =
        [
            new(1001, 71, 0, 0, NodeIncidenceRole.WayStart, 0),
            new(1001, 72, 0, 4, NodeIncidenceRole.WayIntermediate, 1),
            new(1001, 99, 0, 0, NodeIncidenceRole.RestrictionViaNode, 2),
        ];
        NodeIncidenceSummary summary = Assert.Single(
            NodeIncidenceIndexBuilder.BuildSummaries(incidences));

        Assert.Equal(3, summary.IncidenceCount);
        Assert.Equal(2, summary.DistinctWayCount);
        Assert.True(summary.AnchorFlags.HasFlag(NodeAnchorFlags.SharedWay));
        Assert.True(summary.AnchorFlags.HasFlag(NodeAnchorFlags.RestrictionBoundary));

        using var arena = new PooledNodeArena(slabCapacity: 4, memoryBudgetBytes: 4096);
        NodeHandle handle = arena.Rent(new NodeWorkItem
        {
            OsmNodeId = summary.OsmNodeId,
            StableGraphId = new GraphId(1, 0, 0),
            RemainingIncidenceUses = summary.InitialPendingReferenceCount,
            ActivePathReferences = 1,
            PendingFinalizers = 1,
            AnchorFlags = summary.AnchorFlags,
            LifecycleFlags = NodeLifecycleFlags.DurableNodeRecordWritten,
        });

        Assert.Throws<NodeWorkItemStillReferencedException>(() => arena.Release(handle));
        ref NodeWorkItem item = ref arena.Resolve(handle);
        item.RemainingIncidenceUses = 0;
        item.ActivePathReferences = 0;
        item.PendingFinalizers = 0;
        item.LifecycleFlags = NodeLifecycleFlags.AllDurableStateWritten;
        arena.Release(handle);

        Assert.Equal(0, arena.Metrics.LiveSlotCount);
    }

    [Fact]
    public void CompletedSecondaryNodesReleaseWhileNearestUnresolvedEntrancesRemain()
    {
        using var arena = new PooledNodeArena(slabCapacity: 4, memoryBudgetBytes: 4096);
        var sink = new RecordingFrontierEdgeSink();
        var frontier = new PooledPathFrontier(arena, sink);

        PooledPathFrontierResult result = frontier.ProcessWay(
            wayId: 501,
            [
                Anchor(1, new GraphId(4, 0, 0)),
                Secondary(2, 361234567, -867654321),
                Secondary(3, 361234777, -867654111),
                Anchor(4, new GraphId(4, 0, 1)),
            ],
            TestContext.Current.CancellationToken);

        GenerationEdgeRecord edge = Assert.Single(sink.Edges);
        Assert.Equal(new GraphId(4, 0, 0), edge.SourceNode);
        Assert.Equal(new GraphId(4, 0, 1), edge.TargetNode);
        Assert.Equal(4, sink.Shapes[edge.Shape].Count);
        Assert.Equal(2, result.SecondaryNodesProcessed);
        Assert.Equal(2, result.SecondarySlotsReleased);
        Assert.InRange(result.PeakLiveSlots, 1, 3);
        Assert.Equal(0, arena.Metrics.LiveSlotCount);
    }

    [Fact]
    public void ReusedSlot_ClearsStateAndRejectsStaleHandle()
    {
        using var arena = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);
        NodeHandle stale = arena.Rent(CompletedNode(1001) with
        {
            StableGraphId = new GraphId(9, 1, 3),
            AnchorFlags = NodeAnchorFlags.RestrictionBoundary,
        });
        arena.Release(stale);
        NodeHandle current = arena.Rent(default);

        Assert.Throws<StaleNodeHandleException>(() => arena.Resolve(stale));
        ref NodeWorkItem item = ref arena.Resolve(current);
        Assert.Equal(0, item.OsmNodeId);
        Assert.Equal(default, item.StableGraphId);
        Assert.Equal(0, item.RemainingIncidenceUses);
        Assert.Equal(0, item.ActivePathReferences);
        Assert.Equal(0, item.PendingFinalizers);
        Assert.Equal(NodeAnchorFlags.None, item.AnchorFlags);
        Assert.Equal(NodeLifecycleFlags.None, item.LifecycleFlags);
        Assert.Equal(1, arena.Metrics.StaleHandleRejections);
    }

    [Fact]
    public void PeakLiveNodes_IsBoundedByFrontierAndPreservesSemanticGraph()
    {
        const int secondaryCount = 100_000;
        var nodes = new PooledPathNode[secondaryCount + 2];
        nodes[0] = Anchor(1, new GraphId(7, 0, 0));
        for (int index = 0; index < secondaryCount; index++)
        {
            nodes[index + 1] = Secondary(
                index + 2,
                360000000 + index,
                -860000000 + index);
        }

        nodes[^1] = Anchor(secondaryCount + 2, new GraphId(7, 0, 1));

        using var arena = new PooledNodeArena(slabCapacity: 8, memoryBudgetBytes: 4096);
        var sink = new RecordingFrontierEdgeSink();
        var frontier = new PooledPathFrontier(arena, sink);
        PooledPathFrontierResult result = frontier.ProcessWay(
            8001,
            nodes,
            TestContext.Current.CancellationToken);

        GenerationEdgeRecord edge = Assert.Single(sink.Edges);
        Assert.Equal(secondaryCount + 2, sink.Shapes[edge.Shape].Count);
        Assert.Equal(secondaryCount, result.SecondaryNodesProcessed);
        Assert.Equal(secondaryCount, result.SecondarySlotsReleased);
        Assert.True(result.PeakLiveSlots <= 3, $"Peak live slots was {result.PeakLiveSlots}.");
        Assert.Equal(0, arena.Metrics.LiveSlotCount);
    }

    [Fact]
    public void NodeLifetimeReuseAndFrontierMatrix_IsComplete()
    {
        Assert.True(PooledNodeFrontierScenarioMatrix.RequiredScenarios.SetEquals(
        [
            "long-linear-way",
            "two-anchor-chain",
            "y-intersection",
            "four-way-intersection",
            "shared-routable-node",
            "shared-nonconnecting-node",
            "closed-loop",
            "self-intersection",
            "cross-tile-endpoint",
            "hierarchy-boundary",
            "restriction-via-node",
            "restriction-via-way",
            "relation-before-way",
            "relation-after-way",
            "out-of-order-entities",
            "duplicate-node-reference",
            "missing-node-reference",
            "traffic-control",
            "barrier-and-gate",
            "ferry-edge",
            "elevation-shape",
            "cancellation-boundaries",
            "memory-exhaustion",
            "scratch-exhaustion",
            "randomized-scheduling",
            "stale-handle-reuse",
        ]));
    }

    private static NodeWorkItem CompletedNode(long osmNodeId) => new()
    {
        OsmNodeId = osmNodeId,
        StableGraphId = GraphId.Invalid,
        LifecycleFlags = NodeLifecycleFlags.AllDurableStateWritten,
    };

    private static PooledPathNode Anchor(long osmNodeId, GraphId graphId) => new(
        new GenerationNodeRecord(
            osmNodeId,
            LatitudeE7: 360000000,
            LongitudeE7: -860000000,
            NodeSemanticFlags.None,
            TagReference: 0),
        IsGraphAnchor: true,
        graphId);

    private static PooledPathNode Secondary(
        long osmNodeId,
        int latitudeE7,
        int longitudeE7) => new(
        new GenerationNodeRecord(
            osmNodeId,
            latitudeE7,
            longitudeE7,
            NodeSemanticFlags.None,
            TagReference: 0),
        IsGraphAnchor: false,
        GraphId.Invalid);

    private sealed class RecordingFrontierEdgeSink : IFrontierEdgeSink
    {
        private long nextShapeOffset;

        public List<GenerationEdgeRecord> Edges { get; } = [];

        public Dictionary<EdgeShapeReference, List<GenerationNodeRecord>> Shapes { get; } = [];

        public IFrontierShapeWriter BeginShape(long wayId) => new RecordingShapeWriter(this);

        public void PersistEdge(GenerationEdgeRecord edge) => Edges.Add(edge);

        private sealed class RecordingShapeWriter(RecordingFrontierEdgeSink owner)
            : IFrontierShapeWriter
        {
            private readonly List<GenerationNodeRecord> nodes = [];
            private bool completed;

            public void Append(in GenerationNodeRecord node)
            {
                ObjectDisposedException.ThrowIf(completed, this);
                nodes.Add(node);
            }

            public EdgeShapeReference Complete()
            {
                ObjectDisposedException.ThrowIf(completed, this);
                completed = true;
                var shape = new EdgeShapeReference(
                    owner.nextShapeOffset,
                    nodes.Count,
                    checked(nodes.Count * Unsafe.SizeOf<GenerationNodeRecord>()));
                owner.nextShapeOffset += shape.ByteLength;
                owner.Shapes.Add(shape, nodes);
                return shape;
            }

            public void Dispose() => completed = true;
        }
    }
}
