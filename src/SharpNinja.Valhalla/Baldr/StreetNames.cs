// Faithful C# port of Valhalla baldr StreetNames (streetnames.h + src/baldr/streetnames.cc) @ 3.7.0.
// Source: valhalla/baldr/streetnames.h, src/baldr/streetnames.cc
//
// PORT-NOTE: The C++ StreetNames derives from std::list<std::unique_ptr<StreetName>>.
// Here it derives from List<StreetName>, preserving insertion order and the same
// public operations.
//
// PORT-NOTE: The protobuf constructor (RepeatedPtrField<valhalla::StreetName>) is
// omitted; protobuf is excluded from this port. Only the
// vector<pair<string,bool>> constructor is ported.
//
// PORT-NOTE: ToString's optional VerbalTextFormatter parameter is omitted; the
// verbal_text_formatter family is deferred to the odin port (excluded). ToString
// therefore always uses the raw street-name value.

using System.Collections.Generic;
using System.Text;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// An ordered list of <see cref="StreetName"/>. Faithful port of C++ <c>class StreetNames</c>.
/// </summary>
public class StreetNames : List<StreetName>
{
    /// <summary>Default constructor. Faithful port of C++ <c>StreetNames()</c>.</summary>
    public StreetNames()
    {
    }

    /// <summary>
    /// Constructs from a list of (name, is-route-number) pairs. Faithful port of C++
    /// <c>StreetNames(const std::vector&lt;std::pair&lt;std::string, bool&gt;&gt;&amp;)</c>.
    /// </summary>
    public StreetNames(IEnumerable<(string Name, bool IsRouteNumber)> names)
    {
        foreach ((string name, bool isRouteNumber) in names)
        {
            Add(CreateStreetName(name, isRouteNumber, null));
        }
    }

    /// <summary>
    /// Factory hook used by the base and derived list types to create the correct concrete
    /// <see cref="StreetName"/> subtype. The base creates <see cref="StreetName"/>; the US list
    /// overrides this to create <see cref="StreetNameUs"/>.
    /// </summary>
    protected virtual StreetName CreateStreetName(string value, bool isRouteNumber, Pronunciation? pronunciation)
        => new StreetName(value, isRouteNumber, pronunciation);

    /// <summary>
    /// Factory hook used to create a new empty list of the correct concrete type. The base creates
    /// <see cref="StreetNames"/>; the US list overrides this to create <see cref="StreetNamesUs"/>.
    /// This mirrors the C++ methods each allocating a <c>StreetNames</c> / <c>StreetNamesUs</c>.
    /// </summary>
    protected virtual StreetNames NewEmpty() => new StreetNames();

    /// <summary>
    /// Returns the list of names as a delimited string, optionally limited by max count, with each
    /// pronunciation appended in parentheses. Faithful port of C++ <c>ToString()</c> (without the
    /// verbal formatter, which is excluded from this port).
    /// </summary>
    public string ToStringDelimited(uint maxCount = 0, string delim = "/")
    {
        var nameString = new StringBuilder();
        uint count = 0;
        if (Count == 0)
        {
            nameString.Append("unnamed");
        }

        foreach (StreetName streetName in this)
        {
            if (maxCount > 0 && count == maxCount)
            {
                break;
            }

            if (nameString.Length != 0)
            {
                nameString.Append(delim);
            }

            nameString.Append(streetName.Value);
            Pronunciation? pron = streetName.GetPronunciation();
            if (pron.HasValue)
            {
                nameString.Append('(').Append(pron.Value.Value).Append(')');
            }

            ++count;
        }

        return nameString.ToString();
    }

    /// <summary>Clones this list (deep copy of each street name). Faithful port of C++ <c>clone()</c>.</summary>
    public virtual StreetNames Clone()
    {
        var cloneStreetNames = NewEmpty();
        foreach (StreetName streetName in this)
        {
            cloneStreetNames.Add(
                CreateStreetName(streetName.Value, streetName.IsRouteNumber, streetName.GetPronunciation()));
        }

        return cloneStreetNames;
    }

    /// <summary>
    /// Returns the street names common to this list and <paramref name="otherStreetNames"/>.
    /// Faithful port of C++ <c>FindCommonStreetNames()</c>.
    /// </summary>
    public virtual StreetNames FindCommonStreetNames(StreetNames otherStreetNames)
    {
        var commonStreetNames = NewEmpty();
        foreach (StreetName streetName in this)
        {
            foreach (StreetName otherStreetName in otherStreetNames)
            {
                if (streetName == otherStreetName)
                {
                    commonStreetNames.Add(
                        CreateStreetName(streetName.Value, streetName.IsRouteNumber, streetName.GetPronunciation()));
                    break;
                }
            }
        }

        return commonStreetNames;
    }

    /// <summary>
    /// Returns the street names that share a base name with any in <paramref name="otherStreetNames"/>,
    /// preferring the variant carrying a post-cardinal directional. Faithful port of C++
    /// <c>FindCommonBaseNames()</c>.
    /// </summary>
    public virtual StreetNames FindCommonBaseNames(StreetNames otherStreetNames)
    {
        var commonBaseNames = NewEmpty();
        foreach (StreetName streetName in this)
        {
            foreach (StreetName otherStreetName in otherStreetNames)
            {
                if (streetName.HasSameBaseName(otherStreetName))
                {
                    // Use the name with the cardinal directional suffix, thus 'US 30 West' will be
                    // used instead of 'US 30'.
                    if (streetName.GetPostCardinalDir().Length != 0)
                    {
                        commonBaseNames.Add(
                            CreateStreetName(streetName.Value, streetName.IsRouteNumber, streetName.GetPronunciation()));
                    }
                    else if (otherStreetName.GetPostCardinalDir().Length != 0)
                    {
                        commonBaseNames.Add(
                            CreateStreetName(otherStreetName.Value, otherStreetName.IsRouteNumber, otherStreetName.GetPronunciation()));
                    }
                    else
                    {
                        // Use streetName by default.
                        commonBaseNames.Add(
                            CreateStreetName(streetName.Value, streetName.IsRouteNumber, streetName.GetPronunciation()));
                    }

                    break;
                }
            }
        }

        return commonBaseNames;
    }

    /// <summary>Returns only the route-number names. Faithful port of C++ <c>GetRouteNumbers()</c>.</summary>
    public virtual StreetNames GetRouteNumbers()
    {
        var routeNumbers = NewEmpty();
        foreach (StreetName streetName in this)
        {
            if (streetName.IsRouteNumber)
            {
                routeNumbers.Add(
                    CreateStreetName(streetName.Value, streetName.IsRouteNumber, streetName.GetPronunciation()));
            }
        }

        return routeNumbers;
    }

    /// <summary>Returns only the non-route-number names. Faithful port of C++ <c>GetNonRouteNumbers()</c>.</summary>
    public virtual StreetNames GetNonRouteNumbers()
    {
        var nonRouteNumbers = NewEmpty();
        foreach (StreetName streetName in this)
        {
            if (!streetName.IsRouteNumber)
            {
                nonRouteNumbers.Add(
                    CreateStreetName(streetName.Value, streetName.IsRouteNumber, streetName.GetPronunciation()));
            }
        }

        return nonRouteNumbers;
    }
}
