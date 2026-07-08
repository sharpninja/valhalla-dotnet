// Faithful C# port of Valhalla odin EnhancedTripPath
// (valhalla/odin/enhancedtrippath.h + src/odin/enhancedtrippath.cc) @ 3.7.0.
// Source: valhalla/odin/enhancedtrippath.h, src/odin/enhancedtrippath.cc
//
// These classes wrap the ported Thor TripLeg / TripNode / TripEdge / TripIntersectingEdge result
// objects (SharpNinja.Valhalla.Thor) with the navigation helpers the maneuver
// builder needs: turn degrees, intersecting-edge classification (right/left/similar/forward/wider),
// drivable counts, straightest-edge math, and per-use / per-type predicates. Public members are
// PascalCase; every threshold, comparison, and lambda mirrors the C++ exactly.
//
// PORT-NOTE: The C++ wraps mutable protobuf objects (TripLeg_Edge* etc.) so that turn-lane activation
// can write state back into the proto. Here we wrap the ported result objects by reference (they are
// reference types), so writes are visible to callers identically.
//
// PORT-NOTE: The ported Thor TripEdge.TurnLanes is a List<ushort> of direction masks only (no per-lane
// state, as the route-rendering subset does not carry it). The proto TurnLane that odin mutates also
// has state and active_direction. To port the turn-lane activation algorithm (and its gtests)
// faithfully, EnhancedTripLeg_Edge maintains a parallel list of TurnLaneState records keyed to the
// edge's masks. See TurnLaneState below.
//
// PORT-NOTE (DEFER): the LOGGING_LEVEL_TRACE ToString / ToParameterString debug emitters and the
// decode_levels / GetLevelRef tagged-value parsing (a baldr levels feature outside the maneuver
// foundation) are omitted. GetLevelRef returns the (empty) level_refs fallback.
//
// PORT-NOTE: GetOrigin / GetDestination and the location() accessors operate on proto Location
// objects that the ported TripLeg does not carry; they are omitted. GetCountryCode / GetStateCode /
// GetAdmin operate on the ported TripAdmin table and are kept.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Turn-lane state. Faithful port of the proto <c>TurnLane.State</c> enum that odin mutates during
/// turn-lane activation.
/// </summary>
public enum TurnLaneState
{
    /// <summary>Lane is not valid for the maneuver.</summary>
    Invalid = 0,

    /// <summary>Lane is valid (in the maneuver direction) but not the active lane.</summary>
    Valid = 1,

    /// <summary>Lane is active for the maneuver.</summary>
    Active = 2,
}

/// <summary>
/// A turn lane with its direction mask plus the mutable state and active direction that odin sets
/// during activation. Faithful port of the proto <c>TurnLane</c> fields odin uses.
/// </summary>
public sealed class TurnLane
{
    /// <summary>Constructs a turn lane from its direction mask.</summary>
    /// <param name="directionsMask">The OR'd turn-lane direction bits (see <see cref="TurnLaneConstants"/>).</param>
    public TurnLane(ushort directionsMask)
    {
        DirectionsMask = directionsMask;
        State = TurnLaneState.Invalid;
        ActiveDirection = 0;
    }

    /// <summary>The turn-lane direction mask. Faithful port of <c>directions_mask()</c>.</summary>
    public ushort DirectionsMask { get; }

    /// <summary>The lane state (defaults to <see cref="TurnLaneState.Invalid"/>). Faithful port of <c>state()</c>.</summary>
    public TurnLaneState State { get; set; }

    /// <summary>The active direction set for valid/active lanes. Faithful port of <c>active_direction()</c>.</summary>
    public ushort ActiveDirection { get; set; }
}

/// <summary>
/// Per-edge tally of intersecting edges to the right and left of the path, with "similar" and
/// "traversable outbound" sub-counts. Faithful port of <c>struct IntersectingEdgeCounts</c>.
/// </summary>
public struct IntersectingEdgeCounts
{
    /// <summary>Constructs a zeroed counts struct.</summary>
    public static IntersectingEdgeCounts Create() => new IntersectingEdgeCounts();

    /// <summary>Constructs counts with the eight explicit values (matching the C++ ctor order).</summary>
    public IntersectingEdgeCounts(uint r, uint rs, uint rdo, uint rsdo, uint l, uint ls, uint ldo, uint lsdo)
    {
        Right = r;
        RightSimilar = rs;
        RightTraversableOutbound = rdo;
        RightSimilarTraversableOutbound = rsdo;
        Left = l;
        LeftSimilar = ls;
        LeftTraversableOutbound = ldo;
        LeftSimilarTraversableOutbound = lsdo;
    }

    /// <summary>Number of intersecting edges to the right.</summary>
    public uint Right;

    /// <summary>Number of similar (turn-degree) intersecting edges to the right.</summary>
    public uint RightSimilar;

    /// <summary>Number of traversable-outbound intersecting edges to the right.</summary>
    public uint RightTraversableOutbound;

    /// <summary>Number of similar traversable-outbound intersecting edges to the right.</summary>
    public uint RightSimilarTraversableOutbound;

    /// <summary>Number of intersecting edges to the left.</summary>
    public uint Left;

    /// <summary>Number of similar (turn-degree) intersecting edges to the left.</summary>
    public uint LeftSimilar;

    /// <summary>Number of traversable-outbound intersecting edges to the left.</summary>
    public uint LeftTraversableOutbound;

    /// <summary>Number of similar traversable-outbound intersecting edges to the left.</summary>
    public uint LeftSimilarTraversableOutbound;

    /// <summary>Resets all counts to zero. Faithful port of <c>clear()</c>.</summary>
    public void Clear()
    {
        Right = 0;
        RightSimilar = 0;
        RightTraversableOutbound = 0;
        RightSimilarTraversableOutbound = 0;
        Left = 0;
        LeftSimilar = 0;
        LeftTraversableOutbound = 0;
        LeftSimilarTraversableOutbound = 0;
    }
}

/// <summary>
/// Internal constants for the enhanced trip path (anonymous-namespace constants in the C++).
/// </summary>
internal static class EnhancedTripPathConstants
{
    /// <summary>Kilometers (~quarter mile). Faithful port of <c>kShortRemainingDistanceThreshold</c>.</summary>
    public const float ShortRemainingDistanceThreshold = 0.402f;

    /// <summary>Max lower road class delta. Faithful port of <c>kSignificantRoadClassThreshold</c>.</summary>
    public const int SignificantRoadClassThreshold = 2;

    /// <summary>Max similar straight turn degree delta. Faithful port of <c>kSimilarStraightThreshold</c>.</summary>
    public const int SimilarStraightThreshold = 30;

    /// <summary>Buffer between straight delta values. Faithful port of <c>kIsStraightestBuffer</c>.</summary>
    public const int IsStraightestBuffer = 10;

    /// <summary>Backward turn degree lower bound. Faithful port of <c>kBackwardTurnDegreeLowerBound</c>.</summary>
    public const uint BackwardTurnDegreeLowerBound = 124;

    /// <summary>Backward turn degree upper bound. Faithful port of <c>kBackwardTurnDegreeUpperBound</c>.</summary>
    public const uint BackwardTurnDegreeUpperBound = 236;

    // TODO: in the future might have to have dynamic angle based on road class and lane count
    public static bool IsForkForward(uint turnDegree) => turnDegree > 339 || turnDegree < 21;

    public static bool IsRelativeStraight(uint turnDegree) => turnDegree > 329 || turnDegree < 31;

    public static bool IsForward(uint turnDegree) => turnDegree > 314 || turnDegree < 46;

    public static bool IsWiderForward(uint turnDegree) => turnDegree > 304 || turnDegree < 56;

    public static int GetTurnDegreeDelta(uint pathTurnDegree, uint xedgeTurnDegree)
    {
        int pathXedgeTurnDegreeDelta = Math.Abs((int)pathTurnDegree - (int)xedgeTurnDegree);
        if (pathXedgeTurnDegreeDelta > 180)
        {
            pathXedgeTurnDegreeDelta = 360 - pathXedgeTurnDegreeDelta;
        }

        return pathXedgeTurnDegreeDelta;
    }
}

/// <summary>
/// Wraps a <see cref="TripLeg"/> with navigation helpers used by the maneuver builder. Faithful port
/// of <c>valhalla::odin::EnhancedTripLeg</c>.
/// </summary>
public sealed class EnhancedTripLeg
{
    private readonly TripLeg _tripPath;

