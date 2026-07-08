// Faithful C# port of Valhalla's gtest suite test/maneuversbuilder.cc.
// Each [Fact] mirrors a TEST(Maneuversbuilder, ...) case with the same inputs and expected values.
//
// PORT-NOTE: The C++ builds proto TripLeg_Node / TripLeg_Edge / TripLeg_IntersectingEdge and proto
// Maneuvers. Here we build the ported Thor TripNode / TripEdge / TripIntersectingEdge and the odin
// Maneuver working type. The protected ManeuversBuilder methods are exposed through a test subclass,
// matching the C++ ManeuversBuilderTest sub-class pattern.
//
// PORT-NOTE: PopulateEdge's `speed` param maps to TripEdge.SpeedKph and DefaultSpeed (the C++
// set_speed); the Combine / CountAndSort / ProcessRoundabouts tests assert only on
// type / length / time / signs / roundabout fields, none of which depend on speed. The maneuvers are
// pre-populated with explicit time values, so Combine sums them rather than recomputing from node
// elapsed cost (which the standalone gtests also leave at 0).

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Odin;

public class ManeuversBuilderTests
{
    // Sub class to test protected methods (mirrors the C++ ManeuversBuilderTest).
    private sealed class ManeuversBuilderTest : ManeuversBuilder
    {
        public ManeuversBuilderTest(Options? options = null)
            : base(options ?? new Options(), null)
        {
        }

        public ManeuversBuilderTest(Options options, EnhancedTripLeg etp)
            : base(options, etp)
        {
        }

        public void CombinePublic(LinkedList<Maneuver> maneuvers) => Combine(maneuvers);

        public void ProcessRoundaboutsPublic(LinkedList<Maneuver> maneuvers) => ProcessRoundabouts(maneuvers);

        public void CountAndSortSignsPublic(LinkedList<Maneuver> maneuvers) => CountAndSortSigns(maneuvers);

        public void SetSimpleDirectionalManeuverTypePublic(Maneuver maneuver)
            => SetSimpleDirectionalManeuverType(maneuver, null, null);

        public DirectionsLegManeuverCardinalDirection DetermineCardinalDirectionPublic(uint heading)
            => DetermineCardinalDirection(heading);

        public void DetermineRelativeDirectionPublic(Maneuver maneuver) => DetermineRelativeDirection(maneuver);

        public bool IsIntersectingForwardEdgePublic(int nodeIndex, EnhancedTripLeg_Edge prevEdge, EnhancedTripLeg_Edge currEdge)
            => IsIntersectingForwardEdge(nodeIndex, prevEdge, currEdge);
    }

    // ---- Builders mirroring PopulateEdge / PopulateIntersectingEdge / PopulateManeuver ----------

    private static void PopulateEdge(
        TripEdge edge,
        IEnumerable<(string Name, bool IsRouteNumber)> names,
        float length,
        double speed,
        RoadClass roadClass,
        uint beginHeading,
        uint endHeading,
        uint beginShapeIndex,
        uint endShapeIndex,
        TripTraversability traversability,
        bool ramp,
        bool turnChannel,
        bool ferry,
        bool railFerry,
        bool toll,
        bool unpaved,
        bool tunnel,
        bool bridge,
        bool roundabout,
        bool internalIntersection,
        IEnumerable<(string, bool)> exitNumbers,
        IEnumerable<(string, bool)> exitOntoStreets,
        IEnumerable<(string, bool)> exitTowardLocations,
        IEnumerable<(string, bool)> exitNames,
        TravelMode travelMode = TravelMode.Drive)
    {
        foreach ((string name, _) in names)
        {
            // PORT-NOTE: TripEdge.Names is a string list with no is-route-number flag; route-number
            // distinction is recovered by StreetNamesUs base-name parsing, matching the foundation.
            edge.Names.Add(name);
        }

        edge.LengthKm = length;
        edge.SpeedKph = speed;
        edge.DefaultSpeed = (uint)speed;
        edge.RoadClass = roadClass;
        edge.BeginHeading = beginHeading;
        edge.EndHeading = endHeading;
        edge.BeginShapeIndex = beginShapeIndex;
        edge.EndShapeIndex = endShapeIndex;
        edge.Traversability = traversability;
        if (ramp)
        {
            edge.Use = Use.Ramp;
        }
        else if (turnChannel)
        {
            edge.Use = Use.TurnChannel;
        }
        else if (ferry)
        {
            edge.Use = Use.Ferry;
        }
        else if (railFerry)
        {
            edge.Use = Use.RailFerry;
        }

        edge.Toll = toll;
        edge.Unpaved = unpaved;
        edge.Tunnel = tunnel;
        edge.Bridge = bridge;
        edge.Roundabout = roundabout;
        edge.InternalIntersection = internalIntersection;

        var sign = new TripSign();
        foreach ((string text, bool isRouteNumber) in exitNumbers)
        {
            sign.ExitNumbers.Add(new TripSignElement(text, isRouteNumber));
        }

        foreach ((string text, bool isRouteNumber) in exitOntoStreets)
        {
            sign.ExitOntoStreets.Add(new TripSignElement(text, isRouteNumber));
        }

        foreach ((string text, bool isRouteNumber) in exitTowardLocations)
        {
            sign.ExitTowardLocations.Add(new TripSignElement(text, isRouteNumber));
        }

        foreach ((string text, bool isRouteNumber) in exitNames)
        {
            sign.ExitNames.Add(new TripSignElement(text, isRouteNumber));
        }

        if (!sign.IsEmpty)
        {
            edge.Sign = sign;
        }

        edge.Mode = travelMode;
    }

    private static void PopulateIntersectingEdge(
        TripIntersectingEdge xedge,
        uint beginHeading,
        bool prevNameConsistency = false,
        bool currNameConsistency = false,
        TripTraversability driveability = TripTraversability.Both)
    {
        xedge.BeginHeading = beginHeading;
        xedge.Driveability = driveability;
        xedge.PrevNameConsistency = prevNameConsistency;
        xedge.CurrNameConsistency = currNameConsistency;
    }

