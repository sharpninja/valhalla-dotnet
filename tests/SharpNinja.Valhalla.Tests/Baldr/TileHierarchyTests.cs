// Faithful C# port of Valhalla's gtest suite test/tilehierarchy.cc.
//
// Covers the static level table (Parse), the bbox-to-tile queries (Tiles), and the parent-tile
// computation (parent).

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

using Xunit;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class TileHierarchyTests
{
    [Fact]
    public void Parse()
    {
        Assert.Equal(3, TileHierarchy.Levels().Count);
        Assert.Equal("arterial", TileHierarchy.Levels()[1].Name);
        Assert.Equal(0, TileHierarchy.Levels()[0].Level);
        Assert.Equal(0.25f, TileHierarchy.Levels()[^1].Tiles.TileSize());
        Assert.Equal(0, TileHierarchy.Levels()[0].Level);
        Assert.Equal(1, TileHierarchy.Levels()[1].Level);
        Assert.Equal(2, TileHierarchy.Levels()[2].Level);

        GraphId id = TileHierarchy.GetGraphId(new PointLL(0, 0), 34);
        Assert.False(id.IsValid());

        // there are 1440 cols and 720 rows, this spot lands on col 414 and row 522
        id = TileHierarchy.GetGraphId(new PointLL(-76.5, 40.5), 2);
        Assert.Equal(2u, id.Level());
        Assert.Equal((uint)((522 * 1440) + 414), id.Tileid());
        Assert.Equal(0u, id.Id());

        Assert.Equal(RoadClass.Primary, TileHierarchy.Levels()[0].Importance);
        Assert.Equal(RoadClass.Tertiary, TileHierarchy.Levels()[1].Importance);
        Assert.Equal(RoadClass.ServiceOther, TileHierarchy.Levels()[^1].Importance);
    }

    [Fact]
    public void Tiles()
    {
        // there are 1440 cols and 720 rows, this spot lands on col 414 and row 522
        var bbox = new Aabb2T<double>(new PointXY<double>(-76.49, 40.51), new PointXY<double>(-76.48, 40.52));
        var ids = TileHierarchy.GetGraphIds(bbox, 2);
        Assert.Single(ids);

        GraphId id = ids[0];
        Assert.Equal(2u, id.Level());
        Assert.Equal((uint)((522 * 1440) + 414), id.Tileid());
        Assert.Equal(0u, id.Id());

        bbox = new Aabb2T<double>(new PointXY<double>(-76.51, 40.49), new PointXY<double>(-76.49, 40.51));
        ids = TileHierarchy.GetGraphIds(bbox, 2);
        Assert.Equal(4, ids.Count);
    }

    [Fact]
    public void Parent()
    {
        var id = new GraphId((uint)((1440 * 16) + 16), 3, 0);
        GraphId level2 = TileHierarchy.Parent(id);
        Assert.Equal(new GraphId(id.Tileid(), 2, 0), level2);

        GraphId level1 = TileHierarchy.Parent(level2);
        Assert.Equal(new GraphId((uint)((360 * 4) + 4), 1, 0), level1);

        GraphId level0 = TileHierarchy.Parent(level1);
        Assert.Equal(new GraphId((uint)((90 * 1) + 1), 0, 0), level0);

        GraphId invalid = TileHierarchy.Parent(level0);
        Assert.Equal(new GraphId(GraphId.InvalidGraphId), invalid);
    }
}