    /// <summary>Constructs an enhanced trip leg wrapping the supplied <see cref="TripLeg"/>.</summary>
    public EnhancedTripLeg(TripLeg tripPath) => _tripPath = tripPath;

    /// <summary>The encoded shape of the leg. Faithful port of <c>shape()</c>.</summary>
    public string Shape() => _tripPath.EncodedShape;

    /// <summary>Number of nodes in the leg. Faithful port of <c>node_size()</c>.</summary>
    public int NodeSize() => _tripPath.Nodes.Count;

    /// <summary>The node at the specified index. Faithful port of <c>node(index)</c> / <c>mutable_node(index)</c>.</summary>
    public TripNode Node(int index) => _tripPath.Nodes[index];

    /// <summary>The node list. Faithful port of <c>node()</c>.</summary>
    public IReadOnlyList<TripNode> Nodes() => _tripPath.Nodes;

    /// <summary>Number of admin records. Faithful port of <c>admin_size()</c>.</summary>
    public int AdminSize() => _tripPath.Admins.Count;

    /// <summary>The admin record at the specified index. Faithful port of <c>mutable_admin(index)</c>.</summary>
    public TripAdmin Admin(int index) => _tripPath.Admins[index];

    /// <summary>The OSM changeset id. Faithful port of <c>osm_changeset()</c>.</summary>
    public ulong OsmChangeset() => _tripPath.OsmChangeset;

    /// <summary>The leg summary flags (toll / ferry / highway). Faithful port of <c>summary()</c>.</summary>
    public TripSummary Summary() => _tripPath.Summary;

    /// <summary>Returns an enhanced node for the specified node index. Faithful port of <c>GetEnhancedNode()</c>.</summary>
    public EnhancedTripLeg_Node GetEnhancedNode(int nodeIndex) => new EnhancedTripLeg_Node(Node(nodeIndex));

    /// <summary>
    /// Returns an enhanced edge for the edge that ends at (node_index - delta), or null. Faithful
    /// port of <c>GetPrevEdge()</c>.
    /// </summary>
    public EnhancedTripLeg_Edge? GetPrevEdge(int nodeIndex, int delta = 1)
    {
        int index = nodeIndex - delta;
        if (IsValidNodeIndex(index) && Node(index).Edge != null)
        {
            return new EnhancedTripLeg_Edge(Node(index).Edge!);
        }

        return null;
    }

    /// <summary>Returns the enhanced edge that leaves the node at the specified index. Faithful port of <c>GetCurrEdge()</c>.</summary>
    public EnhancedTripLeg_Edge? GetCurrEdge(int nodeIndex) => GetNextEdge(nodeIndex, 0);

    /// <summary>
    /// Returns an enhanced edge for the edge that leaves (node_index + delta), or null. Faithful
    /// port of <c>GetNextEdge()</c>.
    /// </summary>
    public EnhancedTripLeg_Edge? GetNextEdge(int nodeIndex, int delta = 1)
    {
        int index = nodeIndex + delta;
        if (IsValidNodeIndex(index) && !IsLastNodeIndex(index) && Node(index).Edge != null)
        {
            return new EnhancedTripLeg_Edge(Node(index).Edge!);
        }

        return null;
    }

    /// <summary>True if the node index is in range. Faithful port of <c>IsValidNodeIndex()</c>.</summary>
    public bool IsValidNodeIndex(int nodeIndex) => nodeIndex >= 0 && nodeIndex < NodeSize();

    /// <summary>True if the node index is the first. Faithful port of <c>IsFirstNodeIndex()</c>.</summary>
    public bool IsFirstNodeIndex(int nodeIndex) => nodeIndex == 0;

    /// <summary>True if the node index is the last. Faithful port of <c>IsLastNodeIndex()</c>.</summary>
    public bool IsLastNodeIndex(int nodeIndex) => IsValidNodeIndex(nodeIndex) && nodeIndex == NodeSize() - 1;

    /// <summary>The last node index. Faithful port of <c>GetLastNodeIndex()</c>.</summary>
    public int GetLastNodeIndex() => NodeSize() - 1;

    /// <summary>Returns the admin record at the specified index. Faithful port of <c>GetAdmin()</c>.</summary>
    public TripAdmin GetAdmin(int index) => Admin(index);

    /// <summary>The country code at the specified node. Faithful port of <c>GetCountryCode()</c>.</summary>
    public string GetCountryCode(int nodeIndex) => GetAdmin((int)Node(nodeIndex).AdminIndex).CountryCode;

    /// <summary>The state code at the specified node. Faithful port of <c>GetStateCode()</c>.</summary>
    public string GetStateCode(int nodeIndex) => GetAdmin((int)Node(nodeIndex).AdminIndex).StateCode;

    /// <summary>The total length of the leg in the specified units. Faithful port of <c>GetLength()</c>.</summary>
    public float GetLength(bool miles = false)
    {
        float length = 0.0f;
        foreach (TripNode n in _tripPath.Nodes)
        {
            if (n.Edge != null)
            {
                length += n.Edge.LengthKm;
            }
        }

        if (miles)
        {
            return length * Constants.MilePerKm;
        }

        return length;
    }
}

/// <summary>
/// Wraps a <see cref="TripEdge"/> with navigation helpers. Faithful port of
/// <c>valhalla::odin::EnhancedTripLeg_Edge</c>.
/// </summary>
public sealed class EnhancedTripLeg_Edge
{
    private readonly TripEdge _edge;
    private List<TurnLane>? _turnLanes;

    /// <summary>Constructs an enhanced edge wrapping the supplied <see cref="TripEdge"/>.</summary>
    public EnhancedTripLeg_Edge(TripEdge edge) => _edge = edge;

    /// <summary>Number of street names. Faithful port of <c>name_size()</c>.</summary>
    public int NameSize() => _edge.Names.Count;

    /// <summary>The street names. Faithful port of <c>name()</c>.</summary>
    public IReadOnlyList<string> Name() => _edge.Names;

    /// <summary>Length in kilometers. Faithful port of <c>length_km()</c>.</summary>
    public float LengthKm() => _edge.LengthKm;

    /// <summary>Average speed. Faithful port of <c>speed()</c>.</summary>
    public double Speed() => _edge.SpeedKph;

    /// <summary>Road class. Faithful port of <c>road_class()</c>.</summary>
    public RoadClass GetRoadClass() => _edge.RoadClass;

    /// <summary>Begin heading. Faithful port of <c>begin_heading()</c>.</summary>
    public uint BeginHeading() => _edge.BeginHeading;

    /// <summary>Sets the begin heading. Faithful port of <c>set_begin_heading()</c>.</summary>
    public void SetBeginHeading(uint value) => _edge.BeginHeading = value;

    /// <summary>End heading. Faithful port of <c>end_heading()</c>.</summary>
    public uint EndHeading() => _edge.EndHeading;

    /// <summary>Sets the end heading. Faithful port of <c>set_end_heading()</c>.</summary>
    public void SetEndHeading(uint value) => _edge.EndHeading = value;

    /// <summary>Begin shape index. Faithful port of <c>begin_shape_index()</c>.</summary>
    public uint BeginShapeIndex() => _edge.BeginShapeIndex;

    /// <summary>End shape index. Faithful port of <c>end_shape_index()</c>.</summary>
    public uint EndShapeIndex() => _edge.EndShapeIndex;

    /// <summary>Traversability. Faithful port of <c>traversability()</c>.</summary>
    public TripTraversability Traversability() => _edge.Traversability;

    /// <summary>Use. Faithful port of <c>use()</c>.</summary>
    public Use GetUse() => _edge.Use;

    /// <summary>True if a drive mode (has a vehicle type). Faithful port of <c>has_vehicle_type()</c>.</summary>
    public bool HasVehicleType() => _edge.Mode == TravelMode.Drive;

    /// <summary>True if toll. Faithful port of <c>toll()</c>.</summary>
    public bool Toll() => _edge.Toll;

    /// <summary>True if a time-based restriction applies. Faithful port of <c>has_time_restrictions()</c>.</summary>
    public bool HasTimeRestrictions() => _edge.HasTimeRestrictions;