    private static void PopulateManeuver(
        Maneuver maneuver,
        DirectionsLegManeuverType type,
        IEnumerable<(string, bool)> streetNames,
        IEnumerable<(string, bool)> beginStreetNames,
        IEnumerable<(string, bool)> crossStreetNames,
        string instruction,
        float distance,
        double time,
        uint turnDegree,
        Maneuver.RelativeDirection beginRelativeDirection,
        DirectionsLegManeuverCardinalDirection beginCardinalDirection,
        uint beginHeading,
        uint endHeading,
        uint beginNodeIndex,
        uint endNodeIndex,
        uint beginShapeIndex,
        uint endShapeIndex,
        bool ramp,
        bool turnChannel,
        bool ferry,
        bool railFerry,
        bool roundabout,
        bool portionsToll,
        bool portionsUnpaved,
        bool portionsHighway,
        bool internalIntersection,
        IEnumerable<(string, bool, uint)> exitNumbers,
        IEnumerable<(string, bool, uint)> exitBranches,
        IEnumerable<(string, bool, uint)> exitTowards,
        IEnumerable<(string, bool, uint)> exitNames,
        uint internalRightTurnCount = 0,
        uint internalLeftTurnCount = 0,
        uint roundaboutExitCount = 0)
    {
        maneuver.SetType(type);
        maneuver.SetStreetNames(streetNames);
        maneuver.SetBeginStreetNames(beginStreetNames);
        maneuver.SetCrossStreetNames(crossStreetNames);
        maneuver.SetInstruction(instruction);
        maneuver.SetLength(distance);
        maneuver.SetTime(time);
        maneuver.SetTurnDegree(turnDegree);
        maneuver.SetBeginRelativeDirection(beginRelativeDirection);
        maneuver.SetBeginCardinalDirection(beginCardinalDirection);
        maneuver.SetBeginHeading(beginHeading);
        maneuver.SetEndHeading(endHeading);
        maneuver.SetBeginNodeIndex(beginNodeIndex);
        maneuver.SetEndNodeIndex(endNodeIndex);
        maneuver.SetBeginShapeIndex(beginShapeIndex);
        maneuver.SetEndShapeIndex(endShapeIndex);
        maneuver.SetRamp(ramp);
        maneuver.SetTurnChannel(turnChannel);
        maneuver.SetFerry(ferry);
        maneuver.SetRailFerry(railFerry);
        maneuver.SetRoundabout(roundabout);
        maneuver.SetPortionsToll(portionsToll);
        maneuver.SetPortionsUnpaved(portionsUnpaved);
        maneuver.SetPortionsHighway(portionsHighway);
        maneuver.SetInternalIntersection(internalIntersection);

        foreach ((string text, bool isRouteNumber, uint consecutiveCount) in exitNumbers)
        {
            var sign = new OdinSign(text, isRouteNumber);
            sign.SetConsecutiveCount(consecutiveCount);
            maneuver.MutableSigns().MutableExitNumberList().Add(sign);
        }

        foreach ((string text, bool isRouteNumber, uint consecutiveCount) in exitBranches)
        {
            var sign = new OdinSign(text, isRouteNumber);
            sign.SetConsecutiveCount(consecutiveCount);
            maneuver.MutableSigns().MutableExitBranchList().Add(sign);
        }

        foreach ((string text, bool isRouteNumber, uint consecutiveCount) in exitTowards)
        {
            var sign = new OdinSign(text, isRouteNumber);
            sign.SetConsecutiveCount(consecutiveCount);
            maneuver.MutableSigns().MutableExitTowardList().Add(sign);
        }

        foreach ((string text, bool isRouteNumber, uint consecutiveCount) in exitNames)
        {
            var sign = new OdinSign(text, isRouteNumber);
            sign.SetConsecutiveCount(consecutiveCount);
            maneuver.MutableSigns().MutableExitNameList().Add(sign);
        }

        maneuver.SetInternalRightTurnCount(internalRightTurnCount);
        maneuver.SetInternalLeftTurnCount(internalLeftTurnCount);
        maneuver.SetRoundaboutExitCount(roundaboutExitCount);
    }

    private static TripNode AddNode(TripLeg path)
    {
        var node = new TripNode();
        path.Nodes.Add(node);
        return node;
    }

    private static TripEdge AddEdge(TripNode node)
    {
        node.Edge = new TripEdge();
        return node.Edge;
    }

    private static readonly (string, bool)[] NoNames = System.Array.Empty<(string, bool)>();
    private static readonly (string, bool, uint)[] NoSigns = System.Array.Empty<(string, bool, uint)>();

    private static void TryCombine(ManeuversBuilderTest mbTest, LinkedList<Maneuver> maneuvers, LinkedList<Maneuver> expectedManeuvers)
    {
        mbTest.CombinePublic(maneuvers);

        Assert.Equal(expectedManeuvers.Count, maneuvers.Count);

        LinkedListNode<Maneuver>? man = maneuvers.First;
        LinkedListNode<Maneuver>? expectedMan = expectedManeuvers.First;
        while (man != null && expectedMan != null)
        {
            Assert.Equal(expectedMan.Value.Type(), man.Value.Type());
            Assert.Equal(expectedMan.Value.Length(), man.Value.Length(), 5);
            Assert.Equal(expectedMan.Value.Time(), man.Value.Time());
            man = man.Next;
            expectedMan = expectedMan.Next;
        }
    }

