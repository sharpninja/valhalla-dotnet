using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record PooledRoadEdgeBuilderOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    uint GridDivisions = 0,
    int ArenaSlabCapacity = 1024,
    int ShapeBufferSizeBytes = 64 * 1024,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed record PooledRoadEdgeBuildResourceMetrics(
    long NodeLookupPhasePeakMemoryBytes,
    long CandidatePhasePeakMemoryBytes,
    long IdentityPhasePeakMemoryBytes,
    long FrontierPhasePeakMemoryBytes,
    long GraphNodePhasePeakMemoryBytes)
{
    internal long PeakAggregateMemoryBytes =>
        Math.Max(
            NodeLookupPhasePeakMemoryBytes,
            Math.Max(
                CandidatePhasePeakMemoryBytes,
                Math.Max(
                    IdentityPhasePeakMemoryBytes,
                    Math.Max(
                        FrontierPhasePeakMemoryBytes,
                        GraphNodePhasePeakMemoryBytes))));
}

internal sealed class PooledRoadEdgeBuildResult : IDisposable
{
    private readonly CompactNodeLookupIndex nodeIndex;
    private readonly StableGraphIdentityIndex graphIdentities;
    private readonly DurableFrontierEdgeSink edges;
    private readonly NodeEdgeIncidenceIndex graphNodes;
    private bool disposed;

    internal PooledRoadEdgeBuildResult(
        CompactNodeLookupIndex nodeIndex,
        StableGraphIdentityIndex graphIdentities,
        DurableFrontierEdgeSink edges,
        NodeEdgeIncidenceIndex graphNodes,
        ValhallaGenerationFrontierMetrics frontierMetrics,
        PooledRoadEdgeBuildResourceMetrics resourceMetrics)
    {
        this.nodeIndex = nodeIndex;
        this.graphIdentities = graphIdentities;
        this.edges = edges;
        this.graphNodes = graphNodes;
        FrontierMetrics = frontierMetrics;
        ResourceMetrics = resourceMetrics;
    }

    internal PooledRoadEdgeBuildResourceMetrics ResourceMetrics { get; }

    internal long PeakAggregateMemoryBytes =>
        ResourceMetrics.PeakAggregateMemoryBytes;

    internal long CurrentMemoryBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                nodeIndex.CurrentMemoryBytes +
                graphIdentities.CurrentMemoryBytes +
                edges.CurrentMemoryBytes +
                graphNodes.CurrentMemoryBytes);
        }
    }

    internal long CurrentScratchBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                nodeIndex.CurrentScratchBytes +
                graphIdentities.CurrentScratchBytes +
                edges.CurrentScratchBytes +
                graphNodes.CurrentScratchBytes);
        }
    }


    internal long EdgeCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return edges.EdgeCount;
        }
    }

    internal long GraphNodeCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return graphNodes.GraphNodeCount;
        }
    }

    internal ValhallaGenerationFrontierMetrics FrontierMetrics { get; }

    internal long IdentityCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return graphIdentities.IdentityCount;
        }
    }

    internal StableGraphNodeIdentity ReadIdentity(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return graphIdentities.ReadIdentity(ordinal);
    }

    internal bool TryGetGraphId(long osmNodeId, out GraphId graphId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return graphIdentities.TryGetGraphId(osmNodeId, out graphId);
    }


    internal bool TryGetCanonicalNode(
        long osmNodeId,
        out GenerationNodeRecord node)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return nodeIndex.TryGetNode(osmNodeId, out node);
    }

    internal bool TryGetCanonicalNode(
        GraphId graphId,
        out GenerationNodeRecord node)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (graphIdentities.TryGetIdentity(
                graphId,
                out StableGraphNodeIdentity identity))
        {
            return nodeIndex.TryGetNode(identity.OsmNodeId, out node);
        }

        node = default;
        return false;
    }

    internal bool TryGetGraphNode(
        GraphId nodeId,
        out GenerationGraphNodeRecord graphNode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return graphNodes.TryGetGraphNode(nodeId, out graphNode);
    }

    internal NodeEdgeIncidenceRecord ReadIncidence(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return graphNodes.ReadIncidence(ordinal);
    }

    internal GenerationEdgeRecord ReadEdge(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return edges.ReadEdge(ordinal);
    }

    internal bool TryReadEdgeByRecordId(
        long edgeRecordId,
        out GenerationEdgeRecord edge)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return edges.TryReadEdgeByRecordId(edgeRecordId, out edge);
    }

    internal GenerationNodeRecord[] ReadShape(EdgeShapeReference reference)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return edges.ReadShape(reference);
    }

    internal GenerationGraphNodeRecord ReadGraphNode(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return graphNodes.ReadGraphNode(ordinal);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        graphNodes.Dispose();
        edges.Dispose();
        graphIdentities.Dispose();
        nodeIndex.Dispose();
        disposed = true;
    }
}

