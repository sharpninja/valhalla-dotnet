// Faithful C# port of Valhalla mjolnir OSMWay.
// Source: valhalla/mjolnir/osmway.h + src/mjolnir/osmway.cc @ 3.7.0
//
// OSMWay is the normalized result of parsing an OSM way (after the graph.lua tag
// transform). It carries access flags (per mode, per direction), classification
// (road class, use, link), speeds (incl. truck + forward/backward), lanes,
// surface/cyclelane/shoulder attributes, name/ref/destination string indices, and
// the truck/HGV + ferry + roundabout + oneway + bike-network flags.
//
// This port preserves the exact bit-field widths and the exact clamping logic in the
// .cc setters (set_node_count, set_speed*, set_lanes*). The large linguistic name
// subsystem (GetNames / AddPronunciations / GetTaggedValues) from osmway.cc depends
// on UniqueNames + OSMLinguistic + the tile linguistic header and lives in the graph
// builder slice; it is intentionally out of scope for the OSM front-end parser port.

using System;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// OSM way. Faithful port of the C++ <c>struct OSMWay</c> from
/// <c>valhalla/mjolnir/osmway.h</c> and <c>src/mjolnir/osmway.cc</c>.
/// </summary>
public sealed class OSMWay
{
    // Clamping constants from osmway.cc (anonymous namespace).
    private const uint MaxNodesPerWay = 65535;
    private const byte UnlimitedOSMSpeed = byte.MaxValue;
    private const float MaxOSMSpeed = 140.0f;

    private const uint MaxLaneCount = GraphConstants.MaxLaneCount;

    /// <summary>Constructs an empty way (all fields zeroed), matching <c>OSMWay()</c>.</summary>
    public OSMWay()
    {
    }

    /// <summary>Constructs a way with the given OSM way id, matching <c>OSMWay(uint64_t)</c>.</summary>
    public OSMWay(ulong id)
    {
        OsmWayId = id;
    }

    // ---- Identity -------------------------------------------------------------

    /// <summary>OSM way id (public field analogue of C++ <c>osmwayid_</c>).</summary>
    public ulong OsmWayId { get; set; }

    /// <summary>Sets the way id.</summary>
    public void SetWayId(ulong id) => OsmWayId = id;

    /// <summary>Gets the way id.</summary>
    public ulong WayId() => OsmWayId;

    // ---- Node count -----------------------------------------------------------

    private ushort _nodeCount;

    /// <summary>
    /// Sets the number of nodes for this way, clamped to <see cref="MaxNodesPerWay"/>
    /// (faithful to <c>OSMWay::set_node_count</c>).
    /// </summary>
    public void SetNodeCount(uint count) =>
        _nodeCount = count > MaxNodesPerWay ? (ushort)MaxNodesPerWay : (ushort)count;

    /// <summary>Gets the number of nodes for this way.</summary>
    public uint NodeCount() => _nodeCount;

    // ---- Speeds (KPH, rounded with +0.5 then truncated, clamped to 140) -------

    private byte _speed;
    private byte _speedLimit;
    private byte _backwardSpeed;
    private byte _forwardSpeed;
    private byte _truckSpeed;
    private byte _truckSpeedForward;
    private byte _truckSpeedBackward;

    private static byte ClampSpeed(float speed) =>
        speed > MaxOSMSpeed ? (byte)MaxOSMSpeed : (byte)(speed + 0.5f);

    /// <summary>Sets the speed in KPH (faithful to <c>set_speed</c>).</summary>
    public void SetSpeed(float speed) => _speed = ClampSpeed(speed);

    /// <summary>Gets the speed in KPH.</summary>
    public byte Speed() => _speed;

    /// <summary>
    /// Sets the speed limit in KPH. Preserves the special unlimited value and the
    /// max-speed clamp (faithful to <c>set_speed_limit</c>).
    /// </summary>
    public void SetSpeedLimit(float speedLimit)
    {
        if (speedLimit == UnlimitedOSMSpeed)
        {
            _speedLimit = UnlimitedOSMSpeed;
        }
        else if (speedLimit > MaxOSMSpeed)
        {
            _speedLimit = (byte)MaxOSMSpeed;
        }
        else
        {
            _speedLimit = (byte)(speedLimit + 0.5f);
        }
    }