    /// <summary>True if unpaved. Faithful port of <c>unpaved()</c>.</summary>
    public bool Unpaved() => _edge.Unpaved;

    /// <summary>True if a tunnel. Faithful port of <c>tunnel()</c>.</summary>
    public bool Tunnel() => _edge.Tunnel;

    /// <summary>True if a bridge. Faithful port of <c>bridge()</c>.</summary>
    public bool Bridge() => _edge.Bridge;

    /// <summary>True if a roundabout. Faithful port of <c>roundabout()</c>.</summary>
    public bool Roundabout() => _edge.Roundabout;

    /// <summary>True if an internal intersection edge. Faithful port of <c>internal_intersection()</c>.</summary>
    public bool InternalIntersection() => _edge.InternalIntersection;

    /// <summary>True if drive-on-right (NOT drive-on-left). Faithful port of <c>drive_on_right()</c>.</summary>
    public bool DriveOnRight() => !_edge.DriveOnLeft;

    /// <summary>Surface. Faithful port of <c>surface()</c>.</summary>
    public Surface GetSurface() => _edge.Surface;

    /// <summary>True if the edge carries any sign. Faithful port of <c>has_sign()</c>.</summary>
    public bool HasSign() => _edge.Sign != null && !_edge.Sign.IsEmpty;

    /// <summary>The sign information. Faithful port of <c>sign()</c>.</summary>
    public TripSign? Sign() => _edge.Sign;

    /// <summary>Travel mode. Faithful port of <c>travel_mode()</c>.</summary>
    public TravelMode GetTravelMode() => _edge.Mode;

    /// <summary>The directed edge id. Faithful port of <c>id()</c>.</summary>
    public ulong Id() => _edge.EdgeId.Value;

    /// <summary>The OSM way id. Faithful port of <c>way_id()</c>.</summary>
    public ulong WayId() => _edge.WayId;

    /// <summary>Lane count. Faithful port of <c>lane_count()</c>.</summary>
    public uint LaneCount() => _edge.LaneCount;

    /// <summary>Speed limit. Faithful port of <c>speed_limit()</c>.</summary>
    public uint SpeedLimit() => _edge.SpeedLimit;

    /// <summary>Default speed. Faithful port of <c>default_speed()</c>.</summary>
    public uint DefaultSpeed() => _edge.DefaultSpeed;

    /// <summary>Truck speed. Faithful port of <c>truck_speed()</c>.</summary>
    public uint TruckSpeed() => _edge.TruckSpeed;

    /// <summary>True if on a designated truck route. Faithful port of <c>truck_route()</c>.</summary>
    public bool TruckRoute() => _edge.TruckRoute;

    /// <summary>True if destination-only. Faithful port of <c>destination_only()</c>.</summary>
    public bool DestinationOnly() => _edge.DestinationOnly;

    /// <summary>Number of turn lanes. Faithful port of <c>turn_lanes_size()</c>.</summary>
    public int TurnLanesSize() => TurnLanes().Count;

    /// <summary>
    /// The turn lanes (with mutable state). Faithful port of <c>turn_lanes()</c> /
    /// <c>mutable_turn_lanes()</c>. The state-carrying <see cref="TurnLane"/> list is constructed
    /// lazily from the underlying edge's direction masks (see file header PORT-NOTE).
    /// </summary>
    public List<TurnLane> TurnLanes()
    {
        if (_turnLanes == null)
        {
            _turnLanes = new List<TurnLane>(_edge.TurnLanes.Count);
            foreach (ushort mask in _edge.TurnLanes)
            {
                _turnLanes.Add(new TurnLane(mask));
            }
        }

        return _turnLanes;
    }

    /// <summary>True if the edge has no names. Faithful port of <c>IsUnnamed()</c>.</summary>
    public bool IsUnnamed() => NameSize() == 0;

    /// <summary>True if a road use (road or service road). Faithful port of <c>IsRoadUse()</c>.</summary>
    public bool IsRoadUse() => GetUse() == Use.Road || GetUse() == Use.ServiceRoad;

    /// <summary>True if a ramp use. Faithful port of <c>IsRampUse()</c>.</summary>
    public bool IsRampUse() => GetUse() == Use.Ramp;

    /// <summary>True if a turn channel use. Faithful port of <c>IsTurnChannelUse()</c>.</summary>
    public bool IsTurnChannelUse() => GetUse() == Use.TurnChannel;

    /// <summary>True if a track use. Faithful port of <c>IsTrackUse()</c>.</summary>
    public bool IsTrackUse() => GetUse() == Use.Track;

    /// <summary>True if a driveway use. Faithful port of <c>IsDrivewayUse()</c>.</summary>
    public bool IsDrivewayUse() => GetUse() == Use.Driveway;

    /// <summary>True if an alley use. Faithful port of <c>IsAlleyUse()</c>.</summary>
    public bool IsAlleyUse() => GetUse() == Use.Alley;

    /// <summary>True if a parking aisle use. Faithful port of <c>IsParkingAisleUse()</c>.</summary>
    public bool IsParkingAisleUse() => GetUse() == Use.ParkingAisle;

    /// <summary>True if an emergency access use. Faithful port of <c>IsEmergencyAccessUse()</c>.</summary>
    public bool IsEmergencyAccessUse() => GetUse() == Use.EmergencyAccess;

    /// <summary>True if a drive-thru use. Faithful port of <c>IsDriveThruUse()</c>.</summary>
    public bool IsDriveThruUse() => GetUse() == Use.DriveThru;

    /// <summary>True if a cul-de-sac use. Faithful port of <c>IsCuldesacUse()</c>.</summary>
    public bool IsCuldesacUse() => GetUse() == Use.Culdesac;

    /// <summary>True if a cycleway use. Faithful port of <c>IsCyclewayUse()</c>.</summary>
    public bool IsCyclewayUse() => GetUse() == Use.Cycleway;

    /// <summary>True if a mountain bike use. Faithful port of <c>IsMountainBikeUse()</c>.</summary>
    public bool IsMountainBikeUse() => GetUse() == Use.MountainBike;

    /// <summary>True if a sidewalk use. Faithful port of <c>IsSidewalkUse()</c>.</summary>
    public bool IsSidewalkUse() => GetUse() == Use.Sidewalk;

    /// <summary>True if a footway use (footway or pedestrian crossing). Faithful port of <c>IsFootwayUse()</c>.</summary>
    public bool IsFootwayUse() => GetUse() == Use.Footway || GetUse() == Use.PedestrianCrossing;

    /// <summary>True if a pedestrian crossing use. Faithful port of <c>IsPedestrianCrossingUse()</c>.</summary>
    public bool IsPedestrianCrossingUse() => GetUse() == Use.PedestrianCrossing;

    /// <summary>True if an elevator use. Faithful port of <c>IsElevatorUse()</c>.</summary>
    public bool IsElevatorUse() => GetUse() == Use.Elevator;

    /// <summary>True if a steps use. Faithful port of <c>IsStepsUse()</c>.</summary>
    public bool IsStepsUse() => GetUse() == Use.Steps;

    /// <summary>True if an escalator use. Faithful port of <c>IsEscalatorUse()</c>.</summary>
    public bool IsEscalatorUse() => GetUse() == Use.Escalator;

    /// <summary>True if a path use. Faithful port of <c>IsPathUse()</c>.</summary>
    public bool IsPathUse() => GetUse() == Use.Path;

    /// <summary>True if a pedestrian use. Faithful port of <c>IsPedestrianUse()</c>.</summary>
    public bool IsPedestrianUse() => GetUse() == Use.Pedestrian;

    /// <summary>True if a bridleway use. Faithful port of <c>IsBridlewayUse()</c>.</summary>
    public bool IsBridlewayUse() => GetUse() == Use.Bridleway;

    /// <summary>True if a rest area use. Faithful port of <c>IsRestAreaUse()</c>.</summary>
    public bool IsRestAreaUse() => GetUse() == Use.RestArea;

    /// <summary>True if a service area use. Faithful port of <c>IsServiceAreaUse()</c>.</summary>
    public bool IsServiceAreaUse() => GetUse() == Use.ServiceArea;

    /// <summary>True if an "other" use. Faithful port of <c>IsOtherUse()</c>.</summary>
    public bool IsOtherUse() => GetUse() == Use.Other;

