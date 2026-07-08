// Faithful C# port of Valhalla odin Maneuver (valhalla/odin/maneuver.h + src/odin/maneuver.cc)
// @ 3.7.0. Source: valhalla/odin/maneuver.h, src/odin/maneuver.cc
//
// This is the working maneuver data structure the maneuver builder fills in while turning a TripLeg
// into turn-by-turn directions. Public members are PascalCase; the data layout, defaults, and the
// HasSameNames / HasSimilarNames / IsXxxType / HasUsableInternalIntersectionName logic mirror the
// C++ exactly.
//
// PORT-NOTE (DEFER): narrativebuilder.cc + narrative_dictionary.cc (localized prose) are NOT ported.
// The prose-carrying string fields (Instruction, the four Verbal*TransitionInstruction strings, and
// the depart/arrive instruction strings) remain part of the structure because they are members of
// the odin Maneuver class, but the STRUCTURAL builder leaves them empty - only maneuver structure
// (type, street names, length/time, turn degree, headings, begin/end shape+node index, cardinal
// direction, signs, and the boolean flags) is produced.
//
// PORT-NOTE (DEFER): transit support (TransitRouteInfo / TransitEgressInfo / TransitStationInfo /
// TransitPlatformInfo and the transit_* members) belongs to the EXCLUDED transit module. The
// transit_connection flag and IsTransit() are kept (they are simple structural predicates), but the
// transit info objects and their accessors are omitted. The verbal_formatter member (a
// baldr::VerbalTextFormatter) is part of the prose family and is omitted as well.
//
// PORT-NOTE: The C++ ToString / ToParameterString are guarded by LOGGING_LEVEL_TRACE and emit prose
// debug text; they are omitted from this structural port.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Trail type - for cycleways, walkways, mountain bike trails. Faithful port of
/// <c>valhalla::odin::TrailType</c>.
/// </summary>
public enum TrailType
{
    /// <summary>Not a trail.</summary>
    None,

    /// <summary>Named cycleway.</summary>
    NamedCycleway,

    /// <summary>Unnamed cycleway.</summary>
    UnnamedCycleway,

    /// <summary>Named walkway.</summary>
    NamedWalkway,

    /// <summary>Unnamed walkway.</summary>
    UnnamedWalkway,

    /// <summary>Named mountain bike trail.</summary>
    NamedMtbTrail,

    /// <summary>Unnamed mountain bike trail.</summary>
    UnnamedMtbTrail,
}

/// <summary>
/// Utility class used during the creation of the maneuver list that populates the trip directions.
/// Faithful port of <c>valhalla::odin::Maneuver</c>.
/// </summary>
public sealed class Maneuver
{
    /// <summary>
    /// Relative direction of a maneuver. Faithful port of <c>Maneuver::RelativeDirection</c>. Note
    /// the C++ typo <c>KReverse</c> (capital K) is preserved in spirit as <see cref="Reverse"/>.
    /// </summary>
    public enum RelativeDirection
    {
        /// <summary>No relative direction.</summary>
        None,

        /// <summary>Keep straight.</summary>
        KeepStraight,

        /// <summary>Keep right.</summary>
        KeepRight,

        /// <summary>Right.</summary>
        Right,

        /// <summary>Reverse (C++ <c>KReverse</c>).</summary>
        Reverse,

        /// <summary>Left.</summary>
        Left,

        /// <summary>Keep left.</summary>
        KeepLeft,
    }

    private DirectionsLegManeuverType _type;
    private NodeType _nodeType;
    private bool _hasNodeType;
    private bool _trafficSignal;
    private bool _isSteps;
    private bool _isBridge;
    private bool _isTunnel;
    private StreetNames _streetNames;
    private StreetNames _beginStreetNames;
    private StreetNames _crossStreetNames;
    private string _instruction;
    private float _length;      // Kilometers
    private double _time;       // Seconds
    private double _basicTime;  // len/speed on each edge with no stop impact in seconds
    private uint _turnDegree;
    private RelativeDirection _beginRelativeDirection;
    private DirectionsLegManeuverCardinalDirection _beginCardinalDirection;
    private uint _beginHeading;
    private uint _endHeading;
    private uint _beginNodeIndex;
    private uint _endNodeIndex;
    private uint _beginShapeIndex;
    private uint _endShapeIndex;
    private bool _ramp;
    private bool _turnChannel;
    private bool _ferry;
    private bool _railFerry;
    private bool _roundabout;
    private bool _portionsToll;
    private bool _portionsUnpaved;
    private bool _portionsHighway;
    private bool _internalIntersection;
    private readonly Signs _signs = new();
    private uint _internalRightTurnCount;
    private uint _internalLeftTurnCount;
    private bool _fork;
    private bool _beginIntersectingEdgeNameConsistency;
    private bool _intersectingForwardEdge;
    private string _verbalSuccinctTransitionInstruction;
    private string _verbalTransitionAlertInstruction;
    private string _verbalPreTransitionInstruction;
    private string _verbalPostTransitionInstruction;
    private bool _tee;
    private TrailType _trailType;
    private bool _imminentVerbalMultiCue;
    private bool _distantVerbalMultiCue;
    private bool _toStayOn;
    private bool _pedestrianCrossing;
    private RelativeDirection _mergeToRelativeDirection;
    private bool _driveOnRight; // Defaults to true
    private bool _hasTimeRestrictions;
    private bool _hasRightTraversableOutboundIntersectingEdge;
    private bool _hasLeftTraversableOutboundIntersectingEdge;
    private bool _includeVerbalPreTransitionLength;
    private bool _containsObviousManeuver;

    private uint _roundaboutExitCount;
    private bool _hasCombinedEnterExitRoundabout;
    private float _roundaboutLength;      // Kilometers
    private float _roundaboutExitLength;  // Kilometers
    private StreetNames _roundaboutExitStreetNames;
    private StreetNames _roundaboutExitBeginStreetNames;
    private readonly Signs _roundaboutExitSigns = new();
    private uint _roundaboutExitBeginHeading;
    private uint _roundaboutExitTurnDegree;
    private uint _roundaboutExitShapeIndex;

    private bool _hasCollapsedSmallEndRampFork;
    private bool _hasCollapsedMergeManeuver;
    private bool _hasLongStreetName;

    // Indoor elements
    private bool _elevator;
    private bool _indoorSteps;
    private bool _escalator;
    private bool _buildingEnter;
    private bool _buildingExit;
    private bool _hasLevelChanges;
    private string _endLevelRef;

    // Transit connection flag (transit info objects are DEFERRED - see file header).
    private bool _transitConnection;

    private string _departInstruction;
    private string _verbalDepartInstruction;
    private string _arriveInstruction;
    private string _verbalArriveInstruction;

    // Travel mode
    private TravelMode _travelMode;
    private bool _rail;
    private bool _bus;

    // Travel types
    private VehicleType _vehicleType;
    private PedestrianType _pedestrianType;
    private BicycleType _bicycleType;
    private TransitType _transitType;

