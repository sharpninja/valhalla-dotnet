// Seam tests: DirectionsBuilder wires the NarrativeBuilder in when DirectionsType.Instructions is
// requested, and leaves maneuvers structure-only (empty Instruction) for DirectionsType.Maneuvers.
//
// Reuses the straight-leg harness shape from DirectionsBuilderTests: three same-name east edges
// combine into a single Start maneuver followed by the Destination maneuver.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Odin;

public class DirectionsBuilderInstructionsTests
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
        var leg = new TripLeg { EncodedShape = "encoded_shape" };
        leg.Summary.HasHighway = true;
        leg.Admins.Add(new TripAdmin("US", "United States", "PA", "Pennsylvania"));

        leg.Nodes.Add(new TripNode { Edge = MakeEdge("Main Street", 0.5f, 90, 90, 0, 2) });
        leg.Nodes.Add(new TripNode { Edge = MakeEdge("Main Street", 0.5f, 90, 90, 2, 4) });
        leg.Nodes.Add(new TripNode { Edge = MakeEdge("Main Street", 0.5f, 90, 90, 4, 6) });
        leg.Nodes.Add(new TripNode()); // destination node
        return leg;
    }

    [Fact]
    public void Instructions_StraightLeg_ProducesWrittenInstructions()
    {
        var options = new Options { DirectionsType = DirectionsType.Instructions };
        TripLeg leg = MakeStraightLeg();

        DirectionsLeg directions = DirectionsBuilder.Build(options, leg);

        Assert.Equal(2, directions.Maneuvers.Count);
        Assert.Equal("Drive east on Main Street.", directions.Maneuvers[0].Instruction());
        Assert.Equal("You have arrived at your destination.", directions.Maneuvers[1].Instruction());
    }

    [Fact]
    public void Maneuvers_StraightLeg_LeavesInstructionsEmpty()
    {
        var options = new Options { DirectionsType = DirectionsType.Maneuvers };
        TripLeg leg = MakeStraightLeg();

        DirectionsLeg directions = DirectionsBuilder.Build(options, leg);

        foreach (Maneuver maneuver in directions.Maneuvers)
        {
            Assert.Equal(string.Empty, maneuver.Instruction());
        }
    }
}
