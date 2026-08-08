// Faithful C# port of Valhalla mjolnir OSMRestriction.
// Source: valhalla/mjolnir/osmrestriction.h @ 3.8.3 commit a60c7cb
//
// OSMRestriction holds a simple/complex turn restriction parsed from an OSM
// relation. It is stored in a multimap keyed by the "from" way id. The C++ struct
// packs the type/modes/probability into a 32-bit Attributes bit-field; this port
// preserves the same widths and the same ordering (used by operator< / operator==).

using System;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// OSM restriction information. Result of parsing OSM simple restrictions found in
/// relations. Faithful port of the C++ <c>struct OSMRestriction</c>.
/// </summary>
public struct OSMRestriction : IComparable<OSMRestriction>, IEquatable<OSMRestriction>
{
    /// <summary>Fixed size of the via array, mirroring <c>kMaxViasPerRestriction</c> (31).</summary>
    public const int MaxViasPerRestriction = ComplexRestriction.MaxViasPerRestriction;

    // from is a way - uses OSM way Id.
    private ulong _from;

    // to is a way - uses OSM way Id.
    private ulong _to;

    // Via is a node. When parsing OSM this is stored as an OSM node Id (osmid). It
    // later gets changed into a GraphId. The C++ uses a union; we store both and only
    // one is meaningful at a time (osmid during parse, graphid after resolution).
    private ulong _viaOsmid;
    private GraphId _viaGraphId;

    // fixed size of vias.
    private ulong[] _vias;

    // timed restriction information.
    private ulong _timeDomain;

    // Type / modes / probability (the C++ Attributes bit-field):
    //   type_ : 4, modes_ : 12, probability_ : 7, spare_ : 9.
    private byte _type;          // : 4
    private uint _modes;         // : 12
    private byte _probability;   // : 7

    private ulong[] ViasStorage => _vias ??= new ulong[MaxViasPerRestriction];

    /// <summary>Sets the restriction type.</summary>
    public void SetType(RestrictionType type) => _type = (byte)((byte)type & 0xF);

    /// <summary>Gets the restriction type.</summary>
    public readonly RestrictionType TypeValue() => (RestrictionType)_type;

    /// <summary>Sets the via OSM node id.</summary>
    public void SetVia(ulong via) => _viaOsmid = via;

    /// <summary>Gets the via OSM node id.</summary>
    public readonly ulong Via() => _viaOsmid;

    /// <summary>Sets the vias - used for complex restrictions.</summary>
    public void SetVias(System.Collections.Generic.IReadOnlyList<ulong> vias)
    {
        ulong[] v = ViasStorage;
        Array.Clear(v, 0, v.Length);
        int count = Math.Min(vias.Count, MaxViasPerRestriction);
        for (int i = 0; i < count; ++i)
        {
            v[i] = vias[i];
        }
    }

    /// <summary>Gets the vias - used for complex restrictions (copy of the fixed array).</summary>
    public readonly ulong[] ViasArray()
    {
        ulong[] v = _vias ?? new ulong[MaxViasPerRestriction];
        return (ulong[])v.Clone();
    }

    /// <summary>
    /// Gets the vias used for complex restrictions, dropping the trailing/empty zero entries.
    /// Faithful port of the C++ <c>vias()</c> accessor (which returns only the non-zero entries of
    /// the fixed array). Used by the RestrictionBuilder to count and walk the real via ways.
    /// </summary>
    public readonly System.Collections.Generic.List<ulong> Vias()
    {
        var result = new System.Collections.Generic.List<ulong>();
        ulong[] v = _vias ?? Array.Empty<ulong>();
        foreach (ulong via in v)
        {
            if (via != 0)
            {
                result.Add(via);
            }
        }

        return result;
    }

    /// <summary>Sets the via node GraphId.</summary>
    public void SetViaGraphId(GraphId id) => _viaGraphId = id;

    /// <summary>Gets the via GraphId.</summary>
    public readonly GraphId ViaGraphId() => _viaGraphId;

