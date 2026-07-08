// Unit tests for the C# port of midgard closest_first_generator_t (valhalla @ 3.7.0).
// Valhalla exercises this only indirectly (via loki search). These assert the foundational
// contract loki relies on: tuples are yielded in non-decreasing distance order, the first tuple is
// the bin containing the seed (distance 0), and tile/bin indices are within range.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Loki;

public class ClosestFirstGeneratorTests
{
    [Fact]
    public void First_Tuple_Is_The_Seed_Bin_At_Distance_Zero()
    {
        Tiles<PointLL, double> tiles = TileHierarchy.Levels()[^1].Tiles;
        var gen = new ClosestFirstGenerator(tiles, new PointLL(7.42, 43.73));

        (int tileId, ushort bin, double distance) = gen.Next();
        Assert.Equal(0.0, distance, 6);
        Assert.True(tileId >= 0);
        Assert.True(bin < tiles.Nsubdivisions() * tiles.Nsubdivisions());
    }

    [Fact]
    public void Tuples_Are_Yielded_In_Non_Decreasing_Distance_Order()
    {
        Tiles<PointLL, double> tiles = TileHierarchy.Levels()[^1].Tiles;
        var gen = new ClosestFirstGenerator(tiles, new PointLL(-122.4194, 37.7749));

        double prev = double.MinValue;
        for (int i = 0; i < 50; i++)
        {
            (int _, ushort _, double distance) = gen.Next();
            Assert.True(distance >= prev, $"distance {distance} < prev {prev} at step {i}");
            prev = distance;
        }
    }

    [Fact]
    public void Yields_Distinct_Subdivisions()
    {
        Tiles<PointLL, double> tiles = TileHierarchy.Levels()[^1].Tiles;
        var gen = new ClosestFirstGenerator(tiles, new PointLL(7.42, 43.73));

        var seen = new System.Collections.Generic.HashSet<(int, ushort)>();
        for (int i = 0; i < 40; i++)
        {
            (int tileId, ushort bin, double _) = gen.Next();
            Assert.True(seen.Add((tileId, bin)), $"duplicate (tile {tileId}, bin {bin}) at step {i}");
        }
    }
}
