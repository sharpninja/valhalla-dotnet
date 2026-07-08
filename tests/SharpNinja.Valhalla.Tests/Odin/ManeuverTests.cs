// Structural tests for the ported odin Maneuver data structure.
//
// PORT-NOTE: Valhalla has no standalone gtest for odin/maneuver.cc; the maneuver behavior is
// exercised by test/maneuversbuilder.cc, which depends on the DEFERRED narrativebuilder /
// maneuversbuilder code. These tests cover the structural surface the maneuver builder relies on:
// constructor defaults, the IsXxxType predicates (which switch on DirectionsLegManeuverType exactly
// as the C++ does), HasUsableInternalIntersectionName, and HasSameNames / HasSimilarNames.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Odin;

public class ManeuverTests
{
    [Fact]
    public void TestConstructorDefaults()
    {
        var m = new Maneuver();

        Assert.Equal(DirectionsLegManeuverType.None, m.Type());
        Assert.False(m.HasNodeType());
        Assert.Equal(0.0f, m.Length());
        Assert.Equal(0, m.Time());
        Assert.Equal(0u, m.TurnDegree());
        Assert.Equal(Maneuver.RelativeDirection.None, m.BeginRelativeDirection());
        Assert.Equal(DirectionsLegManeuverCardinalDirection.North, m.BeginCardinalDirection());

        // drive_on_right defaults to true.
        Assert.True(m.DriveOnRight());

        // Travel-type defaults.
        Assert.Equal(TravelMode.Drive, m.GetTravelMode());
        Assert.Equal(VehicleType.Car, m.GetVehicleType());
        Assert.Equal(PedestrianType.Foot, m.GetPedestrianType());
        Assert.Equal(BicycleType.Road, m.GetBicycleType());
        Assert.Equal(TransitType.Rail, m.GetTransitType());
        Assert.Equal(DirectionsLegManeuverBssManeuverType.NoneAction, m.BssManeuverType());

        // Street-name lists are constructed empty.
        Assert.False(m.HasStreetNames());
        Assert.False(m.HasBeginStreetNames());
        Assert.False(m.HasCrossStreetNames());
    }

    [Fact]
    public void TestIsStartType()
    {
        var m = new Maneuver();
        foreach (DirectionsLegManeuverType t in new[]
        {
            DirectionsLegManeuverType.Start, DirectionsLegManeuverType.StartLeft, DirectionsLegManeuverType.StartRight,
        })
        {
            m.SetType(t);
            Assert.True(m.IsStartType());
        }

        m.SetType(DirectionsLegManeuverType.Continue);
        Assert.False(m.IsStartType());
    }

    [Fact]
    public void TestIsDestinationType()
    {
        var m = new Maneuver();
        foreach (DirectionsLegManeuverType t in new[]
        {
            DirectionsLegManeuverType.Destination, DirectionsLegManeuverType.DestinationLeft, DirectionsLegManeuverType.DestinationRight,
        })
        {
            m.SetType(t);
            Assert.True(m.IsDestinationType());
        }

        m.SetType(DirectionsLegManeuverType.Start);
        Assert.False(m.IsDestinationType());
    }

    [Fact]
    public void TestIsLeftAndRightType()
    {
        var m = new Maneuver();

        m.SetType(DirectionsLegManeuverType.SharpLeft);
        Assert.True(m.IsLeftType());
        Assert.False(m.IsRightType());

        m.SetType(DirectionsLegManeuverType.UturnRight);
        Assert.True(m.IsRightType());
        Assert.False(m.IsLeftType());

        m.SetType(DirectionsLegManeuverType.MergeLeft);
        Assert.True(m.IsLeftType());
        Assert.True(m.IsMergeType());
    }

    [Fact]
    public void TestHasUsableInternalIntersectionName()
    {
        var m = new Maneuver();
        m.SetInternalIntersection(true);
        m.SetStreetNames(new[] { ("Main Street", false) });

        // link_count == 1
        m.SetBeginNodeIndex(2);
        m.SetEndNodeIndex(3);
        Assert.True(m.HasUsableInternalIntersectionName());

        // link_count == 3
        m.SetBeginNodeIndex(2);
        m.SetEndNodeIndex(5);
        Assert.True(m.HasUsableInternalIntersectionName());

        // link_count == 2 -> false
        m.SetBeginNodeIndex(2);
        m.SetEndNodeIndex(4);
        Assert.False(m.HasUsableInternalIntersectionName());

        // not an internal intersection -> false
        m.SetInternalIntersection(false);
        m.SetBeginNodeIndex(2);
        m.SetEndNodeIndex(3);
        Assert.False(m.HasUsableInternalIntersectionName());
    }

    [Fact]
    public void TestHasSameNames()
    {
        var a = new Maneuver();
        a.SetStreetNames(new[] { ("Main Street", false) });

        var b = new Maneuver();
        b.SetStreetNames(new[] { ("Main Street", false) });

        Assert.True(a.HasSameNames(b));

        var c = new Maneuver();
        c.SetStreetNames(new[] { ("Broad Street", false) });
        Assert.False(a.HasSameNames(c));

        // begin intersecting edge name consistency blocks same-names unless allowed.
        a.SetBeginIntersectingEdgeNameConsistency(true);
        Assert.False(a.HasSameNames(b));
        Assert.True(a.HasSameNames(b, allowBeginIntersectingEdgeNameConsistency: true));
    }

    [Fact]
    public void TestHasSimilarNames()
    {
        var a = new Maneuver();
        a.SetStreetNames(new[] { ("US 322 West", true) });

        var b = new Maneuver();
        b.SetStreetNames(new[] { ("US 322 East", true) });

        // Same base name ("US 322") -> similar.
        Assert.True(a.HasSimilarNames(b));
    }
}
