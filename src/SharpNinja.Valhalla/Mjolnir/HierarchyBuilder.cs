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
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

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
    public static void Build(GraphReader.Config config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Construct GraphReader.
        var reader = new GraphReader(config);

        // Association of old nodes to new nodes.
        var newToOld = new List<(GraphId New, GraphId Old)>();
        var oldToNew = new List<OldToNewNodes>();

        // Association of old nodes to new nodes (both directions).
        CreateNodeAssociations(reader, newToOld, oldToNew);

        // Sort the sequences.
        SortSequences(newToOld, oldToNew);

        // Iterate through the hierarchy (from highway down to local) and build new tiles.
        FormTilesInNewLevel(reader, newToOld, oldToNew);

        // Remove any base tiles that no longer have any data (nodes and edges only exist on arterial
        // and highway levels).
        RemoveUnusedLocalTiles(reader.TileDir(), oldToNew);

        // The transit-connection update (UpdateTransitConnections) runs only when a transit_dir is
        // configured. Transit is out of scope for the auto/truck graph build, so it is omitted.
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

        // Sort old to new by node Id.
        oldToNew.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));
    }

    // Convenience method to find the node association (binary search on the sorted old->new list).
    // Faithful port of find_nodes.
    private static OldToNewNodes FindNodes(List<OldToNewNodes> oldToNew, GraphId node)
    {
        // std::lower_bound on NodeId, then verify the match (the C++ sequence::find returns the first
        // element not-less-than the target and throws if no exact node match).
        int low = 0;
        int high = oldToNew.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (oldToNew[mid].NodeId < node)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        if (low >= oldToNew.Count || oldToNew[low].NodeId != node)
        {
            throw new InvalidOperationException("Didn't find node!");
        }

        return oldToNew[low];
    }

    // Create node associations between "new" nodes placed into respective hierarchy levels and the
    // existing nodes on the base/local level. Faithful port of CreateNodeAssociations.
    private static void CreateNodeAssociations(
        GraphReader reader,
        List<(GraphId New, GraphId Old)> newToOld,
        List<OldToNewNodes> oldToNew)
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
    private static void FormTilesInNewLevel(
        GraphReader reader,
        List<(GraphId New, GraphId Old)> newToOld,
        List<OldToNewNodes> oldToNew)
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
                OldToNewNodes f = FindNodes(oldToNew, baseNode);
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

        // Iterate through the new nodes. They have been sorted by level so highway level is first.
        reader.Clear();
        byte currentLevel = byte.MaxValue;
        GraphId tileId = default;
        PointLL? baseLl = null;
        GraphTileBuilder? tilebuilder = null;
        foreach ((GraphId nodea, GraphId baseNode) in newToOld)
        {
            // Get the node - check if a new tile.
            if (nodea.TileBase() != tileId)
            {
                // Store the prior tile.
                tilebuilder?.StoreTileData(reader.TileDir());

                // New tilebuilder for the next tile. Update current level.
                tileId = nodea.TileBase();
                tilebuilder = new GraphTileBuilder(tileId);
                currentLevel = (byte)nodea.Level();

                // Set the base ll for this tile.
                baseLl = TileHierarchy.GetTiling(currentLevel).Base((int)tileId.Tileid());
                tilebuilder.HeaderBuilder.SetBaseLl(baseLl);

                // Check if we need to clear the base/local tile cache.
                if (reader.OverCommitted())
                {
                    reader.Trim();
                }
            }

            // Get the node in the base level.
            GraphTile? tile = reader.GetGraphTile(baseNode);
            if (tile is null)
            {
                // LOG_ERROR("Base tile is null? ");
                continue;
            }

            // Copy the data version & checksum.
            tilebuilder!.HeaderBuilder.SetDatasetId(tile.Header().DatasetId());
            tilebuilder.HeaderBuilder.SetChecksum(tile.Header().Checksum());

            // Copy node information and set the node lat,lon offsets within the new tile.
            NodeInfo baseni = tile.Node((int)baseNode.Id());
            AdminInfo admin = tile.AdminInfo((int)baseni.AdminIndex);
            NodeInfo node = baseni;
            // baseLl is set on the first loop iteration (tileId starts at default, so
            // nodea.TileBase() != tileId is always true the first time through) and on every tile
            // change thereafter, so it is always non-null by the time it's read here.
            node.SetLatLng(baseLl!, baseni.LatLng(tile.Header().BaseLl()));
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
                    OldToNewNodes newNodes = FindNodes(oldToNew, directededge.EndNode);
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

                // if (!nodeb.IsValid()) LOG_ERROR("Invalid end node - not found in old_to_new map");
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
                    // if (signs.size() == 0) LOG_ERROR("Base edge should have signs, but none found");
                    tilebuilder.AddSigns((uint)tilebuilder.DirectedEdges.Count, signs);
                }

                // Get turn lanes from the base directed edge.
                if (directededge.TurnLanes)
                {
                    uint offset = tile.TurnLanesOffset(baseEdgeId.Id());
                    tilebuilder.AddTurnLanes((uint)tilebuilder.DirectedEdges.Count, tile.GetName(offset));
                }

                // Get access restrictions from the base directed edge. Add these to the list of access
                // restrictions in the new tile. Update the edge index in the restriction to be the
                // current directed edge Id.
                if (directededge.AccessRestriction != 0)
                {
                    (IReadOnlyList<AccessRestriction> restrictions, _) = tile.GetAccessRestrictions(baseEdgeId.Id());
                    foreach (AccessRestriction res in restrictions)
                    {
                        tilebuilder.AddAccessRestriction(new AccessRestriction(
                            (uint)tilebuilder.DirectedEdges.Count, res.Type(), res.Modes(), res.Value(),
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

                // Get edge info, shape, and names from the old tile and add to the new. Cannot use the
                // edge info offset since edges in arterial and highway hierarchy can cross base tiles!
                // Use a hash based on the encoded shape plus way Id.
                EdgeInfo edgeinfo = tile.EdgeInfo(directededge);
                string encodedShape = edgeinfo.EncodedShape();
                uint w = Hash(encodedShape + edgeinfo.WayId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                uint edgeInfoOffset = tilebuilder.AddEdgeInfo(
                    w, nodea, nodeb, edgeinfo.WayId, edgeinfo.MeanElevation, edgeinfo.BikeNetwork,
                    edgeinfo.SpeedLimit, Encoded.Decode7(encodedShape), edgeinfo.GetNames(),
                    edgeinfo.GetTaggedValues(), edgeinfo.GetLinguisticTaggedValues(), edgeinfo.GetTypes(),
                    out _, diffNames);

                newedge.SetEdgeInfoOffset(edgeInfoOffset);

                // reset shortcuts after hijacking them for reclassification.
                newedge.SetHierarchyRoadClass(RoadClass.Motorway, true);

                // Add directed edge.
                tilebuilder.DirectedEdges.Add(newedge);
            }

            // Add node transitions.
            uint index = (uint)tilebuilder.Transitions.Count;
            OldToNewNodes assoc = FindNodes(oldToNew, baseNode);
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
                // if (signs.size() == 0) LOG_ERROR("Base node should have signs, but none found");
                node.SetNamedIntersection(true);
                tilebuilder.AddSigns((uint)tilebuilder.Nodes.Count, signs);
            }

            // The node was mutated locally (C++ mutates nodes().back()); append it now.
            tilebuilder.Nodes.Add(node);
        }

        // Store the final tile.
        tilebuilder?.StoreTileData(reader.TileDir());
    }

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