    // ============================================================================================
    // TestSetSimpleDirectionalManeuverType
    // ============================================================================================
    private static void TrySetSimpleDirectionalManeuverType(uint turnDegree, DirectionsLegManeuverType expected)
    {
        var options = new Options();
        var path = new TripLeg();

        // node:0
        AddNode(path);

        // node:1
        TripNode node = AddNode(path);
        AddEdge(node).DriveOnLeft = false;

        // node:2 dummy last node
        AddNode(path);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);
        var maneuver = new Maneuver();
        maneuver.SetBeginNodeIndex(1);
        maneuver.SetTurnDegree(turnDegree);
        mbTest.SetSimpleDirectionalManeuverTypePublic(maneuver);
        Assert.Equal(expected, maneuver.Type());
    }

    [Fact]
    public void TestSetSimpleDirectionalManeuverType()
    {
        // Continue
        TrySetSimpleDirectionalManeuverType(350, DirectionsLegManeuverType.Continue);
        TrySetSimpleDirectionalManeuverType(0, DirectionsLegManeuverType.Continue);
        TrySetSimpleDirectionalManeuverType(10, DirectionsLegManeuverType.Continue);

        // Slight right
        TrySetSimpleDirectionalManeuverType(11, DirectionsLegManeuverType.SlightRight);
        TrySetSimpleDirectionalManeuverType(28, DirectionsLegManeuverType.SlightRight);
        TrySetSimpleDirectionalManeuverType(44, DirectionsLegManeuverType.SlightRight);

        // Right
        TrySetSimpleDirectionalManeuverType(45, DirectionsLegManeuverType.Right);
        TrySetSimpleDirectionalManeuverType(90, DirectionsLegManeuverType.Right);
        TrySetSimpleDirectionalManeuverType(135, DirectionsLegManeuverType.Right);

        // Sharp right
        TrySetSimpleDirectionalManeuverType(136, DirectionsLegManeuverType.SharpRight);
        TrySetSimpleDirectionalManeuverType(148, DirectionsLegManeuverType.SharpRight);
        TrySetSimpleDirectionalManeuverType(159, DirectionsLegManeuverType.SharpRight);

        // Reverse (right side of street driving)
        TrySetSimpleDirectionalManeuverType(160, DirectionsLegManeuverType.UturnRight);
        TrySetSimpleDirectionalManeuverType(179, DirectionsLegManeuverType.UturnRight);
        TrySetSimpleDirectionalManeuverType(180, DirectionsLegManeuverType.UturnLeft);
        TrySetSimpleDirectionalManeuverType(200, DirectionsLegManeuverType.UturnLeft);

        // Sharp left
        TrySetSimpleDirectionalManeuverType(201, DirectionsLegManeuverType.SharpLeft);
        TrySetSimpleDirectionalManeuverType(213, DirectionsLegManeuverType.SharpLeft);
        TrySetSimpleDirectionalManeuverType(224, DirectionsLegManeuverType.SharpLeft);

        // Left
        TrySetSimpleDirectionalManeuverType(225, DirectionsLegManeuverType.Left);
        TrySetSimpleDirectionalManeuverType(270, DirectionsLegManeuverType.Left);
        TrySetSimpleDirectionalManeuverType(315, DirectionsLegManeuverType.Left);

        // Slight left
        TrySetSimpleDirectionalManeuverType(316, DirectionsLegManeuverType.SlightLeft);
        TrySetSimpleDirectionalManeuverType(333, DirectionsLegManeuverType.SlightLeft);
        TrySetSimpleDirectionalManeuverType(349, DirectionsLegManeuverType.SlightLeft);
    }

    // ============================================================================================
    // TestDetermineCardinalDirection
    // ============================================================================================
    private static void TryDetermineCardinalDirection(uint heading, DirectionsLegManeuverCardinalDirection expected)
    {
        var mbTest = new ManeuversBuilderTest();
        Assert.Equal(expected, mbTest.DetermineCardinalDirectionPublic(heading));
    }

    [Fact]
    public void TestDetermineCardinalDirection()
    {
        TryDetermineCardinalDirection(337, DirectionsLegManeuverCardinalDirection.North);
        TryDetermineCardinalDirection(0, DirectionsLegManeuverCardinalDirection.North);
        TryDetermineCardinalDirection(23, DirectionsLegManeuverCardinalDirection.North);

        TryDetermineCardinalDirection(24, DirectionsLegManeuverCardinalDirection.NorthEast);
        TryDetermineCardinalDirection(45, DirectionsLegManeuverCardinalDirection.NorthEast);
        TryDetermineCardinalDirection(66, DirectionsLegManeuverCardinalDirection.NorthEast);

        TryDetermineCardinalDirection(67, DirectionsLegManeuverCardinalDirection.East);
        TryDetermineCardinalDirection(90, DirectionsLegManeuverCardinalDirection.East);
        TryDetermineCardinalDirection(113, DirectionsLegManeuverCardinalDirection.East);

        TryDetermineCardinalDirection(114, DirectionsLegManeuverCardinalDirection.SouthEast);
        TryDetermineCardinalDirection(135, DirectionsLegManeuverCardinalDirection.SouthEast);
        TryDetermineCardinalDirection(156, DirectionsLegManeuverCardinalDirection.SouthEast);

        TryDetermineCardinalDirection(157, DirectionsLegManeuverCardinalDirection.South);
        TryDetermineCardinalDirection(180, DirectionsLegManeuverCardinalDirection.South);
        TryDetermineCardinalDirection(203, DirectionsLegManeuverCardinalDirection.South);

        TryDetermineCardinalDirection(204, DirectionsLegManeuverCardinalDirection.SouthWest);
        TryDetermineCardinalDirection(225, DirectionsLegManeuverCardinalDirection.SouthWest);
        TryDetermineCardinalDirection(246, DirectionsLegManeuverCardinalDirection.SouthWest);

        TryDetermineCardinalDirection(247, DirectionsLegManeuverCardinalDirection.West);
        TryDetermineCardinalDirection(270, DirectionsLegManeuverCardinalDirection.West);
        TryDetermineCardinalDirection(293, DirectionsLegManeuverCardinalDirection.West);

        TryDetermineCardinalDirection(294, DirectionsLegManeuverCardinalDirection.NorthWest);
        TryDetermineCardinalDirection(315, DirectionsLegManeuverCardinalDirection.NorthWest);
        TryDetermineCardinalDirection(336, DirectionsLegManeuverCardinalDirection.NorthWest);
    }

    // ============================================================================================
    // TestDetermineRelativeDirection_Maneuver
    // ============================================================================================
    private static void TryDetermineRelativeDirectionManeuver(
        uint prevHeading,
        uint currHeading,
        IEnumerable<uint> intersectingHeadings,
        Maneuver.RelativeDirection expected)
    {
        var options = new Options();
        var path = new TripLeg();

        // node:0
        TripNode node0 = AddNode(path);
        AddEdge(node0).EndHeading = prevHeading;

        // node:1
        TripNode node1 = AddNode(path);
        AddEdge(node1).BeginHeading = currHeading;
        foreach (uint intersectingHeading in intersectingHeadings)
        {
            var xedge = new TripIntersectingEdge { BeginHeading = intersectingHeading, Driveability = TripTraversability.Both };
            node1.IntersectingEdges.Add(xedge);
        }

        // node:2 dummy last node
        AddNode(path);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);
        var maneuver = new Maneuver();
        maneuver.SetBeginNodeIndex(1);
        maneuver.SetTurnDegree(Util.GetTurnDegree(prevHeading, currHeading));
        mbTest.DetermineRelativeDirectionPublic(maneuver);
        Assert.Equal(expected, maneuver.BeginRelativeDirection());
    }

    [Fact]
    public void TestDetermineRelativeDirection_Maneuver()
    {
        // Path straight, intersecting straight on the left - thus keep right
        TryDetermineRelativeDirectionManeuver(0, 5, new uint[] { 355 }, Maneuver.RelativeDirection.KeepRight);

        // Path straight, intersecting straight on the right - thus keep left
        TryDetermineRelativeDirectionManeuver(0, 355, new uint[] { 5 }, Maneuver.RelativeDirection.KeepLeft);

        // Path slight right, intersecting straight on the left - thus keep right
        TryDetermineRelativeDirectionManeuver(0, 11, new uint[] { 0 }, Maneuver.RelativeDirection.KeepRight);
        TryDetermineRelativeDirectionManeuver(90, 105, new uint[] { 85 }, Maneuver.RelativeDirection.KeepRight);

        // Path slight left, intersecting straight on the right - thus keep left
        TryDetermineRelativeDirectionManeuver(0, 345, new uint[] { 355 }, Maneuver.RelativeDirection.KeepLeft);
        TryDetermineRelativeDirectionManeuver(270, 255, new uint[] { 275 }, Maneuver.RelativeDirection.KeepLeft);

        // Path slight left, intersecting right and left - thus keep straight
        TryDetermineRelativeDirectionManeuver(80, 60, new uint[] { 157, 337 }, Maneuver.RelativeDirection.KeepStraight);

        // Path sharp right, intersecting right and left - thus right
        TryDetermineRelativeDirectionManeuver(180, 339, new uint[] { 355, 270, 180, 90, 10 }, Maneuver.RelativeDirection.Right);

        // Path sharp left, intersecting right and left - thus left
        TryDetermineRelativeDirectionManeuver(180, 21, new uint[] { 90, 180, 270, 352, 355, 5 }, Maneuver.RelativeDirection.Left);

        // Path reverse right, intersecting right and left - thus reverse
        TryDetermineRelativeDirectionManeuver(180, 352, new uint[] { 355, 270, 180, 90, 10 }, Maneuver.RelativeDirection.Reverse);

        // Path reverse left, intersecting right and left - thus reverse
        TryDetermineRelativeDirectionManeuver(180, 15, new uint[] { 355, 270, 180, 90, 10 }, Maneuver.RelativeDirection.Reverse);
    }

    // ============================================================================================
    // TestDetermineRelativeDirection (static turn-degree overload)
    // ============================================================================================
    private static void TryDetermineRelativeDirection(uint turnDegree, Maneuver.RelativeDirection expected)
    {
        Assert.Equal(expected, ManeuversBuilder.DetermineRelativeDirection(turnDegree));
    }

    [Fact]
    public void TestDetermineRelativeDirection()
    {
        TryDetermineRelativeDirection(330, Maneuver.RelativeDirection.KeepStraight);
        TryDetermineRelativeDirection(0, Maneuver.RelativeDirection.KeepStraight);
        TryDetermineRelativeDirection(30, Maneuver.RelativeDirection.KeepStraight);

        TryDetermineRelativeDirection(31, Maneuver.RelativeDirection.Right);
        TryDetermineRelativeDirection(90, Maneuver.RelativeDirection.Right);
        TryDetermineRelativeDirection(159, Maneuver.RelativeDirection.Right);

        TryDetermineRelativeDirection(160, Maneuver.RelativeDirection.Reverse);
        TryDetermineRelativeDirection(180, Maneuver.RelativeDirection.Reverse);
        TryDetermineRelativeDirection(200, Maneuver.RelativeDirection.Reverse);

        TryDetermineRelativeDirection(201, Maneuver.RelativeDirection.Left);
        TryDetermineRelativeDirection(270, Maneuver.RelativeDirection.Left);
        TryDetermineRelativeDirection(329, Maneuver.RelativeDirection.Left);
    }

    // ============================================================================================
    // TestLeftInternalStraightCombine
    // ============================================================================================
    [Fact]
    public void TestLeftInternalStraightCombine()
    {
        var options = new Options();
        var path = new TripLeg();

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Hershey Road", false), ("PA 743", true), ("PA 341 Truck", true) },
            0.033835f, 60.0, RoadClass.Secondary, 158, 180, 0, 3, TripTraversability.Both, false, false, false, false,
            false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Hershey Road", false), ("PA 743 South", true) },
            0.181000f, 60.0, RoadClass.Secondary, 187, 192, 3, 8, TripTraversability.Both, false, false, false, false,
            false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Hershey Road", false), ("PA 743 South", true) },
            0.079000f, 60.0, RoadClass.Secondary, 196, 196, 8, 10, TripTraversability.Both, false, false, false, false,
            false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Hershey Road", false), ("PA 743 South", true) },
            0.160000f, 60.0, RoadClass.Secondary, 198, 198, 10, 13, TripTraversability.Both, false, false, false, false,
            false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        // node:4 INTERNAL_INTERSECTION
        PopulateEdge(AddEdge(AddNode(path)), NoNames, 0.013000f, 50.0, RoadClass.Secondary, 118, 118, 13, 14,
            TripTraversability.Forward, true, false, false, false, false, false, false, false, false, true, NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), NoNames, 0.073000f, 50.0, RoadClass.Secondary, 127, 127, 14, 15,
            TripTraversability.Forward, true, false, false, false, false, false, false, false, false, false, NoNames,
            new[] { ("PA 283 East", true) }, new[] { ("Lancaster", false) }, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), NoNames, 0.432000f, 50.0, RoadClass.Secondary, 127, 130, 15, 20,
            TripTraversability.Forward, true, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("PA 283 East", true) }, 0.176467f, 105.0, RoadClass.Motorway, 134, 134, 20, 22,
            TripTraversability.Forward, false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);

        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start,
            new[] { ("Hershey Road", false), ("PA 743 South", true) }, NoNames, NoNames, "", 0.453835f, 28, 0,
            Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.South, 158, 198, 0, 4, 0, 13,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.None, NoNames, NoNames, NoNames, "",
            0.013000f, 1, 280, Maneuver.RelativeDirection.Left, DirectionsLegManeuverCardinalDirection.SouthEast, 118, 118,
            4, 5, 13, 14, true, false, false, false, false, false, false, false, true, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.RampStraight, NoNames, NoNames, NoNames, "",
            0.505000f, 36, 9, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.SouthEast,
            127, 130, 5, 7, 14, 20, true, false, false, false, false, false, false, false, false, NoSigns,
            new[] { ("PA 283 East", true, 0u) }, new[] { ("Lancaster", false, 0u) }, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.MergeLeft, new[] { ("PA 283 East", true) }, NoNames, NoNames, "",
            0.176467f, 6, 4, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.SouthEast,
            134, 134, 7, 8, 20, 22, false, false, false, false, false, false, false, true, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 8, 8, 22, 22,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start,
            new[] { ("Hershey Road", false), ("PA 743 South", true) }, NoNames, NoNames, "", 0.453835f, 28, 0,
            Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.South, 158, 198, 0, 4, 0, 13,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.RampLeft, NoNames, NoNames, NoNames, "",
            0.518000f, 37, 289, Maneuver.RelativeDirection.Left, DirectionsLegManeuverCardinalDirection.SouthEast,
            127, 130, 4, 7, 13, 20, true, false, false, false, false, false, false, false, false, NoSigns,
            new[] { ("PA 283 East", true, 0u) }, new[] { ("Lancaster", false, 0u) }, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.MergeLeft, new[] { ("PA 283 East", true) }, NoNames, NoNames, "",
            0.176467f, 6, 4, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.SouthEast,
            134, 134, 7, 8, 20, 22, false, false, false, false, false, false, false, true, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 8, 8, 22, 22,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryCombine(mbTest, maneuvers, expected);
    }

    // ============================================================================================
    // TestLeftInternalUturnCombine
    // ============================================================================================
    [Fact]
    public void TestLeftInternalUturnCombine()
    {
        var options = new Options();
        var path = new TripLeg();

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Jonestown Road", false), ("US 22", true) },
            0.062923f, 75.0, RoadClass.Primary, 36, 32, 0, 2, TripTraversability.Both, false, false, false, false,
            false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        // node:1 TURN_CHANNEL
        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Devonshire Road", false) },
            0.013000f, 50.0, RoadClass.Tertiary, 299, 299, 2, 3, TripTraversability.Both, false, false, false, false,
            false, false, false, false, false, true, NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Jonestown Road", false), ("US 22", true) },
            0.059697f, 75.0, RoadClass.Primary, 212, 221, 3, 5, TripTraversability.Both, false, false, false, false,
            false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);

        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start,
            new[] { ("Jonestown Road", false), ("US 22", true) }, NoNames, NoNames, "", 0.062923f, 3, 0,
            Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.NorthEast, 36, 32, 0, 1, 0, 2,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.None, new[] { ("Devonshire Road", false) }, NoNames, NoNames, "",
            0.013000f, 1, 267, Maneuver.RelativeDirection.Left, DirectionsLegManeuverCardinalDirection.NorthWest, 299, 299,
            1, 2, 2, 3, false, false, false, false, false, false, false, false, true, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Left,
            new[] { ("Jonestown Road", false), ("US 22", true) }, NoNames, NoNames, "", 0.059697f, 3, 273,
            Maneuver.RelativeDirection.Left, DirectionsLegManeuverCardinalDirection.SouthWest, 212, 221, 2, 3, 3, 5,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 3, 3, 5, 5,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start,
            new[] { ("Jonestown Road", false), ("US 22", true) }, NoNames, NoNames, "", 0.062923f, 3, 0,
            Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.NorthEast, 36, 32, 0, 1, 0, 2,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.UturnLeft,
            new[] { ("Jonestown Road", false), ("US 22", true) }, NoNames, new[] { ("Devonshire Road", false) }, "",
            0.072697f, 4, 180, Maneuver.RelativeDirection.Reverse, DirectionsLegManeuverCardinalDirection.SouthWest, 212, 221,
            1, 3, 2, 5, false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 3, 3, 5, 5,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryCombine(mbTest, maneuvers, expected);
    }

    // ============================================================================================
    // TestSimpleRightTurnChannelCombine
    // ============================================================================================
    [Fact]
    public void TestSimpleRightTurnChannelCombine()
    {
        var options = new Options();
        var path = new TripLeg();

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("MD 43 East", true), ("White Marsh Boulevard", false) },
            0.091237f, 80.0, RoadClass.Trunk, 59, 94, 0, 4, TripTraversability.Both, false, false, false, false,
            false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        // node:1 TURN_CHANNEL
        PopulateEdge(AddEdge(AddNode(path)), NoNames, 0.142000f, 113.0, RoadClass.Secondary, 105, 179, 4, 11,
            TripTraversability.Both, false, true, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Perry Hall Boulevard", false) }, 0.065867f, 64.0, RoadClass.Secondary, 188, 188, 11, 14,
            TripTraversability.Both, false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        // node:3 end node
        AddNode(path);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);

        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start,
            new[] { ("MD 43 East", true), ("White Marsh Boulevard", false) }, NoNames, NoNames, "", 0.091237f, 4, 0,
            Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.NorthEast, 59, 94, 0, 1, 0, 4,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.None, NoNames, NoNames, NoNames, "",
            0.142000f, 5, 11, Maneuver.RelativeDirection.KeepRight, DirectionsLegManeuverCardinalDirection.East, 105, 179,
            1, 2, 4, 11, false, true, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Continue, new[] { ("Perry Hall Boulevard", false) }, NoNames, NoNames, "",
            0.065867f, 4, 9, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.South, 188, 188,
            2, 3, 11, 14, false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 3, 3, 14, 14,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start,
            new[] { ("MD 43 East", true), ("White Marsh Boulevard", false) }, NoNames, NoNames, "", 0.091237f, 4, 0,
            Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.NorthEast, 59, 94, 0, 1, 0, 4,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Right, new[] { ("Perry Hall Boulevard", false) }, NoNames, NoNames, "",
            0.207867f, 9, 94, Maneuver.RelativeDirection.KeepRight, DirectionsLegManeuverCardinalDirection.South, 188, 188,
            1, 3, 4, 14, false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 3, 3, 14, 14,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryCombine(mbTest, maneuvers, expected);
    }

    // ============================================================================================
    // TestStraightInternalLeftInternalCombine
    // ============================================================================================
    [Fact]
    public void TestStraightInternalLeftInternalCombine()
    {
        var options = new Options();
        var path = new TripLeg();

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Broken Land Parkway", false) }, 0.056148f, 72.0, RoadClass.Secondary,
            26, 24, 0, 2, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Broken Land Parkway", false) }, 0.081000f, 72.0, RoadClass.Secondary,
            24, 24, 2, 3, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);

        // node:2 INTERNAL_INTERSECTION
        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Broken Land Parkway", false) }, 0.017000f, 72.0, RoadClass.Secondary,
            25, 25, 3, 4, TripTraversability.Both, false, false, false, false, false, false, false, false, false, true,
            NoNames, NoNames, NoNames, NoNames);

        // node:3 INTERNAL_INTERSECTION
        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Snowden River Parkway", false) }, 0.030000f, 60.0, RoadClass.Secondary,
            291, 291, 4, 5, TripTraversability.Both, false, false, false, false, false, false, false, false, false, true,
            NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Patuxent Woods Drive", false) }, 0.059840f, 40.0, RoadClass.Tertiary,
            292, 270, 5, 8, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);

        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, new[] { ("Broken Land Parkway", false) }, NoNames, NoNames, "",
            0.137148f, 7, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.NorthEast, 26, 24, 0, 2, 0, 3,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.None, NoNames, NoNames, NoNames, "",
            0.047000f, 3, 1, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.NorthEast, 25, 291,
            2, 4, 3, 5, false, false, false, false, false, false, false, false, true, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Continue, new[] { ("Patuxent Woods Drive", false) }, NoNames, NoNames, "",
            0.059840f, 5, 1, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.West, 292, 270,
            4, 5, 5, 8, false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 5, 5, 8, 8,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, new[] { ("Broken Land Parkway", false) }, NoNames, NoNames, "",
            0.137148f, 7, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.NorthEast, 26, 24, 0, 2, 0, 3,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Left, new[] { ("Patuxent Woods Drive", false) }, NoNames, NoNames, "",
            0.106840f, 8, 268, Maneuver.RelativeDirection.Left, DirectionsLegManeuverCardinalDirection.West, 292, 270,
            2, 5, 3, 8, false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 5, 5, 8, 8,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryCombine(mbTest, maneuvers, expected);
    }

    // ============================================================================================
    // TestStraightInternalStraightCombine
    // ============================================================================================
    [Fact]
    public void TestStraightInternalStraightCombine()
    {
        var options = new Options();
        var path = new TripLeg();
        var names = new[] { ("MD 43 East", true), ("White Marsh Boulevard", false) };

        PopulateEdge(AddEdge(AddNode(path)), names, 0.120902f, 80.0, RoadClass.Trunk, 59, 94, 0, 5, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);
        PopulateEdge(AddEdge(AddNode(path)), names, 0.086000f, 80.0, RoadClass.Trunk, 94, 94, 5, 8, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);
        // node:2 INTERNAL_INTERSECTION
        PopulateEdge(AddEdge(AddNode(path)), names, 0.018000f, 90.0, RoadClass.Trunk, 96, 96, 8, 9, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, true, NoNames, NoNames, NoNames, NoNames);
        PopulateEdge(AddEdge(AddNode(path)), names, 0.099000f, 80.0, RoadClass.Trunk, 94, 95, 9, 12, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);
        PopulateEdge(AddEdge(AddNode(path)), names, 0.774000f, 80.0, RoadClass.Trunk, 96, 88, 12, 28, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);
        PopulateEdge(AddEdge(AddNode(path)), names, 0.123000f, 80.0, RoadClass.Trunk, 90, 90, 28, 32, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);
        PopulateEdge(AddEdge(AddNode(path)), names, 0.009000f, 80.0, RoadClass.Trunk, 86, 86, 32, 33, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);
        // node:7 INTERNAL_INTERSECTION
        PopulateEdge(AddEdge(AddNode(path)), names, 0.015000f, 72.0, RoadClass.Trunk, 93, 93, 33, 34, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, true, NoNames, NoNames, NoNames, NoNames);
        PopulateEdge(AddEdge(AddNode(path)), names, 0.077000f, 72.0, RoadClass.Trunk, 90, 90, 34, 35, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);
        PopulateEdge(AddEdge(AddNode(path)), names, 0.217965f, 72.0, RoadClass.Trunk, 90, 89, 35, 40, TripTraversability.Both,
            false, false, false, false, false, false, false, false, false, false, NoNames, NoNames, NoNames, NoNames);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);

        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, names, NoNames, NoNames, "",
            0.206902f, 9, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.NorthEast, 59, 94, 0, 2, 0, 8,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.None, NoNames, NoNames, NoNames, "",
            0.018000f, 1, 2, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.East, 96, 96,
            2, 3, 8, 9, false, false, false, false, false, false, false, false, true, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Continue, names, NoNames, NoNames, "",
            1.005000f, 45, 358, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.East, 94, 86,
            3, 7, 9, 33, false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.None, NoNames, NoNames, NoNames, "",
            0.015000f, 1, 7, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.East, 93, 93,
            7, 8, 33, 34, false, false, false, false, false, false, false, false, true, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Continue, names, NoNames, NoNames, "",
            0.294965f, 15, 357, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.East, 90, 89,
            8, 10, 34, 40, false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 10, 10, 40, 40,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, names, NoNames, NoNames, "",
            1.539867f, 71, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.NorthEast, 59, 10, 0, 10, 0, 40,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 10, 10, 40, 40,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryCombine(mbTest, maneuvers, expected);
    }

    // ============================================================================================
    // TestInternalPencilPointUturnProperDirectionCombine
    // ============================================================================================
    [Fact]
    public void TestInternalPencilPointUturnProperDirectionCombine()
    {
        var options = new Options();
        var path = new TripLeg();

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Stonewall Shops Square", false) }, 0.027386f, 40.0, RoadClass.Unclassified,
            352, 343, 0, 2, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);

        // node:1 TURN_CHANNEL
        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Old Carolina Road", false) }, 0.019000f, 50.0, RoadClass.Tertiary,
            331, 331, 2, 3, TripTraversability.Both, false, false, false, false, false, false, false, false, false, true,
            NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Stonewall Shops Square", false) }, 0.021000f, 50.0, RoadClass.Tertiary,
            187, 187, 3, 4, TripTraversability.Both, false, false, false, false, false, false, false, false, false, true,
            NoNames, NoNames, NoNames, NoNames);

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Stonewall Shops Square", false) }, 0.025240f, 40.0, RoadClass.Unclassified,
            162, 149, 4, 6, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);

        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, new[] { ("Stonewall Shops Square", false) }, NoNames, NoNames, "",
            0.027386f, 2, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 352, 343, 0, 1, 0, 2,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.None, new[] { ("Stonewall Shops Square", false) }, NoNames, NoNames, "",
            0.040000f, 3, 348, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.NorthWest, 331, 187,
            1, 3, 2, 4, false, false, false, false, false, false, false, false, true, NoSigns, NoSigns, NoSigns, NoSigns,
            internalLeftTurnCount: 1));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.SlightLeft, new[] { ("Stonewall Shops Square", false) }, NoNames, NoNames, "",
            0.025240f, 2, 335, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.South, 162, 149,
            3, 4, 4, 6, false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 4, 4, 6, 6,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, new[] { ("Stonewall Shops Square", false) }, NoNames, NoNames, "",
            0.027386f, 2, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 352, 343, 0, 1, 0, 2,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.UturnLeft, new[] { ("Stonewall Shops Square", false) }, NoNames, NoNames, "",
            0.065240f, 5, 179, Maneuver.RelativeDirection.Reverse, DirectionsLegManeuverCardinalDirection.South, 162, 149,
            1, 4, 2, 6, false, false, false, false, false, false, false, false, true, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 4, 4, 6, 6,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryCombine(mbTest, maneuvers, expected);
    }

    // ============================================================================================
    // TestCountAndSortExitSigns
    // ============================================================================================
    private static void TryCountAndSortExitSigns(LinkedList<Maneuver> maneuvers, LinkedList<Maneuver> expectedManeuvers)
    {
        var mbTest = new ManeuversBuilderTest();
        mbTest.CountAndSortSignsPublic(maneuvers);

        Assert.Equal(expectedManeuvers.Count, maneuvers.Count);

        LinkedListNode<Maneuver>? man = maneuvers.First;
        LinkedListNode<Maneuver>? expectedMan = expectedManeuvers.First;
        while (man != null && expectedMan != null)
        {
            Assert.True(expectedMan.Value.GetSigns().Equals(man.Value.GetSigns()));
            man = man.Next;
            expectedMan = expectedMan.Next;
        }
    }

    [Fact]
    public void TestCountAndSortExitSigns()
    {
        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start,
            new[] { ("I 81 South", true), ("US 322 West", true), ("American Legion Memorial Highway", false) }, NoNames, NoNames, "",
            0.158406f, 10, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.West, 262, 270, 0, 1, 0, 2,
            false, false, false, false, false, false, false, true, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.ExitRight, new[] { ("US 322 West", true) }, NoNames, NoNames, "",
            0.348589f, 21, 2, Maneuver.RelativeDirection.KeepRight, DirectionsLegManeuverCardinalDirection.West, 272, 278, 1, 2, 2, 6,
            true, false, false, false, false, false, false, false, false,
            new[] { ("67A-B", false, 0u) },
            new[] { ("US 22 East", true, 0u), ("PA 230 East", true, 0u), ("US 22 West", true, 0u), ("US 322 West", true, 0u), ("Cameron Street", false, 0u) },
            new[] { ("Harrisburg", false, 0u), ("Lewistown", false, 0u), ("State College", false, 0u) },
            NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.ExitRight, new[] { ("US 322 West", true) }, NoNames, NoNames, "",
            0.633177f, 39, 8, Maneuver.RelativeDirection.KeepRight, DirectionsLegManeuverCardinalDirection.West, 286, 353, 2, 4, 6, 31,
            true, false, false, false, false, false, false, false, false,
            new[] { ("67B", false, 0u) },
            new[] { ("US 22 West", true, 0u), ("US 322 West", true, 0u) },
            new[] { ("Lewistown", false, 0u), ("State College", false, 0u) },
            NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.MergeLeft, new[] { ("US 322 West", true) }, NoNames, NoNames, "",
            55.286610f, 3319, 358, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.North, 351, 348, 4, 57, 31, 1303,
            false, false, false, false, false, false, false, true, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 57, 57, 1303, 1303,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start,
            new[] { ("I 81 South", true), ("US 322 West", true), ("American Legion Memorial Highway", false) }, NoNames, NoNames, "",
            0.158406f, 10, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.West, 262, 270, 0, 1, 0, 2,
            false, false, false, false, false, false, false, true, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.ExitRight, new[] { ("US 322 West", true) }, NoNames, NoNames, "",
            0.348589f, 21, 2, Maneuver.RelativeDirection.KeepRight, DirectionsLegManeuverCardinalDirection.West, 272, 278, 1, 2, 2, 6,
            true, false, false, false, false, false, false, false, false,
            new[] { ("67A-B", false, 0u) },
            new[] { ("US 322 West", true, 2u), ("US 22 West", true, 1u), ("US 22 East", true, 0u), ("PA 230 East", true, 0u), ("Cameron Street", false, 0u) },
            new[] { ("Lewistown", false, 1u), ("State College", false, 1u), ("Harrisburg", false, 0u) },
            NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.ExitRight, new[] { ("US 322 West", true) }, NoNames, NoNames, "",
            0.633177f, 39, 8, Maneuver.RelativeDirection.KeepRight, DirectionsLegManeuverCardinalDirection.West, 286, 353, 2, 4, 6, 31,
            true, false, false, false, false, false, false, false, false,
            new[] { ("67B", false, 0u) },
            new[] { ("US 322 West", true, 2u), ("US 22 West", true, 1u) },
            new[] { ("Lewistown", false, 1u), ("State College", false, 1u) },
            NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.MergeLeft, new[] { ("US 322 West", true) }, NoNames, NoNames, "",
            55.286610f, 3319, 358, Maneuver.RelativeDirection.KeepStraight, DirectionsLegManeuverCardinalDirection.North, 351, 348, 4, 57, 31, 1303,
            false, false, false, false, false, false, false, true, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.000000f, 0, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.North, 0, 0, 57, 57, 1303, 1303,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryCountAndSortExitSigns(maneuvers, expected);
    }

    // ============================================================================================
    // IsIntersectingForwardEdge cases
    // ============================================================================================
    private static void TryIsIntersectingForwardEdge(ManeuversBuilderTest mbTest, EnhancedTripLeg etp, int nodeIndex, bool expected)
    {
        EnhancedTripLeg_Edge prevEdge = etp.GetPrevEdge(nodeIndex)!;
        EnhancedTripLeg_Edge currEdge = etp.GetCurrEdge(nodeIndex)!;
        bool result = mbTest.IsIntersectingForwardEdgePublic(nodeIndex, prevEdge, currEdge);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TestPathRightXStraightIsIntersectingForwardEdge()
    {
        var options = new Options();
        var path = new TripLeg();

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Raleigh Road", false) }, 0.027827f, 30.0, RoadClass.Residential,
            250, 291, 0, 1, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);

        TripNode node1 = AddNode(path);
        PopulateEdge(AddEdge(node1), new[] { ("Raleigh Road", false) }, 0.054344f, 30.0, RoadClass.Residential,
            20, 337, 1, 3, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);
        var x1 = new TripIntersectingEdge();
        PopulateIntersectingEdge(x1, 289, true, true, TripTraversability.Both);
        node1.IntersectingEdges.Add(x1);

        AddNode(path);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);
        TryIsIntersectingForwardEdge(mbTest, etp, 1, true);
    }

    [Fact]
    public void TestPathLeftXStraightIsIntersectingForwardEdge()
    {
        var options = new Options();
        var path = new TripLeg();

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Raleigh Road", false) }, 0.047007f, 30.0, RoadClass.Residential,
            108, 108, 0, 1, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);

        TripNode node1 = AddNode(path);
        PopulateEdge(AddEdge(node1), new[] { ("Raleigh Road", false) }, 0.046636f, 30.0, RoadClass.Residential,
            20, 337, 1, 3, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);
        var x1 = new TripIntersectingEdge();
        PopulateIntersectingEdge(x1, 111, true, true, TripTraversability.Both);
        node1.IntersectingEdges.Add(x1);

        AddNode(path);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);
        TryIsIntersectingForwardEdge(mbTest, etp, 1, true);
    }

    [Fact]
    public void TestPathSlightRightXSlightLeftIsIntersectingForwardEdge()
    {
        var options = new Options();
        var path = new TripLeg();

        PopulateEdge(AddEdge(AddNode(path)), new[] { ("Horace Greeley Road", false) }, 0.102593f, 30.0, RoadClass.Residential,
            23, 13, 0, 6, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);

        TripNode node1 = AddNode(path);
        PopulateEdge(AddEdge(node1), new[] { ("Horace Greeley Road", false) }, 0.205258f, 30.0, RoadClass.Residential,
            45, 19, 6, 12, TripTraversability.Both, false, false, false, false, false, false, false, false, false, false,
            NoNames, NoNames, NoNames, NoNames);
        var x1 = new TripIntersectingEdge();
        PopulateIntersectingEdge(x1, 3, false, false, TripTraversability.Both);
        node1.IntersectingEdges.Add(x1);

        AddNode(path);

        var etp = new EnhancedTripLeg(path);
        var mbTest = new ManeuversBuilderTest(options, etp);
        TryIsIntersectingForwardEdge(mbTest, etp, 1, true);
    }

    // ============================================================================================
    // Roundabout combine / un-collapse
    // ============================================================================================
    private static void TryCombineRoundaboutManeuvers(LinkedList<Maneuver> maneuvers, LinkedList<Maneuver> expectedManeuvers)
    {
        var options = new Options { RoundaboutExits = false };
        var mbTest = new ManeuversBuilderTest(options);

        mbTest.ProcessRoundaboutsPublic(maneuvers);

        Assert.Equal(expectedManeuvers.Count, maneuvers.Count);

        LinkedListNode<Maneuver>? man = maneuvers.First;
        LinkedListNode<Maneuver>? expectedMan = expectedManeuvers.First;
        while (man != null && expectedMan != null)
        {
            Assert.Equal(expectedMan.Value.Type(), man.Value.Type());
            Assert.Equal(expectedMan.Value.HasCombinedEnterExitRoundabout(), man.Value.HasCombinedEnterExitRoundabout());
            Assert.Equal(expectedMan.Value.RoundaboutLength(), man.Value.RoundaboutLength(), 5);
            Assert.Equal(expectedMan.Value.RoundaboutExitLength(), man.Value.RoundaboutExitLength(), 5);
            Assert.Equal(expectedMan.Value.RoundaboutExitBeginHeading(), man.Value.RoundaboutExitBeginHeading());
            Assert.Equal(expectedMan.Value.RoundaboutExitTurnDegree(), man.Value.RoundaboutExitTurnDegree());
            Assert.Equal(expectedMan.Value.RoundaboutExitShapeIndex(), man.Value.RoundaboutExitShapeIndex());
            man = man.Next;
            expectedMan = expectedMan.Next;
        }
    }

    [Fact]
    public void TestCombineRoundaboutManeuvers()
    {
        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, new[] { ("first st", false) }, NoNames, NoNames, "",
            1.0f, 1, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.West, 0, 100, 0, 0, 0, 5,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.RoundaboutEnter, NoNames, NoNames, NoNames, "",
            1.0f, 1, 32, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 150, 250, 0, 0, 5, 10,
            false, false, false, false, true, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.RoundaboutExit, NoNames, NoNames, NoNames, "",
            2.0f, 1, 90, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 280, 310, 0, 0, 10, 15,
            false, false, false, false, true, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.0f, 1, 0, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 0, 0, 0, 0, 15, 15,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, new[] { ("first st", false) }, NoNames, NoNames, "",
            1.0f, 1, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.West, 0, 100, 0, 0, 0, 5,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m =>
        {
            PopulateManeuver(m, DirectionsLegManeuverType.RoundaboutEnter, NoNames, NoNames, NoNames, "",
                1.0f, 2, 32, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 150, 310, 0, 0, 5, 15,
                false, false, false, false, true, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns);
            m.SetHasCombinedEnterExitRoundabout(true);
            m.SetRoundaboutExitBeginHeading(280);
            m.SetRoundaboutLength(1.0f);
            m.SetRoundaboutExitLength(2.0f);
            m.SetRoundaboutExitTurnDegree(90);
            m.SetRoundaboutExitShapeIndex(10);
        });

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.0f, 1, 0, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 0, 0, 0, 0, 0, 0,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryCombineRoundaboutManeuvers(maneuvers, expected);
    }

    private static void TryUnCollapsedRoundaboutManeuvers(LinkedList<Maneuver> maneuvers, LinkedList<Maneuver> expectedManeuvers)
    {
        var options = new Options { RoundaboutExits = true };
        var mbTest = new ManeuversBuilderTest(options);

        mbTest.ProcessRoundaboutsPublic(maneuvers);

        Assert.Equal(expectedManeuvers.Count, maneuvers.Count);

        LinkedListNode<Maneuver>? man = maneuvers.First;
        LinkedListNode<Maneuver>? expectedMan = expectedManeuvers.First;
        while (man != null && expectedMan != null)
        {
            Assert.Equal(expectedMan.Value.Type(), man.Value.Type());
            Assert.Equal(expectedMan.Value.HasCombinedEnterExitRoundabout(), man.Value.HasCombinedEnterExitRoundabout());
            Assert.Equal(expectedMan.Value.RoundaboutLength(), man.Value.RoundaboutLength(), 5);
            Assert.Equal(expectedMan.Value.RoundaboutExitLength(), man.Value.RoundaboutExitLength(), 5);
            Assert.Equal(expectedMan.Value.RoundaboutExitBeginHeading(), man.Value.RoundaboutExitBeginHeading());
            Assert.Equal(expectedMan.Value.RoundaboutExitTurnDegree(), man.Value.RoundaboutExitTurnDegree());
            man = man.Next;
            expectedMan = expectedMan.Next;
        }
    }

    [Fact]
    public void TestUnCollapseRoundaboutManeuvers()
    {
        var maneuvers = new LinkedList<Maneuver>();
        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, new[] { ("first st", false) }, NoNames, NoNames, "",
            1.0f, 1, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.West, 0, 100, 0, 0, 0, 0,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.RoundaboutEnter, NoNames, NoNames, NoNames, "",
            1.0f, 1, 32, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 150, 250, 0, 0, 0, 0,
            false, false, false, false, true, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.RoundaboutExit, NoNames, NoNames, NoNames, "",
            2.0f, 1, 90, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 280, 310, 0, 0, 0, 0,
            false, false, false, false, true, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(maneuvers, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.0f, 1, 0, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 0, 0, 0, 0, 0, 0,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        var expected = new LinkedList<Maneuver>();
        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Start, new[] { ("first st", false) }, NoNames, NoNames, "",
            1.0f, 1, 0, Maneuver.RelativeDirection.None, DirectionsLegManeuverCardinalDirection.West, 0, 100, 0, 0, 0, 0,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.RoundaboutEnter, NoNames, NoNames, NoNames, "",
            1.0f, 2, 32, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 150, 310, 0, 0, 0, 0,
            false, false, false, false, true, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.RoundaboutExit, NoNames, NoNames, NoNames, "",
            2.0f, 1, 90, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 280, 310, 0, 0, 0, 0,
            false, false, false, false, true, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        AddManeuver(expected, m => PopulateManeuver(m, DirectionsLegManeuverType.Destination, NoNames, NoNames, NoNames, "",
            0.0f, 1, 0, Maneuver.RelativeDirection.Right, DirectionsLegManeuverCardinalDirection.West, 0, 0, 0, 0, 0, 0,
            false, false, false, false, false, false, false, false, false, NoSigns, NoSigns, NoSigns, NoSigns));

        TryUnCollapsedRoundaboutManeuvers(maneuvers, expected);
    }

    private static void AddManeuver(LinkedList<Maneuver> maneuvers, System.Action<Maneuver> populate)
    {
        var maneuver = new Maneuver();
        populate(maneuver);
        maneuvers.AddLast(maneuver);
    }
}
