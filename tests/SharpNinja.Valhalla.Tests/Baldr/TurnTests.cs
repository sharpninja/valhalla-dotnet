// Faithful C# port of Valhalla's gtest suite test/turn.cc.
// Each assertion mirrors the TEST(Turn, TestGetType) case with identical inputs/expectations.
// EXPECT_EQ -> Assert.Equal.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class TurnTests
{
    [Fact]
    public void TestGetType()
    {
        // Straight lower bound
        Assert.Equal(Turn.Type.Straight, Turn.GetType(350));
        // Straight middle
        Assert.Equal(Turn.Type.Straight, Turn.GetType(0));
        Assert.Equal(Turn.Type.Straight, Turn.GetType(360));
        // Straight upper bound
        Assert.Equal(Turn.Type.Straight, Turn.GetType(10));

        // Slight right lower bound
        Assert.Equal(Turn.Type.SlightRight, Turn.GetType(11));
        // Slight right middle
        Assert.Equal(Turn.Type.SlightRight, Turn.GetType(28));
        // Slight right upper bound
        Assert.Equal(Turn.Type.SlightRight, Turn.GetType(44));

        // Right lower bound
        Assert.Equal(Turn.Type.Right, Turn.GetType(45));
        // Right middle
        Assert.Equal(Turn.Type.Right, Turn.GetType(90));
        Assert.Equal(Turn.Type.Right, Turn.GetType(450));
        Assert.Equal(Turn.Type.Right, Turn.GetType(810));
        // Right upper bound
        Assert.Equal(Turn.Type.Right, Turn.GetType(135));

        // Sharp right lower bound
        Assert.Equal(Turn.Type.SharpRight, Turn.GetType(136));
        // Sharp right middle
        Assert.Equal(Turn.Type.SharpRight, Turn.GetType(148));
        // Sharp right upper bound
        Assert.Equal(Turn.Type.SharpRight, Turn.GetType(159));

        // Reverse lower bound
        Assert.Equal(Turn.Type.Reverse, Turn.GetType(160));
        // Reverse middle
        Assert.Equal(Turn.Type.Reverse, Turn.GetType(180));
        // Reverse upper bound
        Assert.Equal(Turn.Type.Reverse, Turn.GetType(200));

        // Sharp left lower bound
        Assert.Equal(Turn.Type.SharpLeft, Turn.GetType(201));
        // Sharp left middle
        Assert.Equal(Turn.Type.SharpLeft, Turn.GetType(213));
        // Sharp left upper bound
        Assert.Equal(Turn.Type.SharpLeft, Turn.GetType(224));

        // Left lower bound
        Assert.Equal(Turn.Type.Left, Turn.GetType(225));
        // Left middle
        Assert.Equal(Turn.Type.Left, Turn.GetType(270));
        // Left upper bound
        Assert.Equal(Turn.Type.Left, Turn.GetType(315));

        // Slight left lower bound
        Assert.Equal(Turn.Type.SlightLeft, Turn.GetType(316));
        // Slight left middle
        Assert.Equal(Turn.Type.SlightLeft, Turn.GetType(333));
        // Slight left upper bound
        Assert.Equal(Turn.Type.SlightLeft, Turn.GetType(349));
    }
}
