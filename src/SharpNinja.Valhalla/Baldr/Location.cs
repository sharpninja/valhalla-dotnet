// Faithful C# port of Valhalla baldr Location (valhalla/baldr/location.h, classic form) @ 3.7.0.
//
// PORT-NOTE: at the 3.7.0 tag the baldr/location.h header has been reduced to a set of operator==
// helpers over the protobuf-generated valhalla::Location message (proto/common.proto). The protobuf
// surface (proto/common.pb.h, rapidjson, the whole protobuf runtime) is an EXCLUDED module for this
// port. What loki actually consumes is the small "location" value type plus its correlated
// candidates: an input lat,lng with a handful of search controls, and (after correlation) a
// PathLocation carrying the projected PathEdges. This file ports that loki-facing value type
// faithfully (the classic baldr::Location design that the proto Location supersedes), with the same
// field set, enums, and semantics. The JSON (de)serialization (FromJson/FromCsv/FromPtree/ToPtree)
// is NOT ported (json/rapidjson is excluded); only the routing-relevant data + comparison are kept.

using System;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Captures an input location to be used in route generation: a lat,lng position plus the search
/// controls loki uses to correlate it to the route network. Faithful port of the loki-facing
/// <c>valhalla::baldr::Location</c> value type (see file header for the proto PORT-NOTE).
/// </summary>
public class Location : IEquatable<Location>
{
    /// <summary>
    /// What side of the road this location is on (relative to the direction of travel). Mirrors C++
    /// <c>enum class SideOfStreet</c>.
    /// </summary>
    public enum SideOfStreetType
    {
        /// <summary>Either side of the street (unknown / no preference).</summary>
        None = 0,

        /// <summary>The left side of the street.</summary>
        Left,

        /// <summary>The right side of the street.</summary>
        Right,
    }

    /// <summary>
    /// Whether the location is a break (stop) in the route or a through point. Mirrors C++
    /// <c>enum class StopType</c>.
    /// </summary>
    public enum StopTypeValue
    {
        /// <summary>A break: the route stops at this location (a new leg starts/ends).</summary>
        Break = 0,

        /// <summary>A through point: the route passes through but does not stop.</summary>
        Through,

        /// <summary>A via point: a soft waypoint along the route.</summary>
        Via,

        /// <summary>A break-through point.</summary>
        BreakThrough,
    }

    /// <summary>
    /// You have to initialize the location with something. Faithful port of the C++ ctor
    /// <c>Location(const midgard::PointLL&amp; latlng, const StopType&amp; stoptype = StopType::BREAK)</c>.
    /// </summary>
    /// <param name="latlng">The lat,lng of this location.</param>
    /// <param name="stoptype">Whether this is a break (default) or a through point.</param>
    /// <param name="minimumReachability">Minimum number of nodes reachable for a valid candidate.</param>
    /// <param name="radius">Search radius (meters) for candidate edges.</param>
    /// <param name="preferredSide">Preferred side of the street, if any.</param>
    public Location(
        PointLL latlng,
        StopTypeValue stoptype = StopTypeValue.Break,
        uint minimumReachability = 0,
        uint radius = 0,
        SideOfStreetType preferredSide = SideOfStreetType.None)
    {
        LatLng = latlng ?? throw new ArgumentNullException(nameof(latlng));
        StopType = stoptype;
        MinimumReachability = minimumReachability;
        Radius = radius;
        PreferredSide = preferredSide;
    }

    /// <summary>The position of the location. This is the only required parameter.</summary>
    public PointLL LatLng { get; set; }

    /// <summary>Whether this location is a break or through point.</summary>
    public StopTypeValue StopType { get; set; }

    /// <summary>
    /// The minimum number of nodes reachable from a candidate edge for it to be considered a valid
    /// correlation. 0 disables the reachability check.
    /// </summary>
    public uint MinimumReachability { get; set; }

    /// <summary>The search radius, in meters, around <see cref="LatLng"/> for candidate edges.</summary>
    public uint Radius { get; set; }

