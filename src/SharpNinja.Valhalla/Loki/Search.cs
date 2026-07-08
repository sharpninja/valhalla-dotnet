// Faithful C# port of Valhalla loki edge-candidate search (valhalla @ 3.7.0).
// Source: F:/github/valhalla/src/loki/search.cc (876 LOC) + valhalla/loki/search.h.
//
// Projects each input lat,lng onto the nearest routable edge(s) in the tiled graph, producing the
// correlated PathEdges (the "snapping" step that feeds thor's A*). The algorithm walks outward
// bin-by-bin from each location (nearest-first via ClosestFirstGenerator), projects the location
// onto every candidate edge in each bin, keeps the best reachable / unreachable candidates, and
// finally turns the best candidates into correlated edges (snapping to a node when the projection
// lands at an edge end, otherwise correlating along the edge and its opposing "evil twin").
//
// PORT-NOTES (what changed vs. the C++ and why):
//   - Output model: C++ writes correlations into the proto valhalla::Location
//     (mutable_correlation()->mutable_edges()/filtered_edges()). The proto runtime is EXCLUDED.
//     Here each input is a baldr::PathLocation (the loki-facing value type) and correlations are
//     written into its Edges / FilteredEdges lists. PathEdge carries graph_id, percent_along,
//     projected ll, distance, side_of_street, and in/outbound reach, matching the proto PathEdge
//     fields loki sets.
//   - SearchFilter: the proto SearchFilter (min/max road class, exclude tunnel/bridge/toll/ramp/
//     ferry/closures, level) is modeled by the lightweight SearchFilter record below with the same
//     fields and the same default values, so search_filter() reproduces the engine. (The level
//     filter uses EdgeInfo.IncludesLevel exactly as the engine does.)
//   - Reachability: get_reach()/check_reachability() call loki::Reach, which derives from
//     thor::Dijkstras. Dijkstras is EXCLUDED from this slice, so reach is provided through the
//     pluggable IReachProvider. The default AllReachableProvider mirrors the engine's own
//     max_reach_limit==0 short-circuit (reachability checking disabled => every candidate is
//     reachable), which is the configuration used for plain point-to-point routing. A real Reach can
//     be supplied later without touching this file.
//   - side_filter needs reader.GetOpposingEdge / GetEndNode and node->drive_on_right(); ported.
//   - The std::move ownership dance in handle_bin (moving edge_info into the kept candidate) becomes
//     plain reference assignment in C#; the selection logic (in_radius / better / last_in_radius /
//     closer_external_reachable) is reproduced verbatim.

using System;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Loki;

/// <summary>
/// The proto <c>valhalla::SearchFilter</c> subset loki's search reads. Faithful field-for-field port
/// (with the engine's default values) so <c>search_filter()</c> behaves identically.
/// </summary>
public sealed class SearchFilter
{
    /// <summary>Reject roads with functional class numerically &gt; this (default service = lowest).</summary>
    public RoadClass MinRoadClass { get; set; } = RoadClass.ServiceOther;

    /// <summary>Reject roads with functional class numerically &lt; this (default motorway = highest).</summary>
    public RoadClass MaxRoadClass { get; set; } = RoadClass.Motorway;

    /// <summary>Exclude tunnels.</summary>
    public bool ExcludeTunnel { get; set; }

    /// <summary>Exclude bridges.</summary>
    public bool ExcludeBridge { get; set; }

    /// <summary>Exclude toll roads.</summary>
    public bool ExcludeToll { get; set; }

    /// <summary>Exclude ramps.</summary>
    public bool ExcludeRamp { get; set; }

    /// <summary>Exclude ferries.</summary>
    public bool ExcludeFerry { get; set; }

    /// <summary>Exclude edges currently closed by live traffic.</summary>
    public bool ExcludeClosures { get; set; }

    /// <summary>Restrict to edges that include this level (kMaxLevel disables the filter).</summary>
    public float Level { get; set; } = GraphConstants.MaxLevel;
}

