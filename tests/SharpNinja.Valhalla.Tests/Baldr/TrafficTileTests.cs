// Faithful C# port of Valhalla's gtest suite test/traffictile.cc to xUnit.
// Each [Fact] mirrors a TEST(Traffic, ...) case with the same inputs and expectations.
//   EXPECT_TRUE/FALSE -> Assert.True / Assert.False
//   EXPECT_EQ         -> Assert.Equal
//
// PORT-NOTE: the C++ TileConstruction test packs a TestTile struct (header + 3 speeds)
// with #pragma pack(push, 1) and reinterpret_casts its bytes into a TrafficTile. This port
// builds the equivalent byte buffer (32-byte header + 3 x 8-byte TrafficSpeed) and wraps it
// in a TrafficTile, exercising the exact same on-disk byte layout.

using System;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class TrafficTileTests
{
    private static byte[] BuildTile(TrafficTileHeader header, params TrafficSpeed[] speeds)
    {
        var buffer = new byte[TrafficTile.HeaderSize + (speeds.Length * TrafficTile.SpeedSize)];
        MemoryMarshal.Write(buffer.AsSpan(0, TrafficTile.HeaderSize), in header);
        for (int i = 0; i < speeds.Length; ++i)
        {
            ulong bits = speeds[i].RawBits;
            MemoryMarshal.Write(
                buffer.AsSpan(TrafficTile.HeaderSize + (i * TrafficTile.SpeedSize), TrafficTile.SpeedSize),
                in bits);
        }

        return buffer;
    }

    // TEST(Traffic, TileConstruction)
    [Fact]
    public void TileConstruction()
    {
        var header = new TrafficTileHeader
        {
            DirectedEdgeCount = 3,
            TrafficTileVersion = TrafficTileConstants.TrafficTileVersion,
        };

        var speed1 = default(TrafficSpeed);
        var speed2 = default(TrafficSpeed);
        var speed3 = default(TrafficSpeed);
        speed3.OverallEncodedSpeed = 98 >> 1;
        speed3.EncodedSpeed1 = 98 >> 1;
        speed3.EncodedSpeed2 = TrafficTileConstants.UnknownTrafficSpeedRaw;
        speed3.EncodedSpeed3 = TrafficTileConstants.UnknownTrafficSpeedRaw;
        speed3.Breakpoint1 = 255;

        byte[] buffer = BuildTile(header, speed1, speed2, speed3);
        var tile = new TrafficTile(buffer);

        TrafficSpeed speed = tile.TrafficSpeed(2);
        Assert.True(speed.SpeedValid());
        Assert.False(speed.Closed());
        Assert.Equal(98, speed.GetOverallSpeed());
        Assert.Equal(98, speed.GetSpeed(0));
        Assert.Equal((byte)(TrafficTileConstants.UnknownTrafficSpeedRaw << 1), speed.GetSpeed(1));

        // Verify the version
        Assert.Equal(3, TrafficTileConstants.TrafficTileVersion);

        // Test with an invalid version
        header.TrafficTileVersion = 78;
        byte[] invalidBuffer = BuildTile(header, speed1, speed2, speed3);
        var invalidTile = new TrafficTile(invalidBuffer);
        TrafficSpeed invalidSpeed = invalidTile.TrafficSpeed(2);
        Assert.False(invalidSpeed.SpeedValid());
    }

    // TEST(Traffic, NullTileConstruction)
    [Fact]
    public void NullTileConstruction()
    {
        var tile = new TrafficTile(); // Should not segfault

        TrafficSpeed speed = tile.TrafficSpeed(99);
        Assert.False(speed.SpeedValid());
        Assert.False(speed.Closed());
    }

    // TEST(Traffic, Closed)
    [Fact]
    public void ClosedTest()
    {
        var speed = default(TrafficSpeed);
        Assert.False(speed.Closed());

        speed.EncodedSpeed1 = 0;
        Assert.False(speed.Closed());
        Assert.False(speed.Closed(0));

        speed.Breakpoint1 = 255;
        Assert.True(speed.Closed());
        Assert.True(speed.Closed(0));

        speed.OverallEncodedSpeed = 0;
        Assert.True(speed.Closed());
        Assert.True(speed.Closed(0));
    }

    // TEST(Traffic, SpeedValid)
    [Fact]
    public void SpeedValidTest()
    {
        var speed = default(TrafficSpeed);
        speed.OverallEncodedSpeed = TrafficTileConstants.UnknownTrafficSpeedRaw;
        Assert.False(speed.SpeedValid());

        speed.EncodedSpeed1 = 1;
        Assert.False(speed.SpeedValid());
        Assert.False(speed.Closed());

        speed.EncodedSpeed1 = 0;
        speed.Congestion1 = 1;
        Assert.False(speed.SpeedValid());
        Assert.False(speed.Closed());

        speed.EncodedSpeed1 = 0;
        speed.Congestion1 = 4;
        Assert.False(speed.SpeedValid());
        Assert.False(speed.Closed());

        speed.EncodedSpeed1 = 0;
        speed.Breakpoint1 = 255;
        Assert.False(speed.SpeedValid());
        Assert.False(speed.Closed());

        speed.EncodedSpeed1 = 0;
        speed.Breakpoint1 = 255;
        speed.OverallEncodedSpeed = 0;
        Assert.True(speed.SpeedValid());
        Assert.True(speed.Closed());

        // Test wraparound: assigning UNKNOWN_TRAFFIC_SPEED_RAW + 1 to a 7-bit field yields 0.
        uint overflowValue = TrafficTileConstants.UnknownTrafficSpeedRaw + 1;
        speed.EncodedSpeed1 = overflowValue;
        Assert.Equal(0u, speed.EncodedSpeed1);
    }

    // Struct-size fidelity checks (mirror the C++ static_assert lines).
    [Fact]
    public void StructSizesMatchOnDiskLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<TrafficSpeed>());
        Assert.Equal(8 * 4, Marshal.SizeOf<TrafficTileHeader>());
        Assert.Equal(32, TrafficTile.HeaderSize);
        Assert.Equal(8, TrafficTile.SpeedSize);
    }
}
