// Faithful C# port of Valhalla's gtest suite test/admin.cc.
// Each [Fact] mirrors a TEST(Admin, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal. sizeof(Admin) -> Marshal.SizeOf<Admin>().

using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class AdminTests
{
    // Expected size is 16 bytes. We want to alert if somehow any change grows
    // this structure size as that indicates incompatible tiles.
    private const int AdminExpectedSize = 16;

    [Fact]
    public void Size()
    {
        Assert.Equal(AdminExpectedSize, Marshal.SizeOf<Admin>());
    }

    [Fact]
    public void Create()
    {
        var ai = new Admin(5, 6, "US", "PA");
        Assert.Equal(5u, ai.CountryOffset);
        Assert.Equal(6u, ai.StateOffset);
        Assert.Equal("US", ai.CountryIsoCode());
        Assert.Equal("PA", ai.StateIsoCode());
    }

    [Fact]
    public void Create3CharStateIso()
    {
        var aiStateIso = new Admin(5, 6, "GB", "WLS");
        Assert.Equal("WLS", aiStateIso.StateIsoCode());
    }

    [Fact]
    public void EmptyStrings()
    {
        var aiEmptyStrings = new Admin(5, 6, string.Empty, string.Empty);
        Assert.Equal(string.Empty, aiEmptyStrings.CountryIsoCode());
        Assert.Equal(string.Empty, aiEmptyStrings.StateIsoCode());
    }
}
