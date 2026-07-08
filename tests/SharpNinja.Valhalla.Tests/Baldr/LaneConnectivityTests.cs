// Faithful C# port of Valhalla's gtest suite test/laneconnectivity.cc.
// EXPECT_THROW(..., std::out_of_range)    -> Assert.Throws<ArgumentOutOfRangeException>
// EXPECT_THROW(..., std::invalid_argument) -> Assert.Throws<ArgumentException> (exact type)
//   (in midgard's Util.ToInt, a non-numeric/empty token throws ArgumentException, matching
//    the std::invalid_argument the C++ midgard::to_int raises).

using System;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class LaneConnectivityTests
{
    private static void TryTestLaneConnectivity(string lanes)
    {
        Assert.Equal(lanes, new LaneConnectivityLanes(lanes).ToTextString());
    }

    [Fact]
    public void TestLaneConnectivity()
    {
        TryTestLaneConnectivity("1");
        TryTestLaneConnectivity("1|1");
        TryTestLaneConnectivity("1|2");
        TryTestLaneConnectivity("1|2|3|4|5|6|7|8|9|10|11|12|13|14|15");

        Assert.Throws<ArgumentOutOfRangeException>(() => new LaneConnectivityLanes("1|16").ToTextString());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LaneConnectivityLanes("1|1|1|1|1|1|1|1|1|1|1|1|1|1|1|1").ToTextString());
        Assert.Throws<ArgumentException>(() => new LaneConnectivityLanes("|1|").ToTextString());
        Assert.Throws<ArgumentException>(() => new LaneConnectivityLanes("|").ToTextString());
        Assert.Throws<ArgumentException>(() => new LaneConnectivityLanes("||").ToTextString());
    }

    [Fact]
    public void SizeOf()
    {
        Assert.Equal(24, Marshal.SizeOf<LaneConnectivity>());
    }
}