    /// <summary>Sets the access modes mask (12 bits).</summary>
    public void SetModes(uint modes) => _modes = modes & 0xFFF;

    /// <summary>Gets the access modes mask.</summary>
    public readonly uint Modes() => _modes;

    /// <summary>Sets the from way id.</summary>
    public void SetFrom(ulong from) => _from = from;

    /// <summary>Gets the from way id.</summary>
    public readonly ulong From() => _from;

    /// <summary>Sets the to way id.</summary>
    public void SetTo(ulong to) => _to = to;

    /// <summary>Gets the to way id.</summary>
    public readonly ulong To() => _to;

    /// <summary>Sets the time domain.</summary>
    public void SetTimeDomain(ulong timeDomain) => _timeDomain = timeDomain;

    /// <summary>Gets the time domain.</summary>
    public readonly ulong TimeDomain() => _timeDomain;

    /// <summary>Sets the probability (7 bits).</summary>
    public void SetProbability(byte probability) => _probability = (byte)(probability & 0x7F);

    /// <summary>Gets the probability.</summary>
    public readonly byte Probability() => _probability;

    private readonly bool ViasEqual(in OSMRestriction o)
    {
        ulong[] a = _vias ?? Array.Empty<ulong>();
        ulong[] b = o._vias ?? Array.Empty<ulong>();
        for (int i = 0; i < MaxViasPerRestriction; ++i)
        {
            ulong av = i < a.Length ? a[i] : 0UL;
            ulong bv = i < b.Length ? b[i] : 0UL;
            if (av != bv)
            {
                return false;
            }
        }

        return true;
    }

    private static int ViasCompare(ulong[]? a, ulong[]? b)
    {
        // Mirrors the C++ "vias() < o.vias()" lexicographic comparison of the std::vector
        // returned by vias(). vias() returns the full fixed-size array (with zero padding).
        for (int i = 0; i < MaxViasPerRestriction; ++i)
        {
            ulong av = (a != null && i < a.Length) ? a[i] : 0UL;
            ulong bv = (b != null && i < b.Length) ? b[i] : 0UL;
            if (av != bv)
            {
                return av < bv ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Faithful port of the C++ <c>operator&lt;</c> used to sort restrictions. The
    /// ordering is: from, to, vias, modes, probability, time_domain.
    /// </summary>
    public readonly int CompareTo(OSMRestriction o)
    {
        if (From() != o.From())
        {
            return From() < o.From() ? -1 : 1;
        }

        if (To() != o.To())
        {
            return To() < o.To() ? -1 : 1;
        }

        int viaCmp = ViasCompare(_vias, o._vias);
        if (viaCmp != 0)
        {
            return viaCmp;
        }

        if (Modes() != o.Modes())
        {
            return Modes() < o.Modes() ? -1 : 1;
        }

        if (Probability() != o.Probability())
        {
            return Probability() < o.Probability() ? -1 : 1;
        }

        return TimeDomain().CompareTo(o.TimeDomain());
    }

    /// <summary>
    /// Faithful port of the C++ <c>operator==</c> used to compare complex restrictions.
    /// </summary>
    public readonly bool Equals(OSMRestriction o) =>
        From() == o.From() && To() == o.To() && ViasEqual(in o) && Modes() == o.Modes() &&
        Probability() == o.Probability() && TimeDomain() == o.TimeDomain();

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is OSMRestriction other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() =>
        HashCode.Combine(_from, _to, _modes, _probability, _timeDomain, _type);

    public static bool operator <(OSMRestriction a, OSMRestriction b) => a.CompareTo(b) < 0;

    public static bool operator >(OSMRestriction a, OSMRestriction b) => a.CompareTo(b) > 0;

    public static bool operator <=(OSMRestriction a, OSMRestriction b) => a.CompareTo(b) <= 0;

    public static bool operator >=(OSMRestriction a, OSMRestriction b) => a.CompareTo(b) >= 0;

    public static bool operator ==(OSMRestriction a, OSMRestriction b) => a.Equals(b);

    public static bool operator !=(OSMRestriction a, OSMRestriction b) => !a.Equals(b);
}