internal static class PooledRoadEdgeBuilder
{
    private const int BudgetPartitionCount = 16;
    private const int NodeLookupPartitions = 4;
    private const int CandidatePartitions = 1;
    private const int GraphIdentityPartitions = 4;
    private const int EdgeStorePartitions = 4;
    private const int NodeEdgeIndexPartitions = 2;
    private const int ArenaPartitions = 1;

    internal static async ValueTask<PooledRoadEdgeBuildResult> BuildAsync(
        CompactOsmSemanticStore semanticStore,
        PooledRoadEdgeBuilderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticStore);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.GetFullPath(options.WorkingDirectory);
        Directory.CreateDirectory(root);
        long memoryPartition = options.MemoryBudgetBytes / BudgetPartitionCount;
        long scratchPartition = options.ScratchDiskBudgetBytes / BudgetPartitionCount;
        long nodeLookupMemory = memoryPartition * NodeLookupPartitions;
        long candidateMemory = memoryPartition * CandidatePartitions;
        long identityMemory = memoryPartition * GraphIdentityPartitions;
        long edgeMemory = memoryPartition * EdgeStorePartitions;
        long nodeEdgeMemory = memoryPartition * NodeEdgeIndexPartitions;
        long arenaMemory = checked(
            options.MemoryBudgetBytes -
            nodeLookupMemory -
            candidateMemory -
            identityMemory -
            edgeMemory -
            nodeEdgeMemory);
        long nodeLookupScratch = scratchPartition * NodeLookupPartitions;
        long candidateScratch = scratchPartition * CandidatePartitions;
        long identityScratch = scratchPartition * GraphIdentityPartitions;
        long edgeScratch = scratchPartition * EdgeStorePartitions;
        long nodeEdgeScratch = scratchPartition * NodeEdgeIndexPartitions;

