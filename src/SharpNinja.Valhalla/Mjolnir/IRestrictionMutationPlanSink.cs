using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

internal enum RestrictionMutationDirection : byte
{
    Forward = 0,
    Reverse = 1,
}

internal interface IRestrictionMutationPlanSink
{
    void EmitRestriction(
        RestrictionMutationDirection direction,
        GraphId tileId,
        GraphId from,
        GraphId to,
        ReadOnlySpan<GraphId> vias,
        RestrictionType type,
        uint modes,
        byte probability,
        ulong timeDomain,
        bool crossTile,
        ulong canonicalOrdinal);

    void EmitEdgePatch(
        GraphId tileId,
        uint directedEdgeIndex,
        uint startRestrictionMask,
        uint endRestrictionMask,
        bool setComplexRestriction,
        bool crossTile,
        ulong canonicalOrdinal);
}

internal readonly record struct RestrictionMutationPlanReceipt(
    uint ForwardRestrictionCount,
    uint ReverseRestrictionCount,
    uint EdgePatchCount,
    uint CrossTileForwardRestrictionCount,
    uint CrossTileEdgePatchCount,
    int TraversalDepthCapacity,
    int VisitedNodeCapacity,
    int TraversedEdgeCapacity,
    int PeakTraversalDepth,
    int PeakVisitedNodes,
    int PeakTraversedEdges,
    long TraversalWorkspaceReservedBytes);