    /// <summary>Gets the speed limit in KPH.</summary>
    public byte SpeedLimit() => _speedLimit;

    /// <summary>Sets the backward speed in KPH.</summary>
    public void SetBackwardSpeed(float backwardSpeed) => _backwardSpeed = ClampSpeed(backwardSpeed);

    /// <summary>Gets the backward speed in KPH.</summary>
    public byte BackwardSpeed() => _backwardSpeed;

    /// <summary>Sets the forward speed in KPH.</summary>
    public void SetForwardSpeed(float forwardSpeed) => _forwardSpeed = ClampSpeed(forwardSpeed);

    /// <summary>Gets the forward speed in KPH.</summary>
    public byte ForwardSpeed() => _forwardSpeed;

    /// <summary>Sets the truck speed in KPH.</summary>
    public void SetTruckSpeed(float truckSpeed) => _truckSpeed = ClampSpeed(truckSpeed);

    /// <summary>Gets the truck speed in KPH.</summary>
    public byte TruckSpeed() => _truckSpeed;

    /// <summary>Sets the forward truck speed in KPH.</summary>
    public void SetTruckSpeedForward(float v) => _truckSpeedForward = ClampSpeed(v);

    /// <summary>Gets the forward truck speed in KPH.</summary>
    public byte TruckSpeedForward() => _truckSpeedForward;

    /// <summary>Sets the backward truck speed in KPH.</summary>
    public void SetTruckSpeedBackward(float v) => _truckSpeedBackward = ClampSpeed(v);

    /// <summary>Gets the backward truck speed in KPH.</summary>
    public byte TruckSpeedBackward() => _truckSpeedBackward;

    // ---- Lanes (clamped to kMaxLaneCount = 15) --------------------------------

    private uint _lanes;
    private uint _forwardLanes;
    private uint _backwardLanes;

    /// <summary>Sets the number of lanes (clamped to <see cref="MaxLaneCount"/>).</summary>
    public void SetLanes(uint lanes) => _lanes = lanes > MaxLaneCount ? MaxLaneCount : lanes;

    /// <summary>Gets the number of lanes.</summary>
    public uint Lanes() => _lanes;

    /// <summary>Sets the number of backward lanes (clamped to <see cref="MaxLaneCount"/>).</summary>
    public void SetBackwardLanes(uint v) => _backwardLanes = v > MaxLaneCount ? MaxLaneCount : v;

    /// <summary>Gets the number of backward lanes.</summary>
    public uint BackwardLanes() => _backwardLanes;

    /// <summary>Sets the number of forward lanes (clamped to <see cref="MaxLaneCount"/>).</summary>
    public void SetForwardLanes(uint v) => _forwardLanes = v > MaxLaneCount ? MaxLaneCount : v;

    /// <summary>Gets the number of forward lanes.</summary>
    public uint ForwardLanes() => _forwardLanes;

    // ---- Ferry duration -------------------------------------------------------

    /// <summary>Ferry crossing duration in seconds.</summary>
    public uint Duration { get; set; }

    /// <summary>Sets the ferry duration in seconds.</summary>
    public void SetDuration(uint duration) => Duration = duration;

    // ---- Per-mode, per-direction access flags ---------------------------------

    /// <summary>Auto allowed in the forward direction?</summary>
    public bool AutoForwardValue { get; set; }

    public void SetAutoForward(bool v) => AutoForwardValue = v;

    public bool AutoForward() => AutoForwardValue;

    public bool BusForwardValue { get; set; }

    public void SetBusForward(bool v) => BusForwardValue = v;

    public bool BusForward() => BusForwardValue;

    public bool TaxiForwardValue { get; set; }

    public void SetTaxiForward(bool v) => TaxiForwardValue = v;

    public bool TaxiForward() => TaxiForwardValue;

    public bool HovForwardValue { get; set; }

    public void SetHovForward(bool v) => HovForwardValue = v;

    public bool HovForward() => HovForwardValue;

    public bool TruckForwardValue { get; set; }

    public void SetTruckForward(bool v) => TruckForwardValue = v;

    public bool TruckForward() => TruckForwardValue;