    /// <summary>The preferred side of the street for this location, if any.</summary>
    public SideOfStreetType PreferredSide { get; set; }

    /// <summary>Preferred heading (degrees) of travel through this location, if specified.</summary>
    public int? Heading { get; set; }

    /// <summary>Tolerance (degrees) around <see cref="Heading"/> for matching candidate edges.</summary>
    public int? HeadingTolerance { get; set; }

    /// <summary>Distance (meters) within which a candidate snaps to a node rather than an edge.</summary>
    public int? NodeSnapTolerance { get; set; }

    /// <summary>The (optional) name of this location.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The (optional) street of this location.</summary>
    public string Street { get; set; } = string.Empty;

    /// <summary>The (optional) date/time string associated with this location.</summary>
    public string DateTime { get; set; } = string.Empty;

    /// <summary>
    /// The (optional) time-tracking info for this location, used as the route's start time by the
    /// time-dependent A* algorithms. PORT-NOTE: in the engine thor builds this with
    /// <c>TimeInfo::make(location, graphreader, &amp;tz_cache_)</c> from <see cref="DateTime"/> and the
    /// DateTime timezone database; both are part of a later port slice. Callers that already have a
    /// <see cref="TimeInfo"/> (e.g. a single-timezone depart-at) supply it here; when null the route
    /// is treated as non-time-dependent (<see cref="Baldr.TimeInfo.Invalid"/>), which keeps
    /// EdgeCost/TransitionCost identical to the engine when no time is set.
    /// </summary>
    public TimeInfo? TimeInfo { get; set; }

    // ------------------------------------------------------------------
    // Loki search controls (the additional proto valhalla::Location fields the loki edge candidate
    // search reads). PORT-NOTE: at 3.7.0 these live on the proto Location message; they are added
    // here as plain properties with Valhalla's documented defaults so the loki search reproduces the
    // engine without the proto runtime. See loki/search.cc.
    // ------------------------------------------------------------------

    /// <summary>
    /// Hard cutoff distance (meters) beyond which a candidate edge is rejected. C++ proto
    /// <c>search_cutoff</c>. Default is the Valhalla default (35 km).
    /// </summary>
    public double SearchCutoff { get; set; } = DefaultSearchCutoffMeters;

    /// <summary>
    /// Minimum number of nodes reachable OUTBOUND from a candidate edge for it to be considered.
    /// C++ proto <c>minimum_outbound_reachability</c>. Defaults to <see cref="MinimumReachability"/>.
    /// </summary>
    public uint? MinimumOutboundReachabilityOverride { get; set; }

    /// <summary>
    /// Minimum number of nodes reachable INBOUND to a candidate edge for it to be considered.
    /// C++ proto <c>minimum_inbound_reachability</c>. Defaults to <see cref="MinimumReachability"/>.
    /// </summary>
    public uint? MinimumInboundReachabilityOverride { get; set; }

    /// <summary>Minimum outbound reachability for this location. Faithful port of <c>minimum_outbound_reachability()</c>.</summary>
    public uint MinimumOutboundReachability() => MinimumOutboundReachabilityOverride ?? MinimumReachability;

    /// <summary>Minimum inbound reachability for this location. Faithful port of <c>minimum_inbound_reachability()</c>.</summary>
    public uint MinimumInboundReachability() => MinimumInboundReachabilityOverride ?? MinimumReachability;

    /// <summary>
    /// Tolerance (meters) within which a snap is considered exactly on the edge (side-of-street is
    /// then kNone). C++ proto <c>street_side_tolerance</c>. Default 5 m.
    /// </summary>
    public double StreetSideTolerance { get; set; } = DefaultStreetSideToleranceMeters;

    /// <summary>
    /// Distance (meters) beyond which a snap is too far to determine side of street. C++ proto
    /// <c>street_side_max_distance</c>. Default 1000 m.
    /// </summary>
    public double StreetSideMaxDistance { get; set; } = DefaultStreetSideMaxDistanceMeters;

