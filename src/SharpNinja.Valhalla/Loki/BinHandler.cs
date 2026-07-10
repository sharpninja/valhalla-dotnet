// Faithful C# port of Valhalla loki bin_handler_t / projector_wrapper / candidate_t
// (valhalla @ 3.7.0). Source: F:/github/valhalla/src/loki/search.cc (anonymous namespace).
//
// This is the engine of the edge-candidate search: bin_handler_t orchestrates the nearest-first bin
// walk for all locations at once, projector_wrapper tracks one location's outward bin traversal and
// its best reachable/unreachable candidates, and candidate_t models a single projection of a
// location onto an edge segment. See Search.cs for the output-model / reachability / proto port
// notes that apply throughout.

using System;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Loki;

/// <summary>
/// Models a candidate projection of a location onto a segment of an edge in a bin. Faithful port of
/// the C++ <c>struct candidate_t</c>.
/// </summary>
internal sealed class Candidate
{
    public double SqDistance;
    public PointLL Point = new();
    public int Index;
    public bool Prefiltered;

    public GraphId EdgeId;
    public DirectedEdge? Edge;
    public EdgeInfo? EdgeInfo;
    public GraphTile? Tile;

    /// <summary>Faithful port of <c>candidate_t::get_side</c>.</summary>
    public Location.SideOfStreetType GetSide(
        PointLL original,
        float tangAngle,
        double sqDistance,
        double sqTolerance,
        double sqMaxDistance)
    {
        // point is basically on the edge, or too far from the edge to determine side of street
        if (sqDistance < sqTolerance || sqDistance > sqMaxDistance)
        {
            return Location.SideOfStreetType.None;
        }

        // absolute angle between the snap point and the provided point
        double angleToPoint = Point.Heading(original);

        // add 360 degrees if angle becomes negative
        double angleDiff = angleToPoint - tangAngle;
        if (angleDiff < 0)
        {
            angleDiff += 360.0;
        }

        // 10 degrees on either side is considered to be straight ahead
        const float angleTolerance = 10.0f;

        // angle_diff in (10,170) => right; in (190,350) => left; otherwise straight ahead/behind.
        if (angleDiff > angleTolerance && angleDiff < (180.0f - angleTolerance))
        {
            return Location.SideOfStreetType.Right;
        }

        if (angleDiff > (180.0f + angleTolerance) && angleDiff < (360.0f - angleTolerance))
        {
            return Location.SideOfStreetType.Left;
        }

        return Location.SideOfStreetType.None;
    }
}

/// <summary>
/// Tracks the outward bin traversal of one location and its best candidates. Faithful port of the
/// C++ <c>struct projector_wrapper</c>.
/// </summary>
internal sealed class ProjectorWrapper
{
    public ProjectorWrapper(PathLocation location, GraphReader reader)
    {
        Location = location;
        Binner = new ClosestFirstGenerator(TileHierarchy.Levels()[^1].Tiles, location.LatLng);
        SqRadius = Search.Square(location.Radius);
        Project = new Projector(location.LatLng);
        Unreachable = new List<Candidate>(64);
        Reachable = new List<Candidate>(64);

        // initialize
        NextBin(reader);
    }

    public ClosestFirstGenerator Binner { get; }

    public GraphTile? CurTile { get; private set; }

    public PathLocation Location { get; }

    public ushort BinIndex { get; private set; }

    public double SqRadius { get; }

    public List<Candidate> Unreachable { get; }

    public List<Candidate> Reachable { get; }

    public double ClosestExternalReachable { get; set; } = double.MaxValue;

    public Projector Project { get; }

    public bool HasSameBin(ProjectorWrapper other) => ReferenceEquals(CurTile, other.CurTile) && BinIndex == other.BinIndex;

    public bool HasBin() => CurTile is not null;

