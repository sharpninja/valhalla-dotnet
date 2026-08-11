using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

[Flags]
internal enum NodeSemanticFlags : uint
{
    None = 0,
    TrafficSignal = 1 << 0,
    StopSign = 1 << 1,
    YieldSign = 1 << 2,
    Barrier = 1 << 3,
    Gate = 1 << 4,
    AccessTransition = 1 << 5,
}

[Flags]
internal enum NodeIncidenceRole : uint
{
    None = 0,
    WayStart = 1 << 0,
    WayEnd = 1 << 1,
    WayIntermediate = 1 << 2,
    SharedWayOccurrence = 1 << 3,
    RestrictionViaNode = 1 << 4,
    RestrictionViaWayBoundary = 1 << 5,
    RelationMember = 1 << 6,
    AccessOrBarrierTransition = 1 << 7,
    CrossTileCandidate = 1 << 8,
    ActivePathEndpoint = 1 << 9,
    HierarchyTransition = 1 << 10,
}

[Flags]
internal enum NodeAnchorFlags : uint
{
    None = 0,
    WayEndpoint = 1 << 0,
    SharedWay = 1 << 1,
    SelfIntersection = 1 << 2,
    RestrictionBoundary = 1 << 3,
    RelationBoundary = 1 << 4,
    AccessTransition = 1 << 5,
    CrossTileEndpoint = 1 << 6,
    ActivePathEndpoint = 1 << 7,
    HierarchyTransition = 1 << 8,
}

[Flags]
internal enum NodeLifecycleFlags : uint
{
    None = 0,
    DurableNodeRecordWritten = 1 << 0,
    DurableIncidentEdgeRangeWritten = 1 << 1,
    RequiredRestrictionMetadataWritten = 1 << 2,
    AllDurableStateWritten =
        DurableNodeRecordWritten |
        DurableIncidentEdgeRangeWritten |
        RequiredRestrictionMetadataWritten,
}

[Flags]
internal enum EdgeSemanticFlags : uint
{
    None = 0,
    Ferry = 1 << 0,
    Link = 1 << 1,
    HasTrafficControl = 1 << 2,
}

internal readonly record struct GenerationNodeRecord(
    long OsmNodeId,
    int LatitudeE7,
    int LongitudeE7,
    NodeSemanticFlags Flags,
    long TagReference);

internal readonly record struct GenerationGraphNodeCandidate(
    GenerationNodeRecord Node,
    GraphId TileBase,
    uint GridId,
    long CanonicalOrdinal)
{
    internal static GenerationGraphNodeCandidate Create(
        GenerationNodeRecord node,
        byte level,
        uint gridId,
        long canonicalOrdinal)
    {
        PointLL point = PointLL.Create(
            node.LongitudeE7 / 10_000_000d,
            node.LatitudeE7 / 10_000_000d);
        GraphId tileBase = TileHierarchy.GetGraphId(point, level);
        if (!tileBase.IsValid())
        {
            throw new InvalidDataException(
                $"OSM node {node.OsmNodeId} is outside the level {level} tiling.");
        }

        return new GenerationGraphNodeCandidate(
            node,
            tileBase.TileBase(),
            gridId,
            canonicalOrdinal);
    }
}

internal readonly record struct StableGraphNodeIdentity(
    long OsmNodeId,
    long CanonicalOrdinal,
    GraphId GraphId,
    uint GridId);

internal readonly record struct NodeIncidenceRecord(
    long OsmNodeId,
    long OwnerId,
    int OwnerOrdinal,
    int NodeOrdinal,
    NodeIncidenceRole Roles,
    long CanonicalOrdinal);

internal readonly record struct NodeIncidenceSummary(
    long OsmNodeId,
    long IncidenceOffset,
    int IncidenceCount,
    int DistinctWayCount,
    NodeAnchorFlags AnchorFlags,
    int InitialPendingReferenceCount);

internal readonly record struct EdgeShapeReference(
    long Offset,
    int PointCount,
    int ByteLength);

internal readonly record struct GenerationEdgeRecord(
    long EdgeRecordId,
    GraphId SourceNode,
    GraphId TargetNode,
    long WayId,
    EdgeShapeReference Shape,
    EdgeSemanticFlags Flags,
    uint ForwardAccess,
    uint ReverseAccess,
    long AttributeReference,
    byte Importance,
    bool HasNames,
    long CanonicalOrdinal);

internal enum EdgeEndpointRole : byte
{
    Source = 0,
    Target = 1,
}

internal readonly record struct NodeEdgeIncidenceRecord(
    GraphId NodeId,
    long EdgeRecordId,
    EdgeEndpointRole Role,
    bool DriveForward,
    byte Importance,
    bool HasNames,
    long ShapeOffset,
    GraphId SourceNode,
    GraphId TargetNode,
    long CanonicalOrdinal);

internal readonly record struct GenerationGraphNodeRecord(
    GraphId NodeId,
    long IncidentEdgeOffset,
    int IncidentEdgeCount);

internal readonly record struct PooledPathNode(
    GenerationNodeRecord Node,
    bool IsGraphAnchor,
    GraphId StableGraphId);

internal interface IFrontierShapeWriter : IDisposable
{
    void Append(in GenerationNodeRecord node);

    EdgeShapeReference Complete();
}

internal interface IFrontierEdgeSource
{
    long EdgeCount { get; }

    GenerationEdgeRecord ReadEdge(long ordinal);
}

internal interface IFrontierEdgeSink
{
    IFrontierShapeWriter BeginShape(long wayId);

    void PersistEdge(GenerationEdgeRecord edge);
}

