// Faithful C# port of Valhalla mjolnir hierarchybuilder.cc + hierarchybuilder.h @ 3.7.0.
// Sources:
//   F:/github/valhalla/src/mjolnir/hierarchybuilder.cc
//   F:/github/valhalla/valhalla/mjolnir/hierarchybuilder.h
//
// Divides the road network graph (the local/base level produced by GraphBuilder + GraphEnhancer +
// GraphFilter) into hierarchy levels (highway / arterial / local). The pipeline (HierarchyBuilder::
// Build) is:
//   1. CreateNodeAssociations - for each base node, determine which levels it should exist on (from
//                               the hierarchy level of each of its non-transit edges) and assign a
//                               "new" node id on each such level. Records new->old (a sequence) and
//                               old->new (an in-memory association keyed by old node id).
//   2. SortSequences          - sort new->old so highway level is first (then tile id, then id), and
//                               sort old->new by old node id.
//   3. FormTilesInNewLevel    - iterate the sorted new nodes (highway down to local), building a new
//                               tile per level/tile. Copy each base node + the directed edges that
//                               belong on the current level, remapping end nodes through old->new,
//                               re-adding signs / turn lanes / access restrictions / lane connectivity
//                               / edge info, and adding up/down node transitions. Reset shortcut bits.
//   4. RemoveUnusedLocalTiles - delete base tiles whose nodes/edges all moved up to higher levels.
//
// EXCLUDED (out of scope: transit/bss/elevation): UpdateTransitConnections runs only when a
// transit_dir is configured; transit-connection / egress / platform edges and bss connections never
// appear in the auto/truck graph, so the transit-level handling collapses to dead branches that are
// preserved structurally but never taken.
//
// PORT-NOTE: the C++ uses midgard::sequence<T> (a memory-mapped, externally-sortable file) for the
// new->old and old->new node associations so the build scales on low-memory machines. The on-device
// port keeps the IDENTICAL algorithm but backs the associations with in-memory List<T> + List.Sort
// (the on-disk backing is purely a memory optimization and does not affect the produced tiles). The
// GraphReader / GraphTileBuilder are the ported baldr reader + the mjolnir write side, so the tiles
// written here are byte-compatible with the GraphTile reader. The boost::property_tree config is
// replaced by the GraphReader.Config (tile_dir + cache knobs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>Measured output from one hierarchy build.</summary>
public sealed class HierarchyBuildResult
{
    private readonly Dictionary<string, TimeSpan> stageDurations =
        new(StringComparer.Ordinal);

    /// <summary>Number of base-node association records.</summary>
    public int BaseNodeAssociationCount { get; internal set; }

    /// <summary>Number of new hierarchy-node association records.</summary>
    public int NewNodeAssociationCount { get; internal set; }

    /// <summary>Elapsed wall time for each hierarchy sub-stage.</summary>
    public IReadOnlyDictionary<string, TimeSpan> StageDurations => stageDurations;

    internal void RecordStageDuration(string stage, TimeSpan duration) =>
        stageDurations[stage] = duration;
}

/// <summary>
/// Divides the road network graph into hierarchy levels. Faithful port of the C++
/// <c>class HierarchyBuilder</c> plus the hierarchybuilder.cc free functions.
/// </summary>
public static class HierarchyBuilder
{
    // Structure to associate old nodes to new nodes. An original node can associate to multiple nodes
    // on different hierarchy levels. If a node does not exist on a level, the associated node is
    // invalid. Faithful port of the anonymous-namespace struct OldToNewNodes.
    private struct OldToNewNodes
    {
        public GraphId NodeId;        // Old node
        public GraphId HighwayNode;   // New, associated node on highway level
        public GraphId ArterialNode;  // New, associated node on arterial level
        public GraphId LocalNode;     // New, associated node on local level
        public uint Density;          // Density at the node (for edge density)

        public OldToNewNodes(GraphId node, GraphId highway, GraphId arterial, GraphId local, uint d)
        {
            NodeId = node;
            HighwayNode = highway;
            ArterialNode = arterial;
            LocalNode = local;
            Density = d;
        }
    }