    /// <summary>True if a ferry use. Faithful port of <c>IsFerryUse()</c>.</summary>
    public bool IsFerryUse() => GetUse() == Use.Ferry;

    /// <summary>True if a rail ferry use. Faithful port of <c>IsRailFerryUse()</c>.</summary>
    public bool IsRailFerryUse() => GetUse() == Use.RailFerry;

    /// <summary>True if a rail use. Faithful port of <c>IsRailUse()</c>.</summary>
    public bool IsRailUse() => GetUse() == Use.Rail;

    /// <summary>True if a bus use. Faithful port of <c>IsBusUse()</c>.</summary>
    public bool IsBusUse() => GetUse() == Use.Bus;

    /// <summary>True if an egress connection use. Faithful port of <c>IsEgressConnectionUse()</c>.</summary>
    public bool IsEgressConnectionUse() => GetUse() == Use.EgressConnection;

    /// <summary>True if a platform connection use. Faithful port of <c>IsPlatformConnectionUse()</c>.</summary>
    public bool IsPlatformConnectionUse() => GetUse() == Use.PlatformConnection;

    /// <summary>True if a transit connection use. Faithful port of <c>IsTransitConnectionUse()</c>.</summary>
    public bool IsTransitConnectionUse() => GetUse() == Use.TransitConnection;

    /// <summary>True if a construction use. Faithful port of <c>IsConstructionUse()</c>.</summary>
    public bool IsConstructionUse() => GetUse() == Use.Construction;

    /// <summary>True if any transit connection use. Faithful port of <c>IsTransitConnection()</c>.</summary>
    public bool IsTransitConnection() => IsTransitConnectionUse() || IsEgressConnectionUse() || IsPlatformConnectionUse();

    /// <summary>True if an unnamed walkway. Faithful port of <c>IsUnnamedWalkway()</c>.</summary>
    public bool IsUnnamedWalkway() => IsUnnamed() && IsFootwayUse();

    /// <summary>True if an unnamed cycleway. Faithful port of <c>IsUnnamedCycleway()</c>.</summary>
    public bool IsUnnamedCycleway() => IsUnnamed() && IsCyclewayUse();

    /// <summary>True if an unnamed mountain bike trail. Faithful port of <c>IsUnnamedMountainBikeTrail()</c>.</summary>
    public bool IsUnnamedMountainBikeTrail() => IsUnnamed() && IsMountainBikeUse();

    /// <summary>True if a highway (motorway and not ramp/turn-channel). Faithful port of <c>IsHighway()</c>.</summary>
    public bool IsHighway() => GetRoadClass() == RoadClass.Motorway && !IsRampUse() && !IsTurnChannelUse();

    /// <summary>True if a one-way edge. Faithful port of <c>IsOneway()</c>.</summary>
    public bool IsOneway()
        => Traversability() == TripTraversability.Forward || Traversability() == TripTraversability.Backward;

    /// <summary>True if forward for the given turn degree. Faithful port of <c>IsForward()</c>.</summary>
    public bool IsForward(uint prev2currTurnDegree) => EnhancedTripPathConstants.IsForward(prev2currTurnDegree);

    /// <summary>True if fork-forward for the given turn degree. Faithful port of <c>IsForkForward()</c>.</summary>
    public bool IsForkForward(uint prev2currTurnDegree) => EnhancedTripPathConstants.IsForkForward(prev2currTurnDegree);

    /// <summary>True if wider-forward for the given turn degree. Faithful port of <c>IsWiderForward()</c>.</summary>
    public bool IsWiderForward(uint prev2currTurnDegree) => EnhancedTripPathConstants.IsWiderForward(prev2currTurnDegree);

    /// <summary>
    /// True if the path turn degree is the straightest compared to the straightest intersecting edge
    /// turn degree. Faithful port of <c>IsStraightest()</c>.
    /// </summary>
    public bool IsStraightest(uint prev2currTurnDegree, uint straightestXedgeTurnDegree)
    {
        if (IsWiderForward(prev2currTurnDegree))
        {
            uint pathStraightDelta = prev2currTurnDegree > 180 ? 360 - prev2currTurnDegree : prev2currTurnDegree;
            uint xedgeStraightDelta = straightestXedgeTurnDegree > 180
                ? 360 - straightestXedgeTurnDegree
                : straightestXedgeTurnDegree;
            int pathStraightXedgeStraightDelta =
                EnhancedTripPathConstants.GetTurnDegreeDelta(pathStraightDelta, xedgeStraightDelta);

            return pathStraightXedgeStraightDelta > EnhancedTripPathConstants.IsStraightestBuffer
                ? pathStraightDelta <= xedgeStraightDelta
                : true;
        }

        return false;
    }

    /// <summary>The (name, is-route-number) list. Faithful port of <c>GetNameList()</c>.</summary>
    public List<(string Name, bool IsRouteNumber)> GetNameList()
    {
        // PORT-NOTE: The ported TripEdge.Names is a string list without the is-route-number flag
        // (the route-rendering subset does not carry it). The C++ reads name.is_route_number(); here
        // it is reported as false. Maneuver-structure consumers that need route-number distinction
        // get it from StreetNamesUs base-name parsing instead.
        var nameList = new List<(string, bool)>(_edge.Names.Count);
        foreach (string name in _edge.Names)
        {
            nameList.Add((name, false));
        }

        return nameList;
    }

    /// <summary>
    /// The level refs. Faithful port of <c>GetLevelRef()</c>. The tagged-value decode path is
    /// DEFERRED (see file header); returns an empty list.
    /// </summary>
    public List<string> GetLevelRef() => new List<string>();

    /// <summary>The length in the specified units. Faithful port of <c>GetLength()</c>.</summary>
    public float GetLength(bool miles = false) => miles ? LengthKm() * Constants.MilePerKm : LengthKm();

