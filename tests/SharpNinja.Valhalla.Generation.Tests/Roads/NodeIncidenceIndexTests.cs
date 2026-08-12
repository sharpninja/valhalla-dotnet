using SharpNinja.Valhalla.Generation.Roads.Frontier;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class NodeIncidenceIndexTests
{
    [Fact]
    public void RelationsBeforeOrAfterWays_ProduceIdenticalSummaries()
    {
        NodeIncidenceRecord[] relationFirst =
        [
            new(7, 100, 0, 0, NodeIncidenceRole.RestrictionViaNode, 0),
            new(7, 20, 0, 3, NodeIncidenceRole.WayIntermediate, 1),
            new(7, 10, 0, 0, NodeIncidenceRole.WayStart, 2),
        ];
        NodeIncidenceRecord[] relationLast =
        [
            relationFirst[2] with { CanonicalOrdinal = 0 },
            relationFirst[1],
            relationFirst[0] with { CanonicalOrdinal = 2 },
        ];

        NodeIncidenceSummary first = Assert.Single(
            NodeIncidenceIndexBuilder.BuildSummaries(relationFirst));
        NodeIncidenceSummary second = Assert.Single(
            NodeIncidenceIndexBuilder.BuildSummaries(relationLast));

        Assert.Equal(first with { IncidenceOffset = 0 }, second with { IncidenceOffset = 0 });
        Assert.Equal(3, first.InitialPendingReferenceCount);
        Assert.Equal(2, first.DistinctWayCount);
        Assert.True(first.AnchorFlags.HasFlag(NodeAnchorFlags.RestrictionBoundary));
        Assert.True(first.AnchorFlags.HasFlag(NodeAnchorFlags.SharedWay));
    }

    [Fact]
    public void RepeatedNodeWithinOneWay_IsClassifiedAsSelfIntersection()
    {
        NodeIncidenceSummary summary = Assert.Single(
            NodeIncidenceIndexBuilder.BuildSummaries(
            [
                new(9, 80, 0, 0, NodeIncidenceRole.WayStart, 0),
                new(9, 80, 0, 7, NodeIncidenceRole.WayIntermediate, 1),
                new(9, 80, 0, 10, NodeIncidenceRole.WayEnd, 2),
            ]));

        Assert.Equal(1, summary.DistinctWayCount);
        Assert.True(summary.AnchorFlags.HasFlag(NodeAnchorFlags.SelfIntersection));
        Assert.True(summary.AnchorFlags.HasFlag(NodeAnchorFlags.WayEndpoint));
    }

    [Fact]
    public void AnchorRoles_AreReducedWithoutDependingOnInputOrder()
    {
        NodeIncidenceSummary summary = Assert.Single(
            NodeIncidenceIndexBuilder.BuildSummaries(
            [
                new(11, 7, 0, 0, NodeIncidenceRole.ActivePathEndpoint, 7),
                new(11, 6, 0, 0, NodeIncidenceRole.HierarchyTransition, 6),
                new(11, 5, 0, 0, NodeIncidenceRole.CrossTileCandidate, 5),
                new(11, 4, 0, 0, NodeIncidenceRole.AccessOrBarrierTransition, 4),
                new(11, 3, 0, 0, NodeIncidenceRole.RelationMember, 3),
                new(11, 2, 0, 0, NodeIncidenceRole.RestrictionViaWayBoundary, 2),
                new(11, 1, 0, 0, NodeIncidenceRole.WayEnd, 1),
            ]));

        NodeAnchorFlags expected =
            NodeAnchorFlags.WayEndpoint |
            NodeAnchorFlags.RestrictionBoundary |
            NodeAnchorFlags.RelationBoundary |
            NodeAnchorFlags.AccessTransition |
            NodeAnchorFlags.CrossTileEndpoint |
            NodeAnchorFlags.ActivePathEndpoint |
            NodeAnchorFlags.HierarchyTransition;
        Assert.Equal(expected, summary.AnchorFlags);
    }
}
