// Faithful C# port of Valhalla baldr graphconstants.h (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/baldr/graphconstants.h
//
// Self-contained engine port: it intentionally does NOT reuse other TruckMate types.
// Constant values, signedness, and precision (float vs double) mirror the original
// C++ constexpr declarations exactly so that bit-packed tile data parses identically.
//
// PORT-NOTE: The string conversion helpers (to_string / stringToRoadClass /
// stringLanguage) are preserved faithfully because they encode the canonical
// enum<->string mappings used throughout the engine. The rapidjson/json paths are
// excluded per the porting scope and are not represented here.
// PORT-NOTE: kMaxLocalEdgeIndex / kMaxEdgesPerNode are declared in nodeinfo.h (not
// graphconstants.h) but are surfaced here for the DirectedEdge port slice that consumes them.

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Baldr graph constants and bit-field masks ported verbatim from
/// <c>valhalla/baldr/graphconstants.h</c>. Values are kept at the exact width/signedness
/// of the C++ declarations to preserve identical tile bit-packing behaviour.
/// </summary>
public static class GraphConstants
{
    // OSM Ids can exceed 32 bits, but these are currently only Node Ids. Way Ids should still
    // have room to grow before exceeding an unsigned 32 bit word.
    public const uint MaxOsmWayId = 4294967295;

    // Maximum tile id/index supported. 22 bits
    public const uint MaxGraphTileId = 4194303;

    // Maximum id/index within a tile. 21 bits
    public const uint MaxGraphId = 2097151;

    /// <summary>
    /// Invalid edge label index. Mirrors <c>kInvalidLabel = std::numeric_limits&lt;uint32_t&gt;::max()</c>.
    /// </summary>
    public const uint InvalidLabel = uint.MaxValue;

    // The largest path id that can be used in a multi path expansion.
    public const byte MaxMultiPathId = 127;

    // Invalid restriction index
    public const byte InvalidRestriction = byte.MaxValue;

    // ---- Access bit field constants. Access in directed edge allows 12 bits. ----

    /// <summary>Auto (car) access bit. Mirrors C++ <c>kAutoAccess</c>.</summary>
    public const ushort AutoAccess = 1;

    /// <summary>Pedestrian access bit. Mirrors C++ <c>kPedestrianAccess</c>.</summary>
    public const ushort PedestrianAccess = 2;

    /// <summary>Bicycle access bit. Mirrors C++ <c>kBicycleAccess</c>.</summary>
    public const ushort BicycleAccess = 4;

    /// <summary>Truck access bit. Mirrors C++ <c>kTruckAccess</c>.</summary>
    public const ushort TruckAccess = 8;

    /// <summary>Emergency vehicle access bit. Mirrors C++ <c>kEmergencyAccess</c>.</summary>
    public const ushort EmergencyAccess = 16;

    /// <summary>Taxi access bit. Mirrors C++ <c>kTaxiAccess</c>.</summary>
    public const ushort TaxiAccess = 32;

    /// <summary>Bus access bit. Mirrors C++ <c>kBusAccess</c>.</summary>
    public const ushort BusAccess = 64;

    /// <summary>High-occupancy-vehicle access bit. Mirrors C++ <c>kHOVAccess</c>.</summary>
    public const ushort HovAccess = 128;

    /// <summary>Wheelchair access bit. Mirrors C++ <c>kWheelchairAccess</c>.</summary>
    public const ushort WheelchairAccess = 256;

    /// <summary>Moped access bit. Mirrors C++ <c>kMopedAccess</c>.</summary>
    public const ushort MopedAccess = 512;

    /// <summary>Motorcycle access bit. Mirrors C++ <c>kMotorcycleAccess</c>.</summary>
    public const ushort MotorcycleAccess = 1024;

    /// <summary>All access modes (12 bits). Mirrors C++ <c>kAllAccess = 4095</c>.</summary>
    public const ushort AllAccess = 4095;

    /// <summary>Constant representing vehicular access types. Mirrors C++ <c>kVehicularAccess</c>.</summary>
    public const ushort VehicularAccess = AutoAccess | TruckAccess | MopedAccess | MotorcycleAccess |
                                          TaxiAccess | BusAccess | HovAccess;

    // Maximum number of transit records per tile and other max. transit field values.
    public const uint MaxTransitDepartures = 16777215;
    public const uint MaxTransitStops = 65535;
    public const uint MaxTransitRoutes = 4095;
    public const uint MaxTransitSchedules = 4095;
    public const uint MaxTransitBlockId = 1048575;
    public const uint MaxTransitLineId = 1048575;
    public const uint MaxTransitDepartureTime = 131071;
    public const uint MaxTransitElapsedTime = 131071;
    public const uint MaxStartTime = 131071;
    public const uint MaxEndTime = 131071;
    public const uint MaxEndDay = 63;
    public const uint ScheduleEndDay = 60;
    public const uint MaxFrequency = 8191;
    public const uint MaxTransfers = 65535;
    public const uint MaxTransferTime = 65535;
    public const uint MaxTripId = 536870912; // 29 bits