    private DirectionsLegManeuverBssManeuverType _bssManeuverType;

    private readonly List<DirectionsLegGuidanceView> _guidanceViews = new();

    /// <summary>Default constructor. Faithful port of <c>Maneuver()</c> with the same field defaults.</summary>
    public Maneuver()
    {
        _type = DirectionsLegManeuverType.None;
        _hasNodeType = false;
        _trafficSignal = false;
        _isSteps = false;
        _isBridge = false;
        _isTunnel = false;
        _instruction = string.Empty;
        _length = 0.0f;
        _time = 0;
        _basicTime = 0;
        _turnDegree = 0;
        _beginRelativeDirection = RelativeDirection.None;
        _beginCardinalDirection = DirectionsLegManeuverCardinalDirection.North;
        _beginHeading = 0;
        _endHeading = 0;
        _beginNodeIndex = 0;
        _endNodeIndex = 0;
        _beginShapeIndex = 0;
        _endShapeIndex = 0;
        _ramp = false;
        _turnChannel = false;
        _ferry = false;
        _railFerry = false;
        _roundabout = false;
        _portionsToll = false;
        _portionsUnpaved = false;
        _portionsHighway = false;
        _internalIntersection = false;
        _internalRightTurnCount = 0;
        _internalLeftTurnCount = 0;
        _fork = false;
        _beginIntersectingEdgeNameConsistency = false;
        _intersectingForwardEdge = false;
        _verbalSuccinctTransitionInstruction = string.Empty;
        _verbalTransitionAlertInstruction = string.Empty;
        _verbalPreTransitionInstruction = string.Empty;
        _verbalPostTransitionInstruction = string.Empty;
        _tee = false;
        _trailType = TrailType.None;
        _imminentVerbalMultiCue = false;
        _distantVerbalMultiCue = false;
        _toStayOn = false;
        _pedestrianCrossing = false;
        _driveOnRight = true;
        _hasTimeRestrictions = false;
        _hasRightTraversableOutboundIntersectingEdge = false;
        _hasLeftTraversableOutboundIntersectingEdge = false;
        _includeVerbalPreTransitionLength = false;
        _containsObviousManeuver = false;
        _roundaboutExitCount = 0;
        _hasCombinedEnterExitRoundabout = false;
        _roundaboutLength = 0.0f;
        _roundaboutExitLength = 0.0f;
        _roundaboutExitBeginHeading = 0;
        _roundaboutExitTurnDegree = 0;
        _roundaboutExitShapeIndex = 0;
        _hasCollapsedSmallEndRampFork = false;
        _hasCollapsedMergeManeuver = false;
        _hasLongStreetName = false;
        _elevator = false;
        _indoorSteps = false;
        _escalator = false;
        _buildingEnter = false;
        _buildingExit = false;
        _hasLevelChanges = false;
        _endLevelRef = string.Empty;
        _transitConnection = false;
        _departInstruction = string.Empty;
        _verbalDepartInstruction = string.Empty;
        _arriveInstruction = string.Empty;
        _verbalArriveInstruction = string.Empty;
        _travelMode = TravelMode.Drive;
        _rail = false;
        _bus = false;
        _vehicleType = VehicleType.Car;
        _pedestrianType = PedestrianType.Foot;
        _bicycleType = BicycleType.Road;
        _transitType = TransitType.Rail;
        _bssManeuverType = DirectionsLegManeuverBssManeuverType.NoneAction;

        // C++ allocates fresh StreetNames (US) for the five name lists in the constructor body.
        _streetNames = new StreetNamesUs();
        _beginStreetNames = new StreetNamesUs();
        _crossStreetNames = new StreetNamesUs();
        _roundaboutExitStreetNames = new StreetNamesUs();
        _roundaboutExitBeginStreetNames = new StreetNamesUs();
    }

    /// <summary>The maneuver type. Faithful port of <c>type()</c>.</summary>
    public DirectionsLegManeuverType Type() => _type;

    /// <summary>Sets the maneuver type. Faithful port of <c>set_type()</c>.</summary>
    public void SetType(DirectionsLegManeuverType type) => _type = type;

    /// <summary>True if a start maneuver type. Faithful port of <c>IsStartType()</c>.</summary>
    public bool IsStartType()
        => _type == DirectionsLegManeuverType.Start
           || _type == DirectionsLegManeuverType.StartLeft
           || _type == DirectionsLegManeuverType.StartRight;

    /// <summary>True if a destination maneuver type. Faithful port of <c>IsDestinationType()</c>.</summary>
    public bool IsDestinationType()
        => _type == DirectionsLegManeuverType.Destination
           || _type == DirectionsLegManeuverType.DestinationLeft
           || _type == DirectionsLegManeuverType.DestinationRight;

    /// <summary>True if a merge maneuver type. Faithful port of <c>IsMergeType()</c>.</summary>
    public bool IsMergeType()
        => _type == DirectionsLegManeuverType.Merge
           || _type == DirectionsLegManeuverType.MergeLeft
           || _type == DirectionsLegManeuverType.MergeRight;

    /// <summary>True if a left-side maneuver type. Faithful port of <c>IsLeftType()</c>.</summary>
    public bool IsLeftType()
        => _type == DirectionsLegManeuverType.SlightLeft
           || _type == DirectionsLegManeuverType.Left
           || _type == DirectionsLegManeuverType.SharpLeft
           || _type == DirectionsLegManeuverType.UturnLeft
           || _type == DirectionsLegManeuverType.RampLeft
           || _type == DirectionsLegManeuverType.ExitLeft
           || _type == DirectionsLegManeuverType.StayLeft
           || _type == DirectionsLegManeuverType.DestinationLeft
           || _type == DirectionsLegManeuverType.MergeLeft;

    /// <summary>True if a right-side maneuver type. Faithful port of <c>IsRightType()</c>.</summary>
    public bool IsRightType()
        => _type == DirectionsLegManeuverType.SlightRight
           || _type == DirectionsLegManeuverType.Right
           || _type == DirectionsLegManeuverType.SharpRight
           || _type == DirectionsLegManeuverType.UturnRight
           || _type == DirectionsLegManeuverType.RampRight
           || _type == DirectionsLegManeuverType.ExitRight
           || _type == DirectionsLegManeuverType.StayRight
           || _type == DirectionsLegManeuverType.DestinationRight
           || _type == DirectionsLegManeuverType.MergeRight;

    /// <summary>Sets the node type (and marks it present). Faithful port of <c>set_node_type()</c>.</summary>
    public void SetNodeType(NodeType type)
    {
        _nodeType = type;
        _hasNodeType = true;
    }

    /// <summary>The node type. Faithful port of <c>node_type()</c>.</summary>
    public NodeType GetNodeType() => _nodeType;

    /// <summary>True if a node type has been set. Faithful port of <c>has_node_type()</c>.</summary>
    public bool HasNodeType() => _hasNodeType;

    /// <summary>True if the maneuver node has a traffic signal. Faithful port of <c>traffic_signal()</c>.</summary>
    public bool TrafficSignal() => _trafficSignal;