    /// <summary>Advance to the next bin. Faithful port of <c>next_bin</c>. Must not be called if HasBin() is false.</summary>
    public void NextBin(GraphReader reader)
    {
        do
        {
            // give up if the next bin is outside the overall cut off OR we have something AND cant
            // find more in the search radius AND cant find anything better in general than what we have
            (int tileIndex, ushort binIndex, double distance) = Binner.Next();
            BinIndex = binIndex;
            if (distance > Location.SearchCutoff ||
                (Reachable.Count > 0 && distance > Location.Radius &&
                 distance > Math.Sqrt(Reachable[^1].SqDistance)))
            {
                CurTile = null;
                break;
            }

            // grab the tile the lat, lon is in
            var tileId = new GraphId((uint)tileIndex, TileHierarchy.Levels()[^1].Level, 0);
            CurTile = reader.GetGraphTile(tileId);
        }
        while (CurTile is null);
    }
}

/// <summary>
/// Orchestrates the edge-candidate search across all locations. Faithful port of the C++
/// <c>struct bin_handler_t</c>.
/// </summary>
internal sealed class BinHandler
{
    private const ushort DisallowShortcut = DynamicCost.DisallowShortcut;

    private readonly GraphReader _reader;
    private readonly IReachProvider _reachProvider;

    private List<ProjectorWrapper> _pps = new();
    private DynamicCost _costing = null!;
    private uint _maxReachLimit;
    private List<Candidate> _binCandidates = new();
    private readonly HashSet<ulong> _correlatedEdges = new();

    // keep track of edges whose reachability we've already computed (keyed by edge id since the C#
    // DirectedEdge is a value type and cannot be used as a stable reference key like the C++ pointer).
    private readonly Dictionary<ulong, DirectedReach> _directedReaches = new();

    public BinHandler(GraphReader reader, IReachProvider reachProvider)
    {
        _reader = reader;
        _reachProvider = reachProvider;
    }

    private void Clear()
    {
        _pps.Clear();
        _maxReachLimit = 0;
        _binCandidates.Clear();
        _correlatedEdges.Clear();
        _directedReaches.Clear();
    }

    // ------------------------------------------------------------------
    // get_reach / check_reachability
    // ------------------------------------------------------------------

    private DirectedReach GetReach(GraphId edgeId, DirectedEdge edge)
    {
        if (_directedReaches.TryGetValue(edgeId.Value, out DirectedReach cached))
        {
            return cached;
        }

        DirectedReach reach = _reachProvider.GetReach(edge, edgeId, _maxReachLimit, _reader, _costing);
        _directedReaches[edgeId.Value] = reach;
        return reach;
    }

    // Faithful port of check_reachability.
    private DirectedReach CheckReachability(int begin, int end, GraphTile tile, DirectedEdge edge, GraphId edgeId)
    {
        // no need when set to 0
        if (_maxReachLimit == 0)
        {
            return default;
        }

        // do we already know about this one?
        if (_directedReaches.TryGetValue(edgeId.Value, out DirectedReach found))
        {
            return found;
        }

        // only worth checking if this could become the best reachable option for a given location
        bool check = false;
        for (int i = begin; i < end; ++i)
        {
            ProjectorWrapper p = _pps[i];
            Candidate c = _binCandidates[i - begin];
            check = check || p.Reachable.Count == 0 || c.SqDistance < p.Reachable[^1].SqDistance;
        }

        // assume its reachable
        if (!check)
        {
            return new DirectedReach(_maxReachLimit, _maxReachLimit);
        }

        DirectedReach reach = _reachProvider.GetReach(edge, edgeId, _maxReachLimit, _reader, _costing);
        _directedReaches[edgeId.Value] = reach;

        // if both reaches are nonzero and the opposing edge is not filtered then both edges share reach
        GraphTile? oppTile = tile;
        DirectedEdge? oppEdge = _reader.GetOpposingEdge(edge, ref oppTile);
        if (reach.Outbound > 0 && reach.Inbound > 0 && oppEdge is not null &&
            _costing.Allowed(oppEdge.Value, oppTile!, DisallowShortcut))
        {
            GraphId oppId = _reader.GetOpposingEdgeId(edgeId);
            if (oppId.IsValid())
            {
                _directedReaches[oppId.Value] = reach;
            }
        }

        return reach;
    }

