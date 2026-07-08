// Faithful C# port of Valhalla mjolnir node_expander.h + src/mjolnir/node_expander.cc @ 3.7.0.
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/node_expander.h
//   F:/github/valhalla/src/mjolnir/node_expander.cc
//
// This defines the INTERMEDIATE GRAPH REPRESENTATION used by the mjolnir GraphBuilder before
// the final baldr tile records are produced:
//
//   - Edge / EdgeAttributes : an edge in the temporary graph. Connects two "graph" nodes (OSM
//     nodes that form an intersection or are the end of a way). OSM nodes with fewer than 2 uses
//     become shape points along the edge.
//   - Node                  : an OSM node lifted into the temporary graph, tagged with the
//     edge it starts (start_of) and/or ends (end_of), its GraphId and grid id.
//   - NodeBundle            : the result of amalgamating all the duplicate Node records that
//     share an OSM id, plus all the edges that touch that node (collect_node_edges).
//
// PORT-NOTE: the C++ structs are POD records spilled to midgard::sequence temp files (Edge/Node
// are written to nodes.bin / edges.bin). This on-device port keeps them as managed objects held
// in List<Edge> / List<Node>, matching the established mjolnir front-end port (PBFGraphParser).
// The EdgeAttributes bit-field is reproduced as plain fields; only the sub-fields and the EXACT
// Edge::operator< sort order are load-bearing for tile fidelity (collect_node_edges relies on
// the std::map<Edge,size_t> ordering to assign local edge indexes).

using System;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Sortable attributes of an <see cref="Edge"/> in the temporary graph. Faithful port of the C++
/// <c>Edge::EdgeAttributes</c> bit-field. The sub-fields are reproduced as plain fields; only the
/// values and the <see cref="Edge"/> sort order matter for tile fidelity.
/// </summary>
public struct EdgeAttributes
{
    /// <summary>Number of lat,lng shape points on the edge (16 bits).</summary>
    public uint LlCount;

    /// <summary>Road class / importance (3 bits).</summary>
    public uint Importance;

    /// <summary>Traffic signal present at a non-intersection shape node.</summary>
    public bool TrafficSignal;

    /// <summary>Forward traffic signal flag.</summary>
    public bool ForwardSignal;

    /// <summary>Backward traffic signal flag.</summary>
    public bool BackwardSignal;

    /// <summary>Stop sign present at a non-intersection shape node.</summary>
    public bool StopSign;

    /// <summary>Forward stop sign flag.</summary>
    public bool ForwardStop;

    /// <summary>Backward stop sign flag.</summary>
    public bool BackwardStop;

    /// <summary>Yield sign present at a non-intersection shape node.</summary>
    public bool YieldSign;

    /// <summary>Forward yield sign flag.</summary>
    public bool ForwardYield;

    /// <summary>Backward yield sign flag.</summary>
    public bool BackwardYield;

    /// <summary>Direction flag for stop/yield (does the sign apply directionally).</summary>
    public bool Direction;

    /// <summary>Is this a link edge (ramp/turn channel candidate).</summary>
    public bool Link;

    /// <summary>Has the edge been reclassified as a link.</summary>
    public bool ReclassLink;

    /// <summary>Does the edge have names.</summary>
    public bool HasNames;

    /// <summary>Set during <c>collect_node_edges</c> based on the source node (drive forward).</summary>
    public bool DriveForward;

    /// <summary>True if this is a link edge short enough to be internal to an intersection.</summary>
    public bool ShortLink;

    /// <summary>True if the edge is a drivable ferry/rail edge.</summary>
    public bool DrivableFerry;

    /// <summary>Has the edge been reclassified due to a ferry connection (removes dest_only).</summary>
    public bool ReclassFerry;

    /// <summary>Link edge should be a turn channel.</summary>
    public bool TurnChannel;

    /// <summary>True if first edge of a way.</summary>
    public bool WayBegin;

    /// <summary>True if last edge of a way.</summary>
    public bool WayEnd;

    /// <summary>The road class for hierarchies; defaults to kInvalidRoadClass. (4 bits)</summary>
    public uint ImportanceHierarchy;
}

/// <summary>
/// An edge in the temporary graph. Connects two nodes that have 2 or more "uses" (i.e. the node
/// forms an intersection or is the end of an OSM way). OSM nodes with fewer than 2 uses become a
/// shape point (lat,lng) along the edge. Faithful port of the C++ <c>struct Edge</c>.
/// </summary>
public struct Edge : IComparable<Edge>
{
    /// <summary>Index into the list of OSM way information.</summary>
    public uint WayIndex;

    /// <summary>Index of the first lat,lng into the GraphBuilder lat/lngs (way_nodes index).</summary>
    public uint LlIndex;

    /// <summary>Attributes needed to sort the edges.</summary>
    public EdgeAttributes Attributes;

