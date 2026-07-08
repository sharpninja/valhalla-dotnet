// Faithful C# port of Valhalla baldr StreetNamesUs (streetnames_us.h + src/baldr/streetnames_us.cc)
// @ 3.7.0.
// Source: valhalla/baldr/streetnames_us.h, src/baldr/streetnames_us.cc
//
// PORT-NOTE: The protobuf constructor (RepeatedPtrField<valhalla::StreetName>) is
// omitted; protobuf is excluded from this port.
//
// PORT-NOTE: The C++ StreetNamesUs duplicates clone / FindCommonStreetNames /
// FindCommonBaseNames / GetRouteNumbers / GetNonRouteNumbers, differing only by
// allocating StreetNamesUs containers full of StreetNameUs entries. Here those
// methods are inherited from StreetNames unchanged; the polymorphic element type
// (StreetNameUs) and container type (StreetNamesUs) are produced via the
// overridden CreateStreetName / NewEmpty factory hooks, yielding identical results.

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// An ordered list of US street names. Faithful port of C++ <c>class StreetNamesUs</c>.
/// </summary>
public class StreetNamesUs : StreetNames
{
    /// <summary>Default constructor. Faithful port of C++ <c>StreetNamesUs()</c>.</summary>
    public StreetNamesUs()
    {
    }

    /// <summary>
    /// Constructs from a list of (name, is-route-number) pairs, creating <see cref="StreetNameUs"/>
    /// entries. Faithful port of C++ <c>StreetNamesUs(const std::vector&lt;...&gt;&amp;)</c>.
    /// </summary>
    public StreetNamesUs(IEnumerable<(string Name, bool IsRouteNumber)> names)
    {
        foreach ((string name, bool isRouteNumber) in names)
        {
            Add(new StreetNameUs(name, isRouteNumber, null));
        }
    }

    /// <inheritdoc/>
    protected override StreetName CreateStreetName(string value, bool isRouteNumber, Pronunciation? pronunciation)
        => new StreetNameUs(value, isRouteNumber, pronunciation);

    /// <inheritdoc/>
    protected override StreetNames NewEmpty() => new StreetNamesUs();
}