    // ------------------------------------------------------------------
    // correlate_node / correlate_edge
    // ------------------------------------------------------------------

    // Faithful port of correlate_node.
    private void CorrelateNode(PathLocation location, GraphId foundNode, Candidate candidate)
    {
        PointLL pt = location.LatLng;

        // the search cutoff is a hard filter so skip any outside of that
        if (candidate.Point.Distance(pt) > location.SearchCutoff)
        {
            return;
        }

        // we might need to go to different levels; cache the distance lazily
        double distance = double.MinValue;

        void Crawl(GraphId nodeId, bool followTransitions)
        {
            GraphTile? tile = _reader.GetGraphTile(nodeId);
            if (tile is null)
            {
                return;
            }

            NodeInfo node = tile.Node(nodeId);
            uint startIndex = node.EdgeIndex;
            uint endIndex = node.EdgeIndex + node.EdgeCount;
            PointLL nodeLl = node.LatLng(tile.BaseLl());

            // cache the distance
            if (distance == double.MinValue)
            {
                distance = nodeLl.Distance(pt);
            }

            // add edges entering/leaving this node
            for (uint e = startIndex; e < endIndex; ++e)
            {
                DirectedEdge edge = tile.DirectedEdge((int)e);
                var id = new GraphId(tile.Id().Tileid(), tile.Id().Level(), e);
                EdgeInfo info = tile.EdgeInfo(edge);
                IReadOnlyList<PointLL> shape = info.Shape();

                // heading of the snapped point to the shape for the heading filter
                int index = edge.Forward ? 0 : shape.Count - 2;
                float angle = Util.TangentAngle(
                    index, candidate.Point, shape,
                    GraphConstants.GetOffsetForHeading(edge.Classification, edge.Use), edge.Forward);
                sbyte layer = info.Layer();

                // re-evaluate the filter because we may be seeing these edges a second time
                if (_costing.Allowed(edge, tile, DisallowShortcut) &&
                    !Search.SearchFilterMatch(edge, _costing, tile, e, location.GetSearchFilter()))
                {
                    DirectedReach reach = GetReach(id, edge);
                    var pathEdge = new PathLocation.PathEdge(
                        id, 0, nodeLl, distance, Location.SideOfStreetType.None, reach.Outbound, reach.Inbound);

                    if ((Search.HeadingFilter(location, angle) || Search.LayerFilter(location, layer)) &&
                        _correlatedEdges.Add(id.Value))
                    {
                        location.FilteredEdges.Add((pathEdge, PathLocation.EdgeFilterReasonType.None));
                    }
                    else if (_correlatedEdges.Add(id.Value))
                    {
                        location.Edges.Add(pathEdge);
                    }
                }

                // do we want the evil twin
                GraphTile? otherTile = null;
                GraphId otherId = _reader.GetOpposingEdgeId(id, out DirectedEdge? otherEdge, ref otherTile);
                if (otherEdge is null)
                {
                    continue;
                }

                uint otherIdx = otherId.Id();
                if (_costing.Allowed(otherEdge.Value, otherTile!, DisallowShortcut) &&
                    !Search.SearchFilterMatch(otherEdge.Value, _costing, otherTile!, otherIdx, location.GetSearchFilter()))
                {
                    float oppAngle = (angle + 180.0f) % 360.0f;
                    DirectedReach reach = GetReach(otherId, otherEdge.Value);
                    var pathEdge = new PathLocation.PathEdge(
                        otherId, 1, nodeLl, distance, Location.SideOfStreetType.None, reach.Outbound, reach.Inbound);

                    if ((Search.HeadingFilter(location, oppAngle) || Search.LayerFilter(location, layer)) &&
                        _correlatedEdges.Add(otherId.Value))
                    {
                        location.FilteredEdges.Add((pathEdge, PathLocation.EdgeFilterReasonType.None));
                    }
                    else if (_correlatedEdges.Add(otherId.Value))
                    {
                        location.Edges.Add(pathEdge);
                    }
                }
            }

            // follow transition to other hierarchy levels
            if (followTransitions && node.TransitionCount > 0)
            {
                for (uint i = 0; i < node.TransitionCount; ++i)
                {
                    NodeTransition trans = tile.Transition(node.TransitionIndex + i);
                    Crawl(trans.EndNode(), false);
                }
            }
        }

        // start where we are and crawl from there
        Crawl(foundNode, true);
    }

