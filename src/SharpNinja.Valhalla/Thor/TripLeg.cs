// Plain C# trip result classes (valhalla @ 3.7.0 trip.proto, de-protobuf'd).
//
// In Valhalla the route result that thor's TripLegBuilder produces is the protobuf TripLeg
// (proto/trip.proto), later consumed by odin to produce maneuvers. The wire protobuf surface is an
// EXCLUDED module for this port (no proto runtime). Per the task brief, TripLeg / TripPath are
// ported here as plain C# classes carrying the "maneuvers-input data": the ordered edges, the
// decoded shape (encoded + decoded), the admin table, and the per-node / per-edge attributes
// sufficient for the app to consume a route and for odin to (later) build maneuvers.
//
// PORT-NOTE: The proto TripLeg has many fields that belong to EXCLUDED modules (transit route info,
// incidents, closures, elevation sampling, recosting, landmarks, guidance views, conditional speed
// limits, lane connectivity, pronunciation/linguistics). Those are intentionally omitted here. What
// is kept is the point-to-point auto/truck subset that drives maneuver generation and route
// rendering: ordered edges with names/signs/turn-lanes/classification/use/flags/headings/shape
// indices, nodes with type/elapsed-and-transition cost/admin index, the admin table, the bounding
// box, the leg summary, and the decoded + encoded shape.
//
// PORT-NOTE: In the engine the AttributesController gates which attributes get populated. There is
// no proto/api request surface in this port, so the de-protobuf'd builder behaves as if a "route"
// (maneuver-generation) controller is active and always populates this subset. The shape index /
// heading / name / sign / cost fields below are exactly those a default route request fills in.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Thor;

/// <summary>
/// Traversability of an edge/intersecting-edge. De-protobuf'd subset of <c>TripLeg.Traversability</c>.
/// </summary>
public enum TripTraversability
{
    /// <summary>Not traversable in either direction for the mode.</summary>
    None = 0,

    /// <summary>Traversable in the forward direction only.</summary>
    Forward = 1,

    /// <summary>Traversable in the backward direction only.</summary>
    Backward = 2,

    /// <summary>Traversable in both directions.</summary>
    Both = 3,
}

/// <summary>
/// A single sign element along an edge (exit number, branch, toward, name, guide, etc.). The
/// de-protobuf'd subset of <c>TripSignElement</c> (text + is-route-number; pronunciation excluded).
/// </summary>
public sealed class TripSignElement
{
    /// <summary>Constructs a sign element.</summary>
    /// <param name="text">The sign text.</param>
    /// <param name="isRouteNumber">Whether the sign text is a route number.</param>
    public TripSignElement(string text, bool isRouteNumber)
    {
        Text = text;
        IsRouteNumber = isRouteNumber;
    }

    /// <summary>The sign text.</summary>
    public string Text { get; }

    /// <summary>Whether the sign text is a route number (e.g. "I 70").</summary>
    public bool IsRouteNumber { get; }
}

/// <summary>
/// All the sign elements attached to an edge or node. The de-protobuf'd subset of <c>TripSign</c>
/// (the element lists odin reads to build exit/guide instructions).
/// </summary>
public sealed class TripSign
{
    /// <summary>Exit number sign elements.</summary>
    public List<TripSignElement> ExitNumbers { get; } = new();

    /// <summary>Exit "branch" (onto-street) sign elements.</summary>
    public List<TripSignElement> ExitOntoStreets { get; } = new();

    /// <summary>Exit "toward" (toward-location) sign elements.</summary>
    public List<TripSignElement> ExitTowardLocations { get; } = new();

    /// <summary>Exit name sign elements.</summary>
    public List<TripSignElement> ExitNames { get; } = new();

    /// <summary>Guide "branch" (onto-street) sign elements.</summary>
    public List<TripSignElement> GuideOntoStreets { get; } = new();

    /// <summary>Guide "toward" (toward-location) sign elements.</summary>
    public List<TripSignElement> GuideTowardLocations { get; } = new();

    /// <summary>Junction name sign elements (named junctions at nodes).</summary>
    public List<TripSignElement> JunctionNames { get; } = new();

    /// <summary>True if no sign element of any kind is present.</summary>
    public bool IsEmpty =>
        ExitNumbers.Count == 0 && ExitOntoStreets.Count == 0 && ExitTowardLocations.Count == 0 &&
        ExitNames.Count == 0 && GuideOntoStreets.Count == 0 && GuideTowardLocations.Count == 0 &&
        JunctionNames.Count == 0;
}

/// <summary>
/// An edge that intersects the path at a node but is not on the path. The de-protobuf'd subset of
/// <c>TripLeg.IntersectingEdge</c>; odin uses these to classify intersections (forks, etc.).
/// </summary>
public sealed class TripIntersectingEdge
{
    /// <summary>Heading (degrees) of the intersecting edge as it leaves the node.</summary>
    public uint BeginHeading { get; set; }

    /// <summary>Whether the previous path edge and this edge share a name.</summary>
    public bool PrevNameConsistency { get; set; }

    /// <summary>Whether the current path edge and this edge share a name.</summary>
    public bool CurrNameConsistency { get; set; }