    public bool BikeForwardValue { get; set; }

    public void SetBikeForward(bool v) => BikeForwardValue = v;

    public bool BikeForward() => BikeForwardValue;

    public bool EmergencyForwardValue { get; set; }

    public void SetEmergencyForward(bool v) => EmergencyForwardValue = v;

    public bool EmergencyForward() => EmergencyForwardValue;

    public bool MopedForwardValue { get; set; }

    public void SetMopedForward(bool v) => MopedForwardValue = v;

    public bool MopedForward() => MopedForwardValue;

    public bool MotorcycleForwardValue { get; set; }

    public void SetMotorcycleForward(bool v) => MotorcycleForwardValue = v;

    public bool MotorcycleForward() => MotorcycleForwardValue;

    public bool PedestrianForwardValue { get; set; }

    public void SetPedestrianForward(bool v) => PedestrianForwardValue = v;

    public bool PedestrianForward() => PedestrianForwardValue;

    public bool AutoBackwardValue { get; set; }

    public void SetAutoBackward(bool v) => AutoBackwardValue = v;

    public bool AutoBackward() => AutoBackwardValue;

    public bool BusBackwardValue { get; set; }

    public void SetBusBackward(bool v) => BusBackwardValue = v;

    public bool BusBackward() => BusBackwardValue;

    public bool TaxiBackwardValue { get; set; }

    public void SetTaxiBackward(bool v) => TaxiBackwardValue = v;

    public bool TaxiBackward() => TaxiBackwardValue;

    public bool HovBackwardValue { get; set; }

    public void SetHovBackward(bool v) => HovBackwardValue = v;

    public bool HovBackward() => HovBackwardValue;

    public bool TruckBackwardValue { get; set; }

    public void SetTruckBackward(bool v) => TruckBackwardValue = v;

    public bool TruckBackward() => TruckBackwardValue;

    public bool BikeBackwardValue { get; set; }

    public void SetBikeBackward(bool v) => BikeBackwardValue = v;

    public bool BikeBackward() => BikeBackwardValue;

    public bool EmergencyBackwardValue { get; set; }

    public void SetEmergencyBackward(bool v) => EmergencyBackwardValue = v;

    public bool EmergencyBackward() => EmergencyBackwardValue;

    public bool MopedBackwardValue { get; set; }

    public void SetMopedBackward(bool v) => MopedBackwardValue = v;

    public bool MopedBackward() => MopedBackwardValue;

    public bool MotorcycleBackwardValue { get; set; }

    public void SetMotorcycleBackward(bool v) => MotorcycleBackwardValue = v;

    public bool MotorcycleBackward() => MotorcycleBackwardValue;

    public bool PedestrianBackwardValue { get; set; }

    public void SetPedestrianBackward(bool v) => PedestrianBackwardValue = v;

    public bool PedestrianBackward() => PedestrianBackwardValue;

    // ---- Way attribute flags --------------------------------------------------

    public bool DestinationOnlyValue { get; set; }

    public void SetDestinationOnly(bool v) => DestinationOnlyValue = v;

    public bool DestinationOnly() => DestinationOnlyValue;

    public bool DestinationOnlyHgvValue { get; set; }

    public void SetDestinationOnlyHgv(bool v) => DestinationOnlyHgvValue = v;

    public bool DestinationOnlyHgv() => DestinationOnlyHgvValue;

    public bool HasUserTagsValue { get; set; }

    public void SetHasUserTags(bool v) => HasUserTagsValue = v;

    public bool HasUserTags() => HasUserTagsValue;

    public bool HasPronunciationTagsValue { get; set; }

    public void SetHasPronunciationTags(bool v) => HasPronunciationTagsValue = v;

    public bool HasPronunciationTags() => HasPronunciationTagsValue;

    public bool InternalValue { get; set; }

    public void SetInternal(bool v) => InternalValue = v;

    public bool Internal() => InternalValue;

    public bool NoThruTrafficValue { get; set; }

    public void SetNoThruTraffic(bool v) => NoThruTrafficValue = v;

    public bool NoThruTraffic() => NoThruTrafficValue;

    public bool OnewayValue { get; set; }

    public void SetOneway(bool v) => OnewayValue = v;

