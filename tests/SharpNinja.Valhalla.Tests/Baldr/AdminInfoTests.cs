// Coverage for the baldr AdminInfo transfer class and the get_iso_3166_1_alpha3 free
// function. No standalone gtest exists for these in baldr; these tests verify the ported
// constructor/accessor, operator==, and the AdminInfoHasher / alpha2->alpha3 map.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class AdminInfoTests
{
    [Fact]
    public void ConstructorStoresAllFields()
    {
        var info = new AdminInfo("United States", "Pennsylvania", "US", "PA");
        Assert.Equal("United States", info.CountryText);
        Assert.Equal("Pennsylvania", info.StateText);
        Assert.Equal("US", info.CountryIso);
        Assert.Equal("PA", info.StateIso);
    }

    [Fact]
    public void EqualityComparesAllFields()
    {
        var a = new AdminInfo("United States", "Pennsylvania", "US", "PA");
        var b = new AdminInfo("United States", "Pennsylvania", "US", "PA");
        var c = new AdminInfo("United States", "Maryland", "US", "MD");

        Assert.True(a == b);
        Assert.True(a.Equals(b));
        Assert.False(a == c);
        Assert.True(a != c);
    }

    [Fact]
    public void HasherUsesConcatenatedFields()
    {
        var hasher = new AdminInfo.AdminInfoHasher();
        var a = new AdminInfo("United States", "Pennsylvania", "US", "PA");
        var b = new AdminInfo("United States", "Pennsylvania", "US", "PA");

        Assert.Equal(hasher.GetHashCode(a), hasher.GetHashCode(b));

        // Usable as a dictionary key comparer.
        var set = new HashSet<AdminInfo>(hasher) { a };
        Assert.Contains(b, set);
    }

    [Theory]
    [InlineData("US", "USA")]
    [InlineData("GB", "GBR")]
    [InlineData("DE", "DEU")]
    [InlineData("XK", "XKX")]
    [InlineData("CS", "SCG")]
    [InlineData("AN", "ANT")]
    public void GetIso31661Alpha3MapsKnownCodes(string alpha2, string alpha3)
    {
        Assert.Equal(alpha3, AdminConverter.GetIso31661Alpha3(alpha2));
    }

    [Fact]
    public void GetIso31661Alpha3ReturnsEmptyForUnknown()
    {
        Assert.Equal(string.Empty, AdminConverter.GetIso31661Alpha3("ZZ"));
        Assert.Equal(string.Empty, AdminConverter.GetIso31661Alpha3(string.Empty));
    }
}
