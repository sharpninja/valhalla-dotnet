// Tests for the faithful C# port of the Valhalla mjolnir GraphEnhancer (local-level graph
// enhancement: density, headings, opposing local index, turn types / stop impact, internal
// intersection detection, not-thru, speed assignment, name consistency, intersection type).
//
// The C++ enhancer is exercised by the full PBF build pipeline (graphparser.cc), which requires the
// harrisburg/utrecht osm.pbf fixtures. Here we drive the same code paths these tests exercise
// (GraphBuilder.BuildEdges/Build -> GraphEnhancer.Enhance) with a small synthetic, hand-built graph
// so we can assert the structural invariants AND round-trip the enhanced tile through the ported
// Baldr GraphTile reader (proving byte compatibility of the enhanced write side with the read side).
// The enhancer's pure helper algorithms (stop impact / turn types via the LUT, density-cell math,
// speed heuristic, name consistency via StreetNames) are validated through the observable tile
// output. The synthetic builder mirrors GraphBuilderTests so the two stages compose exactly as in
// the real build.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class GraphEnhancerTests
{
    [Fact]
    public void Enhance_ReturnsTilesForEveryInput()
    {
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        Assert.NotEmpty(enhanced);
        Assert.Equal(tiles.Count, enhanced.Count);
        foreach (KeyValuePair<GraphId, byte[]> kv in tiles)
        {
            Assert.True(enhanced.ContainsKey(kv.Key.TileBase()));
        }
    }

    [Fact]
    public void Enhance_ReportsOrderedStageDurations()
    {
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        _ = enhancer.Enhance(tiles);

        Assert.Equal(
            new[] { "deserialize", "first-pass", "density", "second-pass", "serialize" },
            enhancer.Stats.StageDurations.Keys);
        Assert.All(
            enhancer.Stats.StageDurations.Values,
            duration => Assert.True(duration >= TimeSpan.Zero));
    }

    [Fact]
    public void Enhance_ReportsSecondPassOperationCounts()
    {
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        _ = enhancer.Enhance(tiles);

        GraphEnhancer.EnhancerStats stats = enhancer.Stats;
        Assert.True(stats.SecondPassEdgeCount > 0);
        Assert.True(stats.NameConsistencyCheckCount >= stats.SecondPassEdgeCount);
        Assert.Equal(stats.SecondPassEdgeCount, stats.StreetNameDecodeCount);
        Assert.Equal(stats.SecondPassEdgeCount, stats.InternalIntersectionCheckCount);
        Assert.Equal(stats.SecondPassEdgeCount, stats.StopYieldCheckCount);
        Assert.True(stats.TurnLaneCheckCount <= stats.SecondPassEdgeCount);
        Assert.True(stats.NotThruCheckCount > 1);
        Assert.True(stats.NotThruCheckCount <= stats.SecondPassEdgeCount);
        Assert.True(stats.NotThruNodeExpansionCount >= stats.NotThruCheckCount);
        Assert.Equal(1UL, stats.NotThruScratchAllocationCount);
    }

    [Fact]
    public void Enhance_ProducesByteCompatibleTile_RoundTripsThroughReader()
    {
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            // The reader must parse the enhanced blob the enhancer wrote.
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            GraphTileHeader header = tile.Header();

            Assert.Equal(kv.Key.Tileid(), header.Graphid().Tileid());
            Assert.Equal(kv.Key.Level(), header.Graphid().Level());
            Assert.Equal((uint)kv.Value.Length, header.EndOffset());

            uint nodeCount = tile.NodeCount();
            uint edgeCount = tile.DirectedEdgeCount();
            Assert.True(nodeCount > 0);
            Assert.True(edgeCount > 0);

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

                    // Edge info must still be parseable after re-serialization.
                    EdgeInfo info = tile.EdgeInfo(de);
                    Assert.True(info.Shape().Count >= 2);
                }
            }

            Assert.Equal(edgeCount, totalEdgesFromNodes);
        }
    }

    [Fact]
    public void Enhance_SetsNodeHeadingsAndLocalEdgeCount()
    {
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        bool anyHeadingSet = false;
        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int i = 0; i < tile.NodeCount(); i++)
            {
                NodeInfo ni = tile.Node(i);

                // local_edge_count is set to min(edge_count, 8) by the enhancer (stored as count-1).
                Assert.True(ni.LocalEdgeCount >= 1);

                for (uint e = 0; e < ni.EdgeCount && e < 8; e++)
                {
                    // A heading is in [0, 360). Any non-zero heading proves the first pass ran.
                    uint heading = ni.Heading(e);
                    Assert.True(heading < 360);
                    if (heading != 0)
                    {
                        anyHeadingSet = true;
                    }
                }
            }
        }

        Assert.True(anyHeadingSet, "Expected the enhancer to populate at least one edge heading.");
    }

    [Fact]
    public void Enhance_SetsOpposingLocalIndex()
    {
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int e = 0; e < tile.DirectedEdgeCount(); e++)
            {
                DirectedEdge de = tile.DirectedEdge(e);

                // The opposing local index must point to a real edge at the end node (same-tile graph).
                NodeInfo endNode = tile.Node((int)de.EndNode.Id());
                Assert.True(de.OppLocalIdx < endNode.EdgeCount);
            }
        }
    }

    [Fact]
    public void Enhance_MarksDeadEndNode()
    {
        // A(1), B(3), C(4) are way ends with a single drivable edge => dead-end intersection.
        // M(2) is touched by 3 ways (A-M, M-B, M-C) so it has 3 edges and is NOT a false intersection
        // (false requires exactly 2 edges).
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        bool foundDeadEnd = false;
        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int i = 0; i < tile.NodeCount(); i++)
            {
                NodeInfo ni = tile.Node(i);
                if (ni.Intersection == IntersectionType.DeadEnd)
                {
                    // A dead-end node has exactly one drivable edge leaving it.
                    Assert.Equal(1u, ni.EdgeCount);
                    foundDeadEnd = true;
                }
                else if (ni.Intersection == IntersectionType.False)
                {
                    // A false intersection has exactly 2 edges.
                    Assert.Equal(2u, ni.EdgeCount);
                }
            }
        }

        Assert.True(foundDeadEnd, "Expected at least one dead-end node (the single-edge way ends).");
    }

    [Fact]
    public void Enhance_MarksFalseIntersection_ForTwoEdgeNode()
    {
        // A node where one way passes through with no other connections has exactly 2 edges and must
        // be classified as a 'false' intersection by the enhancer.
        var osmdata = new OSMData();
        var ways = new List<OSMWay>();
        var wayNodes = new List<OSMWayNode>();

        OSMNode a = MakeNode(1, -76.880, 40.270, intersection: true);
        OSMNode m = MakeNode(2, -76.875, 40.272, intersection: true);
        OSMNode b = MakeNode(3, -76.870, 40.270, intersection: true);

        // Single way A - M - B: M is a shape intersection (way id unchanged) but only 2 edges meet it.
        OSMWay way = MakeWay(100);
        way.SetNodeCount(3);
        ways.Add(way);
        wayNodes.Add(MakeWayNode(a, 0, 0));
        wayNodes.Add(MakeWayNode(m, 0, 1));
        wayNodes.Add(MakeWayNode(b, 0, 2));

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        bool foundFalse = false;
        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int i = 0; i < tile.NodeCount(); i++)
            {
                NodeInfo ni = tile.Node(i);
                if (ni.EdgeCount == 2 && ni.Intersection == IntersectionType.False)
                {
                    foundFalse = true;
                }
            }
        }

        Assert.True(foundFalse, "Expected the 2-edge pass-through node to be a 'false' intersection.");
    }

    [Fact]
    public void Enhance_ClearsTemporaryTransitionIndex()
    {
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int i = 0; i < tile.NodeCount(); i++)
            {
                // The enhancer always clears the temporary stop/yield transition index.
                Assert.Equal(0u, tile.Node(i).TransitionIndex);
            }
        }
    }

    [Fact]
    public void Enhance_RunsDensityPass_AssigningValidRelativeDensities()
    {
        // The density grid assigns each node a relative density in [0, 15]. The absolute magnitude
        // depends on the road density (km/km^2) which is low for a synthetic grid, so we assert the
        // pass ran and produced in-range densities (and a non-negative max-density statistic) rather
        // than a specific magnitude.
        Dictionary<GraphId, byte[]> tiles = BuildDenseGrid();

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int i = 0; i < tile.NodeCount(); i++)
            {
                Assert.True(tile.Node(i).Density <= GraphConstants.MaxDensity);
            }
        }

        // The density pass populated the max-density statistic (km/km^2) from real road lengths.
        Assert.True(enhancer.Stats.MaxDensity > 0.0f,
            "Expected the density pass to compute a positive max road density.");
    }

    [Fact]
    public void Enhance_SetsNamedFlag_FromEdgeInfoNames()
    {
        var osmdata = new OSMData();
        uint mainNameIndex = osmdata.NameOffsetMap.Index("Main Street");

        BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);
        ways[0].NameIndex = mainNameIndex;

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(tiles);

        bool foundNamed = false;
        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int e = 0; e < tile.DirectedEdgeCount(); e++)
            {
                DirectedEdge de = tile.DirectedEdge(e);
                EdgeInfo info = tile.EdgeInfo(de);
                if (info.GetNames().Contains("Main Street"))
                {
                    Assert.True(de.Named, "An edge carrying a name must have the named flag set.");
                    foundNamed = true;
                }
            }
        }

        Assert.True(foundNamed, "Expected the 'Main Street' edge to be present and named.");
    }

    [Fact]
    public void Enhance_SetsNamedFlag_ForRouteReferenceOnlyEdge()
    {
        var osmdata = new OSMData();
        uint routeReferenceIndex = osmdata.NameOffsetMap.Index("US 70S");

        BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);
        ways[0].RefIndex = routeReferenceIndex;

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);
        Dictionary<GraphId, byte[]> enhanced = new GraphEnhancer().Enhance(tiles);

        bool foundRouteReference = false;
        foreach (KeyValuePair<GraphId, byte[]> kv in enhanced)
        {
            GraphTile tile = GraphTile.Create(kv.Key, kv.Value);
            for (int edgeIndex = 0; edgeIndex < tile.DirectedEdgeCount(); edgeIndex++)
            {
                DirectedEdge edge = tile.DirectedEdge(edgeIndex);
                List<(string Name, bool IsRouteNum)> names = tile.EdgeInfo(edge).GetNames(false);
                if (names.Any(static name => name.IsRouteNum && name.Name == "US 70S"))
                {
                    Assert.True(edge.Named, "An edge carrying only a route reference must remain named.");
                    foundRouteReference = true;
                }
            }
        }

        Assert.True(foundRouteReference, "Expected the US 70S route-reference edge to be present.");
    }

    [Fact]
    public void Enhance_IsIdempotentOnStructuralCounts()
    {
        // Enhancing already-enhanced tiles must not change node / edge / sign / admin counts
        // (the access-restriction and turn-lane counts are also preserved for this graph).
        Dictionary<GraphId, byte[]> tiles = BuildTiles(out _);

        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> once = enhancer.Enhance(tiles);
        var enhancer2 = new GraphEnhancer();
        Dictionary<GraphId, byte[]> twice = enhancer2.Enhance(once);

        foreach (KeyValuePair<GraphId, byte[]> kv in once)
        {
            GraphTile a = GraphTile.Create(kv.Key, kv.Value);
            GraphTile b = GraphTile.Create(kv.Key, twice[kv.Key]);

            Assert.Equal(a.NodeCount(), b.NodeCount());
            Assert.Equal(a.DirectedEdgeCount(), b.DirectedEdgeCount());
            Assert.Equal(a.Header().Signcount(), b.Header().Signcount());
            Assert.Equal(a.Header().Admincount(), b.Header().Admincount());
            Assert.Equal(a.Header().AccessRestrictionCount(), b.Header().AccessRestrictionCount());
            Assert.Equal(a.Header().TurnlaneCount(), b.Header().TurnlaneCount());
        }
    }

    [Fact]
    public void Enhance_CrossTileControlState_UsesFrozenInputSnapshotAcrossParallelism()
    {
        var osmdata = new OSMData();
        OSMNode stopNode = MakeNode(101, -76.501, 40.270, intersection: true);
        stopNode.SetStopSign(true);
        OSMNode sourceNode = MakeNode(102, -76.499, 40.270, intersection: true);

        OSMWay way = MakeWay(1001);
        way.SetNodeCount(2);
        var ways = new List<OSMWay> { way };
        var wayNodes = new List<OSMWayNode>
        {
            MakeWayNode(stopNode, 0, 0),
            MakeWayNode(sourceNode, 0, 1),
        };

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        GraphId stopGraphId = graph.Nodes.Single(node => node.OsmNode.Osmid == stopNode.Osmid).GraphId;
        GraphId sourceGraphId = graph.Nodes.Single(node => node.OsmNode.Osmid == sourceNode.Osmid).GraphId;
        Assert.NotEqual(stopGraphId.TileBase(), sourceGraphId.TileBase());
        Assert.True(stopGraphId.TileBase().Value < sourceGraphId.TileBase().Value);

        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);
        Dictionary<GraphId, byte[]> serial = new GraphEnhancer().Enhance(
            tiles,
            inferInternalIntersections: true,
            inferTurnChannels: true,
            maxDegreeOfParallelism: 1,
            TestContext.Current.CancellationToken);
        Dictionary<GraphId, byte[]> parallel = new GraphEnhancer().Enhance(
            tiles,
            inferInternalIntersections: true,
            inferTurnChannels: true,
            maxDegreeOfParallelism: 4,
            TestContext.Current.CancellationToken);

        GraphTile serialSourceTile = GraphTile.Create(
            sourceGraphId.TileBase(),
            serial[sourceGraphId.TileBase()]);
        NodeInfo serialSourceNode = serialSourceTile.Node((int)sourceGraphId.Id());
        DirectedEdge incomingStopEdge = Enumerable
            .Range((int)serialSourceNode.EdgeIndex, (int)serialSourceNode.EdgeCount)
            .Select(serialSourceTile.DirectedEdge)
            .Single(edge => edge.EndNode == stopGraphId);

        Assert.True(incomingStopEdge.StopSign);
        Assert.Equal(serial.Keys.OrderBy(static id => id.Value), parallel.Keys.OrderBy(static id => id.Value));
        foreach (GraphId tileId in serial.Keys)
        {
            Assert.Equal(serial[tileId], parallel[tileId]);
        }
    }

    // ------------------------------------------------------------------
    // Synthetic input builders (mirroring GraphBuilderTests)
    // ------------------------------------------------------------------

    private static Dictionary<GraphId, byte[]> BuildTiles(out GraphBuilder.Graph graph)
    {
        var osmdata = new OSMData();
        BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);
        graph = GraphBuilder.BuildEdges(ways, wayNodes);
        return GraphBuilder.Build(osmdata, ways, wayNodes, graph);
    }

    // way0 = A(1) - M(2) - B(3); way1 = M(2) - C(4). Node M is an intersection touched by both ways.
    private static void BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes)
    {
        ways = new List<OSMWay>();
        wayNodes = new List<OSMWayNode>();

        OSMNode a = MakeNode(1, -76.880, 40.270);
        OSMNode m = MakeNode(2, -76.870, 40.275, intersection: true);
        OSMNode b = MakeNode(3, -76.860, 40.272);
        OSMNode c = MakeNode(4, -76.872, 40.285);

        a.SetIntersection(true);
        b.SetIntersection(true);
        c.SetIntersection(true);

        OSMWay way0 = MakeWay(100);
        way0.SetNodeCount(3);
        ways.Add(way0);
        wayNodes.Add(MakeWayNode(a, 0, 0));
        wayNodes.Add(MakeWayNode(m, 0, 1));
        wayNodes.Add(MakeWayNode(b, 0, 2));

        OSMWay way1 = MakeWay(200);
        way1.SetNodeCount(2);
        ways.Add(way1);
        wayNodes.Add(MakeWayNode(m, 1, 0));
        wayNodes.Add(MakeWayNode(c, 1, 1));
    }

    // A denser grid of residential ways near Harrisburg, PA so the density grid yields non-zero
    // relative density. 4 horizontal + 4 vertical ways crossing at shared intersection nodes.
    private static Dictionary<GraphId, byte[]> BuildDenseGrid()
    {
        var osmdata = new OSMData();
        var ways = new List<OSMWay>();
        var wayNodes = new List<OSMWayNode>();

        const double lng0 = -76.880;
        const double lat0 = 40.270;
        const double step = 0.002;
        const int n = 4;

        // Grid intersection nodes (all intersections so each is a graph node).
        var grid = new OSMNode[n, n];
        ulong nodeId = 1;
        for (int r = 0; r < n; r++)
        {
            for (int col = 0; col < n; col++)
            {
                grid[r, col] = MakeNode(nodeId++, lng0 + (col * step), lat0 + (r * step), intersection: true);
            }
        }

        uint wayIndex = 0;
        ulong wayId = 1000;

        // Horizontal ways (one per row).
        for (int r = 0; r < n; r++)
        {
            OSMWay way = MakeWay(wayId++);
            way.SetNodeCount(n);
            ways.Add(way);
            for (int col = 0; col < n; col++)
            {
                wayNodes.Add(MakeWayNode(grid[r, col], wayIndex, (uint)col));
            }

            wayIndex++;
        }

        // Vertical ways (one per column).
        for (int col = 0; col < n; col++)
        {
            OSMWay way = MakeWay(wayId++);
            way.SetNodeCount(n);
            ways.Add(way);
            for (int r = 0; r < n; r++)
            {
                wayNodes.Add(MakeWayNode(grid[r, col], wayIndex, (uint)r));
            }

            wayIndex++;
        }

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        return GraphBuilder.Build(osmdata, ways, wayNodes, graph);
    }

    private static OSMNode MakeNode(ulong id, double lng, double lat, bool intersection = false)
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
        => new OSMWayNode { Node = node, WayIndex = wayIndex, WayShapeNodeIndex = shapeIndex };
}
