// Faithful C# port of Valhalla mjolnir linkclassification.cc + linkclassification.h @ 3.7.0.
// Sources:
//   F:/github/valhalla/src/mjolnir/linkclassification.cc
//   F:/github/valhalla/valhalla/mjolnir/linkclassification.h
//
// Reclassify links (ramps and turn channels). OSM usually classifies links (motorway_link,
// trunk_link, ...) as the best classification, but to more effectively create shortcuts it is
// better to "downgrade" link edges to the lower classification. This finds "exit" nodes sorted by
// classification, forms an acyclic "link graph" from each exit node, then uses the classifications
// at the link-graph nodes to potentially reclassify the link edges. It also identifies turn
// channels / turn lanes (likely at-grade "slip roads").
//
// This operates on the INTERMEDIATE GRAPH (the Edge / Node lists produced by GraphBuilder's
// ConstructEdges + SortGraph), exactly as the C++ does over the nodes.bin / edges.bin / ways.bin /
// way_nodes.bin midgard::sequence temp files. The on-device port keeps everything in managed lists
// (matching the established GraphBuilder port), so the C++ `sequence<Edge>::iterator element; auto
// edge = *element; ... element = edge;` write-back pattern becomes a List<Edge> index assignment.
//
// EVERY algorithm is preserved exactly:
//   - FormExitNodes        : bucket exit nodes by best non-link road class.
//   - LinkGraphBuilder     : build an acyclic link graph from an exit node (driveforward links).
//   - WayTags + ref/dest:ref matching : reference-based "same road"/"destination" heuristics.
//   - ReclassifyLinkGraph  : walk leaf->root link chains, set the new class + turn channel flag.
//   - IsTurnChannel / IsSlipLane / GoTowardsIntersection : at-grade slip lane detection.
//
// EXCLUDED: the LOGGING_LEVEL_DEBUG GeoJSON visualization helpers (LineStringFeature / PointFeature
// / VisualizeIntersection) are debug-only (baldr::json) and are not ported.

using System;
using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Reclassifies link edges (ramps and turn channels) within the intermediate graph. Faithful port
/// of the free functions in <c>src/mjolnir/linkclassification.cc</c> (the <c>ReclassifyLinks</c>
/// entry point plus its supporting types).
/// </summary>
public static class LinkClassification
{
    /// <summary>Maximum road classification value (count of classes 0..7). Mirrors C++ <c>kMaxClassification</c>.</summary>
    public const uint MaxClassification = 8;

    /// <summary>Maximum link edges from a graph node before erroring. Mirrors C++ <c>kMaxLinkEdges</c>.</summary>
    public const uint MaxLinkEdges = 32;

    /// <summary>The service-other road class value. Mirrors C++ <c>kServiceClass</c>.</summary>
    public const uint ServiceClass = (uint)RoadClass.ServiceOther;

    /// <summary>The "absurd" road class sentinel. Mirrors C++ <c>kAbsurdRoadClass</c> (node_expander.h).</summary>
    public const uint AbsurdRoadClass = 777777;

    /// <summary>
    /// Holds all of the intermediate-graph data we need in one place (the C++ <c>struct Data</c>,
    /// which wraps the four midgard::sequence files plus the OSMData). The on-device port keeps the
    /// edge and node lists mutable so reclassification can write attributes back.
    /// </summary>
    public sealed class Data
    {
        /// <summary>Constructs the data bundle. Faithful port of the C++ <c>Data</c> constructor.</summary>
        /// <param name="nodes">The temporary-graph nodes (sorted by graphid+grid+osmid).</param>
        /// <param name="edges">The temporary-graph edges (indexed by edge index).</param>
        /// <param name="ways">The parsed OSM ways (indexed by way index).</param>
        /// <param name="wayNodes">The parsed way-node references (the way shape with intersections marked).</param>
        /// <param name="osmdata">The parsed OSM data.</param>
        public Data(
            List<Node> nodes,
            List<Edge> edges,
            IReadOnlyList<OSMWay> ways,
            IReadOnlyList<OSMWayNode> wayNodes,
            OSMData osmdata)
        {
            Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            Edges = edges ?? throw new ArgumentNullException(nameof(edges));
            Ways = ways ?? throw new ArgumentNullException(nameof(ways));
            WayNodes = wayNodes ?? throw new ArgumentNullException(nameof(wayNodes));
            Osmdata = osmdata ?? throw new ArgumentNullException(nameof(osmdata));
        }

        /// <summary>The temporary-graph nodes.</summary>
        public List<Node> Nodes { get; }

        /// <summary>The temporary-graph edges.</summary>
        public List<Edge> Edges { get; }

        /// <summary>The parsed OSM ways.</summary>
        public IReadOnlyList<OSMWay> Ways { get; }

        /// <summary>The parsed way-node references.</summary>
        public IReadOnlyList<OSMWayNode> WayNodes { get; }

        /// <summary>The parsed OSM data.</summary>
        public OSMData Osmdata { get; }
    }

