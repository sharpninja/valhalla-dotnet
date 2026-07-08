// Faithful C# port of Valhalla's gtest suite test/sign.cc (the baldr::Sign cases).
// The odin::Sign cases (TestCtor, TestDescendingSortByConsecutiveCount_*) are NOT ported:
// they exercise valhalla::odin::Sign, which is excluded (deferred to the odin port).
//
// Added: a bit-layout round-trip test to prove the on-disk 8-byte tile blob parses
// identically (tile-fidelity requirement). EXPECT_EQ -> Assert.Equal.

using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class SignTests
{
    // Expected size is 8 bytes. We want to alert if somehow any change grows
    // this structure size as that indicates incompatible tiles.
    private const int SignExpectedSize = 8;

    [Fact]
    public void TestSizeof()
    {
        Assert.Equal(SignExpectedSize, Marshal.SizeOf<Sign>());
    }

    [Fact]
    public void TestCtorAndAccessors()
    {
        var sign = new Sign(idx: 12345u, type: Sign.Type.ExitToward, rnType: true, tagged: false, textOffset: 0xCAFEBABEu);

        Assert.Equal(12345u, sign.Index);
        Assert.Equal(Sign.Type.ExitToward, sign.GetSignType());
        Assert.True(sign.IsRouteNumType());
        Assert.False(sign.Tagged());
        Assert.Equal(0xCAFEBABEu, sign.TextOffset);
    }

    [Fact]
    public void TestSetIndex()
    {
        var sign = new Sign(0u, Sign.Type.ExitNumber, false, false, 0u);
        sign.Index = 0x3FFFFFu; // 22-bit max
        Assert.Equal(0x3FFFFFu, sign.Index);
        // setting index must not disturb the other bitfields
        Assert.Equal(Sign.Type.ExitNumber, sign.GetSignType());
        Assert.False(sign.IsRouteNumType());
        Assert.False(sign.Tagged());
    }

    [Fact]
    public void TestIndexFieldIsTwentyTwoBits()
    {
        // 22-bit field must wrap/mask values larger than 0x3FFFFF.
        var sign = new Sign(0xFFFFFFFFu, Sign.Type.ExitName, false, false, 0u);
        Assert.Equal(0x3FFFFFu, sign.Index);
    }

    [Fact]
    public void TestLinguisticTypeFitsEightBits()
    {
        var sign = new Sign(1u, Sign.Type.Linguistic, false, true, 7u);
        Assert.Equal(Sign.Type.Linguistic, sign.GetSignType());
        Assert.Equal((byte)255, (byte)sign.GetSignType());
        Assert.True(sign.Tagged());
    }

    [Fact]
    public void TestBitLayoutRoundTripFromTileBytes()
    {
        // Compose the exact 8-byte on-disk representation and verify the struct reads it back.
        //   word0 = index(22) | type<<22 (8) | rnType<<30 | tagged<<31
        const uint index = 0x123456u & 0x3FFFFFu; // 22-bit
        const uint type = (uint)Sign.Type.GuideBranch; // 4
        const uint rnType = 1u;
        const uint tagged = 1u;
        uint word0 = index | (type << 22) | (rnType << 30) | (tagged << 31);
        const uint textOffset = 0x0BADF00Du;

        Span<byte> blob = stackalloc byte[8];
        BitConverter.TryWriteBytes(blob[..4], word0);
        BitConverter.TryWriteBytes(blob.Slice(4, 4), textOffset);

        Sign sign = MemoryMarshal.Read<Sign>(blob);

        Assert.Equal(index, sign.Index);
        Assert.Equal(Sign.Type.GuideBranch, sign.GetSignType());
        Assert.True(sign.IsRouteNumType());
        Assert.True(sign.Tagged());
        Assert.Equal(textOffset, sign.TextOffset);
    }

    [Fact]
    public void TestCompareByIndexThenType()
    {
        var a = new Sign(5u, Sign.Type.ExitNumber, false, false, 0u);
        var b = new Sign(5u, Sign.Type.ExitBranch, false, false, 0u);
        var c = new Sign(6u, Sign.Type.ExitNumber, false, false, 0u);

        // Same index -> compare by type (ExitNumber=0 < ExitBranch=1)
        Assert.True(a < b);
        // Different index -> compare by index
        Assert.True(b < c);
        Assert.True(a < c);
    }
}