    /// <summary>Maximum offset into the text/name list (24 bits). Mirrors C++ <c>kMaxNameOffset</c>.</summary>
    public const uint MaxNameOffset = 16777215;

    // Payment constants. Bit constants.
    public const byte Coins = 1; // Coins
    public const byte Notes = 2; // Bills
    public const byte Etc = 4;   // Electronic Toll Collector

    /// <summary>Maximum relative density at a node or within a tile. Mirrors C++ <c>kMaxDensity</c>.</summary>
    public const uint MaxDensity = 15;

    /// <summary>
    /// Unlimited speed limit. In OSM maxspeed=none. Set to max value to signify unlimited.
    /// Mirrors C++ <c>kUnlimitedSpeedLimit</c>.
    /// </summary>
    public const byte UnlimitedSpeedLimit = byte.MaxValue;

    /// <summary>The max assumed speed we know from static data (~85 MPH). Mirrors C++ <c>kMaxAssumedSpeed</c>.</summary>
    public const byte MaxAssumedSpeed = 140;

    /// <summary>Actual speed from traffic (~157 MPH). Mirrors C++ <c>kMaxTrafficSpeed</c>.</summary>
    public const byte MaxTrafficSpeed = 252;

    /// <summary>Maximum speed. std::max(kMaxTrafficSpeed, kMaxAssumedSpeed) == 252.</summary>
    public const uint MaxSpeedKph = MaxTrafficSpeed > MaxAssumedSpeed ? MaxTrafficSpeed : MaxAssumedSpeed;

    /// <summary>Max assumed truck speed (~75 MPH). Mirrors C++ <c>kMaxAssumedTruckSpeed</c>.</summary>
    public const uint MaxAssumedTruckSpeed = 120;

    /// <summary>Minimum speed. Stop gap for dubious traffic data. Mirrors C++ <c>kMinSpeedKph</c>.</summary>
    public const uint MinSpeedKph = 5; // ~3 MPH

    /// <summary>Mirrors C++ <c>kMinValidSpeedKph</c>.</summary>
    public const uint MinValidSpeedKph = 1;

    /// <summary>Default fixed speed (disabled). Mirrors C++ <c>kDisableFixedSpeed</c>.</summary>
    public const uint DisableFixedSpeed = 0;

    /// <summary>Faithful port of <c>valid_speed</c>.</summary>
    public static bool ValidSpeed(uint speed) => speed >= MinValidSpeedKph;

    /// <summary>Maximum ferry speed (21 knots). Mirrors C++ <c>kMaxFerrySpeedKph</c>.</summary>
    public const uint MaxFerrySpeedKph = 40;

    public const uint ParkingAisleSpeed = 15; // 15 KPH (10MPH)
    public const uint DriveThruSpeed = 10;    // 10 KPH
    public const uint DrivewaySpeed = 10;     // 10 KPH

    /// <summary>Maximum length in meters of an internal intersection edge. Mirrors C++ <c>kMaxInternalLength</c>.</summary>
    public const float MaxInternalLength = 32.0f;

    /// <summary>
    /// Maximum length in meters of a "link" that can be assigned use=kTurnChannel (vs. kRamp).
    /// Mirrors C++ <c>kMaxTurnChannelLength</c>.
    /// </summary>
    public const float MaxTurnChannelLength = 200.0f;

    // Bicycle Network constants. Bit constants.
    public const byte Ncn = 1; // Part of national bicycle network
    public const byte Rcn = 2; // Part of regional bicycle network
    public const byte Lcn = 4; // Part of local bicycle network
    public const byte Mcn = 8; // Part of mountain bicycle network
    public const byte MaxBicycleNetwork = 15;

    /// <summary>Maximum offset to edge information (2^25 bytes). Mirrors C++ <c>kMaxEdgeInfoOffset</c>.</summary>
    public const uint MaxEdgeInfoOffset = 33554431;

    /// <summary>Maximum length of an edge (2^24 meters). Mirrors C++ <c>kMaxEdgeLength</c>.</summary>
    public const uint MaxEdgeLength = 16777215;

    /// <summary>Maximum number of edges allowed in a turn restriction mask. Mirrors C++ <c>kMaxTurnRestrictionEdges</c>.</summary>
    public const uint MaxTurnRestrictionEdges = 8;

    /// <summary>Maximum lane count. Mirrors C++ <c>kMaxLaneCount</c>.</summary>
    public const uint MaxLaneCount = 15;

    /// <summary>Number of edges considered for edge transitions. Mirrors C++ <c>kNumberOfEdgeTransitions</c>.</summary>
    public const uint NumberOfEdgeTransitions = 8;