    /// <summary>
    /// Reclassify links (ramps and turn channels). Finds the exit nodes (sorted by classification of
    /// their non-link connecting edges), builds a link graph from each, and reclassifies link edges
    /// to the lower classification, optionally inferring turn channels. Faithful port of
    /// <c>ReclassifyLinks</c>.
    /// </summary>
    /// <param name="nodes">The temporary-graph nodes (mutated: not directly; the edges hold the class).</param>
    /// <param name="edges">The temporary-graph edges (mutated: link edges have importance / turn channel set).</param>
    /// <param name="ways">The parsed OSM ways.</param>
    /// <param name="wayNodes">The parsed way-node references.</param>
    /// <param name="osmdata">The parsed OSM data.</param>
    /// <param name="reclassifyLinks">Whether to actually reclassify link edge importance.</param>
    /// <param name="inferTurnChannels">Whether to infer turn channels.</param>
    /// <returns>A tuple of (reclassified link edge count, turn channel count).</returns>
    public static (uint ReclassCount, uint TurnChannelCount) ReclassifyLinks(
        List<Node> nodes,
        List<Edge> edges,
        IReadOnlyList<OSMWay> ways,
        IReadOnlyList<OSMWayNode> wayNodes,
        OSMData osmdata,
        bool reclassifyLinks,
        bool inferTurnChannels)
    {
        var data = new Data(nodes, edges, ways, wayNodes, osmdata);

        // Find list of exit nodes - nodes where drivable outbound links connect to non-link edges.
        // Group by best road class of the non-link connecting edges.
        List<List<int>> exitNodes = FormExitNodes(data);

        // Iterate through the exit node list by classification so exits from major roads are
        // considered before exits from minor roads.
        uint reclassCount = 0;
        uint tcCount = 0;

        for (uint classification = 0; classification < MaxClassification; classification++)
        {
            foreach (int nodeIndex in exitNodes[(int)classification])
            {
                var buildGraph = new LinkGraphBuilder(data);
                // build link graph
                List<LinkGraphNode> linkGraph = buildGraph.Build(nodeIndex, classification);
                // reclassify links and infer turn channels
                (uint rc, uint tc) = ReclassifyLinkGraph(
                    linkGraph, classification, data, reclassifyLinks, inferTurnChannels);
                reclassCount += rc;
                tcCount += tc;
            }
        }

        return (reclassCount, tcCount);
    }

    // ------------------------------------------------------------------
    // Edge / node classification helpers (free functions in linkclassification.cc)
    // ------------------------------------------------------------------

    /// <summary>Is this a drivable non-link edge that is not a service road? Faithful port of <c>IsdrivableNonLink</c>.</summary>
    private static bool IsDrivableNonLink(Edge edge)
        => !edge.Attributes.Link &&
           (((edge.FwdAccess & GraphConstants.AutoAccess) != 0) ||
            ((edge.RevAccess & GraphConstants.AutoAccess) != 0)) &&
           edge.Attributes.Importance != ServiceClass;

    /// <summary>Is this a link edge that is drivable in the forward direction? Faithful port of <c>IsDriveForwardLink</c>.</summary>
    private static bool IsDriveForwardLink(Edge edge) => edge.Attributes.Link && edge.Attributes.DriveForward;

    /// <summary>
    /// Gets the best (lowest) classification for any drivable non-link edges from a node. Faithful
    /// port of <c>GetBestNonLinkClass</c>.
    /// </summary>
    private static uint GetBestNonLinkClass(IEnumerable<KeyValuePair<Edge, ulong>> edges)
    {
        uint bestrc = AbsurdRoadClass;
        foreach (KeyValuePair<Edge, ulong> edge in edges)
        {
            if (IsDrivableNonLink(edge.Key))
            {
                bestrc = Math.Min(bestrc, edge.Key.Attributes.Importance);
            }
        }

        return bestrc;
    }

    /// <summary>Gets the shape (lat,lng) of an edge from the way_nodes. Faithful port of <c>EdgeShape</c>.</summary>
    private static List<PointLL> EdgeShape(Data data, Edge edge)
    {
        int idx = (int)edge.LlIndex;
        uint count = edge.Attributes.LlCount;
        var shape = new List<PointLL>((int)count);
        for (uint i = 0; i < count; ++i)
        {
            shape.Add(data.WayNodes[idx++].Node.LatLng());
        }

        return shape;
    }

    /// <summary>Computes the total length (m) of a set of edges. Faithful port of <c>CalcEdgesLength</c>.</summary>
    private static float CalcEdgesLength(Data data, IReadOnlyList<uint> edges)
    {
        float totalLength = 0.0f;
        foreach (uint idx in edges)
        {
            Edge edge = data.Edges[(int)idx];
            List<PointLL> shape = EdgeShape(data, edge);
            totalLength += (float)PointLlPolyline2.Length(shape);
        }

        return totalLength;
    }

    /// <summary>
    /// Forms a list of all nodes - sorted into buckets by the highest (best) classification of the
    /// non-link edges at the node. Faithful port of <c>FormExitNodes</c>.
    /// </summary>
    private static List<List<int>> FormExitNodes(Data data)
    {
        var exitNodes = new List<List<int>>((int)MaxClassification);
        for (uint i = 0; i < MaxClassification; i++)
        {
            exitNodes.Add(new List<int>());
        }

        int nodeIdx = 0;
        while (nodeIdx < data.Nodes.Count)
        {
            // If the node has both links and non links at it.
            NodeBundle bundle = NodeExpander.CollectNodeEdges(nodeIdx, data.Nodes, data.Edges);
            if (bundle.Node.OsmNode.LinkEdge() && bundle.Node.OsmNode.NonLinkEdge())
            {
                // Check if this node has a link edge that is outgoing (driveforward) from the node.
                foreach (KeyValuePair<Edge, ulong> edge in bundle.NodeEdges)
                {
                    if (edge.Key.Attributes.Link && edge.Key.Attributes.DriveForward)
                    {
                        // Get the highest classification of non-link edges at this node. Add to the
                        // exit node list if a valid classification...if no connecting edge is drivable
                        // the node will be skipped.
                        uint rc = GetBestNonLinkClass(bundle.NodeEdges);
                        if (rc < MaxClassification)
                        {
                            exitNodes[(int)rc].Add(nodeIdx);
                        }
                    }
                }
            }

            // Go to the next node.
            nodeIdx += bundle.NodeCount;
        }

        return exitNodes;
    }