    // Faithful port of correlate_edge.
    private void CorrelateEdge(PathLocation location, Candidate candidate)
    {
        PointLL pt = location.LatLng;
        double distance = candidate.Point.Distance(pt);

        // the search cutoff is a hard filter so skip any outside of that
        if (distance > location.SearchCutoff)
        {
            return;
        }

        if (candidate.Edge is null || candidate.EdgeInfo is null || candidate.Tile is null)
        {
            return;
        }

        DirectedEdge edge = candidate.Edge.Value;
        IReadOnlyList<PointLL> shape = candidate.EdgeInfo.Shape();

        // ratio in the direction of the edge we are correlated to
        double partialLength = 0;
        for (int i = 0; i < candidate.Index; ++i)
        {
            partialLength += shape[i].Distance(shape[i + 1]);
        }

        partialLength += shape[candidate.Index].Distance(candidate.Point);

        // length of the edge only has meters resolution; clamp partial to the edge length
        partialLength = Math.Min(partialLength, edge.Length);
        double lengthRatio = partialLength / edge.Length;
        if (!edge.Forward)
        {
            lengthRatio = 1.0 - lengthRatio;
        }

        // heading of the snapped point to the shape for the heading filter / side of street
        float angle = Util.TangentAngle(
            candidate.Index, candidate.Point, shape,
            GraphConstants.GetOffsetForHeading(edge.Classification, edge.Use), edge.Forward);
        sbyte layer = candidate.EdgeInfo.Layer();
        double sqTolerance = Search.Square(location.StreetSideTolerance);
        double sqMaxDistance = Search.Square(location.StreetSideMaxDistance);
        PointLL displayPt = location.DisplayLatLng ?? pt;
        bool hasDisplay = location.DisplayLatLng is not null;
        Location.SideOfStreetType side = candidate.GetSide(
            hasDisplay ? displayPt : pt,
            angle,
            hasDisplay ? displayPt.DistanceSquared(candidate.Point) : candidate.SqDistance,
            sqTolerance,
            sqMaxDistance);
        DirectedReach edgeReach = GetReach(candidate.EdgeId, edge);

        var firstPathEdge = new PathLocation.PathEdge(
            candidate.EdgeId, lengthRatio, candidate.Point, distance, side, edgeReach.Outbound, edgeReach.Inbound);

        // correlate the edge we found if its not filtered out
        bool hardFiltered = Search.SearchFilterMatch(
            edge, _costing, candidate.Tile, candidate.EdgeId.Id(), location.GetSearchFilter());
        if (!hardFiltered && (Search.SideFilter(firstPathEdge, location, _reader) ||
                              Search.HeadingFilter(location, angle) || Search.LayerFilter(location, layer)))
        {
            location.FilteredEdges.Add((firstPathEdge, PathLocation.EdgeFilterReasonType.None));
        }
        else if (!hardFiltered && _correlatedEdges.Add(candidate.EdgeId.Value))
        {
            location.Edges.Add(firstPathEdge);
        }

        // correlate its evil twin
        GraphTile? otherTile = null;
        GraphId opposingEdgeId = _reader.GetOpposingEdgeId(candidate.EdgeId, out DirectedEdge? otherEdge, ref otherTile);
        if (otherEdge is not null && _costing.Allowed(otherEdge.Value, otherTile!, DisallowShortcut) &&
            !Search.SearchFilterMatch(otherEdge.Value, _costing, otherTile!, opposingEdgeId.Id(), location.GetSearchFilter()))
        {
            float oppAngle = (angle + 180.0f) % 360.0f;
            DirectedReach reach = GetReach(opposingEdgeId, otherEdge.Value);
            var otherPathEdge = new PathLocation.PathEdge(
                opposingEdgeId, 1 - lengthRatio, candidate.Point, distance,
                Search.FlipSide(side), reach.Outbound, reach.Inbound);

            if (Search.SideFilter(otherPathEdge, location, _reader) || Search.HeadingFilter(location, oppAngle) ||
                Search.LayerFilter(location, layer))
            {
                location.FilteredEdges.Add((otherPathEdge, PathLocation.EdgeFilterReasonType.None));
            }
            else if (_correlatedEdges.Add(opposingEdgeId.Value))
            {
                location.Edges.Add(otherPathEdge);
            }
        }
    }

