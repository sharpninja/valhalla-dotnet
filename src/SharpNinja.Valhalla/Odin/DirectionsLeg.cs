// Plain C# directions result classes (valhalla @ 3.7.0 directions.proto, de-protobuf'd).
//
// In Valhalla, odin's maneuver builder consumes the protobuf TripLeg (proto/trip.proto, ported as
// SharpNinja.Valhalla.Thor.TripLeg) and produces the protobuf DirectionsLeg
// (proto/directions.proto), whose central message is DirectionsLeg.Maneuver. The wire protobuf
// surface is an EXCLUDED module for this port (no proto runtime); per the task brief, the
// DirectionsLeg / DirectionsLeg.Maneuver result is ported here as plain C# classes carrying the
// maneuver STRUCTURE (type, street names, length/time, turn degree, begin/end shape index, cardinal
// direction, signs, flags) that the maneuver builder fills in and the app consumes.
//
// PORT-NOTE: The enum value names and underlying integer values are reproduced EXACTLY from
// proto/descriptors/directions.proto so that any algorithm that compares / switches on these types
// behaves identically to the C++.
//
// PORT-NOTE (DEFER): narrativebuilder.cc + narrative_dictionary.cc (localized prose instruction
// text) are NOT ported. The instruction / verbal_* string fields exist on the Maneuver data
// structure (they are part of the odin Maneuver class) but are left empty by the structural builder;
// only the maneuver STRUCTURE is produced. See Maneuver.cs.

using System.Collections.Generic;

using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Cardinal direction of a maneuver's begin heading. Faithful port of
/// <c>DirectionsLeg.Maneuver.CardinalDirection</c> (directions.proto). Underlying values match the
/// proto exactly.
/// </summary>
public enum DirectionsLegManeuverCardinalDirection
{
    /// <summary>North.</summary>
    North = 0,

    /// <summary>North-east.</summary>
    NorthEast = 1,

    /// <summary>East.</summary>
    East = 2,

    /// <summary>South-east.</summary>
    SouthEast = 3,

    /// <summary>South.</summary>
    South = 4,

    /// <summary>South-west.</summary>
    SouthWest = 5,

    /// <summary>West.</summary>
    West = 6,

    /// <summary>North-west.</summary>
    NorthWest = 7,
}

/// <summary>
/// Maneuver type. Faithful port of <c>DirectionsLeg.Maneuver.Type</c> (directions.proto). Underlying
/// integer values match the proto exactly so switch / comparison logic ported from C++ behaves
/// identically.
/// </summary>
public enum DirectionsLegManeuverType
{
    /// <summary>No maneuver type.</summary>
    None = 0,

    /// <summary>Start of the route.</summary>
    Start = 1,

    /// <summary>Start of the route to the right.</summary>
    StartRight = 2,

    /// <summary>Start of the route to the left.</summary>
    StartLeft = 3,

    /// <summary>Destination of the route.</summary>
    Destination = 4,

    /// <summary>Destination of the route on the right.</summary>
    DestinationRight = 5,

    /// <summary>Destination of the route on the left.</summary>
    DestinationLeft = 6,

    /// <summary>Road becomes a different road (name change, no turn).</summary>
    Becomes = 7,

    /// <summary>Continue straight.</summary>
    Continue = 8,

    /// <summary>Slight right.</summary>
    SlightRight = 9,

    /// <summary>Right.</summary>
    Right = 10,

    /// <summary>Sharp right.</summary>
    SharpRight = 11,

    /// <summary>U-turn to the right.</summary>
    UturnRight = 12,

    /// <summary>U-turn to the left.</summary>
    UturnLeft = 13,

    /// <summary>Sharp left.</summary>
    SharpLeft = 14,

    /// <summary>Left.</summary>
    Left = 15,

    /// <summary>Slight left.</summary>
    SlightLeft = 16,

    /// <summary>Take the ramp straight.</summary>
    RampStraight = 17,

    /// <summary>Take the ramp on the right.</summary>
    RampRight = 18,

    /// <summary>Take the ramp on the left.</summary>
    RampLeft = 19,

    /// <summary>Take the exit on the right.</summary>
    ExitRight = 20,

    /// <summary>Take the exit on the left.</summary>
    ExitLeft = 21,

    /// <summary>Stay straight.</summary>
    StayStraight = 22,

    /// <summary>Stay right.</summary>
    StayRight = 23,

    /// <summary>Stay left.</summary>
    StayLeft = 24,

    /// <summary>Merge.</summary>
    Merge = 25,

    /// <summary>Enter a roundabout.</summary>
    RoundaboutEnter = 26,

    /// <summary>Exit a roundabout.</summary>
    RoundaboutExit = 27,

    /// <summary>Enter a ferry.</summary>
    FerryEnter = 28,