    // ------------------------------------------------------------------
    // WayTags + reference-based matching (free functions / struct in linkclassification.cc)
    // ------------------------------------------------------------------

    /// <summary>
    /// Way tags (refs and destination:refs) used to determine the correct link class. Faithful port
    /// of the C++ <c>struct WayTags</c>.
    /// </summary>
    private sealed class WayTags
    {
        public List<string> Refs { get; init; } = new();

        public List<string> DestRefs { get; init; } = new();

        public bool IsEmpty() => Refs.Count == 0 && DestRefs.Count == 0;

        /// <summary>
        /// Parses 'destination:ref' and 'ref' tags as separate vectors of names. Faithful port of
        /// <c>WayTags::Parse</c>.
        /// </summary>
        public static WayTags Parse(OSMWay way, OSMData osmdata)
        {
            var roadTags = new WayTags();

            // Parse 'destination:ref' tag.
            if (way.DestinationRefIndex != 0)
            {
                roadTags.DestRefs.AddRange(GetTagTokens(osmdata.NameOffsetMap.Name(way.DestinationRefIndex)));
            }

            // Parse 'ref' tag.
            if (way.RefIndex != 0)
            {
                roadTags.Refs.AddRange(GetTagTokens(osmdata.NameOffsetMap.Name(way.RefIndex)));
            }

            return roadTags;
        }
    }

    /// <summary>Check if these two references belong to the same road. Faithful port of <c>MatchRefs</c>.</summary>
    private static bool MatchRefs(string ref1, string ref2)
    {
        int sz = Math.Min(ref1.Length, ref2.Length);
        // (sz != 0) && (ref_1.compare(0, sz, ref_2, 0, sz) == 0)
        return sz != 0 && string.CompareOrdinal(ref1, 0, ref2, 0, sz) == 0;
    }

    /// <summary>Check if these two ways belong to the same road. Faithful port of <c>IsTheSameRoad(2)</c>.</summary>
    private static bool IsTheSameRoad(WayTags road1, WayTags road2)
        => road1.Refs.Count != 0 && road2.Refs.Count != 0 && MatchRefs(road1.Refs[0], road2.Refs[0]);

    /// <summary>Check if these three ways belong to the same road. Faithful port of <c>IsTheSameRoad(3)</c>.</summary>
    private static bool IsTheSameRoad(WayTags road1, WayTags road2, WayTags road3)
        => IsTheSameRoad(road1, road2) && IsTheSameRoad(road2, road3);

