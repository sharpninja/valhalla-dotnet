// Tests for the ported odin DirectionsBuilder (top-level TripLeg -> DirectionsLeg).
//
// PORT-NOTE: Valhalla's directionsbuilder gtest (test/instructions.cc and the scenario-driven
// narrative tests) exercises the full request/narrative pipeline that is DEFERRED here. These tests
// cover the structural top-level contract: UpdateHeading fixes ~0-length edge headings, Build wraps
// the leg and runs the maneuver builder, and PopulateDirectionsLeg transfers the ordered maneuvers
// plus the leg-level shape / toll / highway / ferry flags. The maneuver-structure correctness itself
// is covered by ManeuversBuilderTests.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Odin;

public class DirectionsBuilderTests
{
    private static TripEdge MakeEdge(string name, float lengthKm, uint beginHeading, uint endHeading, uint beginShape, uint endShape)
    {
        var edge = new TripEdge
        {
            LengthKm = lengthKm,
            SpeedKph = 50,
            DefaultSpeed = 50,
            RoadClass = RoadClass.Secondary,
            Use = Use.Road,
            Mode = TravelMode.Drive,
            Traversability = TripTraversability.Both,
            BeginHeading = beginHeading,
            EndHeading = endHeading,
            BeginShapeIndex = beginShape,
            EndShapeIndex = endShape,
        };
        edge.Names.Add(name);
        return edge;
    }

    private static TripLeg MakeStraightLeg()
    {
        // Three real edges all pointing east ("Main Street"), then a final node with no edge.
        var leg = new TripLeg { EncodedShape = "encoded_shape" };
        leg.Summary.HasHighway = true;
        leg.Admins.Add(new TripAdmin("US", "United States", "PA", "Pennsylvania"));

        leg.Nodes.Add(new TripNode { Edge = MakeEdge("Main Street", 0.5f, 90, 90, 0, 2) });
        leg.Nodes.Add(new TripNode { Edge = MakeEdge("Main Street", 0.5f, 90, 90, 2, 4) });
        leg.Nodes.Add(new TripNode { Edge = MakeEdge("Main Street", 0.5f, 90, 90, 4, 6) });
        leg.Nodes.Add(new TripNode()); // last node (destination), no edge
        return leg;
    }

    [Fact]
    public void Build_StraightLeg_ProducesStartAndDestination()
    {
        var options = new Options();
        TripLeg leg = MakeStraightLeg();

        DirectionsLeg directions = DirectionsBuilder.Build(options, leg);

        // The three same-name straight edges combine into a single Start maneuver followed by the
        // Destination maneuver.
        Assert.Equal(2, directions.Maneuvers.Count);
        Assert.Equal(DirectionsLegManeuverType.Start, directions.Maneuvers[0].Type());
        Assert.Equal(DirectionsLegManeuverType.Destination, directions.Maneuvers[1].Type());

        // Start maneuver spans the whole leg.
        Assert.Equal(1.5f, directions.Maneuvers[0].Length(), 5);
        Assert.Equal(DirectionsLegManeuverCardinalDirection.East, directions.Maneuvers[0].BeginCardinalDirection());

        // Leg-level metadata transferred.
        Assert.Equal("encoded_shape", directions.Shape);
        Assert.True(directions.HasHighway);
        Assert.False(directions.HasToll);
        Assert.False(directions.HasFerry);
    }

    [Fact]
    public void Build_DirectionsTypeNone_ProducesNoManeuvers()
    {
        var options = new Options { DirectionsType = DirectionsType.None };
        TripLeg leg = MakeStraightLeg();

        DirectionsLeg directions = DirectionsBuilder.Build(options, leg);

        Assert.Empty(directions.Maneuvers);
        Assert.Equal("encoded_shape", directions.Shape);
    }

    [Fact]
    public void UpdateHeading_ZeroLengthEdge_TakesNextEdgeHeading()
    {
        var leg = new TripLeg();
        // node 0: a degenerate ~0-length edge with no heading
        leg.Nodes.Add(new TripNode { Edge = MakeEdge("A", 0.0005f, 0, 0, 0, 0) });
        // node 1: a real edge heading 123
        leg.Nodes.Add(new TripNode { Edge = MakeEdge("B", 0.5f, 123, 124, 0, 2) });
        // node 2: destination
        leg.Nodes.Add(new TripNode());

        var etp = new EnhancedTripLeg(leg);
        DirectionsBuilder.UpdateHeading(etp);

        // The degenerate current edge at node 0 should take the next edge's begin heading (123) for
        // both begin and end.
        EnhancedTripLeg_Edge curr = etp.GetCurrEdge(0)!;
        Assert.Equal(123u, curr.BeginHeading());
        Assert.Equal(123u, curr.EndHeading());
    }
}
