// Unit tests for the C# port of loki search.cc's stateless filter helpers (valhalla @ 3.7.0).
// These cover the anonymous-namespace helpers (heading_filter, layer_filter, flip_side, square, and
// the road-class portion of search_filter) which can be exercised without a graph. The full bin
// walk / snapping is covered by SearchMonacoTests against the real Monaco fixture.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Loki;

public class SearchFilterTests
{
    private static Location LocWithHeading(int? heading, int? tolerance)
        => new Location(new PointLL(0, 0)) { Heading = heading, HeadingTolerance = tolerance };

    [Fact]
    public void HeadingFilter_No_Heading_Filters_Nothing()
    {
        Location loc = LocWithHeading(null, null);
        Assert.False(Search.HeadingFilter(loc, 123.0f));
    }

    [Fact]
    public void HeadingFilter_Within_Tolerance_Does_Not_Filter()
    {
        Location loc = LocWithHeading(90, 30);
        Assert.False(Search.HeadingFilter(loc, 100.0f)); // 10 deg off, within 30
    }

    [Fact]
    public void HeadingFilter_Outside_Tolerance_Filters()
    {
        Location loc = LocWithHeading(90, 10);
        Assert.True(Search.HeadingFilter(loc, 150.0f)); // 60 deg off, beyond 10
    }

    [Fact]
    public void HeadingFilter_Wraps_Across_Zero()
    {
        // heading 10, angle 350 -> closest distance is 20 degrees (across 0), within tolerance 30.
        Location loc = LocWithHeading(10, 30);
        Assert.False(Search.HeadingFilter(loc, 350.0f));

        // tolerance 10 -> 20 > 10 -> filtered
        Location loc2 = LocWithHeading(10, 10);
        Assert.True(Search.HeadingFilter(loc2, 350.0f));
    }

    [Fact]
    public void LayerFilter_No_Preference_Filters_Nothing()
    {
        var loc = new Location(new PointLL(0, 0));
        Assert.False(Search.LayerFilter(loc, 3));
    }

    [Fact]
    public void LayerFilter_Mismatch_Filters()
    {
        var loc = new Location(new PointLL(0, 0)) { PreferredLayer = 1 };
        Assert.True(Search.LayerFilter(loc, 2));
        Assert.False(Search.LayerFilter(loc, 1));
    }

    [Fact]
    public void FlipSide_Swaps_Left_And_Right_But_Not_None()
    {
        Assert.Equal(Location.SideOfStreetType.Right, Search.FlipSide(Location.SideOfStreetType.Left));
        Assert.Equal(Location.SideOfStreetType.Left, Search.FlipSide(Location.SideOfStreetType.Right));
        Assert.Equal(Location.SideOfStreetType.None, Search.FlipSide(Location.SideOfStreetType.None));
    }

    [Fact]
    public void Square_Squares_The_Value()
    {
        Assert.Equal(9.0, Search.Square(3.0), 9);
        Assert.Equal(0.25, Search.Square(0.5), 9);
    }

    [Fact]
    public void DefaultSearchFilter_Accepts_All_Road_Classes()
    {
        // Default filter: min=service(7), max=motorway(0). The road-class clause rejects roads where
        // (rc > min || rc < max). With min=7,max=0 nothing is rejected by class.
        var filter = new SearchFilter();
        Assert.Equal(RoadClass.ServiceOther, filter.MinRoadClass);
        Assert.Equal(RoadClass.Motorway, filter.MaxRoadClass);
        Assert.Equal(GraphConstants.MaxLevel, filter.Level);
    }
}
