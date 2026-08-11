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
    internal PooledPathWaySession BeginWay(long wayId) =>
        BeginWay(wayId, wayId, default);

    internal PooledPathWaySession BeginWay(
        long wayId,
        long canonicalOrdinal,
        PooledWayEdgeSemantics semantics) =>
        new(arena, edgeSink, wayId, canonicalOrdinal, semantics);

    internal PooledPathFrontierResult ProcessWay(
        long wayId,
        ReadOnlySpan<PooledPathNode> nodes,
        CancellationToken cancellationToken = default)
    {
        using PooledPathWaySession session = BeginWay(wayId);
        foreach (PooledPathNode node in nodes)
        {
            session.Append(node, cancellationToken);
        }

        return session.Complete(cancellationToken);
    }
}
