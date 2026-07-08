// Faithful C# port of Valhalla mjolnir graphenhancer.h + src/mjolnir/graphenhancer.cc @ 3.7.0,
// plus the enhancer helper free functions it relies on from src/mjolnir/util.cc
// (GetOpposingEdgeIndex, shapes_match, ProcessEdgeTransitions, GetStopImpact, IsPencilPointUturn,
// IsCyclewayUturn) and the SpeedAssigner heuristic from src/mjolnir/speed_assigner.h.
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/graphenhancer.h
//   F:/github/valhalla/src/mjolnir/graphenhancer.cc  (1475 LOC)
//   F:/github/valhalla/src/mjolnir/util.cc           (the enhancer-used free functions)
//   F:/github/valhalla/src/mjolnir/speed_assigner.h  (UpdateSpeed heuristic path)
//
// GraphEnhancer is the second mjolnir build stage: it reads the local-level tiles produced by
// GraphBuilder.Build, then "enhances" each node and directed edge in place:
//   First pass  (per node): edge headings + local driveability, link use stats, opposing local idx.
//   Density     : per-tile road-density grid (km/km2 -> relative density 0-15).
//   Second pass (per node/edge): density, country code, "Use::kPedestrian -> kFootway", drop a
//     traffic signal on a oneway-against edge, named flag, speed assignment (heuristic path),
//     name consistency, edge transitions (turn type / edge_to_left/right / stop impact), internal
//     intersection detection, stop/yield sign resolution, turn lane enhancement, not-thru detection,
//     access restriction weight unit conversion (US short ton -> metric); finally node intersection
//     type (dead-end / false) and clearing the temporary stop/yield transition index.
//
// PORT-NOTES / SCOPE (matching the established mjolnir front-end + GraphBuilder port):
//   - The C++ runs `enhance` on a thread pool over a randomized tile queue, deserializing each tile
//     into a GraphTileBuilder, mutating node_builder()/directededge_builder(), and StoreTileData()
//     back to the tile_dir. This port is single-threaded and operates on in-memory tile blobs (the
//     GraphBuilder.Build output, or a GraphReader tile_dir), reserializing each enhanced tile to a
//     byte-compatible blob. Every algorithm is preserved exactly.
//   - The admin SQLite DB / country-access overrides (AdminDB / GetCountryAccess / SetCountryAccess /
//     motorroad defaults) are EXCLUDED (no admin db in this on-device build, exactly as GraphBuilder
//     left admin_index == 0 and drive-on-right per-way). The `apply_country_overrides` branch is
//     therefore inert here (country_code is read from the tile admin record, which is the "None"
//     admin at index 0 -> empty iso code), matching the GraphBuilder output.
//   - SpeedAssigner config-driven speeds (FromConfig / default_speeds_config json) are EXCLUDED; the
//     heuristic UpdateSpeed path is ported (this is the default when no speed config is supplied).
//   - The access.bin (OSMAccess sequence) lookup used by the country-override branch is EXCLUDED
//     along with that branch.
//   - Unreachable-edge detection (#ifdef UNREACHABLE) is dead code in C++ and is not ported.
//   - StreetNamesFactory (ported) drives ConsistentNames exactly.

using System;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Enhances graph tile information at the local level. Faithful port of the C++
/// <c>class GraphEnhancer</c> (plus the enhancer-used free functions from <c>util.cc</c> and the
/// <c>SpeedAssigner</c> heuristic). See the file header for scope/omissions.
/// </summary>
public sealed class GraphEnhancer
{
    // Number of tries when determining not thru edges.
    private const uint MaxNoThruTries = 256;

    // Radius (km) to use for density.
    private const float DensityRadius = 2.0f;
    private static readonly float DensityLatDeg =
        (DensityRadius * Constants.MetersPerKm) / (float)Constants.MetersPerDegreeLat;

    // Temporary stop/yield bit flags stored by GraphBuilder in the node transition index.
    private const uint Minor = 1;
    private const uint StopSign = 2;
    private const uint YieldSign = 4;

    /// <summary>
    /// Statistics gathered during enhancement. Faithful port of the C++ <c>enhancer_stats</c>.
    /// </summary>
    public sealed class EnhancerStats
    {
        /// <summary>Maximum density (km/km^2) seen across all tiles.</summary>
        public float MaxDensity { get; set; } = float.MinValue;

        /// <summary>Number of edges marked not-thru.</summary>
        public uint NotThru { get; set; }

        /// <summary>Number of nodes with no admin/country found.</summary>
        public uint NoCountryFound { get; set; }

        /// <summary>Number of edges marked internal to an intersection.</summary>
        public uint InternalCount { get; set; }

        /// <summary>Number of turn channel edges.</summary>
        public uint TurnChannelCount { get; set; }

        /// <summary>Number of ramp edges.</summary>
        public uint RampCount { get; set; }

        /// <summary>Number of pencil-point u-turns.</summary>
        public uint PencilUCount { get; set; }

        /// <summary>Histogram of node densities (0-15).</summary>
        public uint[] DensityCounts { get; } = new uint[16];
    }

    private readonly EnhancerStats _stats = new();

    /// <summary>The statistics collected during the last <see cref="Enhance(IDictionary{GraphId, byte[]})"/> call.</summary>
    public EnhancerStats Stats => _stats;

    /// <summary>
    /// Enhances the local level graph tiles held in memory, returning the enhanced tile blobs. Each
    /// tile is parsed, enhanced in place, and reserialized to a byte-compatible blob. Faithful port
    /// of <c>GraphEnhancer::Enhance</c> driving the single-threaded <c>enhance</c> loop.
    /// </summary>
    /// <param name="tiles">Map of tile GraphId (tile base) to the serialized tile blob bytes.</param>
    /// <param name="inferInternalIntersections">Whether to infer internal intersections (default true).</param>
    /// <param name="inferTurnChannels">Whether turn channels are being inferred (default true).</param>
    /// <returns>A map of tile GraphId (tile base) to the enhanced tile blob bytes.</returns>
    public Dictionary<GraphId, byte[]> Enhance(
        IDictionary<GraphId, byte[]> tiles,
        bool inferInternalIntersections = true,
        bool inferTurnChannels = true)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        var reader = new InMemoryTileSource(tiles);
        var result = new Dictionary<GraphId, byte[]>(tiles.Count);
        foreach (KeyValuePair<GraphId, byte[]> kv in tiles)
        {
            byte[]? enhanced = EnhanceTile(kv.Key.TileBase(), reader, inferInternalIntersections, inferTurnChannels);
            result[kv.Key.TileBase()] = enhanced ?? kv.Value;
        }

