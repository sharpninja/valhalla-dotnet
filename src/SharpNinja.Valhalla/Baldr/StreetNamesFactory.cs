// Faithful C# port of Valhalla baldr StreetNamesFactory (streetnames_factory.h +
// src/baldr/streetnames_factory.cc) @ 3.7.0.
// Source: valhalla/baldr/streetnames_factory.h, src/baldr/streetnames_factory.cc
//
// PORT-NOTE: The protobuf overload (RepeatedPtrField<valhalla::StreetName>) is
// omitted; protobuf is excluded from this port. Only the
// vector<pair<string,bool>> overload is ported.

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Creates the appropriate <see cref="StreetNames"/> subtype for a given country code. Faithful
/// port of C++ <c>class StreetNamesFactory</c>.
/// </summary>
public static class StreetNamesFactory
{
    /// <summary>
    /// Creates a <see cref="StreetNamesUs"/> for the US country code, otherwise a base
    /// <see cref="StreetNames"/>. Faithful port of C++ <c>StreetNamesFactory::Create</c>.
    /// </summary>
    public static StreetNames Create(string countryCode, IEnumerable<(string Name, bool IsRouteNumber)> names)
    {
        if (countryCode == "US")
        {
            return new StreetNamesUs(names);
        }

        return new StreetNames(names);
    }
}