    /// <summary>Index of the source (start) node of the edge.</summary>
    public uint SourceNode;

    /// <summary>Index of the target (end) node of the edge.</summary>
    public uint TargetNode;

    /// <summary>The access of the edge in the forward direction.</summary>
    public ushort FwdAccess;

    /// <summary>The access of the edge in the reverse direction.</summary>
    public ushort RevAccess;

    /// <summary>
    /// For now you can't be valid if you don't have any shape. Returns true if the edge has at
    /// least one shape point. Faithful port of <c>Edge::is_valid</c>.
    /// </summary>
    public readonly bool IsValid() => Attributes.LlCount > 0;

    /// <summary>
    /// Construct a new edge. Target node and additional lat,lngs will be filled in later. Faithful
    /// port of the static <c>Edge::make_edge</c>.
    /// </summary>
    /// <param name="wayIndex">Index into the list of OSM ways.</param>
    /// <param name="llIndex">Index of the first lat,lng (way_nodes index) along the edge.</param>
    /// <param name="way">OSM way info generated from parsing OSM tags.</param>
    /// <param name="inferTurnChannels">Whether turn channels are being inferred.</param>
    /// <returns>A newly constructed edge.</returns>
    public static Edge MakeEdge(uint wayIndex, uint llIndex, OSMWay way, bool inferTurnChannels)
    {
        // TODO(nils): include a "motorvehicle_fwd/rev" in lua/OSMWay?
        bool driveFwd = way.AutoForward() || way.TruckForward() || way.BusForward() ||
                        way.MopedForward() || way.MotorcycleForward() || way.HovForward() ||
                        way.TaxiForward();
        bool driveRev = way.AutoBackward() || way.TruckBackward() || way.BusBackward() ||
                        way.MopedBackward() || way.MotorcycleBackward() || way.HovBackward() ||
                        way.TaxiBackward();

        var e = default(Edge);
        e.WayIndex = wayIndex;
        e.LlIndex = llIndex;
        e.Attributes.LlCount = 1;
        e.Attributes.Importance = (uint)way.RoadClassValue();
        e.Attributes.Link = way.Link();
        e.Attributes.DrivableFerry = (way.Ferry() || way.Rail()) && (driveFwd || driveRev);
        e.Attributes.ReclassLink = false;
        e.Attributes.ReclassFerry = false;
        e.Attributes.ImportanceHierarchy = (uint)RoadClass.Invalid;
        e.Attributes.HasNames =
            way.NameIndex != 0 || way.AltNameIndex != 0 || way.OfficialNameIndex != 0 ||
            way.RefIndex != 0 || way.IntRefIndex != 0;

        // If this data has turn_channels set and we are not inferring turn channels then we need to
        // use the flag. Otherwise the turn_channel is set in the reclassify links. Also, an edge
        // can't be a ramp and a turn channel.
        e.Attributes.TurnChannel = !inferTurnChannels && way.TurnChannel() && !way.Link();

        // Set the access masks.
        e.FwdAccess = 0;
        e.RevAccess = 0;

        // Don't set access for emergency uses.
        if (way.UseValue() == Use.EmergencyAccess)
        {
            return e;
        }

        if (way.AutoForward())
        {
            e.FwdAccess |= GraphConstants.AutoAccess;
        }

        if (way.AutoBackward())
        {
            e.RevAccess |= GraphConstants.AutoAccess;
        }

        if (way.TruckForward())
        {
            e.FwdAccess |= GraphConstants.TruckAccess;
        }

        if (way.TruckBackward())
        {
            e.RevAccess |= GraphConstants.TruckAccess;
        }

        if (way.BusForward())
        {
            e.FwdAccess |= GraphConstants.BusAccess;
        }

        if (way.BusBackward())
        {
            e.RevAccess |= GraphConstants.BusAccess;
        }

        if (way.MopedForward())
        {
            e.FwdAccess |= GraphConstants.MopedAccess;
        }

        if (way.MopedBackward())
        {
            e.RevAccess |= GraphConstants.MopedAccess;
        }

        if (way.MotorcycleForward())
        {
            e.FwdAccess |= GraphConstants.MotorcycleAccess;
        }

        if (way.MotorcycleBackward())
        {
            e.RevAccess |= GraphConstants.MotorcycleAccess;
        }

        if (way.HovForward())
        {
            e.FwdAccess |= GraphConstants.HovAccess;
        }

        if (way.HovBackward())
        {
            e.RevAccess |= GraphConstants.HovAccess;
        }

        if (way.TaxiForward())
        {
            e.FwdAccess |= GraphConstants.TaxiAccess;
        }

        if (way.TaxiBackward())
        {
            e.RevAccess |= GraphConstants.TaxiAccess;
        }

        return e;
    }

