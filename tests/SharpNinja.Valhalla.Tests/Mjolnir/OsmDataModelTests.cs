// Tests for the faithful C# port of the Valhalla mjolnir OSM data model.
// Sources: valhalla/mjolnir/osmway.h + osmway.cc, osmnode.h, osmaccess.h, osmrestriction.h.
//
// These mirror the C++ struct invariants exercised by the mjolnir gtests and the setters'
// clamping logic in osmway.cc (set_speed*, set_node_count, set_lanes*), the OSMNode fixed
// precision lat/lng encode/decode, the OSMAccess bit-field round trip, and the
// OSMRestriction comparison operators.

using System;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class OsmDataModelTests
{
    [Fact]
    public void OsmWay_DefaultsAreZeroed()
    {
        var way = new OSMWay(12345);
        Assert.Equal(12345UL, way.WayId());
        Assert.Equal(0u, way.NodeCount());
        Assert.False(way.AutoForward());
        Assert.False(way.Oneway());
        Assert.Equal(RoadClass.Motorway, way.RoadClassValue()); // 0
        Assert.Equal(Use.Road, way.UseValue());                 // 0
    }

    [Theory]
    [InlineData(50.0f, (byte)50)]
    [InlineData(50.4f, (byte)50)]   // +0.5 then truncate
    [InlineData(50.5f, (byte)51)]
    [InlineData(200.0f, (byte)140)] // clamped to kMaxOSMSpeed
    public void OsmWay_SetSpeed_RoundsAndClamps(float input, byte expected)
    {
        var way = new OSMWay();
        way.SetSpeed(input);
        Assert.Equal(expected, way.Speed());
    }

    [Fact]
    public void OsmWay_SetSpeedLimit_PreservesUnlimited()
    {
        var way = new OSMWay();
        way.SetSpeedLimit(byte.MaxValue); // kUnlimitedOSMSpeed
        Assert.Equal(byte.MaxValue, way.SpeedLimit());

        way.SetSpeedLimit(300f); // > max -> clamp to 140
        Assert.Equal((byte)140, way.SpeedLimit());
    }

    [Theory]
    [InlineData(3u, 3u)]
    [InlineData(15u, 15u)]
    [InlineData(20u, 15u)] // clamp to kMaxLaneCount
    public void OsmWay_SetLanes_Clamps(uint input, uint expected)
    {
        var way = new OSMWay();
        way.SetLanes(input);
        way.SetForwardLanes(input);
        way.SetBackwardLanes(input);
        Assert.Equal(expected, way.Lanes());
        Assert.Equal(expected, way.ForwardLanes());
        Assert.Equal(expected, way.BackwardLanes());
    }

    [Fact]
    public void OsmWay_SetNodeCount_ClampsTo65535()
    {
        var way = new OSMWay();
        way.SetNodeCount(70000);
        Assert.Equal(65535u, way.NodeCount());

        way.SetNodeCount(10);
        Assert.Equal(10u, way.NodeCount());
    }

    [Fact]
    public void OsmWay_AccessFlags_RoundTrip()
    {
        var way = new OSMWay();
        way.SetAutoForward(true);
        way.SetBikeBackward(true);
        way.SetTruckForward(true);

        Assert.True(way.AutoForward());
        Assert.True(way.BikeBackward());
        Assert.True(way.TruckForward());
        Assert.False(way.AutoBackward());
        Assert.False(way.BikeForward());
    }

    [Fact]
    public void OsmWay_EnumsRoundTrip()
    {
        var way = new OSMWay();
        way.SetRoadClass(RoadClass.Residential);
        way.SetUse(Use.Driveway);
        way.SetSurface(Surface.Gravel);
        way.SetCyclelaneRight(CycleLane.Separated);
        way.SetHovType(HovEdgeType.Hov3);

        Assert.Equal(RoadClass.Residential, way.RoadClassValue());
        Assert.Equal(Use.Driveway, way.UseValue());
        Assert.Equal(Surface.Gravel, way.SurfaceValue());
        Assert.Equal(CycleLane.Separated, way.CyclelaneRight());
        Assert.Equal(HovEdgeType.Hov3, way.HovType());
    }

    [Fact]
    public void OsmWay_Layer_Signed()
    {
        var way = new OSMWay();
        way.SetLayer(-2);
        Assert.Equal((sbyte)-2, way.Layer());
    }

    [Fact]
    public void OsmNode_LatLng_FixedPrecisionRoundTrip()
    {
        // A node at lat 39.3, lon -76.6 (Baltimore-ish).
        var node = new OSMNode(42, 39.3, -76.6);
        PointLL ll = node.LatLng();
        Assert.True(ll.IsValid());
        Assert.Equal(-76.6, ll.Lng, 6);
        Assert.Equal(39.3, ll.Lat, 6);
        Assert.Equal(42UL, node.Osmid);
    }

    [Fact]
    public void OsmNode_DefaultLatLng_IsInvalid()
    {
        // Constructed without lat/lng -> coordinates are "borked" -> invalid PointLL.
        var node = new OSMNode(7);
        Assert.False(node.LatLng().IsValid());
    }

    [Fact]
    public void OsmNode_NameIndices_RoundTripAndThrowOnOverflow()
    {
        var node = new OSMNode(1);
        node.SetNameIndex(5);
        node.SetRefIndex(9);
        node.SetExitToIndex(0);

        Assert.True(node.HasName());
        Assert.Equal(5u, node.NameIndex());
        Assert.True(node.HasRef());
        Assert.Equal(9u, node.RefIndex());
        Assert.False(node.HasExitTo());

        Assert.Throws<InvalidOperationException>(() => node.SetNameIndex(OSMNode.MaxNodeNameIndex + 1));
    }

    [Fact]
    public void OsmNode_ControlFlags_RoundTrip()
    {
        var node = new OSMNode(1);
        node.SetTrafficSignal(true);
        node.SetForwardStop(true);
        node.SetBackwardYield(true);
        node.SetType(NodeType.Gate);
        node.SetAccess(GraphConstants.AutoAccess | GraphConstants.BicycleAccess);

        Assert.True(node.TrafficSignal());
        Assert.True(node.ForwardStop());
        Assert.True(node.BackwardYield());
        Assert.Equal(NodeType.Gate, node.Type());
        Assert.Equal((uint)(GraphConstants.AutoAccess | GraphConstants.BicycleAccess), node.Access());
    }

    [Fact]
    public void OsmAccess_BitFieldRoundTrip()
    {
        var access = new OSMAccess(99);
        Assert.Equal(99UL, access.WayId());

        access.SetAutoTag(true);
        access.SetTruckTag(true);
        access.SetMotorcycleTag(true);

        Assert.True(access.AutoTag());
        Assert.True(access.TruckTag());
        Assert.True(access.MotorcycleTag());
        Assert.False(access.BikeTag());
        Assert.False(access.FootTag());

        // Bit layout: auto=bit0, truck=bit4, motorcycle=bit9.
        Assert.Equal((ushort)((1 << 0) | (1 << 4) | (1 << 9)), access.Attributes);
    }

    [Fact]
    public void OsmRestriction_Comparison_OrdersByFromThenTo()
    {
        var a = new OSMRestriction();
        a.SetFrom(10);
        a.SetTo(20);
        a.SetType(RestrictionType.NoLeftTurn);
        a.SetVia(2123388822);

        var b = new OSMRestriction();
        b.SetFrom(10);
        b.SetTo(30);
        b.SetType(RestrictionType.NoUTurn);
        b.SetVia(2123388822);

        Assert.True(a < b);          // same from, a.to < b.to
        Assert.False(a == b);
        Assert.Equal(RestrictionType.NoLeftTurn, a.TypeValue());
        Assert.Equal(2123388822UL, a.Via());
    }

    [Fact]
    public void OsmRestriction_Equality_IncludesViasAndModes()
    {
        var a = new OSMRestriction();
        a.SetFrom(1);
        a.SetTo(2);
        a.SetModes(GraphConstants.AutoAccess);
        a.SetVias(new ulong[] { 100, 200 });

        var b = new OSMRestriction();
        b.SetFrom(1);
        b.SetTo(2);
        b.SetModes(GraphConstants.AutoAccess);
        b.SetVias(new ulong[] { 100, 200 });

        Assert.True(a == b);

        b.SetVias(new ulong[] { 100, 201 });
        Assert.False(a == b);
        Assert.True(a < b || b < a);
    }

    [Fact]
    public void OsmAccessRestriction_RoundTrip()
    {
        var ar = new OSMAccessRestriction();
        ar.SetType(AccessType.MaxWeight);
        ar.SetValue(1000);
        ar.SetModes(GraphConstants.TruckAccess);
        ar.SetDirection(AccessRestrictionDirection.Forward);
        ar.SetExceptDestination(true);

        Assert.Equal(AccessType.MaxWeight, ar.TypeValue());
        Assert.Equal(1000UL, ar.Value());
        Assert.Equal((ushort)GraphConstants.TruckAccess, ar.Modes());
        Assert.Equal(AccessRestrictionDirection.Forward, ar.Direction());
        Assert.True(ar.ExceptDestination());
    }
}
