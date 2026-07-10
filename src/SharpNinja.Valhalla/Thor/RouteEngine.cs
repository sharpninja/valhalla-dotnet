// Faithful C# port of Valhalla's in-process thor ROUTE orchestration (valhalla @ 3.7.0).
// Source: F:/github/valhalla/src/thor/route_action.cc (thor_worker_t::route / get_path_algorithm /
// get_path / path_depart_at / path_arrive_by) + valhalla/thor/worker.h.
//
// This is the minimal, in-process equivalent of the HTTP worker's route handler: given a GraphReader,
// a single Sif DynamicCost, and origin/destination (+ optional via/through points), it
//   1. correlates every location to the route network via Loki search,
//   2. for each consecutive location pair picks a path algorithm exactly as get_path_algorithm does
//      (bidirectional A* in the general case; unidirectional time-dependent forward A* for the
//      trivial / connected-edge case the C++ comment calls out), runs it with the two-pass relaxed
//      retry that get_path implements, and
//   3. stitches the per-pair PathInfo lists into one path with the same elapsed-cost / path-distance
//      offset accumulation and same-edge dedup as path_depart_at, then builds a single TripLeg via
//      TripLegBuilder.
//
// PORT SCOPE / EXCLUSIONS (point-to-point auto/truck, in-process only):
//   - The HTTP worker plumbing (Api/Options proto request, measure_scope_time, adjust_locations,
//     AttributesController, parse_costing, add_warning, the cost_factor_lines edge-walking pass) is
//     EXCLUDED: this is the in-process algorithm core, not the worker. The caller supplies the already
//     constructed DynamicCost and Locations.
//   - Multimodal / transit / bikeshare / auto_pedestrian routetypes are EXCLUDED (those branches of
//     get_path_algorithm select multi_modal_transit / multimodal_astar which are not ported). The
//     supported routetypes are auto / truck / motorcycle / taxi / bus / motor_scooter, all of which
//     use bidir_astar or timedep_forward exactly as the default branch does.
//   - date_time / arrive_by routing is EXCLUDED: TimeInfo for the supported case is invalid (no
//     timezone DB in this slice; see BidirectionalAStar / Location.TimeInfo PORT-NOTEs). Only the
//     depart_at control flow is ported (arrive_by is the mirror image and would need the timezone
//     back-propagation that lives in a later slice). The time-zone propagation, hierarchy-limit
//     warnings, and intermediate_loc_edge_trimming (proto leg_shape_index / EdgeTrimmingInfo) are
//     therefore NOT ported - they only affect serialized timestamps and shape-cutting metadata, not
//     the path the algorithm finds. Multi-leg stitching (offset accumulation + same-edge dedup) IS
//     ported faithfully; the result is returned as a single merged TripLeg (point-to-point with
//     through/via waypoints), matching the common single-break route.
//   - get_path_algorithm's hierarchy-limit checking (check_hierarchy_limits / Set/GetHierarchyLimits
//     bookkeeping) is EXCLUDED: it only emits warnings and toggles user-supplied limits, which the
//     costing already carries. The algorithm-selection logic itself is reproduced exactly.

using System;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Thor;

/// <summary>
/// Minimal in-process port of Valhalla's thor ROUTE orchestration (<c>thor_worker_t::route</c> and the
/// <c>path_depart_at</c> / <c>get_path</c> / <c>get_path_algorithm</c> helpers it drives). Correlates
/// the input locations, picks and runs the least-cost path algorithm for each consecutive pair (with
/// the relaxed two-pass retry), stitches the legs, and builds a <see cref="TripLeg"/>. See the file
/// header for the excluded HTTP / serialization / multimodal / time-dependent surfaces.
/// </summary>
public sealed class RouteEngine
{
    // Threshold for running a second pass pedestrian route (route_action.cc anonymous namespace).
    // Pedestrian is not in scope here but the constant is kept for parity with get_path.
    private const float PedestrianMultipassThreshold = 50000.0f; // 50km

    private readonly GraphReader _reader;
    private readonly Action? _interrupt;

    // The algorithm instances. The C++ worker holds one of each as members and reuses them across
    // legs (calling Clear() between uses); we do the same.
    private readonly BidirectionalAStar _bidirAstar;
    private readonly UnidirectionalAStar _timedepForward;