internal static class PooledNodeFrontierScenarioMatrix
{
    internal static HashSet<string> RequiredScenarios { get; } = new(
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
        ],
        StringComparer.Ordinal);
}

internal static class NodeIncidenceIndexBuilder
{
    internal static IReadOnlyList<NodeIncidenceSummary> BuildSummaries(
        IEnumerable<NodeIncidenceRecord> incidences)
    {
        ArgumentNullException.ThrowIfNull(incidences);
        NodeIncidenceRecord[] ordered = incidences.ToArray();
        Array.Sort(ordered, Compare);

        var summaries = new List<NodeIncidenceSummary>();
        int offset = 0;
        while (offset < ordered.Length)
        {
            long osmNodeId = ordered[offset].OsmNodeId;
            int end = offset + 1;
            while (end < ordered.Length && ordered[end].OsmNodeId == osmNodeId)
            {
                end++;
            }

            summaries.Add(SummarizeNode(ordered, offset, end));
            offset = end;
        }

        return summaries;
    }

    private static NodeIncidenceSummary SummarizeNode(
        NodeIncidenceRecord[] ordered,
        int start,
        int end)
    {
        NodeAnchorFlags flags = NodeAnchorFlags.None;
        int distinctWayCount = 0;
        long lastWayId = long.MinValue;
        int occurrencesForWay = 0;

        for (int index = start; index < end; index++)
        {
            NodeIncidenceRecord incidence = ordered[index];
            flags |= ToAnchorFlags(incidence.Roles);
            if (!IsWayRole(incidence.Roles))
            {
                continue;
            }

            if (incidence.OwnerId != lastWayId)
            {
                if (occurrencesForWay > 1)
                {
                    flags |= NodeAnchorFlags.SelfIntersection;
                }

                distinctWayCount++;
                lastWayId = incidence.OwnerId;
                occurrencesForWay = 1;
            }
            else
            {
                occurrencesForWay++;
            }
        }

        if (occurrencesForWay > 1)
        {
            flags |= NodeAnchorFlags.SelfIntersection;
        }

        if (distinctWayCount > 1)
        {
            flags |= NodeAnchorFlags.SharedWay;
        }

        int count = end - start;
        return new NodeIncidenceSummary(
            ordered[start].OsmNodeId,
            start,
            count,
            distinctWayCount,
            flags,
            count);
    }

    internal static int Compare(NodeIncidenceRecord x, NodeIncidenceRecord y) =>
        NodeIncidenceComparer.Instance.Compare(x, y);

    internal static bool IsWayRole(NodeIncidenceRole roles) =>
        (roles & (
            NodeIncidenceRole.WayStart |
            NodeIncidenceRole.WayEnd |
            NodeIncidenceRole.WayIntermediate |
            NodeIncidenceRole.SharedWayOccurrence)) != 0;

    internal static NodeAnchorFlags ToAnchorFlags(NodeIncidenceRole roles)
    {
        NodeAnchorFlags flags = NodeAnchorFlags.None;
        if ((roles & (NodeIncidenceRole.WayStart | NodeIncidenceRole.WayEnd)) != 0)
        {
            flags |= NodeAnchorFlags.WayEndpoint;
        }

        if ((roles & NodeIncidenceRole.SharedWayOccurrence) != 0)
        {
            flags |= NodeAnchorFlags.SharedWay;
        }

        if ((roles & (
            NodeIncidenceRole.RestrictionViaNode |
            NodeIncidenceRole.RestrictionViaWayBoundary)) != 0)
        {
            flags |= NodeAnchorFlags.RestrictionBoundary;
        }

        if ((roles & NodeIncidenceRole.RelationMember) != 0)
        {
            flags |= NodeAnchorFlags.RelationBoundary;
        }

        if ((roles & NodeIncidenceRole.AccessOrBarrierTransition) != 0)
        {
            flags |= NodeAnchorFlags.AccessTransition;
        }

        if ((roles & NodeIncidenceRole.CrossTileCandidate) != 0)
        {
            flags |= NodeAnchorFlags.CrossTileEndpoint;
        }

        if ((roles & NodeIncidenceRole.ActivePathEndpoint) != 0)
        {
            flags |= NodeAnchorFlags.ActivePathEndpoint;
        }

        if ((roles & NodeIncidenceRole.HierarchyTransition) != 0)
        {
            flags |= NodeAnchorFlags.HierarchyTransition;
        }

        return flags;
    }

    private sealed class NodeIncidenceComparer : IComparer<NodeIncidenceRecord>
    {
        internal static NodeIncidenceComparer Instance { get; } = new();

        public int Compare(NodeIncidenceRecord x, NodeIncidenceRecord y)
        {
            int comparison = x.OsmNodeId.CompareTo(y.OsmNodeId);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = OwnerType(x.Roles).CompareTo(OwnerType(y.Roles));
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = x.OwnerId.CompareTo(y.OwnerId);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = x.OwnerOrdinal.CompareTo(y.OwnerOrdinal);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = x.NodeOrdinal.CompareTo(y.NodeOrdinal);
            if (comparison != 0)
            {
                return comparison;
            }

            return x.CanonicalOrdinal.CompareTo(y.CanonicalOrdinal);
        }

        private static int OwnerType(NodeIncidenceRole roles)
        {
            if (IsWayRole(roles))
            {
                return 0;
            }

            if ((roles & (
                NodeIncidenceRole.RestrictionViaNode |
                NodeIncidenceRole.RestrictionViaWayBoundary)) != 0)
            {
                return 1;
            }

            return 2;
        }
    }
}