    /// <summary>
    /// Maximum shortcut edges from a node. More than this can be added but this is the max that can
    /// supersede an edge. Mirrors C++ <c>kMaxShortcutsFromNode</c>.
    /// </summary>
    public const uint MaxShortcutsFromNode = 7;

    /// <summary>Maximum stop impact. Mirrors C++ <c>kMaxStopImpact</c>.</summary>
    public const uint MaxStopImpact = 7;

    /// <summary>Maximum grade factor. Mirrors C++ <c>kMaxGradeFactor</c>.</summary>
    public const uint MaxGradeFactor = 15;

    /// <summary>Maximum curvature factor. Mirrors C++ <c>kMaxCurvatureFactor</c>.</summary>
    public const uint MaxCurvatureFactor = 15;

    /// <summary>Maximum added time along shortcuts to approximate transition costs. Mirrors C++ <c>kMaxAddedTime</c>.</summary>
    public const uint MaxAddedTime = 255;

    /// <summary>
    /// NO_DATA elevation value (the minimum we support; -500 m would result in "no elevation").
    /// Mirrors C++ <c>kNoElevationData</c>.
    /// </summary>
    public const float NoElevationData = -500.0f;

    public const uint DefaultIndoorSearchCutoff = 300;
    public const uint DefaultSearchCutoff = 35000;
    public const uint MaxIndoorSearchCutoff = 1000;

    /// <summary>(building) level sentinel: highest 3-byte value. Mirrors C++ <c>kLevelRangeSeparator</c>.</summary>
    public const float LevelRangeSeparator = 1048575.0f;

    /// <summary>Mirrors C++ <c>kMinLevel = std::numeric_limits&lt;float&gt;::min()</c> (smallest positive normal).</summary>
    public const float MinLevel = 1.17549435e-38f;

    /// <summary>Mirrors C++ <c>kMaxLevel = std::numeric_limits&lt;float&gt;::max()</c>.</summary>
    public const float MaxLevel = float.MaxValue;

    // ---- DirectedEdge / NodeInfo field limits surfaced for the DE port slice ----

    /// <summary>
    /// Maximum local edge index (the first 8 local edge indexes 0-7). Mirrors C++ <c>kMaxLocalEdgeIndex</c>.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: <c>kMaxLocalEdgeIndex</c> is declared in <c>valhalla/baldr/nodeinfo.h</c>
    /// (value 7), not graphconstants.h, but it is consumed by DirectedEdge so it is reproduced here.
    /// </remarks>
    public const uint MaxLocalEdgeIndex = 7;

    /// <summary>
    /// Maximum number of edges per node. Mirrors C++ <c>kMaxEdgesPerNode</c> (declared in
    /// <c>valhalla/baldr/nodeinfo.h</c>, value 127), consumed by DirectedEdge local index setters.
    /// </summary>
    public const uint MaxEdgesPerNode = 127;

    // Mountain bike scale
    public const uint MaxMtbScale = 6;
    public const uint MaxMtbUphillScale = 5;

    // ---- Access Restriction masks ----

    public const uint HazmatMask = 1;
    public const uint MaxHeightMask = 2;
    public const uint MaxWidthMask = 4;
    public const uint MaxLengthMask = 8;
    public const uint MaxWeightMask = 16;
    public const uint MaxAxleLoadMask = 32;
    public const uint MaxAxlesMask = 64;

    /// <summary>Faithful port of <c>kAccessRestrictionMasks</c> (populate_access_restriction_masks).</summary>
    public static readonly byte[] AccessRestrictionMasks = PopulateAccessRestrictionMasks();

    /// <summary>Mirrors C++ <c>kInvalidAccessRestrictionMask</c>.</summary>
    public const byte InvalidAccessRestrictionMask = byte.MaxValue;

    private static byte[] PopulateAccessRestrictionMasks()
    {
        var masks = new byte[32];
        masks[0] = (byte)HazmatMask;
        masks[1] = (byte)MaxHeightMask;
        masks[2] = (byte)MaxWidthMask;
        masks[3] = (byte)MaxLengthMask;
        masks[4] = (byte)MaxWeightMask;
        masks[5] = (byte)MaxAxleLoadMask;
        masks[9] = (byte)MaxAxlesMask;
        return masks;
    }

    /// <summary>Minimum meters offset from start/end of shape for finding heading. Mirrors C++ <c>kMinMetersOffsetForHeading</c>.</summary>
    public const float MinMetersOffsetForHeading = 15.0f;

