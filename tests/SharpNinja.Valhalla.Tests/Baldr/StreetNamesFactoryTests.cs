// Faithful C# port of Valhalla's gtest suite test/streetnames_factory.cc.
// The C++ test compares typeid(...).name() mangled strings; here the equivalent assertion is on
// the concrete runtime type (US -> StreetNamesUs, otherwise -> StreetNames).

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class StreetNamesFactoryTests
{
    private static void TryCreate(
        string countryCode,
        IReadOnlyList<(string Name, bool IsRouteNumber)> names,
        Type expected)
    {
        StreetNames streetNames = StreetNamesFactory.Create(countryCode, names);
        Assert.Equal(expected, streetNames.GetType());
    }

    [Fact]
    public void Create()
    {
        // US - should be StreetNamesUs
        TryCreate("US", new[] { ("Main Street", false) }, typeof(StreetNamesUs));
        TryCreate("US", new[] { ("Hershey Road", false), ("PA 743 North", true) }, typeof(StreetNamesUs));

        // DE - should be default StreetNames
        TryCreate("DE", new[] { ("Mittelstraße", false) }, typeof(StreetNames));
        TryCreate("DE", new[] { ("Unter den Linden", false), ("B 2", true), ("B 5", true) }, typeof(StreetNames));
    }
}
