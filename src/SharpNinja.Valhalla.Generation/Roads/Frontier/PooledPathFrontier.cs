namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal readonly record struct PooledPathFrontierResult(
    long WayNodeOccurrencesProcessed,
    long GraphAnchorsProcessed,
    long SecondaryNodesProcessed,
    long SecondarySlotsReleased,
    long EdgeRecordsWritten,
    int PeakLiveSlots,
    int MaximumUnresolvedPathAnchors);

internal sealed class PooledPathFrontier(
    PooledNodeArena arena,
    IFrontierEdgeSink edgeSink)
{
    internal PooledPathFrontierResult ProcessWay(
        long wayId,
        ReadOnlySpan<PooledPathNode> nodes,
        CancellationToken cancellationToken = default)
    {
        if (nodes.Length < 2)
        {
            throw new ArgumentException(
                "A routable way requires at least two node occurrences.",
                nameof(nodes));
        }

        if (!nodes[0].IsGraphAnchor)
        {
            throw new ArgumentException(
                "The first way occurrence must be a graph anchor.",
                nameof(nodes));
        }

        long anchors = 1;
        long secondaryNodes = 0;
        long releasedSecondarySlots = 0;
        long edgeRecords = 0;
        int maximumUnresolvedAnchors = 1;
        NodeHandle currentAnchor = default;
        bool hasCurrentAnchor = false;
        IFrontierShapeWriter? shapeWriter = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentAnchor = arena.Rent(CreateAnchor(nodes[0]));
            hasCurrentAnchor = true;
            shapeWriter = edgeSink.BeginShape(wayId);
            shapeWriter.Append(nodes[0].Node);
            int segmentOrdinal = 0;

            for (int index = 1; index < nodes.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PooledPathNode occurrence = nodes[index];
                IFrontierShapeWriter activeShapeWriter = shapeWriter ??
                    throw new InvalidOperationException(
                        "The active edge shape writer was not available.");
                if (!occurrence.IsGraphAnchor)
                {
                    NodeHandle secondaryHandle = arena.Rent(new NodeWorkItem
                    {
                        OsmNodeId = occurrence.Node.OsmNodeId,
                        StableGraphId = occurrence.StableGraphId,
                        LifecycleFlags = NodeLifecycleFlags.DurableNodeRecordWritten,
                    });
                    try
                    {
                        activeShapeWriter.Append(occurrence.Node);
                        ref NodeWorkItem secondary = ref arena.Resolve(secondaryHandle);
                        secondary.LifecycleFlags =
                            NodeLifecycleFlags.AllDurableStateWritten;
                        arena.Release(secondaryHandle);
                        releasedSecondarySlots++;
                    }
                    catch
                    {
                        arena.Abandon(secondaryHandle);
                        throw;
                    }

                    secondaryNodes++;
                    continue;
                }

                anchors++;
                NodeHandle nextAnchor = arena.Rent(CreateAnchor(occurrence));
                bool hasNextAnchor = true;
                maximumUnresolvedAnchors = Math.Max(maximumUnresolvedAnchors, 2);
                try
                {
                    activeShapeWriter.Append(occurrence.Node);
                    EdgeShapeReference shape = activeShapeWriter.Complete();
                    activeShapeWriter.Dispose();
                    shapeWriter = null;

                    ref NodeWorkItem source = ref arena.Resolve(currentAnchor);
                    ref NodeWorkItem target = ref arena.Resolve(nextAnchor);
                    long edgeRecordId = checked((wayId * 1_000_000L) + segmentOrdinal);
                    edgeSink.PersistEdge(new GenerationEdgeRecord(
                        edgeRecordId,
                        source.StableGraphId,
                        target.StableGraphId,
                        wayId,
                        shape,
                        EdgeSemanticFlags.None,
                        ForwardAccess: 0,
                        ReverseAccess: 0,
                        AttributeReference: 0,
                        Importance: 0,
                        HasNames: false,
                        CanonicalOrdinal: edgeRecordId));
                    edgeRecords++;

                    CompleteAnchor(ref source);
                    arena.Release(currentAnchor);
                    hasCurrentAnchor = false;

                    currentAnchor = nextAnchor;
                    hasCurrentAnchor = true;
                    hasNextAnchor = false;
                    segmentOrdinal++;

                    if (index < nodes.Length - 1)
                    {
                        shapeWriter = edgeSink.BeginShape(wayId);
                        shapeWriter.Append(occurrence.Node);
                    }
                }
                finally
                {
                    if (hasNextAnchor)
                    {
                        arena.Abandon(nextAnchor);
                    }
                }
            }

            if (shapeWriter is not null)
            {
                throw new ArgumentException(
                    "The last way occurrence must be a graph anchor.",
                    nameof(nodes));
            }

            ref NodeWorkItem finalAnchor = ref arena.Resolve(currentAnchor);
            CompleteAnchor(ref finalAnchor);
            arena.Release(currentAnchor);
            hasCurrentAnchor = false;

            return new PooledPathFrontierResult(
                nodes.Length,
                anchors,
                secondaryNodes,
                releasedSecondarySlots,
                edgeRecords,
                arena.Metrics.PeakLiveSlotCount,
                maximumUnresolvedAnchors);
        }
        finally
        {
            shapeWriter?.Dispose();
            if (hasCurrentAnchor)
            {
                arena.Abandon(currentAnchor);
            }
        }
    }

    private static NodeWorkItem CreateAnchor(PooledPathNode occurrence) => new()
    {
        OsmNodeId = occurrence.Node.OsmNodeId,
        StableGraphId = occurrence.StableGraphId,
        RemainingIncidenceUses = 1,
        ActivePathReferences = 1,
        PendingFinalizers = 1,
        AnchorFlags = NodeAnchorFlags.ActivePathEndpoint,
        LifecycleFlags = NodeLifecycleFlags.DurableNodeRecordWritten,
    };

    private static void CompleteAnchor(ref NodeWorkItem item)
    {
        item.RemainingIncidenceUses = 0;
        item.ActivePathReferences = 0;
        item.PendingFinalizers = 0;
        item.LifecycleFlags = NodeLifecycleFlags.AllDurableStateWritten;
    }
}