    /// <summary>Faithful port of <c>GetOffsetForHeading(RoadClass, Use)</c>.</summary>
    public static float GetOffsetForHeading(RoadClass roadClass, Use use)
    {
        byte rc = (byte)roadClass;
        float offset = MinMetersOffsetForHeading;
        // Adjust offset based on road class
        if (rc < 2)
        {
            offset *= 1.6f;
        }
        else if (rc < 5)
        {
            offset *= 1.4f;
        }

        // Adjust offset based on use
        switch (use)
        {
            case Use.Cycleway:
            case Use.MountainBike:
            case Use.Footway:
            case Use.Steps:
            case Use.Path:
            case Use.Pedestrian:
            case Use.Bridleway:
                offset *= 0.5f;
                break;
            default:
                break;
        }

        return offset;
    }

    // ------------------------------- Transit information --------------------- //

    public const uint OneStopIdSize = 256;

    /// <summary>Pivot date for transit. No dates will be older than this date. Mirrors C++ <c>kPivotDate</c>.</summary>
    public const string PivotDate = "2014-01-01"; // January 1, 2014

    // Used for day of week mask.
    public const byte DowNone = 0;
    public const byte Sunday = 1;
    public const byte Monday = 2;
    public const byte Tuesday = 4;
    public const byte Wednesday = 8;
    public const byte Thursday = 16;
    public const byte Friday = 32;
    public const byte Saturday = 64;
    public const byte AllDaysOfWeek = 127;

    // --------------------- Traffic information ------------------------ //

    public const byte NoFlowMask = 0;
    public const byte FreeFlowMask = 1;
    public const byte ConstrainedFlowMask = 2;
    public const byte PredictedFlowMask = 4;
    public const byte CurrentFlowMask = 8;
    public const byte DefaultFlowMask =
        FreeFlowMask | ConstrainedFlowMask | PredictedFlowMask | CurrentFlowMask;
    public const uint FreeFlowSecondOfDay = 60 * 60 * 0;         // midnight
    public const uint ConstrainedFlowSecondOfDay = 60 * 60 * 12; // noon
    public const ulong InvalidSecondsOfWeek = 1048575; // invalid (20 bits - 1)

    // ----------------------- enum <-> string maps ---------------------------- //

    /// <summary>Faithful port of <c>stringToRoadClass</c>. Throws if the key is unknown
    /// (matches C++ <c>find(s)->second</c> which dereferences end() on a miss).</summary>
    public static RoadClass StringToRoadClass(string s)
    {
        return s switch
        {
            "Motorway" => RoadClass.Motorway,
            "Trunk" => RoadClass.Trunk,
            "Primary" => RoadClass.Primary,
            "Secondary" => RoadClass.Secondary,
            "Tertiary" => RoadClass.Tertiary,
            "Unclassified" => RoadClass.Unclassified,
            "Residential" => RoadClass.Residential,
            "ServiceOther" => RoadClass.ServiceOther,
            _ => throw new KeyNotFoundException($"Unknown RoadClass string: {s}"),
        };
    }

    public static string ToStringValue(RoadClass r) => r switch
    {
        RoadClass.Motorway => "motorway",
        RoadClass.Trunk => "trunk",
        RoadClass.Primary => "primary",
        RoadClass.Secondary => "secondary",
        RoadClass.Tertiary => "tertiary",
        RoadClass.Unclassified => "unclassified",
        RoadClass.Residential => "residential",
        RoadClass.ServiceOther => "service_other",
        _ => "null",
    };

    public static string ToStringValue(NodeType n) => n switch
    {
        NodeType.StreetIntersection => "street_intersection",
        NodeType.Gate => "gate",
        NodeType.Bollard => "bollard",
        NodeType.TollBooth => "toll_booth",
        NodeType.TransitEgress => "transit_egress",
        NodeType.TransitStation => "transit_station",
        NodeType.MultiUseTransitPlatform => "multi_use_transit_platform",
        NodeType.BikeShare => "bike_share",
        NodeType.Parking => "parking",
        NodeType.MotorWayJunction => "motor_way_junction",
        NodeType.BorderControl => "border_control",
        NodeType.TollGantry => "toll_gantry",
        NodeType.SumpBuster => "sump_buster",
        NodeType.BuildingEntrance => "building_entrance",
        NodeType.Elevator => "elevator",
        _ => "null",
    };

    public static string ToStringValue(IntersectionType x) => x switch
    {
        IntersectionType.Regular => "regular",
        IntersectionType.False => "false",
        IntersectionType.DeadEnd => "dead-end",
        IntersectionType.Fork => "fork",
        _ => "null",
    };