    /// <summary>
    /// Build successive levels of the hierarchy, starting at the local base level. Each successive
    /// level of the hierarchy is based on and connected to the next. Faithful port of
    /// <c>HierarchyBuilder::Build</c>.
    /// </summary>
    /// <param name="config">GraphReader configuration (tile directory + cache knobs).</param>
    public static HierarchyBuildResult Build(GraphReader.Config config) =>
        Build(config, maxDegreeOfParallelism: 1, CancellationToken.None);

    /// <summary>
    /// Builds hierarchy tiles with bounded tile-local parallelism after the global node association
    /// indexes are frozen.
    /// </summary>
    public static HierarchyBuildResult Build(
        GraphReader.Config config,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        cancellationToken.ThrowIfCancellationRequested();

        // Construct the discovery reader. Tile workers receive independent, bounded readers after
        // the association indexes are complete and immutable.
        var reader = new GraphReader(config);
        var result = new HierarchyBuildResult();
        var stopwatch = Stopwatch.StartNew();

        // Association of old nodes to new nodes.
        var newToOld = new List<(GraphId New, GraphId Old)>();
        var oldToNew = new List<OldToNewNodes>();

        // Association of old nodes to new nodes (both directions).
        CreateNodeAssociations(reader, newToOld, oldToNew, cancellationToken);
        stopwatch.Stop();
        result.RecordStageDuration("associations", stopwatch.Elapsed);
        result.NewNodeAssociationCount = newToOld.Count;
        result.BaseNodeAssociationCount = oldToNew.Count;

        // Sort the sequences and index dense old-node ids by their base tile.
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Restart();
        SortSequences(newToOld, oldToNew);
        Dictionary<GraphId, int> oldToNewTileOffsets =
            CreateAssociationTileOffsets(oldToNew);
        stopwatch.Stop();
        result.RecordStageDuration("sort", stopwatch.Elapsed);

        // Iterate through the hierarchy (from highway down to local) and build new tiles.
        cancellationToken.ThrowIfCancellationRequested();
        reader.Clear();
        stopwatch.Restart();
        FormTilesInNewLevel(
            config,
            newToOld,
            oldToNew,
            oldToNewTileOffsets,
            maxDegreeOfParallelism,
            cancellationToken);
        stopwatch.Stop();
        result.RecordStageDuration("form-tiles", stopwatch.Elapsed);

        // Remove any base tiles that no longer have any data (nodes and edges only exist on arterial
        // and highway levels).
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Restart();
        RemoveUnusedLocalTiles(config.TileDir, oldToNew);
        stopwatch.Stop();
        result.RecordStageDuration("cleanup", stopwatch.Elapsed);

        // The transit-connection update (UpdateTransitConnections) runs only when a transit_dir is
        // configured. Transit is out of scope for the auto/truck graph build, so it is omitted.
        return result;
    }

    // Gets the hierarchy level respecting ramp & ferry-related edges which can be marked with a
    // different road class: links will have the lowest connecting non-link road class,
    // ferry-connecting edges will have kPrimary. Faithful port of get_hierarchy_level.
    private static byte GetHierarchyLevel(DirectedEdge de)
        => de.IsShortcut
            ? TileHierarchy.GetLevel((RoadClass)de.Shortcut)
            : TileHierarchy.GetLevel(de.Classification);

    // Add a downward transition edge if the node is valid. Faithful port of AddDownwardTransition.
    private static bool AddDownwardTransition(GraphId node, GraphTileBuilder tilebuilder)
    {
        if (node.IsValid())
        {
            tilebuilder.Transitions.Add(new NodeTransition(node, false));
            return true;
        }

        return false;
    }

    // Add an upward transition edge if the node is valid. Faithful port of AddUpwardTransition.
    private static bool AddUpwardTransition(GraphId node, GraphTileBuilder tilebuilder)
    {
        if (node.IsValid())
        {
            tilebuilder.Transitions.Add(new NodeTransition(node, true));
            return true;
        }

        return false;
    }

