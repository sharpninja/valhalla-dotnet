// Faithful C# port of Valhalla mjolnir OSMAccess.
// Source: valhalla/mjolnir/osmaccess.h @ 3.7.0
//
// OSMAccess records the user-set access tags for a way (e.g. auto, bike, bus,
// foot, truck, hov, taxi, motorroad, moped, motorcycle). It is keyed by the OSM
// way id and is used to record whether access was explicitly tagged (vs. defaulted)
// so that country-default access overrides do not stomp on user intent.
//
// The C++ struct packs the flags into a 16-bit bit-field union (AccessAttributes).
// Here the flags are stored in a single backing ushort with the identical bit
// layout so the on-disk representation (and any masking logic) is preserved.

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// OSM Access. User-set access flags, keyed by OSM way id. Faithful port of the
/// C++ <c>struct OSMAccess</c> from <c>valhalla/mjolnir/osmaccess.h</c>.
/// </summary>
public struct OSMAccess
{
    // Bit positions inside the attributes word. These mirror the C++
    // AccessAttributes::Fields bit-field declaration order exactly.
    private const ushort AutoBit = 1 << 0;
    private const ushort BikeBit = 1 << 1;
    private const ushort BusBit = 1 << 2;
    private const ushort FootBit = 1 << 3;
    private const ushort TruckBit = 1 << 4;
    private const ushort HovBit = 1 << 5;
    private const ushort TaxiBit = 1 << 6;
    private const ushort MotorroadBit = 1 << 7;
    private const ushort MopedBit = 1 << 8;
    private const ushort MotorcycleBit = 1 << 9;
    // bits 10..15 are spare (6 bits).

    private ulong _osmWayId;
    private ushort _attributes;

    /// <summary>Constructs an empty <see cref="OSMAccess"/> (all flags clear, way id 0).</summary>
    public OSMAccess()
    {
        _osmWayId = 0;
        _attributes = 0;
    }

    /// <summary>Constructs an <see cref="OSMAccess"/> for the given way id with all flags clear.</summary>
    public OSMAccess(ulong id)
    {
        _osmWayId = id;
        _attributes = 0;
    }

    /// <summary>Sets the OSM way id.</summary>
    public void SetWayId(ulong id) => _osmWayId = id;

    /// <summary>Gets the OSM way id.</summary>
    public readonly ulong WayId() => _osmWayId;

    /// <summary>Raw attributes word (mirrors the C++ union <c>v</c> field, low 16 bits).</summary>
    public ushort Attributes
    {
        readonly get => _attributes;
        set => _attributes = value;
    }

    private void SetBit(ushort bit, bool on)
    {
        if (on)
        {
            _attributes |= bit;
        }
        else
        {
            _attributes = (ushort)(_attributes & ~bit);
        }
    }

    private readonly bool GetBit(ushort bit) => (_attributes & bit) != 0;

    /// <summary>Sets the auto_tag flag (autos allowed on this way?).</summary>
    public void SetAutoTag(bool v) => SetBit(AutoBit, v);

    /// <summary>Gets the auto_tag flag.</summary>
    public readonly bool AutoTag() => GetBit(AutoBit);

    /// <summary>Sets the bike_tag flag.</summary>
    public void SetBikeTag(bool v) => SetBit(BikeBit, v);

    /// <summary>Gets the bike_tag flag.</summary>
    public readonly bool BikeTag() => GetBit(BikeBit);

    /// <summary>Sets the moped_tag flag (mopeds / electric scooters allowed?).</summary>
    public void SetMopedTag(bool v) => SetBit(MopedBit, v);

    /// <summary>Gets the moped_tag flag.</summary>
    public readonly bool MopedTag() => GetBit(MopedBit);

    /// <summary>Sets the bus_tag flag.</summary>
    public void SetBusTag(bool v) => SetBit(BusBit, v);

    /// <summary>Gets the bus_tag flag.</summary>
    public readonly bool BusTag() => GetBit(BusBit);

    /// <summary>Sets the foot_tag flag (pedestrians allowed?).</summary>
    public void SetFootTag(bool v) => SetBit(FootBit, v);

    /// <summary>Gets the foot_tag flag.</summary>
    public readonly bool FootTag() => GetBit(FootBit);

    /// <summary>Sets the truck_tag flag.</summary>
    public void SetTruckTag(bool v) => SetBit(TruckBit, v);

    /// <summary>Gets the truck_tag flag.</summary>
    public readonly bool TruckTag() => GetBit(TruckBit);

    /// <summary>Sets the hov_tag flag.</summary>
    public void SetHovTag(bool v) => SetBit(HovBit, v);

    /// <summary>Gets the hov_tag flag.</summary>
    public readonly bool HovTag() => GetBit(HovBit);

    /// <summary>Sets the taxi_tag flag.</summary>
    public void SetTaxiTag(bool v) => SetBit(TaxiBit, v);

    /// <summary>Gets the taxi_tag flag.</summary>
    public readonly bool TaxiTag() => GetBit(TaxiBit);

    /// <summary>Sets the motorroad_tag flag (does a motorroad tag exist?).</summary>
    public void SetMotorroadTag(bool v) => SetBit(MotorroadBit, v);

    /// <summary>Gets the motorroad_tag flag.</summary>
    public readonly bool MotorroadTag() => GetBit(MotorroadBit);

    /// <summary>Sets the motorcycle_tag flag.</summary>
    public void SetMotorcycleTag(bool v) => SetBit(MotorcycleBit, v);

    /// <summary>Gets the motorcycle_tag flag.</summary>
    public readonly bool MotorcycleTag() => GetBit(MotorcycleBit);
}