    /// <summary>True if any turn lane is active. Faithful port of <c>HasActiveTurnLane()</c>.</summary>
    public bool HasActiveTurnLane()
    {
        foreach (TurnLane turnLane in TurnLanes())
        {
            if (turnLane.State == TurnLaneState.Active)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if any turn lane is non-directional. Faithful port of <c>HasNonDirectionalTurnLane()</c>.</summary>
    public bool HasNonDirectionalTurnLane()
    {
        foreach (TurnLane turnLane in TurnLanes())
        {
            // Return true if a directions mask is empty or none for a turn lane
            if (turnLane.DirectionsMask == TurnLaneConstants.TurnLaneEmpty
                || (turnLane.DirectionsMask & TurnLaneConstants.TurnLaneNone) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if any turn lane has the specified direction. Faithful port of <c>HasTurnLane()</c>.</summary>
    public bool HasTurnLane(ushort turnLaneDirection)
    {
        foreach (TurnLane turnLane in TurnLanes())
        {
            if ((turnLane.DirectionsMask & turnLaneDirection) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Activates matching turn lanes from the left up to <paramref name="activatedMax"/>. Faithful
    /// port of <c>ActivateTurnLanesFromLeft()</c>.
    /// </summary>
    public ushort ActivateTurnLanesFromLeft(
        ushort turnLaneDirection,
        DirectionsLegManeuverType currManeuverType,
        ushort activatedMax = ushort.MaxValue)
    {
        ushort activatedCount = 0;

        // Make sure turn lane has a direction
        if (HasNonDirectionalTurnLane())
        {
            return activatedCount;
        }

        foreach (TurnLane turnLane in TurnLanes())
        {
            // Process lanes matching the turn_lane_direction
            if ((turnLane.DirectionsMask & turnLaneDirection) != 0)
            {
                // activate upto activated_max lanes
                if (activatedCount < activatedMax)
                {
                    turnLane.State = TurnLaneState.Active;
                    ++activatedCount;
                }
                else if (currManeuverType != DirectionsLegManeuverType.UturnLeft)
                {
                    // Mark non-active lane in the same direction as the maneuver, as valid except if
                    // we're taking a left uturn, in which case only the left-most left lane needs to
                    // be active (which would've been activated above)
                    turnLane.State = TurnLaneState.Valid;
                }

                // Set the active direction for active & valid lanes
                turnLane.ActiveDirection = turnLaneDirection;
            }
        }

        return activatedCount;
    }

    /// <summary>
    /// Activates matching turn lanes from the right up to <paramref name="activatedMax"/>. Faithful
    /// port of <c>ActivateTurnLanesFromRight()</c>.
    /// </summary>
    public ushort ActivateTurnLanesFromRight(
        ushort turnLaneDirection,
        DirectionsLegManeuverType currManeuverType,
        ushort activatedMax = ushort.MaxValue)
    {
        ushort activatedCount = 0;

        if (HasNonDirectionalTurnLane())
        {
            return activatedCount;
        }

        List<TurnLane> turnLanes = TurnLanes();
        for (int i = turnLanes.Count - 1; i >= 0; --i)
        {
            TurnLane turnLane = turnLanes[i];
            if ((turnLane.DirectionsMask & turnLaneDirection) != 0)
            {
                // activate upto activated_max lanes
                if (activatedCount < activatedMax)
                {
                    turnLane.State = TurnLaneState.Active;
                    ++activatedCount;
                }
                else if (currManeuverType != DirectionsLegManeuverType.UturnRight)
                {
                    // Mark non-active lane in the same direction as the maneuver, as valid except if
                    // we're taking a right uturn, in which case only the right-most right lane needs
                    // to be active (which would've been activated above)
                    turnLane.State = TurnLaneState.Valid;
                }

                turnLane.ActiveDirection = turnLaneDirection;
            }
        }

        return activatedCount;
    }

    /// <summary>
    /// Activates turn lanes based on the current/next maneuver type and remaining distance. Faithful
    /// port of <c>ActivateTurnLanes()</c>.
    /// </summary>
    public ushort ActivateTurnLanes(
        ushort turnLaneDirection,
        float remainingStepDistance,
        DirectionsLegManeuverType currManeuverType,
        DirectionsLegManeuverType nextManeuverType)
    {
        if (currManeuverType == DirectionsLegManeuverType.UturnLeft
            && turnLaneDirection != TurnLaneConstants.TurnLaneReverse)
        {
            // Activate the left most turn lane
            return ActivateTurnLanesFromLeft(turnLaneDirection, currManeuverType, 1);
        }

        if (currManeuverType == DirectionsLegManeuverType.UturnRight
            && turnLaneDirection != TurnLaneConstants.TurnLaneReverse)
        {
            // Activate the right most turn lane
            return ActivateTurnLanesFromRight(turnLaneDirection, currManeuverType, 1);
        }

        if (remainingStepDistance < EnhancedTripPathConstants.ShortRemainingDistanceThreshold
            && !(nextManeuverType == DirectionsLegManeuverType.Becomes
                 || nextManeuverType == DirectionsLegManeuverType.Continue
                 || nextManeuverType == DirectionsLegManeuverType.RampStraight
                 || nextManeuverType == DirectionsLegManeuverType.StayStraight))
        {
            // If remaining step distance is less than short threshold and next maneuver is not a
            // straight, activate only specific matching turn lanes
            switch (nextManeuverType)
            {
                case DirectionsLegManeuverType.SlightLeft:
                case DirectionsLegManeuverType.Left:
                case DirectionsLegManeuverType.SharpLeft:
                case DirectionsLegManeuverType.UturnLeft:
                case DirectionsLegManeuverType.RampLeft:
                case DirectionsLegManeuverType.ExitLeft:
                case DirectionsLegManeuverType.StayLeft:
                case DirectionsLegManeuverType.DestinationLeft:
                case DirectionsLegManeuverType.MergeLeft:
                    return ActivateTurnLanesFromLeft(turnLaneDirection, currManeuverType, 1);
                case DirectionsLegManeuverType.SlightRight:
                case DirectionsLegManeuverType.Right:
                case DirectionsLegManeuverType.SharpRight:
                case DirectionsLegManeuverType.UturnRight:
                case DirectionsLegManeuverType.RampRight:
                case DirectionsLegManeuverType.ExitRight:
                case DirectionsLegManeuverType.StayRight:
                case DirectionsLegManeuverType.DestinationRight:
                case DirectionsLegManeuverType.MergeRight:
                    return ActivateTurnLanesFromRight(turnLaneDirection, currManeuverType, 1);
                case DirectionsLegManeuverType.Merge:
                    if (DriveOnRight())
                    {
                        return ActivateTurnLanesFromLeft(turnLaneDirection, currManeuverType, 1);
                    }

                    return ActivateTurnLanesFromRight(turnLaneDirection, currManeuverType, 1);
                case DirectionsLegManeuverType.RoundaboutEnter:
                case DirectionsLegManeuverType.RoundaboutExit:
                case DirectionsLegManeuverType.FerryEnter:
                case DirectionsLegManeuverType.FerryExit:
                    return ActivateTurnLanesFromLeft(turnLaneDirection, currManeuverType);
                case DirectionsLegManeuverType.Destination:
                    if (DriveOnRight())
                    {
                        return ActivateTurnLanesFromRight(turnLaneDirection, currManeuverType, 1);
                    }

                    return ActivateTurnLanesFromLeft(turnLaneDirection, currManeuverType, 1);
                default:
                    return ActivateTurnLanesFromLeft(turnLaneDirection, currManeuverType);
            }
        }

        // Activate all matching turn lanes
        return ActivateTurnLanesFromLeft(turnLaneDirection, currManeuverType);
    }
}

/// <summary>
/// Wraps a <see cref="TripIntersectingEdge"/> with navigation helpers. Faithful port of
/// <c>valhalla::odin::EnhancedTripLeg_IntersectingEdge</c>.
/// </summary>
public sealed class EnhancedTripLeg_IntersectingEdge
{
    private readonly TripIntersectingEdge _intersectingEdge;

    /// <summary>Constructs an enhanced intersecting edge wrapping the supplied object.</summary>
    public EnhancedTripLeg_IntersectingEdge(TripIntersectingEdge intersectingEdge)
        => _intersectingEdge = intersectingEdge;

    /// <summary>Number of names. Faithful port of <c>name_size()</c>.</summary>
    public int NameSize() => _intersectingEdge.Names.Count;

    /// <summary>The names. Faithful port of <c>name()</c>.</summary>
    public IReadOnlyList<string> Name() => _intersectingEdge.Names;

    /// <summary>Begin heading. Faithful port of <c>begin_heading()</c>.</summary>
    public uint BeginHeading() => _intersectingEdge.BeginHeading;

    /// <summary>True if previous edge name consistency. Faithful port of <c>prev_name_consistency()</c>.</summary>
    public bool PrevNameConsistency() => _intersectingEdge.PrevNameConsistency;

    /// <summary>True if current edge name consistency. Faithful port of <c>curr_name_consistency()</c>.</summary>
    public bool CurrNameConsistency() => _intersectingEdge.CurrNameConsistency;

    /// <summary>Driveability. Faithful port of <c>driveability()</c>.</summary>
    public TripTraversability Driveability() => _intersectingEdge.Driveability;

    /// <summary>Cyclability. Faithful port of <c>cyclability()</c>.</summary>
    public TripTraversability Cyclability() => _intersectingEdge.Cyclability;

    /// <summary>Walkability. Faithful port of <c>walkability()</c>.</summary>
    public TripTraversability Walkability() => _intersectingEdge.Walkability;

    /// <summary>Use. Faithful port of <c>use()</c>.</summary>
    public Use GetUse() => _intersectingEdge.Use;

    /// <summary>Road class. Faithful port of <c>road_class()</c>.</summary>
    public RoadClass GetRoadClass() => _intersectingEdge.RoadClass;

    /// <summary>Lane count. Faithful port of <c>lane_count()</c>.</summary>
    public uint LaneCount() => _intersectingEdge.LaneCount;

    /// <summary>True if traversable for the travel mode. Faithful port of <c>IsTraversable()</c>.</summary>
    public bool IsTraversable(TravelMode travelMode)
        => GetTravelModeTraversability(travelMode) != TripTraversability.None;

    /// <summary>True if traversable outbound (forward/both) for the travel mode. Faithful port of <c>IsTraversableOutbound()</c>.</summary>
    public bool IsTraversableOutbound(TravelMode travelMode)
    {
        TripTraversability traversability = GetTravelModeTraversability(travelMode);
        return traversability == TripTraversability.Forward || traversability == TripTraversability.Both;
    }

    /// <summary>True if a highway (motorway and not ramp/turn-channel). Faithful port of <c>IsHighway()</c>.</summary>
    public bool IsHighway()
        => GetRoadClass() == RoadClass.Motorway && !(GetUse() == Use.Ramp || GetUse() == Use.TurnChannel);

    private TripTraversability GetTravelModeTraversability(TravelMode travelMode)
    {
        if (travelMode == TravelMode.Drive)
        {
            return Driveability();
        }

        if (travelMode == TravelMode.Bicycle)
        {
            return Cyclability();
        }

        return Walkability();
    }
}

/// <summary>
/// Wraps a <see cref="TripNode"/> with navigation helpers used to classify intersections. Faithful
/// port of <c>valhalla::odin::EnhancedTripLeg_Node</c>.
/// </summary>
public sealed class EnhancedTripLeg_Node
{
    private readonly TripNode _node;

    /// <summary>Constructs an enhanced node wrapping the supplied <see cref="TripNode"/>.</summary>
    public EnhancedTripLeg_Node(TripNode node) => _node = node;

    /// <summary>Number of intersecting edges. Faithful port of <c>intersecting_edge_size()</c>.</summary>
    public int IntersectingEdgeSize() => _node.IntersectingEdges.Count;

    /// <summary>True if the node is a fork. Faithful port of <c>fork()</c>.</summary>
    public bool Fork() => _node.Fork;

    /// <summary>The intersecting edge at the specified index. Faithful port of <c>intersecting_edge(index)</c>.</summary>
    public TripIntersectingEdge IntersectingEdge(int index) => _node.IntersectingEdges[index];

    /// <summary>The edge that leaves this node. Faithful port of <c>edge()</c>.</summary>
    public TripEdge Edge() => _node.Edge!;

    /// <summary>The node type. Faithful port of <c>type()</c>.</summary>
    public NodeType GetNodeType() => _node.Type;

    /// <summary>True if the node has a traffic signal. Faithful port of <c>traffic_signal()</c>.</summary>
    public bool TrafficSignal() => _node.TrafficSignal;

    /// <summary>The elapsed time (seconds). Faithful port of <c>elapsed_time()</c>.</summary>
    public double ElapsedTime() => _node.ElapsedCost.Secs;

    /// <summary>The admin index. Faithful port of <c>admin_index()</c>.</summary>
    public uint AdminIndex() => _node.AdminIndex;

    /// <summary>The IANA time zone. Faithful port of <c>time_zone()</c>.</summary>
    public string TimeZone() => _node.TimeZone;

    /// <summary>True if the node has intersecting edges. Faithful port of <c>HasIntersectingEdges()</c>.</summary>
    public bool HasIntersectingEdges() => IntersectingEdgeSize() > 0;

    /// <summary>True if any intersecting edge has name consistency. Faithful port of <c>HasIntersectingEdgeNameConsistency()</c>.</summary>
    public bool HasIntersectingEdgeNameConsistency()
    {
        foreach (TripIntersectingEdge xedge in _node.IntersectingEdges)
        {
            if (xedge.CurrNameConsistency || xedge.PrevNameConsistency)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if any intersecting edge has current name consistency. Faithful port of <c>HasIntersectingEdgeCurrNameConsistency()</c>.</summary>
    public bool HasIntersectingEdgeCurrNameConsistency()
    {
        foreach (TripIntersectingEdge xedge in _node.IntersectingEdges)
        {
            if (xedge.CurrNameConsistency)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if there is a non-backward traversable intersecting ramp with the same name as the
    /// previous and/or current edge. Faithful port of
    /// <c>HasNonBackwardTraversableSameNameRampIntersectingEdge()</c>.
    /// </summary>
    public bool HasNonBackwardTraversableSameNameRampIntersectingEdge(uint fromHeading, TravelMode travelMode)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            if ((xedge.PrevNameConsistency() || xedge.CurrNameConsistency())
                && xedge.IsTraversable(travelMode)
                && xedge.GetUse() == Use.Ramp)
            {
                uint intersectingTurnDegree = Util.GetTurnDegree(fromHeading, xedge.BeginHeading());
                bool nonBackward = !(intersectingTurnDegree > EnhancedTripPathConstants.BackwardTurnDegreeLowerBound
                                     && intersectingTurnDegree < EnhancedTripPathConstants.BackwardTurnDegreeUpperBound);
                if (nonBackward)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns an enhanced intersecting edge for the specified index. Faithful port of <c>GetIntersectingEdge()</c>.</summary>
    public EnhancedTripLeg_IntersectingEdge GetIntersectingEdge(int index)
        => new EnhancedTripLeg_IntersectingEdge(_node.IntersectingEdges[index]);

    /// <summary>
    /// Tallies intersecting edges to the right and left of the path edge, with similar /
    /// traversable-outbound sub-counts. Faithful port of <c>CalculateRightLeftIntersectingEdgeCounts()</c>.
    /// </summary>
    public void CalculateRightLeftIntersectingEdgeCounts(
        uint fromHeading,
        TravelMode travelMode,
        ref IntersectingEdgeCounts xedgeCounts)
    {
        xedgeCounts.Clear();

        // No turn - just return
        if (IntersectingEdgeSize() == 0)
        {
            return;
        }

        uint pathTurnDegree = Util.GetTurnDegree(fromHeading, Edge().BeginHeading);
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            uint intersectingTurnDegree = Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading);
            bool xedgeTraversableOutbound = GetIntersectingEdge(i).IsTraversableOutbound(travelMode);

            if (pathTurnDegree > 180)
            {
                if (intersectingTurnDegree > pathTurnDegree || intersectingTurnDegree < 180)
                {
                    ++xedgeCounts.Right;
                    if (OdinUtil.IsSimilarTurnDegree(pathTurnDegree, intersectingTurnDegree, true))
                    {
                        ++xedgeCounts.RightSimilar;
                        if (xedgeTraversableOutbound)
                        {
                            ++xedgeCounts.RightSimilarTraversableOutbound;
                        }
                    }

                    if (xedgeTraversableOutbound)
                    {
                        ++xedgeCounts.RightTraversableOutbound;
                    }
                }
                else if (intersectingTurnDegree < pathTurnDegree && intersectingTurnDegree > 180)
                {
                    ++xedgeCounts.Left;
                    if (OdinUtil.IsSimilarTurnDegree(pathTurnDegree, intersectingTurnDegree, false))
                    {
                        ++xedgeCounts.LeftSimilar;
                        if (xedgeTraversableOutbound)
                        {
                            ++xedgeCounts.LeftSimilarTraversableOutbound;
                        }
                    }

                    if (xedgeTraversableOutbound)
                    {
                        ++xedgeCounts.LeftTraversableOutbound;
                    }
                }
            }
            else
            {
                if (intersectingTurnDegree > pathTurnDegree && intersectingTurnDegree < 180)
                {
                    ++xedgeCounts.Right;
                    if (OdinUtil.IsSimilarTurnDegree(pathTurnDegree, intersectingTurnDegree, true))
                    {
                        ++xedgeCounts.RightSimilar;
                        if (xedgeTraversableOutbound)
                        {
                            ++xedgeCounts.RightSimilarTraversableOutbound;
                        }
                    }

                    if (xedgeTraversableOutbound)
                    {
                        ++xedgeCounts.RightTraversableOutbound;
                    }
                }
                else if (intersectingTurnDegree < pathTurnDegree || intersectingTurnDegree > 180)
                {
                    ++xedgeCounts.Left;
                    if (OdinUtil.IsSimilarTurnDegree(pathTurnDegree, intersectingTurnDegree, false))
                    {
                        ++xedgeCounts.LeftSimilar;
                        if (xedgeTraversableOutbound)
                        {
                            ++xedgeCounts.LeftSimilarTraversableOutbound;
                        }
                    }

                    if (xedgeTraversableOutbound)
                    {
                        ++xedgeCounts.LeftTraversableOutbound;
                    }
                }
            }
        }
    }

    /// <summary>True if there is a forward intersecting edge. Faithful port of <c>HasForwardIntersectingEdge()</c>.</summary>
    public bool HasForwardIntersectingEdge(uint fromHeading)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (EnhancedTripPathConstants.IsForward(Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if there is a forward traversable intersecting edge. Faithful port of <c>HasForwardTraversableIntersectingEdge()</c>.</summary>
    public bool HasForwardTraversableIntersectingEdge(uint fromHeading, TravelMode travelMode)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (EnhancedTripPathConstants.IsForward(Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading))
                && GetIntersectingEdge(i).IsTraversableOutbound(travelMode))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if there is a road-fork (fork-forward, traversable, name-consistent, non-ramp/channel/ferry)
    /// intersecting edge. Faithful port of <c>HasRoadForkTraversableIntersectingEdge()</c>.
    /// </summary>
    public bool HasRoadForkTraversableIntersectingEdge(uint fromHeading, TravelMode travelMode, bool allowServiceRoad)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            if (EnhancedTripPathConstants.IsForkForward(Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading))
                && xedge.IsTraversableOutbound(travelMode)
                && xedge.PrevNameConsistency()
                && xedge.GetUse() != Use.Ramp
                && xedge.GetUse() != Use.TurnChannel
                && xedge.GetUse() != Use.Ferry
                && xedge.GetUse() != Use.RailFerry)
            {
                // If service roads are not allowed then skip intersecting service roads
                if (!allowServiceRoad && xedge.GetRoadClass() == RoadClass.ServiceOther)
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if there is a forward traversable intersecting edge of a significant road class. Faithful
    /// port of <c>HasForwardTraversableSignificantRoadClassXEdge()</c>.
    /// </summary>
    public bool HasForwardTraversableSignificantRoadClassXEdge(uint fromHeading, TravelMode travelMode, RoadClass pathRoadClass)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            if (EnhancedTripPathConstants.IsForward(Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading))
                && xedge.IsTraversableOutbound(travelMode)
                && (int)xedge.GetRoadClass() - (int)pathRoadClass <= EnhancedTripPathConstants.SignificantRoadClassThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if there is a forward traversable intersecting edge of the specified use. Faithful port
    /// of <c>HasForwardTraversableUseXEdge()</c>.
    /// </summary>
    public bool HasForwardTraversableUseXEdge(uint fromHeading, TravelMode travelMode, Use use)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            if (EnhancedTripPathConstants.IsForward(Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading))
                && xedge.IsTraversableOutbound(travelMode)
                && xedge.GetUse() == use)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if there is a similar-straight traversable intersecting edge of a significant road class.
    /// Faithful port of <c>HasSimilarStraightSignificantRoadClassXEdge()</c>.
    /// </summary>
    public bool HasSimilarStraightSignificantRoadClassXEdge(
        uint pathTurnDegree,
        uint fromHeading,
        TravelMode travelMode,
        RoadClass pathRoadClass)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            uint xedgeTurnDegree = Util.GetTurnDegree(fromHeading, xedge.BeginHeading());
            int pathXedgeTurnDegreeDelta = EnhancedTripPathConstants.GetTurnDegreeDelta(pathTurnDegree, xedgeTurnDegree);
            if (EnhancedTripPathConstants.IsRelativeStraight(pathTurnDegree)
                && EnhancedTripPathConstants.IsRelativeStraight(xedgeTurnDegree)
                && xedge.IsTraversableOutbound(travelMode)
                && pathXedgeTurnDegreeDelta <= EnhancedTripPathConstants.SimilarStraightThreshold
                && (int)xedge.GetRoadClass() - (int)pathRoadClass <= EnhancedTripPathConstants.SignificantRoadClassThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if there is a similar-straight traversable intersecting edge that is non-ramp OR a ramp
    /// with the same previous edge name. Faithful port of
    /// <c>HasSimilarStraightNonRampOrSameNameRampXEdge()</c>.
    /// </summary>
    public bool HasSimilarStraightNonRampOrSameNameRampXEdge(uint pathTurnDegree, uint fromHeading, TravelMode travelMode)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            uint xedgeTurnDegree = Util.GetTurnDegree(fromHeading, xedge.BeginHeading());
            int pathXedgeTurnDegreeDelta = EnhancedTripPathConstants.GetTurnDegreeDelta(pathTurnDegree, xedgeTurnDegree);
            if (EnhancedTripPathConstants.IsRelativeStraight(pathTurnDegree)
                && EnhancedTripPathConstants.IsRelativeStraight(xedgeTurnDegree)
                && xedge.IsTraversableOutbound(travelMode)
                && pathXedgeTurnDegreeDelta <= EnhancedTripPathConstants.SimilarStraightThreshold
                && (xedge.GetUse() != Use.Ramp
                    || (xedge.GetUse() == Use.Ramp && xedge.PrevNameConsistency())))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if all intersecting edges are forward-traversable of an equal-or-lower road class (no
    /// ramps/channels/ferries). Faithful port of <c>HasOnlyForwardTraversableRoadClassXEdges()</c>.
    /// </summary>
    public bool HasOnlyForwardTraversableRoadClassXEdges(uint fromHeading, TravelMode travelMode, RoadClass pathRoadClass)
    {
        // Must have intersecting edges
        if (IntersectingEdgeSize() == 0)
        {
            return false;
        }

        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            // Can not be a ramp or turn channel
            if (xedge.GetUse() == Use.Ramp || xedge.GetUse() == Use.TurnChannel
                || xedge.GetUse() == Use.Ferry || xedge.GetUse() == Use.RailFerry)
            {
                return false;
            }

            if ((int)pathRoadClass >= (int)xedge.GetRoadClass()
                && EnhancedTripPathConstants.IsForkForward(Util.GetTurnDegree(fromHeading, xedge.BeginHeading()))
                && xedge.IsTraversableOutbound(travelMode))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>True if there is a wider-forward traversable intersecting edge. Faithful port of <c>HasWiderForwardTraversableIntersectingEdge()</c>.</summary>
    public bool HasWiderForwardTraversableIntersectingEdge(uint fromHeading, TravelMode travelMode)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (EnhancedTripPathConstants.IsWiderForward(Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading))
                && GetIntersectingEdge(i).IsTraversableOutbound(travelMode))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if there is a wider-forward traversable highway intersecting edge. Faithful port of <c>HasWiderForwardTraversableHighwayXEdge()</c>.</summary>
    public bool HasWiderForwardTraversableHighwayXEdge(uint fromHeading, TravelMode travelMode)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            if (EnhancedTripPathConstants.IsWiderForward(Util.GetTurnDegree(fromHeading, xedge.BeginHeading()))
                && xedge.IsTraversableOutbound(travelMode)
                && xedge.IsHighway())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if there is a traversable intersecting edge. Faithful port of <c>HasTraversableIntersectingEdge()</c>.</summary>
    public bool HasTraversableIntersectingEdge(TravelMode travelMode)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (GetIntersectingEdge(i).IsTraversable(travelMode))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if there is a traversable-outbound intersecting edge. Faithful port of <c>HasTraversableOutboundIntersectingEdge()</c>.</summary>
    public bool HasTraversableOutboundIntersectingEdge(TravelMode travelMode)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (GetIntersectingEdge(i).IsTraversableOutbound(travelMode))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if there is a traversable intersecting edge excluding the specified use. Faithful port of <c>HasTraversableExcludeUseXEdge()</c>.</summary>
    public bool HasTraversableExcludeUseXEdge(TravelMode travelMode, Use excludeUse)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            if (xedge.IsTraversableOutbound(travelMode) && xedge.GetUse() != excludeUse)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if there is a forward traversable intersecting edge excluding the specified use. Faithful port of <c>HasForwardTraversableExcludeUseXEdge()</c>.</summary>
    public bool HasForwardTraversableExcludeUseXEdge(uint fromHeading, TravelMode travelMode, Use excludeUse)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            if (EnhancedTripPathConstants.IsForward(Util.GetTurnDegree(fromHeading, xedge.BeginHeading()))
                && xedge.IsTraversableOutbound(travelMode)
                && xedge.GetUse() != excludeUse)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if there is a traversable-outbound intersecting edge of the specified turn type. Faithful port of <c>HasSpecifiedTurnXEdge()</c>.</summary>
    public bool HasSpecifiedTurnXEdge(Turn.Type turnType, uint fromHeading, TravelMode travelMode)
    {
        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (GetIntersectingEdge(i).IsTraversableOutbound(travelMode)
                && Turn.GetType(Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading)) == turnType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if there is an intersecting edge of the specified road class. Faithful port of <c>HasSpecifiedRoadClassXEdge()</c>.</summary>
    public bool HasSpecifiedRoadClassXEdge(RoadClass roadClass)
    {
        if (!HasIntersectingEdges())
        {
            return false;
        }

        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (GetIntersectingEdge(i).GetRoadClass() == roadClass)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the turn degree of the straightest traversable intersecting edge (180 if none). If a
    /// use box is supplied it receives the matching edge's use. Faithful port of
    /// <c>GetStraightestTraversableIntersectingEdgeTurnDegree()</c>.
    /// </summary>
    public uint GetStraightestTraversableIntersectingEdgeTurnDegree(uint fromHeading, TravelMode travelMode, UseBox? use = null)
    {
        uint straightestTurnDegree = 180; // Initialize to reverse turn degree
        uint straightestDelta = 180;      // Initialize to reverse delta

        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            EnhancedTripLeg_IntersectingEdge xedge = GetIntersectingEdge(i);
            uint intersectingTurnDegree = Util.GetTurnDegree(fromHeading, xedge.BeginHeading());
            bool xedgeTraversableOutbound = xedge.IsTraversableOutbound(travelMode);
            uint straightDelta = intersectingTurnDegree > 180 ? 360 - intersectingTurnDegree : intersectingTurnDegree;
            if (xedgeTraversableOutbound && straightDelta < straightestDelta)
            {
                straightestDelta = straightDelta;
                straightestTurnDegree = intersectingTurnDegree;
                if (use != null)
                {
                    use.Value = xedge.GetUse();
                    use.HasValue = true;
                }
            }
        }

        return straightestTurnDegree;
    }

    /// <summary>True if the straightest traversable intersecting edge is reversed. Faithful port of <c>IsStraightestTraversableIntersectingEdgeReversed()</c>.</summary>
    public bool IsStraightestTraversableIntersectingEdgeReversed(uint fromHeading, TravelMode travelMode)
    {
        uint straightestTraversableXedgeTurnDegree =
            GetStraightestTraversableIntersectingEdgeTurnDegree(fromHeading, travelMode);
        return straightestTraversableXedgeTurnDegree > 124 && straightestTraversableXedgeTurnDegree < 236;
    }

    /// <summary>Returns the turn degree of the straightest intersecting edge (180 if none). Faithful port of <c>GetStraightestIntersectingEdgeTurnDegree()</c>.</summary>
    public uint GetStraightestIntersectingEdgeTurnDegree(uint fromHeading)
    {
        uint straightestTurnDegree = 180; // Initialize to reverse turn degree
        uint straightestDelta = 180;      // Initialize to reverse delta

        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            uint intersectingTurnDegree = Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading);
            uint straightDelta = intersectingTurnDegree > 180 ? 360 - intersectingTurnDegree : intersectingTurnDegree;
            if (straightDelta < straightestDelta)
            {
                straightestDelta = straightDelta;
                straightestTurnDegree = intersectingTurnDegree;
            }
        }

        return straightestTurnDegree;
    }

    /// <summary>Returns the right-most traversable turn degree among the path + intersecting edges. Faithful port of <c>GetRightMostTurnDegree()</c>.</summary>
    public uint GetRightMostTurnDegree(uint turnDegree, uint fromHeading, TravelMode travelMode)
    {
        uint rightMostTurnDegree = turnDegree;
        uint rightMostDelta = GetRightDelta(turnDegree);

        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (GetIntersectingEdge(i).IsTraversableOutbound(travelMode))
            {
                uint xturnDegree = Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading);
                uint rightDelta = GetRightDelta(xturnDegree);
                if (rightDelta < rightMostDelta)
                {
                    rightMostDelta = rightDelta;
                    rightMostTurnDegree = xturnDegree;
                }
            }
        }

        return rightMostTurnDegree;

        static uint GetRightDelta(uint td)
        {
            if (td < 90)
            {
                return 90 - td;
            }

            if (td > 270)
            {
                return 360 - td + 90;
            }

            return td - 90;
        }
    }

    /// <summary>Returns the left-most traversable turn degree among the path + intersecting edges. Faithful port of <c>GetLeftMostTurnDegree()</c>.</summary>
    public uint GetLeftMostTurnDegree(uint turnDegree, uint fromHeading, TravelMode travelMode)
    {
        uint leftMostTurnDegree = turnDegree;
        uint leftMostDelta = GetLeftDelta(turnDegree);

        for (int i = 0; i < IntersectingEdgeSize(); ++i)
        {
            if (GetIntersectingEdge(i).IsTraversableOutbound(travelMode))
            {
                uint xturnDegree = Util.GetTurnDegree(fromHeading, IntersectingEdge(i).BeginHeading);
                uint leftDelta = GetLeftDelta(xturnDegree);
                if (leftDelta < leftMostDelta)
                {
                    leftMostDelta = leftDelta;
                    leftMostTurnDegree = xturnDegree;
                }
            }
        }

        return leftMostTurnDegree;

        static uint GetLeftDelta(uint td)
        {
            if (td < 90)
            {
                return 90 + td;
            }

            if (td < 270)
            {
                return 270 - td;
            }

            return td - 270;
        }
    }

    /// <summary>True if the node is a street intersection. Faithful port of <c>IsStreetIntersection()</c>.</summary>
    public bool IsStreetIntersection() => GetNodeType() == NodeType.StreetIntersection;

    /// <summary>True if the node is a gate. Faithful port of <c>IsGate()</c>.</summary>
    public bool IsGate() => GetNodeType() == NodeType.Gate;

    /// <summary>True if the node is a bollard. Faithful port of <c>IsBollard()</c>.</summary>
    public bool IsBollard() => GetNodeType() == NodeType.Bollard;

    /// <summary>True if the node is a toll booth. Faithful port of <c>IsTollBooth()</c>.</summary>
    public bool IsTollBooth() => GetNodeType() == NodeType.TollBooth;

    /// <summary>True if the node is a transit egress. Faithful port of <c>IsTransitEgress()</c>.</summary>
    public bool IsTransitEgress() => GetNodeType() == NodeType.TransitEgress;

    /// <summary>True if the node is a transit station. Faithful port of <c>IsTransitStation()</c>.</summary>
    public bool IsTransitStation() => GetNodeType() == NodeType.TransitStation;

    /// <summary>True if the node is a transit platform. Faithful port of <c>IsTransitPlatform()</c>.</summary>
    public bool IsTransitPlatform() => GetNodeType() == NodeType.MultiUseTransitPlatform;

    /// <summary>True if the node is a bike share. Faithful port of <c>IsBikeShare()</c>.</summary>
    public bool IsBikeShare() => GetNodeType() == NodeType.BikeShare;

    /// <summary>True if the node is parking. Faithful port of <c>IsParking()</c>.</summary>
    public bool IsParking() => GetNodeType() == NodeType.Parking;

    /// <summary>True if the node is a motorway junction. Faithful port of <c>IsMotorwayJunction()</c>.</summary>
    public bool IsMotorwayJunction() => GetNodeType() == NodeType.MotorWayJunction;

    /// <summary>True if the node is border control. Faithful port of <c>IsBorderControl()</c>.</summary>
    public bool IsBorderControl() => GetNodeType() == NodeType.BorderControl;

    /// <summary>True if the node is a toll gantry. Faithful port of <c>IsTollGantry()</c>.</summary>
    public bool IsTollGantry() => GetNodeType() == NodeType.TollGantry;

    /// <summary>True if the node is a sump buster. Faithful port of <c>IsSumpBuster()</c>.</summary>
    public bool IsSumpBuster() => GetNodeType() == NodeType.SumpBuster;

    /// <summary>True if the node is a building entrance. Faithful port of <c>IsBuildingEntrance()</c>.</summary>
    public bool IsBuildingEntrance() => GetNodeType() == NodeType.BuildingEntrance;

    /// <summary>True if the node is an elevator. Faithful port of <c>IsElevator()</c>.</summary>
    public bool IsElevator() => GetNodeType() == NodeType.Elevator;
}

/// <summary>
/// A mutable optional-Use box. Faithful port of the C++ <c>std::optional&lt;TripLeg_Use&gt;*</c>
/// out-parameter used by <see cref="EnhancedTripLeg_Node.GetStraightestTraversableIntersectingEdgeTurnDegree"/>.
/// </summary>
public sealed class UseBox
{
    /// <summary>The use value (valid only when <see cref="HasValue"/> is true).</summary>
    public Use Value { get; set; }

    /// <summary>True if a use value has been set.</summary>
    public bool HasValue { get; set; }
}