    // Sort the new nodes (highway level first), then sort old to new by old node id. Faithful port of
    // SortSequences.
    private static void SortSequences(
        List<(GraphId New, GraphId Old)> newToOld,
        List<OldToNewNodes> oldToNew)
    {
        // Sort the new nodes. Sort so highway level is first.
        newToOld.Sort((a, b) =>
        {
            if (a.New.Level() == b.New.Level())
            {
                if (a.New.Tileid() == b.New.Tileid())
                {
                    return a.New.Id().CompareTo(b.New.Id());
                }

                return a.New.Tileid().CompareTo(b.New.Tileid());
            }

            return a.New.Level().CompareTo(b.New.Level());
        });

        // Group dense base-node ids by tile so association lookup is a direct offset operation.
        oldToNew.Sort(static (left, right) =>
        {
            int tileComparison = left.NodeId.Tileid().CompareTo(right.NodeId.Tileid());
            return tileComparison != 0
                ? tileComparison
                : left.NodeId.Id().CompareTo(right.NodeId.Id());
        });
    }

    private static Dictionary<GraphId, int> CreateAssociationTileOffsets(
        List<OldToNewNodes> oldToNew)
    {
        var offsets = new Dictionary<GraphId, int>();
        GraphId previousTile = default;
        var tileStart = 0;
        for (var index = 0; index < oldToNew.Count; index++)
        {
            GraphId node = oldToNew[index].NodeId;
            GraphId tile = node.TileBase();
            if (index == 0 || tile != previousTile)
            {
                if (node.Id() != 0)
                {
                    throw new InvalidOperationException("Node association tile does not start at node zero.");
                }

                offsets.Add(tile, index);
                previousTile = tile;
                tileStart = index;
            }
            else if (node.Id() != checked((uint)(index - tileStart)))
            {
                throw new InvalidOperationException("Node associations are not dense within their tile.");
            }
        }

        return offsets;
    }

    // Node ids are dense within each base tile. Resolve them through a tile-sized offset index rather
    // than repeating a whole-sequence binary search for every node, directed edge, and transition.
    private static OldToNewNodes FindNodes(
        List<OldToNewNodes> oldToNew,
        IReadOnlyDictionary<GraphId, int> tileOffsets,
        GraphId node)
    {
        if (!tileOffsets.TryGetValue(node.TileBase(), out int tileStart))
        {
            throw new InvalidOperationException("Didn't find node tile!");
        }

        int index = checked(tileStart + (int)node.Id());
        if ((uint)index >= (uint)oldToNew.Count || oldToNew[index].NodeId != node)
        {
            throw new InvalidOperationException("Didn't find node!");
        }

        return oldToNew[index];
    }

