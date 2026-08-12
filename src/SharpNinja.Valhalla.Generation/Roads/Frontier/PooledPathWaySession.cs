namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed class PooledPathWaySession : IDisposable
{
    private readonly PooledNodeArena arena;
    private readonly IFrontierEdgeSink edgeSink;
    private readonly long wayId;
    private readonly long canonicalOrdinal;
    private readonly PooledWayEdgeSemantics semantics;
    private NodeHandle currentAnchor;
    private EdgeSemanticFlags segmentFlags;
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
        long wayId,
        long canonicalOrdinal,
        PooledWayEdgeSemantics semantics)
    {
        this.arena = arena ?? throw new ArgumentNullException(nameof(arena));
        this.edgeSink = edgeSink ?? throw new ArgumentNullException(nameof(edgeSink));
        ArgumentOutOfRangeException.ThrowIfNegative(wayId);
        ArgumentOutOfRangeException.ThrowIfNegative(canonicalOrdinal);
        if ((ulong)canonicalOrdinal > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalOrdinal),
                "A canonical way ordinal must fit in 32 bits.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(semantics.AttributeReference);
        this.wayId = wayId;
        this.canonicalOrdinal = canonicalOrdinal;
        this.semantics = semantics;
        segmentFlags = semantics.Flags;
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
        TrackNodeSemanticFlags(occurrence.Node.Flags);
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
        TrackNodeSemanticFlags(occurrence.Node.Flags);
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
            long edgeRecordId = ComposeEdgeRecordId(
                canonicalOrdinal,
                segmentOrdinal);
            edgeSink.PersistEdge(new GenerationEdgeRecord(
                edgeRecordId,
                source.StableGraphId,
                target.StableGraphId,
                wayId,
                shape,
                segmentFlags,
                semantics.ForwardAccess,
                semantics.ReverseAccess,
                semantics.AttributeReference,
                semantics.Importance,
                semantics.HasNames,
                CanonicalOrdinal: edgeRecordId));
            edgeRecords = checked(edgeRecords + 1);

            CompleteAnchor(ref source);
            arena.Release(currentAnchor);
            hasCurrentAnchor = false;

            currentAnchor = nextAnchor;
            currentAnchorNode = occurrence.Node;
            segmentFlags = semantics.Flags;
            TrackNodeSemanticFlags(occurrence.Node.Flags);
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

    private void TrackNodeSemanticFlags(NodeSemanticFlags flags)
    {
        const NodeSemanticFlags TrafficControlFlags =
            NodeSemanticFlags.TrafficSignal |
            NodeSemanticFlags.StopSign |
            NodeSemanticFlags.YieldSign;
        if ((flags & TrafficControlFlags) != 0)
        {
            segmentFlags |= EdgeSemanticFlags.HasTrafficControl;
        }
    }

    private static long ComposeEdgeRecordId(
        long canonicalWayOrdinal,
        int segmentOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(canonicalWayOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentOrdinal);
        if ((ulong)canonicalWayOrdinal > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalWayOrdinal),
                "A canonical way ordinal must fit in 32 bits.");
        }

        return checked((canonicalWayOrdinal << 32) | (uint)segmentOrdinal);
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