    public static string ToStringValue(Use u) => u switch
    {
        Use.Road => "road",
        Use.Ramp => "ramp",
        Use.TurnChannel => "turn_channel",
        Use.Track => "track",
        Use.Driveway => "driveway",
        Use.Alley => "alley",
        Use.ParkingAisle => "parking_aisle",
        Use.EmergencyAccess => "emergency_access",
        Use.DriveThru => "drive_through",
        Use.Culdesac => "culdesac",
        Use.LivingStreet => "living_street",
        Use.ServiceRoad => "service_road",
        Use.Cycleway => "cycleway",
        Use.MountainBike => "mountain_bike",
        Use.Sidewalk => "sidewalk",
        Use.Footway => "footway",
        Use.Elevator => "elevator",
        Use.Steps => "steps",
        Use.Escalator => "escalator",
        Use.Path => "path",
        Use.Pedestrian => "pedestrian",
        Use.Platform => "platform",
        Use.Bridleway => "bridleway",
        Use.PedestrianCrossing => "pedestrian_crossing",
        Use.RestArea => "rest_area",
        Use.ServiceArea => "service_area",
        Use.Other => "other",
        Use.RailFerry => "rail-ferry",
        Use.Ferry => "ferry",
        Use.Rail => "rail",
        Use.Bus => "bus",
        Use.EgressConnection => "egress_connection",
        Use.PlatformConnection => "platform_connection",
        Use.TransitConnection => "transit_connection",
        Use.Construction => "construction",
        _ => "null",
    };

    public static string ToStringValue(SpeedType s) => s switch
    {
        SpeedType.Tagged => "tagged",
        SpeedType.Classified => "classified",
        _ => "null",
    };

    public static string ToStringValue(CycleLane c) => c switch
    {
        CycleLane.None => "none",
        CycleLane.Shared => "shared",
        CycleLane.Dedicated => "dedicated",
        CycleLane.Separated => "separated",
        _ => "null",
    };

    public static string ToStringValue(SacScale c) => c switch
    {
        SacScale.None => "none",
        SacScale.Hiking => "hiking",
        SacScale.MountainHiking => "mountain hiking",
        SacScale.DemandingMountainHiking => "demanding mountain hiking",
        SacScale.AlpineHiking => "alpine hiking",
        SacScale.DemandingAlpineHiking => "demanding alpine hiking",
        SacScale.DifficultAlpineHiking => "difficult alpine hiking",
        _ => "null",
    };

    public static string ToStringValue(Surface s) => s switch
    {
        Surface.PavedSmooth => "paved_smooth",
        Surface.Paved => "paved",
        Surface.PavedRough => "paved_rough",
        Surface.Compacted => "compacted",
        Surface.Dirt => "dirt",
        Surface.Gravel => "gravel",
        Surface.Path => "path",
        Surface.Impassable => "impassable",
        _ => "null",
    };

    public static string ToStringValue(HovEdgeType h) => h switch
    {
        HovEdgeType.Hov2 => "HOV-2",
        HovEdgeType.Hov3 => "HOV-3",
        _ => "null",
    };

    /// <summary>Faithful port of <c>stringLanguage</c>. Returns <see cref="Language.None"/>
    /// for an unknown key (matches C++ behaviour).</summary>
    public static Language StringLanguage(string s) => s switch
    {
        "ab" => Language.Ab, "am" => Language.Am, "ar" => Language.Ar, "az" => Language.Az,
        "be" => Language.Be, "bg" => Language.Bg, "bn" => Language.Bn, "bs" => Language.Bs,
        "ca" => Language.Ca, "ckb" => Language.Ckb, "cs" => Language.Cs, "da" => Language.Da,
        "de" => Language.De, "dv" => Language.Dv, "dz" => Language.Dz, "el" => Language.El,
        "en" => Language.En, "es" => Language.Es, "et" => Language.Et, "fa" => Language.Fa,
        "fi" => Language.Fi, "fr" => Language.Fr, "fy" => Language.Fy, "gl" => Language.Gl,
        "he" => Language.He, "hr" => Language.Hr, "hu" => Language.Hu, "hy" => Language.Hy,
        "id" => Language.Id, "is" => Language.Is, "it" => Language.It, "ja" => Language.Ja,
        "ka" => Language.Ka, "kl" => Language.Kl, "km" => Language.Km, "ko" => Language.Ko,
        "lo" => Language.Lo, "lt" => Language.Lt, "lv" => Language.Lv, "mg" => Language.Mg,
        "mk" => Language.Mk, "mn" => Language.Mn, "mo" => Language.Mo, "mt" => Language.Mt,
        "my" => Language.My, "ne" => Language.Ne, "nl" => Language.Nl, "no" => Language.No,
        "oc" => Language.Oc, "pap" => Language.Pap, "pl" => Language.Pl, "ps" => Language.Ps,
        "pt" => Language.Pt, "rm" => Language.Rm, "ro" => Language.Ro, "ru" => Language.Ru,
        "sk" => Language.Sk, "sl" => Language.Sl, "sq" => Language.Sq, "sr" => Language.Sr,
        "sr-Latn" => Language.SrLatn, "sv" => Language.Sv, "tg" => Language.Tg, "th" => Language.Th,
        "tk" => Language.Tk, "tr" => Language.Tr, "uk" => Language.Uk, "ur" => Language.Ur,
        "uz" => Language.Uz, "vi" => Language.Vi, "zh" => Language.Zh, "cy" => Language.Cy,
        "ta" => Language.Ta, "ms" => Language.Ms, "none" => Language.None,
        _ => Language.None,
    };