    // ------------------------------------------------------------------
    // handle_bin
    // ------------------------------------------------------------------

    // Faithful port of handle_bin (begin/end are indices into _pps; the matching bin_candidates are
    // _binCandidates[0 .. end-begin)).
    private void HandleBin(int begin, int end)
    {
        // iterate over the edges in the bin
        GraphTile? tile = _pps[begin].CurTile;
        IReadOnlyList<GraphId> edges = tile!.GetBin(_pps[begin].BinIndex);
        foreach (GraphId edgeIdInit in edges)
        {
            GraphId edgeId = edgeIdInit;

            // get the tile and edge
            tile = _reader.GetGraphTile(edgeId, ref tile);
            if (tile is null)
            {
                continue;
            }

            DirectedEdge edge = tile.DirectedEdge(edgeId);

            // if this edge is filtered, try the opposing edge instead
            if (!_costing.Allowed(edge, tile, DisallowShortcut))
            {
                GraphTile? oppTile = tile;
                GraphId oppEdgeid = _reader.GetOpposingEdgeId(edgeId, out DirectedEdge? oppEdge, ref oppTile);
                if (!oppEdgeid.IsValid() || oppEdge is null ||
                    !_costing.Allowed(oppEdge.Value, oppTile!, DisallowShortcut))
                {
                    continue;
                }

                // swap in the opposing edge. oppTile is guaranteed non-null here: GetOpposingEdgeId
                // only returns a valid id after successfully resolving oppTile via GetGraphTile.
                edge = oppEdge.Value;
                tile = oppTile!;
                edgeId = oppEdgeid;
            }

            // initialize candidates vector: reset sq_distance to max; apply prefilters
            bool allPrefiltered = true;
            for (int i = begin; i < end; ++i)
            {
                Candidate c = _binCandidates[i - begin];
                ProjectorWrapper p = _pps[i];
                c.SqDistance = double.MaxValue;

                // for traffic closures only one direction may be disabled; check opp too before
                // declaring the whole edge pair filtered for this location
                bool prefiltered = Search.SearchFilterMatch(edge, _costing, tile, edgeId.Id(), p.Location.GetSearchFilter());
                if (prefiltered)
                {
                    GraphTile? oppTile = tile;
                    GraphId oppEdgeid = _reader.GetOpposingEdgeId(edgeId, out DirectedEdge? oppEdge, ref oppTile);
                    prefiltered = oppEdgeid.IsValid() && oppEdge is not null &&
                                  Search.SearchFilterMatch(oppEdge.Value, _costing, oppTile!, oppEdgeid.Id(), p.Location.GetSearchFilter());
                }

                c.Prefiltered = prefiltered;
                allPrefiltered = allPrefiltered && c.Prefiltered;
            }

            // short-circuit if all candidates were prefiltered
            if (allPrefiltered)
            {
                continue;
            }

            // get some shape of the edge
            EdgeInfo edgeInfo = tile.EdgeInfo(edge);
            IReadOnlyList<PointLL> shape = edgeInfo.Shape();

            // iterate along this edge's segments projecting each of the input points
            for (int s = 0; s + 1 < shape.Count; ++s)
            {
                PointLL u = shape[s];
                PointLL v = shape[s + 1];
                for (int i = begin; i < end; ++i)
                {
                    Candidate c = _binCandidates[i - begin];
                    ProjectorWrapper p = _pps[i];

                    // skip prefiltered candidates
                    if (c.Prefiltered)
                    {
                        continue;
                    }

                    // how close is the input to this segment
                    PointLL point = p.Project.Project(u, v);
                    double sqDistance = p.Project.Approx.DistanceSquared(point);
                    if (sqDistance < c.SqDistance)
                    {
                        c.SqDistance = sqDistance;
                        c.Point = point;
                        c.Index = s;
                    }
                }
            }

            // if we already have a better reachable candidate, assume this one is reachable
            DirectedReach reach = CheckReachability(begin, end, tile, edge, edgeId);

            // keep the best point along this edge if it makes sense
            for (int i = begin; i < end; ++i)
            {
                Candidate c = _binCandidates[i - begin];
                ProjectorWrapper p = _pps[i];

                // skip prefiltered candidates
                if (c.Prefiltered)
                {
                    continue;
                }

                GraphTile? edgeTile = tile;
                DirectedEdge edgeForCand = edge;
                GraphId edgeIdForCand = edgeId;
                DirectedReach reachForCand = reach;

                // is this edge reachable in the right way
                bool reachable = reachForCand.Outbound >= p.Location.MinimumOutboundReachability() &&
                                 reachForCand.Inbound >= p.Location.MinimumInboundReachability();

                // it's possible the edge isnt reachable but the opposing is; switch if so
                if (!reachable)
                {
                    GraphTile? oppTile = tile;
                    GraphId oppEdgeid = _reader.GetOpposingEdgeId(edgeId, out DirectedEdge? oppEdge, ref oppTile);
                    if (oppEdgeid.IsValid() && oppEdge is not null &&
                        _costing.Allowed(oppEdge.Value, oppTile!, DisallowShortcut) &&
                        !Search.SearchFilterMatch(oppEdge.Value, _costing, oppTile!, oppEdgeid.Id(), p.Location.GetSearchFilter()))
                    {
                        DirectedReach oppReach = CheckReachability(begin, end, oppTile!, oppEdge.Value, oppEdgeid);
                        if (oppReach.Outbound >= p.Location.MinimumOutboundReachability() &&
                            oppReach.Inbound >= p.Location.MinimumInboundReachability())
                        {
                            edgeTile = oppTile;
                            edgeForCand = oppEdge.Value;
                            edgeIdForCand = oppEdgeid;
                            reachForCand = oppReach;
                            reachable = true;
                        }
                    }
                }

                // which batch of findings will this go into
                List<Candidate> batch = reachable ? p.Reachable : p.Unreachable;

                // if its empty append
                if (batch.Count == 0)
                {
                    c.Edge = edgeForCand;
                    c.EdgeId = edgeIdForCand;
                    c.EdgeInfo = edgeInfo;
                    c.Tile = edgeTile;
                    batch.Add(MoveCandidate(c, i - begin));
                    continue;
                }

                // get some info about possibilities
                bool inRadius = c.SqDistance < p.SqRadius;
                bool better = c.SqDistance < batch[^1].SqDistance;
                bool lastInRadius = batch[^1].SqDistance < p.SqRadius;
                bool closerExternalReachable = reachable && c.SqDistance < p.ClosestExternalReachable;

                // it has to either be better or in the radius to move on
                if (inRadius || better)
                {
                    c.Edge = edgeForCand;
                    c.EdgeId = edgeIdForCand;
                    c.EdgeInfo = edgeInfo;
                    c.Tile = edgeTile;

                    if (!lastInRadius)
                    {
                        // last one wasnt in the radius; replace it because this is better or in radius
                        if (closerExternalReachable)
                        {
                            p.ClosestExternalReachable = batch[^1].SqDistance;
                        }

                        batch[^1] = MoveCandidate(c, i - begin);
                    }
                    else if (better)
                    {
                        // last one is in the radius but this one is better; put it on the end
                        batch.Add(MoveCandidate(c, i - begin));
                    }
                    else
                    {
                        // both in the radius but this one is not as good; insert before the last
                        batch.Add(MoveCandidate(c, i - begin));
                        (batch[^1], batch[^2]) = (batch[^2], batch[^1]);
                    }
                }
                else if (closerExternalReachable)
                {
                    // not in radius or better, but reachable and closer than the closest one outside
                    p.ClosestExternalReachable = c.SqDistance;
                }
            }
        }

        // bin is finished, advance the candidates to their respective next bins
        for (int i = begin; i < end; ++i)
        {
            _pps[i].NextBin(_reader);
        }
    }