    /// <summary>
    /// For sorting edges. By driveability (forward), importance, and presence of names. Faithful
    /// port of the C++ <c>Edge::operator&lt;</c>.
    /// </summary>
    public readonly int CompareTo(Edge other)
    {
        // Is this a loop?
        if (TargetNode == other.TargetNode && SourceNode == other.SourceNode &&
            SourceNode == TargetNode)
        {
            // C++ returns false (i.e. not less-than). For a total order we treat equal here.
            return 0;
        }

        // Sort by driveability (forward, importance, has_names).
        bool d = Attributes.DriveForward;
        bool od = other.Attributes.DriveForward;
        if (d == od)
        {
            if (Attributes.Importance == other.Attributes.Importance)
            {
                // Equal importance - check presence of names.
                if (Attributes.HasNames == other.Attributes.HasNames)
                {
                    return LlIndex.CompareTo(other.LlIndex);
                }

                // return has_names > other.has_names; (true sorts first)
                return (Attributes.HasNames ? 1 : 0) > (other.Attributes.HasNames ? 1 : 0) ? -1 : 1;
            }

            return Attributes.Importance.CompareTo(other.Attributes.Importance);
        }

        // return d > od; (driveforward true sorts first)
        return (d ? 1 : 0) > (od ? 1 : 0) ? -1 : 1;
    }

    /// <summary>Less-than operator mirroring C++ <c>operator&lt;</c>.</summary>
    public static bool operator <(Edge a, Edge b) => a.CompareTo(b) < 0;

    /// <summary>Greater-than operator.</summary>
    public static bool operator >(Edge a, Edge b) => a.CompareTo(b) > 0;
}

/// <summary>
/// A node within the temporary graph. Faithful port of the C++ <c>struct Node</c>. Holds the
/// underlying OSM node plus which graph edge this node starts and/or ends, its GraphId, and the
/// grid id used for spatial sorting within a tile.
/// </summary>
public struct Node
{
    /// <summary>Sentinel value meaning "this node does not start/end an edge" (C++ uint32 -1).</summary>
    public const uint NoEdge = uint.MaxValue;

    /// <summary>The underlying OSM node and attributes.</summary>
    public OSMNode OsmNode;

    /// <summary>The graph edge that this node starts.</summary>
    public uint StartOf;

    /// <summary>The graph edge that this node ends.</summary>
    public uint EndOf;

    /// <summary>The GraphId of the node.</summary>
    public GraphId GraphId;

    /// <summary>Grid id within the tile (used for spatial node sorting).</summary>
    public uint GridId;

    /// <summary>True if this node starts an edge. Faithful port of <c>Node::is_start</c>.</summary>
    public readonly bool IsStart() => StartOf != NoEdge;

    /// <summary>True if this node ends an edge. Faithful port of <c>Node::is_end</c>.</summary>
    public readonly bool IsEnd() => EndOf != NoEdge;
}

/// <summary>
/// Collects all the edges that start or end at a node. Faithful port of the C++
/// <c>struct node_bundle</c> (which derives from <c>Node</c>).
/// </summary>
public sealed class NodeBundle
{
    /// <summary>The merged node (attributes amalgamated from all duplicate Node records).</summary>
    public Node Node;

    /// <summary>Number of duplicate Node records that share this node's OSM id.</summary>
    public int NodeCount;

    /// <summary>Number of link edges at this node.</summary>
    public int LinkCount;

    /// <summary>Number of non-link edges at this node.</summary>
    public int NonLinkCount;

    /// <summary>Number of edges that can be driven forward away from this node.</summary>
    public int DriveForwardCount;

    /// <summary>
    /// The edges that touch this node, keyed by the temporary <see cref="Edge"/> (in
    /// <c>Edge::operator&lt;</c> order) with the value being the edge index in the edges list. This
    /// is a sorted map exactly like the C++ <c>std::map&lt;Edge, size_t&gt;</c>; iteration order
    /// determines the local edge indexes assigned in the tile.
    /// </summary>
    public System.Collections.Generic.SortedList<Edge, ulong> NodeEdges { get; } =
        new System.Collections.Generic.SortedList<Edge, ulong>(new EdgeComparer());

    private sealed class EdgeComparer : System.Collections.Generic.IComparer<Edge>
    {
        public int Compare(Edge x, Edge y)
        {
            int c = x.CompareTo(y);
            // SortedList requires a strict weak ordering with no duplicate keys. The C++
            // std::map<Edge,size_t> can collide on equal keys (loops); break ties by way/ll index
            // so every edge gets a distinct slot while preserving the primary ordering.
            if (c != 0)
            {
                return c;
            }

            c = x.LlIndex.CompareTo(y.LlIndex);
            if (c != 0)
            {
                return c;
            }

            c = x.SourceNode.CompareTo(y.SourceNode);
            return c != 0 ? c : x.TargetNode.CompareTo(y.TargetNode);
        }
    }
}