    public static string ToStringValue(Language l) => l switch
    {
        Language.Ab => "ab", Language.Am => "am", Language.Ar => "ar", Language.Az => "az",
        Language.Be => "be", Language.Bg => "bg", Language.Bn => "bn", Language.Bs => "bs",
        Language.Ca => "ca", Language.Ckb => "ckb", Language.Cs => "cs", Language.Da => "da",
        Language.De => "de", Language.Dv => "dv", Language.Dz => "dz", Language.El => "el",
        Language.En => "en", Language.Es => "es", Language.Et => "et", Language.Fa => "fa",
        Language.Fi => "fi", Language.Fr => "fr", Language.Fy => "fy", Language.Gl => "gl",
        Language.He => "he", Language.Hr => "hr", Language.Hu => "hu", Language.Hy => "hy",
        Language.Id => "id", Language.Is => "is", Language.It => "it", Language.Ja => "ja",
        Language.Ka => "ka", Language.Kl => "kl", Language.Km => "km", Language.Ko => "ko",
        Language.Lo => "lo", Language.Lt => "lt", Language.Lv => "lv", Language.Mg => "mg",
        Language.Mk => "mk", Language.Mn => "mn", Language.Mo => "mo", Language.Mt => "mt",
        Language.My => "my", Language.Ne => "ne", Language.Nl => "nl", Language.No => "no",
        Language.Oc => "oc", Language.Pap => "pap", Language.Pl => "pl", Language.Ps => "ps",
        Language.Pt => "pt", Language.Rm => "rm", Language.Ro => "ro", Language.Ru => "ru",
        Language.Sk => "sk", Language.Sl => "sl", Language.Sq => "sq", Language.Sr => "sr",
        Language.SrLatn => "sr-Latn", Language.Sv => "sv", Language.Tg => "tg", Language.Th => "th",
        Language.Tk => "tk", Language.Tr => "tr", Language.Uk => "uk", Language.Ur => "ur",
        Language.Uz => "uz", Language.Vi => "vi", Language.Zh => "zh", Language.Cy => "cy",
        Language.Ta => "ta", Language.Ms => "ms", Language.None => "none",
        _ => "none",
    };
}

/// <summary>Edge traversability. C++ <c>enum class Traversability</c>.</summary>
public enum Traversability : byte
{
    None = 0,     // Edge is not traversable in either direction
    Forward = 1,  // Edge is traversable in the forward direction
    Backward = 2, // Edge is traversable in the backward direction
    Both = 3,     // Edge is traversable in both directions
}

/// <summary>Road class or importance of an edge. C++ <c>enum class RoadClass : uint8_t</c>.</summary>
public enum RoadClass : byte
{
    Motorway = 0,
    Trunk = 1,
    Primary = 2,
    Secondary = 3,
    Tertiary = 4,
    Unclassified = 5,
    Residential = 6,
    ServiceOther = 7,
    Invalid = 8, // only 3 bits in DE for road class
}

/// <summary>Node types. C++ <c>enum class NodeType : uint8_t</c>.</summary>
public enum NodeType : byte
{
    StreetIntersection = 0,      // Regular intersection of 2 roads
    Gate = 1,                    // Gate or rising bollard
    Bollard = 2,                 // Bollard (fixed obstruction)
    TollBooth = 3,               // Toll booth / fare collection
    TransitEgress = 4,           // Transit egress
    TransitStation = 5,          // Transit station
    MultiUseTransitPlatform = 6, // Multi-use transit platform (rail and bus)
    BikeShare = 7,               // Bike share location
    Parking = 8,                 // Parking location
    MotorWayJunction = 9,        // Highway = motorway_junction
    BorderControl = 10,          // Border control
    TollGantry = 11,             // Toll gantry
    SumpBuster = 12,             // Sump Buster
    BuildingEntrance = 13,       // Building entrance
    Elevator = 14,               // Elevator
}

/// <summary>Intersection types. C++ <c>enum class IntersectionType : uint8_t</c>. Max value 15.</summary>
public enum IntersectionType : byte
{
    Regular = 0, // Regular, unclassified intersection
    False = 1,   // False intersection. Only 2 edges connect.
    DeadEnd = 2, // Node only connects to one edge ("dead-end").
    Fork = 3,    // All edges are links OR all edges are not links and node is a motorway_junction.
}