    // The C++ std::move(*c_itr) transfers the bin candidate into the batch and leaves a fresh slot
    // behind for the next edge. In C# the Candidate is a reference type; we copy its current state
    // into a new Candidate (so the batch keeps a stable snapshot) and reset the working slot.
    private Candidate MoveCandidate(Candidate c, int slot)
    {
        var moved = new Candidate
        {
            SqDistance = c.SqDistance,
            Point = c.Point,
            Index = c.Index,
            Prefiltered = c.Prefiltered,
            EdgeId = c.EdgeId,
            Edge = c.Edge,
            EdgeInfo = c.EdgeInfo,
            Tile = c.Tile,
        };
        _binCandidates[slot] = new Candidate();
        return moved;
    }

    // ------------------------------------------------------------------
    // find_best_range / search / finalize
    // ------------------------------------------------------------------

    // Faithful port of find_best_range (returns [begin,end) of the greatest run of equal non-empty bins).
    private (int Begin, int End) FindBestRange()
    {
        var best = (Begin: 0, End: 0);
        int curFirst = 0;
        int curSecond = 0;
        while (curSecond != _pps.Count)
        {
            curFirst = curSecond;
            curSecond = curFirst;
            while (curSecond < _pps.Count && _pps[curFirst].HasSameBin(_pps[curSecond]))
            {
                ++curSecond;
            }

            if (_pps[curFirst].HasBin() && (curSecond - curFirst) > (best.End - best.Begin))
            {
                best = (curFirst, curSecond);
            }
        }

        return best;
    }

