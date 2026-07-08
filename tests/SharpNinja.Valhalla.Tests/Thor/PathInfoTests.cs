// Unit tests for the C# port of thor PathInfo (valhalla @ 3.7.0).
// PathInfo is a plain aggregate (the C++ struct has no dedicated gtest; it is exercised through the
// astar suite). These assert the constructor wiring + defaults that TripLegBuilder relies on.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Thor;

public class PathInfoTests
{
    [Fact]
    public void Constructor_Sets_All_Provided_Fields()
    {
        var edge = new GraphId(123, 2, 8);
        var cost = new Cost(10.0f, 12.0f);
        var tc = new Cost(2.0f, 3.0f);

        var p = new PathInfo(TravelMode.Drive, cost, edge, 0, 250.0f, restrictionIdx: 5, tc: tc,
            startNodeIsRecovered: true, isShortcut: true);

        Assert.Equal(TravelMode.Drive, p.Mode);
        Assert.Equal(10.0f, p.ElapsedCost.CostValue);
        Assert.Equal(12.0f, p.ElapsedCost.Secs);
        Assert.Equal(edge, p.Edgeid);
        Assert.Equal(0u, p.TripId);
        Assert.Equal(250.0f, p.PathDistance);
        Assert.Equal((byte)5, p.RestrictionIndex);
        Assert.Equal(2.0f, p.TransitionCost.CostValue);
        Assert.True(p.StartNodeIsRecovered);
        Assert.True(p.IsShortcut);
        Assert.False(p.IsDisconnected);
    }

    [Fact]
    public void Constructor_Defaults_Match_Cpp()
    {
        var p = new PathInfo(TravelMode.Drive, new Cost(1.0f, 1.0f), new GraphId(1, 0, 0), 0, 1.0f);

        // default restriction_idx == kInvalidRestriction
        Assert.Equal(GraphConstants.InvalidRestriction, p.RestrictionIndex);
        // default transition cost == {0,0}
        Assert.Equal(0.0f, p.TransitionCost.CostValue);
        Assert.Equal(0.0f, p.TransitionCost.Secs);
        Assert.False(p.StartNodeIsRecovered);
        Assert.False(p.IsShortcut);
        Assert.False(p.IsDisconnected);
    }
}
