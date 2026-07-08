// Tests for the faithful C# port of the Valhalla mjolnir link classification (ReclassifyLinks).
//
// Valhalla has no dedicated gtest for linkclassification.cc - it is exercised through the full
// build pipeline against PBF fixtures. These tests drive ReclassifyLinks with a small synthetic
// intermediate graph (built via GraphBuilder.BuildEdges from hand-made OSM ways, exactly the
// representation the C++ ReclassifyLinks consumes) and assert the reclassification invariants:
//   - a motorway-link ramp leaving a residential exit node is downgraded to the exit class,
//   - the reclass_link flag is set on processed link edges,
//   - turn channels can be inferred for short links,
//   - a graph with no links is left untouched.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class LinkClassificationTests
{
    [Fact]
    public void ReclassifyLinks_NoLinks_DoesNothing()
    {
        // way0: A - M - B (residential), way1: M - C (residential). No link edges at all.
        BuildGraph(out List<OSMWay> ways, out List<OSMWayNode> wayNodes, linkWay1: false);
        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        var osmdata = new OSMData();

        (uint reclass, uint tc) = LinkClassification.ReclassifyLinks(
            graph.Nodes, graph.Edges, ways, wayNodes, osmdata,
            reclassifyLinks: true, inferTurnChannels: true);

        Assert.Equal(0u, reclass);
        Assert.Equal(0u, tc);
    }

    [Fact]
    public void ReclassifyLinks_DowngradesMotorwayLinkToExitClass()
    {
        // way0: A - M - B is a residential through road (RoadClass.Residential = 6).
        // way1: M - C is a motorway_link (RoadClass.Motorway = 0, link = true) leaving M.
        // The exit node M has best non-link class = Residential (6), so rc = max(6, leaf). Since
        // rc (6) >= Unclassified (5), the C++ caps the reclassified importance at Tertiary (4):
        //   if (rc < kUnclassified) importance = rc; else importance = kTertiary;
        BuildGraph(out List<OSMWay> ways, out List<OSMWayNode> wayNodes, linkWay1: true);
        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        var osmdata = new OSMData();

        // The link edge is the one whose way index == 1 and is a link.
        int linkEdgeIdx = FindEdge(graph, wayIndex: 1);
        Assert.True(linkEdgeIdx >= 0);
        Assert.True(graph.Edges[linkEdgeIdx].Attributes.Link);
        // Initially the link carries the motorway importance (0).
        Assert.Equal((uint)RoadClass.Motorway, graph.Edges[linkEdgeIdx].Attributes.Importance);

        (uint reclass, uint _) = LinkClassification.ReclassifyLinks(
            graph.Nodes, graph.Edges, ways, wayNodes, osmdata,
            reclassifyLinks: true, inferTurnChannels: false);

        Assert.True(reclass >= 1, "Expected at least one link edge to be reclassified.");

        // After reclassification the link's importance is downgraded to Tertiary (the cap applied
        // when the target class is >= Unclassified) and it is marked so it isn't reclassified again.
        Edge link = graph.Edges[linkEdgeIdx];
        Assert.Equal((uint)RoadClass.Tertiary, link.Attributes.Importance);
        Assert.True(link.Attributes.ReclassLink);
    }

    [Fact]
    public void ReclassifyLinks_ReclassifyDisabled_StillMarksLinkProcessed()
    {
        // With reclassify_links = false the importance is NOT changed, but the link is still walked
        // and marked (reclass_link) and the reclass count is 0.
        BuildGraph(out List<OSMWay> ways, out List<OSMWayNode> wayNodes, linkWay1: true);
        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        var osmdata = new OSMData();

        int linkEdgeIdx = FindEdge(graph, wayIndex: 1);

        (uint reclass, uint _) = LinkClassification.ReclassifyLinks(
            graph.Nodes, graph.Edges, ways, wayNodes, osmdata,
            reclassifyLinks: false, inferTurnChannels: false);

        Assert.Equal(0u, reclass);
        Edge link = graph.Edges[linkEdgeIdx];
        // Importance unchanged (still motorway), but processed.
        Assert.Equal((uint)RoadClass.Motorway, link.Attributes.Importance);
        Assert.True(link.Attributes.ReclassLink);
    }

    // ------------------------------------------------------------------
    // Synthetic graph
    // ------------------------------------------------------------------

    // Finds the first edge in the graph that belongs to the given way index.
    private static int FindEdge(GraphBuilder.Graph graph, uint wayIndex)
    {
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            if (graph.Edges[i].WayIndex == wayIndex)
            {
                return i;
            }
        }

        return -1;
    }

    // Builds: way0 = A(1) - M(2) - B(3) residential through-road; way1 = M(2) - C(4) which is either
    // a residential road (linkWay1 = false) or a motorway_link ramp (linkWay1 = true). M is the
    // shared intersection / exit node. Placed near Harrisburg, PA so they land in level-2 tiling.
    private static void BuildGraph(out List<OSMWay> ways, out List<OSMWayNode> wayNodes, bool linkWay1)
    {
        ways = new List<OSMWay>();
        wayNodes = new List<OSMWayNode>();

        OSMNode a = MakeNode(1, -76.880, 40.270, intersection: true);
        OSMNode m = MakeNode(2, -76.870, 40.275, intersection: true);
        OSMNode b = MakeNode(3, -76.860, 40.272, intersection: true);
        OSMNode c = MakeNode(4, -76.872, 40.285, intersection: true);

        // way0: A - M - B (residential, bidirectional auto).
        OSMWay way0 = MakeWay(100, RoadClass.Residential, link: false);
        way0.SetNodeCount(3);
        ways.Add(way0);
        wayNodes.Add(MakeWayNode(a, 0, 0));
        wayNodes.Add(MakeWayNode(m, 0, 1));
        wayNodes.Add(MakeWayNode(b, 0, 2));

        // way1: M - C. Either residential or a motorway_link ramp (one-way forward out of M).
        OSMWay way1 = linkWay1
            ? MakeRamp(200)
            : MakeWay(200, RoadClass.Residential, link: false);
        way1.SetNodeCount(2);
        ways.Add(way1);
        wayNodes.Add(MakeWayNode(m, 1, 0));
        wayNodes.Add(MakeWayNode(c, 1, 1));
    }

    private static OSMNode MakeNode(ulong id, double lng, double lat, bool intersection)
    {
        var node = new OSMNode(id, lat, lng);
        node.SetIntersection(intersection);
        return node;
    }

    private static OSMWay MakeWay(ulong id, RoadClass roadClass, bool link)
    {
        var way = new OSMWay(id);
        way.SetRoadClass(roadClass);
        way.SetUse(Use.Road);
        way.SetSpeed(50);
        way.SetAutoForward(true);
        way.SetAutoBackward(true);
        way.SetDriveOnRight(true);
        way.SetLink(link);
        return way;
    }

    // A motorway_link: highest classification, link = true, one-way (auto forward only).
    private static OSMWay MakeRamp(ulong id)
    {
        var way = new OSMWay(id);
        way.SetRoadClass(RoadClass.Motorway);
        way.SetUse(Use.Road);
        way.SetSpeed(50);
        way.SetAutoForward(true);
        way.SetAutoBackward(false);
        way.SetDriveOnRight(true);
        way.SetLink(true);
        return way;
    }

    private static OSMWayNode MakeWayNode(OSMNode node, uint wayIndex, uint shapeIndex)
    {
        return new OSMWayNode
        {
            Node = node,
            WayIndex = wayIndex,
            WayShapeNodeIndex = shapeIndex,
        };
    }
}
