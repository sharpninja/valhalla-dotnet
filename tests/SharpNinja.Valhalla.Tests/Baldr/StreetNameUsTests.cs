// Faithful C# port of Valhalla's gtest suite test/streetname_us.cc.
// Each [Fact] mirrors a TEST(StreetnameUs, ...) case with the same inputs and expected values.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class StreetNameUsTests
{
    private static void TryCtor(string text, bool isRouteNumber)
    {
        var streetName = new StreetNameUs(text, isRouteNumber);
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
        var lhs = new StreetNameUs(text, isRouteNumber);
        var rhs = new StreetNameUs(text, isRouteNumber);
        Assert.Equal(lhs, rhs);
    }

    [Fact]
    public void TestEquals()
    {
        TryEquals("Main Street", false);
        TryEquals("PA 743", true);
        TryEquals("US 220 Business", true);
        TryEquals("I 81 South", true);
    }

    private static void TryStartsWith(StreetNameUs streetName, string prefix)
        => Assert.True(streetName.StartsWith(prefix), streetName.Value + " : " + prefix);

    [Fact]
    public void TestStartsWith()
    {
        TryStartsWith(new StreetNameUs("I 81 South", true), "I ");
        TryStartsWith(new StreetNameUs("North Main Street", false), "North");
    }

    private static void TryEndsWith(StreetNameUs streetName, string suffix)
        => Assert.True(streetName.EndsWith(suffix), streetName.Value + " : " + suffix);

    [Fact]
    public void TestEndsWith()
    {
        TryEndsWith(new StreetNameUs("I 81 South", true), "South");
        TryEndsWith(new StreetNameUs("Main Street", false), "Street");
    }

    private static void TryGetPreDir(StreetNameUs streetName, string preDir)
        => Assert.Equal(preDir, streetName.GetPreDir());

    [Fact]
    public void TestGetPreDir()
    {
        TryGetPreDir(new StreetNameUs("North Main Street", false), "North ");
        TryGetPreDir(new StreetNameUs("East Chestnut Avenue", false), "East ");
        TryGetPreDir(new StreetNameUs("South Main Street", false), "South ");
        TryGetPreDir(new StreetNameUs("West 26th Street", false), "West ");
        TryGetPreDir(new StreetNameUs("Main Street", false), string.Empty);
    }

    private static void TryGetPostDir(StreetNameUs streetName, string postDir)
        => Assert.Equal(postDir, streetName.GetPostDir());

    [Fact]
    public void TestGetPostDir()
    {
        TryGetPostDir(new StreetNameUs("US 220 North", true), " North");
        TryGetPostDir(new StreetNameUs("US 22 East", true), " East");
        TryGetPostDir(new StreetNameUs("I 81 South", true), " South");
        TryGetPostDir(new StreetNameUs("PA 283 West", true), " West");
        TryGetPostDir(new StreetNameUs("Constitution Avenue Northeast", false), " Northeast");
        TryGetPostDir(new StreetNameUs("Constitution Avenue Northwest", false), " Northwest");
        TryGetPostDir(new StreetNameUs("Independence Avenue Southeast", false), " Southeast");
        TryGetPostDir(new StreetNameUs("Independence Avenue Southwest", false), " Southwest");
        TryGetPostDir(new StreetNameUs("Main Street", false), string.Empty);
    }

    private static void TryGetPostCardinalDir(StreetNameUs streetName, string postDir)
        => Assert.Equal(postDir, streetName.GetPostCardinalDir());

    [Fact]
    public void TestGetPostCardinalDir()
    {
        TryGetPostCardinalDir(new StreetNameUs("US 220 North", true), " North");
        TryGetPostCardinalDir(new StreetNameUs("US 22 East", true), " East");
        TryGetPostCardinalDir(new StreetNameUs("I 81 South", true), " South");
        TryGetPostCardinalDir(new StreetNameUs("PA 283 West", true), " West");
        TryGetPostCardinalDir(new StreetNameUs("Main Street", false), string.Empty);
    }

    private static void TryGetBaseName(StreetNameUs streetName, string baseName)
        => Assert.Equal(baseName, streetName.GetBaseName());

    [Fact]
    public void TestGetBaseName()
    {
        TryGetBaseName(new StreetNameUs("North Main Street", false), "Main Street");
        TryGetBaseName(new StreetNameUs("East Chestnut Avenue", false), "Chestnut Avenue");
        TryGetBaseName(new StreetNameUs("South Main Street", false), "Main Street");
        TryGetBaseName(new StreetNameUs("West 26th Street", false), "26th Street");
        TryGetBaseName(new StreetNameUs("US 220 North", true), "US 220");
        TryGetBaseName(new StreetNameUs("US 22 East", true), "US 22");
        TryGetBaseName(new StreetNameUs("I 81 South", true), "I 81");
        TryGetBaseName(new StreetNameUs("PA 283 West", true), "PA 283");
        TryGetBaseName(new StreetNameUs("Constitution Avenue Northeast", false), "Constitution Avenue");
        TryGetBaseName(new StreetNameUs("Constitution Avenue Northwest", false), "Constitution Avenue");
        TryGetBaseName(new StreetNameUs("Independence Avenue Southeast", false), "Independence Avenue");
        TryGetBaseName(new StreetNameUs("Independence Avenue Southwest", false), "Independence Avenue");
        TryGetBaseName(new StreetNameUs("North South Street Northwest", false), "South Street");
        TryGetBaseName(new StreetNameUs("East North Avenue Southwest", false), "North Avenue");
        TryGetBaseName(new StreetNameUs("Main Street", false), "Main Street");
        TryGetBaseName(new StreetNameUs("Broadway", false), "Broadway");
        TryGetBaseName(new StreetNameUs(string.Empty, false), string.Empty);
    }

    private static void TryHasSameBaseName(StreetNameUs streetName, StreetNameUs rhs)
        => Assert.True(streetName.HasSameBaseName(rhs), streetName.Value + ": Incorrect HasSameBaseName");

    [Fact]
    public void TestHasSameBaseName()
    {
        TryHasSameBaseName(new StreetNameUs("North Main Street", false), new StreetNameUs("Main Street", false));
        TryHasSameBaseName(new StreetNameUs("East Chestnut Avenue", false), new StreetNameUs("Chestnut Avenue", false));
        TryHasSameBaseName(new StreetNameUs("South Main Street", false), new StreetNameUs("Main Street", false));
        TryHasSameBaseName(new StreetNameUs("West 26th Street", false), new StreetNameUs("East 26th Street", false));
        TryHasSameBaseName(new StreetNameUs("I 695 West", true), new StreetNameUs("I 695 South", true));
        TryHasSameBaseName(new StreetNameUs("US 220 North", true), new StreetNameUs("US 220", true));
        TryHasSameBaseName(new StreetNameUs("US 22 East", true), new StreetNameUs("US 22", true));
        TryHasSameBaseName(new StreetNameUs("I 81 South", true), new StreetNameUs("I 81", true));
        TryHasSameBaseName(new StreetNameUs("PA 283 West", true), new StreetNameUs("PA 283", true));
        TryHasSameBaseName(
            new StreetNameUs("Constitution Avenue Northeast", false),
            new StreetNameUs("Constitution Avenue", false));
        TryHasSameBaseName(
            new StreetNameUs("Constitution Avenue Northwest", false),
            new StreetNameUs("Constitution Avenue", false));
        TryHasSameBaseName(
            new StreetNameUs("Constitution Avenue Northwest", false),
            new StreetNameUs("Constitution Avenue Northeast", false));
        TryHasSameBaseName(
            new StreetNameUs("Independence Avenue Southeast", false),
            new StreetNameUs("Independence Avenue", false));
        TryHasSameBaseName(
            new StreetNameUs("Independence Avenue Southwest", false),
            new StreetNameUs("Independence Avenue", false));
        TryHasSameBaseName(
            new StreetNameUs("Independence Avenue Southwest", false),
            new StreetNameUs("Independence Avenue Southeast", false));
        TryHasSameBaseName(new StreetNameUs("Main Street", false), new StreetNameUs("Main Street", false));
        TryHasSameBaseName(new StreetNameUs("Broadway", false), new StreetNameUs("Broadway", false));
        TryHasSameBaseName(new StreetNameUs(string.Empty, false), new StreetNameUs(string.Empty, false));
    }
}