    public bool Oneway() => OnewayValue;

    public bool OnewayReverseValue { get; set; }

    public void SetOnewayReverse(bool v) => OnewayReverseValue = v;

    public bool OnewayReverse() => OnewayReverseValue;

    public bool RoundaboutValue { get; set; }

    public void SetRoundabout(bool v) => RoundaboutValue = v;

    public bool Roundabout() => RoundaboutValue;

    public bool FerryValue { get; set; }

    public void SetFerry(bool v) => FerryValue = v;

    public bool Ferry() => FerryValue;

    public bool RailValue { get; set; }

    public void SetRail(bool v) => RailValue = v;

    public bool Rail() => RailValue;

    public bool TunnelValue { get; set; }

    public void SetTunnel(bool v) => TunnelValue = v;

    public bool Tunnel() => TunnelValue;

    public bool TollValue { get; set; }

    public void SetToll(bool v) => TollValue = v;

    public bool Toll() => TollValue;

    public bool BridgeValue { get; set; }

    public void SetBridge(bool v) => BridgeValue = v;

    public bool Bridge() => BridgeValue;

    public bool IndoorValue { get; set; }

    public void SetIndoor(bool v) => IndoorValue = v;

    public bool Indoor() => IndoorValue;

    public bool WheelchairValue { get; set; }

    public void SetWheelchair(bool v) => WheelchairValue = v;

    public bool Wheelchair() => WheelchairValue;

    public bool WheelchairTagValue { get; set; }

    public void SetWheelchairTag(bool v) => WheelchairTagValue = v;

    public bool WheelchairTag() => WheelchairTagValue;

    public bool SidewalkLeftValue { get; set; }

    public void SetSidewalkLeft(bool v) => SidewalkLeftValue = v;

    public bool SidewalkLeft() => SidewalkLeftValue;

    public bool SidewalkRightValue { get; set; }

    public void SetSidewalkRight(bool v) => SidewalkRightValue = v;

    public bool SidewalkRight() => SidewalkRightValue;

    public bool DriveOnRightValue { get; set; }

    public void SetDriveOnRight(bool v) => DriveOnRightValue = v;

    public bool DriveOnRight() => DriveOnRightValue;

    public bool MultipleLevelsValue { get; set; }

    public void SetMultipleLevels(bool v) => MultipleLevelsValue = v;

    public bool MultipleLevels() => MultipleLevelsValue;

    public bool ExitValue { get; set; }

    public void SetExit(bool v) => ExitValue = v;

    public bool Exit() => ExitValue;

    public bool TaggedSpeedValue { get; set; }

    public void SetTaggedSpeed(bool v) => TaggedSpeedValue = v;

    public bool TaggedSpeed() => TaggedSpeedValue;

    public bool ForwardTaggedSpeedValue { get; set; }

    public void SetForwardTaggedSpeed(bool v) => ForwardTaggedSpeedValue = v;

    public bool ForwardTaggedSpeed() => ForwardTaggedSpeedValue;

    public bool BackwardTaggedSpeedValue { get; set; }

    public void SetBackwardTaggedSpeed(bool v) => BackwardTaggedSpeedValue = v;

    public bool BackwardTaggedSpeed() => BackwardTaggedSpeedValue;

    public bool TaggedLanesValue { get; set; }

    public void SetTaggedLanes(bool v) => TaggedLanesValue = v;

    public bool TaggedLanes() => TaggedLanesValue;

    public bool ForwardTaggedLanesValue { get; set; }

    public void SetForwardTaggedLanes(bool v) => ForwardTaggedLanesValue = v;

    public bool ForwardTaggedLanes() => ForwardTaggedLanesValue;

    public bool BackwardTaggedLanesValue { get; set; }

    public void SetBackwardTaggedLanes(bool v) => BackwardTaggedLanesValue = v;

    public bool BackwardTaggedLanes() => BackwardTaggedLanesValue;

    public bool TruckRouteValue { get; set; }

    public void SetTruckRoute(bool v) => TruckRouteValue = v;

    public bool TruckRoute() => TruckRouteValue;

    public bool LinkValue { get; set; }

    public void SetLink(bool v) => LinkValue = v;

    public bool Link() => LinkValue;

