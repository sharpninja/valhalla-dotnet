// Faithful C# port of Valhalla baldr StreetNameUs (streetname_us.h + src/baldr/streetname_us.cc)
// @ 3.7.0.
// Source: valhalla/baldr/streetname_us.h, src/baldr/streetname_us.cc

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// A US street name, with US-specific directional prefix / suffix handling. Faithful port of C++
/// <c>class StreetNameUs</c>.
/// </summary>
public class StreetNameUs : StreetName
{
    // Order matters: the C++ vectors are scanned in order and the first match wins.
    private static readonly IReadOnlyList<string> PreDirs = new[]
    {
        "North ", "East ", "South ", "West ", "Northeast ", "Southeast ", "Southwest ", "Northwest ",
    };

    private static readonly IReadOnlyList<string> PostDirs = new[]
    {
        " North", " East", " South", " West", " Northeast", " Southeast", " Southwest", " Northwest",
    };

    private static readonly IReadOnlyList<string> PostCardinalDirs = new[]
    {
        " North", " East", " South", " West",
    };

    /// <summary>
    /// Constructor. Faithful port of C++ <c>StreetNameUs(value, is_route_number, pronunciation)</c>.
    /// </summary>
    public StreetNameUs(string value, bool isRouteNumber, Pronunciation? pronunciation = null)
        : base(value, isRouteNumber, pronunciation)
    {
    }

    /// <inheritdoc/>
    public override string GetPreDir()
    {
        foreach (string preDir in PreDirs)
        {
            if (StartsWith(preDir))
            {
                return preDir;
            }
        }

        return string.Empty;
    }

    /// <inheritdoc/>
    public override string GetPostDir()
    {
        foreach (string postDir in PostDirs)
        {
            if (EndsWith(postDir))
            {
                return postDir;
            }
        }

        return string.Empty;
    }

    /// <inheritdoc/>
    public override string GetPostCardinalDir()
    {
        foreach (string postCardinalDir in PostCardinalDirs)
        {
            if (EndsWith(postCardinalDir))
            {
                return postCardinalDir;
            }
        }

        return string.Empty;
    }

    /// <inheritdoc/>
    public override string GetBaseName()
    {
        string preDir = GetPreDir();
        string postDir = GetPostDir();

        return ValueField.Substring(preDir.Length, ValueField.Length - preDir.Length - postDir.Length);
    }

    /// <inheritdoc/>
    public override bool HasSameBaseName(StreetName rhs) => GetBaseName() == rhs.GetBaseName();
}
