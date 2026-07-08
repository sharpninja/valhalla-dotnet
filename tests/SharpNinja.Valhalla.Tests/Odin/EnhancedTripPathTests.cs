// Faithful C# port of Valhalla's gtest suite test/enhancedtrippath.cc.
// Each [Fact] mirrors a TEST(...) case with the same inputs and expected values.
//
// PORT-NOTE: The C++ builds proto TripLeg_Node / TripLeg_Edge and adds proto TurnLanes via
// add_turn_lanes()->set_directions_mask(). Here we build the ported Thor TripNode / TripEdge /
// TripIntersectingEdge and wrap them. The C++ pattern of creating a fresh EnhancedTripLeg_Edge for
// each activation call (and calling ClearActiveTurnLanes between calls) maps onto creating a fresh
// EnhancedTripLeg_Edge per call: each EnhancedTripLeg_Edge instance builds its own TurnLane state
// list from the edge masks, so per-call instances start with cleared (Invalid) state.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

using TM = SharpNinja.Valhalla.Baldr.TurnLaneConstants;

namespace SharpNinja.Valhalla.Tests.Odin;

public class EnhancedTripPathTests
{
    private static TripNode MakePathNode(uint pathBeginHeading, params (uint BeginHeading, TripTraversability? Driveability)[] xedges)
    {
        var node = new TripNode { Edge = new TripEdge { BeginHeading = pathBeginHeading } };
        foreach ((uint beginHeading, TripTraversability? driveability) in xedges)
        {
            var xe = new TripIntersectingEdge { BeginHeading = beginHeading };
            if (driveability.HasValue)
            {
                xe.Driveability = driveability.Value;
            }

            node.IntersectingEdges.Add(xe);
        }

        return node;
    }

    private static void TryCalculateRightLeftIntersectingEdgeCounts(
        uint fromHeading,
        TripNode node,
        IntersectingEdgeCounts expected,
        TravelMode travelMode = TravelMode.Drive)
    {
        var enhanced = new EnhancedTripLeg_Node(node);
        var xedgeCounts = new IntersectingEdgeCounts();
        xedgeCounts.Clear();

        enhanced.CalculateRightLeftIntersectingEdgeCounts(fromHeading, travelMode, ref xedgeCounts);
        Assert.Equal(expected.Right, xedgeCounts.Right);
        Assert.Equal(expected.RightSimilar, xedgeCounts.RightSimilar);
        Assert.Equal(expected.RightTraversableOutbound, xedgeCounts.RightTraversableOutbound);
        Assert.Equal(expected.RightSimilarTraversableOutbound, xedgeCounts.RightSimilarTraversableOutbound);
        Assert.Equal(expected.Left, xedgeCounts.Left);
        Assert.Equal(expected.LeftSimilar, xedgeCounts.LeftSimilar);
        Assert.Equal(expected.LeftTraversableOutbound, xedgeCounts.LeftTraversableOutbound);
        Assert.Equal(expected.LeftSimilarTraversableOutbound, xedgeCounts.LeftSimilarTraversableOutbound);
    }

    [Fact]
    public void CalculateRightLeftIntersectingEdgeCounts_StraightStraight()
    {
        // Path straight, intersecting straight
        TripNode node1 = MakePathNode(5, (355, TripTraversability.Both));
        TryCalculateRightLeftIntersectingEdgeCounts(0, node1, new IntersectingEdgeCounts(0, 0, 0, 0, 1, 1, 1, 1));

        // Path straight, intersecting straight
        TripNode node2 = MakePathNode(355, (5, TripTraversability.Forward));
        TryCalculateRightLeftIntersectingEdgeCounts(0, node2, new IntersectingEdgeCounts(1, 1, 1, 1, 0, 0, 0, 0));
    }