    /// <summary>Driveability of the intersecting edge.</summary>
    public TripTraversability Driveability { get; set; }

    /// <summary>Cyclability of the intersecting edge.</summary>
    public TripTraversability Cyclability { get; set; }

    /// <summary>Walkability of the intersecting edge.</summary>
    public TripTraversability Walkability { get; set; }

    /// <summary>Road classification of the intersecting edge.</summary>
    public RoadClass RoadClass { get; set; }

    /// <summary>Specialized use of the intersecting edge.</summary>
    public Use Use { get; set; }

    /// <summary>Number of lanes on the intersecting edge.</summary>
    public uint LaneCount { get; set; }

    /// <summary>The (untagged) street names of the intersecting edge, in priority order.</summary>
    public List<string> Names { get; } = new();
}

/// <summary>
/// Per-edge attributes along a trip leg. The de-protobuf'd subset of <c>TripLeg.Edge</c> needed to
/// drive maneuver generation and to render the route.
/// </summary>
public sealed class TripEdge
{
    /// <summary>The directed edge id this trip edge corresponds to (graphid value in <see cref="GraphId.Value"/>).</summary>
    public GraphId EdgeId { get; set; }

    /// <summary>Whether the edge shape is stored/traversed in the forward direction.</summary>
    public bool Forward { get; set; }

    /// <summary>OSM way id of the underlying edge.</summary>
    public ulong WayId { get; set; }

    /// <summary>The (untagged) street names of the edge, in priority order.</summary>
    public List<string> Names { get; } = new();

    /// <summary>Tunnel/bridge tagged names of the edge, in priority order.</summary>
    public List<string> TunnelNames { get; } = new();

    /// <summary>Length of the (used portion of the) edge in kilometers.</summary>
    public float LengthKm { get; set; }

    /// <summary>Average speed (KPH) used for this edge by the costing model.</summary>
    public double SpeedKph { get; set; }

    /// <summary>Road classification of the edge.</summary>
    public RoadClass RoadClass { get; set; }

    /// <summary>Specialized use of the edge (road, ramp, ferry, etc.).</summary>
    public Use Use { get; set; }

    /// <summary>Travel mode along this edge.</summary>
    public TravelMode Mode { get; set; }

    /// <summary>Whether the edge is a roundabout.</summary>
    public bool Roundabout { get; set; }

    /// <summary>Whether the edge has a toll.</summary>
    public bool Toll { get; set; }

    /// <summary>Whether the edge is a tunnel.</summary>
    public bool Tunnel { get; set; }

    /// <summary>Whether the edge is a bridge.</summary>
    public bool Bridge { get; set; }

    /// <summary>Whether the edge is unpaved.</summary>
    public bool Unpaved { get; set; }

    /// <summary>Whether the edge is an internal intersection edge.</summary>
    public bool InternalIntersection { get; set; }

    /// <summary>Whether the edge is destination-only (private/restricted access).</summary>
    public bool DestinationOnly { get; set; }

    /// <summary>Whether driving is on the left for this edge (i.e. NOT drive-on-right).</summary>
    public bool DriveOnLeft { get; set; }

    /// <summary>Whether the edge has a time-based restriction along the path.</summary>
    public bool HasTimeRestrictions { get; set; }

    /// <summary>Traversability of the edge for the travel mode.</summary>
    public TripTraversability Traversability { get; set; }

    /// <summary>Surface type of the edge.</summary>
    public Surface Surface { get; set; }

    /// <summary>Number of lanes on the edge.</summary>
    public uint LaneCount { get; set; }

    /// <summary>The posted speed limit (KPH) for the edge (0 if unknown).</summary>
    public uint SpeedLimit { get; set; }

    /// <summary>The default (tile) speed (KPH) for the edge.</summary>
    public uint DefaultSpeed { get; set; }

    /// <summary>The truck speed (KPH) for the edge (0 if not set).</summary>
    public uint TruckSpeed { get; set; }

    /// <summary>Whether the edge is on a designated truck route.</summary>
    public bool TruckRoute { get; set; }

    /// <summary>Begin heading (degrees) of the edge.</summary>
    public uint BeginHeading { get; set; }

    /// <summary>End heading (degrees) of the edge.</summary>
    public uint EndHeading { get; set; }

    /// <summary>Index into the leg shape of the first point of this edge.</summary>
    public uint BeginShapeIndex { get; set; }

    /// <summary>Index into the leg shape of the last point of this edge.</summary>
    public uint EndShapeIndex { get; set; }

    /// <summary>Percent (0..1) along the underlying edge where the used portion starts.</summary>
    public float SourceAlongEdge { get; set; }

    /// <summary>Percent (0..1) along the underlying edge where the used portion ends.</summary>
    public float TargetAlongEdge { get; set; }

    /// <summary>Sign information for the edge (exit/guide/junction). Null if the edge has no signs.</summary>
    public TripSign? Sign { get; set; }

    /// <summary>Turn-lane direction masks for the edge (one per lane), if any.</summary>
    public List<ushort> TurnLanes { get; } = new();
}

