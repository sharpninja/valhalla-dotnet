// Faithful C# port of Valhalla's gtest suite test/directededge.cc.
// Each [Fact] mirrors a TEST(DirectedEdge, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal; EXPECT_TRUE/FALSE -> Assert.True/False.
// The sizeof check uses Marshal.SizeOf / unsafe sizeof to verify the bit-packed tile layout
// remains exactly 48 bytes (matching kDirectedEdgeExpectedSize in the C++).

using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class DirectedEdgeTests
{
    // Expected size is 48 bytes. Since there are still "spare" bits we want to alert if somehow
    // any change grows this structure size.
    private const int DirectedEdgeExpectedSize = 48;

    [Fact]
    public void TestSizeof()
    {
        Assert.Equal(DirectedEdgeExpectedSize, Marshal.SizeOf<DirectedEdge>());
        Assert.Equal(DirectedEdgeExpectedSize, DirectedEdge.SizeOf);
    }

    [Fact]
    public void DirectedEdgeExtSizeofIsEight()
    {
        Assert.Equal(8, Marshal.SizeOf<DirectedEdgeExt>());
        Assert.Equal(8, DirectedEdgeExt.SizeOf);
    }

    [Fact]
    public void TestWriteRead()
    {
        // Test building a directed edge and reading back values.
        var directededge = DirectedEdge.Create();
        directededge.SetTurnType(0, Turn.Type.Straight);
        directededge.SetTurnType(1, Turn.Type.Left);
        directededge.SetTurnType(3, Turn.Type.Right);
        directededge.SetTurnType(2, Turn.Type.SharpRight);
        directededge.SetTurnType(5, Turn.Type.SharpLeft);

        Assert.Equal(Turn.Type.Straight, directededge.TurnType(0));
        Assert.Equal(Turn.Type.Right, directededge.TurnType(3));
        Assert.Equal(Turn.Type.SharpLeft, directededge.TurnType(5));
        Assert.Equal(Turn.Type.Left, directededge.TurnType(1));
        Assert.Equal(Turn.Type.SharpRight, directededge.TurnType(2));

        directededge.SetStopImpact(5, 7);
        directededge.SetStopImpact(1, 4);
        directededge.SetStopImpact(3, 0);

        Assert.Equal(0u, directededge.StopImpact(3));
        Assert.Equal(7u, directededge.StopImpact(5));
        Assert.Equal(4u, directededge.StopImpact(1));

        // name consistency should be false by default.
        Assert.False(directededge.NameConsistencyAt(2));

        directededge.SetNameConsistency(4, true);
        directededge.SetNameConsistency(1, false);
        directededge.SetNameConsistency(7, true);
        directededge.SetNameConsistency(6, true);

        Assert.True(directededge.NameConsistencyAt(4));
        Assert.False(directededge.NameConsistencyAt(1));
        Assert.True(directededge.NameConsistencyAt(7));
        Assert.True(directededge.NameConsistencyAt(6));

        // Overwrite idx 6 with false.
        directededge.SetNameConsistency(6, false);
        Assert.False(directededge.NameConsistencyAt(6));
    }

    [Fact]
    public void TestMaxSlope()
    {
        // Test setting max slope and reading back values.
        var edge = DirectedEdge.Create();

        edge.SetMaxUpSlope(5.0f);
        Assert.Equal(5, edge.MaxUpSlope());

        edge.SetMaxUpSlope(15.0f);
        Assert.Equal(15, edge.MaxUpSlope());

        edge.SetMaxUpSlope(-5.0f);
        Assert.Equal(0, edge.MaxUpSlope());

        edge.SetMaxUpSlope(25.0f);
        Assert.Equal(28, edge.MaxUpSlope());

        edge.SetMaxUpSlope(71.5f);
        Assert.Equal(72, edge.MaxUpSlope());

        edge.SetMaxUpSlope(88.0f);
        Assert.Equal(76, edge.MaxUpSlope());

        edge.SetMaxUpSlope(15.7f);
        Assert.Equal(16, edge.MaxUpSlope());

        edge.SetMaxDownSlope(-5.5f);
        Assert.Equal(-6, edge.MaxDownSlope());

        edge.SetMaxDownSlope(-15.0f);
        Assert.Equal(-15, edge.MaxDownSlope());

        edge.SetMaxDownSlope(5.0f);
        Assert.Equal(0, edge.MaxDownSlope());

        edge.SetMaxDownSlope(-25.0f);
        Assert.Equal(-28, edge.MaxDownSlope());

        edge.SetMaxDownSlope(-71.5f);
        Assert.Equal(-72, edge.MaxDownSlope());

        edge.SetMaxDownSlope(-88.0f);
        Assert.Equal(-76, edge.MaxDownSlope());

        edge.SetMaxDownSlope(-15.7f);
        Assert.Equal(-16, edge.MaxDownSlope());
    }

    // ---- Additional fidelity tests beyond the C++ suite, exercising the bit-packed layout and
    //      the accessor categories called out in the porting task (stop/yield/traffic signal,
    //      classification, use, forwardaccess, default constructor). These verify no bitfield
    //      overlap and that the default weighted_grade_ == 6 (set in the C++ ctor). ----

    [Fact]
    public void DefaultWeightedGradeIsSix()
    {
        var edge = DirectedEdge.Create();
        Assert.Equal(6u, edge.WeightedGrade);
    }

    [Fact]
    public void SignFlagsAreIndependent()
    {
        var edge = DirectedEdge.Create();

        edge.SetStopSign(true);
        edge.SetYieldSign(false);
        edge.SetTrafficSignal(true);

        Assert.True(edge.StopSign);
        Assert.False(edge.YieldSign);
        Assert.True(edge.TrafficSignal);

        edge.SetYieldSign(true);
        edge.SetStopSign(false);

        Assert.False(edge.StopSign);
        Assert.True(edge.YieldSign);
        Assert.True(edge.TrafficSignal);
    }

    [Fact]
    public void ClassificationUseAndAccessRoundTrip()
    {
        var edge = DirectedEdge.Create();

        edge.SetClassification(RoadClass.Tertiary);
        edge.SetUse(Use.RailFerry);
        edge.SetSurface(Surface.Gravel);
        edge.SetForwardAccess(GraphConstants.AutoAccess | GraphConstants.TruckAccess);
        edge.SetReverseAccess(GraphConstants.BicycleAccess);

        Assert.Equal(RoadClass.Tertiary, edge.Classification);
        Assert.Equal(Use.RailFerry, edge.Use);
        Assert.Equal(Surface.Gravel, edge.Surface);
        Assert.True(edge.Unpaved);
        Assert.Equal((uint)(GraphConstants.AutoAccess | GraphConstants.TruckAccess), edge.ForwardAccess);
        Assert.Equal((uint)GraphConstants.BicycleAccess, edge.ReverseAccess);
    }

    [Fact]
    public void EndNodeRoundTripPreservesOtherWord0Fields()
    {
        var edge = DirectedEdge.Create();

        var node = new GraphId(1234, 2, 56789);
        edge.SetEndNode(node);
        edge.SetRestrictions(0b1010_1010);
        edge.SetOppIndex(63);
        edge.SetForward(true);
        edge.SetLeavesTile(true);
        edge.SetCtryCrossing(true);

        Assert.Equal(node.Value, edge.EndNode.Value);
        Assert.Equal(0b1010_1010u, edge.Restrictions);
        Assert.Equal(63u, edge.OppIndex);
        Assert.True(edge.Forward);
        Assert.True(edge.LeavesTile);
        Assert.True(edge.CtryCrossing);
    }

    [Fact]
    public void StopImpactAndLineIdShareStorage()
    {
        // The C++ StopOrLine union shares the same 4 bytes between stopimpact/edge_to_right and lineid.
        var edge = DirectedEdge.Create();

        edge.SetLineId(0x00ABCDEF);
        Assert.Equal(0x00ABCDEFu, edge.LineId);

        // lineid low 24 bits = stopimpact view; high 8 bits = edge_to_right view.
        edge.SetEdgeToRight(0, true);
        edge.SetEdgeToRight(2, true);
        Assert.True(edge.EdgeToRight(0));
        Assert.False(edge.EdgeToRight(1));
        Assert.True(edge.EdgeToRight(2));
    }

    [Fact]
    public void ShortcutAndSupersededMasks()
    {
        var edge = DirectedEdge.Create();

        edge.SetShortcut(3);
        Assert.True(edge.IsShortcut);
        Assert.Equal(1u << 2, edge.Shortcut);

        edge.SetSuperseded(5);
        Assert.Equal(1u << 4, edge.Superseded);
        Assert.Equal(5u, edge.SupersededIdx());

        edge.SetSuperseded(0);
        Assert.Equal(0u, edge.Superseded);
        Assert.Equal(0u, edge.SupersededIdx());
    }

    [Fact]
    public void LaneCountClampingAndMinimum()
    {
        var edge = DirectedEdge.Create();

        edge.SetLaneCount(0);
        Assert.Equal(1u, edge.LaneCount);

        edge.SetLaneCount(99);
        Assert.Equal(GraphConstants.MaxLaneCount, edge.LaneCount);

        edge.SetLaneCount(3);
        Assert.Equal(3u, edge.LaneCount);
    }
}
