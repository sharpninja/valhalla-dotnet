// C# port of Valhalla mjolnir graphbuilder.h + graphbuilder.cc, qualified against
// Valhalla 3.8.3 commit a60c7cbfc83e073f50887cd27e0109d02e6b64e5.
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/graphbuilder.h
//   F:/github/valhalla/src/mjolnir/graphbuilder.cc
//   (+ node_expander, directededgebuilder, edgeinfobuilder, graphtilebuilder for the helpers)
//
// Constructs the initial routing graph from OSMData (the output of PBFGraphParser):
//   1. ConstructEdges  - walk the way_nodes, emitting graph Edge records (split at intersections /
//                        way ends / doubled-back loops) and Node records (start_of / end_of), tiling
//                        nodes by GraphId (and grid id within the tile).
//   2. SortGraph       - sort nodes by graphid+grid+osmid, assign per-tile node ids, and wire each
//                        edge's source/target node index. Returns the tile -> first-node-index map.
//   3. Build           - for each tile, bundle the duplicate nodes + their edges (collect_node_edges),
//                        build DirectedEdges + EdgeInfo, signs, simple turn restrictions, access
//                        restrictions, and write a byte-compatible baldr tile.
//
// Ancillary transit, bike-share, elevation, admin, timezone, statistics, and historical-speed
// generation remain separate pipeline stages. This road-graph stage now emits Valhalla 3.8.3 way
// names, route references, languages, pronunciations, and linguistic tagged values. Node/sign
// linguistic attribution remains a distinct graph-builder surface.
//
// PORT-NOTE: the C++ build spills ways / way_nodes / nodes / edges to mmapped midgard::sequence
// temp files and runs BuildTileSet on a thread pool. This on-device port keeps everything in
// managed lists and runs single-threaded; every algorithm (edge splitting, node sorting / id
// assignment, edge wiring, node bundling, directed-edge / edge-info construction, fork detection,
// simple turn restrictions, access restrictions) is preserved exactly.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Builds the initial routing graph (baldr tiles) from a parsed <see cref="OSMData"/>. Faithful
/// port of the C++ <c>class GraphBuilder</c> + the graphbuilder.cc free functions.
/// </summary>
public sealed class GraphBuilder
{
    /// <summary>
    /// The intermediate graph produced by <see cref="ConstructEdges"/> / <see cref="BuildEdges"/>:
    /// the edge and node lists plus the tile -> first-node-index map. This is the representation the
    /// enhancer / filter / tile writer consume.
    /// </summary>
    public sealed class Graph
    {
        /// <summary>The edges of the temporary graph (indexed by edge index).</summary>
        public List<Edge> Edges { get; } = new();

        /// <summary>The nodes of the temporary graph (sorted by graphid+grid+osmid).</summary>
        public List<Node> Nodes { get; } = new();

        /// <summary>
        /// Map of tile GraphId (tile base) to the index of the first node in that tile within
        /// <see cref="Nodes"/>. Equivalent to the C++ <c>std::map&lt;GraphId, size_t&gt;</c>.
        /// </summary>
        public SortedDictionary<GraphId, int> Tiles { get; } = new();
    }

    /// <summary>
    /// Returns the grid Id within the tile. A tile is subdivided into an nxn grid; the grid id is
    /// used to sort nodes spatially. Faithful port of the free function <c>GetGridId</c>.
    /// </summary>
    public static uint GetGridId(OSMNode node, Tiles<PointLL, double> tiling, uint gridDivisions)
    {
        // By default grid_divisions is 0 to indicate no spatial sorting within a tile.
        if (gridDivisions == 0)
        {
            return 0;
        }

        int tileId = tiling.TileId(node.LatLng());
        if (tileId >= 0)
        {
            PointLL baseLl = tiling.Base(tileId);
            double gridSize = tiling.TileSize() / gridDivisions;
            uint row = (uint)((node.LatLng().Lat - baseLl.Lat) / gridSize);
            uint col = (uint)((node.LatLng().Lng - baseLl.Lng) / gridSize);
            if (row > gridDivisions || col > gridDivisions)
            {
                return 0;
            }

            return (row * gridDivisions) + col;
        }

        return 0;
    }

    /// <summary>
    /// Constructs the graph edges from the parsed ways/way_nodes and assigns nodes to tiles, then
    /// sorts the graph. Faithful port of <c>GraphBuilder::BuildEdges</c>.
    /// </summary>
    /// <param name="ways">Parsed OSM ways.</param>
    /// <param name="wayNodes">Parsed way-node references (the way shape with intersections marked).</param>
    /// <param name="gridDivisions">nxn grid divisions for spatial node sorting (0 disables).</param>
    /// <param name="inferTurnChannels">Whether turn channels are being inferred.</param>
    /// <returns>The intermediate <see cref="Graph"/>.</returns>
    public static Graph BuildEdges(
        IReadOnlyList<OSMWay> ways,
        IReadOnlyList<OSMWayNode> wayNodes,
        uint gridDivisions = 0,
        bool inferTurnChannels = true)
    {
        byte level = TileHierarchy.Levels()[^1].Level;
        Tiles<PointLL, double> tiling = TileHierarchy.GetTiling(level);

        var graph = new Graph();

        ConstructEdges(
            ways,
            wayNodes,
            graph.Nodes,
            graph.Edges,
            node => TileHierarchy.GetGraphId(node.LatLng(), level),
            node => GetGridId(node, tiling, gridDivisions),
            inferTurnChannels);

        SortGraph(graph);
        return graph;
    }