    public bool TurnChannelValue { get; set; }

    public void SetTurnChannel(bool v) => TurnChannelValue = v;

    public bool TurnChannel() => TurnChannelValue;

    public bool ShoulderRightValue { get; set; }

    public void SetShoulderRight(bool v) => ShoulderRightValue = v;

    public bool ShoulderRight() => ShoulderRightValue;

    public bool ShoulderLeftValue { get; set; }

    public void SetShoulderLeft(bool v) => ShoulderLeftValue = v;

    public bool ShoulderLeft() => ShoulderLeftValue;

    public bool DismountValue { get; set; }

    public void SetDismount(bool v) => DismountValue = v;

    public bool Dismount() => DismountValue;

    public bool UseSidepathValue { get; set; }

    public void SetUseSidepath(bool v) => UseSidepathValue = v;

    public bool UseSidepath() => UseSidepathValue;

    public bool CyclelaneRightOppositeValue { get; set; }

    public void SetCyclelaneRightOpposite(bool v) => CyclelaneRightOppositeValue = v;

    public bool CyclelaneRightOpposite() => CyclelaneRightOppositeValue;

    public bool CyclelaneLeftOppositeValue { get; set; }

    public void SetCyclelaneLeftOpposite(bool v) => CyclelaneLeftOppositeValue = v;

    public bool CyclelaneLeftOpposite() => CyclelaneLeftOppositeValue;

    public bool LitValue { get; set; }

    public void SetLit(bool v) => LitValue = v;

    public bool Lit() => LitValue;

    // ---- Enumerated classification --------------------------------------------

    private byte _roadClass;
    private byte _use;
    private byte _surface;
    private byte _sacScale;
    private byte _cycleLaneRight;
    private byte _cycleLaneLeft;
    private byte _hovType;
    private uint _bikeNetwork;

    /// <summary>Sets the road class (importance of the road/path).</summary>
    public void SetRoadClass(RoadClass roadClass) => _roadClass = (byte)roadClass;

    /// <summary>Gets the road class.</summary>
    public RoadClass RoadClassValue() => (RoadClass)_roadClass;

    /// <summary>Sets the use/form tag.</summary>
    public void SetUse(Use use) => _use = (byte)use;

    /// <summary>Gets the use.</summary>
    public Use UseValue() => (Use)_use;

    /// <summary>Sets the surface.</summary>
    public void SetSurface(Surface surface) => _surface = (byte)surface;

    /// <summary>Gets the surface.</summary>
    public Surface SurfaceValue() => (Surface)_surface;

    /// <summary>Sets the SAC scale.</summary>
    public void SetSacScale(SacScale sacScale) => _sacScale = (byte)sacScale;

    /// <summary>Gets the SAC scale.</summary>
    public SacScale SacScaleValue() => (SacScale)_sacScale;

    /// <summary>Sets the right cycle lane.</summary>
    public void SetCyclelaneRight(CycleLane cyclelane) => _cycleLaneRight = (byte)cyclelane;

    /// <summary>Gets the right cycle lane.</summary>
    public CycleLane CyclelaneRight() => (CycleLane)_cycleLaneRight;

    /// <summary>Sets the left cycle lane.</summary>
    public void SetCyclelaneLeft(CycleLane cyclelane) => _cycleLaneLeft = (byte)cyclelane;

    /// <summary>Gets the left cycle lane.</summary>
    public CycleLane CyclelaneLeft() => (CycleLane)_cycleLaneLeft;

    /// <summary>Sets the HOV edge type.</summary>
    public void SetHovType(HovEdgeType hovType) => _hovType = (byte)hovType;

    /// <summary>Gets the HOV edge type.</summary>
    public HovEdgeType HovType() => (HovEdgeType)_hovType;

    /// <summary>Sets the bike network mask (ncn/rcn/lcn/mcn).</summary>
    public void SetBikeNetwork(uint bikeNetwork) => _bikeNetwork = bikeNetwork & 0xF;

    /// <summary>Gets the bike network mask.</summary>
    public uint BikeNetwork() => _bikeNetwork;

    // ---- Layer (Z-level, signed) ----------------------------------------------

    private sbyte _layer;

