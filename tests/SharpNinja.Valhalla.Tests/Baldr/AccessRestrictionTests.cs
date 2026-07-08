// Faithful C# port of Valhalla's gtest suite test/access_restriction.cc.
//
// The C++ test is a single sizeof guard: AccessRestriction must be exactly 16 bytes so that an
// incompatible change that grows the struct (and would break tile parsing) is caught. We mirror
// that with Unsafe.SizeOf<AccessRestriction>() (the struct is two contiguous 64-bit words, no
// managed references, so its blittable size is its tile size). Additional round-trip getter/setter
// cases exercise the bit-packing accessors (accessrestriction.h / accessrestriction.cc).

using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class AccessRestrictionTests
{
    // Expected size is 16 bytes. We want to alert if somehow any change grows this structure size
    // as that indicates incompatible tiles.
    private const int AccessRestrictionExpectedSize = 16;

    [Fact]
    public void SizeofCheck()
        => Assert.Equal(AccessRestrictionExpectedSize, Unsafe.SizeOf<AccessRestriction>());

    [Fact]
    public void ConstructorAndGettersRoundTrip()
    {
        var ar = new AccessRestriction(
            edgeindex: 1234,
            type: AccessType.MaxWeight,
            modes: GraphConstants.TruckAccess | GraphConstants.AutoAccess,
            value: 36287,
            exceptDestination: true);

        Assert.Equal(1234u, ar.EdgeIndex());
        Assert.Equal(AccessType.MaxWeight, ar.Type());
        Assert.Equal((uint)(GraphConstants.TruckAccess | GraphConstants.AutoAccess), ar.Modes());
        Assert.Equal(36287UL, ar.Value());
        Assert.True(ar.ExceptDestination());
    }

    [Fact]
    public void SettersRoundTrip()
    {
        var ar = new AccessRestriction(0, AccessType.Hazmat, 0, 0, false);

        ar.SetEdgeIndex(4194303); // max 22-bit value
        Assert.Equal(4194303u, ar.EdgeIndex());

        ar.SetValue(0xFFFFFFFFFFFFFFFF);
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, ar.Value());

        ar.SetExceptDestination(true);
        Assert.True(ar.ExceptDestination());
        ar.SetExceptDestination(false);
        Assert.False(ar.ExceptDestination());
    }

    [Fact]
    public void FieldsDoNotOverlap()
    {
        // Set each packed field to its maximum and confirm the others read back unchanged.
        var ar = new AccessRestriction(
            edgeindex: 0x3FFFFF,          // 22 bits all set
            type: (AccessType)0x3F,       // 6 bits all set
            modes: 0xFFF,                 // 12 bits all set
            value: 0,
            exceptDestination: true);

        Assert.Equal(0x3FFFFFu, ar.EdgeIndex());
        Assert.Equal((AccessType)0x3F, ar.Type());
        Assert.Equal(0xFFFu, ar.Modes());
        Assert.True(ar.ExceptDestination());
    }

    [Fact]
    public void LessThan_SortsByEdgeThenModesThenTypeThenValue()
    {
        // operator< primary key: edge index.
        var a = new AccessRestriction(1, AccessType.Hazmat, 0, 0, false);
        var b = new AccessRestriction(2, AccessType.Hazmat, 0, 0, false);
        Assert.True(a < b);
        Assert.False(b < a);

        // same edge, different modes.
        var c = new AccessRestriction(1, AccessType.Hazmat, 1, 0, false);
        var d = new AccessRestriction(1, AccessType.Hazmat, 2, 0, false);
        Assert.True(c < d);

        // same edge + modes, different type.
        var e = new AccessRestriction(1, AccessType.Hazmat, 1, 0, false);
        var f = new AccessRestriction(1, AccessType.MaxHeight, 1, 0, false);
        Assert.True(e < f);

        // same edge + modes + type, different value.
        var g = new AccessRestriction(1, AccessType.Hazmat, 1, 10, false);
        var h = new AccessRestriction(1, AccessType.Hazmat, 1, 20, false);
        Assert.True(g < h);
    }
}