    /// <summary>Sets the traffic signal flag. Faithful port of <c>set_traffic_signal()</c>.</summary>
    public void SetTrafficSignal(bool trafficSignal) => _trafficSignal = trafficSignal;

    /// <summary>True if the maneuver is steps. Faithful port of <c>is_steps()</c>.</summary>
    public bool IsSteps() => _isSteps;

    /// <summary>Sets the steps flag. Faithful port of <c>set_steps()</c>.</summary>
    public void SetSteps(bool steps) => _isSteps = steps;

    /// <summary>True if the maneuver is a bridge. Faithful port of <c>is_bridge()</c>.</summary>
    public bool IsBridge() => _isBridge;

    /// <summary>Sets the bridge flag. Faithful port of <c>set_bridge()</c>.</summary>
    public void SetBridge(bool bridge) => _isBridge = bridge;

    /// <summary>True if the maneuver is a tunnel. Faithful port of <c>is_tunnel()</c>.</summary>
    public bool IsTunnel() => _isTunnel;

    /// <summary>Sets the tunnel flag. Faithful port of <c>set_tunnel()</c>.</summary>
    public void SetTunnel(bool tunnel) => _isTunnel = tunnel;

    /// <summary>The maneuver street names. Faithful port of <c>street_names()</c>.</summary>
    public StreetNames StreetNames() => _streetNames;

    /// <summary>Sets the street names from (name, is-route-number) pairs. Faithful port of the vector overload.</summary>
    public void SetStreetNames(IEnumerable<(string Name, bool IsRouteNumber)> names)
        => _streetNames = new StreetNamesUs(names);

    /// <summary>Sets the street names from a StreetNames instance. Faithful port of the move overload.</summary>
    public void SetStreetNames(StreetNames streetNames) => _streetNames = streetNames;

    /// <summary>True if there are street names. Faithful port of <c>HasStreetNames()</c>.</summary>
    public bool HasStreetNames() => _streetNames.Count != 0;

    /// <summary>Clears the street names. Faithful port of <c>ClearStreetNames()</c>.</summary>
    public void ClearStreetNames() => _streetNames.Clear();

