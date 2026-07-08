// Faithful C# port of Valhalla's gtest suite test/graphid.cc (part of the "ids-constants" group).
// Source: F:/github/valhalla/test/graphid.cc
//
// Each [Fact] mirrors a TEST(GraphId, ...) / TEST(GraphIdGet, ...) case with the same inputs
// and expected values. EXPECT_EQ -> Assert.Equal; EXPECT_TRUE/FALSE -> Assert.True/False;
// EXPECT_THROW(..., logic_error) -> Assert.Throws<InvalidOperationException> (the C# port maps
// std::logic_error to InvalidOperationException); EXPECT_LT -> Assert.True(a < b).

using System;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class GraphIdTests
{
    [Fact]
    public void TestValues()
    {
        var target = new GraphId(123, 2, 8);
        Assert.Equal(123u, target.Tileid());
        Assert.Equal(2u, target.Level());
        Assert.Equal(8u, target.Id());

        var target2 = new GraphId(5689, 1, 1234567);
        Assert.Equal(5689u, target2.Tileid());
        Assert.Equal(1u, target2.Level());
        Assert.Equal(1234567u, target2.Id());

        // Test the tile_value
        Assert.Equal(target2.TileValue(), (uint)target2.TileBase().Value);

        target.SetId(5678);
        Assert.Equal(5678u, target.Id());
        Assert.Equal(123u, target.Tileid());
        Assert.Equal(2u, target.Level());
    }

    [Fact]
    public void TestInvalidValues()
    {
        Assert.Throws<InvalidOperationException>(
            () => new GraphId(111, GraphId.MaxGraphHierarchy + 1, 222));
        Assert.Throws<InvalidOperationException>(
            () => new GraphId(GraphConstants.MaxGraphTileId + 1, 0, 222));
        Assert.Throws<InvalidOperationException>(
            () => new GraphId(111, 1, GraphConstants.MaxGraphId + 1));
    }

    [Fact]
    public void TestCtorDefault()
    {
        var target = GraphId.Invalid;
        Assert.False(target.IsValid());
    }

    private static void TryCtorUintUintUint(uint tileid, uint level, uint id, GraphId expected)
    {
        var result = new GraphId(tileid, level, id);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TestCtorUintUintUint()
    {
        TryCtorUintUintUint(10, 2, 1, new GraphId(10, 2, 1));
        TryCtorUintUintUint(5, 1, 50, new GraphId(5, 1, 50));
    }

    private static void TryCtorCopy(GraphId gid, GraphId expected)
    {
        GraphId result = gid;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TestCtorCopy()
    {
        TryCtorCopy(new GraphId(10, 2, 1), new GraphId(10, 2, 1));
        TryCtorCopy(new GraphId(5, 1, 50), new GraphId(5, 1, 50));
    }

    private static void TryGetTileid(GraphId gid, uint expected) => Assert.Equal(expected, gid.Tileid());

    [Fact]
    public void TestGetTileid()
    {
        TryGetTileid(new GraphId(10, 2, 1), 10);
        TryGetTileid(new GraphId(5, 1, 50), 5);
    }

    private static void TryGetLevel(GraphId gid, uint expected) => Assert.Equal(expected, gid.Level());

    [Fact]
    public void TestGetLevel()
    {
        TryGetLevel(new GraphId(10, 2, 1), 2);
        TryGetLevel(new GraphId(5, 1, 50), 1);
    }

    private static void TryGetId(GraphId gid, uint expected) => Assert.Equal(expected, gid.Id());

    [Fact]
    public void TestGetId()
    {
        TryGetId(new GraphId(10, 2, 1), 1);
        TryGetId(new GraphId(5, 1, 50), 50);
    }

    [Fact]
    public void TestIsValid()
    {
        var id = new GraphId(1, 2, 3);
        Assert.True(id.IsValid());

        id = GraphId.Invalid;
        Assert.False(id.IsValid()); // Default constructor should never return valid graphid
    }

    private static void TryOpPostIncrement(ref GraphId gid, uint expected)
    {
        GraphId old = gid.PostIncrement();
        Assert.Equal(expected, gid.Id());
        Assert.Equal(expected - 1, old.Id());
    }

    [Fact]
    public void TestOpPostIncrement()
    {
        var graphid1 = new GraphId(10, 5, 0);
        TryOpPostIncrement(ref graphid1, 1);
        var graphid2 = new GraphId(10, 5, 1);
        TryOpPostIncrement(ref graphid2, 2);
        var graphid3 = new GraphId(5, 1, 50);
        TryOpPostIncrement(ref graphid3, 51);
    }

    [Fact]
    public void TestOpLessThan()
    {
        Assert.True(new GraphId(0, 0, 0) < new GraphId(0, 0, 1));
        Assert.True(new GraphId(10, 5, 1) < new GraphId(10, 6, 1));
        Assert.True(new GraphId(5, 1, 50) < new GraphId(6, 1, 50));
        Assert.True(new GraphId(111, 6, 333) < new GraphId(112, 7, 334));
    }

    private static void TryOpEqualTo(GraphId gid, GraphId expected)
    {
        Assert.Equal(expected, gid);
        Assert.Equal(gid, expected);
    }

    [Fact]
    public void TestOpEqualTo()
    {
        TryOpEqualTo(new GraphId(0, 0, 0), new GraphId(0, 0, 0));
        TryOpEqualTo(new GraphId(10, 5, 1), new GraphId(10, 5, 1));
        TryOpEqualTo(new GraphId(5, 1, 50), new GraphId(5, 1, 50));
    }
}