    /// <summary>
    /// Constructs the route engine over a graph reader. Faithful to the worker holding the graph
    /// reader and the per-algorithm instances as members.
    /// </summary>
    /// <param name="reader">The tiled graph reader used for correlation and expansion.</param>
    /// <param name="interrupt">Optional abort hook called periodically by the algorithms / builder.</param>
    public RouteEngine(GraphReader reader, Action? interrupt = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _interrupt = interrupt;
        _bidirAstar = new BidirectionalAStar();
        _timedepForward = UnidirectionalAStar.TimeDepForward();
        _bidirAstar.SetInterrupt(interrupt);
        _timedepForward.SetInterrupt(interrupt);
    }

    /// <summary>
    /// Computes a route from <paramref name="origin"/> to <paramref name="destination"/> through the
    /// optional ordered <paramref name="vias"/>, returning the assembled <see cref="TripLeg"/>. Faithful
    /// in-process port of <c>thor_worker_t::route</c> -&gt; <c>path_depart_at</c> (see file header for
    /// the scope). The locations are correlated via Loki search using <paramref name="costing"/>, each
    /// consecutive pair is routed with the algorithm <c>get_path_algorithm</c> selects, and the legs
    /// are stitched into one path that <see cref="TripLegBuilder"/> turns into the leg.
    /// </summary>
    /// <param name="reader">The graph reader (must be the one the engine was constructed with).</param>
    /// <param name="costing">The Sif costing model (DynamicCost) to route with.</param>
    /// <param name="origin">The start location.</param>
    /// <param name="destination">The end location.</param>
    /// <param name="vias">Optional ordered intermediate (through/via) locations.</param>
    /// <returns>The assembled <see cref="TripLeg"/> for the route.</returns>
    /// <exception cref="InvalidOperationException">No route could be found (mirrors valhalla exception 442).</exception>
    public TripLeg Route(
        GraphReader reader,
        DynamicCost costing,
        Location origin,
        Location destination,
        IReadOnlyList<Location>? vias = null)
        => RouteAlternates(reader, costing, origin, destination, vias, options: null)[0];

    /// <summary>
    /// Computes one or more routes from <paramref name="origin"/> to <paramref name="destination"/>
    /// through the optional ordered <paramref name="vias"/>, returning one <see cref="TripLeg"/> per
    /// route (the primary route at index 0, alternates following by ascending cost). Alternates are only
    /// produced for a single origin/destination pair (no vias) when <paramref name="options"/> requests
    /// them (<c>HasAlternates</c> and <c>Alternates &gt; 0</c>); with vias present, or when alternates
    /// are not requested, exactly one stitched route is returned (the leg axis is kept separate from the
    /// route axis - see the file header two-axis note). Faithful in-process analogue of
    /// <c>path_depart_at</c> producing <c>trip.routes()</c>.
    /// </summary>
    /// <param name="reader">The graph reader (must be the one the engine was constructed with).</param>
    /// <param name="costing">The Sif costing model (DynamicCost) to route with.</param>
    /// <param name="origin">The start location.</param>
    /// <param name="destination">The end location.</param>
    /// <param name="vias">Optional ordered intermediate (through/via) locations.</param>
    /// <param name="options">Optional request options carrying the alternates request.</param>
    /// <returns>One <see cref="TripLeg"/> per route; index 0 is the primary route.</returns>
    /// <exception cref="InvalidOperationException">No route could be found (mirrors valhalla exception 442).</exception>
    public IReadOnlyList<TripLeg> RouteAlternates(
        GraphReader reader,
        DynamicCost costing,
        Location origin,
        Location destination,
        IReadOnlyList<Location>? vias = null,
        Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(costing);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);

        // Build the ordered location list (origin, vias..., destination) as the C++ options.locations()
        // would be. Correlate them all in one Loki search pass, exactly as loki precedes thor.
        var locations = new List<Location>(2 + (vias?.Count ?? 0)) { origin };
        if (vias is not null)
        {
            locations.AddRange(vias);
        }

        locations.Add(destination);

        IReadOnlyList<PathLocation> correlated = Correlate(locations, costing);

        // Build the mode-costing array the algorithms consume (single mode point-to-point).
        TravelMode mode = costing.TravelMode();
        var modeCosting = new ModeCosting { [(int)mode] = costing };

