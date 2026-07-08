// Unit tests for the C# port of thor AStarHeuristic (valhalla @ 3.7.0).
// Valhalla ships no dedicated gtest for AStarHeuristic (it is exercised via the astar suite, which
// needs full tile fixtures + the A* algorithm not in this slice). These assert the foundation
// contract the A* algorithms rely on: Get(distance) == distance * factor, Get(ll) ==
// sqrt(distapprox.DistanceSquared(ll)) * factor, the distance-out overload agrees with GetDistance,
// and the heuristic is admissible (a factor that underestimates never overestimates).

using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Thor;

public class AStarHeuristicTests
{
    [Fact]
    public void GetDistance_Multiplied_By_Factor_Matches_Get()
    {
        var h = new AStarHeuristic();
        var dest = new PointLL(7.42, 43.73); // Monaco-ish
        h.Init(dest, 0.5f);

        var here = new PointLL(7.43, 43.74);
        float dist = h.GetDistance(here);
        Assert.Equal(dist * 0.5f, h.Get(here), 3);
    }

    [Fact]
    public void Get_With_Distance_Argument_Is_Distance_Times_Factor()
    {
        var h = new AStarHeuristic();
        h.Init(new PointLL(0.0, 0.0), 0.25f);
        Assert.Equal(2500.0f, h.Get(10000.0f), 3);
    }

    [Fact]
    public void Get_Out_Distance_Overload_Agrees_With_GetDistance()
    {
        var h = new AStarHeuristic();
        var dest = new PointLL(-122.4, 37.77);
        h.Init(dest, 1.0f);

        var here = new PointLL(-122.41, 37.78);
        float estimate = h.Get(here, out float dist);
        Assert.Equal(h.GetDistance(here), dist, 3);
        Assert.Equal(dist, estimate, 3); // factor == 1
    }

    [Fact]
    public void At_Destination_Heuristic_Is_Zero()
    {
        var h = new AStarHeuristic();
        var dest = new PointLL(7.42, 43.73);
        h.Init(dest, 0.7f);
        Assert.Equal(0.0f, h.Get(dest), 3);
    }

    [Fact]
    public void Underestimating_Factor_Keeps_Heuristic_Admissible()
    {
        // With a factor < the true cost-per-meter, the heuristic must not exceed the straight-line
        // distance (the loosest possible true cost). factor 0.5 < 1 => estimate <= distance.
        var h = new AStarHeuristic();
        var dest = new PointLL(7.42, 43.73);
        h.Init(dest, 0.5f);

        var here = new PointLL(7.50, 43.80);
        Assert.True(h.Get(here) <= h.GetDistance(here));
    }
}