    /// <summary>
    /// True if this maneuver and <paramref name="otherManeuver"/> have the same complete set of
    /// names. Faithful port of <c>HasSameNames()</c>.
    /// </summary>
    public bool HasSameNames(Maneuver? otherManeuver, bool allowBeginIntersectingEdgeNameConsistency = false)
    {
        // Allow similar intersecting edge names
        // OR verify that there are no similar intersecting edge names
        if (allowBeginIntersectingEdgeNameConsistency || !BeginIntersectingEdgeNameConsistency())
        {
            // If this maneuver has street names and other maneuver exists
            if (HasStreetNames() && otherManeuver != null)
            {
                // other and this maneuvers have same names
                StreetNames sameStreetNames = otherManeuver.StreetNames().FindCommonStreetNames(StreetNames());
                if (sameStreetNames.Count != 0 && StreetNames().Count == sameStreetNames.Count)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True if this maneuver and <paramref name="otherManeuver"/> have similar (base) names.
    /// Faithful port of <c>HasSimilarNames()</c>.
    /// </summary>
    public bool HasSimilarNames(Maneuver? otherManeuver, bool allowBeginIntersectingEdgeNameConsistency = false)
    {
        if (allowBeginIntersectingEdgeNameConsistency || !BeginIntersectingEdgeNameConsistency())
        {
            if (HasStreetNames() && otherManeuver != null)
            {
                StreetNames similarStreetNames = otherManeuver.StreetNames().FindCommonBaseNames(StreetNames());
                if (similarStreetNames.Count != 0 && StreetNames().Count == similarStreetNames.Count)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The begin street names. Faithful port of <c>begin_street_names()</c>.</summary>
    public StreetNames BeginStreetNames() => _beginStreetNames;

    /// <summary>Sets the begin street names from pairs. Faithful port of the vector overload.</summary>
    public void SetBeginStreetNames(IEnumerable<(string Name, bool IsRouteNumber)> names)
        => _beginStreetNames = new StreetNamesUs(names);

    /// <summary>Sets the begin street names. Faithful port of the move overload.</summary>
    public void SetBeginStreetNames(StreetNames beginStreetNames) => _beginStreetNames = beginStreetNames;

    /// <summary>True if there are begin street names. Faithful port of <c>HasBeginStreetNames()</c>.</summary>
    public bool HasBeginStreetNames() => _beginStreetNames.Count != 0;

    /// <summary>Clears the begin street names. Faithful port of <c>ClearBeginStreetNames()</c>.</summary>
    public void ClearBeginStreetNames() => _beginStreetNames.Clear();

    /// <summary>The cross street names. Faithful port of <c>cross_street_names()</c>.</summary>
    public StreetNames CrossStreetNames() => _crossStreetNames;

    /// <summary>Sets the cross street names from pairs. Faithful port of the vector overload.</summary>
    public void SetCrossStreetNames(IEnumerable<(string Name, bool IsRouteNumber)> names)
        => _crossStreetNames = new StreetNamesUs(names);

    /// <summary>Sets the cross street names. Faithful port of the move overload.</summary>
    public void SetCrossStreetNames(StreetNames crossStreetNames) => _crossStreetNames = crossStreetNames;

    /// <summary>True if there are cross street names. Faithful port of <c>HasCrossStreetNames()</c>.</summary>
    public bool HasCrossStreetNames() => _crossStreetNames.Count != 0;

    /// <summary>Clears the cross street names. Faithful port of <c>ClearCrossStreetNames()</c>.</summary>
    public void ClearCrossStreetNames() => _crossStreetNames.Clear();

    /// <summary>The instruction text (DEFERRED - left empty by the structural builder).</summary>
    public string Instruction() => _instruction;

    /// <summary>Sets the instruction text. Faithful port of <c>set_instruction()</c>.</summary>
    public void SetInstruction(string instruction) => _instruction = instruction;

    /// <summary>The maneuver length, in the specified units. Faithful port of <c>length()</c>.</summary>
    public float Length(bool miles = false) => miles ? _length * Constants.MilePerKm : _length;

    /// <summary>Sets the maneuver length (kilometers). Faithful port of <c>set_length()</c>.</summary>
    public void SetLength(float kmLength) => _length = kmLength;

    /// <summary>The maneuver time (seconds). Faithful port of <c>time()</c>.</summary>
    public double Time() => _time;

    /// <summary>Sets the maneuver time (seconds). Faithful port of <c>set_time()</c>.</summary>
    public void SetTime(double time) => _time = time;

    /// <summary>The basic time (len/speed, no stop impact). Faithful port of <c>basic_time()</c>.</summary>
    public double BasicTime() => _basicTime;

    /// <summary>Sets the basic time. Faithful port of <c>set_basic_time()</c>.</summary>
    public void SetBasicTime(double basicTime) => _basicTime = basicTime;

    /// <summary>The turn degree. Faithful port of <c>turn_degree()</c>.</summary>
    public uint TurnDegree() => _turnDegree;

    /// <summary>Sets the turn degree. Faithful port of <c>set_turn_degree()</c>.</summary>
    public void SetTurnDegree(uint turnDegree) => _turnDegree = turnDegree;

    /// <summary>The begin relative direction. Faithful port of <c>begin_relative_direction()</c>.</summary>
    public RelativeDirection BeginRelativeDirection() => _beginRelativeDirection;

    /// <summary>Sets the begin relative direction. Faithful port of <c>set_begin_relative_direction()</c>.</summary>
    public void SetBeginRelativeDirection(RelativeDirection beginRelativeDirection)
        => _beginRelativeDirection = beginRelativeDirection;

    /// <summary>The begin cardinal direction. Faithful port of <c>begin_cardinal_direction()</c>.</summary>
    public DirectionsLegManeuverCardinalDirection BeginCardinalDirection() => _beginCardinalDirection;

    /// <summary>Sets the begin cardinal direction. Faithful port of <c>set_begin_cardinal_direction()</c>.</summary>
    public void SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection beginCardinalDirection)
        => _beginCardinalDirection = beginCardinalDirection;

    /// <summary>The begin heading. Faithful port of <c>begin_heading()</c>.</summary>
    public uint BeginHeading() => _beginHeading;

    /// <summary>Sets the begin heading. Faithful port of <c>set_begin_heading()</c>.</summary>
    public void SetBeginHeading(uint beginHeading) => _beginHeading = beginHeading;

    /// <summary>The end heading. Faithful port of <c>end_heading()</c>.</summary>
    public uint EndHeading() => _endHeading;

    /// <summary>Sets the end heading. Faithful port of <c>set_end_heading()</c>.</summary>
    public void SetEndHeading(uint endHeading) => _endHeading = endHeading;

    /// <summary>The begin node index. Faithful port of <c>begin_node_index()</c>.</summary>
    public uint BeginNodeIndex() => _beginNodeIndex;

    /// <summary>Sets the begin node index. Faithful port of <c>set_begin_node_index()</c>.</summary>
    public void SetBeginNodeIndex(uint beginNodeIndex) => _beginNodeIndex = beginNodeIndex;

    /// <summary>The end node index. Faithful port of <c>end_node_index()</c>.</summary>
    public uint EndNodeIndex() => _endNodeIndex;

    /// <summary>Sets the end node index. Faithful port of <c>set_end_node_index()</c>.</summary>
    public void SetEndNodeIndex(uint endNodeIndex) => _endNodeIndex = endNodeIndex;

    /// <summary>The begin shape index. Faithful port of <c>begin_shape_index()</c>.</summary>
    public uint BeginShapeIndex() => _beginShapeIndex;

    /// <summary>Sets the begin shape index. Faithful port of <c>set_begin_shape_index()</c>.</summary>
    public void SetBeginShapeIndex(uint beginShapeIndex) => _beginShapeIndex = beginShapeIndex;

    /// <summary>The end shape index. Faithful port of <c>end_shape_index()</c>.</summary>
    public uint EndShapeIndex() => _endShapeIndex;

    /// <summary>Sets the end shape index. Faithful port of <c>set_end_shape_index()</c>.</summary>
    public void SetEndShapeIndex(uint endShapeIndex) => _endShapeIndex = endShapeIndex;

    /// <summary>True if the maneuver is a ramp. Faithful port of <c>ramp()</c>.</summary>
    public bool Ramp() => _ramp;

    /// <summary>Sets the ramp flag. Faithful port of <c>set_ramp()</c>.</summary>
    public void SetRamp(bool ramp) => _ramp = ramp;

    /// <summary>True if the maneuver is a turn channel. Faithful port of <c>turn_channel()</c>.</summary>
    public bool TurnChannel() => _turnChannel;

    /// <summary>Sets the turn channel flag. Faithful port of <c>set_turn_channel()</c>.</summary>
    public void SetTurnChannel(bool turnChannel) => _turnChannel = turnChannel;

    /// <summary>True if the maneuver is a ferry. Faithful port of <c>ferry()</c>.</summary>
    public bool Ferry() => _ferry;

    /// <summary>Sets the ferry flag. Faithful port of <c>set_ferry()</c>.</summary>
    public void SetFerry(bool ferry) => _ferry = ferry;

    /// <summary>True if the maneuver is a rail ferry. Faithful port of <c>rail_ferry()</c>.</summary>
    public bool RailFerry() => _railFerry;

    /// <summary>Sets the rail ferry flag. Faithful port of <c>set_rail_ferry()</c>.</summary>
    public void SetRailFerry(bool railFerry) => _railFerry = railFerry;

    /// <summary>True if the maneuver is a roundabout. Faithful port of <c>roundabout()</c>.</summary>
    public bool Roundabout() => _roundabout;

    /// <summary>Sets the roundabout flag. Faithful port of <c>set_roundabout()</c>.</summary>
    public void SetRoundabout(bool roundabout) => _roundabout = roundabout;

    /// <summary>True if portions of the maneuver are tolled. Faithful port of <c>portions_toll()</c>.</summary>
    public bool PortionsToll() => _portionsToll;

    /// <summary>Sets the portions-toll flag. Faithful port of <c>set_portions_toll()</c>.</summary>
    public void SetPortionsToll(bool portionsToll) => _portionsToll = portionsToll;

    /// <summary>True if portions of the maneuver are unpaved. Faithful port of <c>portions_unpaved()</c>.</summary>
    public bool PortionsUnpaved() => _portionsUnpaved;

    /// <summary>Sets the portions-unpaved flag. Faithful port of <c>set_portions_unpaved()</c>.</summary>
    public void SetPortionsUnpaved(bool portionsUnpaved) => _portionsUnpaved = portionsUnpaved;

    /// <summary>True if portions of the maneuver are highway. Faithful port of <c>portions_highway()</c>.</summary>
    public bool PortionsHighway() => _portionsHighway;

    /// <summary>Sets the portions-highway flag. Faithful port of <c>set_portions_highway()</c>.</summary>
    public void SetPortionsHighway(bool portionsHighway) => _portionsHighway = portionsHighway;

    /// <summary>True if an internal intersection maneuver. Faithful port of <c>internal_intersection()</c>.</summary>
    public bool InternalIntersection() => _internalIntersection;

    /// <summary>Sets the internal-intersection flag. Faithful port of <c>set_internal_intersection()</c>.</summary>
    public void SetInternalIntersection(bool internalIntersection) => _internalIntersection = internalIntersection;

    /// <summary>
    /// True if the maneuver has a usable internal intersection name (internal intersection with names
    /// and a link count of 1 or 3). Faithful port of <c>HasUsableInternalIntersectionName()</c>.
    /// </summary>
    public bool HasUsableInternalIntersectionName()
    {
        uint linkCount = _endNodeIndex - _beginNodeIndex;
        return _internalIntersection && _streetNames.Count != 0 && (linkCount == 1 || linkCount == 3);
    }

    /// <summary>The signs of the maneuver. Faithful port of <c>signs()</c>.</summary>
    public Signs GetSigns() => _signs;

    /// <summary>The mutable signs of the maneuver. Faithful port of <c>mutable_signs()</c>.</summary>
    public Signs MutableSigns() => _signs;

    /// <summary>True if the maneuver has any sign. Faithful port of <c>HasSigns()</c>.</summary>
    public bool HasSigns() => HasExitSign() || HasGuideSign() || HasJunctionNameSign();

    /// <summary>True if the maneuver has an exit sign. Faithful port of <c>HasExitSign()</c>.</summary>
    public bool HasExitSign() => _signs.HasExit();

    /// <summary>True if the maneuver has an exit number sign. Faithful port of <c>HasExitNumberSign()</c>.</summary>
    public bool HasExitNumberSign() => _signs.HasExitNumber();

    /// <summary>True if the maneuver has an exit branch sign. Faithful port of <c>HasExitBranchSign()</c>.</summary>
    public bool HasExitBranchSign() => _signs.HasExitBranch();

    /// <summary>True if the maneuver has an exit toward sign. Faithful port of <c>HasExitTowardSign()</c>.</summary>
    public bool HasExitTowardSign() => _signs.HasExitToward();

    /// <summary>True if the maneuver has an exit name sign. Faithful port of <c>HasExitNameSign()</c>.</summary>
    public bool HasExitNameSign() => _signs.HasExitName();

    /// <summary>True if the maneuver has a guide sign. Faithful port of <c>HasGuideSign()</c>.</summary>
    public bool HasGuideSign() => _signs.HasGuide();

    /// <summary>True if the maneuver has a guide branch sign. Faithful port of <c>HasGuideBranchSign()</c>.</summary>
    public bool HasGuideBranchSign() => _signs.HasGuideBranch();

    /// <summary>True if the maneuver has a guide toward sign. Faithful port of <c>HasGuideTowardSign()</c>.</summary>
    public bool HasGuideTowardSign() => _signs.HasGuideToward();

    /// <summary>True if the maneuver has a junction name sign. Faithful port of <c>HasJunctionNameSign()</c>.</summary>
    public bool HasJunctionNameSign() => _signs.HasJunctionName();

    /// <summary>The internal right turn count. Faithful port of <c>internal_right_turn_count()</c>.</summary>
    public uint InternalRightTurnCount() => _internalRightTurnCount;

    /// <summary>Sets the internal right turn count. Faithful port of <c>set_internal_right_turn_count()</c>.</summary>
    public void SetInternalRightTurnCount(uint internalRightTurnCount) => _internalRightTurnCount = internalRightTurnCount;

    /// <summary>The internal left turn count. Faithful port of <c>internal_left_turn_count()</c>.</summary>
    public uint InternalLeftTurnCount() => _internalLeftTurnCount;

    /// <summary>Sets the internal left turn count. Faithful port of <c>set_internal_left_turn_count()</c>.</summary>
    public void SetInternalLeftTurnCount(uint internalLeftTurnCount) => _internalLeftTurnCount = internalLeftTurnCount;

    /// <summary>True if the maneuver is a fork. Faithful port of <c>fork()</c>.</summary>
    public bool Fork() => _fork;

    /// <summary>Sets the fork flag. Faithful port of <c>set_fork()</c>.</summary>
    public void SetFork(bool fork) => _fork = fork;

    /// <summary>True if begin intersecting edge name consistency. Faithful port of <c>begin_intersecting_edge_name_consistency()</c>.</summary>
    public bool BeginIntersectingEdgeNameConsistency() => _beginIntersectingEdgeNameConsistency;

    /// <summary>Sets the begin intersecting edge name consistency flag.</summary>
    public void SetBeginIntersectingEdgeNameConsistency(bool beginIntersectingEdgeNameConsistency)
        => _beginIntersectingEdgeNameConsistency = beginIntersectingEdgeNameConsistency;

    /// <summary>True if there is an intersecting forward edge. Faithful port of <c>intersecting_forward_edge()</c>.</summary>
    public bool IntersectingForwardEdge() => _intersectingForwardEdge;

    /// <summary>Sets the intersecting-forward-edge flag. Faithful port of <c>set_intersecting_forward_edge()</c>.</summary>
    public void SetIntersectingForwardEdge(bool intersectingForwardEdge) => _intersectingForwardEdge = intersectingForwardEdge;

    /// <summary>The verbal succinct transition instruction (DEFERRED - prose).</summary>
    public string VerbalSuccinctTransitionInstruction() => _verbalSuccinctTransitionInstruction;

    /// <summary>Sets the verbal succinct transition instruction.</summary>
    public void SetVerbalSuccinctTransitionInstruction(string value) => _verbalSuccinctTransitionInstruction = value;

    /// <summary>True if a verbal succinct transition instruction is present.</summary>
    public bool HasVerbalSuccinctTransitionInstruction() => _verbalSuccinctTransitionInstruction.Length != 0;

    /// <summary>The verbal transition alert instruction (DEFERRED - prose).</summary>
    public string VerbalTransitionAlertInstruction() => _verbalTransitionAlertInstruction;

    /// <summary>Sets the verbal transition alert instruction.</summary>
    public void SetVerbalTransitionAlertInstruction(string value) => _verbalTransitionAlertInstruction = value;

    /// <summary>True if a verbal transition alert instruction is present.</summary>
    public bool HasVerbalTransitionAlertInstruction() => _verbalTransitionAlertInstruction.Length != 0;

    /// <summary>The verbal pre-transition instruction (DEFERRED - prose).</summary>
    public string VerbalPreTransitionInstruction() => _verbalPreTransitionInstruction;

    /// <summary>Sets the verbal pre-transition instruction.</summary>
    public void SetVerbalPreTransitionInstruction(string value) => _verbalPreTransitionInstruction = value;

    /// <summary>True if a verbal pre-transition instruction is present.</summary>
    public bool HasVerbalPreTransitionInstruction() => _verbalPreTransitionInstruction.Length != 0;

    /// <summary>The verbal post-transition instruction (DEFERRED - prose).</summary>
    public string VerbalPostTransitionInstruction() => _verbalPostTransitionInstruction;

    /// <summary>Sets the verbal post-transition instruction.</summary>
    public void SetVerbalPostTransitionInstruction(string value) => _verbalPostTransitionInstruction = value;

    /// <summary>True if a verbal post-transition instruction is present.</summary>
    public bool HasVerbalPostTransitionInstruction() => _verbalPostTransitionInstruction.Length != 0;

    /// <summary>True if the maneuver is at a T-intersection. Faithful port of <c>tee()</c>.</summary>
    public bool Tee() => _tee;

    /// <summary>Sets the tee flag. Faithful port of <c>set_tee()</c>.</summary>
    public void SetTee(bool tee) => _tee = tee;

    /// <summary>The trail type. Faithful port of <c>trail_type()</c>.</summary>
    public TrailType GetTrailType() => _trailType;

    /// <summary>Sets the trail type. Faithful port of <c>set_trail_type()</c>.</summary>
    public void SetTrailType(TrailType trail) => _trailType = trail;

    /// <summary>True if a walkway trail. Faithful port of <c>is_walkway()</c>.</summary>
    public bool IsWalkway() => _trailType == TrailType.NamedWalkway || _trailType == TrailType.UnnamedWalkway;

    /// <summary>True if an unnamed walkway. Faithful port of <c>unnamed_walkway()</c>.</summary>
    public bool UnnamedWalkway() => _trailType == TrailType.UnnamedWalkway;

    /// <summary>True if a cycleway trail. Faithful port of <c>is_cycleway()</c>.</summary>
    public bool IsCycleway() => _trailType == TrailType.NamedCycleway || _trailType == TrailType.UnnamedCycleway;

    /// <summary>True if an unnamed cycleway. Faithful port of <c>unnamed_cycleway()</c>.</summary>
    public bool UnnamedCycleway() => _trailType == TrailType.UnnamedCycleway;

    /// <summary>True if a mountain bike trail. Faithful port of <c>is_mountain_bike_trail()</c>.</summary>
    public bool IsMountainBikeTrail() => _trailType == TrailType.NamedMtbTrail || _trailType == TrailType.UnnamedMtbTrail;

    /// <summary>True if an unnamed mountain bike trail. Faithful port of <c>unnamed_mountain_bike_trail()</c>.</summary>
    public bool UnnamedMountainBikeTrail() => _trailType == TrailType.UnnamedMtbTrail;

    /// <summary>True if a pedestrian crossing. Faithful port of <c>pedestrian_crossing()</c>.</summary>
    public bool PedestrianCrossing() => _pedestrianCrossing;

    /// <summary>Sets the pedestrian-crossing flag. Faithful port of <c>set_pedestrian_crossing()</c>.</summary>
    public void SetPedestrianCrossing(bool pedestrianCrossing) => _pedestrianCrossing = pedestrianCrossing;

    /// <summary>True if an imminent verbal multi-cue. Faithful port of <c>imminent_verbal_multi_cue()</c>.</summary>
    public bool ImminentVerbalMultiCue() => _imminentVerbalMultiCue;

    /// <summary>Sets the imminent verbal multi-cue flag. Faithful port of <c>set_imminent_verbal_multi_cue()</c>.</summary>
    public void SetImminentVerbalMultiCue(bool imminentVerbalMultiCue) => _imminentVerbalMultiCue = imminentVerbalMultiCue;

    /// <summary>True if a distant verbal multi-cue. Faithful port of <c>distant_verbal_multi_cue()</c>.</summary>
    public bool DistantVerbalMultiCue() => _distantVerbalMultiCue;

    /// <summary>Sets the distant verbal multi-cue flag. Faithful port of <c>set_distant_verbal_multi_cue()</c>.</summary>
    public void SetDistantVerbalMultiCue(bool distantVerbalMultiCue) => _distantVerbalMultiCue = distantVerbalMultiCue;

    /// <summary>True if there is a verbal multi-cue. Faithful port of <c>HasVerbalMultiCue()</c>.</summary>
    public bool HasVerbalMultiCue() => _imminentVerbalMultiCue || _distantVerbalMultiCue;

    /// <summary>True if the maneuver is "to stay on" (same name as previous). Faithful port of <c>to_stay_on()</c>.</summary>
    public bool ToStayOn() => _toStayOn;

    /// <summary>Sets the to-stay-on flag. Faithful port of <c>set_to_stay_on()</c>.</summary>
    public void SetToStayOn(bool toStayOn) => _toStayOn = toStayOn;

    /// <summary>The merge-to relative direction. Faithful port of <c>merge_to_relative_direction()</c>.</summary>
    public RelativeDirection MergeToRelativeDirection() => _mergeToRelativeDirection;

    /// <summary>Sets the merge-to relative direction. Faithful port of <c>set_merge_to_relative_direction()</c>.</summary>
    public void SetMergeToRelativeDirection(RelativeDirection mergeToRelativeDirection)
        => _mergeToRelativeDirection = mergeToRelativeDirection;

    /// <summary>True if drive-on-right (default true). Faithful port of <c>drive_on_right()</c>.</summary>
    public bool DriveOnRight() => _driveOnRight;

    /// <summary>Sets the drive-on-right flag. Faithful port of <c>set_drive_on_right()</c>.</summary>
    public void SetDriveOnRight(bool driveOnRight) => _driveOnRight = driveOnRight;

    /// <summary>True if the maneuver has time restrictions. Faithful port of <c>has_time_restrictions()</c>.</summary>
    public bool HasTimeRestrictions() => _hasTimeRestrictions;

    /// <summary>Sets the has-time-restrictions flag. Faithful port of <c>set_has_time_restrictions()</c>.</summary>
    public void SetHasTimeRestrictions(bool hasTimeRestrictions) => _hasTimeRestrictions = hasTimeRestrictions;

    /// <summary>True if there is a right traversable outbound intersecting edge.</summary>
    public bool HasRightTraversableOutboundIntersectingEdge() => _hasRightTraversableOutboundIntersectingEdge;

    /// <summary>Sets the right-traversable-outbound-intersecting-edge flag.</summary>
    public void SetHasRightTraversableOutboundIntersectingEdge(bool value)
        => _hasRightTraversableOutboundIntersectingEdge = value;

    /// <summary>True if there is a left traversable outbound intersecting edge.</summary>
    public bool HasLeftTraversableOutboundIntersectingEdge() => _hasLeftTraversableOutboundIntersectingEdge;

    /// <summary>Sets the left-traversable-outbound-intersecting-edge flag.</summary>
    public void SetHasLeftTraversableOutboundIntersectingEdge(bool value)
        => _hasLeftTraversableOutboundIntersectingEdge = value;

    /// <summary>True if the verbal pre-transition length should be included.</summary>
    public bool IncludeVerbalPreTransitionLength() => _includeVerbalPreTransitionLength;

    /// <summary>Sets the include-verbal-pre-transition-length flag.</summary>
    public void SetIncludeVerbalPreTransitionLength(bool value) => _includeVerbalPreTransitionLength = value;

    /// <summary>True if the maneuver contains an obvious maneuver. Faithful port of <c>contains_obvious_maneuver()</c>.</summary>
    public bool ContainsObviousManeuver() => _containsObviousManeuver;

    /// <summary>Sets the contains-obvious-maneuver flag. Faithful port of <c>set_contains_obvious_maneuver()</c>.</summary>
    public void SetContainsObviousManeuver(bool value) => _containsObviousManeuver = value;

    /// <summary>The roundabout exit count (spoke). Faithful port of <c>roundabout_exit_count()</c>.</summary>
    public uint RoundaboutExitCount() => _roundaboutExitCount;

    /// <summary>Sets the roundabout exit count. Faithful port of <c>set_roundabout_exit_count()</c>.</summary>
    public void SetRoundaboutExitCount(uint roundaboutExitCount) => _roundaboutExitCount = roundaboutExitCount;

    /// <summary>True if combined enter/exit roundabout. Faithful port of <c>has_combined_enter_exit_roundabout()</c>.</summary>
    public bool HasCombinedEnterExitRoundabout() => _hasCombinedEnterExitRoundabout;

    /// <summary>Sets the combined-enter-exit-roundabout flag.</summary>
    public void SetHasCombinedEnterExitRoundabout(bool value) => _hasCombinedEnterExitRoundabout = value;

    /// <summary>The roundabout length, in the specified units. Faithful port of <c>roundabout_length()</c>.</summary>
    public float RoundaboutLength(bool miles = false) => miles ? _roundaboutLength * Constants.MilePerKm : _roundaboutLength;

    /// <summary>Sets the roundabout length (kilometers). Faithful port of <c>set_roundabout_length()</c>.</summary>
    public void SetRoundaboutLength(float roundaboutKmLength) => _roundaboutLength = roundaboutKmLength;

    /// <summary>The roundabout exit length, in the specified units. Faithful port of <c>roundabout_exit_length()</c>.</summary>
    public float RoundaboutExitLength(bool miles = false)
        => miles ? _roundaboutExitLength * Constants.MilePerKm : _roundaboutExitLength;

    /// <summary>Sets the roundabout exit length (kilometers). Faithful port of <c>set_roundabout_exit_length()</c>.</summary>
    public void SetRoundaboutExitLength(float roundaboutExitKmLength) => _roundaboutExitLength = roundaboutExitKmLength;

    /// <summary>The roundabout exit street names. Faithful port of <c>roundabout_exit_street_names()</c>.</summary>
    public StreetNames RoundaboutExitStreetNames() => _roundaboutExitStreetNames;

    /// <summary>Sets the roundabout exit street names from pairs.</summary>
    public void SetRoundaboutExitStreetNames(IEnumerable<(string Name, bool IsRouteNumber)> names)
        => _roundaboutExitStreetNames = new StreetNamesUs(names);

    /// <summary>Sets the roundabout exit street names.</summary>
    public void SetRoundaboutExitStreetNames(StreetNames names) => _roundaboutExitStreetNames = names;

    /// <summary>True if there are roundabout exit street names.</summary>
    public bool HasRoundaboutExitStreetNames() => _roundaboutExitStreetNames.Count != 0;

    /// <summary>Clears the roundabout exit street names.</summary>
    public void ClearRoundaboutExitStreetNames() => _roundaboutExitStreetNames.Clear();

    /// <summary>The roundabout exit begin street names. Faithful port of <c>roundabout_exit_begin_street_names()</c>.</summary>
    public StreetNames RoundaboutExitBeginStreetNames() => _roundaboutExitBeginStreetNames;

    /// <summary>Sets the roundabout exit begin street names from pairs.</summary>
    public void SetRoundaboutExitBeginStreetNames(IEnumerable<(string Name, bool IsRouteNumber)> names)
        => _roundaboutExitBeginStreetNames = new StreetNamesUs(names);

    /// <summary>Sets the roundabout exit begin street names.</summary>
    public void SetRoundaboutExitBeginStreetNames(StreetNames names) => _roundaboutExitBeginStreetNames = names;

    /// <summary>True if there are roundabout exit begin street names.</summary>
    public bool HasRoundaboutExitBeginStreetNames() => _roundaboutExitBeginStreetNames.Count != 0;

    /// <summary>Clears the roundabout exit begin street names.</summary>
    public void ClearRoundaboutExitBeginStreetNames() => _roundaboutExitBeginStreetNames.Clear();

    /// <summary>The roundabout exit signs. Faithful port of <c>roundabout_exit_signs()</c>.</summary>
    public Signs RoundaboutExitSigns() => _roundaboutExitSigns;

    /// <summary>The mutable roundabout exit signs. Faithful port of <c>mutable_roundabout_exit_signs()</c>.</summary>
    public Signs MutableRoundaboutExitSigns() => _roundaboutExitSigns;

    /// <summary>The roundabout exit begin heading. Faithful port of <c>roundabout_exit_begin_heading()</c>.</summary>
    public uint RoundaboutExitBeginHeading() => _roundaboutExitBeginHeading;

    /// <summary>Sets the roundabout exit begin heading.</summary>
    public void SetRoundaboutExitBeginHeading(uint beginHeading) => _roundaboutExitBeginHeading = beginHeading;

    /// <summary>The roundabout exit turn degree. Faithful port of <c>roundabout_exit_turn_degree()</c>.</summary>
    public uint RoundaboutExitTurnDegree() => _roundaboutExitTurnDegree;

    /// <summary>Sets the roundabout exit turn degree.</summary>
    public void SetRoundaboutExitTurnDegree(uint turnDegree) => _roundaboutExitTurnDegree = turnDegree;

    /// <summary>The roundabout exit shape index. Faithful port of <c>roundabout_exit_shape_index()</c>.</summary>
    public uint RoundaboutExitShapeIndex() => _roundaboutExitShapeIndex;

    /// <summary>Sets the roundabout exit shape index.</summary>
    public void SetRoundaboutExitShapeIndex(uint shapeIndex) => _roundaboutExitShapeIndex = shapeIndex;

    /// <summary>True if a small end ramp fork was collapsed. Faithful port of <c>has_collapsed_small_end_ramp_fork()</c>.</summary>
    public bool HasCollapsedSmallEndRampFork() => _hasCollapsedSmallEndRampFork;

    /// <summary>Sets the collapsed-small-end-ramp-fork flag.</summary>
    public void SetHasCollapsedSmallEndRampFork(bool value) => _hasCollapsedSmallEndRampFork = value;

    /// <summary>True if a merge maneuver was collapsed. Faithful port of <c>has_collapsed_merge_maneuver()</c>.</summary>
    public bool HasCollapsedMergeManeuver() => _hasCollapsedMergeManeuver;

    /// <summary>Sets the collapsed-merge-maneuver flag.</summary>
    public void SetHasCollapsedMergeManeuver(bool value) => _hasCollapsedMergeManeuver = value;

    /// <summary>The travel mode. Faithful port of <c>travel_mode()</c>.</summary>
    public TravelMode GetTravelMode() => _travelMode;

    /// <summary>Sets the travel mode. Faithful port of <c>set_travel_mode()</c>.</summary>
    public void SetTravelMode(TravelMode travelMode) => _travelMode = travelMode;

    /// <summary>True if a rail maneuver. Faithful port of <c>rail()</c>.</summary>
    public bool Rail() => _rail;

    /// <summary>Sets the rail flag. Faithful port of <c>set_rail()</c>.</summary>
    public void SetRail(bool rail) => _rail = rail;

    /// <summary>True if a bus maneuver. Faithful port of <c>bus()</c>.</summary>
    public bool Bus() => _bus;

    /// <summary>Sets the bus flag. Faithful port of <c>set_bus()</c>.</summary>
    public void SetBus(bool bus) => _bus = bus;

    /// <summary>The vehicle type. Faithful port of <c>vehicle_type()</c>.</summary>
    public VehicleType GetVehicleType() => _vehicleType;

    /// <summary>Sets the vehicle type. Faithful port of <c>set_vehicle_type()</c>.</summary>
    public void SetVehicleType(VehicleType vehicleType) => _vehicleType = vehicleType;

    /// <summary>The pedestrian type. Faithful port of <c>pedestrian_type()</c>.</summary>
    public PedestrianType GetPedestrianType() => _pedestrianType;

    /// <summary>Sets the pedestrian type. Faithful port of <c>set_pedestrian_type()</c>.</summary>
    public void SetPedestrianType(PedestrianType pedestrianType) => _pedestrianType = pedestrianType;

    /// <summary>The bicycle type. Faithful port of <c>bicycle_type()</c>.</summary>
    public BicycleType GetBicycleType() => _bicycleType;

    /// <summary>Sets the bicycle type. Faithful port of <c>set_bicycle_type()</c>.</summary>
    public void SetBicycleType(BicycleType bicycleType) => _bicycleType = bicycleType;

    /// <summary>The transit type. Faithful port of <c>transit_type()</c>.</summary>
    public TransitType GetTransitType() => _transitType;

    /// <summary>Sets the transit type. Faithful port of <c>set_transit_type()</c>.</summary>
    public void SetTransitType(TransitType transitType) => _transitType = transitType;

    /// <summary>True if a transit connection. Faithful port of <c>transit_connection()</c>.</summary>
    public bool TransitConnection() => _transitConnection;

    /// <summary>Sets the transit connection flag. Faithful port of <c>set_transit_connection()</c>.</summary>
    public void SetTransitConnection(bool transitConnection) => _transitConnection = transitConnection;

    /// <summary>True if a transit maneuver type. Faithful port of <c>IsTransit()</c>.</summary>
    public bool IsTransit()
        => _type == DirectionsLegManeuverType.Transit
           || _type == DirectionsLegManeuverType.TransitTransfer
           || _type == DirectionsLegManeuverType.TransitRemainOn;

    /// <summary>The depart instruction (DEFERRED - prose).</summary>
    public string DepartInstruction() => _departInstruction;

    /// <summary>Sets the depart instruction. Faithful port of <c>set_depart_instruction()</c>.</summary>
    public void SetDepartInstruction(string departInstruction) => _departInstruction = departInstruction;

    /// <summary>The verbal depart instruction (DEFERRED - prose).</summary>
    public string VerbalDepartInstruction() => _verbalDepartInstruction;

    /// <summary>Sets the verbal depart instruction.</summary>
    public void SetVerbalDepartInstruction(string value) => _verbalDepartInstruction = value;

    /// <summary>The arrive instruction (DEFERRED - prose).</summary>
    public string ArriveInstruction() => _arriveInstruction;

    /// <summary>Sets the arrive instruction. Faithful port of <c>set_arrive_instruction()</c>.</summary>
    public void SetArriveInstruction(string arriveInstruction) => _arriveInstruction = arriveInstruction;

    /// <summary>The verbal arrive instruction (DEFERRED - prose).</summary>
    public string VerbalArriveInstruction() => _verbalArriveInstruction;

    /// <summary>Sets the verbal arrive instruction.</summary>
    public void SetVerbalArriveInstruction(string value) => _verbalArriveInstruction = value;

    /// <summary>The guidance views. Faithful port of <c>guidance_views()</c>.</summary>
    public IReadOnlyList<DirectionsLegGuidanceView> GuidanceViews() => _guidanceViews;

    /// <summary>The mutable guidance views. Faithful port of <c>mutable_guidance_views()</c>.</summary>
    public List<DirectionsLegGuidanceView> MutableGuidanceViews() => _guidanceViews;

    /// <summary>The bike share maneuver type. Faithful port of <c>bss_maneuver_type()</c>.</summary>
    public DirectionsLegManeuverBssManeuverType BssManeuverType() => _bssManeuverType;

    /// <summary>Sets the bike share maneuver type. Faithful port of <c>set_bss_maneuver_type()</c>.</summary>
    public void SetBssManeuverType(DirectionsLegManeuverBssManeuverType type) => _bssManeuverType = type;

    /// <summary>True if the maneuver has a long street name. Faithful port of <c>has_long_street_name()</c>.</summary>
    public bool HasLongStreetName() => _hasLongStreetName;

    /// <summary>Sets the long-street-name flag. Faithful port of <c>set_long_street_name()</c>.</summary>
    public void SetLongStreetName(bool hasLongStreetName) => _hasLongStreetName = hasLongStreetName;

    /// <summary>True if the maneuver is an elevator. Faithful port of <c>elevator()</c>.</summary>
    public bool Elevator() => _elevator;

    /// <summary>Sets the elevator flag. Faithful port of <c>set_elevator()</c>.</summary>
    public void SetElevator(bool elevator) => _elevator = elevator;

    /// <summary>True if the maneuver is indoor steps. Faithful port of <c>indoor_steps()</c>.</summary>
    public bool IndoorSteps() => _indoorSteps;

    /// <summary>Sets the indoor-steps flag. Faithful port of <c>set_indoor_steps()</c>.</summary>
    public void SetIndoorSteps(bool indoorSteps) => _indoorSteps = indoorSteps;

    /// <summary>True if the maneuver is an escalator. Faithful port of <c>escalator()</c>.</summary>
    public bool Escalator() => _escalator;

    /// <summary>Sets the escalator flag. Faithful port of <c>set_escalator()</c>.</summary>
    public void SetEscalator(bool escalator) => _escalator = escalator;

    /// <summary>True if the maneuver enters a building. Faithful port of <c>building_enter()</c>.</summary>
    public bool BuildingEnter() => _buildingEnter;

    /// <summary>Sets the building-enter flag. Faithful port of <c>set_building_enter()</c>.</summary>
    public void SetBuildingEnter(bool buildingEnter) => _buildingEnter = buildingEnter;

    /// <summary>True if the maneuver exits a building. Faithful port of <c>building_exit()</c>.</summary>
    public bool BuildingExit() => _buildingExit;

    /// <summary>Sets the building-exit flag. Faithful port of <c>set_building_exit()</c>.</summary>
    public void SetBuildingExit(bool buildingExit) => _buildingExit = buildingExit;

    /// <summary>The end level ref. Faithful port of <c>end_level_ref()</c>.</summary>
    public string EndLevelRef() => _endLevelRef;

    /// <summary>Sets the end level ref. Faithful port of <c>set_end_level_ref()</c>.</summary>
    public void SetEndLevelRef(string endLevelRef) => _endLevelRef = endLevelRef;

    /// <summary>True if the maneuver has level changes. Faithful port of <c>has_level_changes()</c>.</summary>
    public bool HasLevelChanges() => _hasLevelChanges;

    /// <summary>Sets the has-level-changes flag. Faithful port of <c>set_has_level_changes()</c>.</summary>
    public void SetHasLevelChanges(bool hasLevelChanges) => _hasLevelChanges = hasLevelChanges;
}