    /// <summary>
    /// Constructs edges in the graph and assigns nodes to tiles. Faithful port of the free function
    /// <c>ConstructEdges</c>.
    /// </summary>
    public static void ConstructEdges(
        IReadOnlyList<OSMWay> ways,
        IReadOnlyList<OSMWayNode> wayNodes,
        List<Node> nodes,
        List<Edge> edges,
        Func<OSMNode, GraphId> graphIdPredicate,
        Func<OSMNode, uint> gridIdPredicate,
        bool inferTurnChannels)
    {
        // Method to get length of an edge (used to find short link edges).
        double Length(uint idx1, OSMNode node2) => wayNodes[(int)idx1].Node.LatLng().Distance(node2.LatLng());

        int currentWayNodeIndex = 0;
        while (currentWayNodeIndex < wayNodes.Count)
        {
            // Grab the way and its first node.
            OSMWayNode wayNode = wayNodes[currentWayNodeIndex];
            OSMWay way = ways[(int)wayNode.WayIndex];
            int firstWayNodeIndex = currentWayNodeIndex;
            int lastWayNodeIndex =
                (int)(firstWayNodeIndex + way.NodeCount() - wayNode.WayShapeNodeIndex - 1);

            // Valhalla 3.8.3 retains pedestrian-area rings for the dedicated area pass, but those
            // boundary ways must never create ordinary graph edges.
            if (way.Area())
            {
                currentWayNodeIndex = lastWayNodeIndex + 1;
                continue;
            }

            // Validate - make sure all nodes for this edge are valid.
            bool valid = true;
            for (int ni = currentWayNodeIndex; ni <= lastWayNodeIndex; ni++)
            {
                OSMNode wn = wayNodes[ni].Node;
                if (!wn.LatLng().IsValid())
                {
                    valid = false;
                }
            }

            if (!valid)
            {
                currentWayNodeIndex = lastWayNodeIndex + 1;
                continue;
            }

            // Remember this edge starts here.
            Edge prevEdge = default;
            bool prevValid = false;
            Edge edge = Edge.MakeEdge(wayNode.WayIndex, (uint)currentWayNodeIndex, way, inferTurnChannels);
            edge.Attributes.WayBegin = wayNode.WayShapeNodeIndex == 0;

            // Remember this node as starting this edge.
            OSMNode startNode = wayNode.Node;
            startNode.SetLinkEdge(way.Link());
            startNode.SetNonLinkEdge(!way.Link() && (way.AutoForward() || way.AutoBackward()));
            nodes.Add(new Node
            {
                OsmNode = startNode,
                StartOf = (uint)edges.Count,
                EndOf = Node.NoEdge,
                GraphId = graphIdPredicate(startNode),
                GridId = gridIdPredicate(startNode),
            });

            // Iterate through the nodes of the way until we find an intersection.
            while (currentWayNodeIndex < wayNodes.Count)
            {
                // Get the next shape point on this edge.
                wayNode = wayNodes[++currentWayNodeIndex];
                edge.Attributes.LlCount++;

                // If it's an intersection or the end of the way it's a node of the road network graph.
                if (wayNode.Node.Intersection())
                {
                    // Finish off this edge.
                    edge.Attributes.ShortLink =
                        way.Link() && Length(edge.LlIndex, wayNode.Node) < GraphConstants.MaxInternalLength;
                    OSMNode endNode = wayNode.Node;
                    endNode.SetLinkEdge(way.Link());
                    endNode.SetNonLinkEdge(!way.Link() && (way.AutoForward() || way.AutoBackward()));

                    // Remember what edge this node will end (delayed add: + prev edge if valid).
                    uint endOf = (uint)(edges.Count + (prevValid ? 1 : 0));
                    nodes.Add(new Node
                    {
                        OsmNode = endNode,
                        StartOf = Node.NoEdge,
                        EndOf = endOf,
                        GraphId = graphIdPredicate(endNode),
                        GridId = gridIdPredicate(endNode),
                    });

                    // Mark the edge as ending a way if this is the last node in the way.
                    edge.Attributes.WayEnd = currentWayNodeIndex == lastWayNodeIndex;

                    // We should add the previous edge now that we know it's done.
                    if (prevValid)
                    {
                        edges.Add(prevEdge);
                    }

                    // Finish this edge.
                    prevEdge = edge;
                    prevValid = true;

                    // Figure out if the way doubled back on itself; if so treat it like the way ended.
                    bool doubledBack = false;
                    while (currentWayNodeIndex != lastWayNodeIndex && wayNode.Node.FlatLoop() &&
                           wayNodes[currentWayNodeIndex + 1].Node.FlatLoop())
                    {
                        wayNode = wayNodes[currentWayNodeIndex + 1];
                        ++currentWayNodeIndex;
                        doubledBack = currentWayNodeIndex != lastWayNodeIndex;
                    }

                    // Either we were done making edges from this way, or there is an internal part that
                    // is doubled back over itself and we need to skip it.
                    if (currentWayNodeIndex == lastWayNodeIndex || doubledBack)
                    {
                        edges.Add(prevEdge);
                        currentWayNodeIndex += doubledBack ? 0 : 1;
                        break;
                    }

                    // Start a new edge if this is not the last node in the way.
                    edge = Edge.MakeEdge(wayNode.WayIndex, (uint)currentWayNodeIndex, way, inferTurnChannels);
                    // The just-added end node now also starts the new edge.
                    Node n = nodes[^1];
                    n.StartOf = (uint)(edges.Count + 1); // + 1 because the edge has not been added yet
                    nodes[^1] = n;
                }
                else if (wayNode.Node.TrafficSignal())
                {
                    // If this edge has a signal not at an intersection.
                    edge.Attributes.TrafficSignal = true;
                    edge.Attributes.ForwardSignal = wayNode.Node.ForwardSignal();
                    edge.Attributes.BackwardSignal = wayNode.Node.BackwardSignal();
                }
                else if (wayNode.Node.StopSign())
                {
                    edge.Attributes.StopSign = true;
                    edge.Attributes.Direction = wayNode.Node.Direction();
                    edge.Attributes.ForwardStop = wayNode.Node.ForwardStop();
                    edge.Attributes.BackwardStop = wayNode.Node.BackwardStop();
                }
                else if (wayNode.Node.YieldSign())
                {
                    edge.Attributes.YieldSign = true;
                    edge.Attributes.Direction = wayNode.Node.Direction();
                    edge.Attributes.ForwardYield = wayNode.Node.ForwardYield();
                    edge.Attributes.BackwardYield = wayNode.Node.BackwardYield();
                }
            }
        }
    }

    /// <summary>
    /// Sorts the graph nodes by graphid then grid then osmid, assigns per-tile node ids, builds the
    /// tile -> first-node-index map, and wires each edge's source/target node index. Faithful port
    /// of the free function <c>SortGraph</c>.
    /// </summary>
    public static void SortGraph(Graph graph)
    {
        List<Node> nodes = graph.Nodes;
        List<Edge> edges = graph.Edges;

        // Sort nodes by graphid then by grid within the tile, then by osmid.
        nodes.Sort((a, b) =>
        {
            if (a.GraphId == b.GraphId)
            {
                if (a.GridId == b.GridId)
                {
                    return a.OsmNode.Osmid.CompareTo(b.OsmNode.Osmid);
                }

                return a.GridId.CompareTo(b.GridId);
            }

            return a.GraphId.CompareTo(b.GraphId);
        });

        // Run through the sorted nodes, going back to the edges they reference and updating each edge
        // to point to the first (out of the duplicates) node's index.
        var starts = new List<(uint Edge, uint Node)>();
        var ends = new List<(uint Edge, uint Node)>();

        uint runIndex = 0;
        Node lastNode = default;
        graph.Tiles.Clear();
        GraphId lastTile = default;
        bool haveTile = false;

        for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            Node node = nodes[nodeIndex];

            // Remember if this was a new tile.
            if (nodeIndex == 0 || node.GraphId != lastTile)
            {
                graph.Tiles[node.GraphId] = nodeIndex;
                lastTile = node.GraphId;
                haveTile = true;
                node.GraphId.SetId(0);
                runIndex = (uint)nodeIndex;
            }
            else if (lastNode.OsmNode.Osmid != node.OsmNode.Osmid)
            {
                // It's a new node.
                node.GraphId.SetId(lastNode.GraphId.Id() + 1);
                runIndex = (uint)nodeIndex;
            }
            else
            {
                // Not new - keep the same graphid.
                node.GraphId.SetId(lastNode.GraphId.Id());
            }

            // If this node marks the start/end of an edge, keep track so we can later wire the edge.
            if (node.IsStart())
            {
                starts.Add((node.StartOf, runIndex));
            }

            if (node.IsEnd())
            {
                ends.Add((node.EndOf, runIndex));
            }

            nodes[nodeIndex] = node;
            lastNode = node;
        }

