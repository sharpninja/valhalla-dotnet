// Faithful C# port of Valhalla's gtest suite test/streetname.cc.
// Each [Fact] mirrors a TEST(Streetname, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal; EXPECT_TRUE -> Assert.True.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class StreetNameTests
{
    private static void TryCtor(string text, bool isRouteNumber)
    {
        var streetName = new StreetName(text, isRouteNumber);
        Assert.Equal(text, streetName.Value);
        Assert.Equal(isRouteNumber, streetName.IsRouteNumber);
    }

    [Fact]
    public void TestCtor()
    {
        TryCtor("Main Street", false);
        TryCtor("PA 743", true);
        TryCtor("US 220 Business", true);
        TryCtor("I 81 South", true);
    }

    private static void TryEquals(string text, bool isRouteNumber)
    {
        var lhs = new StreetName(text, isRouteNumber);
        var rhs = new StreetName(text, isRouteNumber);
        Assert.Equal(lhs, rhs);
    }

    [Fact]
    public void TestEquals()
    {
        TryEquals("Main Street", false);
        TryEquals("PA 743", true);
        TryEquals("US 220 Business", true);
        TryEquals("I 81 South", true);
        TryEquals("Mittelstraße", false);
    }

    private static void TryStartsWith(StreetName streetName, string prefix)
        => Assert.True(streetName.StartsWith(prefix), streetName.Value + ": Incorrect StartsWith");

    [Fact]
    public void TestStartsWith()
    {
        TryStartsWith(new StreetName("I 81 South", true), "I ");
        TryStartsWith(new StreetName("North Main Street", false), "North");
    }

    private static void TryEndsWith(StreetName streetName, string suffix)
        => Assert.True(streetName.EndsWith(suffix), streetName.Value + ": Incorrect EndsWith");

    [Fact]
    public void TestEndsWith()
    {
        TryEndsWith(new StreetName("I 81 South", true), "South");
        TryEndsWith(new StreetName("Main Street", false), "Street");
    }

    private static void TryGetPreDir(StreetName streetName, string preDir)
        => Assert.Equal(preDir, streetName.GetPreDir());

    [Fact]
    public void TestGetPreDir()
    {
        TryGetPreDir(new StreetName("North Main Street", false), string.Empty);
        TryGetPreDir(new StreetName("Main Street", false), string.Empty);
    }

    private static void TryGetPostDir(StreetName streetName, string postDir)
        => Assert.Equal(postDir, streetName.GetPostDir());

    [Fact]
    public void TestGetPostDir()
    {
        TryGetPostDir(new StreetName("I 81 South", true), string.Empty);
        TryGetPostDir(new StreetName("Main Street", true), string.Empty);
    }

    private static void TryGetPostCardinalDir(StreetName streetName, string postDir)
        => Assert.Equal(postDir, streetName.GetPostCardinalDir());

    [Fact]
    public void TestGetPostCardinalDir()
    {
        TryGetPostCardinalDir(new StreetName("US 220 North", true), string.Empty);
        TryGetPostCardinalDir(new StreetName("Main Street", false), string.Empty);
    }

    private static void TryGetBaseName(StreetName streetName, string baseName)
        => Assert.Equal(baseName, streetName.GetBaseName());

    [Fact]
    public void TestGetBaseName()
    {
        TryGetBaseName(new StreetName("North Main Street", false), "North Main Street");
        TryGetBaseName(new StreetName("Main Street", false), "Main Street");
        TryGetBaseName(new StreetName("Broadway", false), "Broadway");
        TryGetBaseName(new StreetName(string.Empty, false), string.Empty);
    }

    private static void TryHasSameBaseName(StreetName streetName, StreetName rhs)
        => Assert.True(streetName.HasSameBaseName(rhs), streetName.Value + ": Incorrect HasSameBaseName");

    [Fact]
    public void TestHasSameBaseName()
    {
        TryHasSameBaseName(new StreetName("North Main Street", false), new StreetName("North Main Street", false));
        TryHasSameBaseName(new StreetName("I 81 South", true), new StreetName("I 81 South", true));
        TryHasSameBaseName(new StreetName("PA 283 West", true), new StreetName("PA 283 West", true));
        TryHasSameBaseName(
            new StreetName("Constitution Avenue Northeast", false),
            new StreetName("Constitution Avenue Northeast", false));
        TryHasSameBaseName(new StreetName("Main Street", false), new StreetName("Main Street", false));
        TryHasSameBaseName(new StreetName("Broadway", false), new StreetName("Broadway", false));
        TryHasSameBaseName(new StreetName("Mittelstraße", false), new StreetName("Mittelstraße", false));
        TryHasSameBaseName(new StreetName(string.Empty, false), new StreetName(string.Empty, false));
    }
}