/// <summary>
/// Node expansion helpers. Faithful port of the C++ free function <c>collect_node_edges</c> from
/// <c>src/mjolnir/node_expander.cc</c>.
/// </summary>
public static class NodeExpander
{
    /// <summary>
    /// Collect node information and the edges from the node, starting at <paramref name="nodeIndex"/>
    /// in <paramref name="nodes"/>. Returns a <see cref="NodeBundle"/> and advances over all the
    /// duplicate node records that share the same OSM id. Faithful port of
    /// <c>collect_node_edges</c>.
    /// </summary>
    /// <param name="nodeIndex">Index of the first (correctly merged) node record.</param>
    /// <param name="nodes">The list of node records.</param>
    /// <param name="edges">The list of edge records.</param>
    /// <returns>The collected node bundle.</returns>
    public static NodeBundle CollectNodeEdges(
        int nodeIndex,
        System.Collections.Generic.IReadOnlyList<Node> nodes,
        System.Collections.Generic.IReadOnlyList<Edge> edges)
    {
        // Copy out the first node's attributes (as they are the correctly merged one).
        var bundle = new NodeBundle { Node = nodes[nodeIndex] };

        // For each node with the same id (duplicate).
        for (int itr = nodeIndex;
             itr < nodes.Count && nodes[itr].OsmNode.Osmid == bundle.Node.OsmNode.Osmid;
             ++itr)
        {
            Node node = nodes[itr];
            ++bundle.NodeCount;

            if (node.IsStart())
            {
                Edge edge = edges[(int)node.StartOf];
                // Set driveforward - this edge is traversed in forward direction.
                edge.Attributes.DriveForward = (edge.FwdAccess & GraphConstants.AutoAccess) != 0;
                bundle.NodeEdges[edge] = node.StartOf;

                OSMNode bn = bundle.Node.OsmNode;
                bn.SetLinkEdge(bn.LinkEdge() || edge.Attributes.Link);
                bn.SetFerryEdge(bn.FerryEdge() || edge.Attributes.DrivableFerry);
                bn.SetShortlink(bn.Shortlink() || edge.Attributes.ShortLink);

                // Do not count non-drivable (e.g. emergency service roads) as a non-link edge.
                if (edge.Attributes.DriveForward || (edge.RevAccess & GraphConstants.AutoAccess) != 0)
                {
                    bn.SetNonLinkEdge(bn.NonLinkEdge() || !edge.Attributes.Link);
                }

                // Non-ferry edges need access to _some_ vehicular mode.
                if ((edge.FwdAccess & GraphConstants.VehicularAccess) != 0 ||
                    (edge.RevAccess & GraphConstants.VehicularAccess) != 0)
                {
                    bn.SetNonFerryEdge(bn.NonFerryEdge() || !edge.Attributes.DrivableFerry);
                }

                bundle.Node.OsmNode = bn;

                if (edge.Attributes.Link)
                {
                    bundle.LinkCount++;
                }
                else
                {
                    bundle.NonLinkCount++;
                }

                if (edge.Attributes.DriveForward)
                {
                    bundle.DriveForwardCount++;
                }
            }

            if (node.IsEnd())
            {
                Edge edge = edges[(int)node.EndOf];
                // Set driveforward - this edge is traversed in reverse direction.
                edge.Attributes.DriveForward = (edge.RevAccess & GraphConstants.AutoAccess) != 0;
                bundle.NodeEdges[edge] = node.EndOf;

                OSMNode bn = bundle.Node.OsmNode;
                bn.SetLinkEdge(bn.LinkEdge() || edge.Attributes.Link);
                bn.SetFerryEdge(bn.FerryEdge() || edge.Attributes.DrivableFerry);
                bn.SetShortlink(bn.Shortlink() || edge.Attributes.ShortLink);

                // Do not count non-drivable (e.g. emergency service roads) as a non-link edge.
                if ((edge.FwdAccess & GraphConstants.AutoAccess) != 0 || edge.Attributes.DriveForward)
                {
                    bn.SetNonLinkEdge(bn.NonLinkEdge() || !edge.Attributes.Link);
                }

                // Non-ferry edges need access to _some_ vehicular mode.
                if ((edge.FwdAccess & GraphConstants.VehicularAccess) != 0 ||
                    (edge.RevAccess & GraphConstants.VehicularAccess) != 0)
                {
                    bn.SetNonFerryEdge(bn.NonFerryEdge() || !edge.Attributes.DrivableFerry);
                }

                bundle.Node.OsmNode = bn;

                if (edge.Attributes.Link)
                {
                    bundle.LinkCount++;
                }
                else
                {
                    bundle.NonLinkCount++;
                }

                if (edge.Attributes.DriveForward)
                {
                    bundle.DriveForwardCount++;
                }
            }
        }

        return bundle;
    }
}