    // We keep the points sorted at each round such that unfinished ones are at the front.
    // Faithful port of the operator< on projector_wrapper: ones with a null current tile (finished)
    // sort to the END; otherwise sort by bin index.
    private void SortPps()
    {
        _pps.Sort((a, b) =>
        {
            bool aHas = a.HasBin();
            bool bHas = b.HasBin();
            if (aHas != bHas)
            {
                // C++: if (cur_tile != other.cur_tile) return cur_tile.get() > other.cur_tile.get();
                // a non-null tile is "less" than a null tile so finished ones go to the end.
                return aHas ? -1 : 1;
            }

            return a.BinIndex.CompareTo(b.BinIndex);
        });
    }

    // Faithful port of bin_handler_t::search. Named Run (not Search) to avoid colliding with the
    // Loki.Search class whose static filter helpers this method calls.
    public void Run(IReadOnlyList<PathLocation> locations, DynamicCost costing)
    {
        Clear();
        _costing = costing;

        // get the unique set of input locations and the max reachability of them all
        _pps = new List<ProjectorWrapper>(locations.Count);
        _maxReachLimit = 0;
        foreach (PathLocation loc in locations)
        {
            _pps.Add(new ProjectorWrapper(loc, _reader));
            _maxReachLimit = Math.Max(_maxReachLimit, loc.MinimumOutboundReachability());
            _maxReachLimit = Math.Max(_maxReachLimit, loc.MinimumInboundReachability());
        }

        // preallocate one bin candidate per location
        _binCandidates = new List<Candidate>(_pps.Count);
        for (int i = 0; i < _pps.Count; ++i)
        {
            _binCandidates.Add(new Candidate());
        }

        SortPps();
        while (_pps.Count > 0 && _pps[0].HasBin())
        {
            (int begin, int end) = FindBestRange();
            HandleBin(begin, end);
            SortPps();
        }

        FinalizeBins();
    }