        CompactNodeLookupIndex? nodeIndex = null;
        IntermediateSequenceStore<GenerationGraphNodeCandidate>? candidates = null;
        StableGraphIdentityIndex? graphIdentities = null;
        DurableFrontierEdgeSink? edges = null;
        NodeEdgeIncidenceIndex? graphNodes = null;
        try
        {
            nodeIndex = await CompactNodeLookupIndex.BuildAsync(
                    semanticStore,
                    new CompactNodeLookupIndexOptions(
                        Path.Combine(root, "nodes"),
                        options.StorageMode,
                        nodeLookupMemory,
                        nodeLookupScratch,
                        options.SegmentSizeBytes),
                    cancellationToken)
                .ConfigureAwait(false);

            candidates = new IntermediateSequenceStore<GenerationGraphNodeCandidate>(
                new IntermediateSequenceStoreOptions(
                    Path.Combine(root, "graph-identities"),
                    "graph-node-candidates",
                    options.StorageMode,
                    candidateMemory,
                    candidateScratch,
                    options.SegmentSizeBytes));
            EmitGraphNodeCandidates(
                semanticStore,
                nodeIndex,
                candidates,
                options.GridDivisions,
                cancellationToken);
            await candidates.CompleteAsync(cancellationToken).ConfigureAwait(false);
            long candidatePhasePeakMemory = checked(
                nodeIndex.CurrentMemoryBytes +
                candidates.State.PeakMemoryBytes);
            long candidateRetainedDuringIdentityMemory =
                candidates.State.CurrentMemoryBytes;
            graphIdentities = await StableGraphIdentityIndex.BuildAsync(
                    candidates,
                    new StableGraphIdentityIndexOptions(
                        Path.Combine(root, "graph-identities"),
                        options.StorageMode,
                        identityMemory,
                        identityScratch,
                        options.SegmentSizeBytes),
                    cancellationToken)
                .ConfigureAwait(false);
            candidates.Dispose();
            candidates = null;

            edges = new DurableFrontierEdgeSink(
                new DurableFrontierEdgeSinkOptions(
                    Path.Combine(root, "edges"),
                    options.StorageMode,
                    edgeMemory,
                    edgeScratch,
                    options.ShapeBufferSizeBytes,
                    options.SegmentSizeBytes));
            using var arena = new PooledNodeArena(
                options.ArenaSlabCapacity,
                arenaMemory);
            var frontier = new PooledPathFrontier(arena, edges);
            PooledPathAggregate aggregate = BuildEdges(
                semanticStore,
                nodeIndex,
                graphIdentities,
                frontier,
                cancellationToken);
            DurableFrontierEdgeStoreReceipt edgeReceipt =
                await edges.CompleteAsync(cancellationToken).ConfigureAwait(false);

            graphNodes = await NodeEdgeIncidenceIndex.BuildAsync(
                    edges,
                    new NodeEdgeIncidenceIndexOptions(
                        Path.Combine(root, "graph-nodes"),
                        options.StorageMode,
                        nodeEdgeMemory,
                        nodeEdgeScratch,
                        options.SegmentSizeBytes),
                    cancellationToken)
                .ConfigureAwait(false);

            ValhallaGenerationFrontierMetrics metrics = CreateMetrics(
                semanticStore,
                nodeIndex,
                graphIdentities,
                graphNodes,
                edgeReceipt,
                arena.Metrics,
                aggregate,
                arenaMemory,
                edgeMemory);
            long identityPhasePeakMemory = checked(
                nodeIndex.CurrentMemoryBytes +
                candidateRetainedDuringIdentityMemory +
                graphIdentities.PeakMemoryBytes);
            long frontierPhasePeakMemory = checked(
                nodeIndex.CurrentMemoryBytes +
                graphIdentities.CurrentMemoryBytes +
                edges.PeakMemoryBytes +
                arena.Metrics.PeakSlabBytes);
            long graphNodePhasePeakMemory = checked(
                nodeIndex.CurrentMemoryBytes +
                graphIdentities.CurrentMemoryBytes +
                edges.CurrentMemoryBytes +
                graphNodes.PeakMemoryBytes);
            var resourceMetrics = new PooledRoadEdgeBuildResourceMetrics(
                nodeIndex.PeakMemoryBytes,
                candidatePhasePeakMemory,
                identityPhasePeakMemory,
                frontierPhasePeakMemory,
                graphNodePhasePeakMemory);
            var result = new PooledRoadEdgeBuildResult(
                nodeIndex,
                graphIdentities,
                edges,
                graphNodes,
                metrics,
                resourceMetrics);
            nodeIndex = null;
            graphIdentities = null;
            edges = null;
            graphNodes = null;
            return result;
        }
        catch
        {
            graphNodes?.Dispose();
            edges?.Dispose();
            graphIdentities?.Dispose();
            candidates?.Dispose();
            nodeIndex?.Dispose();
            throw;
        }
    }

    private static void EmitGraphNodeCandidates(
        CompactOsmSemanticStore semanticStore,
        CompactNodeLookupIndex nodeIndex,
        IIntermediateSequenceStore<GenerationGraphNodeCandidate> candidates,
        uint gridDivisions,
        CancellationToken cancellationToken)
    {
        TileLevel localLevel = TileHierarchy.Levels()[^1];
        for (long ordinal = 0; ordinal < nodeIndex.UniqueNodeCount; ordinal++)
        {
            if ((ordinal & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            GenerationNodeRecord node = nodeIndex.ReadNode(ordinal);
            if (!IsRoutableGraphAnchor(semanticStore, node))
            {
                continue;
            }

            candidates.Append(GenerationGraphNodeCandidate.Create(
                node,
                localLevel.Level,
                GetGridId(node, localLevel.Tiles, gridDivisions),
                canonicalOrdinal: 0));
        }
    }

    private static PooledPathAggregate BuildEdges(
        CompactOsmSemanticStore semanticStore,
        CompactNodeLookupIndex nodeIndex,
        StableGraphIdentityIndex graphIdentities,
        PooledPathFrontier frontier,
        CancellationToken cancellationToken)
    {
        var aggregate = new PooledPathAggregate();
        for (long wayOrdinal = 0; wayOrdinal < semanticStore.WayCount; wayOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GenerationWayRecord way = semanticStore.ReadWay(wayOrdinal);
            PooledWayEdgeSemantics semantics = PooledWayEdgeSemantics.Project(
                semanticStore.ReadTags(way.TagReference),
                way.TagReference);
            using PooledPathWaySession session = frontier.BeginWay(
                way.OsmWayId,
                way.CanonicalOrdinal,
                semantics);
            for (var nodeOrdinal = 0; nodeOrdinal < way.NodeReferenceCount; nodeOrdinal++)
            {
                if ((nodeOrdinal & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                GenerationWayNodeReference reference =
                    semanticStore.ReadWayNodeReference(
                        checked(way.NodeReferenceOffset + nodeOrdinal));
                ValidateWayReference(way, reference, nodeOrdinal);
                if (!nodeIndex.TryGetNode(reference.OsmNodeId, out GenerationNodeRecord node))
                {
                    throw new InvalidDataException(
                        $"OSM node {reference.OsmNodeId} referenced by way {way.OsmWayId} was not found.");
                }

                bool isAnchor = IsRoutableGraphAnchor(semanticStore, node);
                GraphId graphId = GraphId.Invalid;
                if (isAnchor &&
                    !graphIdentities.TryGetGraphId(
                        reference.OsmNodeId,
                        canonicalOrdinal: 0,
                        out graphId))
                {
                    throw new InvalidDataException(
                        $"OSM node {reference.OsmNodeId} has no stable graph identity.");
                }

                session.Append(
                    new PooledPathNode(node, isAnchor, graphId),
                    cancellationToken);
            }

            aggregate.Add(session.Complete(cancellationToken));
        }

        return aggregate;
    }

    private static bool IsRoutableGraphAnchor(
        CompactOsmSemanticStore semanticStore,
        in GenerationNodeRecord node)
    {
        if (!semanticStore.TryFindIncidenceSummary(
                node.OsmNodeId,
                out NodeIncidenceSummary summary) ||
            summary.DistinctWayCount == 0)
        {
            return false;
        }

        NodeSemanticFlags semanticAnchorFlags =
            NodeSemanticFlags.TrafficSignal |
            NodeSemanticFlags.StopSign |
            NodeSemanticFlags.YieldSign |
            NodeSemanticFlags.Barrier |
            NodeSemanticFlags.Gate |
            NodeSemanticFlags.AccessTransition;
        return summary.AnchorFlags != NodeAnchorFlags.None ||
               (node.Flags & semanticAnchorFlags) != 0;
    }

    private static void ValidateWayReference(
        in GenerationWayRecord way,
        in GenerationWayNodeReference reference,
        int expectedNodeOrdinal)
    {
        if (reference.OsmWayId != way.OsmWayId ||
            reference.NodeOrdinal != expectedNodeOrdinal)
        {
            throw new InvalidDataException(
                $"Way {way.OsmWayId} has an invalid durable node-reference range.");
        }
    }

    private static uint GetGridId(
        in GenerationNodeRecord node,
        Tiles<PointLL, double> tiling,
        uint gridDivisions)
    {
        if (gridDivisions == 0)
        {
            return 0;
        }

        PointLL point = PointLL.Create(
            node.LongitudeE7 / 10_000_000d,
            node.LatitudeE7 / 10_000_000d);
        int tileId = tiling.TileId(point);
        if (tileId < 0)
        {
            return 0;
        }

        PointLL basePoint = tiling.Base(tileId);
        double gridSize = tiling.TileSize() / gridDivisions;
        uint row = (uint)((point.Lat - basePoint.Lat) / gridSize);
        uint column = (uint)((point.Lng - basePoint.Lng) / gridSize);
        return row > gridDivisions || column > gridDivisions
            ? 0
            : (row * gridDivisions) + column;
    }

    private static ValhallaGenerationFrontierMetrics CreateMetrics(
        CompactOsmSemanticStore semanticStore,
        CompactNodeLookupIndex nodeIndex,
        StableGraphIdentityIndex graphIdentities,
        NodeEdgeIncidenceIndex graphNodes,
        DurableFrontierEdgeStoreReceipt edgeReceipt,
        PooledNodeArenaMetrics arenaMetrics,
        PooledPathAggregate aggregate,
        long arenaMemory,
        long edgeMemory)
    {
        long incidenceBytes = checked(
            semanticStore.IncidenceCount *
            Unsafe.SizeOf<NodeIncidenceRecord>());
        long nodeBytes = checked(
            nodeIndex.Manifest.RecordCount *
            nodeIndex.Manifest.RecordSize);
        long edgeBytes = checked(
            edgeReceipt.EdgeManifest.RecordCount *
            edgeReceipt.EdgeManifest.RecordSize);
        long mappedHighWater = checked(
            nodeIndex.ScratchHighWaterMarkBytes +
            edgeReceipt.ScratchHighWaterMarkBytes +
            graphIdentities.CandidateSortReceipt.ScratchHighWaterMarkBytes +
            graphIdentities.LookupSortReceipt.ScratchHighWaterMarkBytes +
            graphNodes.SortReceipt.ScratchHighWaterMarkBytes);

        return new ValhallaGenerationFrontierMetrics(
            semanticStore.NodeCount,
            aggregate.WayNodeOccurrencesProcessed,
            graphIdentities.IdentityCount,
            aggregate.SecondaryNodesProcessed,
            aggregate.SecondarySlotsReleased,
            arenaMetrics.TotalSlotRents,
            arenaMetrics.SlotReuseCount,
            arenaMetrics.PeakLiveSlotCount,
            arenaMetrics.TotalSlabsRented,
            arenaMetrics.PeakSlabBytes,
            aggregate.MaximumUnresolvedPathAnchors,
            incidenceBytes,
            nodeBytes,
            edgeReceipt.ShapeManifest.ByteLength,
            edgeBytes,
            SelectedDegreeOfParallelism: 1,
            PerWorkerMemoryReservationBytes: checked(arenaMemory + edgeMemory),
            mappedHighWater,
            arenaMetrics.StaleHandleRejections);
    }

    private static void ValidateOptions(PooledRoadEdgeBuilderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ArenaSlabCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ShapeBufferSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);

        int largestRecord = new[]
        {
            Unsafe.SizeOf<CompactNodeLookupRecord>(),
            Unsafe.SizeOf<GenerationGraphNodeCandidate>(),
            Unsafe.SizeOf<GenerationEdgeRecord>(),
            Unsafe.SizeOf<NodeEdgeIncidenceRecord>(),
        }.Max();
        if (options.MemoryBudgetBytes / BudgetPartitionCount < largestRecord)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The pooled road-edge memory budget cannot fit one record per partition.");
        }

        if (options.ScratchDiskBudgetBytes / BudgetPartitionCount < largestRecord)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The pooled road-edge scratch budget cannot fit one record per partition.");
        }

        if (NodeLookupPartitions +
            CandidatePartitions +
            GraphIdentityPartitions +
            EdgeStorePartitions +
            NodeEdgeIndexPartitions +
            ArenaPartitions != BudgetPartitionCount)
        {
            throw new InvalidOperationException(
                "The pooled road-edge resource partitions are invalid.");
        }
    }

    private sealed class PooledPathAggregate
    {
        internal long WayNodeOccurrencesProcessed { get; private set; }

        internal long SecondaryNodesProcessed { get; private set; }

        internal long SecondarySlotsReleased { get; private set; }

        internal int MaximumUnresolvedPathAnchors { get; private set; }

        internal void Add(PooledPathFrontierResult result)
        {
            WayNodeOccurrencesProcessed = checked(
                WayNodeOccurrencesProcessed +
                result.WayNodeOccurrencesProcessed);
            SecondaryNodesProcessed = checked(
                SecondaryNodesProcessed +
                result.SecondaryNodesProcessed);
            SecondarySlotsReleased = checked(
                SecondarySlotsReleased +
                result.SecondarySlotsReleased);
            MaximumUnresolvedPathAnchors = Math.Max(
                MaximumUnresolvedPathAnchors,
                result.MaximumUnresolvedPathAnchors);
        }
    }
}
