// Faithful C# port of Valhalla's gtest suite test/streetnames_us.cc.
// Each [Fact] mirrors a TEST(StreetnamesUs, ...) case with the same inputs and expected values.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class StreetNamesUsTests
{
    private static StreetNamesUs Names(params (string Name, bool IsRouteNumber)[] names)
        => new StreetNamesUs(names);

    private static void TryListCtor(IReadOnlyList<(string Name, bool IsRouteNumber)> names)
    {
        var streetNames = new StreetNamesUs(names);

        int x = 0;
        foreach (StreetName streetName in streetNames)
        {
            Assert.Equal(names[x].Name, streetName.Value);
            Assert.Equal(names[x].IsRouteNumber, streetName.IsRouteNumber);
            ++x;
        }
    }

    [Fact]
    public void TestListCtor()
    {
        TryListCtor(new[] { ("Main Street", false) });
        TryListCtor(new[] { ("Hershey Road", false), ("PA 743 North", true) });
    }

    private static void TryFindCommonStreetNames(StreetNamesUs lhs, StreetNamesUs rhs, StreetNamesUs expected)
    {
        StreetNames computed = lhs.FindCommonStreetNames(rhs);
        Assert.Equal(expected.ToStringDelimited(), computed.ToStringDelimited());
    }

    [Fact]
    public void TestFindCommonStreetNames()
    {
        TryFindCommonStreetNames(
            Names(("Hershey Road", false), ("PA 743 North", true)),
            Names(("Fishburn Road", false), ("PA 743 North", true)),
            Names(("PA 743 North", true)));

        TryFindCommonStreetNames(
            Names(("Hershey Road", false), ("PA 743 North", true)),
            Names(("Fishburn Road", false), ("PA 743", true)),
            Names());

        TryFindCommonStreetNames(
            Names(("Capital Beltway", false), ("I 95 South", true), ("I 495 South", true)),
            Names(("I 95 South", true)),
            Names(("I 95 South", true)));
    }

    private static void TryFindCommonBaseNames(StreetNamesUs lhs, StreetNamesUs rhs, StreetNamesUs expected)
    {
        StreetNames computed = lhs.FindCommonBaseNames(rhs);
        Assert.Equal(expected.ToStringDelimited(), computed.ToStringDelimited());
    }

    [Fact]
    public void TestFindCommonBaseNames()
    {
        TryFindCommonBaseNames(
            Names(("Hershey Road", false), ("PA 743 North", true)),
            Names(("Fishburn Road", false), ("PA 743 North", true)),
            Names(("PA 743 North", true)));

        TryFindCommonBaseNames(
            Names(("Hershey Road", false), ("PA 743 North", true)),
            Names(("Fishburn Road", false), ("PA 743", true)),
            Names(("PA 743 North", true)));

        TryFindCommonBaseNames(
            Names(("Hershey Road", false), ("PA 743", true)),
            Names(("Fishburn Road", false), ("PA 743 North", true)),
            Names(("PA 743 North", true)));

        TryFindCommonBaseNames(
            Names(("Hershey Road", false), ("PA 743", true)),
            Names(("Fishburn Road", false), ("PA 743", true)),
            Names(("PA 743", true)));

        TryFindCommonBaseNames(
            Names(("Capital Beltway", false), ("I 95 South", true), ("I 495 South", true)),
            Names(("I 95 South", true)),
            Names(("I 95 South", true)));
    }

    private static void TryGetRouteNumbers(StreetNamesUs streetNames, StreetNamesUs expected)
    {
        StreetNames computed = streetNames.GetRouteNumbers();
        Assert.Equal(expected.ToStringDelimited(), computed.ToStringDelimited());
    }

    [Fact]
    public void TestGetRouteNumbers()
    {
        TryGetRouteNumbers(
            Names(("Hershey Road", false), ("PA 743 North", true)),
            Names(("PA 743 North", true)));

        TryGetRouteNumbers(
            Names(("Unter den Linden", false), ("B 2", true), ("B 5", true)),
            Names(("B 2", true), ("B 5", true)));

        TryGetRouteNumbers(Names(("I 95 South", true)), Names(("I 95 South", true)));

        TryGetRouteNumbers(Names(("Sheridan Circle", false)), Names());

        TryGetRouteNumbers(
            Names(("Capital Beltway", false), ("I 95 South", true), ("I 495 South", true)),
            Names(("I 95 South", true), ("I 495 South", true)));
    }

    private static void TryGetNonRouteNumbers(StreetNamesUs streetNames, StreetNamesUs expected)
    {
        StreetNames computed = streetNames.GetNonRouteNumbers();
        Assert.Equal(expected.ToStringDelimited(), computed.ToStringDelimited());
    }

    [Fact]
    public void TestGetNonRouteNumbers()
    {
        TryGetNonRouteNumbers(
            Names(("Hershey Road", false), ("PA 743 North", true)),
            Names(("Hershey Road", false)));

        TryGetNonRouteNumbers(
            Names(("Unter den Linden", false), ("B 2", true), ("B 5", true)),
            Names(("Unter den Linden", false)));

        TryGetNonRouteNumbers(Names(("I 95 South", true)), Names());

        TryGetNonRouteNumbers(Names(("Sheridan Circle", false)), Names(("Sheridan Circle", false)));

        TryGetNonRouteNumbers(
            Names(("Capital Beltway", false), ("I 95 South", true), ("I 495 South", true)),
            Names(("Capital Beltway", false)));
    }
}