/// <summary>
/// The optional SearchFilter for a location. PORT-NOTE: the proto Location carries this; it is hung
/// off the baldr Location here via this lookup so the search can read it. Defaults to an
/// all-permissive filter.
/// </summary>
public static class LocationSearchFilterExtensions
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Location, SearchFilter> Filters = new();

    /// <summary>Gets the search filter for the location (default all-permissive).</summary>
    public static SearchFilter GetSearchFilter(this Location location)
        => Filters.TryGetValue(location, out SearchFilter? f) ? f : DefaultFilter;

    /// <summary>Sets the search filter for the location.</summary>
    public static void SetSearchFilter(this Location location, SearchFilter filter)
        => Filters.AddOrUpdate(location, filter);

    private static readonly SearchFilter DefaultFilter = new();
}

/// <summary>
/// Provides the in/outbound reach of an edge for the candidate search. PORT-NOTE: the real provider
/// (loki::Reach) does a mini graph expansion via thor::Dijkstras, which is EXCLUDED from this slice.
/// </summary>
public interface IReachProvider
{
    /// <summary>
    /// Returns the in and outbound reach for a given edge. Mirrors <c>Reach::operator()</c>.
    /// </summary>
    /// <param name="edge">The directed edge.</param>
    /// <param name="edgeId">The directed edge id.</param>
    /// <param name="maxReach">The maximum reach to check.</param>
    /// <param name="reader">Graph reader for the expansion.</param>
    /// <param name="costing">Costing model to apply.</param>
    /// <returns>The reach in both directions.</returns>
    DirectedReach GetReach(DirectedEdge edge, GraphId edgeId, uint maxReach, GraphReader reader, DynamicCost costing);
}

/// <summary>In and outbound reach of an edge. Faithful port of <c>struct directed_reach</c>.</summary>
public struct DirectedReach
{
    /// <summary>Number of nodes reachable outbound from the edge.</summary>
    public uint Outbound;

    /// <summary>Number of nodes reachable inbound to the edge.</summary>
    public uint Inbound;

    /// <summary>Constructs a directed reach.</summary>
    public DirectedReach(uint outbound, uint inbound)
    {
        Outbound = outbound;
        Inbound = inbound;
    }
}

/// <summary>
/// Default reach provider that reports every edge as fully reachable. Mirrors the engine's
/// max_reach_limit==0 short-circuit used for plain point-to-point routing (reachability checking
/// disabled). PORT-NOTE: stands in for loki::Reach until thor::Dijkstras is ported.
/// </summary>
public sealed class AllReachableProvider : IReachProvider
{
    /// <inheritdoc/>
    public DirectedReach GetReach(DirectedEdge edge, GraphId edgeId, uint maxReach, GraphReader reader, DynamicCost costing)
        => new DirectedReach(maxReach, maxReach);
}

/// <summary>
/// Search class for finding locations within the route network. Faithful port of
/// <c>valhalla::loki::Search</c>.
/// </summary>
public sealed class Search
{
    private const ushort DisallowShortcut = DynamicCost.DisallowShortcut;

    private readonly GraphReader _reader;
    private readonly IReachProvider _reachProvider;
    private readonly BinHandler _handler;

    /// <summary>Constructor. Faithful port of <c>Search(GraphReader&amp; reader)</c>.</summary>
    /// <param name="reader">An object used to access tiled route data.</param>
    /// <param name="reachProvider">
    /// Optional reach provider. Defaults to <see cref="AllReachableProvider"/> (reachability
    /// disabled), mirroring the engine's max_reach_limit==0 behavior used for point-to-point routing.
    /// </param>
    public Search(GraphReader reader, IReachProvider? reachProvider = null)
    {
        _reader = reader;
        _reachProvider = reachProvider ?? new AllReachableProvider();
        _handler = new BinHandler(_reader, _reachProvider);
    }

    /// <summary>
    /// Find locations within the route network given input locations. Faithful port of
    /// <c>Search::search</c>. Correlations are written into each <see cref="PathLocation"/>'s
    /// <see cref="PathLocation.Edges"/> / <see cref="PathLocation.FilteredEdges"/> lists. A location
    /// with no projection will have no entries.
    /// </summary>
    /// <param name="locations">The positions to correlate to the route network.</param>
    /// <param name="costing">A costing object determining which edges are accessible candidates.</param>
    public void DoSearch(IReadOnlyList<PathLocation> locations, DynamicCost costing)
    {
        // we cannot continue without costing
        if (costing is null)
        {
            throw new InvalidOperationException("No costing was provided for edge candidate search");
        }

        // trivially finished already
        if (locations.Count == 0)
        {
            return;
        }

        _handler.Run(locations, costing);
    }

