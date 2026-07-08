// Faithful C# port of Valhalla's gtest suite test/turnlanes.cc.
// Ports the routing-relevant cases: test_sizeof, test_access, test_static_methods.
//
// PORT-NOTE: TEST(Turnlanes, validate_turn_lanes) is NOT ported. It loads odin pinpoint .pbf
// protobuf fixtures and runs valhalla::odin::DirectionsBuilder + EnhancedTripLeg, all of which
// belong to the excluded odin / proto / json modules. The TurnLanes structure itself
// (size/access/static string helpers) is fully covered below.

using System.Collections.Generic;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class TurnLanesTests
{
    // Expected size is 8 bytes. We alert if any change grows this structure size, as that
    // indicates incompatible tiles. Mirrors constexpr kTurnLanesExpectedSize = 8.
    private const int TurnLanesExpectedSize = 8;

    [Fact]
    public void TestSizeOf()
    {
        Assert.Equal(TurnLanesExpectedSize, Marshal.SizeOf<TurnLanes>());
    }

    [Fact]
    public void TestAccess()
    {
        var tl = new TurnLanes(32, 1234);
        Assert.Equal(32u, tl.EdgeIndex);
        Assert.Equal(1234u, tl.TextOffset);
    }

    [Fact]
    public void TestStaticMethods()
    {
        string osmTurnLanes = "left|through;right|";
        string valInternal = TurnLanes.GetTurnLaneString(osmTurnLanes);
        List<ushort> masks = TurnLanes.LaneMasks(valInternal);
        string valTurnLanes = TurnLanes.TurnLaneString(masks);
        Assert.Equal(osmTurnLanes, valTurnLanes);

        osmTurnLanes = "|through;right||none|slight_left|left";
        valInternal = TurnLanes.GetTurnLaneString(osmTurnLanes);
        masks = TurnLanes.LaneMasks(valInternal);
        valTurnLanes = TurnLanes.TurnLaneString(masks);
        Assert.Equal(osmTurnLanes, valTurnLanes);

        osmTurnLanes = "merge_to_left||reverse|merge_to_right";
        valInternal = TurnLanes.GetTurnLaneString(osmTurnLanes);
        masks = TurnLanes.LaneMasks(valInternal);
        valTurnLanes = TurnLanes.TurnLaneString(masks);
        Assert.Equal(osmTurnLanes, valTurnLanes);

        osmTurnLanes = "none||none||none|";
        valInternal = TurnLanes.GetTurnLaneString(osmTurnLanes);
        masks = TurnLanes.LaneMasks(valInternal);
        valTurnLanes = TurnLanes.TurnLaneString(masks);
        Assert.Equal(osmTurnLanes, valTurnLanes);

        // Test invalid values
        osmTurnLanes = "|blah||none||none|";
        valInternal = TurnLanes.GetTurnLaneString(osmTurnLanes);
        masks = TurnLanes.LaneMasks(valInternal);
        valTurnLanes = TurnLanes.TurnLaneString(masks);
        Assert.Equal("|||none||none|", valTurnLanes);

        osmTurnLanes = "blah|blah|";
        valInternal = TurnLanes.GetTurnLaneString(osmTurnLanes);
        masks = TurnLanes.LaneMasks(valInternal);
        valTurnLanes = TurnLanes.TurnLaneString(masks);
        Assert.Equal("||", valTurnLanes);
    }
}
