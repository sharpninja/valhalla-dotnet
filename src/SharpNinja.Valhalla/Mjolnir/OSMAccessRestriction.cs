// Faithful C# port of Valhalla mjolnir OSMAccessRestriction.
// Source: valhalla/mjolnir/osmaccessrestriction.h @ 3.7.0
//
// Access restrictions (maxweight/maxheight/maxlength/maxwidth/hazmat/maxaxles/
// maxaxleload/timed/destination-allowed) parsed from way tags. Stored in a multimap
// keyed by the "from" way id. The C++ class packs type (4 bits) + modes (12 bits) into
// an Attributes bit-field plus a 64-bit value, a direction enum, and an
// except_destination flag. This port keeps the same fields and widths.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Direction in which an access restriction applies. Faithful port of C++
/// <c>enum class AccessRestrictionDirection : uint8_t</c>.
/// </summary>
public enum AccessRestrictionDirection : byte
{
    Both = 0,
    Forward = 1,
    Backward = 2,
}

/// <summary>
/// OSM access restriction information. Faithful port of the C++
/// <c>class OSMAccessRestriction</c> from <c>valhalla/mjolnir/osmaccessrestriction.h</c>.
/// </summary>
public struct OSMAccessRestriction
{
    private ulong _value;
    private byte _type;            // : 4
    private ushort _modes;         // : 12
    private bool _exceptDestination; // : 1
    private AccessRestrictionDirection _direction;

    /// <summary>Constructs an empty access restriction (direction Both, no exceptions).</summary>
    public OSMAccessRestriction()
    {
        _value = 0;
        _type = 0;
        _modes = 0;
        _exceptDestination = false;
        _direction = AccessRestrictionDirection.Both;
    }

    /// <summary>Sets the restriction type (4 bits).</summary>
    public void SetType(AccessType type) => _type = (byte)((byte)type & 0xF);

    /// <summary>Gets the restriction type.</summary>
    public readonly AccessType TypeValue() => (AccessType)_type;

    /// <summary>Sets the value for the restriction.</summary>
    public void SetValue(ulong value) => _value = value;

    /// <summary>Gets the value.</summary>
    public readonly ulong Value() => _value;

    /// <summary>Sets the affected modes bit-field (12 bits).</summary>
    public void SetModes(ushort modes) => _modes = (ushort)(modes & 0xFFF);

    /// <summary>Gets the affected modes bit-field.</summary>
    public readonly ushort Modes() => _modes;

    /// <summary>Sets the direction the access restriction applies to.</summary>
    public void SetDirection(AccessRestrictionDirection direction) => _direction = direction;

    /// <summary>Gets the direction the access restriction applies to.</summary>
    public readonly AccessRestrictionDirection Direction() => _direction;

    /// <summary>Sets the flag for whether the restriction excepts local (destination) traffic.</summary>
    public void SetExceptDestination(bool exceptDestination) => _exceptDestination = exceptDestination;

    /// <summary>Whether the restriction excepts local (destination) traffic.</summary>
    public readonly bool ExceptDestination() => _exceptDestination;
}