/// <summary>
/// Per-node attributes along a trip leg. The de-protobuf'd subset of <c>TripLeg.Node</c> needed to
/// drive maneuver generation: the edge that leaves this node, the elapsed/transition cost, node
/// type, admin index, and the intersecting-edge context used to classify the intersection.
/// </summary>
public sealed class TripNode
{
    /// <summary>The edge that leaves this node along the path (the last node has no edge).</summary>
    public TripEdge? Edge { get; set; }

    /// <summary>The elapsed cost (cost + seconds) from the start of the leg to this node.</summary>
    public Cost ElapsedCost { get; set; }

    /// <summary>The transition cost incurred entering the edge that starts at this node.</summary>
    public Cost TransitionCost { get; set; }

    /// <summary>The node type (gate, toll booth, border control, etc.).</summary>
    public NodeType Type { get; set; }

    /// <summary>Whether the node has a traffic signal.</summary>
    public bool TrafficSignal { get; set; }

    /// <summary>Whether the node is a fork.</summary>
    public bool Fork { get; set; }

    /// <summary>Index into <see cref="TripLeg.Admins"/> for the admin (country/state) of this node.</summary>
    public uint AdminIndex { get; set; }

    /// <summary>The IANA time zone name at this node (empty if unknown).</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>The edges that intersect the path at this node but are not on the path.</summary>
    public List<TripIntersectingEdge> IntersectingEdges { get; } = new();
}

/// <summary>
/// A country/state admin record referenced by node admin indices. De-protobuf'd <c>TripLeg.Admin</c>.
/// </summary>
public sealed class TripAdmin
{
    /// <summary>Constructs an admin record.</summary>
    /// <param name="countryCode">ISO country code.</param>
    /// <param name="countryText">Country display name.</param>
    /// <param name="stateCode">ISO state code.</param>
    /// <param name="stateText">State display name.</param>
    public TripAdmin(string countryCode, string countryText, string stateCode, string stateText)
    {
        CountryCode = countryCode;
        CountryText = countryText;
        StateCode = stateCode;
        StateText = stateText;
    }

    /// <summary>ISO country code (e.g. "US").</summary>
    public string CountryCode { get; }

    /// <summary>Country display name.</summary>
    public string CountryText { get; }

    /// <summary>ISO state code.</summary>
    public string StateCode { get; }

    /// <summary>State display name.</summary>
    public string StateText { get; }
}

/// <summary>
/// Leg summary flags. De-protobuf'd subset of the proto <c>Summary</c> attached to a leg.
/// </summary>
public sealed class TripSummary
{
    /// <summary>True if the leg uses at least one tolled edge.</summary>
    public bool HasToll { get; set; }

    /// <summary>True if the leg uses at least one ferry edge.</summary>
    public bool HasFerry { get; set; }

    /// <summary>True if the leg uses at least one motorway edge.</summary>
    public bool HasHighway { get; set; }
}

/// <summary>
/// A single leg of a trip: the ordered nodes (each carrying the edge that leaves it), the decoded
/// and encoded shape, the admin table, the bounding box, and the leg summary. The de-protobuf'd
/// subset of the proto <c>TripLeg</c> sufficient to consume a route and build maneuvers.
/// </summary>
public sealed class TripLeg
{
    /// <summary>
    /// The ordered nodes of the leg. Each node (except the last) carries in <see cref="TripNode.Edge"/>
    /// the directed edge that leaves it. This mirrors the proto layout where edges hang off nodes.
    /// </summary>
    public List<TripNode> Nodes { get; } = new();

    /// <summary>The ordered directed edges that make up the leg (the non-null node edges, in order).</summary>
    public List<TripEdge> Edges { get; } = new();

    /// <summary>The decoded lat,lng shape of the entire leg.</summary>
    public List<PointLL> Shape { get; } = new();

    /// <summary>The polyline6-encoded shape of the entire leg (Valhalla's encoded shape string).</summary>
    public string EncodedShape { get; set; } = string.Empty;

    /// <summary>The admin (country/state) records referenced by node admin indices.</summary>
    public List<TripAdmin> Admins { get; } = new();

    /// <summary>Minimum corner of the leg bounding box (min lng, min lat).</summary>
    public PointLL BoundingBoxMin { get; set; } = new PointLL();

    /// <summary>Maximum corner of the leg bounding box (max lng, max lat).</summary>
    public PointLL BoundingBoxMax { get; set; } = new PointLL();

    /// <summary>Leg summary flags (toll / ferry / highway).</summary>
    public TripSummary Summary { get; } = new();

    /// <summary>The names of the graph search algorithms used to create this leg.</summary>
    public List<string> Algorithms { get; } = new();

    /// <summary>The OSM changeset (dataset) id the leg was built from (0 if unknown).</summary>
    public ulong OsmChangeset { get; set; }
}

/// <summary>
/// The full trip result: an ordered set of legs (one per break location pair). The de-protobuf'd
/// subset of the proto <c>TripRoute</c> / <c>Trip</c>.
/// </summary>
public sealed class TripPath
{
    /// <summary>The ordered legs of the trip.</summary>
    public List<TripLeg> Legs { get; } = new();
}
