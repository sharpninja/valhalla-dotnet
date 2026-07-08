// Faithful C# port of Valhalla baldr StreetName (streetname.h + src/baldr/streetname.cc) @ 3.7.0.
// Source: valhalla/baldr/streetname.h, src/baldr/streetname.cc
//
// PORT-NOTE: The C++ Pronunciation uses the protobuf enum
// valhalla::Pronunciation_Alphabet. Protobuf is excluded from this port, so the
// alphabet is represented by the baldr PronunciationAlphabet enum (graphconstants.h),
// which carries the same values.

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Pronunciation of a street name: an alphabet plus a value. Faithful port of C++
/// <c>struct Pronunciation</c>.
/// </summary>
public readonly struct Pronunciation
{
    /// <summary>Constructs a pronunciation.</summary>
    public Pronunciation(PronunciationAlphabet alphabet, string value)
    {
        Alphabet = alphabet;
        Value = value;
    }

    /// <summary>The phonetic alphabet of this pronunciation.</summary>
    public PronunciationAlphabet Alphabet { get; }

    /// <summary>The pronunciation string.</summary>
    public string Value { get; }
}

/// <summary>
/// A street name. Faithful port of C++ <c>class StreetName</c>.
/// </summary>
public class StreetName : IEquatable<StreetName>
{
    /// <summary>The street name string.</summary>
    protected readonly string ValueField;

    /// <summary>Whether the street name is a reference route number.</summary>
    protected readonly bool IsRouteNumberField;

    /// <summary>The (optional) pronunciation.</summary>
    protected readonly Pronunciation? PronunciationField;

    /// <summary>
    /// Constructor. Faithful port of C++ <c>StreetName(value, is_route_number, pronunciation)</c>.
    /// </summary>
    /// <param name="value">Street name string.</param>
    /// <param name="isRouteNumber">Whether the street name is a reference route number.</param>
    /// <param name="pronunciation">The (optional) pronunciation of this street name.</param>
    public StreetName(string value, bool isRouteNumber, Pronunciation? pronunciation = null)
    {
        ValueField = value;
        IsRouteNumberField = isRouteNumber;
        PronunciationField = pronunciation;
    }

    /// <summary>The street name string.</summary>
    public string Value => ValueField;

    /// <summary>
    /// Returns true if the street name is a reference route number such as: I 81 South or
    /// US 322 West.
    /// </summary>
    public bool IsRouteNumber => IsRouteNumberField;

    /// <summary>Returns the (optional) pronunciation of this street name.</summary>
    public Pronunciation? GetPronunciation() => PronunciationField;

    /// <summary>Operator equality. Compares value and route-number flag. Mirrors C++ <c>operator==</c>.</summary>
    public bool Equals(StreetName? rhs)
        => rhs != null && ValueField == rhs.ValueField && IsRouteNumberField == rhs.IsRouteNumberField;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as StreetName);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ValueField, IsRouteNumberField);

    /// <summary>Operator equality.</summary>
    public static bool operator ==(StreetName? a, StreetName? b)
        => a is null ? b is null : a.Equals(b);

    /// <summary>Operator inequality.</summary>
    public static bool operator !=(StreetName? a, StreetName? b) => !(a == b);

    /// <summary>Returns true if the value starts with the given prefix. Mirrors C++ <c>StartsWith</c>.</summary>
    public bool StartsWith(string prefix) => ValueField.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>Returns true if the value ends with the given suffix. Mirrors C++ <c>EndsWith</c>.</summary>
    public bool EndsWith(string suffix) => ValueField.EndsWith(suffix, StringComparison.Ordinal);

    /// <summary>Gets the leading directional prefix (base class returns empty). Mirrors C++ <c>GetPreDir</c>.</summary>
    public virtual string GetPreDir() => string.Empty;

    /// <summary>Gets the trailing directional suffix (base class returns empty). Mirrors C++ <c>GetPostDir</c>.</summary>
    public virtual string GetPostDir() => string.Empty;

    /// <summary>Gets the trailing cardinal directional suffix (base class returns empty). Mirrors C++ <c>GetPostCardinalDir</c>.</summary>
    public virtual string GetPostCardinalDir() => string.Empty;

    /// <summary>
    /// Gets the base name (the value with the pre/post directional removed). Mirrors C++
    /// <c>GetBaseName</c>.
    /// </summary>
    public virtual string GetBaseName()
    {
        string preDir = GetPreDir();
        string postDir = GetPostDir();

        return ValueField.Substring(preDir.Length, ValueField.Length - preDir.Length - postDir.Length);
    }

    /// <summary>Returns true if this name has the same base name as <paramref name="rhs"/>. Mirrors C++ <c>HasSameBaseName</c>.</summary>
    public virtual bool HasSameBaseName(StreetName rhs) => GetBaseName() == rhs.GetBaseName();
}