    // Create node associations between "new" nodes placed into respective hierarchy levels and the
    // existing nodes on the base/local level. Faithful port of CreateNodeAssociations.
    private static void CreateNodeAssociations(
        GraphReader reader,
        List<(GraphId New, GraphId Old)> newToOld,
        List<OldToNewNodes> oldToNew,
        CancellationToken cancellationToken)
    {
        // Map of tiles vs. count of nodes. Used to construct new node Ids.
        var newNodes = new Dictionary<GraphId, uint>();

        // lambda to get the next "new" node Id in a given tile.
        GraphId GetNewNode(GraphId tile)
        {
            if (!newNodes.TryGetValue(tile, out uint count))
            {
                var newNode = new GraphId(tile.Tileid(), tile.Level(), 0);
                newNodes[tile] = 1;
                return newNode;
            }

            var node = new GraphId(tile.Tileid(), tile.Level(), count);
            newNodes[tile] = count + 1;
            return node;
        }

        // Hierarchy level information.
        TileLevel arterialLevel = TileHierarchy.Levels()[1];
        uint al = arterialLevel.Level;
        TileLevel highwayLevel = TileHierarchy.Levels()[0];
        uint hl = highwayLevel.Level;

        // Iterate through all tiles in the local level.
        HashSet<GraphId> localTiles = reader.GetTileSet();
        foreach (GraphId baseTileId in localTiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // We keep all transit data inside the transit hierarchy.
            if (baseTileId.Level() == TileHierarchy.GetTransitLevel().Level)
            {
                continue;
            }

            // Get the graph tile. Skip if no tile exists or no nodes exist in the tile.
            GraphTile? tile = reader.GetGraphTile(baseTileId);
            if (tile is null)
            {
                continue;
            }

            // Iterate through the nodes. Add nodes to the new level when best road class <= the new
            // level classification cutoff.
            var levels = new bool[3];
            uint nodecount = tile.Header().Nodecount();
            GraphId basenode = baseTileId;
            GraphId edgeid = baseTileId;
            PointLL baseLl = tile.Header().BaseLl();
            for (uint i = 0; i < nodecount; i++, basenode = Increment(basenode))
            {
                if ((i & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                NodeInfo nodeinfo = tile.Node(basenode);

                // Iterate through the edges to see which levels this node exists.
                levels[0] = levels[1] = levels[2] = false;
                for (uint j = 0; j < nodeinfo.EdgeCount; j++, edgeid = Increment(edgeid))
                {
                    // Update the flag for the level of this edge (skip transit connection edges).
                    DirectedEdge directededge = tile.DirectedEdge(edgeid);
                    if (directededge.BssConnection)
                    {
                        // Despite the road class, Bike Share Stations' connections are always local.
                        levels[2] = true;
                    }
                    else if (directededge.Use != Use.TransitConnection &&
                             directededge.Use != Use.EgressConnection &&
                             directededge.Use != Use.PlatformConnection)
                    {
                        levels[GetHierarchyLevel(directededge)] = true;
                    }
                }

                // Associate new nodes to base nodes and base node to new nodes.
                GraphId highwayNode = default;
                GraphId arterialNode = default;
                GraphId localNode = default;
                if (levels[0])
                {
                    var newTile = new GraphId((uint)highwayLevel.Tiles.TileId(nodeinfo.LatLng(baseLl)), hl, 0);
                    highwayNode = GetNewNode(newTile);
                    newToOld.Add((highwayNode, basenode));
                }

                if (levels[1])
                {
                    var newTile = new GraphId((uint)arterialLevel.Tiles.TileId(nodeinfo.LatLng(baseLl)), al, 0);
                    arterialNode = GetNewNode(newTile);
                    newToOld.Add((arterialNode, basenode));
                }

                if (levels[2])
                {
                    localNode = GetNewNode(baseTileId);
                    newToOld.Add((localNode, basenode));
                }

                // Associate the old node to the new node(s). Invalid nodes indicate no node exists on
                // the new level.
                oldToNew.Add(new OldToNewNodes(basenode, highwayNode, arterialNode, localNode, nodeinfo.Density));
            }

            // Check if we need to clear the tile cache.
            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }
    }

    // Form tiles in the new level. Faithful port of FormTilesInNewLevel.
    // Form tiles in the new level. Global association indexes are frozen before this method runs.
    // Target tiles are independent, but levels retain an explicit barrier so local tiles are not
    // replaced while highway or arterial workers still read their base data.
    private static void FormTilesInNewLevel(
        GraphReader.Config config,
        List<(GraphId New, GraphId Old)> newToOld,
        List<OldToNewNodes> oldToNew,
        IReadOnlyDictionary<GraphId, int> oldToNewTileOffsets,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        // lambda to indicate whether a directed edge should be included on the current level.
        bool IncludeEdge(DirectedEdge directededge, GraphId baseNode, byte currentLevel)
        {
            if (directededge.Use == Use.TransitConnection ||
                directededge.Use == Use.EgressConnection ||
                directededge.Use == Use.PlatformConnection)
            {
                // Transit connection edges should live on the lowest class level where a new node
                // exists.
                OldToNewNodes f = FindNodes(oldToNew, oldToNewTileOffsets, baseNode);
                byte lowestLevel;
                if (f.LocalNode.IsValid())
                {
                    lowestLevel = 2;
                }
                else if (f.ArterialNode.IsValid())
                {
                    lowestLevel = 1;
                }
                else if (f.HighwayNode.IsValid())
                {
                    lowestLevel = 0;
                }
                else
                {
                    throw new InvalidOperationException("Could not find valid node level");
                }

                return lowestLevel == currentLevel;
            }

            if (directededge.BssConnection)
            {
                // Despite the road class, Bike Share Stations' connections are always local.
                return currentLevel == 2;
            }

            return GetHierarchyLevel(directededge) == currentLevel;
        }

        void BuildTile(
            GraphReader reader,
            (int Start, int End, GraphId TileId) tileRange)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GraphId tileId = tileRange.TileId;
            byte currentLevel = (byte)tileId.Level();
            PointLL baseLl = TileHierarchy.GetTiling(currentLevel).Base((int)tileId.Tileid());
            var tilebuilder = new GraphTileBuilder(tileId);
            tilebuilder.HeaderBuilder.SetBaseLl(baseLl);

            for (var nodeIndex = tileRange.Start; nodeIndex < tileRange.End; nodeIndex++)
            {
                if ((nodeIndex & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                (GraphId nodea, GraphId baseNode) = newToOld[nodeIndex];

                // Get the node in the base level.
                GraphTile? tile = reader.GetGraphTile(baseNode);
                if (tile is null)
                {
                    continue;
                }

                // Copy the data version & checksum.
                tilebuilder.HeaderBuilder.SetDatasetId(tile.Header().DatasetId());
                tilebuilder.HeaderBuilder.SetChecksum(tile.Header().Checksum());

                // Copy node information and set the node lat,lon offsets within the new tile.
                NodeInfo baseni = tile.Node((int)baseNode.Id());
                AdminInfo admin = tile.AdminInfo((int)baseni.AdminIndex);
                NodeInfo node = baseni;
                node.SetLatLng(baseLl, baseni.LatLng(tile.Header().BaseLl()));
                node.SetEdgeIndex((uint)tilebuilder.DirectedEdges.Count);
                node.SetTimezone(baseni.Timezone());
                node.SetAdminIndex((ushort)tilebuilder.AddAdmin(
                    admin.CountryText, admin.StateText, admin.CountryIso, admin.StateIso));

                // Density at this node.
                uint density1 = baseni.Density;

                // Current edge count.
                int edgeCount = tilebuilder.DirectedEdges.Count;

                // Iterate through directed edges of the base node to get the remaining directed edges
                // (based on classification/importance cutoff).
                var baseEdgeId = new GraphId(baseNode.Tileid(), baseNode.Level(), baseni.EdgeIndex);
                for (uint i = 0; i < baseni.EdgeCount; i++, baseEdgeId = Increment(baseEdgeId))
                {
                    // Check if the directed edge should exist on this level.
                    DirectedEdge directededge = tile.DirectedEdge(baseEdgeId);
                    if (!IncludeEdge(directededge, baseNode, currentLevel))
                    {
                        continue;
                    }

                    // Copy the directed edge information.
                    DirectedEdge newedge = directededge;

                    // Set the end node for this edge. Transit connection edges remain connected to the
                    // same node on the transit level. Need to set nodeb for use in AddEdgeInfo.
                    uint density2 = 32;
                    GraphId nodeb;
                    if (directededge.Use == Use.TransitConnection ||
                        directededge.Use == Use.EgressConnection ||
                        directededge.Use == Use.PlatformConnection)
                    {
                        nodeb = directededge.EndNode;
                    }
                    else
                    {
                        OldToNewNodes newNodes =
                            FindNodes(oldToNew, oldToNewTileOffsets, directededge.EndNode);
                        if (currentLevel == 0)
                        {
                            nodeb = newNodes.HighwayNode;
                        }
                        else if (currentLevel == 1)
                        {
                            nodeb = newNodes.ArterialNode;
                        }
                        else
                        {
                            nodeb = newNodes.LocalNode;
                        }

                        density2 = newNodes.Density;
                    }

                    newedge.SetEndNode(nodeb);

                    // Set the edge density to the average of the relative density at the end nodes.
                    uint edgeDensity = (density2 == 32) ? density1 : (density1 + density2) / 2;
                    newedge.SetDensity(edgeDensity);

                    // Set opposing edge indexes to 0 (gets set in graph validator).
                    newedge.SetOppIndex(0);

                    // Get signs from the base directed edge.
                    if (directededge.Sign)
                    {
                        List<SignInfo> signs = tile.GetSigns(baseEdgeId.Id());
                        tilebuilder.AddSigns((uint)tilebuilder.DirectedEdges.Count, signs);
                    }

                    // Get turn lanes from the base directed edge.
                    if (directededge.TurnLanes)
                    {
                        uint offset = tile.TurnLanesOffset(baseEdgeId.Id());
                        tilebuilder.AddTurnLanes(
                            (uint)tilebuilder.DirectedEdges.Count,
                            tile.GetName(offset));
                    }

                    // Get access restrictions from the base directed edge.
                    if (directededge.AccessRestriction != 0)
                    {
                        (IReadOnlyList<AccessRestriction> restrictions, _) =
                            tile.GetAccessRestrictions(baseEdgeId.Id());
                        foreach (AccessRestriction res in restrictions)
                        {
                            tilebuilder.AddAccessRestriction(new AccessRestriction(
                                (uint)tilebuilder.DirectedEdges.Count,
                                res.Type(),
                                res.Modes(),
                                res.Value(),
                                res.ExceptDestination()));
                        }
                    }

                    // Copy lane connectivity.
                    if (directededge.LaneConnectivity)
                    {
                        tilebuilder.CopyLaneConnectivityFromTile(tile, baseEdgeId.Id());
                    }

                    // Names can be different in the forward and backward direction.
                    bool diffNames = tilebuilder.OpposingEdgeInfoDiffers(tile, directededge);

                    // Get edge info, shape, and names from the old tile and add to the new.
                    EdgeInfo edgeinfo = tile.EdgeInfo(directededge);
                    string encodedShape = edgeinfo.EncodedShape();
                    uint w = Hash(
                        encodedShape +
                        edgeinfo.WayId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    uint edgeInfoOffset = tilebuilder.AddEdgeInfo(
                        w,
                        nodea,
                        nodeb,
                        edgeinfo.WayId,
                        edgeinfo.MeanElevation,
                        edgeinfo.BikeNetwork,
                        edgeinfo.SpeedLimit,
                        Encoded.Decode7(encodedShape),
                        edgeinfo.GetNames(),
                        edgeinfo.GetTaggedValues(),
                        edgeinfo.GetLinguisticTaggedValues(),
                        edgeinfo.GetTypes(),
                        out _,
                        diffNames);

                    newedge.SetEdgeInfoOffset(edgeInfoOffset);

                    // reset shortcuts after hijacking them for reclassification.
                    newedge.SetHierarchyRoadClass(RoadClass.Motorway, true);

                    // Add directed edge.
                    tilebuilder.DirectedEdges.Add(newedge);
                }

                // Add node transitions.
                uint index = (uint)tilebuilder.Transitions.Count;
                OldToNewNodes assoc = FindNodes(oldToNew, oldToNewTileOffsets, baseNode);
                if (currentLevel == 0)
                {
                    AddDownwardTransition(assoc.ArterialNode, tilebuilder);
                    AddDownwardTransition(assoc.LocalNode, tilebuilder);
                }
                else if (currentLevel == 1)
                {
                    AddUpwardTransition(assoc.HighwayNode, tilebuilder);
                    AddDownwardTransition(assoc.LocalNode, tilebuilder);
                }
                else if (currentLevel == 2)
                {
                    AddUpwardTransition(assoc.HighwayNode, tilebuilder);
                    AddUpwardTransition(assoc.ArterialNode, tilebuilder);
                }
                else
                {
                    throw new InvalidOperationException("current_level was never set");
                }

                // Set the node transition count and index.
                uint count = (uint)tilebuilder.Transitions.Count - index;
                if (count > 0)
                {
                    node.SetTransitionCount(count);
                    node.SetTransitionIndex(index);
                }

                // Set the edge count for the new node.
                node.SetEdgeCount((uint)(tilebuilder.DirectedEdges.Count - edgeCount));

                // Get named signs from the base node.
                if (baseni.NamedIntersection)
                {
                    List<SignInfo> signs = tile.GetSigns(baseNode.Id(), true);
                    node.SetNamedIntersection(true);
                    tilebuilder.AddSigns((uint)tilebuilder.Nodes.Count, signs);
                }

                tilebuilder.Nodes.Add(node);
            }

            cancellationToken.ThrowIfCancellationRequested();
            tilebuilder.StoreTileData(reader.TileDir());
            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }

        var tileRanges = new List<(int Start, int End, GraphId TileId)>();
        for (var start = 0; start < newToOld.Count;)
        {
            GraphId tileId = newToOld[start].New.TileBase();
            var end = start + 1;
            while (end < newToOld.Count && newToOld[end].New.TileBase() == tileId)
            {
                end++;
            }

            tileRanges.Add((start, end, tileId));
            start = end;
        }

        GraphReader.Config workerConfig =
            CreateHierarchyWorkerReaderConfig(config, maxDegreeOfParallelism);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken,
        };

        // Preserve the upstream level order. In particular, local-level publication cannot begin
        // until every higher-level worker has finished reading the original local tiles.
        for (byte level = 0; level < TileHierarchy.Levels().Count; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var levelRanges = new List<(int Start, int End, GraphId TileId)>();
            foreach ((int Start, int End, GraphId TileId) tileRange in tileRanges)
            {
                if (tileRange.TileId.Level() == level)
                {
                    levelRanges.Add(tileRange);
                }
            }

            if (levelRanges.Count == 0)
            {
                continue;
            }

            if (maxDegreeOfParallelism == 1 || levelRanges.Count == 1)
            {
                var reader = new GraphReader(workerConfig);
                foreach ((int Start, int End, GraphId TileId) tileRange in levelRanges)
                {
                    BuildTile(reader, tileRange);
                }

                reader.Clear();
                continue;
            }

            Parallel.ForEach(
                levelRanges,
                options,
                () => new GraphReader(workerConfig),
                (tileRange, _, reader) =>
                {
                    BuildTile(reader, tileRange);
                    return reader;
                },
                reader => reader.Clear());
        }
    }

    private static GraphReader.Config CreateHierarchyWorkerReaderConfig(
        GraphReader.Config config,
        int maxDegreeOfParallelism) =>
        new()
        {
            TileDir = config.TileDir,
            MaxCacheSize = Math.Max(1, config.MaxCacheSize / maxDegreeOfParallelism),
            UseLruMemCache = config.UseLruMemCache,
            LruMemCacheHardControl = config.LruMemCacheHardControl,
            UseSimpleMemCache = config.UseSimpleMemCache,
            GlobalSynchronizedCache = config.GlobalSynchronizedCache,
            MaxConcurrentReaderUsers = 1,
            TrafficSnapshot = config.TrafficSnapshot,
        };


    // Remove any base tiles that no longer have any data (nodes and edges only exist on arterial and
    // highway levels). Faithful port of RemoveUnusedLocalTiles.
    private static void RemoveUnusedLocalTiles(string tileDir, List<OldToNewNodes> oldToNew)
    {
        // Iterate through the node association sequence.
        var tileMap = new Dictionary<GraphId, bool>();
        foreach (OldToNewNodes assoc in oldToNew)
        {
            GraphId tileBase = assoc.NodeId.TileBase();
            if (!tileMap.TryGetValue(tileBase, out bool hasLocal))
            {
                tileMap[tileBase] = assoc.LocalNode.IsValid();
            }
            else if (assoc.LocalNode.IsValid())
            {
                tileMap[tileBase] = true;
            }
        }

        foreach (KeyValuePair<GraphId, bool> entry in tileMap)
        {
            if (!entry.Value)
            {
                // Remove the file.
                GraphId emptyTile = entry.Key;
                string fileLocation = Path.Combine(tileDir, GraphTile.FileSuffix(emptyTile.TileBase()));
                if (File.Exists(fileLocation))
                {
                    File.Delete(fileLocation);
                }

                string gz = fileLocation + ".gz";
                if (File.Exists(gz))
                {
                    File.Delete(gz);
                }
            }
        }
    }

    // Increment a GraphId's id field by one (the C++ ++GraphId increments the id within the tile).
    private static GraphId Increment(GraphId id) => new GraphId(id.Tileid(), id.Level(), id.Id() + 1);

    // std::hash<std::string> analogue used to key shared edge info across tiles. Any stable hash works
    // here: the value is only used as the edgeindex component of the (edgeindex, nodea, nodeb) tuple
    // that shares edge info between the two directed edges of an edge (faithful to the C++ which hashes
    // the encoded shape + way id for the same purpose). Matches GraphFilter.Hash.
    private static uint Hash(string s)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in s)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }
    }
}