        return result;
    }

    /// <summary>
    /// Provides read access to tiles (current and neighboring) during enhancement. Mirrors the role
    /// of the C++ local <c>GraphReader reader</c> within <c>enhance</c>.
    /// </summary>
    private interface ITileSource
    {
        TileModel? GetTile(GraphId tileId);
    }

    // Tile source over an in-memory blob dictionary. Caches parsed TileModels so neighbor lookups
    // re-use the same parsed object (matching the C++ reader cache).
    private sealed class InMemoryTileSource : ITileSource
    {
        private readonly IDictionary<GraphId, byte[]> _blobs;
        private readonly Dictionary<ulong, TileModel?> _cache = new();

        public InMemoryTileSource(IDictionary<GraphId, byte[]> blobs) => _blobs = blobs;

        public TileModel? GetTile(GraphId tileId)
        {
            GraphId @base = tileId.TileBase();
            if (_cache.TryGetValue(@base.Value, out TileModel? cached))
            {
                return cached;
            }

            TileModel? model = _blobs.TryGetValue(@base, out byte[]? blob) ? new TileModel(@base, blob) : null;
            _cache[@base.Value] = model;
            return model;
        }
    }

    // ------------------------------------------------------------------
    // enhance(): per-tile enhancement
    // ------------------------------------------------------------------

    private byte[]? EnhanceTile(
        GraphId tileId, ITileSource reader, bool inferInternalIntersections, bool inferTurnChannels)
    {
        TileModel? tileOpt = reader.GetTile(tileId);
        if (tileOpt is null || tileOpt.NodeCount == 0)
        {
            // Empty tiles are skipped (added where ways go through a tile but no end node is within).
            return null;
        }

        TileModel tile = tileOpt;
        byte level = TileHierarchy.Levels()[^1].Level;
        uint id = tileId.Tileid();

        uint arBefore = tile.AccessRestrictionCountBefore;
        var accessRestrictions = new List<AccessRestriction>();

        uint tlBefore = tile.TurnLaneCountBefore;
        var turnLanes = new List<TurnLanes>();

        // First pass - set headings + local driveability and opposing local index.
        for (int i = 0; i < tile.NodeCount; i++)
        {
            var startnode = new GraphId(id, level, (uint)i);
            ref NodeInfo nodeinfo = ref tile.NodeRef(i);

            uint count = nodeinfo.EdgeCount;
            uint ntrans = Math.Min(count, GraphConstants.NumberOfEdgeTransitions);
            if (ntrans == 0)
            {
                throw new InvalidOperationException("edge transitions set is empty");
            }

            // Headings first so that internal-edge checks during turn-lane processing can use them.
            var heading = new uint[ntrans];
            nodeinfo.SetLocalEdgeCount(ntrans);
            for (uint j = 0; j < ntrans; j++)
            {
                ref DirectedEdge directededge = ref tile.DirectedEdgeRef((int)(nodeinfo.EdgeIndex + j));

                List<PointLL> shape = tile.EdgeShape(directededge);
                if (!directededge.Forward)
                {
                    shape.Reverse();
                }

                heading[j] = (uint)Math.Round(
                    PointLL.HeadingAlongPolyline(
                        shape,
                        GraphConstants.GetOffsetForHeading(directededge.Classification, directededge.Use)),
                    MidpointRounding.AwayFromZero);

                nodeinfo.SetHeading(j, heading[j]);

                // Set traversability for autos.
                Traversability traversability;
                if ((directededge.ForwardAccess & GraphConstants.AutoAccess) != 0)
                {
                    traversability = (directededge.ReverseAccess & GraphConstants.AutoAccess) != 0
                        ? Traversability.Both
                        : Traversability.Forward;
                }
                else
                {
                    traversability = (directededge.ReverseAccess & GraphConstants.AutoAccess) != 0
                        ? Traversability.Backward
                        : Traversability.None;
                }

                nodeinfo.SetLocalDriveability(j, traversability);
            }

            for (uint j = 0; j < nodeinfo.EdgeCount; j++)
            {
                ref DirectedEdge directededge = ref tile.DirectedEdgeRef((int)(nodeinfo.EdgeIndex + j));

                // Get the tile at the end node.
                TileModel endnodetile = directededge.EndNode.TileBase() == tile.Id
                    ? tile
                    : reader.GetTile(directededge.EndNode)!;

                if (directededge.Use == Use.TurnChannel)
                {
                    _stats.TurnChannelCount++;
                }
                else if (directededge.Use == Use.Ramp)
                {
                    _stats.RampCount++;
                }

                // Set the opposing index on the local level.
                directededge.SetOppLocalIdx(GetOpposingEdgeIndex(endnodetile, startnode, tile, directededge));
            }
        }

        // Density index (urban tag not used in this build, so always compute).
        DensityIndex densityIndex = BuildDensityIndex(reader, tile, level);

        // Second pass - enhance node and edge attributes.
        PointLL baseLl = tile.BaseLl;
        for (int i = 0; i < tile.NodeCount; i++)
        {
            var startnode = new GraphId(id, level, (uint)i);
            ref NodeInfo nodeinfo = ref tile.NodeRef(i);

            uint density = 0;
            if (densityIndex.TryGet(nodeinfo.LatLng(baseLl), out uint dv))
            {
                density = dv;
                _stats.DensityCounts[density]++;
                nodeinfo.SetDensity(density);
            }

            uint adminIndex = nodeinfo.AdminIndex;
            string countryCode = string.Empty;
            if (adminIndex != 0)
            {
                countryCode = tile.AdminCountryIso((int)adminIndex);
            }
            else
            {
                _stats.NoCountryFound++;
            }

            // Snapshot of the node's edges (for edge transitions / pencil-point lookups). The C++
            // captures `const DirectedEdge* edges = directededges(edge_index)` BEFORE the per-edge
            // mutation loop, so name_consistency / use / access seen by ProcessEdgeTransitions are the
            // values as each edge is reached. We thread the live tile edge array instead (the same
            // backing store the builder mutates), reproducing the in-place semantics exactly.
            uint edgeIndex = nodeinfo.EdgeIndex;
            uint edgeCount = nodeinfo.EdgeCount;

            uint drivableCount = 0;
            for (uint j = 0; j < edgeCount; j++)
            {
                ref DirectedEdge directededge = ref tile.DirectedEdgeRef((int)(edgeIndex + j));

                // PORT-NOTE: country-override branch excluded (no admin db); end-node admin is the
                // "None" admin with empty iso codes (matching GraphBuilder's admin_index == 0).
                string endNodeCode = string.Empty;
                string endNodeStateCode = string.Empty;
                {
                    TileModel endnodetile = directededge.EndNode.TileBase() == tile.Id
                        ? tile
                        : reader.GetTile(directededge.EndNode)!;
                    uint endAdminIndex = endnodetile.NodeRef((int)directededge.EndNode.Id()).AdminIndex;
                    endNodeCode = endnodetile.AdminCountryIso((int)endAdminIndex);
                    endNodeStateCode = endnodetile.AdminStateIso((int)endAdminIndex);
                }

                // Update drivable count (do this after country access logic).
                if ((directededge.ForwardAccess & GraphConstants.AutoAccess) != 0 ||
                    (directededge.ReverseAccess & GraphConstants.AutoAccess) != 0)
                {
                    drivableCount++;
                }

                // Use::kPedestrian is really a kFootway.
                if (directededge.Use == Use.Pedestrian)
                {
                    directededge.SetUse(Use.Footway);
                }

                if (directededge.TrafficSignal && (directededge.ForwardAccess & GraphConstants.AutoAccess) == 0)
                {
                    // oneway edge: no need for a traffic signal on the edge.
                    directededge.SetTrafficSignal(false);
                }

                // Update the named flag.
                EdgeInfo eOffset = tile.EdgeInfoFor(directededge);
                List<(string Name, bool IsRouteNum)> names = eOffset.GetNames(false);
                directededge.SetNamed(names.Count > 0);

                // Speed assignment (heuristic path).
                UpdateSpeed(ref directededge, density, inferTurnChannels);

                // Name continuity - on the directed edge.
                uint ntrans = nodeinfo.LocalEdgeCount;
                for (uint k = 0; k < ntrans; k++)
                {
                    DirectedEdge fromedge = tile.DirectedEdgeRef((int)(nodeinfo.EdgeIndex + k));
                    if (ConsistentNames(countryCode, names, tile.EdgeInfoFor(fromedge).GetNames(false)))
                    {
                        directededge.SetNameConsistency(k, true);
                    }
                }

                // Set edge transitions.
                if (j < GraphConstants.NumberOfEdgeTransitions)
                {
                    ProcessEdgeTransitions(j, ref directededge, tile, edgeIndex, ntrans, nodeinfo, _stats);
                }

                // Test if an internal intersection edge (must be after setting opposing edge index).
                if (inferInternalIntersections &&
                    IsIntersectionInternal(tile, reader, nodeinfo, directededge, j))
                {
                    directededge.SetInternal(true);
                }

                if (directededge.Internal)
                {
                    _stats.InternalCount++;
                }

                SetStopYieldSignInfo(tile, reader, nodeinfo, ref directededge);

                // Enhance and add turn lanes.
                if (directededge.TurnLanes)
                {
                    UpdateTurnLanes(tile, edgeIndex + j, ref directededge, reader, turnLanes);
                }

                // Check for not_thru edge (only on low importance edges).
                if (directededge.Classification > RoadClass.Tertiary)
                {
                    if (IsNotThruEdge(reader, tile, startnode, directededge))
                    {
                        directededge.SetNotThru(true);
                        _stats.NotThru++;
                    }
                }

                // Update access restrictions (update weight units).
                if (directededge.AccessRestriction != 0)
                {
                    (IReadOnlyList<AccessRestriction> span, _) = tile.GetAccessRestrictions(edgeIndex + j);
                    var restrictions = new List<AccessRestriction>(span);

                    // Convert any US weight values from short ton (U.S. customary) to metric.
                    if (countryCode == "US" || countryCode == "MM" || countryCode == "LR")
                    {
                        for (int r = 0; r < restrictions.Count; r++)
                        {
                            AccessRestriction res = restrictions[r];
                            if (res.Type() == AccessType.MaxWeight || res.Type() == AccessType.MaxAxleLoad)
                            {
                                res.SetValue((ulong)Math.Round(res.Value() * Constants.TonsShortToMetric));
                                restrictions[r] = res;
                            }
                        }
                    }

                    accessRestrictions.AddRange(restrictions);
                }
            }

            // Set the intersection type to false or dead-end (do not override gates / toll booths /
            // toll gantry / sump buster).
            if (nodeinfo.Type != NodeType.Gate && nodeinfo.Type != NodeType.TollBooth &&
                nodeinfo.Type != NodeType.TollGantry && nodeinfo.Type != NodeType.SumpBuster)
            {
                if (drivableCount == 1)
                {
                    nodeinfo.SetIntersection(IntersectionType.DeadEnd);
                }
                else if (nodeinfo.EdgeCount == 2)
                {
                    nodeinfo.SetIntersection(IntersectionType.False);
                }
            }

            nodeinfo.SetTransitionIndex(0);
        }

        _ = arBefore;
        _ = tlBefore;

        // Replace access restrictions and turn lanes, then reserialize the tile.
        tile.SetAccessRestrictions(accessRestrictions);
        tile.SetTurnLanes(turnLanes);

        return tile.Serialize();
    }

    // ------------------------------------------------------------------
    // GetOpposingEdgeIndex / shapes_match (src/mjolnir/util.cc)
    // ------------------------------------------------------------------

    private static uint GetOpposingEdgeIndex(
        TileModel endnodetile, GraphId startnode, TileModel tile, DirectedEdge edge)
    {
        NodeInfo nodeinfo = endnodetile.NodeRef((int)edge.EndNode.Id());

        uint edgeIndex = nodeinfo.EdgeIndex;
        for (uint i = 0; i < nodeinfo.EdgeCount; i++)
        {
            DirectedEdge directededge = endnodetile.DirectedEdgeRef((int)(edgeIndex + i));
            if (directededge.EndNode == startnode && directededge.Length == edge.Length)
            {
                // If in the same tile and edgeinfo offset matches then shape and names match.
                if (ReferenceEquals(endnodetile, tile) &&
                    directededge.EdgeInfoOffset == edge.EdgeInfoOffset)
                {
                    return i;
                }

                // Compare shape if not in the same tile or different EdgeInfo.
                if (ShapesMatch(tile.EdgeShape(edge), endnodetile.EdgeShape(directededge)))
                {
                    return i;
                }
            }
        }

        // LOG_ERROR("Could not find opposing edge index").
        return GraphConstants.MaxEdgesPerNode;
    }

    private static bool ShapesMatch(IReadOnlyList<PointLL> shape1, IReadOnlyList<PointLL> shape2)
    {
        if (shape1.Count != shape2.Count)
        {
            return false;
        }

        if (PointEquals(shape1[0], shape2[0]))
        {
            for (int i = 0; i < shape1.Count; i++)
            {
                if (!PointEquals(shape1[i], shape2[i]))
                {
                    return false;
                }
            }

            return true;
        }

        if (PointEquals(shape1[0], shape2[^1]))
        {
            for (int i = 0; i < shape1.Count; i++)
            {
                if (!PointEquals(shape1[i], shape2[shape2.Count - 1 - i]))
                {
                    return false;
                }
            }

            return true;
        }

        // LOG_WARN("Neither end of the shape matches").
        return false;
    }

    // ------------------------------------------------------------------
    // ProcessEdgeTransitions / GetStopImpact / IsPencilPointUturn / IsCyclewayUturn
    // ------------------------------------------------------------------

    private static void ProcessEdgeTransitions(
        uint idx,
        ref DirectedEdge directededge,
        TileModel tile,
        uint edgeIndex,
        uint ntrans,
        NodeInfo nodeinfo,
        EnhancerStats stats)
    {
        for (uint i = 0; i < ntrans; i++)
        {
            // Reverse the heading of the from directed edge (it is incoming).
            uint fromHeading = (nodeinfo.Heading(i) + 180) % 360;
            uint turnDegree = Midgard.Util.GetTurnDegree(fromHeading, nodeinfo.Heading(idx));
            directededge.SetTurnType(i, Turn.GetType(turnDegree));

            // edge_to_left / edge_to_right flags.
            uint rightCount = 0;
            uint leftCount = 0;
            if (ntrans > 2)
            {
                for (uint j = 0; j < ntrans; ++j)
                {
                    // Skip the from and to edges; also skip roads under construction.
                    if (j == i || j == idx ||
                        tile.DirectedEdgeRef((int)(edgeIndex + j)).Use == Use.Construction)
                    {
                        continue;
                    }

                    uint degree = Midgard.Util.GetTurnDegree(fromHeading, nodeinfo.Heading(j));
                    if (turnDegree > 180)
                    {
                        if (degree > turnDegree || degree < 180)
                        {
                            ++rightCount;
                        }
                        else if (degree < turnDegree && degree > 180)
                        {
                            ++leftCount;
                        }
                    }
                    else
                    {
                        if (degree > turnDegree && degree < 180)
                        {
                            ++rightCount;
                        }
                        else if (degree < turnDegree || degree > 180)
                        {
                            ++leftCount;
                        }
                    }
                }
            }

            directededge.SetEdgeToLeft(i, leftCount > 0);
            directededge.SetEdgeToRight(i, rightCount > 0);

            // Stop impact (uses the right/left edges so must come after the right/left edge logic).
            uint stopimpact = GetStopImpact(i, idx, directededge, tile, edgeIndex, ntrans, nodeinfo, turnDegree, stats);
            directededge.SetStopImpact(i, stopimpact);
        }
    }

    private static bool IsPencilPointUturn(
        uint fromIndex,
        uint toIndex,
        DirectedEdge directededge,
        TileModel tile,
        uint edgeIndex,
        NodeInfo nodeInfo,
        uint turnDegree)
    {
        DirectedEdge from = tile.DirectedEdgeRef((int)(edgeIndex + fromIndex));
        DirectedEdge to = tile.DirectedEdgeRef((int)(edgeIndex + toIndex));

        if (nodeInfo.DriveOnRight)
        {
            // Left pencil point u-turn.
            if (((turnDegree > 179 && turnDegree < 211) ||
                 ((from.Length < 50 || directededge.Length < 50) &&
                  turnDegree > 179 && turnDegree < 226)) &&
                (from.ForwardAccess & GraphConstants.AutoAccess) == 0 &&
                (from.ReverseAccess & GraphConstants.AutoAccess) != 0 &&
                (directededge.ForwardAccess & GraphConstants.AutoAccess) != 0 &&
                (directededge.ReverseAccess & GraphConstants.AutoAccess) == 0 &&
                directededge.EdgeToRight(fromIndex) && !directededge.EdgeToLeft(fromIndex) &&
                to.NameConsistencyAt(fromIndex))
            {
                return true;
            }
        }
        else
        {
            // Right pencil point u-turn.
            if (((turnDegree > 149 && turnDegree < 181) ||
                 ((from.Length < 50 || directededge.Length < 50) &&
                  turnDegree > 134 && turnDegree < 181)) &&
                (from.ForwardAccess & GraphConstants.AutoAccess) == 0 &&
                (from.ReverseAccess & GraphConstants.AutoAccess) != 0 &&
                (directededge.ForwardAccess & GraphConstants.AutoAccess) != 0 &&
                (directededge.ReverseAccess & GraphConstants.AutoAccess) == 0 &&
                !directededge.EdgeToRight(fromIndex) && directededge.EdgeToLeft(fromIndex) &&
                to.NameConsistencyAt(fromIndex))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCyclewayUturn(
        uint fromIndex,
        uint toIndex,
        DirectedEdge directededge,
        TileModel tile,
        uint edgeIndex,
        NodeInfo nodeInfo,
        uint turnDegree)
    {
        DirectedEdge from = tile.DirectedEdgeRef((int)(edgeIndex + fromIndex));
        DirectedEdge to = tile.DirectedEdgeRef((int)(edgeIndex + toIndex));

        // We only deal with cycleways.
        if (from.Use != Use.Cycleway || to.Use != Use.Cycleway)
        {
            return false;
        }

        if (nodeInfo.DriveOnRight)
        {
            if (((turnDegree > 179 && turnDegree < 211) ||
                 ((from.Length < 50 || directededge.Length < 50) &&
                  turnDegree > 179 && turnDegree < 226)) &&
                directededge.EdgeToRight(fromIndex) && directededge.EdgeToLeft(fromIndex))
            {
                return true;
            }
        }
        else
        {
            if (((turnDegree > 149 && turnDegree < 181) ||
                 ((from.Length < 50 || directededge.Length < 50) &&
                  turnDegree > 134 && turnDegree < 181)) &&
                directededge.EdgeToRight(fromIndex) && directededge.EdgeToLeft(fromIndex))
            {
                return true;
            }
        }

        return false;
    }

    private static uint GetStopImpact(
        uint from,
        uint to,
        DirectedEdge directededge,
        TileModel tile,
        uint edgeIndex,
        uint count,
        NodeInfo nodeinfo,
        uint turnDegree,
        EnhancerStats stats)
    {
        DirectedEdge Edges(uint k) => tile.DirectedEdgeRef((int)(edgeIndex + k));

        // Handle roundabouts.
        if (Edges(from).Roundabout && Edges(to).Roundabout)
        {
            return 0;
        }

        // Handle pencil point u-turn.
        if (IsPencilPointUturn(from, to, directededge, tile, edgeIndex, nodeinfo, turnDegree))
        {
            stats.PencilUCount++;
            return 7;
        }

        // Handle cycleway u-turn.
        if (IsCyclewayUturn(from, to, directededge, tile, edgeIndex, nodeinfo, turnDegree))
        {
            return 7;
        }

        // Get the highest classification of other roads at the intersection.
        bool allRamps = true;
        bool foundOtherEdge = false;
        RoadClass bestrc = RoadClass.Unclassified;
        for (uint i = 0; i < count; i++)
        {
            DirectedEdge edge = Edges(i);

            // Check the road if drivable TO the intersection and not the to/from edge.
            if (i != to && i != from && (edge.ReverseAccess & GraphConstants.AutoAccess) != 0)
            {
                if (edge.Roundabout)
                {
                    uint c = (uint)edge.Classification + 2;
                    if (c < (uint)bestrc)
                    {
                        bestrc = (RoadClass)c;
                    }
                }
                else if (edge.Classification < bestrc)
                {
                    bestrc = edge.Classification;
                }
            }

            // Track whether any other drivable edge exists (in either direction).
            if (i != to && i != from &&
                ((edge.ReverseAccess | edge.ForwardAccess) & GraphConstants.AutoAccess) != 0)
            {
                foundOtherEdge = true;
            }

            if (!edge.Link)
            {
                allRamps = false;
            }
        }

        // No other drivable edge means this is not a real intersection. Don't apply to U-turns.
        if (!foundOtherEdge && from != to)
        {
            return 0;
        }

        // kUnclassified, kResidential, kServiceOther are grouped for the stop_impact logic.
        RoadClass fromRc = Edges(from).Classification;
        if (fromRc > RoadClass.Unclassified)
        {
            fromRc = RoadClass.Unclassified;
        }

        // High stop impact from a turn channel onto a turn channel unless the other edge is low class.
        if (Edges(from).Use == Use.TurnChannel && Edges(to).Use == Use.TurnChannel &&
            bestrc < RoadClass.Unclassified)
        {
            return 7;
        }

        // Set stop impact to the difference in road class (non-negative).
        int impact = (int)fromRc - (int)bestrc;
        uint stopImpact = impact < -3 ? 0u : (uint)(impact + 3);

        Turn.Type turnType = Turn.GetType(turnDegree);
        bool isSharp = turnType is Turn.Type.SharpLeft or Turn.Type.SharpRight or Turn.Type.Reverse;
        bool isSlight = turnType is Turn.Type.Straight or Turn.Type.SlightRight or Turn.Type.SlightLeft;
        if (allRamps)
        {
            if (isSharp)
            {
                stopImpact += 2;
            }
            else if (isSlight)
            {
                stopImpact /= 2;
            }
            else if (stopImpact != 0)
            {
                stopImpact -= 1;
            }
        }
        else if (Edges(from).Use == Use.Ramp && Edges(to).Use == Use.Ramp &&
                 bestrc < RoadClass.Unclassified)
        {
            // Ramp may be crossing a road (not a path or service road).
            if (nodeinfo.TrafficSignal || Edges(from).TrafficSignal || Edges(from).StopSign)
            {
                stopImpact = 4;
            }
            else if (count > 3)
            {
                stopImpact += 2;
            }
        }
        else if (Edges(from).Use == Use.Ramp && Edges(to).Use != Use.Ramp &&
                 !Edges(from).Internal && !Edges(to).Internal)
        {
            // Increase stop impact on merge.
            if (isSharp)
            {
                stopImpact += 3;
            }
            else if (isSlight)
            {
                stopImpact += 1;
            }
            else
            {
                stopImpact += 2;
            }
        }
        else if (Edges(from).Use == Use.TurnChannel)
        {
            // Penalize sharp turns.
            if (isSharp)
            {
                stopImpact += 2;
            }
            else if (Edges(to).Use == Use.Ramp)
            {
                stopImpact += 1;
            }
            else if (isSlight)
            {
                stopImpact /= 2;
            }
            else if (stopImpact != 0)
            {
                stopImpact -= 1;
            }
        }
        else if (Edges(from).Use == Use.ParkingAisle && Edges(to).Use == Use.ParkingAisle)
        {
            // Decrease stop impact inside parking lots.
            if (stopImpact != 0)
            {
                stopImpact -= 1;
            }
        }
        else if (nodeinfo.DriveOnRight &&
                 (turnType == Turn.Type.SharpLeft || turnType == Turn.Type.Left) &&
                 fromRc != Edges(to).Classification && Edges(to).Use != Use.Ramp &&
                 Edges(to).Use != Use.TurnChannel)
        {
            // Penalize lefts when driving on the right.
            if (nodeinfo.TrafficSignal || Edges(from).TrafficSignal || Edges(from).StopSign)
            {
                stopImpact += 2;
            }
            else if (Math.Abs((int)fromRc - (int)Edges(to).Classification) > 1)
            {
                stopImpact++;
            }
        }
        else if (!nodeinfo.DriveOnRight &&
                 (turnType == Turn.Type.SharpRight || turnType == Turn.Type.Right) &&
                 fromRc != Edges(to).Classification && Edges(to).Use != Use.Ramp &&
                 Edges(to).Use != Use.TurnChannel)
        {
            // Penalize rights when driving on the left.
            if (nodeinfo.TrafficSignal || Edges(from).TrafficSignal || Edges(from).StopSign)
            {
                stopImpact += 2;
            }
            else if (Math.Abs((int)fromRc - (int)Edges(to).Classification) > 1)
            {
                stopImpact++;
            }
        }

        return stopImpact <= GraphConstants.MaxStopImpact ? stopImpact : GraphConstants.MaxStopImpact;
    }

    // ------------------------------------------------------------------
    // SpeedAssigner heuristic path (src/mjolnir/speed_assigner.h UpdateSpeed)
    // ------------------------------------------------------------------

    private const float TurnChannelFactor = 1.25f;
    private const float RampDensityFactor = 0.8f;
    private const float RampFactor = 0.85f;
    private const float RoundaboutFactor = 0.5f;
    private const uint MaxRuralDensity = 8;

    private static readonly uint[] UrbanRcSpeed =
    {
        89, // motorway
        73, // trunk
        57, // primary
        49, // secondary
        40, // tertiary
        35, // unclassified
        30, // residential
        20, // service/other
    };

    private static void UpdateSpeed(ref DirectedEdge directededge, uint density, bool inferTurnChannels)
    {
        // PORT-NOTE: FromConfig (json default-speeds) is excluded; heuristic path only.

        // Update speed on ramps (if not a tagged speed) and turn channels.
        if (directededge.Link)
        {
            uint speed = directededge.Speed;
            Use use = directededge.Use;
            if (use == Use.TurnChannel && inferTurnChannels)
            {
                speed = (uint)((speed * TurnChannelFactor) + 0.5f);
            }
            else if (use == Use.Ramp && directededge.SpeedType != SpeedType.Tagged)
            {
                RoadClass rc = directededge.Classification;
                if (rc == RoadClass.Motorway || rc == RoadClass.Trunk || rc == RoadClass.Primary)
                {
                    speed = density > MaxRuralDensity
                        ? (uint)((speed * RampDensityFactor) + 0.5f)
                        : (uint)((speed * RampFactor) + 0.5f);
                }
                else
                {
                    speed = (uint)((speed * RampFactor) + 0.5f);
                }
            }

            directededge.SetSpeed(speed);
            return;
        }

        // If speed is assigned from an OSM max_speed tag, only update based on surface type.
        if (directededge.SpeedType == SpeedType.Tagged)
        {
            if (directededge.Surface >= Surface.PavedRough)
            {
                uint speed = directededge.Speed;
                if (speed >= 50)
                {
                    directededge.SetSpeed(speed - 10);
                }
                else if (speed > 15)
                {
                    directededge.SetSpeed(speed - 5);
                }
            }

            return;
        }

        // Set speed on ferries based on length.
        if (directededge.Use == Use.RailFerry)
        {
            directededge.SetSpeed(65); // 40 MPH
            return;
        }

        if (directededge.Use == Use.Ferry)
        {
            // If duration flag is set (leaves_tile temporary) do nothing.
            if (directededge.LeavesTile)
            {
                return;
            }

            if (directededge.Length < 2000)
            {
                directededge.SetSpeed(10); // 5 knots
            }
            else if (directededge.Length < 8000)
            {
                directededge.SetSpeed(20); // 10 knots
            }
            else
            {
                directededge.SetSpeed(30); // 15 knots
            }

            return;
        }

        // Modify speed for roads in urban regions.
        if (density > MaxRuralDensity)
        {
            uint rc = (uint)directededge.Classification;
            directededge.SetSpeed(UrbanRcSpeed[rc]);
        }

        if (directededge.Roundabout)
        {
            uint speed = directededge.Speed;
            directededge.SetSpeed((uint)((speed * RoundaboutFactor) + 0.5f));
        }

        // Reduce speeds on parking aisles, driveways, and drive-thrus.
        if (directededge.Use == Use.ParkingAisle)
        {
            directededge.SetSpeed(GraphConstants.ParkingAisleSpeed);
        }
        else if (directededge.Use == Use.Driveway)
        {
            directededge.SetSpeed(GraphConstants.DrivewaySpeed);
        }
        else if (directededge.Use == Use.DriveThru)
        {
            directededge.SetSpeed(GraphConstants.DriveThruSpeed);
        }

        // Modify speed based on surface.
        if (directededge.Surface >= Surface.PavedRough)
        {
            uint speed = directededge.Speed;
            directededge.SetSpeed(speed / 2);
        }
    }

    // ------------------------------------------------------------------
    // IsNotThruEdge
    // ------------------------------------------------------------------

    private static bool IsNotThruEdge(
        ITileSource reader, TileModel startTile, GraphId startnode, DirectedEdge directededge)
    {
        var visitedset = new HashSet<ulong>();
        var expandset = new List<GraphId>();
        int expandPos = 0;
        expandset.Add(directededge.EndNode);

        TileModel? tile = null;
        for (uint n = 0; n < MaxNoThruTries; n++)
        {
            // If expand list is exhausted this is "not thru".
            if (expandPos == expandset.Count)
            {
                return true;
            }

            GraphId expandnode = expandset[expandPos++];
            visitedset.Add(expandnode.Value);
            if (tile is null || tile.Id != expandnode.TileBase())
            {
                tile = expandnode.TileBase() == startTile.Id ? startTile : reader.GetTile(expandnode);
            }

            if (tile is null)
            {
                continue;
            }

            NodeInfo nodeinfo = tile.NodeRef((int)expandnode.Id());
            uint edgeIndex = nodeinfo.EdgeIndex;
            for (uint i = 0; i < nodeinfo.EdgeCount; i++)
            {
                DirectedEdge diredge = tile.DirectedEdgeRef((int)(edgeIndex + i));

                // Do not allow use of the opposing start edge (check more than just endnode).
                if (n == 0 && diredge.EndNode == startnode &&
                    diredge.ForwardAccess == directededge.ReverseAccess &&
                    diredge.ReverseAccess == directededge.ForwardAccess &&
                    diredge.Length == directededge.Length)
                {
                    if (startnode.Tileid() == expandnode.Tileid() &&
                        diredge.EdgeInfoOffset == directededge.EdgeInfoOffset)
                    {
                        continue;
                    }
                }

                // Return false if we get back to the start node or hit a higher classification.
                if (diredge.Classification < RoadClass.Tertiary || diredge.EndNode == startnode)
                {
                    return false;
                }

                // Add the end node to expand set if not already visited.
                if (!visitedset.Contains(diredge.EndNode.Value))
                {
                    expandset.Add(diredge.EndNode);
                }
            }
        }

        return false;
    }

    // ------------------------------------------------------------------
    // SetStopYieldSignInfo
    // ------------------------------------------------------------------

    private static void SetStopYieldSignInfo(
        TileModel startTile, ITileSource reader, NodeInfo startnodeinfo, ref DirectedEdge directededge)
    {
        if (directededge.StopSign || directededge.YieldSign)
        {
            uint ntrans = startnodeinfo.LocalEdgeCount;
            for (uint k = 0; k < ntrans; k++)
            {
                DirectedEdge fromedge = startTile.DirectedEdgeRef((int)(startnodeinfo.EdgeIndex + k));
                // The temporarily set deadend flag indicates whether the stop/yield is at minor roads.
                if (directededge.Deadend)
                {
                    if (fromedge.Classification > directededge.Classification ||
                        (fromedge.Classification == directededge.Classification &&
                         (fromedge.Use == Use.Ramp || fromedge.Use == Use.TurnChannel)))
                    {
                        directededge.SetStopSign(false);
                        directededge.SetYieldSign(false);
                        directededge.SetDeadend(false);
                        return;
                    }
                }
            }

            if ((directededge.ForwardAccess & GraphConstants.AutoAccess) == 0)
            {
                directededge.SetStopSign(false);
                directededge.SetYieldSign(false);
                directededge.SetDeadend(false);
                return;
            }
        }

        // Get the tile at the end node.
        TileModel tile = directededge.EndNode.TileBase() == startTile.Id
            ? startTile
            : reader.GetTile(directededge.EndNode)!;
        NodeInfo nodeinfo = tile.NodeRef((int)directededge.EndNode.Id());
        if (nodeinfo.TransitionIndex != 0)
        {
            bool minor = (nodeinfo.TransitionIndex & Minor) != 0;
            bool stop = (nodeinfo.TransitionIndex & StopSign) != 0;
            bool yield = (nodeinfo.TransitionIndex & YieldSign) != 0;
            RoadClass rc = directededge.Classification;

            if (stop || yield)
            {
                uint edgeIndex = nodeinfo.EdgeIndex;
                for (uint i = 0; i < nodeinfo.EdgeCount; i++)
                {
                    DirectedEdge diredge = tile.DirectedEdgeRef((int)(edgeIndex + i));
                    if (!diredge.IsRoad || (diredge.ReverseAccess & GraphConstants.AutoAccess) == 0)
                    {
                        continue;
                    }

                    if (minor && rc > diredge.Classification)
                    {
                        rc = diredge.Classification;
                    }
                }

                if (minor)
                {
                    if ((directededge.ForwardAccess & GraphConstants.AutoAccess) != 0 &&
                        (directededge.Classification > rc ||
                         (directededge.Classification == rc &&
                          (directededge.Use == Use.Ramp || directededge.Use == Use.TurnChannel))))
                    {
                        directededge.SetStopSign(stop);
                        directededge.SetYieldSign(yield);
                    }
                }
                else if ((directededge.ForwardAccess & GraphConstants.AutoAccess) != 0)
                {
                    directededge.SetStopSign(stop);
                    directededge.SetYieldSign(yield);
                }
            }
        }

        // Remove the temporarily set deadend flag.
        directededge.SetDeadend(false);
    }

    // ------------------------------------------------------------------
    // IsIntersectionInternal
    // ------------------------------------------------------------------

    private static bool IsIntersectionInternal(
        TileModel startTile, ITileSource reader, NodeInfo startnodeinfo, DirectedEdge directededge, uint idx)
    {
        // Internal intersection edges must be short, not a roundabout, and a road use.
        if (directededge.Length > GraphConstants.MaxInternalLength || directededge.Roundabout ||
            (byte)directededge.Use > (byte)Use.Cycleway)
        {
            return false;
        }

        static bool HasTurnRight(HashSet<Turn.Type> t) =>
            t.Contains(Turn.Type.Right) || t.Contains(Turn.Type.SharpRight);
        static bool HasTurnLeft(HashSet<Turn.Type> t) =>
            t.Contains(Turn.Type.Left) || t.Contains(Turn.Type.SharpLeft);

        TileModel tile = startTile;

        // Exclude trivial loops with only 2 edges at the start node.
        if (startnodeinfo.EdgeCount == 2)
        {
            uint ei = startnodeinfo.EdgeIndex;
            for (uint i = 0; i < startnodeinfo.EdgeCount; i++)
            {
                DirectedEdge diredge = tile.DirectedEdgeRef((int)(ei + i));
                if (i != idx && diredge.EndNode == directededge.EndNode)
                {
                    return false;
                }
            }
        }

        // Inbound edges: turn degrees onto the candidate edge.
        bool onewayInbound = false;
        uint heading = startnodeinfo.Heading(idx);
        var incomingTurnType = new HashSet<Turn.Type>();
        uint startEdgeIndex = startnodeinfo.EdgeIndex;
        for (uint i = 0; i < startnodeinfo.EdgeCount; i++)
        {
            DirectedEdge diredge = tile.DirectedEdgeRef((int)(startEdgeIndex + i));
            if (i == idx || !diredge.IsRoad || (diredge.ReverseAccess & GraphConstants.AutoAccess) == 0)
            {
                continue;
            }

            if (diredge.Roundabout)
            {
                return false;
            }

            uint fromHeading = (startnodeinfo.Heading(i) + 180) % 360;
            uint turndegree = Midgard.Util.GetTurnDegree(fromHeading, heading);
            incomingTurnType.Add(Turn.GetType(turndegree));

            // Flag if oneway, not a link, and not nearly straight onto the candidate.
            if ((diredge.ForwardAccess & GraphConstants.AutoAccess) == 0 && !diredge.Link &&
                !(turndegree < 30 || turndegree > 330))
            {
                onewayInbound = true;
            }
        }

        if (!onewayInbound)
        {
            return false;
        }

        // Get the tile at the end node; inbound heading of the candidate edge to the end node.
        if (directededge.EndNode.TileBase() != tile.Id)
        {
            tile = reader.GetTile(directededge.EndNode)!;
        }

        NodeInfo node = tile.NodeRef((int)directededge.EndNode.Id());
        uint nodeEdgeIndex = node.EdgeIndex;
        for (uint i = 0; i < node.EdgeCount; i++)
        {
            DirectedEdge diredge = tile.DirectedEdgeRef((int)(nodeEdgeIndex + i));
            if (i == directededge.OppLocalIdx)
            {
                List<PointLL> shape = tile.EdgeShape(diredge);
                if (!diredge.Forward)
                {
                    shape.Reverse();
                }

                uint hdg = (uint)Math.Round(
                    PointLL.HeadingAlongPolyline(
                        shape, GraphConstants.GetOffsetForHeading(diredge.Classification, diredge.Use)),
                    MidpointRounding.AwayFromZero);

                heading = (hdg + 180) % 360;
                break;
            }
        }

        // Outbound edges: turn degrees from the candidate edge.
        bool onewayOutbound = false;
        var outgoingTurnType = new HashSet<Turn.Type>();
        for (uint i = 0; i < node.EdgeCount; i++)
        {
            DirectedEdge diredge = tile.DirectedEdgeRef((int)(nodeEdgeIndex + i));
            if (i == directededge.OppLocalIdx || !diredge.IsRoad ||
                (diredge.ForwardAccess & GraphConstants.AutoAccess) == 0)
            {
                continue;
            }

            if (diredge.Roundabout)
            {
                return false;
            }

            List<PointLL> shape = tile.EdgeShape(diredge);
            if (!diredge.Forward)
            {
                shape.Reverse();
            }

            uint toHeading = (uint)Math.Round(
                PointLL.HeadingAlongPolyline(
                    shape, GraphConstants.GetOffsetForHeading(diredge.Classification, diredge.Use)),
                MidpointRounding.AwayFromZero);

            uint turndegree = Midgard.Util.GetTurnDegree(heading, toHeading);
            outgoingTurnType.Add(Turn.GetType(turndegree));

            if ((diredge.ReverseAccess & GraphConstants.AutoAccess) == 0 && !diredge.Link &&
                !(turndegree < 30 || turndegree > 330))
            {
                onewayOutbound = true;
            }
        }

        if (!onewayOutbound)
        {
            return false;
        }

        // Reject if incoming and outgoing edges have opposing turn degrees.
        if ((HasTurnLeft(incomingTurnType) && HasTurnRight(outgoingTurnType)) ||
            (HasTurnRight(incomingTurnType) && HasTurnLeft(outgoingTurnType)) ||
            (HasTurnLeft(outgoingTurnType) && HasTurnRight(outgoingTurnType)))
        {
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Density index (BuildDensityIndex / NodeRoadlengths / DensityCellId)
    // ------------------------------------------------------------------

    private static float NodeRoadlengths(TileModel tile, NodeInfo node)
    {
        float roadlengths = 0.0f;
        uint edgeIndex = node.EdgeIndex;
        for (uint i = 0; i < node.EdgeCount; i++)
        {
            DirectedEdge de = tile.DirectedEdgeRef((int)(edgeIndex + i));
            if (de.IsRoad || de.Use == Use.Ramp || de.Use == Use.TurnChannel ||
                de.Use == Use.Alley || de.Use == Use.EmergencyAccess)
            {
                roadlengths += de.Length;
            }
        }

        return roadlengths;
    }

    private DensityIndex BuildDensityIndex(ITileSource reader, TileModel tile, byte level)
    {
        TileLevel tileLevel = TileHierarchy.Levels()[^1];

        // Extend the tile bbox by the density radius, rounded up to the grid cell size.
        Aabb2T<double> tileBbox = tile.BoundingBox();
        double centerLat = tileBbox.Center().Y;
        float latCos = (float)Math.Cos(Constants.RadPerDeg * centerLat);
        float densityLngDeg = DensityLatDeg / latCos;

        var bbox = new Aabb2T<double>(
            new PointXY<double>(
                tileBbox.Minpt.X - (densityLngDeg + DensityCellId.SizeDeg),
                tileBbox.Minpt.Y - (DensityLatDeg + DensityCellId.SizeDeg)),
            new PointXY<double>(
                tileBbox.Maxpt.X + (densityLngDeg + DensityCellId.SizeDeg),
                tileBbox.Maxpt.Y + (DensityLatDeg + DensityCellId.SizeDeg)));

        var densityGrid = new Dictionary<uint, float>();

        // Process the current tile separately so each node always has a cell.
        {
            PointLL nodeBaseLl = tile.BaseLl;
            for (int i = 0; i < tile.NodeCount; i++)
            {
                NodeInfo node = tile.NodeRef(i);
                uint cell = DensityCellId.FromLatLng(node.LatLng(nodeBaseLl));
                densityGrid.TryGetValue(cell, out float existing);
                densityGrid[cell] = existing + NodeRoadlengths(tile, node);
            }
        }

        // Process neighboring tiles within the bbox.
        foreach (int t in tileLevel.Tiles.TileList(bbox))
        {
            var neighborId = new GraphId((uint)t, level, 0);
            if (neighborId == tile.Id)
            {
                continue;
            }

            TileModel? newtile = reader.GetTile(neighborId);
            if (newtile is null || newtile.NodeCount == 0)
            {
                continue;
            }

            PointLL nbBaseLl = newtile.BaseLl;
            for (int i = 0; i < newtile.NodeCount; i++)
            {
                NodeInfo node = newtile.NodeRef(i);
                PointLL nodeLl = node.LatLng(nbBaseLl);
                if (bbox.Contains(new PointXY<double>(nodeLl.X, nodeLl.Y)))
                {
                    uint cell = DensityCellId.FromLatLng(nodeLl);
                    densityGrid.TryGetValue(cell, out float existing);
                    densityGrid[cell] = existing + NodeRoadlengths(newtile, node);
                }
            }
        }

        float cellSizeKm = DensityCellId.SizeDeg * (float)Constants.MetersPerDegreeLat / Constants.MetersPerKm;
        float cellArea = cellSizeKm * cellSizeKm * latCos;

        var densityIndex = new DensityIndex();
        foreach (uint cellId in densityGrid.Keys)
        {
            float roadlengths = 0.0f;
            List<uint> neighbors = DensityCellId.Neighbors(cellId, densityLngDeg, DensityLatDeg);
            foreach (uint neighbor in neighbors)
            {
                if (densityGrid.TryGetValue(neighbor, out float v))
                {
                    roadlengths += v;
                }
            }

            // km/km^2. Convert roadlengths to km and divide by 2 (2 directed edges per edge).
            float density = (roadlengths * 0.0005f) / (cellArea * neighbors.Count);
            if (density > _stats.MaxDensity)
            {
                _stats.MaxDensity = density;
            }

            uint relativeValue = (uint)Math.Round(density * 0.7f, MidpointRounding.AwayFromZero);
            densityIndex.Set(cellId, Math.Min(relativeValue, 15u));
        }

        return densityIndex;
    }

    // Maps density grid cell id -> relative density (0-15).
    private sealed class DensityIndex
    {
        private readonly Dictionary<uint, uint> _map = new();

        public void Set(uint cellId, uint value) => _map[cellId] = value;

        public bool TryGet(PointLL ll, out uint value) => _map.TryGetValue(DensityCellId.FromLatLng(ll), out value);
    }

    // Density grid cell id math. Faithful port of the anonymous-namespace `struct DensityCellId`.
    private static class DensityCellId
    {
        private const uint GridLevel = 14;

        public const float SizeDeg = (float)(1 << (int)GridLevel) / 1e7f;

        private const uint ComponentBits = (sizeof(uint) * 8) / 2; // 16
        private const uint ComponentMask = (1u << (int)ComponentBits) - 1u;

        public static uint FromLatLng(PointLL ll)
        {
            uint x = (uint)((ll.Lng + 180.0) * 1e7);
            uint y = (uint)((ll.Lat + 90.0) * 1e7);

            x >>= (int)GridLevel;
            y >>= (int)GridLevel;

            return (x & ComponentMask) | ((y & ComponentMask) << (int)ComponentBits);
        }

        public static List<uint> Neighbors(uint id, float radiusLngDeg, float radiusLatDeg)
        {
            int xNeighbors = (int)Math.Ceiling(radiusLngDeg / SizeDeg);
            int yNeighbors = (int)Math.Ceiling(radiusLatDeg / SizeDeg);

            int x = (int)(id & ComponentMask);
            int y = (int)((id >> (int)ComponentBits) & ComponentMask);

            var neighbors = new List<uint>(4 * xNeighbors * yNeighbors);
            for (int nx = -xNeighbors; nx <= xNeighbors; nx++)
            {
                for (int ny = -yNeighbors; ny <= yNeighbors; ny++)
                {
                    if ((nx * nx) + (ny * ny) <= xNeighbors * yNeighbors)
                    {
                        uint neighborId =
                            (uint)(((x + nx) & ComponentMask) | (((y + ny) & ComponentMask) << (int)ComponentBits));
                        neighbors.Add(neighborId);
                    }
                }
            }

            return neighbors;
        }
    }

    // ------------------------------------------------------------------
    // ConsistentNames (StreetNamesFactory)
    // ------------------------------------------------------------------

    private static bool ConsistentNames(
        string countryCode,
        List<(string Name, bool IsRouteNum)> names1,
        List<(string Name, bool IsRouteNum)> names2)
    {
        StreetNames streetNames1 = StreetNamesFactory.Create(countryCode, names1);
        StreetNames streetNames2 = StreetNamesFactory.Create(countryCode, names2);

        // Consistent when neither has names.
        if (streetNames1.Count == 0 && streetNames2.Count == 0)
        {
            return true;
        }

        // Consistent if the common base names are not empty.
        return streetNames1.FindCommonBaseNames(streetNames2).Count != 0;
    }

    // ------------------------------------------------------------------
    // Turn lane enhancement (UpdateTurnLanes / ProcessLanes / EnhanceLeft/RightLane / GetTurnTypes)
    // ------------------------------------------------------------------

    private static void UpdateTurnLanes(
        TileModel tile,
        uint idx,
        ref DirectedEdge directededge,
        ITileSource reader,
        List<TurnLanes> turnLanes)
    {
        static bool HasTurnRight(HashSet<Turn.Type> t) =>
            t.Contains(Turn.Type.SlightRight) || t.Contains(Turn.Type.Right) || t.Contains(Turn.Type.SharpRight);
        static bool HasTurnLeft(HashSet<Turn.Type> t) =>
            t.Contains(Turn.Type.SlightLeft) || t.Contains(Turn.Type.Left) || t.Contains(Turn.Type.SharpLeft);

        if (!directededge.TurnLanes)
        {
            return;
        }

        uint index = tile.TurnLanesOffset(idx);
        string turnlaneTags = tile.NameAt(index);
        string str = TurnLanes.GetTurnLaneString(turnlaneTags);
        List<ushort> enhancedTls = TurnLanes.LaneMasks(str);

        bool updated;
        // handle [left, none, none, right] --> [left, straight, straight, right]
        updated = ProcessLanes(true, true, enhancedTls);

        if (!updated)
        {
            // handle [left, [straight, left], none, straight], [left, none, none]
            enhancedTls = TurnLanes.LaneMasks(str);

            var outgoingTurnType = new HashSet<Turn.Type>();
            GetTurnTypes(directededge, outgoingTurnType, tile, reader);
            if (outgoingTurnType.Count == 0)
            {
                directededge.SetTurnLanes(false);
                return;
            }

            updated = ProcessLanes(true, false, enhancedTls);

            if (updated && HasTurnLeft(outgoingTurnType) && directededge.StartRestriction == 0)
            {
                EnhanceRightLane(directededge, tile, reader, enhancedTls);
            }
        }

        if (!updated)
        {
            // handle [none, none, right] --> [straight, straight, right]
            enhancedTls = TurnLanes.LaneMasks(str);
            if (enhancedTls.Count > 0 &&
                ((enhancedTls[^1] & TurnLaneConstants.TurnLaneRight) != 0 ||
                 (enhancedTls[^1] & TurnLaneConstants.TurnLaneSharpRight) != 0 ||
                 (enhancedTls[^1] & TurnLaneConstants.TurnLaneSlightRight) != 0) &&
                (enhancedTls[0] == TurnLaneConstants.TurnLaneEmpty || enhancedTls[0] == TurnLaneConstants.TurnLaneNone))
            {
                var outgoingTurnType = new HashSet<Turn.Type>();
                GetTurnTypes(directededge, outgoingTurnType, tile, reader);
                if (outgoingTurnType.Count == 0)
                {
                    directededge.SetTurnLanes(false);
                    return;
                }

                updated = ProcessLanes(false, false, enhancedTls);

                if (updated && HasTurnRight(outgoingTurnType) && directededge.StartRestriction == 0)
                {
                    EnhanceLeftLane(directededge, tile, reader, enhancedTls);
                }
            }
        }

        if (!updated)
        {
            // handle [straight, straight, none] --> [straight, straight, straight]
            enhancedTls = TurnLanes.LaneMasks(str);
            if ((enhancedTls[0] & TurnLaneConstants.TurnLaneThrough) != 0 &&
                (enhancedTls[^1] == TurnLaneConstants.TurnLaneEmpty || enhancedTls[^1] == TurnLaneConstants.TurnLaneNone))
            {
                ushort previous = 0;
                for (int it = 0; it < enhancedTls.Count; it++)
                {
                    if ((enhancedTls[it] & TurnLaneConstants.TurnLaneThrough) != 0 &&
                        (previous == 0 || (previous & TurnLaneConstants.TurnLaneThrough) != 0))
                    {
                        previous = enhancedTls[it];
                    }
                    else if (previous != 0 &&
                             (enhancedTls[it] == TurnLaneConstants.TurnLaneEmpty ||
                              enhancedTls[it] == TurnLaneConstants.TurnLaneNone))
                    {
                        enhancedTls[it] = TurnLaneConstants.TurnLaneThrough;
                        updated = true;
                    }
                    else
                    {
                        updated = false;
                        break;
                    }
                }

                if (updated && directededge.StartRestriction == 0)
                {
                    EnhanceRightLane(directededge, tile, reader, enhancedTls);
                    EnhanceLeftLane(directededge, tile, reader, enhancedTls);
                }
            }
        }

        if (!updated)
        {
            // handle [none, straight, straight] --> [straight, straight, straight]
            enhancedTls = TurnLanes.LaneMasks(str);
            ushort previous = 0;
            if ((enhancedTls[^1] & TurnLaneConstants.TurnLaneThrough) != 0 &&
                (enhancedTls[0] == TurnLaneConstants.TurnLaneEmpty || enhancedTls[0] == TurnLaneConstants.TurnLaneNone))
            {
                for (int rIt = enhancedTls.Count - 1; rIt >= 0; rIt--)
                {
                    // NOTE: faithful port - C++ checks enhanced_tls.back() (not *r_it) in the first branch.
                    if ((enhancedTls[^1] & TurnLaneConstants.TurnLaneThrough) != 0 &&
                        (previous == 0 || (previous & TurnLaneConstants.TurnLaneThrough) != 0))
                    {
                        previous = enhancedTls[rIt];
                    }
                    else if (previous != 0 &&
                             (enhancedTls[rIt] == TurnLaneConstants.TurnLaneEmpty ||
                              enhancedTls[rIt] == TurnLaneConstants.TurnLaneNone))
                    {
                        enhancedTls[rIt] = TurnLaneConstants.TurnLaneThrough;
                        updated = true;
                    }
                    else
                    {
                        updated = false;
                        break;
                    }
                }

                if (updated && directededge.StartRestriction == 0)
                {
                    EnhanceRightLane(directededge, tile, reader, enhancedTls);
                    EnhanceLeftLane(directededge, tile, reader, enhancedTls);
                }
            }
        }

        // If anything was updated, regenerate the string from the updated vector.
        if (updated)
        {
            str = TurnLanes.GetTurnLaneString(TurnLanes.TurnLaneString(enhancedTls));
        }

        uint offset = tile.AddName(str);
        turnLanes.Add(new TurnLanes(idx, offset));
    }

    private static bool ProcessLanes(bool isLeft, bool endOnTurn, List<ushort> enhancedTls)
    {
        bool updated = false;
        ushort previous = 0;

        if (isLeft)
        {
            for (int it = 0; it < enhancedTls.Count; it++)
            {
                ushort cur = enhancedTls[it];
                if (((cur & TurnLaneConstants.TurnLaneLeft) != 0 || (cur & TurnLaneConstants.TurnLaneSharpLeft) != 0 ||
                     (cur & TurnLaneConstants.TurnLaneSlightLeft) != 0 || (cur & TurnLaneConstants.TurnLaneThrough) != 0) &&
                    (previous == 0 || (previous & TurnLaneConstants.TurnLaneLeft) != 0 ||
                     (previous & TurnLaneConstants.TurnLaneSharpLeft) != 0 ||
                     (previous & TurnLaneConstants.TurnLaneSlightLeft) != 0 ||
                     (previous & TurnLaneConstants.TurnLaneThrough) != 0))
                {
                    previous = cur;
                }
                else if (previous != 0 &&
                         (cur == TurnLaneConstants.TurnLaneEmpty || cur == TurnLaneConstants.TurnLaneNone))
                {
                    enhancedTls[it] = TurnLaneConstants.TurnLaneThrough;
                    if (!endOnTurn)
                    {
                        updated = true; // should end on a through
                    }
                }
                else if (previous != 0 &&
                         ((cur & TurnLaneConstants.TurnLaneRight) != 0 ||
                          (cur & TurnLaneConstants.TurnLaneSharpRight) != 0 ||
                          (cur & TurnLaneConstants.TurnLaneSlightRight) != 0))
                {
                    updated = endOnTurn;
                    break;
                }
                else
                {
                    updated = false;
                    break;
                }
            }
        }
        else
        {
            for (int rIt = enhancedTls.Count - 1; rIt >= 0; rIt--)
            {
                ushort cur = enhancedTls[rIt];
                if (((cur & TurnLaneConstants.TurnLaneRight) != 0 || (cur & TurnLaneConstants.TurnLaneSharpRight) != 0 ||
                     (cur & TurnLaneConstants.TurnLaneSlightRight) != 0 || (cur & TurnLaneConstants.TurnLaneThrough) != 0) &&
                    (previous == 0 || (previous & TurnLaneConstants.TurnLaneRight) != 0 ||
                     (previous & TurnLaneConstants.TurnLaneSharpRight) != 0 ||
                     (previous & TurnLaneConstants.TurnLaneSlightRight) != 0 ||
                     (previous & TurnLaneConstants.TurnLaneThrough) != 0))
                {
                    previous = cur;
                }
                else if (previous != 0 &&
                         (cur == TurnLaneConstants.TurnLaneEmpty || cur == TurnLaneConstants.TurnLaneNone))
                {
                    enhancedTls[rIt] = TurnLaneConstants.TurnLaneThrough;
                    updated = true;
                }
                else
                {
                    updated = false;
                    break;
                }
            }
        }

        return updated;
    }

    private static void EnhanceRightLane(
        DirectedEdge directededge, TileModel tile, ITileSource reader, List<ushort> enhancedTls)
    {
        var outgoingTurnType = new HashSet<Turn.Type>();
        GetTurnTypes(directededge, outgoingTurnType, tile, reader);

        int index = enhancedTls.Count - 1;
        ushort tl = enhancedTls[index];

        if (outgoingTurnType.Contains(Turn.Type.SlightRight))
        {
            // Assume slight right is the through if straight is not found.
            if (tl == TurnLaneConstants.TurnLaneThrough && outgoingTurnType.Contains(Turn.Type.Straight))
            {
                tl |= TurnLaneConstants.TurnLaneSlightRight;
            }
        }

        if (outgoingTurnType.Contains(Turn.Type.Right))
        {
            tl |= TurnLaneConstants.TurnLaneRight;
        }

        if (outgoingTurnType.Contains(Turn.Type.SharpRight))
        {
            tl |= TurnLaneConstants.TurnLaneSharpRight;
        }

        enhancedTls[index] = tl;
    }

    private static void EnhanceLeftLane(
        DirectedEdge directededge, TileModel tile, ITileSource reader, List<ushort> enhancedTls)
    {
        var outgoingTurnType = new HashSet<Turn.Type>();
        GetTurnTypes(directededge, outgoingTurnType, tile, reader);

        ushort tl = enhancedTls[0];
        if (outgoingTurnType.Contains(Turn.Type.SlightLeft))
        {
            if (tl == TurnLaneConstants.TurnLaneThrough && outgoingTurnType.Contains(Turn.Type.Straight))
            {
                tl |= TurnLaneConstants.TurnLaneSlightLeft;
            }
        }

        if (outgoingTurnType.Contains(Turn.Type.Left))
        {
            tl |= TurnLaneConstants.TurnLaneLeft;
        }

        if (outgoingTurnType.Contains(Turn.Type.SharpLeft))
        {
            tl |= TurnLaneConstants.TurnLaneSharpLeft;
        }

        enhancedTls[0] = tl;
    }

    private static void GetTurnTypes(
        DirectedEdge directededge, HashSet<Turn.Type> outgoingTurnType, TileModel tile, ITileSource reader)
    {
        // Heading at the end of the incoming edge based on its shape.
        List<PointLL> incomingShape = tile.EdgeShape(directededge);
        if (directededge.Forward)
        {
            incomingShape.Reverse();
        }

        uint heading = (uint)Math.Round(
            PointLL.HeadingAlongPolyline(
                incomingShape, GraphConstants.GetOffsetForHeading(directededge.Classification, directededge.Use)),
            MidpointRounding.AwayFromZero);
        heading = (heading + 180) % 360;

        // Get the tile at the end node and find inbound heading of the candidate edge.
        TileModel endTile = directededge.EndNode.TileBase() == tile.Id ? tile : reader.GetTile(directededge.EndNode)!;
        NodeInfo node = endTile.NodeRef((int)directededge.EndNode.Id());

        uint edgeIndex = node.EdgeIndex;
        for (uint i = 0; i < node.EdgeCount; i++)
        {
            DirectedEdge diredge = endTile.DirectedEdgeRef((int)(edgeIndex + i));

            // Skip opposing edge, non-roads, and non-drivable-outbound edges.
            if (i == directededge.OppLocalIdx ||
                (diredge.ForwardAccess & GraphConstants.AutoAccess) == 0 ||
                (directededge.Restrictions & (1u << (int)diredge.LocalEdgeIdx)) != 0)
            {
                continue;
            }

            List<PointLL> shape = endTile.EdgeShape(diredge);
            if (!diredge.Forward)
            {
                shape.Reverse();
            }

            uint toHeading = (uint)Math.Round(
                PointLL.HeadingAlongPolyline(
                    shape, GraphConstants.GetOffsetForHeading(diredge.Classification, diredge.Use)),
                MidpointRounding.AwayFromZero);

            uint turndegree = Midgard.Util.GetTurnDegree(heading, toHeading);
            outgoingTurnType.Add(Turn.GetType(turndegree));
        }
    }

    private static bool PointEquals(PointLL a, PointLL b) => a.First == b.First && a.Second == b.Second;
}