/// <summary>Edge use. C++ <c>enum class Use : uint8_t</c>. Max value for a directed edge is 63.</summary>
public enum Use : byte
{
    // Road specific uses
    Road = 0,
    Ramp = 1,            // Link - exits/entrance ramps.
    TurnChannel = 2,     // Link - turn lane.
    Track = 3,           // Agricultural use, forest tracks
    Driveway = 4,        // Driveway/private service
    Alley = 5,           // Service road - limited route use
    ParkingAisle = 6,    // Access roads in parking areas
    EmergencyAccess = 7, // Emergency vehicles only
    DriveThru = 8,       // Commercial drive-thru (banks/fast-food)
    Culdesac = 9,        // Cul-de-sac
    LivingStreet = 10,   // Streets with preference towards bicyclists and pedestrians
    ServiceRoad = 11,    // Generic service road

    // Bicycle specific uses
    Cycleway = 20,     // Dedicated bicycle path
    MountainBike = 21, // Mountain bike trail

    Sidewalk = 24,

    // Pedestrian specific uses
    Footway = 25,
    Steps = 26, // Stairs
    Path = 27,
    Pedestrian = 28,
    Bridleway = 29,
    PedestrianCrossing = 32, // cross walks
    Elevator = 33,
    Escalator = 34,
    Platform = 35,

    // Rest/Service Areas
    RestArea = 30,
    ServiceArea = 31,

    // Other... currently, either BSS Connection or unspecified service road
    Other = 40,

    // Ferry and rail ferry
    Ferry = 41,
    RailFerry = 42,

    Construction = 43, // Road under construction

    // Transit specific uses. Must be last in the list
    Rail = 50,               // Rail line
    Bus = 51,                // Bus line
    EgressConnection = 52,   // Connection egress <-> station
    PlatformConnection = 53, // Connection station <-> platform
    TransitConnection = 54,  // Connection osm <-> egress

    Size = 64,
}

/// <summary>
/// Tagged value tag bytes stored as the first character of a tagged name entry in
/// the tile text/names list. Faithful port of C++ <c>enum class TaggedValue : uint8_t</c>
/// from <c>valhalla/baldr/graphconstants.h</c>. Must start at 1 due to nulls.
/// </summary>
public enum TaggedValue : byte
{
    /// <summary>Layer index (Z-level). One signed byte after the tag.</summary>
    Layer = 1,

    /// <summary>Linguistic (pronunciation / language) records. Handled separately from other tags.</summary>
    Linguistic = 2,

    /// <summary>Bike share station info.</summary>
    BssInfo = 3,

    /// <summary>Single building level (deprecated in favor of <see cref="Levels"/>).</summary>
    Level = 4,

    /// <summary>Level reference string (level:ref).</summary>
    LevelRef = 5,

    /// <summary>Landmark record (fixed 9-byte header + null-terminated name).</summary>
    Landmark = 6,

    /// <summary>Conditional speed limits (TimeDomain + speed).</summary>
    ConditionalSpeedLimits = 7,

    /// <summary>Encoded building levels (varint size + varint precision + values).</summary>
    Levels = 8,

    /// <summary>Encoded OSM node ids (varint size + delta-encoded ids).</summary>
    OSMNodeIds = 9,

    // we used to have a bug when we encoded 1 and 2 as their ASCII codes, but not actual 1 and 2
    // values. See https://github.com/valhalla/valhalla/issues/3262

    /// <summary>Tunnel name. Tag byte is the ASCII code for '1'.</summary>
    Tunnel = (byte)'1',

    /// <summary>Bridge name. Tag byte is the ASCII code for '2'.</summary>
    Bridge = (byte)'2',
}

/// <summary>
/// Pronunciation alphabet. Faithful port of C++ <c>enum class PronunciationAlphabet : uint8_t</c>.
/// </summary>
/// <remarks>
/// kNone = 0 has been deprecated as this introduced a bug while processing the linguistic
/// records, so <see cref="None"/> is intentionally 5 (not 0).
/// </remarks>
public enum PronunciationAlphabet : byte
{
    /// <summary>International Phonetic Alphabet.</summary>
    Ipa = 1,

    /// <summary>Katakana.</summary>
    Katakana = 2,

    /// <summary>JEITA.</summary>
    Jeita = 3,

    /// <summary>NT-SAMPA.</summary>
    NtSampa = 4,

    /// <summary>No phonetic alphabet (deliberately 5, not 0; see remarks).</summary>
    None = 5,
}

/// <summary>C++ <c>enum class Language : uint8_t</c>. Must start at 1 due to nulls.</summary>
public enum Language : byte
{
    Ab = 1, Am = 2, Ar = 3, Az = 4, Be = 5, Bg = 6, Bn = 7, Bs = 8, Ca = 9, Ckb = 10,
    Cs = 11, Da = 12, De = 13, Dv = 14, Dz = 15, El = 16, En = 17, Es = 18, Et = 19, Fa = 20,
    Fi = 21, Fr = 22, Fy = 23, Gl = 24, He = 25, Hr = 26, Hu = 27, Hy = 28, Id = 29, Is = 30,
    It = 31, Ja = 32, Ka = 33, Kl = 34, Km = 35, Ko = 36, Lo = 37, Lt = 38, Lv = 39, Mg = 40,
    Mk = 41, Mn = 42, Mo = 43, Mt = 44, My = 45, Ne = 46, Nl = 47, No = 48, Oc = 49, Pap = 50,
    Pl = 51, Ps = 52, Pt = 53, Rm = 54, Ro = 55, Ru = 56, Sk = 57, Sl = 58, Sq = 59, Sr = 60,
    SrLatn = 61, Sv = 62, Tg = 63, Th = 64, Tk = 65, Tr = 66, Uk = 67, Ur = 68, Uz = 69, Vi = 70,
    Zh = 71, Cy = 72, Ta = 73, Ms = 74, None = 255,
}

