// Faithful C# port of Valhalla's gtest suite test/streetnames.cc.
// Each [Fact] mirrors a TEST(Streetnames, ...) case with the same inputs and expected values.
// C++ ToString() (default delim) maps to the ported ToStringDelimited().

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class StreetNamesTests
{
    private static StreetNames Names(params (string Name, bool IsRouteNumber)[] names)
        => new StreetNames(names);

    private static void TryListCtor(IReadOnlyList<(string Name, bool IsRouteNumber)> names)
    {
        var streetNames = new StreetNames(names);

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
        TryListCtor(new[] { ("Unter den Linden", false), ("B 2", true), ("B 5", true) });
    }

    private static void TryFindCommonStreetNames(StreetNames lhs, StreetNames rhs, StreetNames expected)
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

        TryFindCommonStreetNames(
            Names(("Unter den Linden", false), ("B 2", true), ("B 5", true)),
            Names(("B 2", true), ("B 5", true)),
            Names(("B 2", true), ("B 5", true)));
    }

    private static void TryFindCommonBaseNames(StreetNames lhs, StreetNames rhs, StreetNames expected)
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
            Names(("Unter den Linden", false), ("B 2", true), ("B 5", true)),
            Names(("B 2", true), ("B 5", true)),
            Names(("B 2", true), ("B 5", true)));
    }

    private static void TryGetRouteNumbers(StreetNames streetNames, StreetNames expected)
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
    }

    private static void TryGetNonRouteNumbers(StreetNames streetNames, StreetNames expected)
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
    }
}
