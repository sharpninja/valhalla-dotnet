namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed class PooledPathWaySession : IDisposable
{
    private readonly PooledNodeArena arena;
    private readonly IFrontierEdgeSink edgeSink;
    private readonly long wayId;
    private NodeHandle currentAnchor;
    private GenerationNodeRecord currentAnchorNode;
    private IFrontierShapeWriter? shapeWriter;
    private bool hasCurrentAnchor;
    private bool completed;
    private bool disposed;
    private long wayNodeOccurrences;
    private long anchors;
    private long secondaryNodes;
    private long releasedSecondarySlots;
    private long edgeRecords;
    private int segmentOrdinal;
    private int maximumUnresolvedAnchors;

    internal PooledPathWaySession(
        PooledNodeArena arena,
        IFrontierEdgeSink edgeSink,
        long wayId)
    {
        this.arena = arena ?? throw new ArgumentNullException(nameof(arena));
        this.edgeSink = edgeSink ?? throw new ArgumentNullException(nameof(edgeSink));
        this.wayId = wayId;
    }

    internal void Append(
        in PooledPathNode occurrence,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
        {
            throw new InvalidOperationException("The pooled path way is already complete.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (wayNodeOccurrences == 0)
        {
            Start(occurrence);
            return;
        }

        IFrontierShapeWriter activeShapeWriter = EnsureShapeWriter();
        if (!occurrence.IsGraphAnchor)
        {
            AppendSecondary(occurrence, activeShapeWriter);
            wayNodeOccurrences = checked(wayNodeOccurrences + 1);
            return;
        }

        AppendAnchor(occurrence, activeShapeWriter);
        wayNodeOccurrences = checked(wayNodeOccurrences + 1);
    }

    internal PooledPathFrontierResult Complete(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
        {
            throw new InvalidOperationException("The pooled path way is already complete.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (wayNodeOccurrences < 2)
        {
            throw new ArgumentException(
                "A routable way requires at least two node occurrences.");
        }

        if (shapeWriter is not null)
        {
            throw new ArgumentException(
                "The last way occurrence must be a graph anchor.");
        }

        ref NodeWorkItem finalAnchor = ref arena.Resolve(currentAnchor);
        CompleteAnchor(ref finalAnchor);
        arena.Release(currentAnchor);
        hasCurrentAnchor = false;
        completed = true;

        return new PooledPathFrontierResult(
            wayNodeOccurrences,
            anchors,
            secondaryNodes,
            releasedSecondarySlots,
            edgeRecords,
            arena.Metrics.PeakLiveSlotCount,
            maximumUnresolvedAnchors);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        shapeWriter?.Dispose();
        shapeWriter = null;
        if (hasCurrentAnchor)
        {
            arena.Abandon(currentAnchor);
            hasCurrentAnchor = false;
        }

        disposed = true;
    }

    private void Start(in PooledPathNode occurrence)
    {
        if (!occurrence.IsGraphAnchor)
        {
            throw new ArgumentException(
                "The first way occurrence must be a graph anchor.",
                nameof(occurrence));
        }

        currentAnchor = arena.Rent(CreateAnchor(occurrence));
        currentAnchorNode = occurrence.Node;
        hasCurrentAnchor = true;
        wayNodeOccurrences = 1;
        anchors = 1;
        maximumUnresolvedAnchors = 1;
    }

    private IFrontierShapeWriter EnsureShapeWriter()
    {
        if (!hasCurrentAnchor)
        {
            throw new InvalidOperationException(
                "The pooled path has no unresolved source anchor.");
        }

        if (shapeWriter is not null)
        {
            return shapeWriter;
        }

        shapeWriter = edgeSink.BeginShape(wayId);
        shapeWriter.Append(currentAnchorNode);
        return shapeWriter;
    }

    private void AppendSecondary(
        in PooledPathNode occurrence,
        IFrontierShapeWriter activeShapeWriter)
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
            secondary.LifecycleFlags = NodeLifecycleFlags.AllDurableStateWritten;
            arena.Release(secondaryHandle);
            releasedSecondarySlots = checked(releasedSecondarySlots + 1);
        }
        catch
        {
            arena.Abandon(secondaryHandle);
            throw;
        }

        secondaryNodes = checked(secondaryNodes + 1);
    }

    private void AppendAnchor(
        in PooledPathNode occurrence,
        IFrontierShapeWriter activeShapeWriter)
    {
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
            edgeRecords = checked(edgeRecords + 1);

            CompleteAnchor(ref source);
            arena.Release(currentAnchor);
            hasCurrentAnchor = false;

            currentAnchor = nextAnchor;
            currentAnchorNode = occurrence.Node;
            hasCurrentAnchor = true;
            hasNextAnchor = false;
            segmentOrdinal = checked(segmentOrdinal + 1);
            anchors = checked(anchors + 1);
        }
        finally
        {
            if (hasNextAnchor)
            {
                arena.Abandon(nextAnchor);
            }
        }
    }

    private static NodeWorkItem CreateAnchor(in PooledPathNode occurrence) => new()
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
