// Faithful C# port of Valhalla baldr SignInfo (signinfo.h) @ 3.7.0.
// Source: valhalla/baldr/signinfo.h
// Self-contained engine port: does NOT reuse other TruckMate types.
//
// SignInfo is an interface/transfer class (NOT a bit-packed on-disk tile struct);
// it carries the sign type plus the resolved text and linguistic indices. No fixed
// byte size applies.

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Interface class used to pass information about a sign.
/// Encapsulates the sign type and the associated text.
/// </summary>
public sealed class SignInfo
{
    private readonly uint _linguisticStartIndex;
    private readonly uint _linguisticCount;
    private readonly Sign.Type _type;
    private readonly bool _isRouteNum;
    private readonly bool _isTagged;
    private readonly bool _hasLinguistic;
    private readonly string _text;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="type">Sign type.</param>
    /// <param name="rn">Indicates if this sign is a route number.</param>
    /// <param name="tagged">Indicates if this sign is a special tagged type.</param>
    /// <param name="hasLinguistic">Indicates if this has a linguistic record or not.</param>
    /// <param name="linguisticStartIndex">The linguistic start index.</param>
    /// <param name="linguisticCount">The number of linguistic records.</param>
    /// <param name="text">Text string.</param>
    public SignInfo(
        Sign.Type type,
        bool rn,
        bool tagged,
        bool hasLinguistic,
        uint linguisticStartIndex,
        uint linguisticCount,
        string text)
    {
        _linguisticStartIndex = linguisticStartIndex;
        _linguisticCount = linguisticCount;
        _type = type;
        _isRouteNum = rn;
        _isTagged = tagged;
        _hasLinguistic = hasLinguistic;
        _text = text;
    }

    /// <summary>Returns the linguistic start index.</summary>
    public uint LinguisticStartIndex => _linguisticStartIndex;

    /// <summary>Returns the linguistic count.</summary>
    public uint LinguisticCount => _linguisticCount;

    /// <summary>Returns the sign type.</summary>
    public Sign.Type Type => _type;

    /// <summary>Does this sign record indicate a route number.</summary>
    public bool IsRouteNum => _isRouteNum;

    /// <summary>Is the sign text tagged.</summary>
    public bool IsTagged => _isTagged;

    /// <summary>Does the sign have a linguistic set.</summary>
    public bool HasLinguistic => _hasLinguistic;

    /// <summary>Returns the sign text.</summary>
    public string Text => _text;

    /// <summary>operator&lt; - for sorting. Sort by type.</summary>
    public int CompareTo(SignInfo other) => _type.CompareTo(other._type);

    /// <summary>Less-than comparison mirroring the C++ <c>operator&lt;</c>.</summary>
    public static bool operator <(SignInfo a, SignInfo b) => a.CompareTo(b) < 0;

    /// <summary>Greater-than comparison.</summary>
    public static bool operator >(SignInfo a, SignInfo b) => a.CompareTo(b) > 0;
}
