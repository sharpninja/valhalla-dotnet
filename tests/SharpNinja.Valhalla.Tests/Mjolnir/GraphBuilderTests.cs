// Tests for the faithful C# port of the Valhalla mjolnir GraphBuilder (initial routing graph
// construction + byte-compatible tile writing).
// Source gtests: valhalla/test/graphbuilder.cc (TestDEBuilderLength, TestConstructEdges).
//
// The C++ TestConstructEdges/Subset tests are driven by the harrisburg.osm.pbf fixture + the
// full PBFGraphParser. Those exercise the same code these tests exercise (ConstructEdges /
// SortGraph / Build) but require the PBF binary fixture. Here we drive the pipeline with a small
// synthetic, hand-built graph so we can assert the structural invariants AND round-trip the
// written tile through the ported Baldr GraphTile reader (proving byte compatibility of the
// write side with the read side). TestDEBuilderLength is ported directly.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class GraphBuilderTests
{
    // ---- TestDEBuilderLength (ported directly from graphbuilder.cc) ----

    [Fact]
    public void DirectedEdgeBuilder_AcceptsValidLength()
    {
        var shape1 = new List<PointLL>
        {
            new(-160.096619, 21.997619),
            new(-90.037697, 41.004531),
            new(-160.096619, 21.997619),
        };

        uint len = (uint)(PointLlPolyline2.Length(shape1) + 0.5);

        // Should not throw - length is within bounds after the +1 protection / clamp.
        DirectedEdge de = DirectedEdgeBuilder.Build(
            new OSMWay(),
            new GraphId(123, 2, 8),
            true,
            len,
            1,
            1,
            Use.Road,
            RoadClass.Motorway,
            0,
            false,
            false,
            false,
            false,
            0,
            0,
            false,
            RoadClass.Invalid);

        Assert.Equal(new GraphId(123, 2, 8), de.EndNode);
    }

    [Fact]
    public void DirectedEdgeBuilder_ThrowsOnExcessiveLength()
    {
        var shape2 = new List<PointLL>
        {
            new(-160.096619, 21.997619),
            new(-90.037697, 41.004531),
            new(-160.096619, 21.997619),
            new(-90.037697, 41.004531),
        };

        uint len = (uint)(PointLlPolyline2.Length(shape2) + 0.5);

        Assert.Throws<System.InvalidOperationException>(() =>
            DirectedEdgeBuilder.Build(
                new OSMWay(),
                new GraphId(123, 2, 8),
                true,
                len,
                1,
                1,
                Use.Road,
                RoadClass.Motorway,
                0,
                false,
                false,
                false,
                false,
                0,
                0,
                false,
                RoadClass.Invalid));
    }

    // ---- ConstructEdges / SortGraph / Build pipeline (synthetic) ----

    [Fact]
    public void ConstructEdges_SplitsWayAtIntersection_AndCountsNodesAndEdges()
    {
        // Two ways sharing a middle intersection node:
        //   way0: A - M - B   (M is an intersection => splits into A-M and M-B)
        //   way1: M - C       (M is the shared intersection)
        BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);

        // way0 splits into 2 edges (A-M, M-B); way1 is 1 edge (M-C) => 3 edges total.
        Assert.Equal(3, graph.Edges.Count);

        // The unique graph nodes are A, M, B, C => 4 distinct nodes (M is shared/merged).
        // graph.Tiles maps the single tile to the first node index.
        Assert.Single(graph.Tiles);

        // Every edge must have its source/target node wired (not the default 0/0 unless valid).
        foreach (Edge e in graph.Edges)
        {
            Assert.True(e.IsValid());
        }
    }

    [Fact]
    public void Build_WritesTile_RoundTripsThroughReader()
    {
        BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);
        var osmdata = new OSMData();

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);

        Assert.NotEmpty(tiles);

        foreach (KeyValuePair<GraphId, byte[]> kv in tiles)
        {
            GraphId tileId = kv.Key;
            byte[] blob = kv.Value;

            // The reader must parse the blob the builder wrote.
            GraphTile tile = GraphTile.Create(tileId, blob);

            GraphTileHeader header = tile.Header();
            Assert.Equal(tileId.Tileid(), header.Graphid().Tileid());
            Assert.Equal(tileId.Level(), header.Graphid().Level());

            uint nodeCount = tile.NodeCount();
            uint edgeCount = tile.DirectedEdgeCount();
            Assert.True(nodeCount > 0);
            Assert.True(edgeCount > 0);

            // Each node's edge_index + edge_count must stay within the directed edge array, and
            // each directed edge's end node id must be valid within the tile (same-tile graph).
            uint totalEdgesFromNodes = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                NodeInfo ni = tile.Node(i);
                Assert.True(ni.EdgeIndex + ni.EdgeCount <= edgeCount);
                totalEdgesFromNodes += ni.EdgeCount;

                for (uint e = 0; e < ni.EdgeCount; e++)
                {
                    DirectedEdge de = tile.DirectedEdge((int)(ni.EdgeIndex + e));
                    Assert.True(de.EndNode.Id() < nodeCount);
                    Assert.True(de.Length >= 1);

                    // The edge info offset must be parseable: read back the shape + way id.
                    EdgeInfo info = tile.EdgeInfo(de);
                    Assert.True(info.Shape().Count >= 2);
                }
            }

            // The sum of node edge counts equals the directed edge count (forward-star invariant).
            Assert.Equal(edgeCount, totalEdgesFromNodes);
        }
    }

    [Fact]
    public void Build_PreservesWayNameInEdgeInfo()
    {
        var osmdata = new OSMData();

        // Way "Main Street" between two intersections, plus a cross way so the endpoints intersect.
        uint mainNameIndex = osmdata.NameOffsetMap.Index("Main Street");

        BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);
        // Give way0 a name index resolvable via osmdata.name_offset_map.
        ways[0].NameIndex = mainNameIndex;

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);

        bool foundMain = false;
        foreach (KeyValuePair<GraphId, byte[]> kv in tiles)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int e = 0; e < tile.DirectedEdgeCount(); e++)
            {
                DirectedEdge de = tile.DirectedEdge(e);
                EdgeInfo info = tile.EdgeInfo(de);
                if (info.GetNames().Contains("Main Street"))
                {
                    foundMain = true;
                }
            }
        }

        Assert.True(foundMain, "Expected the 'Main Street' name to round-trip into EdgeInfo names.");
    }

    [Fact]
    public void GetRef_MergesWayRefWithRelationDirection()
    {
        // way ref "US 51;I 57", relation ref "US 51|north;I 57|south" => directional refs merged.
        string refs = GraphBuilder.GetRef("US 51;I 57", "US 51|north;I 57|south");
        Assert.Equal("US 51 north;I 57 south", refs);
    }

    [Fact]
    public void GetRef_KeepsWayRefWhenNoRelationDirection()
    {
        string refs = GraphBuilder.GetRef("US 51", string.Empty);
        Assert.Equal("US 51", refs);
    }

    [Fact]
    public void ComputeCurvature_TwoPointShapeHasNoCurvature()
    {
        var shape = new List<PointLL> { new(-76.0, 40.0), new(-76.001, 40.001) };
        Assert.Equal(0u, GraphBuilder.ComputeCurvature(shape));
    }

    // ------------------------------------------------------------------
    // Synthetic input builder
    // ------------------------------------------------------------------

    // Builds a tiny graph: way0 = A(1) - M(2) - B(3); way1 = M(2) - C(4). Node M is an
    // intersection (touched by both ways). All nodes are placed near Harrisburg, PA so they land
    // in the local (level 2) tiling. Each way carries auto access in both directions.
    private static void BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes)
    {
        ways = new List<OSMWay>();
        wayNodes = new List<OSMWayNode>();

        var a = MakeNode(1, -76.880, 40.270, intersection: false);
        var m = MakeNode(2, -76.870, 40.275, intersection: true);
        var b = MakeNode(3, -76.860, 40.272, intersection: false);
        var c = MakeNode(4, -76.872, 40.285, intersection: false);

        // way0: A - M - B (3 nodes). The endpoints A and B are also single-use ends => intersections
        // implicitly (ends of a way are graph nodes). Mark A, B, C as intersections (way ends).
        var aEnd = a;
        aEnd.SetIntersection(true);
        var bEnd = b;
        bEnd.SetIntersection(true);
        var cEnd = c;
        cEnd.SetIntersection(true);

        var way0 = MakeWay(100);
        way0.SetNodeCount(3);
        ways.Add(way0);
        wayNodes.Add(MakeWayNode(aEnd, 0, 0));
        wayNodes.Add(MakeWayNode(m, 0, 1));
        wayNodes.Add(MakeWayNode(bEnd, 0, 2));

        // way1: M - C (2 nodes).
        var way1 = MakeWay(200);
        way1.SetNodeCount(2);
        ways.Add(way1);
        wayNodes.Add(MakeWayNode(m, 1, 0));
        wayNodes.Add(MakeWayNode(cEnd, 1, 1));
    }

    private static OSMNode MakeNode(ulong id, double lng, double lat, bool intersection)
    {
        var node = new OSMNode(id, lat, lng);
        node.SetIntersection(intersection);
        return node;
    }

    private static OSMWay MakeWay(ulong id)
    {
        var way = new OSMWay(id);
        way.SetRoadClass(RoadClass.Residential);
        way.SetUse(Use.Road);
        way.SetSpeed(50);
        way.SetAutoForward(true);
        way.SetAutoBackward(true);
        way.SetDriveOnRight(true);
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