/// <summary>Speed type. C++ <c>enum class SpeedType : uint8_t</c>.</summary>
public enum SpeedType : byte
{
    Tagged = 0,     // Tagged maximum speed
    Classified = 1, // Speed assigned based on highway classification
}

/// <summary>Cycle lane type. C++ <c>enum class CycleLane : uint8_t</c>.</summary>
public enum CycleLane : byte
{
    None = 0,      // No specified bicycle lane
    Shared = 1,    // Shared use lane
    Dedicated = 2, // Dedicated cycle lane
    Separated = 3, // A separate cycle lane (physical separation)
}

/// <summary>C++ <c>enum class SacScale : uint8_t</c>.</summary>
public enum SacScale : byte
{
    None = 0,
    Hiking = 1,
    MountainHiking = 2,
    DemandingMountainHiking = 3,
    AlpineHiking = 4,
    DemandingAlpineHiking = 5,
    DifficultAlpineHiking = 6,
}

/// <summary>Generalized representation of surface types. C++ <c>enum class Surface : uint8_t</c>.</summary>
public enum Surface : byte
{
    PavedSmooth = 0,
    Paved = 1,
    PavedRough = 2,
    Compacted = 3,
    Dirt = 4,
    Gravel = 5,
    Path = 6,
    Impassable = 7,
}

/// <summary>Restriction start/end day. C++ <c>enum class DOW : uint8_t</c>.</summary>
public enum Dow : byte
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 3,
    Wednesday = 4,
    Thursday = 5,
    Friday = 6,
    Saturday = 7,
}

/// <summary>Restriction start/end month. C++ <c>enum class MONTH : uint8_t</c>.</summary>
public enum Month : byte
{
    None = 0,
    Jan = 1,
    Feb = 2,
    Mar = 3,
    Apr = 4,
    May = 5,
    Jun = 6,
    Jul = 7,
    Aug = 8,
    Sep = 9,
    Oct = 10,
    Nov = 11,
    Dec = 12,
}

/// <summary>Types of transit currently supported. C++ <c>enum class TransitType : uint8_t</c>.</summary>
public enum TransitType : byte
{
    Tram = 0,
    Metro = 1,
    Rail = 2,
    Bus = 3,
    Ferry = 4,
    CableCar = 5,
    Gondola = 6,
    Funicular = 7,
}

/// <summary>Restriction types. C++ <c>enum class RestrictionType : uint8_t</c>.</summary>
public enum RestrictionType : byte
{
    NoLeftTurn = 0,
    NoRightTurn = 1,
    NoStraightOn = 2,
    NoUTurn = 3,
    OnlyRightTurn = 4,
    OnlyLeftTurn = 5,
    OnlyStraightOn = 6,
    NoEntry = 7,
    NoExit = 8,
    NoTurn = 9,
    OnlyProbable = 10,
    NoProbable = 11,
}

/// <summary>Access Restriction types. C++ <c>enum class AccessType : uint8_t</c>. Max value 31.</summary>
public enum AccessType : byte
{
    Hazmat = 0,
    MaxHeight = 1,
    MaxWidth = 2,
    MaxLength = 3,
    MaxWeight = 4,
    MaxAxleLoad = 5,
    TimedAllowed = 6,
    TimedDenied = 7,
    DestinationAllowed = 8,
    MaxAxles = 9,
}

/// <summary>Transit transfer types. C++ <c>enum class TransferType : uint8_t</c>.</summary>
public enum TransferType : byte
{
    Recommended = 0, // Recommended transfer point between 2 routes
    Timed = 1,       // Timed transfer between 2 routes.
    MinTime = 2,     // Transfer is expected to take the time specified.
    NotPossible = 3, // Transfers not possible between routes
}

/// <summary>C++ <c>enum class CalendarExceptionType : uint8_t</c>.</summary>
public enum CalendarExceptionType : byte
{
    Added = 1,   // Service added for the specified date
    Removed = 2, // Service removed for the specified date
}

/// <summary>HOV edge type. C++ <c>enum class HOVEdgeType : uint8_t</c>. Only 1 bit, do not exceed 1.</summary>
public enum HovEdgeType : byte
{
    Hov2 = 0,
    Hov3 = 1,
}