    /// <summary>
    /// Road-class cutoff for side-of-street filtering (roads below this class are not side-filtered).
    /// C++ proto <c>street_side_cutoff</c>. Default service (lowest).
    /// </summary>
    public byte StreetSideCutoff { get; set; } = (byte)Baldr.RoadClass.ServiceOther;

    /// <summary>
    /// Distance (meters) within which a candidate snaps to a node rather than an edge. Faithful port
    /// of <c>node_snap_tolerance()</c> (returns the int? <see cref="NodeSnapTolerance"/> or the
    /// Valhalla default of 5 m).
    /// </summary>
    public double NodeSnapToleranceMeters => NodeSnapTolerance ?? DefaultNodeSnapToleranceMeters;

    /// <summary>The optional display lat,lng used for side-of-street (C++ proto <c>display_ll</c>).</summary>
    public PointLL? DisplayLatLng { get; set; }

    /// <summary>The optional preferred layer (Z-level) the candidate edge must match. C++ proto <c>preferred_layer</c>.</summary>
    public int? PreferredLayer { get; set; }

    /// <summary>
    /// Whether the preferred side should match the side of travel, be the opposite, or either. C++
    /// proto <c>preferred_side</c> is represented by <see cref="PreferredSide"/> for the side value;
    /// this models the same/opposite/either preference used by <c>side_filter</c>.
    /// </summary>
    public PreferredSideType PreferredSideMode { get; set; } = PreferredSideType.Either;

    /// <summary>The Valhalla default search cutoff (meters).</summary>
    public const double DefaultSearchCutoffMeters = 35000.0;

    /// <summary>The Valhalla default node snap tolerance (meters).</summary>
    public const double DefaultNodeSnapToleranceMeters = 5.0;

    /// <summary>The Valhalla default street-side tolerance (meters).</summary>
    public const double DefaultStreetSideToleranceMeters = 5.0;

    /// <summary>The Valhalla default street-side max distance (meters).</summary>
    public const double DefaultStreetSideMaxDistanceMeters = 1000.0;

    /// <summary>
    /// Whether the location's preferred side should match, be opposite to, or be either side of the
    /// direction of travel. Mirrors the proto <c>Location.PreferredSide</c> enum.
    /// </summary>
    public enum PreferredSideType
    {
        /// <summary>Either side is acceptable (no side filtering).</summary>
        Either = 0,

        /// <summary>The candidate must be on the same side as the direction of travel.</summary>
        Same,

        /// <summary>The candidate must be on the opposite side from the direction of travel.</summary>
        Opposite,
    }

    /// <summary>
    /// Equality matches the C++ <c>operator==(const Location&amp;, const Location&amp;)</c>: the lat,lng,
    /// stop type, and the optional search controls.
    /// </summary>
    public bool Equals(Location? other)
    {
        if (other is null)
        {
            return false;
        }

        return LatLng.Equals(other.LatLng)
            && StopType == other.StopType
            && Heading == other.Heading
            && HeadingTolerance == other.HeadingTolerance
            && NodeSnapTolerance == other.NodeSnapTolerance
            && MinimumReachability == other.MinimumReachability
            && Radius == other.Radius
            && PreferredSide == other.PreferredSide
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(Street, other.Street, StringComparison.Ordinal)
            && string.Equals(DateTime, other.DateTime, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Location other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // C++ std::hash<Location> hashes only the lat,lng (see location.h). Mirror that.
        return HashCode.Combine(LatLng.Lng, LatLng.Lat);
    }

    /// <summary>Operator EqualTo.</summary>
    public static bool operator ==(Location? lhs, Location? rhs)
        => lhs is null ? rhs is null : lhs.Equals(rhs);

    /// <summary>Operator NotEqualTo.</summary>
    public static bool operator !=(Location? lhs, Location? rhs) => !(lhs == rhs);
}