    /// <summary>Sets the layer index (Z-level) of the way; may be negative.</summary>
    public void SetLayer(sbyte layer) => _layer = layer;

    /// <summary>Gets the layer (Z-level), can be negative.</summary>
    public sbyte Layer() => _layer;

    // ---- Name / ref / destination / linguistic string indices -----------------
    // These index into OSMData.name_offset_map (UniqueNames). They are kept as plain
    // uint fields exactly as in the C++ struct so the parser can populate them.

    public uint RefIndex { get; set; }

    public uint RefLangIndex { get; set; }

    public uint RefLeftIndex { get; set; }

    public uint RefLeftLangIndex { get; set; }

    public uint RefRightIndex { get; set; }

    public uint RefRightLangIndex { get; set; }

    public uint IntRefIndex { get; set; }

    public uint IntRefLangIndex { get; set; }

    public uint IntRefLeftIndex { get; set; }

    public uint IntRefLeftLangIndex { get; set; }

    public uint IntRefRightIndex { get; set; }

    public uint IntRefRightLangIndex { get; set; }

    public uint NameIndex { get; set; }

    public uint NameLangIndex { get; set; }

    public uint NameLeftIndex { get; set; }

    public uint NameLeftLangIndex { get; set; }

    public uint NameRightIndex { get; set; }

    public uint NameRightLangIndex { get; set; }

    public uint NameForwardIndex { get; set; }

    public uint NameForwardLangIndex { get; set; }

    public uint NameBackwardIndex { get; set; }

    public uint NameBackwardLangIndex { get; set; }

    public uint AltNameIndex { get; set; }

    public uint AltNameLangIndex { get; set; }

    public uint AltNameLeftIndex { get; set; }

    public uint AltNameLeftLangIndex { get; set; }

    public uint AltNameRightIndex { get; set; }

    public uint AltNameRightLangIndex { get; set; }

    public uint OfficialNameIndex { get; set; }

    public uint OfficialNameLangIndex { get; set; }

    public uint OfficialNameLeftIndex { get; set; }

    public uint OfficialNameLeftLangIndex { get; set; }

    public uint OfficialNameRightIndex { get; set; }

    public uint OfficialNameRightLangIndex { get; set; }

    public uint TunnelNameIndex { get; set; }

    public uint TunnelNameLangIndex { get; set; }

    public uint TunnelNameLeftIndex { get; set; }

    public uint TunnelNameLeftLangIndex { get; set; }

    public uint TunnelNameRightIndex { get; set; }

    public uint TunnelNameRightLangIndex { get; set; }

    public uint FwdTurnLanesIndex { get; set; }

    public uint BwdTurnLanesIndex { get; set; }

    public uint FwdJctBaseIndex { get; set; }

    public uint BwdJctBaseIndex { get; set; }

    public uint FwdJctOverlayIndex { get; set; }

    public uint BwdJctOverlayIndex { get; set; }

    public uint FwdSignboardBaseIndex { get; set; }

    public uint BwdSignboardBaseIndex { get; set; }

    public uint DestinationIndex { get; set; }

    public uint DestinationLangIndex { get; set; }

    public uint DestinationForwardIndex { get; set; }

    public uint DestinationBackwardIndex { get; set; }

    public uint DestinationForwardLangIndex { get; set; }

    public uint DestinationBackwardLangIndex { get; set; }

    public uint DestinationRefIndex { get; set; }

    public uint DestinationRefLangIndex { get; set; }

    public uint DestinationRefToIndex { get; set; }

    public uint DestinationRefToLangIndex { get; set; }

    public uint DestinationIntRefIndex { get; set; }

    public uint DestinationIntRefToIndex { get; set; }

    public uint DestinationStreetIndex { get; set; }

    public uint DestinationStreetLangIndex { get; set; }

    public uint DestinationStreetToIndex { get; set; }

    public uint DestinationStreetToLangIndex { get; set; }

    public uint JunctionNameIndex { get; set; }

    public uint JunctionNameLangIndex { get; set; }

    public uint JunctionRefIndex { get; set; }

    public uint JunctionRefLangIndex { get; set; }

    public uint LevelIndex { get; set; }

    public uint LevelRefIndex { get; set; }
}