    /// <summary>Exit a ferry.</summary>
    FerryExit = 29,

    /// <summary>Transit.</summary>
    Transit = 30,

    /// <summary>Transit transfer.</summary>
    TransitTransfer = 31,

    /// <summary>Remain on transit.</summary>
    TransitRemainOn = 32,

    /// <summary>Transit connection start.</summary>
    TransitConnectionStart = 33,

    /// <summary>Transit connection transfer.</summary>
    TransitConnectionTransfer = 34,

    /// <summary>Transit connection destination.</summary>
    TransitConnectionDestination = 35,

    /// <summary>Post transit connection destination.</summary>
    PostTransitConnectionDestination = 36,

    /// <summary>Merge to the right.</summary>
    MergeRight = 37,

    /// <summary>Merge to the left.</summary>
    MergeLeft = 38,

    /// <summary>Enter an elevator.</summary>
    ElevatorEnter = 39,

    /// <summary>Enter steps.</summary>
    StepsEnter = 40,

    /// <summary>Enter an escalator.</summary>
    EscalatorEnter = 41,

    /// <summary>Enter a building.</summary>
    BuildingEnter = 42,

    /// <summary>Exit a building.</summary>
    BuildingExit = 43,

    /// <summary>Level change.</summary>
    LevelChange = 44,

    /// <summary>Park the vehicle.</summary>
    ParkVehicle = 45,
}

/// <summary>
/// Bike share maneuver type. Faithful port of <c>DirectionsLeg.Maneuver.BssManeuverType</c>.
/// </summary>
public enum DirectionsLegManeuverBssManeuverType
{
    /// <summary>No bike-share action.</summary>
    NoneAction = 0,

    /// <summary>Rent a bike at the bike share station.</summary>
    RentBikeAtBikeShare = 1,

    /// <summary>Return a bike at the bike share station.</summary>
    ReturnBikeAtBikeShare = 2,
}

/// <summary>
/// A guidance view associated with a maneuver. Faithful port of
/// <c>DirectionsLeg.GuidanceView</c> (directions.proto).
/// </summary>
public sealed class DirectionsLegGuidanceView
{
    /// <summary>Guidance view type. Faithful port of <c>DirectionsLeg.GuidanceView.Type</c>.</summary>
    public enum ViewType
    {
        /// <summary>Junction.</summary>
        Junction = 0,

        /// <summary>SAPA.</summary>
        Sapa = 1,

        /// <summary>Toll branch.</summary>
        Tollbranch = 2,

        /// <summary>After toll.</summary>
        Aftertoll = 3,

        /// <summary>Entrance.</summary>
        Ent = 4,

        /// <summary>Exit.</summary>
        Exit = 5,

        /// <summary>City real view.</summary>
        Cityreal = 6,

        /// <summary>Direction board.</summary>
        Directionboard = 7,

        /// <summary>Signboard.</summary>
        Signboard = 8,
    }

    /// <summary>Data id (image data identifier).</summary>
    public string DataId { get; set; } = string.Empty;

    /// <summary>The type of guidance view.</summary>
    public ViewType Type { get; set; }

    /// <summary>Image base id.</summary>
    public string BaseId { get; set; } = string.Empty;

    /// <summary>List of overlay ids.</summary>
    public List<string> OverlayIds { get; } = new();
}

/// <summary>
/// A single leg of the directions result: the ordered maneuvers plus leg-level metadata. The
/// de-protobuf'd subset of the proto <c>DirectionsLeg</c>. odin's maneuver builder produces a list
/// of <see cref="Maneuver"/> (the odin working type) and then populates a <see cref="DirectionsLeg"/>
/// from it. The <see cref="Maneuvers"/> here carry the same structural data as the odin
/// <see cref="Maneuver"/> objects.
/// </summary>
public sealed class DirectionsLeg
{
    /// <summary>Trip id.</summary>
    public ulong TripId { get; set; }

    /// <summary>Leg id within the trip.</summary>
    public uint LegId { get; set; }

    /// <summary>Number of legs in the trip.</summary>
    public uint LegCount { get; set; }

    /// <summary>The ordered maneuvers of the leg.</summary>
    public List<Maneuver> Maneuvers { get; } = new();

    /// <summary>The polyline6-encoded shape of the leg (mirrors <see cref="TripLeg.EncodedShape"/>).</summary>
    public string Shape { get; set; } = string.Empty;

    /// <summary>True if any maneuver on the leg uses a tolled edge.</summary>
    public bool HasToll { get; set; }

    /// <summary>True if any maneuver on the leg uses a ferry edge.</summary>
    public bool HasFerry { get; set; }

    /// <summary>True if any maneuver on the leg uses a motorway edge.</summary>
    public bool HasHighway { get; set; }
}
