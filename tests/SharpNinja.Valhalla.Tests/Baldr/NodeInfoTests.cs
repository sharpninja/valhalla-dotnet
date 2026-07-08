// Faithful C# port of Valhalla's gtest suite test/nodeinfo.cc.
// Each [Fact] mirrors a TEST(NodeInfo, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal (exact); EXPECT_NEAR(a,b,eps) -> Assert.Equal(expected, actual, tol);
// EXPECT_TRUE/FALSE -> Assert.True/False; EXPECT_LE(|x|, e) -> Assert.True(Math.Abs(x) <= e).
//
// The C++ "TEST(NodeInfo, Sizeof)" asserts sizeof(NodeInfo) == 32. In C# the equivalent of the
// on-disk struct size is Marshal.SizeOf<NodeInfo>(), which for the [StructLayout(Sequential,
// Pack=1)] struct of four ulong words is exactly 32 bytes.

using System;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class NodeInfoTests
{
    // Expected size is 32 bytes (kNodeInfoExpectedSize).
    private const int NodeInfoExpectedSize = 32;

    [Fact]
    public void Sizeof()
    {
        Assert.Equal(NodeInfoExpectedSize, Marshal.SizeOf<NodeInfo>());
    }

    [Fact]
    public void Ll()
    {
        const double kEpsilon = .00001;

        var baseLl = new PointLL(-70.0f, 40.0f);
        var n = default(NodeInfo);
        PointLL nodeLl = n.LatLng(baseLl);
        Assert.Equal(-70.0f, nodeLl.Lng, kEpsilon);
        Assert.Equal(40.0f, nodeLl.Lat, kEpsilon);

        var t = default(NodeInfo);
        var nodell0 = new PointLL(-69.5f, 40.25f);
        t.SetLatLng(baseLl, nodell0);
        nodeLl = t.LatLng(baseLl);
        Assert.Equal(nodell0.Lng, nodeLl.Lng, kEpsilon);
        Assert.Equal(nodell0.Lat, nodeLl.Lat, kEpsilon);

        // Test lon just outside tile bounds.
        var nodell1 = new PointLL(-70.000005f, 40.25f);
        t.SetLatLng(baseLl, nodell1);
        nodeLl = t.LatLng(baseLl);
        // NodeInfo ll should be -70.0, 40.25
        Assert.Equal(baseLl.Lng, nodeLl.Lng, kEpsilon);
        Assert.Equal(nodell1.Lat, nodeLl.Lat, kEpsilon);

        // Test lat just outside tile bounds.
        var nodell2 = new PointLL(-69.5f, 39.999995f);
        t.SetLatLng(baseLl, nodell2);
        nodeLl = t.LatLng(baseLl);
        // NodeInfo ll should be -69.5, 40.0
        Assert.Equal(nodell2.Lng, nodeLl.Lng, kEpsilon);
        Assert.Equal(baseLl.Lat, nodeLl.Lat, kEpsilon);
    }

    // Write to file and read into NodeInfo (build NodeInfo and read back values).
    [Fact]
    public void WriteRead()
    {
        var nodeinfo = default(NodeInfo);

        // Headings are reduced to 8 bits.
        nodeinfo.SetHeading(0, 266);
        nodeinfo.SetHeading(1, 90);
        nodeinfo.SetHeading(2, 32);
        nodeinfo.SetHeading(3, 180);
        nodeinfo.SetHeading(4, 185);
        nodeinfo.SetHeading(5, 270);
        nodeinfo.SetHeading(6, 145);
        nodeinfo.SetHeading(7, 0);

        Assert.Equal(266u, nodeinfo.Heading(0));
        Assert.Equal(90u, nodeinfo.Heading(1));
        Assert.Equal(32u, nodeinfo.Heading(2));
        Assert.Equal(180u, nodeinfo.Heading(3));
        Assert.Equal(184u, nodeinfo.Heading(4));
        Assert.Equal(270u, nodeinfo.Heading(5));
        Assert.Equal(145u, nodeinfo.Heading(6));
        Assert.Equal(0u, nodeinfo.Heading(7));

        nodeinfo.SetLocalDriveability(3, Traversability.Both);
        nodeinfo.SetLocalDriveability(5, Traversability.None);
        nodeinfo.SetLocalDriveability(7, Traversability.Forward);
        nodeinfo.SetLocalDriveability(1, Traversability.Backward);

        Assert.Equal(Traversability.Both, nodeinfo.LocalDriveability(3));
        Assert.Equal(Traversability.None, nodeinfo.LocalDriveability(5));
        Assert.Equal(Traversability.Forward, nodeinfo.LocalDriveability(7));
        Assert.Equal(Traversability.Backward, nodeinfo.LocalDriveability(1));
    }

    // Test elevation.
    [Fact]
    public void Elevation()
    {
        // Test elevation at 0.
        var node = default(NodeInfo);
        node.SetElevation(0);
        Assert.True(Math.Abs(node.Elevation()) <= 0.25f);

        // Elevation < -500 is set to -500.
        node.SetElevation(-700.0f);
        Assert.True(Math.Abs(node.Elevation() - -500.0f) <= 0.25f);

        node.SetElevation(700.0f);
        Assert.True(Math.Abs(node.Elevation() - 700.0f) <= 0.25f);

        node.SetElevation(1426.511963f);
        Assert.True(Math.Abs(node.Elevation() - 1426.511963f) <= 0.25f);

        // Highest road elevation is ~5600m, test at 6000m to be safe.
        node.SetElevation(6000.0f);
        Assert.True(Math.Abs(node.Elevation() - 6000.0f) <= 0.25f);
    }

    // ---- Additional coverage for the C# port (no direct gtest counterpart) ----
    // These guard the bit-packing fidelity of the remaining NodeInfo fields that nodeinfo.cc's
    // gtest suite does not exercise directly. They confirm fields round-trip and do not overlap.

    [Fact]
    public void FieldsRoundTripWithoutOverlap()
    {
        var n = default(NodeInfo);

        n.SetEdgeIndex(123456);              // 21-bit field
        n.SetEdgeCount(50);                  // 7-bit field (<= 127)
        n.SetAccess(GraphConstants.AllAccess);
        n.SetIntersection(IntersectionType.Fork);
        n.SetAdminIndex(2000);               // <= 4095
        n.SetTimezone(600);                  // <= 1023, exercises ext1 bit
        n.SetType(NodeType.TollGantry);
        n.SetDensity(9);
        n.SetTrafficSignal(true);
        n.SetModeChange(true);
        n.SetNamedIntersection(true);
        n.SetTransitionIndex(98765);         // 21-bit field
        n.SetTransitionCount(5);             // 3-bit field
        n.SetLocalEdgeCount(8);              // stored as 7, reported as 8
        n.SetDriveOnRight(true);
        n.SetTaggedAccess(true);
        n.SetPrivateAccess(true);
        n.SetCashOnlyToll(true);

        Assert.Equal(123456u, n.EdgeIndex);
        Assert.Equal(50u, n.EdgeCount);
        Assert.Equal(GraphConstants.AllAccess, n.Access);
        Assert.Equal(IntersectionType.Fork, n.Intersection);
        Assert.Equal(2000u, n.AdminIndex);
        Assert.Equal(600u, n.Timezone());
        Assert.Equal(NodeType.TollGantry, n.Type);
        Assert.Equal(9u, n.Density);
        Assert.True(n.TrafficSignal);
        Assert.True(n.ModeChange);
        Assert.True(n.NamedIntersection);
        Assert.Equal(98765u, n.TransitionIndex);
        Assert.Equal(5u, n.TransitionCount);
        Assert.Equal(8u, n.LocalEdgeCount);
        Assert.True(n.DriveOnRight);
        Assert.True(n.TaggedAccess);
        Assert.True(n.PrivateAccess);
        Assert.True(n.CashOnlyToll);
    }

    [Fact]
    public void AccessIsMaskedToAllAccess()
    {
        var n = default(NodeInfo);
        // Value exceeding kAllAccess (4095) is masked, not stored verbatim.
        n.SetAccess(0xFFFF);
        Assert.Equal(GraphConstants.AllAccess, n.Access);
    }

    [Fact]
    public void EdgeCountClampedToMax()
    {
        var n = default(NodeInfo);
        n.SetEdgeCount(500);
        Assert.Equal(NodeInfo.MaxEdgesPerNode, n.EdgeCount);
    }

    [Fact]
    public void DensityClampedToMax()
    {
        var n = default(NodeInfo);
        n.SetDensity(99);
        Assert.Equal(GraphConstants.MaxDensity, n.Density);
    }

    [Fact]
    public void EdgeIndexAboveMaxThrows()
    {
        var n = default(NodeInfo);
        Assert.Throws<InvalidOperationException>(() => n.SetEdgeIndex(GraphConstants.MaxGraphId + 1));
    }

    [Fact]
    public void TimezoneAboveMaxThrows()
    {
        var n = default(NodeInfo);
        Assert.Throws<InvalidOperationException>(() => n.SetTimezone(NodeInfo.MaxTimeZoneIdExt1 + 1));
    }

    [Fact]
    public void CanContractMatchesConditions()
    {
        var n = default(NodeInfo);
        n.SetEdgeCount(2);
        n.SetIntersection(IntersectionType.Regular);
        n.SetType(NodeType.StreetIntersection);
        Assert.True(n.CanContract());

        // A fork disqualifies contraction.
        n.SetIntersection(IntersectionType.Fork);
        Assert.False(n.CanContract());

        // A gate disqualifies contraction.
        n.SetIntersection(IntersectionType.Regular);
        n.SetType(NodeType.Gate);
        Assert.False(n.CanContract());

        // Fewer than 2 edges disqualifies contraction.
        n.SetType(NodeType.StreetIntersection);
        n.SetEdgeCount(1);
        Assert.False(n.CanContract());
    }

    [Fact]
    public void IsTransitOnlyForMultiUseTransitPlatform()
    {
        var n = default(NodeInfo);
        n.SetType(NodeType.StreetIntersection);
        Assert.False(n.IsTransit());
        n.SetType(NodeType.MultiUseTransitPlatform);
        Assert.True(n.IsTransit());
    }

    [Fact]
    public void HeadingOutOfRangeReturnsZero()
    {
        var n = default(NodeInfo);
        n.SetHeading(0, 123);
        // Indices above kMaxLocalEdgeIndex (7) return 0 and do not write.
        Assert.Equal(0u, n.Heading(8));
    }
}