    // ------------------------------------------------------------------
    // Static filter helpers (anonymous-namespace functions in search.cc)
    // ------------------------------------------------------------------

    // Faithful port of search_filter().
    internal static bool SearchFilterMatch(DirectedEdge edge, DynamicCost costing, GraphTile tile, uint deIndex, SearchFilter filter)
    {
        uint roadClass = (uint)edge.Classification;
        uint minRoadClass = (uint)filter.MinRoadClass;
        uint maxRoadClass = (uint)filter.MaxRoadClass;

        // min_/max_road_class are integers where by default max_road_class is 0 (motorway) and
        // min_road_class is 7 (service). Reject roads outside the [max, min] range.
        return roadClass > minRoadClass || roadClass < maxRoadClass ||
               (filter.ExcludeTunnel && edge.Tunnel) || (filter.ExcludeBridge && edge.Bridge) ||
               (filter.ExcludeToll && edge.Toll) ||
               (filter.ExcludeRamp && edge.Use == Use.Ramp) ||
               (filter.ExcludeFerry && (edge.Use == Use.Ferry || edge.Use == Use.RailFerry)) ||
               (filter.ExcludeClosures && (costing.FlowMask() & GraphConstants.CurrentFlowMask) != 0 &&
                tile.IsClosed(deIndex)) ||
               (filter.Level != GraphConstants.MaxLevel && !tile.EdgeInfo(edge).IncludesLevel(filter.Level));
    }

    // Faithful port of heading_filter().
    internal static bool HeadingFilter(Location location, float angle)
    {
        // no heading means we filter nothing
        if (location.Heading is null)
        {
            return false;
        }

        float heading = location.Heading.Value;
        float tolerance = location.HeadingTolerance ?? 0;

        // closest distance between two angles which can be had across 0 or between the two
        if (heading > angle)
        {
            return Math.Min(heading - angle, (360.0f - heading) + angle) > tolerance;
        }

        return Math.Min(angle - heading, (360.0f - angle) + heading) > tolerance;
    }

    // Faithful port of layer_filter().
    internal static bool LayerFilter(Location location, sbyte layer)
    {
        // no layer - we do not filter
        if (location.PreferredLayer is null)
        {
            return false;
        }

        return location.PreferredLayer.Value != layer;
    }

    // Faithful port of side_filter().
    internal static bool SideFilter(PathLocation.PathEdge edge, Location location, GraphReader reader)
    {
        // nothing to filter if you dont want to filter or if there is no side of street
        if (edge.Sos == Location.SideOfStreetType.None ||
            location.PreferredSideMode == Location.PreferredSideType.Either)
        {
            return false;
        }

        // need this for further checking of driving side and road class
        GraphTile? tile = null;
        DirectedEdge? opp = reader.GetOpposingEdge(edge.Id, ref tile);
        if (opp is null)
        {
            return false;
        }

        // nothing to filter if it is a minor road; higher number means smaller road class
        uint roadClass = (uint)opp.Value.Classification;
        if (roadClass > location.StreetSideCutoff)
        {
            return false;
        }

        // need the driving side for this edge
        NodeInfo? node = reader.GetEndNode(opp.Value, ref tile);
        if (node is null)
        {
            return false;
        }

        // if its on the right and you drive on the right OR if its not on the right and you dont drive
        // on the right THEN its the same side that you drive on
        bool same = node.Value.DriveOnRight == (edge.Sos == Location.SideOfStreetType.Right);

        // if you asked for same and it was same OR if you asked for opposite and it was opposite THEN
        // we dont filter
        return same != (location.PreferredSideMode == Location.PreferredSideType.Same);
    }

    // Faithful port of flip_side().
    internal static Location.SideOfStreetType FlipSide(Location.SideOfStreetType side)
    {
        if (side != Location.SideOfStreetType.None)
        {
            return side == Location.SideOfStreetType.Left
                ? Location.SideOfStreetType.Right
                : Location.SideOfStreetType.Left;
        }

        return side;
    }

    internal static double Square(double v) => v * v;
}