    [Fact]
    public void CalculateRightLeftIntersectingEdgeCounts_SlightRightStraight()
    {
        // Path slight right, intersecting straight
        TripNode node1 = MakePathNode(11, (0, TripTraversability.Backward));
        TryCalculateRightLeftIntersectingEdgeCounts(0, node1, new IntersectingEdgeCounts(0, 0, 0, 0, 1, 1, 0, 0));

        // Path slight right, intersecting straight
        TripNode node2 = MakePathNode(105, (85, TripTraversability.None));
        TryCalculateRightLeftIntersectingEdgeCounts(90, node2, new IntersectingEdgeCounts(0, 0, 0, 0, 1, 1, 0, 0));
    }

    [Fact]
    public void CalculateRightLeftIntersectingEdgeCounts_SlightLeftStraight()
    {
        // Path slight left, intersecting straight (no driveability -> defaults to None)
        TripNode node1 = MakePathNode(345, (355, null));
        TryCalculateRightLeftIntersectingEdgeCounts(0, node1, new IntersectingEdgeCounts(1, 1, 0, 0, 0, 0, 0, 0));

        TripNode node2 = MakePathNode(255, (275, null));
        TryCalculateRightLeftIntersectingEdgeCounts(270, node2, new IntersectingEdgeCounts(1, 1, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void CalculateRightLeftIntersectingEdgeCounts_SlightLeftRightLeft()
    {
        // Path slight left, intersecting right and left (no driveability)
        TripNode node1 = MakePathNode(340, (45, null), (90, null), (135, null), (315, null), (270, null), (225, null));
        TryCalculateRightLeftIntersectingEdgeCounts(0, node1, new IntersectingEdgeCounts(3, 0, 0, 0, 3, 1, 0, 0));

        // Path slight left, intersecting right and left
        TripNode node2 = MakePathNode(60, (157, TripTraversability.Both), (337, TripTraversability.Forward));
        TryCalculateRightLeftIntersectingEdgeCounts(80, node2, new IntersectingEdgeCounts(1, 0, 1, 0, 1, 0, 1, 0));
    }

    [Fact]
    public void CalculateRightLeftIntersectingEdgeCounts_SharpRightRightLeft()
    {
        // Path sharp right, intersecting right and left
        TripNode node1 = MakePathNode(352, (355, null), (270, null), (180, null), (90, null), (10, null));
        TryCalculateRightLeftIntersectingEdgeCounts(180, node1, new IntersectingEdgeCounts(1, 1, 0, 0, 4, 0, 0, 0));
    }

    [Fact]
    public void CalculateRightLeftIntersectingEdgeCounts_SharpLeftRightLeft()
    {
        // Path sharp left, intersecting right and left
        TripNode node1 = MakePathNode(10, (90, null), (180, null), (270, null), (352, null), (355, null), (5, null));
        TryCalculateRightLeftIntersectingEdgeCounts(180, node1, new IntersectingEdgeCounts(5, 0, 0, 0, 1, 1, 0, 0));
    }

    private static TripEdge MakeTurnLaneEdge(params ushort[] masks)
    {
        var edge = new TripEdge();
        foreach (ushort mask in masks)
        {
            edge.TurnLanes.Add(mask);
        }

        return edge;
    }

    [Fact]
    public void DefaultTurnLaneState_True()
    {
        TripEdge edge = MakeTurnLaneEdge(TM.TurnLaneLeft);
        var enhanced = new EnhancedTripLeg_Edge(edge);
        Assert.Equal(TurnLaneState.Invalid, enhanced.TurnLanes()[0].State);
    }

    [Fact]
    public void HasActiveTurnLane_False()
    {
        TripEdge edge = MakeTurnLaneEdge(TM.TurnLaneLeft, TM.TurnLaneThrough, TM.TurnLaneRight);
        Assert.False(new EnhancedTripLeg_Edge(edge).HasActiveTurnLane());
    }

    [Fact]
    public void HasActiveTurnLane_True()
    {
        TripEdge edge = MakeTurnLaneEdge(TM.TurnLaneLeft, TM.TurnLaneThrough, TM.TurnLaneRight);

        // Left active
        var e1 = new EnhancedTripLeg_Edge(edge);
        e1.TurnLanes()[0].State = TurnLaneState.Active;
        Assert.True(e1.HasActiveTurnLane());

        // Straight active
        var e2 = new EnhancedTripLeg_Edge(edge);
        e2.TurnLanes()[1].State = TurnLaneState.Active;
        Assert.True(e2.HasActiveTurnLane());

        // Right active
        var e3 = new EnhancedTripLeg_Edge(edge);
        e3.TurnLanes()[2].State = TurnLaneState.Active;
        Assert.True(e3.HasActiveTurnLane());
    }

    [Fact]
    public void HasNonDirectionalTurnLane_False()
    {
        TripEdge edge = MakeTurnLaneEdge(TM.TurnLaneLeft, TM.TurnLaneThrough, TM.TurnLaneRight);
        Assert.False(new EnhancedTripLeg_Edge(edge).HasNonDirectionalTurnLane());
    }

    [Fact]
    public void HasNonDirectionalTurnLane_True()
    {
        TripEdge edge1 = MakeTurnLaneEdge(TM.TurnLaneLeft, TM.TurnLaneNone);
        Assert.True(new EnhancedTripLeg_Edge(edge1).HasNonDirectionalTurnLane());

        TripEdge edge2 = MakeTurnLaneEdge(TM.TurnLaneEmpty, TM.TurnLaneRight);
        Assert.True(new EnhancedTripLeg_Edge(edge2).HasNonDirectionalTurnLane());
    }

    private static void TryActivateTurnLanes(
        TripEdge edge,
        ushort turnLaneDirection,
        float remainingStepDistance,
        DirectionsLegManeuverType currManeuverType,
        DirectionsLegManeuverType nextManeuverType,
        ushort expectedActivatedCount)
    {
        // A fresh EnhancedTripLeg_Edge mirrors ClearActiveTurnLanes between C++ calls.
        var enhanced = new EnhancedTripLeg_Edge(edge);
        ushort activatedCount = enhanced.ActivateTurnLanes(
            turnLaneDirection, remainingStepDistance, currManeuverType, nextManeuverType);
        Assert.Equal(expectedActivatedCount, activatedCount);
    }

    [Fact]
    public void TestActivateTurnLanes()
    {
        TripEdge edge1 = MakeTurnLaneEdge(
            TM.TurnLaneReverse, TM.TurnLaneSharpLeft, TM.TurnLaneLeft, TM.TurnLaneLeft,
            (ushort)(TM.TurnLaneLeft | TM.TurnLaneThrough), TM.TurnLaneThrough, TM.TurnLaneThrough,
            (ushort)(TM.TurnLaneThrough | TM.TurnLaneRight), TM.TurnLaneRight, TM.TurnLaneSharpRight);

        const float remainingStepDistance = 2.0f; // kilometers
        const DirectionsLegManeuverType next = DirectionsLegManeuverType.Right;

        TryActivateTurnLanes(edge1, TM.TurnLaneReverse, remainingStepDistance, DirectionsLegManeuverType.UturnLeft, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSharpLeft, remainingStepDistance, DirectionsLegManeuverType.SharpLeft, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneLeft, remainingStepDistance, DirectionsLegManeuverType.Left, next, 3);
        TryActivateTurnLanes(edge1, TM.TurnLaneSlightLeft, remainingStepDistance, DirectionsLegManeuverType.SlightLeft, next, 0);
        TryActivateTurnLanes(edge1, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 4);
        TryActivateTurnLanes(edge1, TM.TurnLaneSlightRight, remainingStepDistance, DirectionsLegManeuverType.SlightRight, next, 0);
        TryActivateTurnLanes(edge1, TM.TurnLaneRight, remainingStepDistance, DirectionsLegManeuverType.Right, next, 2);
        TryActivateTurnLanes(edge1, TM.TurnLaneSharpRight, remainingStepDistance, DirectionsLegManeuverType.SharpRight, next, 1);

        TripEdge edge2 = MakeTurnLaneEdge(
            TM.TurnLaneSlightLeft, TM.TurnLaneSlightLeft, TM.TurnLaneThrough, TM.TurnLaneThrough,
            TM.TurnLaneThrough, TM.TurnLaneMergeToRight);

        TryActivateTurnLanes(edge2, TM.TurnLaneSlightLeft, remainingStepDistance, DirectionsLegManeuverType.SlightLeft, next, 2);
        TryActivateTurnLanes(edge2, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 3);
        TryActivateTurnLanes(edge2, TM.TurnLaneMergeToRight, remainingStepDistance, DirectionsLegManeuverType.MergeRight, next, 1);

        TripEdge edge3 = MakeTurnLaneEdge(
            TM.TurnLaneMergeToLeft, TM.TurnLaneThrough, TM.TurnLaneThrough, TM.TurnLaneThrough,
            TM.TurnLaneSlightRight, TM.TurnLaneSlightRight);

        TryActivateTurnLanes(edge3, TM.TurnLaneMergeToLeft, remainingStepDistance, DirectionsLegManeuverType.MergeLeft, next, 1);
        TryActivateTurnLanes(edge3, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 3);
        TryActivateTurnLanes(edge3, TM.TurnLaneSlightRight, remainingStepDistance, DirectionsLegManeuverType.SlightRight, next, 2);

        TripEdge edge4 = MakeTurnLaneEdge(
            TM.TurnLaneLeft, TM.TurnLaneLeft, TM.TurnLaneThrough, TM.TurnLaneThrough, TM.TurnLaneThrough,
            TM.TurnLaneRight, TM.TurnLaneRight);

        // Both left turns active
        TryActivateTurnLanes(edge4, TM.TurnLaneLeft, remainingStepDistance, DirectionsLegManeuverType.Left, next, 2);
        // Left most turn active
        TryActivateTurnLanes(edge4, TM.TurnLaneLeft, remainingStepDistance, DirectionsLegManeuverType.UturnLeft, next, 1);
        // Both right turns active
        TryActivateTurnLanes(edge4, TM.TurnLaneRight, remainingStepDistance, DirectionsLegManeuverType.Right, next, 2);
        // Right most turn active
        TryActivateTurnLanes(edge4, TM.TurnLaneRight, remainingStepDistance, DirectionsLegManeuverType.UturnRight, next, 1);
    }

    [Fact]
    public void TestActivateTurnLanesShortNextRight()
    {
        TripEdge edge1 = MakeTurnLaneEdge(
            TM.TurnLaneReverse, TM.TurnLaneSharpLeft, TM.TurnLaneLeft, TM.TurnLaneLeft,
            (ushort)(TM.TurnLaneLeft | TM.TurnLaneThrough), TM.TurnLaneThrough, TM.TurnLaneThrough,
            (ushort)(TM.TurnLaneThrough | TM.TurnLaneRight), TM.TurnLaneRight, TM.TurnLaneSharpRight);

        const float remainingStepDistance = 0.1f; // kilometers
        const DirectionsLegManeuverType next = DirectionsLegManeuverType.Right;

        TryActivateTurnLanes(edge1, TM.TurnLaneReverse, remainingStepDistance, DirectionsLegManeuverType.UturnLeft, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSharpLeft, remainingStepDistance, DirectionsLegManeuverType.SharpLeft, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneLeft, remainingStepDistance, DirectionsLegManeuverType.Left, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSlightLeft, remainingStepDistance, DirectionsLegManeuverType.SlightLeft, next, 0);
        TryActivateTurnLanes(edge1, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSlightRight, remainingStepDistance, DirectionsLegManeuverType.SlightRight, next, 0);
        TryActivateTurnLanes(edge1, TM.TurnLaneRight, remainingStepDistance, DirectionsLegManeuverType.Right, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSharpRight, remainingStepDistance, DirectionsLegManeuverType.SharpRight, next, 1);

        TripEdge edge2 = MakeTurnLaneEdge(
            TM.TurnLaneSlightLeft, TM.TurnLaneSlightLeft, TM.TurnLaneThrough, TM.TurnLaneThrough,
            TM.TurnLaneThrough, TM.TurnLaneMergeToRight);

        TryActivateTurnLanes(edge2, TM.TurnLaneSlightLeft, remainingStepDistance, DirectionsLegManeuverType.SlightLeft, next, 1);
        TryActivateTurnLanes(edge2, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 1);
        TryActivateTurnLanes(edge2, TM.TurnLaneMergeToRight, remainingStepDistance, DirectionsLegManeuverType.MergeRight, next, 1);

        TripEdge edge3 = MakeTurnLaneEdge(
            TM.TurnLaneMergeToLeft, TM.TurnLaneThrough, TM.TurnLaneThrough, TM.TurnLaneThrough,
            TM.TurnLaneSlightRight, TM.TurnLaneSlightRight);

        TryActivateTurnLanes(edge3, TM.TurnLaneMergeToLeft, remainingStepDistance, DirectionsLegManeuverType.MergeLeft, next, 1);
        TryActivateTurnLanes(edge3, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 1);
        TryActivateTurnLanes(edge3, TM.TurnLaneSlightRight, remainingStepDistance, DirectionsLegManeuverType.SlightRight, next, 1);
    }

    [Fact]
    public void TestActivateTurnLanesShortNextLeft()
    {
        TripEdge edge1 = MakeTurnLaneEdge(
            TM.TurnLaneReverse, TM.TurnLaneSharpLeft, TM.TurnLaneLeft, TM.TurnLaneLeft,
            (ushort)(TM.TurnLaneLeft | TM.TurnLaneThrough), TM.TurnLaneThrough, TM.TurnLaneThrough,
            (ushort)(TM.TurnLaneThrough | TM.TurnLaneRight), TM.TurnLaneRight, TM.TurnLaneSharpRight);

        const float remainingStepDistance = 0.1f; // kilometers
        const DirectionsLegManeuverType next = DirectionsLegManeuverType.Left;

        TryActivateTurnLanes(edge1, TM.TurnLaneReverse, remainingStepDistance, DirectionsLegManeuverType.UturnLeft, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSharpLeft, remainingStepDistance, DirectionsLegManeuverType.SharpLeft, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneLeft, remainingStepDistance, DirectionsLegManeuverType.Left, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSlightLeft, remainingStepDistance, DirectionsLegManeuverType.SlightLeft, next, 0);
        TryActivateTurnLanes(edge1, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSlightRight, remainingStepDistance, DirectionsLegManeuverType.SlightRight, next, 0);
        TryActivateTurnLanes(edge1, TM.TurnLaneRight, remainingStepDistance, DirectionsLegManeuverType.Right, next, 1);
        TryActivateTurnLanes(edge1, TM.TurnLaneSharpRight, remainingStepDistance, DirectionsLegManeuverType.SharpRight, next, 1);

        TripEdge edge2 = MakeTurnLaneEdge(
            TM.TurnLaneSlightLeft, TM.TurnLaneSlightLeft, TM.TurnLaneThrough, TM.TurnLaneThrough,
            TM.TurnLaneThrough, TM.TurnLaneMergeToRight);

        TryActivateTurnLanes(edge2, TM.TurnLaneSlightLeft, remainingStepDistance, DirectionsLegManeuverType.SlightLeft, next, 1);
        TryActivateTurnLanes(edge2, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 1);
        TryActivateTurnLanes(edge2, TM.TurnLaneMergeToRight, remainingStepDistance, DirectionsLegManeuverType.MergeRight, next, 1);

        TripEdge edge3 = MakeTurnLaneEdge(
            TM.TurnLaneMergeToLeft, TM.TurnLaneThrough, TM.TurnLaneThrough, TM.TurnLaneThrough,
            TM.TurnLaneSlightRight, TM.TurnLaneSlightRight);

        TryActivateTurnLanes(edge3, TM.TurnLaneMergeToLeft, remainingStepDistance, DirectionsLegManeuverType.MergeLeft, next, 1);
        TryActivateTurnLanes(edge3, TM.TurnLaneThrough, remainingStepDistance, DirectionsLegManeuverType.Continue, next, 1);
        TryActivateTurnLanes(edge3, TM.TurnLaneSlightRight, remainingStepDistance, DirectionsLegManeuverType.SlightRight, next, 1);
    }
}