        _ = haveTile;

        // Sort by edge. This enables a sequential update of edges.
        starts.Sort((a, b) => a.Edge.CompareTo(b.Edge));
        ends.Sort((a, b) => a.Edge.CompareTo(b.Edge));

        int si = 0;
        int ei = 0;
        while (si < starts.Count && ei < ends.Count)
        {
            // There should be exactly one edge per begin and end node; they were sorted the same.
            Edge edge = edges[(int)starts[si].Edge];
            edge.SourceNode = starts[si].Node;
            edge.TargetNode = ends[ei].Node;
            edges[(int)starts[si].Edge] = edge;
            ++si;
            ++ei;
        }
    }

    /// <summary>
    /// Handles simple turn restrictions that originate from a directed edge. Returns the restriction
    /// mask (bit set per restricted local edge index). Faithful port of the free function
    /// <c>CreateSimpleTurnRestriction</c>.
    /// </summary>
    public static uint CreateSimpleTurnRestriction(
        ulong wayid,
        int endnode,
        IReadOnlyList<Node> nodes,
        IReadOnlyList<Edge> edges,
        OSMData osmdata,
        IReadOnlyList<OSMWay> ways)
    {
        IReadOnlyList<OSMRestriction> res = osmdata.RestrictionsFor(wayid);
        if (res.Count == 0)
        {
            return 0;
        }

        // Find all TRs (if any) through the target (end) node of this directed edge.
        Node node = nodes[endnode];
        var trs = new List<OSMRestriction>();
        foreach (OSMRestriction r in res)
        {
            if (r.Via() == node.OsmNode.Osmid)
            {
                trs.Add(r);
            }
        }

        if (trs.Count == 0)
        {
            return 0;
        }

        // Get the way Ids of the edges at the endnode.
        NodeBundle bundle = NodeExpander.CollectNodeEdges(endnode, nodes, edges);
        var wayids = new List<ulong>(bundle.NodeEdges.Count);
        foreach (KeyValuePair<Edge, ulong> e in bundle.NodeEdges)
        {
            wayids.Add(ways[(int)e.Key.WayIndex].OsmWayId);
        }

        // Iterate through all restrictions; set the restriction mask to include the restricted turns.
        uint mask = 0;
        foreach (OSMRestriction tr in trs)
        {
            switch (tr.TypeValue())
            {
                case RestrictionType.NoLeftTurn:
                case RestrictionType.NoRightTurn:
                case RestrictionType.NoStraightOn:
                case RestrictionType.NoUTurn:
                case RestrictionType.NoEntry:
                case RestrictionType.NoExit:
                case RestrictionType.NoTurn:
                    for (int idx = 0; idx < wayids.Count; idx++)
                    {
                        if (wayids[idx] == tr.To())
                        {
                            mask |= 1u << idx;
                            break;
                        }
                    }

                    break;

                case RestrictionType.OnlyRightTurn:
                case RestrictionType.OnlyLeftTurn:
                case RestrictionType.OnlyStraightOn:
                    for (int idx = 0; idx < wayids.Count; idx++)
                    {
                        if (wayids[idx] != tr.To())
                        {
                            mask |= 1u << idx;
                        }
                    }

                    break;

                case RestrictionType.OnlyProbable:
                case RestrictionType.NoProbable:
                    break;
            }
        }

        return mask;
    }

    /// <summary>
    /// Adds access restrictions for an edge. Returns the mode(s) that have access restrictions on
    /// this edge. Faithful port of the free function <c>AddAccessRestrictions</c>.
    /// </summary>
    public static uint AddAccessRestrictions(
        uint edgeid,
        ulong wayid,
        OSMData osmdata,
        bool forward,
        GraphTileBuilder graphtile)
    {
        if (!osmdata.AccessRestrictions.TryGetValue(wayid, out List<OSMAccessRestriction>? res))
        {
            return 0;
        }

        uint modes = 0;
        foreach (OSMAccessRestriction r in res)
        {
            AccessRestrictionDirection direction = r.Direction();
            if (direction == AccessRestrictionDirection.Both ||
                (forward && direction == AccessRestrictionDirection.Forward) ||
                (!forward && direction == AccessRestrictionDirection.Backward))
            {
                var accessRestriction = new AccessRestriction(
                    edgeid, r.TypeValue(), r.Modes(), r.Value(), r.ExceptDestination());
                graphtile.AddAccessRestriction(accessRestriction);
                modes |= r.Modes();
            }
        }

        return modes;
    }

    /// <summary>
    /// Computes speeds (kph) for ferries that have a "duration" tag, keyed by way id. Faithful port
    /// of the free function <c>ComputeFerrySpeeds</c>.
    /// </summary>
    public static Dictionary<ulong, uint> ComputeFerrySpeeds(
        IReadOnlyList<OSMWay> ways,
        IReadOnlyList<OSMWayNode> wayNodes)
    {
        var ferrySpeeds = new Dictionary<ulong, uint>();
        int wayNodeIndex = 0;
        for (int wayIndex = 0; wayIndex < ways.Count; ++wayIndex)
        {
            OSMWay way = ways[wayIndex];
            if (way.Ferry() && way.Duration != 0)
            {
                PointLL? prev = null;
                while (wayNodeIndex < wayNodes.Count)
                {
                    OSMWayNode wayNode = wayNodes[wayNodeIndex];
                    if (wayNode.WayIndex < wayIndex)
                    {
                        // Ferries are rare; jump by more than 1 (at least 2 nodes per way).
                        uint currentWayIndex = wayNodes[wayNodeIndex].WayIndex;
                        wayNodeIndex += (int)(((wayIndex - currentWayIndex - 1) * 2) + 1);
                    }
                    else
                    {
                        prev = wayNode.Node.LatLng();
                        wayNodeIndex += 1;
                        break;
                    }
                }

                double length = 0.0;
                while (wayNodeIndex < wayNodes.Count)
                {
                    OSMWayNode wayNode = wayNodes[wayNodeIndex];
                    if (wayNode.WayIndex != wayIndex)
                    {
                        break;
                    }

                    PointLL curr = wayNode.Node.LatLng();
                    // prev is set below once the first matching wayNode.WayIndex is found; every way
                    // has at least 2 nodes (ferries are rare but never degenerate), so it is non-null
                    // by the time this second loop runs.
                    length += prev!.Distance(curr);
                    prev = curr;
                    wayNodeIndex += 1;
                }

                uint speed = (uint)((length * 3.6) / way.Duration);
                ferrySpeeds[way.WayId()] = speed == 0 ? 1u : speed;
            }
        }

        return ferrySpeeds;
    }

    /// <summary>
    /// Builds tiles for the local graph hierarchy from the intermediate graph and writes
    /// byte-compatible baldr tile blobs. Faithful port of <c>BuildLocalTiles</c> / the build path of
    /// <c>BuildTileSet</c> (single-threaded, no admin/tz DB, transit/elevation excluded).
    /// </summary>
    /// <param name="osmdata">The parsed OSM data.</param>
    /// <param name="ways">Parsed OSM ways.</param>
    /// <param name="wayNodes">Parsed way-node references.</param>
    /// <param name="graph">The intermediate graph (from <see cref="BuildEdges"/>).</param>
    /// <param name="tileCreationDate">Days from pivot date for the tile creation date.</param>
    /// <returns>A map of tile GraphId (tile base) to the serialized tile blob bytes.</returns>
    public static Dictionary<GraphId, byte[]> Build(
        OSMData osmdata,
        IReadOnlyList<OSMWay> ways,
        IReadOnlyList<OSMWayNode> wayNodes,
        Graph graph,
        uint tileCreationDate = 0) =>
        Build(
            osmdata,
            ways,
            wayNodes,
            graph,
            tileCreationDate,
            maxDegreeOfParallelism: 1,
            CancellationToken.None);

    /// <summary>
    /// Builds local graph tiles with bounded parallel tile construction after global indexes freeze.
    /// </summary>
    public static Dictionary<GraphId, byte[]> Build(
        OSMData osmdata,
        IReadOnlyList<OSMWay> ways,
        IReadOnlyList<OSMWayNode> wayNodes,
        Graph graph,
        uint tileCreationDate,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(osmdata);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<ulong, uint> ferrySpeeds = ComputeFerrySpeeds(ways, wayNodes);
        List<Node> nodes = graph.Nodes;
        List<Edge> edges = graph.Edges;

        Tiles<PointLL, double> tiling = TileHierarchy.Levels()[^1].Tiles;
        var parallelResult = new ConcurrentDictionary<GraphId, byte[]>();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
        };

        Parallel.ForEach(
            graph.Tiles,
            parallelOptions,
            tile =>
        {
            GraphId tileId = tile.Key.TileBase();
            var graphtile = new GraphTileBuilder(tileId);

            graphtile.AddTileCreationDate(tileCreationDate);
            graphtile.HeaderBuilder.SetDatasetId(osmdata.MaxChangesetId);

            // Valhalla 3.8.3 hashes the serialized tile body, then stamps one build ID after all
            // tiles have been serialized. The former PBF-wide checksum is not a tile checksum.
            // Set the base lat,lon of the tile.
            uint id = tileId.Tileid();
            PointLL baseLl = tiling.Base((int)id);
            graphtile.HeaderBuilder.SetBaseLl(baseLl);

            uint idx = 0; // current directed edge index

            // Cache of edge_info_offset -> (length, curvature).
            var geoAttributeCache = new Dictionary<uint, (double Length, uint Curvature)>();

            int nodeItr = tile.Value;
            while (nodeItr < nodes.Count && nodes[nodeItr].GraphId.TileBase() == tileId)
            {
                if ((nodeItr & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                NodeBundle bundle = NodeExpander.CollectNodeEdges(nodeItr, nodes, edges);

                if (bundle.NodeEdges.Count == 0)
                {
                    // Node has no edges - skip (advance by the duplicate count, min 1).
                    nodeItr += Math.Max(1, bundle.NodeCount);
                    continue;
                }

                OSMNode node = bundle.Node.OsmNode;
                PointLL nodeLl = node.LatLng();

                // No admin db: drive-on-right is taken per-way below.
                const uint adminIndex = 0;
                bool dor = false;

                // Fork detection (see graphbuilder.cc).
                bool fork =
                    bundle.NodeEdges.Count > 2 && bundle.DriveForwardCount > 1 &&
                    (bundle.LinkCount == bundle.NodeEdges.Count ||
                     (node.Type() == NodeType.MotorWayJunction &&
                      (bundle.LinkCount == 0 ||
                       (bundle.LinkCount == bundle.DriveForwardCount &&
                        bundle.NodeEdges.Count == bundle.LinkCount + 1))));

                uint n = 0;
                foreach (KeyValuePair<Edge, ulong> edgePair in bundle.NodeEdges)
                {
                    Edge edge = edgePair.Key;
                    OSMWay w = ways[(int)edge.WayIndex];

                    // Determine orientation along the edge.
                    bool forward = edge.SourceNode == nodeItr;
                    int source = (int)edge.SourceNode;
                    int target = (int)edge.TargetNode;
                    if (!forward)
                    {
                        (source, target) = (target, source);
                    }

                    // No admin db: use the way's drive_on_right.
                    dor = w.DriveOnRight();

                    uint speed = w.Speed();
                    if (forward && w.ForwardTaggedSpeed())
                    {
                        speed = w.ForwardSpeed();
                    }
                    else if (!forward && w.BackwardTaggedSpeed())
                    {
                        speed = w.BackwardSpeed();
                    }

                    uint speedLimit = w.SpeedLimit();

                    byte directedTruckSpeed = forward ? w.TruckSpeedForward() : w.TruckSpeedBackward();

                    uint truckSpeed = w.TruckSpeed() != 0 && directedTruckSpeed != 0
                        ? Math.Min(w.TruckSpeed(), directedTruckSpeed)
                        : Math.Max(w.TruckSpeed(), directedTruckSpeed);

                    Use use = w.UseValue();

                    // Handle simple turn restrictions from this directed edge.
                    uint restrictions =
                        CreateSimpleTurnRestriction(w.WayId(), target, nodes, edges, osmdata, ways);

                    bool hasSignal =
                        edge.Attributes.TrafficSignal &&
                        ((forward && edge.Attributes.ForwardSignal) ||
                         (!forward && edge.Attributes.BackwardSignal) ||
                         (w.Oneway() && !edge.Attributes.ForwardSignal && !edge.Attributes.BackwardSignal));

                    bool hasStop =
                        edge.Attributes.StopSign &&
                        ((forward && (edge.Attributes.Direction ? edge.Attributes.ForwardStop : true)) ||
                         (!forward && (edge.Attributes.Direction ? edge.Attributes.BackwardStop : true)) ||
                         (w.Oneway() && !edge.Attributes.ForwardStop && !edge.Attributes.BackwardStop));

                    bool hasYield =
                        edge.Attributes.YieldSign &&
                        ((forward && (edge.Attributes.Direction ? edge.Attributes.ForwardYield : true)) ||
                         (!forward && (edge.Attributes.Direction ? edge.Attributes.BackwardYield : true)) ||
                         (w.Oneway() && !edge.Attributes.ForwardYield && !edge.Attributes.BackwardYield));

                    // Bike network mask from relations.
                    uint bikeNetwork = 0;
                    foreach (OSMBike b in osmdata.BikeRelationsFor(w.WayId()))
                    {
                        if ((b.BikeNetwork & GraphConstants.Mcn) != 0)
                        {
                            bikeNetwork |= GraphConstants.Mcn;
                        }
                        else
                        {
                            bikeNetwork |= b.BikeNetwork;
                        }
                    }

                    // dual refs: if a way has both forward and reverse refs from relations.
                    bool dualRefs = false;
                    string refStr = string.Empty;
                    if (w.RefIndex != 0)
                    {
                        bool hasFwd = osmdata.WayRef.TryGetValue(w.WayId(), out uint fwdRef);
                        bool hasRev = osmdata.WayRefRev.TryGetValue(w.WayId(), out uint revRef);
                        dualRefs = hasFwd && hasRev;

                        if (dualRefs && !forward)
                        {
                            if (hasRev)
                            {
                                refStr = GetRef(
                                    osmdata.NameOffsetMap.Name(w.RefIndex),
                                    osmdata.NameOffsetMap.Name(revRef));
                            }
                        }
                        else if (hasFwd)
                        {
                            refStr = GetRef(
                                osmdata.NameOffsetMap.Name(w.RefIndex),
                                osmdata.NameOffsetMap.Name(fwdRef));
                        }
                    }

                    // Get the shape for the edge and compute its length.
                    (double Length, uint Curvature) found;
                    bool haveCache;
                    uint edgeIndexForInfo = edgePair.Value > uint.MaxValue ? 0u : (uint)edgePair.Value;

                    // diff/dual conditions that always force a new EdgeInfo, OR no existing edge info.
                    bool forcedNew =
                        (w.RefLeftIndex != 0 && w.RefRightIndex != 0) ||
                        (w.NameLeftIndex != 0 && w.NameRightIndex != 0) ||
                        (w.OfficialNameLeftIndex != 0 && w.OfficialNameRightIndex != 0) ||
                        (w.AltNameLeftIndex != 0 && w.AltNameRightIndex != 0) ||
                        (w.TunnelNameLeftIndex != 0 && w.TunnelNameRightIndex != 0) ||
                        dualRefs ||
                        (w.NameForwardIndex != 0 && w.NameBackwardIndex != 0);

                    bool haveExisting = graphtile.HasEdgeInfo(
                        edgeIndexForInfo, nodes[source].GraphId, nodes[target].GraphId, out uint edgeInfoOffset);

                    bool needNewEdgeInfo = forcedNew || !haveExisting;

                    if (needNewEdgeInfo)
                    {
                        // Collect the shape from the way_nodes.
                        var shape = new List<PointLL>((int)edge.Attributes.LlCount);
                        for (int i = 0; i < edge.Attributes.LlCount; i++)
                        {
                            shape.Add(wayNodes[(int)edge.LlIndex + i].Node.LatLng());
                        }

                        OSMWayNameData nameData =
                            OSMWayLinguisticBuilder.Build(w, refStr, osmdata.NameOffsetMap, forward);
                        var taggedValues = new List<string>();

                        if (bikeNetwork != 0)
                        {
                            bikeNetwork |= w.BikeNetwork();
                        }
                        else
                        {
                            bikeNetwork = w.BikeNetwork();
                        }

                        edgeInfoOffset = graphtile.AddEdgeInfo(
                            edgeIndexForInfo,
                            nodes[source].GraphId,
                            nodes[target].GraphId,
                            w.WayId(),
                            GraphConstants.NoElevationData,
                            bikeNetwork,
                            speedLimit,
                            shape,
                            nameData.Names,
                            taggedValues,
                            nameData.Linguistics,
                            nameData.Types,
                            out _,
                            nameData.DiffNames || dualRefs);

                        double length = PointLlPolyline2.Length(shape);
                        uint curvature = ComputeCurvature(shape);

                        found = (length, curvature);
                        geoAttributeCache[edgeInfoOffset] = found;
                        haveCache = true;
                    }
                    else
                    {
                        haveCache = geoAttributeCache.TryGetValue(edgeInfoOffset, out found);
                    }

                    if (!haveCache)
                    {
                        throw new InvalidOperationException("GeoAttributes cached object should be there!");
                    }

                    // Ferry speed override. Duration is set on the way.
                    if (w.Ferry() && w.Duration != 0)
                    {
                        speed = ferrySpeeds[w.WayId()];
                    }

                    // Add a directed edge.
                    DirectedEdge directededge = DirectedEdgeBuilder.Build(
                        w,
                        nodes[target].GraphId,
                        forward,
                        (uint)(found.Length + 0.5),
                        speed,
                        truckSpeed,
                        use,
                        (RoadClass)edge.Attributes.Importance,
                        n,
                        hasSignal,
                        hasStop,
                        hasYield,
                        (hasStop || hasYield) && node.Minor(),
                        restrictions,
                        bikeNetwork,
                        edge.Attributes.ReclassFerry,
                        (RoadClass)edge.Attributes.ImportanceHierarchy);

                    // Temporarily use leaves_tile to indicate whether to search the access.bin file
                    // (ferries with duration use it to mark that the speed was set via duration+length).
                    if (!w.Ferry())
                    {
                        directededge.SetLeavesTile(w.HasUserTags());
                    }
                    else if (w.Duration != 0)
                    {
                        directededge.SetLeavesTile(true);
                    }

                    directededge.SetEdgeInfoOffset(edgeInfoOffset);
                    directededge.SetCurvature(found.Curvature);

                    // Set use to ramp or turn channel.
                    if (edge.Attributes.TurnChannel && use != Use.Construction)
                    {
                        directededge.SetUse(Use.TurnChannel);
                    }
                    else if (edge.Attributes.Link &&
                             use != Use.ServiceArea && use != Use.RestArea && use != Use.Construction)
                    {
                        directededge.SetUse(Use.Ramp);
                    }

                    if (w.Internal())
                    {
                        if (directededge.Use != Use.Ramp && directededge.Use != Use.TurnChannel)
                        {
                            directededge.SetInternal(true);
                        }
                    }

                    // Signs for this directed edge.
                    var signs = new List<SignInfo>();
                    var signLinguistics = new List<string>();
                    bool hasGuide = CreateSignInfoList(
                        node, w, osmdata, signs, signLinguistics, fork, forward,
                        directededge.Use == Use.Ramp, directededge.Use == Use.TurnChannel);

                    if (signs.Count > 0 && (directededge.ForwardAccess & GraphConstants.AutoAccess) != 0 &&
                        ((directededge.Link &&
                          !(bundle.LinkCount == 2 && bundle.DriveForwardCount == 1)) ||
                         fork || hasGuide))
                    {
                        graphtile.AddSigns(idx, signs, signLinguistics);
                        directededge.SetSign(true);
                    }

                    // Turn lanes.
                    if (forward && w.FwdTurnLanesIndex > 0)
                    {
                        string turnlaneTags = osmdata.NameOffsetMap.Name(w.FwdTurnLanesIndex);
                        if (!string.IsNullOrEmpty(turnlaneTags))
                        {
                            string str = TurnLanes.GetTurnLaneString(turnlaneTags);
                            if (!string.IsNullOrEmpty(str))
                            {
                                directededge.SetTurnLanes(true);
                                // C++ graphbuilder.cc:1039 stores the OSM name_offset_map index and the
                                // enhancer re-reads it from osmdata.name_offset_map.name(index). This C#
                                // port does not thread OSMData into the enhancer, so store the raw OSM
                                // turn-lane tag string into the tile text list instead (see
                                // GraphEnhancerTileModel.NameAt, which resolves the turn-lane offset
                                // against the tile text list). GetTurnLaneString is re-applied by the
                                // enhancer, so the unconverted tag string must be persisted.
                                graphtile.AddTurnLanes(idx, turnlaneTags);
                            }
                        }
                    }
                    else if (!forward && w.BwdTurnLanesIndex > 0)
                    {
                        string turnlaneTags = osmdata.NameOffsetMap.Name(w.BwdTurnLanesIndex);
                        if (!string.IsNullOrEmpty(turnlaneTags))
                        {
                            string str = TurnLanes.GetTurnLaneString(turnlaneTags);
                            if (!string.IsNullOrEmpty(str))
                            {
                                directededge.SetTurnLanes(true);
                                // See forward branch above: store the raw OSM turn-lane tags into the
                                // tile text list so the enhancer's NameAt resolves them without OSMData.
                                graphtile.AddTurnLanes(idx, turnlaneTags);
                            }
                        }
                    }

                    // Lane connectivity.
                    if (osmdata.LaneConnectivityMap.TryGetValue(w.WayId(), out List<OSMLaneConnectivity>? lcs) &&
                        lcs.Count > 0)
                    {
                        try
                        {
                            var v = new List<LaneConnectivity>();
                            foreach (OSMLaneConnectivity lc in lcs)
                            {
                                v.Add(new LaneConnectivity(
                                    idx,
                                    lc.FromWayId,
                                    osmdata.NameOffsetMap.Name(lc.ToLanesIndex),
                                    osmdata.NameOffsetMap.Name(lc.FromLanesIndex)));
                            }

                            graphtile.AddLaneConnectivity(v);
                            directededge.SetLaneConnectivity(true);
                        }
                        catch
                        {
                            // Failed to import lane connectivity for the way; skip (matches C++).
                        }
                    }

                    // Set the number of lanes.
                    if (w.ForwardTaggedLanes() && w.BackwardTaggedLanes())
                    {
                        directededge.SetLaneCount(forward ? w.ForwardLanes() : w.BackwardLanes());
                    }
                    else if (w.Oneway() || w.OnewayReverse())
                    {
                        directededge.SetLaneCount(w.Lanes());
                    }
                    else
                    {
                        directededge.SetLaneCount((uint)Math.Max(1, (int)w.Lanes() / 2));
                    }

                    // Access restrictions (for trucks).
                    if (directededge.ForwardAccess != 0)
                    {
                        uint arModes = AddAccessRestrictions(idx, w.WayId(), osmdata, directededge.Forward, graphtile);
                        if (arModes != 0)
                        {
                            directededge.SetAccessRestriction(arModes);
                        }
                    }

                    if (osmdata.ViaSet.Contains(w.WayId()))
                    {
                        directededge.SetComplexRestriction(true);
                    }

                    // Shoulder.
                    if (forward)
                    {
                        directededge.SetShoulder(dor ? w.ShoulderRight() : w.ShoulderLeft());
                    }
                    else
                    {
                        directededge.SetShoulder(dor ? w.ShoulderLeft() : w.ShoulderRight());
                    }

                    // Cycle lanes.
                    bool rightCyclelaneForward = true;
                    bool leftCyclelaneForward = false;
                    if (w.BikeForward() && !w.BikeBackward())
                    {
                        rightCyclelaneForward = true;
                        leftCyclelaneForward = true;
                    }
                    else if (!w.BikeForward() && w.BikeBackward())
                    {
                        rightCyclelaneForward = false;
                        leftCyclelaneForward = false;
                    }
                    else if (w.Oneway())
                    {
                        rightCyclelaneForward = !w.CyclelaneRightOpposite();
                        leftCyclelaneForward = !w.CyclelaneLeftOpposite();
                        if (w.OnewayReverse())
                        {
                            rightCyclelaneForward = !rightCyclelaneForward;
                            leftCyclelaneForward = !leftCyclelaneForward;
                        }
                    }
                    else
                    {
                        rightCyclelaneForward = w.CyclelaneRightOpposite() ? !dor : dor;
                        leftCyclelaneForward = w.CyclelaneLeftOpposite() ? dor : !dor;
                    }

                    directededge.SetCycleLane(CycleLane.None);
                    if (forward)
                    {
                        if (rightCyclelaneForward)
                        {
                            directededge.SetCycleLane(w.CyclelaneRight());
                        }

                        if (leftCyclelaneForward && (byte)w.CyclelaneLeft() > (byte)directededge.CycleLane)
                        {
                            directededge.SetCycleLane(w.CyclelaneLeft());
                        }
                    }
                    else
                    {
                        if (!rightCyclelaneForward)
                        {
                            directededge.SetCycleLane(w.CyclelaneRight());
                        }

                        if (!leftCyclelaneForward && (byte)w.CyclelaneLeft() > (byte)directededge.CycleLane)
                        {
                            directededge.SetCycleLane(w.CyclelaneLeft());
                        }
                    }

                    // Downgrade classification of footways that are not kServiceOther.
                    if ((directededge.Use == Use.Footway || directededge.Use == Use.Steps ||
                         directededge.Use == Use.Sidewalk || directededge.Use == Use.Pedestrian) &&
                        directededge.Classification != RoadClass.ServiceOther)
                    {
                        directededge.SetClassification(RoadClass.ServiceOther);
                    }

                    graphtile.DirectedEdges.Add(directededge);

                    idx++;
                    n++;
                }

                // Set the node lat,lng, edge index/count, etc.
                var nodeInfo = new NodeInfo(
                    baseLl, nodeLl, node.Access(), node.Type(), node.TrafficSignal(),
                    node.TaggedAccess(), node.PrivateAccess(), node.CashOnlyToll());
                nodeInfo.SetEdgeIndex((uint)(graphtile.DirectedEdges.Count - bundle.NodeEdges.Count));
                nodeInfo.SetEdgeCount((uint)bundle.NodeEdges.Count);
                if (fork)
                {
                    nodeInfo.SetIntersection(IntersectionType.Fork);
                }

                nodeInfo.SetAdminIndex((ushort)adminIndex);

                // Temporarily stash stop/yield info in the transition index (enhancer removes it).
                if ((node.StopSign() && !node.YieldSign()) || (!node.StopSign() && node.YieldSign()))
                {
                    uint stopYieldInfo = 0;
                    if (node.Minor())
                    {
                        stopYieldInfo |= Minor;
                    }

                    if (node.StopSign())
                    {
                        stopYieldInfo |= StopSignFlag;
                    }

                    if (node.YieldSign())
                    {
                        stopYieldInfo |= YieldSignFlag;
                    }

                    nodeInfo.SetTransitionIndex(stopYieldInfo);
                }

                nodeInfo.SetDriveOnRight(dor);

                // No tz db: leave timezone at 0.
                graphtile.Nodes.Add(nodeInfo);

                nodeItr += bundle.NodeCount;
            }

            parallelResult[tileId] = graphtile.StoreTileData();
        });

        cancellationToken.ThrowIfCancellationRequested();
        var result = new Dictionary<GraphId, byte[]>(graph.Tiles.Count);
        foreach (GraphId tileId in graph.Tiles.Keys)
        {
            result.Add(tileId.TileBase(), parallelResult[tileId.TileBase()]);
        }

        GraphTileChecksum.StampTilesetBuildId(result.Values.ToArray());
        return result;
    }

    /// <summary>Get highway refs from relations. Faithful port of <c>GraphBuilder::GetRef</c>.</summary>
    public static string GetRef(string wayRef, string relationRef)
    {
        bool found;
        string refs = string.Empty;
        List<string> wayRefs = GetTagTokens(wayRef);          // US 51;I 57
        List<string> refdirs = GetTagTokens(relationRef);     // US 51|north;I 57|north
        foreach (string @ref in wayRefs)
        {
            found = false;
            foreach (string refdir in refdirs)
            {
                List<string> tmp = GetTagTokens(refdir, '|'); // US 51|north
                if (tmp.Count == 2)
                {
                    if (tmp[0] == @ref)
                    {
                        // US 51 == US 51
                        refs = refs.Length != 0 ? refs + ";" + @ref + " " + tmp[1] : @ref + " " + tmp[1];
                        found = true;
                        break;
                    }

                    if (tmp[0].Contains(' ', StringComparison.Ordinal) &&
                        @ref.Contains(' ', StringComparison.Ordinal))
                    {
                        // SR 747 vs OH 747
                        List<string> sign1 = GetTagTokens(tmp[0], ' ');
                        List<string> sign2 = GetTagTokens(@ref, ' ');
                        if (sign1.Count == 2 && sign2.Count == 2 && sign1[1] == sign2[1])
                        {
                            refs = refs.Length != 0 ? refs + ";" + @ref + " " + tmp[1] : @ref + " " + tmp[1];
                            found = true;
                            break;
                        }
                    }
                }
            }

            if (!found)
            {
                // No direction found in relations for this ref.
                refs = refs.Length != 0 ? refs + ";" + @ref : @ref;
            }
        }

        return refs;
    }

    // -- Stop/yield bit flags temporarily stored in the node transition index (graphconstants). --
    private const uint Minor = 1;
    private const uint StopSignFlag = 2;
    private const uint YieldSignFlag = 4;


    /// <summary>
    /// Builds the list of <see cref="SignInfo"/> exits/guides for a directed edge. Faithful port of
    /// the structure of <c>GraphBuilder::CreateSignInfoList</c> for the non-linguistic paths
    /// (pronunciation/language records are out of scope). Returns true if guide signs or guidance
    /// views were added. The signs are stable-sorted by type at the end, as in the C++.
    /// </summary>
    public static bool CreateSignInfoList(
        OSMNode node,
        OSMWay way,
        OSMData osmdata,
        List<SignInfo> exitList,
        List<string> linguistics,
        bool fork,
        bool forward,
        bool ramp,
        bool tc)
    {
        _ = linguistics;
        bool hasGuide = false;
        bool isBranchOrToward = tc || (!ramp && !fork);
        Sign.Type signType = Sign.Type.ExitNumber;

        void AddSignInfo(List<string> refs, Sign.Type type, bool isRouteNumber)
        {
            foreach (string r in refs)
            {
                exitList.Add(new SignInfo(type, isRouteNumber, false, false, 0, 0, r));
            }
        }

        var signNames = new List<string>();

        // NUMBER - exit sign number.
        if (way.JunctionRefIndex != 0)
        {
            signNames = GetTagTokens(osmdata.NameOffsetMap.Name(way.JunctionRefIndex));
        }
        else if (node.HasRef() && !fork && ramp)
        {
            signNames = GetTagTokens(osmdata.NodeNames.Name(node.RefIndex()));
        }

        AddSignInfo(signNames, Sign.Type.ExitNumber, false);
        signNames = new List<string>();

        // BRANCH - guide or exit sign branch refs.
        if (way.DestinationRefIndex != 0)
        {
            signNames = GetTagTokens(osmdata.NameOffsetMap.Name(way.DestinationRefIndex));
            signType = isBranchOrToward ? Sign.Type.GuideBranch : Sign.Type.ExitBranch;
            hasGuide = isBranchOrToward;
        }

        AddSignInfo(signNames, signType, true);
        signNames = new List<string>();

        // Guide or exit sign branch road names.
        if (way.DestinationStreetIndex != 0)
        {
            signNames = GetTagTokens(osmdata.NameOffsetMap.Name(way.DestinationStreetIndex));
            signType = isBranchOrToward ? Sign.Type.GuideBranch : Sign.Type.ExitBranch;
            hasGuide = isBranchOrToward;
        }

        AddSignInfo(signNames, signType, false);
        signNames = new List<string>();

        // TOWARD - guide or exit sign toward refs.
        if (way.DestinationRefToIndex != 0)
        {
            signNames = GetTagTokens(osmdata.NameOffsetMap.Name(way.DestinationRefToIndex));
            signType = isBranchOrToward ? Sign.Type.GuideToward : Sign.Type.ExitToward;
            hasGuide = isBranchOrToward;
        }

        AddSignInfo(signNames, signType, true);
        signNames = new List<string>();

        // Guide or exit sign toward streets.
        if (way.DestinationStreetToIndex != 0)
        {
            signNames = GetTagTokens(osmdata.NameOffsetMap.Name(way.DestinationStreetToIndex));
            signType = isBranchOrToward ? Sign.Type.GuideToward : Sign.Type.ExitToward;
            hasGuide = isBranchOrToward;
        }

        AddSignInfo(signNames, signType, false);
        signNames = new List<string>();

        // Exit sign toward locations.
        bool hasBranch = way.DestinationRefIndex != 0 || way.DestinationStreetIndex != 0;
        bool hasToward = way.DestinationRefToIndex != 0 || way.DestinationStreetToIndex != 0;
        if (way.DestinationIndex != 0 ||
            (forward && way.DestinationForwardIndex != 0) ||
            (!forward && way.DestinationBackwardIndex != 0))
        {
            uint index = way.DestinationIndex != 0
                ? way.DestinationIndex
                : (forward ? way.DestinationForwardIndex : way.DestinationBackwardIndex);
            signNames = GetTagTokens(osmdata.NameOffsetMap.Name(index));
            signType = isBranchOrToward ? Sign.Type.GuideToward : Sign.Type.ExitToward;
            hasToward = true;
            hasGuide = hasGuide || isBranchOrToward;
        }

        AddSignInfo(signNames, signType, false);
        signNames = new List<string>();

        // Process exit_to only if other branch or toward info does not exist.
        if (!hasBranch && !hasToward)
        {
            if (node.HasExitTo() && !fork)
            {
                foreach (string exitTo in GetTagTokens(osmdata.NodeNames.Name(node.ExitToIndex())))
                {
                    string tmp = exitTo.ToLowerInvariant();

                    if (tmp.StartsWith("to ", StringComparison.Ordinal))
                    {
                        exitList.Add(new SignInfo(Sign.Type.ExitToward, false, false, false, 0, 0, exitTo.Substring(3)));
                        continue;
                    }

                    if (tmp.StartsWith("toward ", StringComparison.Ordinal))
                    {
                        exitList.Add(new SignInfo(Sign.Type.ExitToward, false, false, false, 0, 0, exitTo.Substring(7)));
                        continue;
                    }

                    int foundIdx = tmp.IndexOf(" to ", StringComparison.Ordinal);
                    if (foundIdx != -1 &&
                        tmp.IndexOf(" to ", foundIdx + 4, StringComparison.Ordinal) == -1 &&
                        tmp.IndexOf(" toward ", StringComparison.Ordinal) == -1)
                    {
                        exitList.Add(new SignInfo(Sign.Type.ExitBranch, false, false, false, 0, 0, exitTo.Substring(0, foundIdx)));
                        exitList.Add(new SignInfo(Sign.Type.ExitToward, false, false, false, 0, 0, exitTo.Substring(foundIdx + 4)));
                        continue;
                    }

                    foundIdx = tmp.IndexOf(" toward ", StringComparison.Ordinal);
                    if (foundIdx != -1 &&
                        tmp.IndexOf(" toward ", foundIdx + 8, StringComparison.Ordinal) == -1 &&
                        tmp.IndexOf(" to ", StringComparison.Ordinal) == -1)
                    {
                        exitList.Add(new SignInfo(Sign.Type.ExitBranch, false, false, false, 0, 0, exitTo.Substring(0, foundIdx)));
                        exitList.Add(new SignInfo(Sign.Type.ExitToward, false, false, false, 0, 0, exitTo.Substring(foundIdx + 8)));
                        continue;
                    }

                    exitList.Add(new SignInfo(Sign.Type.ExitToward, false, false, false, 0, 0, exitTo));
                }
            }
        }

        // NAME - exit sign name.
        if (node.HasName() && !node.NamedIntersection() && !fork && ramp)
        {
            signType = Sign.Type.ExitName;
            signNames = GetTagTokens(osmdata.NodeNames.Name(node.NameIndex()));
        }

        AddSignInfo(signNames, signType, false);
        signNames = new List<string>();

        // junction:name.
        if (way.JunctionNameIndex != 0)
        {
            signNames = GetTagTokens(osmdata.NameOffsetMap.Name(way.JunctionNameIndex));
            signType = Sign.Type.ExitName;
        }

        AddSignInfo(signNames, signType, false);

        // GUIDANCE VIEWS - junction.
        bool hasGuidanceViewJct = false;
        if (forward && way.FwdJctBaseIndex > 0)
        {
            foreach (string nm in GetTagTokens(osmdata.NameOffsetMap.Name(way.FwdJctBaseIndex), '|'))
            {
                exitList.Add(new SignInfo(Sign.Type.GuidanceViewJunction, true, false, false, 0, 0, nm));
            }

            hasGuidanceViewJct = true;
        }
        else if (!forward && way.BwdJctBaseIndex > 0)
        {
            foreach (string nm in GetTagTokens(osmdata.NameOffsetMap.Name(way.BwdJctBaseIndex), '|'))
            {
                exitList.Add(new SignInfo(Sign.Type.GuidanceViewJunction, true, false, false, 0, 0, nm));
            }

            hasGuidanceViewJct = true;
        }

        if (forward && way.FwdJctOverlayIndex > 0)
        {
            foreach (string nm in GetTagTokens(osmdata.NameOffsetMap.Name(way.FwdJctOverlayIndex), '|'))
            {
                exitList.Add(new SignInfo(Sign.Type.GuidanceViewJunction, false, false, false, 0, 0, nm));
            }

            hasGuidanceViewJct = true;
        }
        else if (!forward && way.BwdJctOverlayIndex > 0)
        {
            foreach (string nm in GetTagTokens(osmdata.NameOffsetMap.Name(way.BwdJctOverlayIndex), '|'))
            {
                exitList.Add(new SignInfo(Sign.Type.GuidanceViewJunction, false, false, false, 0, 0, nm));
            }

            hasGuidanceViewJct = true;
        }

        // GUIDANCE VIEWS - signboard.
        bool hasGuidanceViewSignboard = false;
        if (forward && way.FwdSignboardBaseIndex > 0)
        {
            foreach (string nm in GetTagTokens(osmdata.NameOffsetMap.Name(way.FwdSignboardBaseIndex), '|'))
            {
                exitList.Add(new SignInfo(Sign.Type.GuidanceViewSignboard, true, false, false, 0, 0, nm));
            }

            hasGuidanceViewSignboard = true;
        }
        else if (!forward && way.BwdSignboardBaseIndex > 0)
        {
            foreach (string nm in GetTagTokens(osmdata.NameOffsetMap.Name(way.BwdSignboardBaseIndex), '|'))
            {
                exitList.Add(new SignInfo(Sign.Type.GuidanceViewSignboard, true, false, false, 0, 0, nm));
            }

            hasGuidanceViewSignboard = true;
        }

        // Stable-sort because we need the key/indexes for phonemes.
        StableSortByType(exitList);

        return hasGuide || hasGuidanceViewJct || hasGuidanceViewSignboard;
    }

    /// <summary>
    /// Computes a curvature metric [0-15] for an edge shape. Faithful port of the free function
    /// <c>compute_curvature</c> in <c>src/mjolnir/util.cc</c>.
    /// </summary>
    public static uint ComputeCurvature(IReadOnlyList<PointLL> shape)
    {
        // Edges with just 2 shape points have no curvature.
        if (shape.Count == 2)
        {
            return 0;
        }

        uint n = 0;
        float totalScore = 0.0f;
        for (int i = 0; i + 2 < shape.Count; ++i)
        {
            float radius = (float)shape[i].Curvature(shape[i + 1], shape[i + 2]);
            if (!float.IsNaN(radius))
            {
                float score = radius > 1000.0f ? 0.0f : 1500.0f / radius;
                totalScore += score > 25.0f ? 25.0f : score;
                n++;
            }
        }

        float averageScore = n == 0 ? 0.0f : totalScore / n;
        return averageScore > 15.0f ? 15u : (uint)averageScore;
    }

    // GetTagTokens: split a tag value on a delimiter (default ';'), trimming nothing (matches the
    // midgard GetTagTokens used in graphbuilder.cc, which splits on the delimiter and keeps tokens).
    private static List<string> GetTagTokens(string tagValue, char delim = ';')
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(tagValue))
        {
            return tokens;
        }

        tokens.AddRange(tagValue.Split(delim));
        return tokens;
    }

    private static void StableSortByType(List<SignInfo> list)
    {
        var indexed = new List<(SignInfo Sign, int Order)>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            indexed.Add((list[i], i));
        }

        indexed.Sort((a, b) =>
        {
            int c = a.Sign.CompareTo(b.Sign);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });

        for (int i = 0; i < list.Count; i++)
        {
            list[i] = indexed[i].Sign;
        }
    }
}