        // path_depart_at: build the route(s). The outer list is the route axis (alternates); each inner
        // path is one whole route (with any via/through legs already stitched).
        List<List<PathInfo>> routes = DepartAt(correlated, costing, modeCosting, mode, options, out List<string> algorithms);

        if (routes.Count == 0 || routes[0].Count == 0)
        {
            // no route found (valhalla_exception_t{442}).
            throw new InvalidOperationException("No route found between the supplied locations.");
        }

        // Form output information based on path edges. The first and last correlated locations are the
        // route's break points (origin / destination of each emitted route). Ordering follows the engine
        // order: primary first, alternates by ascending cost (from the stretch-sorted connections).
        var legs = new List<TripLeg>(routes.Count);
        foreach (List<PathInfo> route in routes)
        {
            if (route.Count == 0)
            {
                continue;
            }

            legs.Add(TripLegBuilder.Build(
                reader,
                modeCosting,
                route,
                correlated[0],
                correlated[^1],
                algorithms,
                _interrupt));
        }

        return legs;
    }

    /// <summary>
    /// Correlates the input locations to the route network via Loki search. Faithful to the loki step
    /// that precedes thor: each input becomes a <see cref="PathLocation"/> whose <c>Edges</c> are the
    /// snapped candidates the path algorithms read.
    /// </summary>
    private IReadOnlyList<PathLocation> Correlate(IReadOnlyList<Location> locations, DynamicCost costing)
    {
        var pathLocations = locations.Select(l => l as PathLocation ?? new PathLocation(l)).ToList();
        var search = new Search(_reader);
        search.DoSearch(pathLocations, costing);
        return pathLocations;
    }

    // Faithful port of thor_worker_t::get_path_algorithm for the supported (auto/truck/...) routetypes.
    // The multimodal/transit/bikeshare/auto_pedestrian and date_time branches are excluded (see header);
    // for the supported case the decision is: use the unidirectional time-dependent forward A* if the
    // origin/destination share an edge or are connected (bidirectional A* does not handle those trivial
    // / oneway-adjacent cases well), otherwise bidirectional A*.
    private PathAlgorithm GetPathAlgorithm(PathLocation origin, PathLocation destination)
    {
        // make sure they are all cancelable (C++ sets interrupt on each algorithm here).
        _bidirAstar.SetInterrupt(_interrupt);
        _timedepForward.SetInterrupt(_interrupt);

        // Use A* if any origin and destination edges are the same or are connected - otherwise use
        // bidirectional A*. Bidirectional A* does not handle trivial cases with oneways and has issues
        // when the cost of the origin or destination edge is high.
        foreach (PathLocation.PathEdge edge1 in origin.Edges)
        {
            foreach (PathLocation.PathEdge edge2 in destination.Edges)
            {
                bool sameGraphId = edge1.Id == edge2.Id;
                bool areConnected = _reader.AreEdgesConnected(edge1.Id, edge2.Id);
                if (sameGraphId || areConnected)
                {
                    return _timedepForward;
                }
            }
        }

        // No other special cases: land on bidirectional A*.
        return _bidirAstar;
    }

    // Faithful port of thor_worker_t::get_path: run the algorithm once (bidir A* with destination-only
    // edges disabled on the first pass), then - if no path was found and the costing allows multipass -
    // merge in the heading-filtered candidate edges, relax the hierarchy limits, re-enable
    // destination-only / conditional-destination, disable not-thru pruning, and try once more.
    private List<List<PathInfo>> GetPath(
        PathAlgorithm pathAlgorithm,
        PathLocation origin,
        PathLocation destination,
        DynamicCost costing,
        ModeCosting modeCosting,
        TravelMode mode,
        Options? options = null)
    {
        // If bidirectional A* disable use of destination-only edges on the first pass. Other path
        // algorithms can use destination-only edges on the first pass.
        bool isBidir = pathAlgorithm == _bidirAstar;
        costing.SetAllowDestinationOnly(!isBidir);

        costing.SetPass(0);
        List<List<PathInfo>> paths = pathAlgorithm.GetBestPath(origin, destination, _reader, modeCosting, mode, options);

        // Check if we should run a second pass pedestrian route (ferry). Pedestrian is out of scope, so
        // ped_second_pass is always false here; preserved for parity with the C++ condition.
        bool pedSecondPass = false;

        // If path is not found try again with relaxed limits (if allowed). Use less aggressive hierarchy
        // transition limits, and retry with more candidate edges (those filtered by heading on pass 1).
        if ((paths.Count == 0 || pedSecondPass) && costing.AllowMultiPass())
        {
            // add filtered edges to candidate edges for origin and destination.
            MergeFilteredEdges(origin);
            MergeFilteredEdges(destination);

            pathAlgorithm.Clear();
            costing.SetPass(1);
            costing.RelaxHierarchyLimits(isBidir);
            costing.SetAllowDestinationOnly(true);
            costing.SetAllowConditionalDestination(true);
            pathAlgorithm.SetNotThruPruning(false);

            // Get the best path. Return if not empty (else return the original path).
            List<List<PathInfo>> relaxedPaths =
                pathAlgorithm.GetBestPath(origin, destination, _reader, modeCosting, mode, options);
            if (relaxedPaths.Count != 0)
            {
                return relaxedPaths;
            }
        }

        return paths;
    }

    // Faithful port of thor_worker_t::path_depart_at, scoped to depart-at (no arrive_by, no timezone
    // back-propagation; see header). Iterates consecutive location pairs forward, picks + runs the
    // algorithm via get_path, and stitches the per-pair PathInfo lists into one path with the same
    // elapsed-cost / path-distance offset accumulation and same-edge dedup. Returns the merged path and
    // the ordered list of algorithm names used. Implements the low-reachability through-point retry.
    private List<List<PathInfo>> DepartAt(
        IReadOnlyList<PathLocation> correlatedInput,
        DynamicCost costing,
        ModeCosting modeCosting,
        TravelMode mode,
        Options? options,
        out List<string> algorithms)
    {
        List<PathLocation> correlated = correlatedInput.ToList();
        algorithms = new List<string>();

        // Route axis (alternates): multiple DISTINCT whole routes between the SAME single origin and
        // destination. Only meaningful for a single break-to-break pair (no through/via waypoints). When
        // requested, the algorithm's List<List<PathInfo>> IS the route list - each inner path is a whole
        // route - so return it directly (no stitch accumulator). This keeps the route axis cleanly
        // separate from the leg axis below.
        bool wantAlternates = options is not null && options.HasAlternates && options.Alternates != 0 &&
                              correlated.Count == 2;
        if (wantAlternates)
        {
            PathLocation altOrigin = correlated[0];
            PathLocation altDestination = correlated[1];
            PathAlgorithm alg = GetPathAlgorithm(altOrigin, altDestination);
            alg.Clear();
            algorithms.Add(alg.Name());

            // Pass the alternates-requesting options so GetBestPath sets desired_paths_count > 1.
            return GetPath(alg, altOrigin, altDestination, costing, modeCosting, mode, options);
        }

        // Leg axis (via / through, or single route without alternates): stitch each consecutive pair's
        // PRIMARY path into one merged route. Returned as a one-element route list.
        var path = new List<PathInfo>();
        var last_edge = GraphId.Invalid;
        bool allowRetry = true;

        // For each pair of locations (origin = prev(destination)). The C++ uses iterators; here we use
        // indices over the ordered list.
        int destinationIdx = 1;
        while (destinationIdx < correlated.Count)
        {
            int originIdx = destinationIdx - 1;
            if (!RouteTwoLocations(correlated, originIdx, destinationIdx, costing, modeCosting, mode,
                                   path, ref last_edge, algorithms))
            {
                // If routing failed because an intermediate waypoint snapped to a low-reachability road
                // (small connectivity component) leave only high-reachability candidates and retry once.
                if (allowRetry && originIdx != 0 && IsThroughPoint(correlated[originIdx]) &&
                    correlated[originIdx].Edges.Count > 0 &&
                    !IsHighlyReachable(correlated[originIdx], correlated[originIdx].Edges[0]))
                {
                    allowRetry = false;
                    // for each intermediate waypoint remove candidates with low reachability.
                    for (int i = 1; i < correlated.Count - 1; ++i)
                    {
                        PathLocation loc = correlated[i];
                        loc.Edges.RemoveAll(e => !IsHighlyReachable(loc, e));
                        if (loc.Edges.Count == 0)
                        {
                            throw new InvalidOperationException("No route found (intermediate waypoint unreachable).");
                        }
                    }

                    // reset the entire state of all legs and start over from the beginning.
                    last_edge = GraphId.Invalid;
                    path.Clear();
                    algorithms.Clear();
                    destinationIdx = 1;
                    continue;
                }

                // no route found (valhalla_exception_t{442}).
                throw new InvalidOperationException("No route found between the supplied locations.");
            }

            ++destinationIdx;
        }

        return path.Count == 0 ? new List<List<PathInfo>>() : new List<List<PathInfo>> { path };
    }

    // Faithful port of the route_two_locations lambda inside path_depart_at (time-zone propagation and
    // proto edge_trimming excluded; see header). Picks the algorithm, runs get_path, and merges the
    // returned path(s) into `path`. Returns false if no path was found for this pair.
    private bool RouteTwoLocations(
        List<PathLocation> correlated,
        int originIdx,
        int destinationIdx,
        DynamicCost costing,
        ModeCosting modeCosting,
        TravelMode mode,
        List<PathInfo> path,
        ref GraphId last_edge,
        List<string> algorithms)
    {
        PathLocation origin = correlated[originIdx];
        PathLocation destination = correlated[destinationIdx];

        // Get the algorithm type for this location pair.
        PathAlgorithm pathAlgorithm = GetPathAlgorithm(origin, destination);
        pathAlgorithm.Clear();
        algorithms.Add(pathAlgorithm.Name());

        // If we are continuing through a location we need to make sure we only allow the edge that was
        // used previously (avoid u-turns).
        if (IsThroughPoint(origin) && last_edge.IsValid())
        {
            GraphId keep = last_edge;
            origin.Edges.RemoveAll(e => e.Id != keep);
        }

        // Get best path and keep it. This is the LEG axis: only the PRIMARY path is stitched per pair.
        // GetPath is called without alternates options (default null), so it returns a single path; even
        // if an algorithm ever returned more, only tempPaths[0] is used - alternates (the route axis)
        // never leak into the leg accumulator. See DepartAt's two-axis note.
        List<List<PathInfo>> tempPaths = GetPath(pathAlgorithm, origin, destination, costing, modeCosting, mode);
        if (tempPaths.Count == 0 || tempPaths[0].Count == 0)
        {
            return false;
        }

        List<PathInfo> tempPath = tempPaths[0];
        last_edge = tempPath[^1].Edgeid;

        // Merge through legs by updating the time and splicing the lists.
        if (path.Count != 0)
        {
            Cost offset = path[^1].ElapsedCost;
            float distanceOffset = path[^1].PathDistance;
            foreach (PathInfo i in tempPath)
            {
                i.ElapsedCost += offset;
                i.PathDistance += distanceOffset;
            }

            // Connects via the same edge so we only need it once. (The proto edge_trimming
            // at_node computation is excluded; the same-edge dedup it gates is preserved: when the
            // last edge of the accumulated path equals the first edge of the next leg and the join
            // is at a node, drop the duplicate. With trimming excluded we conservatively dedup the
            // shared edge, which is the node-join case for through/via waypoints.)
            if (path[^1].Edgeid == tempPath[0].Edgeid)
            {
                path.RemoveAt(path.Count - 1);
            }

            path.AddRange(tempPath);
        }
        else
        {
            // Didn't need to merge.
            path.AddRange(tempPath);
        }

        return true;
    }

    // Merge the heading-filtered candidate edges back into the location's candidate edge list (the C++
    // origin.mutable_correlation()->mutable_edges()->MergeFrom(filtered_edges())). Edges that are
    // already present are not duplicated.
    private static void MergeFilteredEdges(PathLocation loc)
    {
        foreach ((PathLocation.PathEdge edge, _) in loc.FilteredEdges)
        {
            if (!loc.Edges.Any(e => e.Equals(edge)))
            {
                loc.Edges.Add(edge);
            }
        }
    }

    // Faithful port of is_through_point(): a through or break-through location.
    private static bool IsThroughPoint(Location l)
        => l.StopType == Location.StopTypeValue.Through || l.StopType == Location.StopTypeValue.BreakThrough;

    // Faithful port of is_highly_reachable(): the candidate edge's in/outbound reach meets the
    // location's minimum in/outbound reachability requirement.
    private static bool IsHighlyReachable(Location loc, PathLocation.PathEdge edge)
        => edge.InboundReach >= loc.MinimumInboundReachability() &&
           edge.OutboundReach >= loc.MinimumOutboundReachability();
}