    /// <summary>Check if the link contains this destination reference. Faithful port of <c>IsDestinationRef</c>.</summary>
    private static bool IsDestinationRef(string @ref, WayTags link)
    {
        if (string.IsNullOrEmpty(@ref))
        {
            return false;
        }

        foreach (string destRef in link.DestRefs)
        {
            if (MatchRefs(@ref, destRef))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Using destination link tags, detect if the road is a final destination. Faithful port of <c>IsDestinationRoad</c>.</summary>
    private static bool IsDestinationRoad(WayTags road, WayTags link)
        => road.Refs.Count != 0 && IsDestinationRef(road.Refs[0], link);

    /// <summary>Check if these two links have a common destination road. Faithful port of <c>HasCommonDestination(2)</c>.</summary>
    private static bool HasCommonDestination(WayTags link1, WayTags link2)
    {
        foreach (string destRef1 in link1.DestRefs)
        {
            if (IsDestinationRef(destRef1, link2))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Check if these three links have a common destination road. Faithful port of <c>HasCommonDestination(3)</c>.</summary>
    private static bool HasCommonDestination(WayTags link1, WayTags link2, WayTags link3)
    {
        foreach (string destRef1 in link1.DestRefs)
        {
            if (IsDestinationRef(destRef1, link2) && IsDestinationRef(destRef1, link3))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check if this node contains a road that is a destination for the link. Faithful port of
    /// <c>IsDestinationNode</c>.
    /// </summary>
    private static bool IsDestinationNode(NodeBundle node, WayTags link, Data data)
    {
        if (link.IsEmpty())
        {
            return false;
        }

        foreach (KeyValuePair<Edge, ulong> edge in node.NodeEdges)
        {
            if (IsDrivableNonLink(edge.Key))
            {
                WayTags road = WayTags.Parse(data.Ways[(int)edge.Key.WayIndex], data.Osmdata);
                if (IsTheSameRoad(road, link) || IsDestinationRoad(road, link))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Check if any of the destination roads for the root link can be reached from this node. Builds
    /// all possible link paths from this node and checks if any leads to a destination road. Faithful
    /// port of <c>CheckIfNodeLeadsToDestination</c>.
    /// </summary>
    private static bool CheckIfNodeLeadsToDestination(
        int nodeIdx,
        WayTags rootLinkTags,
        HashSet<int> alreadyVisitedNodes,
        Data data)
    {
        // Initialize the set of visited nodes (copy, matching the C++ value-copy).
        var visitedNodes = new HashSet<int>(alreadyVisitedNodes);
        var expandQueue = new Queue<int>();

        expandQueue.Enqueue(nodeIdx);
        while (expandQueue.Count > 0)
        {
            nodeIdx = expandQueue.Dequeue();
            // Skip if the node has already been visited.
            if (visitedNodes.Contains(nodeIdx))
            {
                continue;
            }

            visitedNodes.Add(nodeIdx);

            NodeBundle bundle = NodeExpander.CollectNodeEdges(nodeIdx, data.Nodes, data.Edges);
            if (IsDestinationNode(bundle, rootLinkTags, data))
            {
                return true;
            }

            foreach (KeyValuePair<Edge, ulong> edge in bundle.NodeEdges)
            {
                if (!IsDriveForwardLink(edge.Key))
                {
                    continue;
                }

                int endNodeIdx = edge.Key.SourceNode == nodeIdx
                    ? (int)edge.Key.TargetNode
                    : (int)edge.Key.SourceNode;
                expandQueue.Enqueue(endNodeIdx);
            }
        }

        return false;
    }

    /// <summary>
    /// Check if the node lies on the path from the root link to some destination road (and is not the
    /// last node in this path). Faithful port of <c>CanGoThroughNode</c>.
    /// </summary>
    private static bool CanGoThroughNode(
        NodeBundle node,
        int nodeIdx,
        WayTags inboundLink,
        WayTags rootLink,
        HashSet<int> visitedNodes,
        Data data)
    {
        if (!HasCommonDestination(rootLink, inboundLink) && !IsTheSameRoad(rootLink, inboundLink))
        {
            return false;
        }

        bool hasCommonDest = false;
        foreach (KeyValuePair<Edge, ulong> edge in node.NodeEdges)
        {
            if (IsDriveForwardLink(edge.Key))
            {
                WayTags link = WayTags.Parse(data.Ways[(int)edge.Key.WayIndex], data.Osmdata);
                // We can go through this node if the root link, inbound link and outbound link belong
                // to the same road.
                if (IsTheSameRoad(link, inboundLink, rootLink))
                {
                    return true;
                }

                if (!hasCommonDest && HasCommonDestination(link, inboundLink, rootLink))
                {
                    hasCommonDest = true;
                }
            }
        }

        return hasCommonDest && CheckIfNodeLeadsToDestination(nodeIdx, rootLink, visitedNodes, data);
    }

    // ------------------------------------------------------------------
    // Link graph (struct LinkGraphNode + struct LinkGraphBuilder)
    // ------------------------------------------------------------------

    /// <summary>
    /// A node in the acyclic link graph (NOTE: assumes the graph is acyclic). Faithful port of the
    /// C++ <c>struct LinkGraphNode</c>.
    /// </summary>
    private sealed class LinkGraphNode
    {
        public LinkGraphNode(int nodeIndex, uint rc, NodeBundle bundle)
        {
            NodeIndex = nodeIndex;
            Classification = rc;
            Bundle = bundle;
            HasExit = bundle.Node.OsmNode.HasExitTo() || bundle.Node.OsmNode.HasRef();
        }

        /// <summary>Node index in the sequence.</summary>
        public int NodeIndex { get; }

        /// <summary>Classification at this node.</summary>
        public uint Classification { get; set; }

        /// <summary>Info about node edges.</summary>
        public NodeBundle Bundle { get; }

        /// <summary>Whether this node has an exit sign / ref.</summary>
        public bool HasExit { get; }

        /// <summary>Indices of parent nodes in the graph (graph indices, not sequence indices).</summary>
        public List<int> Parents { get; } = new();

        /// <summary>Indices of parent edges in the sequence.</summary>
        public List<uint> ParentsEdges { get; } = new();

        /// <summary>Indices of children nodes in the graph (graph indices, not sequence indices).</summary>
        public List<int> Children { get; } = new();

        /// <summary>Indices of children edges in the sequence.</summary>
        public List<uint> ChildrenEdges { get; } = new();

        /// <summary>Number of reclassified children nodes (used during reclassification).</summary>
        public int ChildrenReclassified { get; set; }
    }

    /// <summary>
    /// Builds an acyclic link graph starting from an exit node (the root node). Then recursively
    /// traverses the graph using only driveforward links. Faithful port of the C++
    /// <c>struct LinkGraphBuilder</c>.
    /// </summary>
    private sealed class LinkGraphBuilder
    {
        private readonly Data _data;

        // Way tags of the root link.
        private WayTags _rootLink = new();

        // List of link graph nodes.
        private readonly List<LinkGraphNode> _graph = new();

        // Processed node indices (from the sequence). Maps sequence node indices to graph indices.
        private readonly Dictionary<int, int> _processed = new();

        // Node indices (from the sequence) that are being processed now.
        private readonly HashSet<int> _inProgress = new();

        public LinkGraphBuilder(Data data) => _data = data;

        /// <summary>Faithful port of <c>LinkGraphBuilder::operator()</c>.</summary>
        public List<LinkGraphNode> Build(int exitNodeIndex, uint classification)
        {
            NodeBundle exitBundle = NodeExpander.CollectNodeEdges(exitNodeIndex, _data.Nodes, _data.Edges);
            _graph.Add(new LinkGraphNode(exitNodeIndex, classification, exitBundle));
            _inProgress.Add(exitNodeIndex);

            // Expand link edges from the exit node.
            foreach (KeyValuePair<Edge, ulong> startedge in exitBundle.NodeEdges)
            {
                // Get the edge information. Skip non-link edges, link edges that are not drivable in
                // the forward direction, and link edges already tested for reclassification.
                if (!IsDriveForwardLink(startedge.Key) || startedge.Key.Attributes.ReclassLink)
                {
                    continue;
                }

                // 'destination:ref' and 'ref' tags are widely distributed only among motorway and
                // trunk links. TODO: extend reference-based classification to other link classes.
                if (startedge.Key.Attributes.Importance <= (uint)RoadClass.Trunk)
                {
                    _rootLink = WayTags.Parse(_data.Ways[(int)startedge.Key.WayIndex], _data.Osmdata);
                }

                ExpandLink(startedge.Key, (uint)startedge.Value, 0);
            }

            return _graph;
        }

        private void ExpandLink(Edge inEdge, uint inEdgeIdx, int parent)
        {
            // Find the end node of this link edge.
            int endNodeIndex = inEdge.SourceNode == _graph[parent].NodeIndex
                ? (int)inEdge.TargetNode
                : (int)inEdge.SourceNode;

            // TODO: process cycles.
            if (_inProgress.Contains(endNodeIndex))
            {
                return;
            }

            // Check if this node has already been processed.
            if (_processed.TryGetValue(endNodeIndex, out int processedGraphIdx))
            {
                // Add a new edge to the graph.
                AddGraphEdge(parent, processedGraphIdx, inEdgeIdx);
                return;
            }

            // Get the edges at the end node and the best non-link classification.
            NodeBundle bundle = NodeExpander.CollectNodeEdges(endNodeIndex, _data.Nodes, _data.Edges);
            uint rc = GetBestNonLinkClass(bundle.NodeEdges);
            int graphIdx = _graph.Count;
            _graph.Add(new LinkGraphNode(endNodeIndex, rc, bundle));
            // Add a new edge to the graph.
            AddGraphEdge(parent, graphIdx, inEdgeIdx);

            // Check "stop criterions" only if this link intersects a "major" road.
            if (bundle.Node.OsmNode.NonLinkEdge() && rc <= (uint)RoadClass.Residential)
            {
                WayTags edgeTags = WayTags.Parse(_data.Ways[(int)inEdge.WayIndex], _data.Osmdata);
                // We should stop if this node contains a destination road or if it doesn't belong to
                // any path from the root to a destination road.
                if (edgeTags.IsEmpty() || IsDestinationNode(bundle, _rootLink, _data) ||
                    !CanGoThroughNode(bundle, endNodeIndex, edgeTags, _rootLink, _inProgress, _data))
                {
                    return;
                }
            }

            ExpandGraphNode(graphIdx);
        }

        private void ExpandGraphNode(int graphIdx)
        {
            int nodeIndex = _graph[graphIdx].NodeIndex;
            // Snapshot the node edges (the C++ takes a const ref; iterate over the bundle's edges).
            var bundle = new List<KeyValuePair<Edge, ulong>>(_graph[graphIdx].Bundle.NodeEdges);

            // Update 'processed' and 'in progress' sets in order to be able to detect cycles.
            _processed.Remove(nodeIndex);
            _inProgress.Add(nodeIndex);

            // Expand link edges from the node.
            foreach (KeyValuePair<Edge, ulong> edge in bundle)
            {
                // Use only links drivable in the forward direction.
                if (!IsDriveForwardLink(edge.Key))
                {
                    continue;
                }

                // If the edge has already been considered for reclassification, update the node
                // classification.
                if (edge.Key.Attributes.ReclassLink)
                {
                    _graph[graphIdx].Classification =
                        Math.Min(_graph[graphIdx].Classification, edge.Key.Attributes.Importance);
                }
                else
                {
                    ExpandLink(edge.Key, (uint)edge.Value, graphIdx);
                }
            }

            // This node has been processed. Move it from 'in progress' to 'processed'.
            _inProgress.Remove(nodeIndex);
            _processed[nodeIndex] = graphIdx;
        }

        private void AddGraphEdge(int from, int to, uint edgeIdx)
        {
            _graph[from].Children.Add(to);
            _graph[from].ChildrenEdges.Add(edgeIdx);

            // Make sure that the number of children does not exceed the threshold.
            if (_graph[from].Children.Count >= MaxLinkEdges)
            {
                PointLL ll = _graph[from].Bundle.Node.OsmNode.LatLng();
                throw new InvalidOperationException(
                    "Exceeding kMaxLinkEdges in ReclassifyLinks at location " +
                    ll.Lng.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                    ll.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            _graph[to].Parents.Add(from);
            _graph[to].ParentsEdges.Add(edgeIdx);
        }
    }

    // ------------------------------------------------------------------
    // Turn channel / slip lane detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Road name (way id + names + ref) used to follow a road through an intersection. Faithful port
    /// of the C++ <c>struct RoadName</c>.
    /// </summary>
    private readonly struct RoadName : IEquatable<RoadName>
    {
        private readonly ulong _wayId;
        private readonly List<string> _names;
        private readonly List<string> _ref;

        public RoadName(Data data, Edge edge)
        {
            OSMWay way = data.Ways[(int)edge.WayIndex];
            _names = GetTagTokens(data.Osmdata.NameOffsetMap.Name(way.NameIndex));
            _ref = GetTagTokens(data.Osmdata.NameOffsetMap.Name(way.RefIndex));
            _wayId = way.WayId();
        }

        public bool Equals(RoadName rhs)
            => _wayId == rhs._wayId || Equal(_names, rhs._names) || Equal(_ref, rhs._ref);

        public override bool Equals(object? obj) => obj is RoadName rn && Equals(rn);

        public override int GetHashCode() => _wayId.GetHashCode();

        public static bool operator ==(RoadName a, RoadName b) => a.Equals(b);

        public static bool operator !=(RoadName a, RoadName b) => !a.Equals(b);

        private static bool Equal(List<string> lhs, List<string> rhs)
            => lhs.Count != 0 && rhs.Count != 0 && lhs[0].Length != 0 && rhs[0].Length != 0 && lhs[0] == rhs[0];
    }

    /// <summary>Is the edge drivable in the given direction from <paramref name="fromNode"/>? Faithful port of <c>IsEdgedrivableInDirection</c>.</summary>
    private static bool IsEdgeDrivableInDirection(int fromNode, Edge edge, bool forward)
    {
        bool rightDirection;
        if (forward)
        {
            rightDirection =
                (edge.SourceNode == fromNode && (edge.FwdAccess & GraphConstants.AutoAccess) != 0) ||
                (edge.TargetNode == fromNode && (edge.RevAccess & GraphConstants.AutoAccess) != 0);
        }
        else
        {
            rightDirection =
                (edge.SourceNode == fromNode && (edge.RevAccess & GraphConstants.AutoAccess) != 0) ||
                (edge.TargetNode == fromNode && (edge.FwdAccess & GraphConstants.AutoAccess) != 0);
        }

        return rightDirection;
    }

    /// <summary>Returns the end node of an edge given the from node. Faithful port of <c>EndNode</c>.</summary>
    private static int EndNode(int fromNode, Edge edge)
        => fromNode == edge.SourceNode ? (int)edge.TargetNode : (int)edge.SourceNode;

    /// <summary>
    /// Traverse non-link edges towards an intersection, following the same road name where the path
    /// branches, until the length threshold is reached. Returns the nodes of the path. Faithful port
    /// of <c>GoTowardsIntersection</c>.
    /// </summary>
    private static List<uint> GoTowardsIntersection(
        int startNode,
        uint startEdgeIdx,
        bool forward,
        double lengthStopThreshold,
        Data data)
    {
        Edge startEdge = data.Edges[(int)startEdgeIdx];
        int node = EndNode(startNode, startEdge);
        Edge prevEdge = startEdge;
        bool nextFound;
        var nodes = new List<uint> { (uint)startNode, (uint)node };
        var visitedNodes = new HashSet<int> { startNode, node };
        double currentLength = PointLlPolyline2.Length(EdgeShape(data, startEdge));

        void Next(KeyValuePair<Edge, ulong> edge)
        {
            nextFound = true;
            node = EndNode(node, edge.Key);
            visitedNodes.Add(node);
            prevEdge = edge.Key;
            nodes.Add((uint)node);
            currentLength += PointLlPolyline2.Length(EdgeShape(data, edge.Key));
        }

        // Traverse edges until the length limit is reached.
        while (currentLength < lengthStopThreshold)
        {
            nextFound = false;
            NodeBundle bundle = NodeExpander.CollectNodeEdges(node, data.Nodes, data.Edges);
            var candidates = new List<KeyValuePair<Edge, ulong>>();

            // Look for a non-link edge with the right direction.
            foreach (KeyValuePair<Edge, ulong> nodeEdge in bundle.NodeEdges)
            {
                Edge edge = nodeEdge.Key;
                if (!edge.Attributes.Link && IsEdgeDrivableInDirection(node, edge, forward) &&
                    !visitedNodes.Contains(EndNode(node, nodeEdge.Key)))
                {
                    candidates.Add(nodeEdge);
                }
            }

            if (candidates.Count == 1)
            {
                Next(candidates[0]);
            }
            else if (candidates.Count > 1)
            {
                // If there is more than 1 candidate, filter them by road name.
                var nameCandidates = new List<KeyValuePair<Edge, ulong>>();
                foreach (KeyValuePair<Edge, ulong> edge in candidates)
                {
                    if (new RoadName(data, prevEdge) == new RoadName(data, edge.Key))
                    {
                        nameCandidates.Add(edge);
                    }
                }

                if (nameCandidates.Count != 0)
                {
                    Next(nameCandidates[0]);
                }

                // TODO(merkispavel): check cases when there are more than 1 road name match.
            }

            if (!nextFound)
            {
                break;
            }
        }

        return nodes;
    }

    /// <summary>
    /// Inputs to the slip-lane "triangle" check (the fork edge, merge edge and the end nodes of the
    /// slip lane). Faithful port of the C++ <c>struct SlipLaneInput</c>.
    /// </summary>
    private struct SlipLaneInput
    {
        public const uint InvalidEdge = uint.MaxValue;

        public int FirstNode;
        public int LastNode;
        public uint ForkEdge;
        public uint MergeEdge;

        public readonly bool Valid() => ForkEdge != InvalidEdge && MergeEdge != InvalidEdge;
    }

    /// <summary>
    /// Checks whether the edges fit a "triangle" pattern (the slip lane and a detour through an
    /// intersection both connecting the first and last nodes). Faithful port of <c>IsSlipLane</c>.
    /// </summary>
    private static bool IsSlipLane(Data data, SlipLaneInput input, double traverseThreshold)
    {
        // Traverse from the fork edge in the forward direction hoping to reach the intersection.
        List<uint> forwardNodes =
            GoTowardsIntersection(input.FirstNode, input.ForkEdge, true, traverseThreshold, data);
        // Traverse from the merge edge in the reverse direction hoping to reach the intersection.
        List<uint> reverseNodes =
            GoTowardsIntersection(input.LastNode, input.MergeEdge, false, traverseThreshold, data);

        // Check if the two directions intersect.
        foreach (uint node in reverseNodes)
        {
            if (forwardNodes.Contains(node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the <see cref="SlipLaneInput"/> (fork / merge edges + end nodes) for a link chain.
    /// Faithful port of <c>GetSlipLaneInput</c>.
    /// </summary>
    private static SlipLaneInput GetSlipLaneInput(Data data, IReadOnlyList<uint> linkEdges)
    {
        double EdgeHeading(int fromNode, Edge edge)
        {
            List<PointLL> shape = EdgeShape(data, edge);
            if (edge.SourceNode == fromNode)
            {
                return shape[0].Heading(shape[1]);
            }

            return shape[shape.Count - 1].Heading(shape[shape.Count - 2]);
        }

        // Finds the neighbour edge with the minimum absolute heading delta.
        uint FindClosestNeighbourEdge(int node, Edge edge, bool forward)
        {
            double minAngleDelta = 360;
            uint bestEdgeIdx = SlipLaneInput.InvalidEdge;
            double originHeading = EdgeHeading(node, edge);
            NodeBundle bundle = NodeExpander.CollectNodeEdges(node, data.Nodes, data.Edges);
            foreach (KeyValuePair<Edge, ulong> to in bundle.NodeEdges)
            {
                if (to.Key.Attributes.Link || !IsEdgeDrivableInDirection(node, to.Key, forward))
                {
                    continue;
                }

                double neighbourHeading = EdgeHeading(node, to.Key);
                double absDelta = Math.Abs(originHeading - neighbourHeading);
                double delta = Math.Min(absDelta, 360 - absDelta);
                if (delta < minAngleDelta)
                {
                    minAngleDelta = delta;
                    bestEdgeIdx = (uint)to.Value;
                }
            }

            return bestEdgeIdx;
        }

        var res = default(SlipLaneInput);
        res.ForkEdge = SlipLaneInput.InvalidEdge;
        res.MergeEdge = SlipLaneInput.InvalidEdge;

        // link_edges store the link sequence in reverse order, so the first link edge is actually the
        // last in the list.
        Edge firstLinkEdge = data.Edges[(int)linkEdges[linkEdges.Count - 1]];
        res.FirstNode = (firstLinkEdge.FwdAccess & GraphConstants.AutoAccess) != 0
            ? (int)firstLinkEdge.SourceNode
            : (int)firstLinkEdge.TargetNode;
        res.ForkEdge = FindClosestNeighbourEdge(res.FirstNode, firstLinkEdge, true);

        Edge lastLinkEdge = data.Edges[(int)linkEdges[0]];
        res.LastNode = (lastLinkEdge.FwdAccess & GraphConstants.AutoAccess) != 0
            ? (int)lastLinkEdge.TargetNode
            : (int)lastLinkEdge.SourceNode;
        res.MergeEdge = FindClosestNeighbourEdge(res.LastNode, lastLinkEdge, false);
        return res;
    }

    /// <summary>
    /// Tests if a set of link edges can be classified as a turn channel (a short link, or a slip lane
    /// that fits the triangle pattern). Faithful port of <c>IsTurnChannel</c>.
    /// </summary>
    private static bool IsTurnChannel(Data data, IReadOnlyList<uint> linkEdges)
    {
        bool bidirectional = linkEdges.Any(edgeIdx =>
        {
            Edge edge = data.Edges[(int)edgeIdx];
            OSMWay way = data.Ways[(int)edge.WayIndex];
            return way.AutoForward() && way.AutoBackward();
        });

        // A turn channel can not be bidirectional.
        if (bidirectional)
        {
            return false;
        }

        float totalLength = CalcEdgesLength(data, linkEdges);
        if (totalLength < GraphConstants.MaxTurnChannelLength)
        {
            return true;
        }

        // Length is greater than the threshold so we need further analysis.
        SlipLaneInput input = GetSlipLaneInput(data, linkEdges);
        if (input.Valid())
        {
            // In most cases the slip lane and its detour fit a right triangle with a ~90 degree angle
            // at the intersection so traverse_threshold = total_length (slip lane length) is enough:
            // the hypotenuse (slip lane) length is always greater than a leg length; but we need a
            // greater threshold for intersections with sharp angles.
            return IsSlipLane(data, input, 2 * totalLength);
        }

        return false;
    }

    // ------------------------------------------------------------------
    // ReclassifyLinkGraph
    // ------------------------------------------------------------------

    /// <summary>
    /// Reclassifies links in the acyclic link graph. Maintains a queue of leaf nodes; on each step
    /// takes a leaf, builds the link chain up to the root, determines the final road class for the
    /// whole chain (and sets the new class), then virtually "removes" the reclassified links from the
    /// graph and updates the queue if a new leaf appears. The final link class is the maximum of the
    /// root node classification and the current leaf node classification. Faithful port of
    /// <c>ReclassifyLinkGraph</c>.
    /// </summary>
    private static (uint ReclassCount, uint TurnChannelCount) ReclassifyLinkGraph(
        List<LinkGraphNode> linkGraph,
        uint exitClassification,
        Data data,
        bool reclassifyLinks,
        bool inferTurnChannels)
    {
        uint reclassCount = 0;
        uint tcCount = 0;

        // Collect the start leaf nodes in the acyclic graph.
        var leaves = new Queue<int>();
        for (int i = 0; i < linkGraph.Count; ++i)
        {
            if (linkGraph[i].Children.Count == 0)
            {
                leaves.Enqueue(i);
            }
        }

        // Iterate through leaf nodes and reclassify links.
        while (leaves.Count > 0)
        {
            int leafIdx = leaves.Dequeue();
            LinkGraphNode leaf = linkGraph[leafIdx];

            // Go through each parent.
            foreach (int initialParentIdx in leaf.Parents)
            {
                int parentIdx = initialParentIdx;
                var linkEdges = new List<uint>();
                int currentIdx = leafIdx;

                // Track information required for turn channel tests.
                bool hasFork = false;
                bool hasExit = linkGraph[currentIdx].HasExit;
                bool endsHaveNonLink = linkGraph[currentIdx].Bundle.NonLinkCount > 0;

                // Make a chain of edges, stop if this is a root node.
                while (linkGraph[currentIdx].Parents.Count != 0)
                {
                    // Get the link edge index from the parent to the current node.
                    for (int i = 0; i < linkGraph[currentIdx].Parents.Count; ++i)
                    {
                        if (linkGraph[currentIdx].Parents[i] == parentIdx)
                        {
                            linkEdges.Add(linkGraph[currentIdx].ParentsEdges[i]);
                            break;
                        }
                    }

                    LinkGraphNode parent = linkGraph[parentIdx];
                    // Increment the count of reclassified children for the parent node.
                    parent.ChildrenReclassified++;

                    if (parent.Bundle.LinkCount > 2)
                    {
                        hasFork = true;
                    }

                    if (parent.HasExit)
                    {
                        hasExit = true;
                    }

                    // Check if the parent has a valid classification (contains non-link edges) or has
                    // more than one child or parent (and is not the root).
                    if (parent.Classification != AbsurdRoadClass || parent.Children.Count > 1 ||
                        parent.Parents.Count != 1)
                    {
                        // Update the parent classification.
                        parent.Classification = Math.Min(parent.Classification, leaf.Classification);
                        // Add the parent to the leaves queue if all the children have already been
                        // tested for reclassification.
                        if (parent.ChildrenReclassified == parent.Children.Count)
                        {
                            leaves.Enqueue(parentIdx);
                        }

                        currentIdx = parentIdx;
                        break;
                    }

                    // Set the current node to the parent - continue to move up the tree.
                    currentIdx = parentIdx;
                    // We would have exited the loop earlier if the number of parents wasn't equal to 1.
                    parentIdx = linkGraph[currentIdx].Parents[0];
                }

                // Check the non-link count.
                endsHaveNonLink = endsHaveNonLink && linkGraph[currentIdx].Bundle.NonLinkCount > 0;

                // The leaf classification may be invalid in case of a cycle; use the parent's
                // classification instead, or just skip these links.
                uint leafClassification = leaf.Classification;
                if (leafClassification == AbsurdRoadClass)
                {
                    if (linkGraph[currentIdx].Classification != AbsurdRoadClass)
                    {
                        leafClassification = linkGraph[currentIdx].Classification;
                    }
                    else
                    {
                        continue;
                    }
                }

                uint rc = Math.Max(exitClassification, leafClassification);
                if (rc == AbsurdRoadClass)
                {
                    // LOG_ERROR("Trying to reclassify to invalid road class!");
                    continue;
                }

                // Test if this link is a turn channel. The classification cannot be trunk or motorway.
                // No nodes can be marked as having an exit sign. None of the nodes along the path can
                // have more than 2 links (fork). The end nodes must have a non-link edge.
                bool turnChannel = false;
                if (inferTurnChannels && rc > (uint)RoadClass.Trunk && !hasFork && !hasExit && endsHaveNonLink)
                {
                    turnChannel = IsTurnChannel(data, linkEdges);
                }

                // Reclassify the link edges to the new classification.
                foreach (uint edgeIdx in linkEdges)
                {
                    Edge edge = data.Edges[(int)edgeIdx];

                    // Reclassify the edge (if reclassify_links is true).
                    if (reclassifyLinks && rc > edge.Attributes.Importance)
                    {
                        edge.Attributes.Importance = rc < (uint)RoadClass.Unclassified
                            ? rc
                            : (uint)RoadClass.Tertiary;

                        ++reclassCount;
                    }

                    if (turnChannel)
                    {
                        edge.Attributes.TurnChannel = true;
                        ++tcCount;
                    }

                    // Mark the edge so we don't try to reclassify it again. Copy the updated edge back
                    // to the sequence (here: the managed list slot).
                    edge.Attributes.ReclassLink = true;
                    data.Edges[(int)edgeIdx] = edge;
                }
            } // for each leaf parent
        } // for each leaf

        return (reclassCount, tcCount);
    }

    // ------------------------------------------------------------------
    // GetTagTokens (midgard util used in linkclassification.cc)
    // ------------------------------------------------------------------

    // GetTagTokens: split a tag value on a delimiter (default ';'), keeping every token (matches the
    // midgard GetTagTokens used in linkclassification.cc). An empty value yields no tokens.
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
}
