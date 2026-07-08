// Faithful C# port of Valhalla baldr PathLocation (classic valhalla/baldr/pathlocation.h) @ 3.7.0.
//
// PORT-NOTE: at the 3.7.0 tag the standalone baldr/pathlocation.h no longer exists; loki now writes
// its correlation results directly into the protobuf valhalla::Location message
// (Location::path_edges / Location::correlation). The protobuf surface is an EXCLUDED module for this
// port. PathLocation is the value type loki/thor/sif actually pass around once an input
// <see cref="Location"/> has been correlated to the route network: it is a Location plus the list of
// candidate <see cref="PathEdge"/>s (the edges the location snapped to) and the candidate filter
// edges. This file ports that classic baldr::PathLocation faithfully (the design the proto Location
// supersedes), preserving the PathEdge fields, the SideOfStreet semantics, and the filtered-edge
// split. The JSON/ptree (de)serialization is NOT ported (json/rapidjson excluded).

using System;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// The result of correlating an input <see cref="Location"/> to the route network: the input
/// location plus the candidate edges it projected onto. Faithful port of
/// <c>valhalla::baldr::PathLocation</c> (see file header for the proto PORT-NOTE).
/// </summary>
public sealed class PathLocation : Location
{
    /// <summary>
    /// Reasons a candidate edge can be filtered out during correlation. Faithful port of
    /// <c>enum class PathLocation::EdgeFilterReason</c> bit flags (stored as a bit mask).
    /// </summary>
    [Flags]
    public enum EdgeFilterReasonType
    {
        /// <summary>Edge was not filtered.</summary>
        None = 0,

        /// <summary>Filtered because access was not allowed for the costing mode.</summary>
        Restricted = 1,

        /// <summary>Filtered because the edge is below the minimum road class.</summary>
        LowRoadClass = 2,

        /// <summary>Filtered because the edge does not match the search filter.</summary>
        ExcludedFromSearch = 4,

        /// <summary>Filtered because the edge is closed (e.g. by live traffic).</summary>
        Closed = 8,
    }

    /// <summary>
    /// The projection of an input location onto a directed edge: which edge, where along it the
    /// projection landed (percent along + projected point), the distance to it, and the side of the
    /// street. Faithful port of the nested <c>struct PathLocation::PathEdge</c>.
    /// </summary>
    public sealed class PathEdge : IEquatable<PathEdge>
    {
        /// <summary>Constructs a path edge. Faithful port of the C++ PathEdge ctor.</summary>
        /// <param name="id">The directed edge the location correlated to.</param>
        /// <param name="percentAlong">Percent (0..1) along the edge where the projection landed.</param>
        /// <param name="projected">The projected point on the edge.</param>
        /// <param name="distance">Distance (meters) from the input point to <paramref name="projected"/>.</param>
        /// <param name="sos">The side of the street the input point is on.</param>
        /// <param name="outboundReach">Number of nodes reachable outbound from this edge.</param>
        /// <param name="inboundReach">Number of nodes reachable inbound to this edge.</param>
        public PathEdge(
            GraphId id,
            double percentAlong,
            PointLL projected,
            double distance,
            SideOfStreetType sos = SideOfStreetType.None,
            uint outboundReach = 0,
            uint inboundReach = 0)
        {
            Id = id;
            PercentAlong = percentAlong;
            Projected = projected ?? throw new ArgumentNullException(nameof(projected));
            Distance = distance;
            Sos = sos;
            OutboundReach = outboundReach;
            InboundReach = inboundReach;
        }

        /// <summary>The directed edge the location correlated to.</summary>
        public GraphId Id { get; }

        /// <summary>Percent (0..1) along the edge where the projection landed.</summary>
        public double PercentAlong { get; }

        /// <summary>The projected point on the edge nearest the input location.</summary>
        public PointLL Projected { get; }

        /// <summary>Distance (meters) from the input location to <see cref="Projected"/>.</summary>
        public double Distance { get; }

        /// <summary>The side of the street the input location is on relative to this edge.</summary>
        public SideOfStreetType Sos { get; }

        /// <summary>Number of nodes reachable outbound from this edge (reachability check).</summary>
        public uint OutboundReach { get; }

        /// <summary>Number of nodes reachable inbound to this edge (reachability check).</summary>
        public uint InboundReach { get; }

        /// <summary>
        /// True if the projection landed at the begin node of the edge (percent_along == 0).
        /// Faithful port of <c>begin_node()</c>.
        /// </summary>
        public bool BeginNode() => PercentAlong < 1.0 - PercentAlongTolerance && PercentAlong <= PercentAlongTolerance;

        /// <summary>
        /// True if the projection landed at the end node of the edge (percent_along == 1). Faithful
        /// port of <c>end_node()</c>.
        /// </summary>
        public bool EndNode() => PercentAlong > PercentAlongTolerance && PercentAlong >= 1.0 - PercentAlongTolerance;

        /// <inheritdoc/>
        public bool Equals(PathEdge? other)
        {
            if (other is null)
            {
                return false;
            }

            return Id == other.Id
                && Sos == other.Sos
                && PercentAlong.Equals(other.PercentAlong)
                && Projected.Equals(other.Projected)
                && Distance.Equals(other.Distance);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is PathEdge other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Id, PercentAlong, Distance, (int)Sos);
    }

    // Tolerance used by begin_node()/end_node(). Mirrors the C++ kCornerTolerance behavior of
    // treating projections at the very ends of an edge as snapping to its nodes.
    private const double PercentAlongTolerance = 0.001;

    /// <summary>
    /// Constructs a path location from an input location. Faithful port of
    /// <c>PathLocation(const Location&amp;)</c>.
    /// </summary>
    /// <param name="location">The input location being correlated.</param>
    public PathLocation(Location location)
        : base(
            location.LatLng,
            location.StopType,
            location.MinimumReachability,
            location.Radius,
            location.PreferredSide)
    {
        Heading = location.Heading;
        HeadingTolerance = location.HeadingTolerance;
        NodeSnapTolerance = location.NodeSnapTolerance;
        Name = location.Name;
        Street = location.Street;
        DateTime = location.DateTime;
    }

    /// <summary>
    /// The candidate edges the location correlated to (those that passed the costing/search filters).
    /// Faithful port of <c>std::vector&lt;PathEdge&gt; edges</c>.
    /// </summary>
    public List<PathEdge> Edges { get; } = new();

    /// <summary>
    /// The edges that were considered but filtered out, paired with the reason. Faithful port of
    /// <c>std::vector&lt;PathEdge&gt; filtered_edges</c> (with the filter reason carried alongside).
    /// </summary>
    public List<(PathEdge Edge, EdgeFilterReasonType Reason)> FilteredEdges { get; } = new();

    /// <summary>
    /// Equality matches the C++ <c>operator==</c>: the underlying location is equal and every
    /// candidate edge in <paramref name="other"/> is present in this location's edge set. Faithful
    /// port of <c>PathLocation::operator==</c>.
    /// </summary>
    /// <param name="other">The path location to compare against.</param>
    public bool Equals(PathLocation other)
    {
        if (other is null)
        {
            return false;
        }

        // The base location must match.
        if (!base.Equals(other))
        {
            return false;
        }

        // Same number of correlated edges.
        if (Edges.Count != other.Edges.Count)
        {
            return false;
        }

        // Every edge in other must be found in this (order-independent), exactly as the C++ does with
        // its nested loop over edges.
        foreach (PathEdge e in other.Edges)
        {
            if (!Edges.Any(f => f.Equals(e)))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PathLocation other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();
}