    // Faithful port of finalize.
    private void FinalizeBins()
    {
        foreach (ProjectorWrapper pp in _pps)
        {
            // remove non-sensical island candidates
            pp.Unreachable.RemoveAll(c => c.SqDistance > pp.ClosestExternalReachable);

            // concatenate and sort
            pp.Reachable.AddRange(pp.Unreachable);
            pp.Reachable.Sort((a, b) => a.SqDistance.CompareTo(b.SqDistance));

            // dedupe across this location
            _correlatedEdges.Clear();

            foreach (Candidate candidate in pp.Reachable)
            {
                if (candidate.Edge is null || candidate.EdgeInfo is null)
                {
                    continue;
                }

                PointLL ppPt = pp.Location.LatLng;
                IReadOnlyList<PointLL> shape = candidate.EdgeInfo.Shape();
                PointLL frontPt = shape[0];
                PointLL backPt = shape[^1];

                // this may be at a node, either because it was the closest thing or from snap tolerance
                bool front = candidate.Point.Equals(frontPt) ||
                             ppPt.Distance(frontPt) < pp.Location.NodeSnapToleranceMeters;
                bool back = candidate.Point.Equals(backPt) ||
                            ppPt.Distance(backPt) < pp.Location.NodeSnapToleranceMeters;

                bool forward = candidate.Edge.Value.Forward;
                if ((front && forward) || (back && !forward))
                {
                    // it was the begin node
                    GraphTile? otherTile = null;
                    DirectedEdge? opposingEdge = _reader.GetOpposingEdge(candidate.EdgeId, ref otherTile);
                    if (otherTile is null || opposingEdge is null)
                    {
                        // TODO: do an edge snap instead, but you'll only get one direction
                        continue;
                    }

                    CorrelateNode(pp.Location, opposingEdge.Value.EndNode, candidate);
                }
                else if ((back && forward) || (front && !forward))
                {
                    // it was the end node
                    CorrelateNode(pp.Location, candidate.Edge.Value.EndNode, candidate);
                }
                else
                {
                    // it was along the edge
                    CorrelateEdge(pp.Location, candidate);
                }
            }

            // if it was a through/break-through location with a heading, only keep outbound edges
            // (move node-snapped end edges to filtered). Faithful port of the stable_partition step.
            if ((pp.Location.StopType == Location.StopTypeValue.Through ||
                 pp.Location.StopType == Location.StopTypeValue.BreakThrough) &&
                pp.Location.Heading is not null)
            {
                var keep = new List<PathLocation.PathEdge>();
                var moveToFiltered = new List<PathLocation.PathEdge>();
                foreach (PathLocation.PathEdge e in pp.Location.Edges)
                {
                    if (!e.EndNode())
                    {
                        keep.Add(e);
                    }
                    else
                    {
                        moveToFiltered.Add(e);
                    }
                }

                pp.Location.Edges.Clear();
                pp.Location.Edges.AddRange(keep);
                foreach (PathLocation.PathEdge e in moveToFiltered)
                {
                    pp.Location.FilteredEdges.Add((e, PathLocation.EdgeFilterReasonType.None));
                }
            }

            // PORT-NOTE: the C++ also copies edge names onto each correlated PathEdge here
            // (reader.edgeinfo(graph_id).GetNames()). The C# PathLocation.PathEdge does not carry a
            // names field (names are recovered from the edge id when needed), so that copy is omitted.
        }
    }
}
