// Tests for the faithful C# port of the Valhalla mjolnir GraphFilter (access-based edge/node
// filtering + edge aggregation).
//
// Valhalla has no dedicated gtest for graphfilter.cc - it is exercised through the full build
// pipeline against PBF fixtures. These tests build a tiny tile set on disk (via the ported
// GraphBuilder.Build + GraphTile.FileSuffix layout), run GraphFilter.Filter, and assert the
// filtering invariants:
//   - filtering with all modes enabled is a no-op (early return),
//   - filtering to driving-only drops pedestrian-only edges and re-stores byte-compatible tiles,
//   - the filtered tiles still round-trip through the Baldr GraphTile reader.

using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class GraphFilterTests
{
    [Fact]
    public void Filter_AllModesEnabled_IsNoOp()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            WriteTiles(tileDir, includePedestrianSpur: true);
            int before = CountTileFiles(tileDir);

            GraphFilter.FilterStats stats = GraphFilter.Filter(new GraphFilter.FilterConfig
            {
                TileDir = tileDir,
                IncludeDriving = true,
                IncludeBicycle = true,
                IncludePedestrian = true,
            });

            // Nothing to filter: early return, no stats accumulated, files untouched.
            Assert.Equal(0u, stats.OriginalEdges);
            Assert.Equal(0u, stats.FilteredEdges);
            Assert.Equal(before, CountTileFiles(tileDir));
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    [Fact]
    public void Filter_DrivingOnly_DropsPedestrianEdges_AndTilesRoundTrip()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            WriteTiles(tileDir, includePedestrianSpur: true);

            uint pedestrianEdgesBefore = CountEdges(tileDir, autoOnly: false) - CountEdges(tileDir, autoOnly: true);
            Assert.True(pedestrianEdgesBefore > 0, "Fixture should contain at least one pedestrian-only edge.");

            GraphFilter.FilterStats stats = GraphFilter.Filter(new GraphFilter.FilterConfig
            {
                TileDir = tileDir,
                IncludeDriving = true,
                IncludeBicycle = false,
                IncludePedestrian = false,
            });

            // Some edges were filtered and some originals were counted.
            Assert.True(stats.OriginalEdges > 0);
            Assert.True(stats.FilteredEdges > 0);

            // After filtering, every remaining directed edge has vehicular access (the pedestrian-only
            // spur was dropped). The tiles must still round-trip through the reader.
            var reader = new GraphReader(new GraphReader.Config { TileDir = tileDir });
            HashSet<GraphId> tiles = reader.GetTileSet(TileHierarchy.Levels()[^1].Level);
            Assert.NotEmpty(tiles);

            int remaining = 0;
            foreach (GraphId tileId in tiles)
            {
                GraphTile? tile = reader.GetGraphTile(tileId);
                Assert.NotNull(tile);
                for (int e = 0; e < tile!.DirectedEdgeCount(); e++)
                {
                    DirectedEdge de = tile.DirectedEdge(e);
                    bool vehicular =
                        (de.ForwardAccess & GraphConstants.VehicularAccess) != 0 ||
                        (de.ReverseAccess & GraphConstants.VehicularAccess) != 0;
                    Assert.True(vehicular, "Every remaining edge should have vehicular access.");

                    // Edge info must still be parseable.
                    EdgeInfo info = tile.EdgeInfo(de);
                    Assert.True(info.Shape().Count >= 2);
                    remaining++;
                }

                // Forward-star invariant: node edge index/count stays in bounds.
                for (int n = 0; n < tile.NodeCount(); n++)
                {
                    NodeInfo ni = tile.Node(n);
                    Assert.True(ni.EdgeIndex + ni.EdgeCount <= tile.DirectedEdgeCount());
                }
            }

            Assert.True(remaining > 0, "Driving edges should remain after filtering.");
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    // ------------------------------------------------------------------
    // Fixture: build tiles on disk
    // ------------------------------------------------------------------

    // Builds a tiny graph and writes the tiles to disk using the standard <level>/<id-path>.gph layout
    // so the GraphReader can read them. way0 = A-M-B (auto through-road). When includePedestrianSpur is
    // true, way1 = M-P is a pedestrian-only footway (no auto access) that GraphFilter should drop when
    // driving-only.
    private static void WriteTiles(string tileDir, bool includePedestrianSpur)
    {
        BuildInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes, includePedestrianSpur);
        var osmdata = new OSMData();

        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);

        foreach (KeyValuePair<GraphId, byte[]> kv in tiles)
        {
            string path = Path.Combine(tileDir, GraphTile.FileSuffix(kv.Key));
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(path, kv.Value);
        }
    }

    private static int CountTileFiles(string tileDir)
        => Directory.Exists(tileDir) ? Directory.GetFiles(tileDir, "*.gph", SearchOption.AllDirectories).Length : 0;

    // Counts directed edges across all tiles, optionally only those with auto access.
    private static uint CountEdges(string tileDir, bool autoOnly)
    {
        var reader = new GraphReader(new GraphReader.Config { TileDir = tileDir });
        uint count = 0;
        foreach (GraphId tileId in reader.GetTileSet(TileHierarchy.Levels()[^1].Level))
        {
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            for (int e = 0; e < tile.DirectedEdgeCount(); e++)
            {
                DirectedEdge de = tile.DirectedEdge(e);
                bool hasAuto =
                    (de.ForwardAccess & GraphConstants.AutoAccess) != 0 ||
                    (de.ReverseAccess & GraphConstants.AutoAccess) != 0;
                if (!autoOnly || hasAuto)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void BuildInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes, bool includePedestrianSpur)
    {
        ways = new List<OSMWay>();
        wayNodes = new List<OSMWayNode>();

        OSMNode a = MakeNode(1, -76.880, 40.270);
        OSMNode m = MakeNode(2, -76.870, 40.275);
        OSMNode b = MakeNode(3, -76.860, 40.272);
        OSMNode p = MakeNode(4, -76.872, 40.285);

        // way0: A - M - B auto through road.
        OSMWay way0 = MakeAutoWay(100);
        way0.SetNodeCount(3);
        ways.Add(way0);
        wayNodes.Add(MakeWayNode(a, 0, 0));
        wayNodes.Add(MakeWayNode(m, 0, 1));
        wayNodes.Add(MakeWayNode(b, 0, 2));

        if (includePedestrianSpur)
        {
            // way1: M - P pedestrian-only footway (no auto access).
            OSMWay way1 = MakePedestrianWay(200);
            way1.SetNodeCount(2);
            ways.Add(way1);
            wayNodes.Add(MakeWayNode(m, 1, 0));
            wayNodes.Add(MakeWayNode(p, 1, 1));
        }
    }

    private static OSMNode MakeNode(ulong id, double lng, double lat)
    {
        var node = new OSMNode(id, lat, lng);
        node.SetIntersection(true);
        return node;
    }

    private static OSMWay MakeAutoWay(ulong id)
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

    private static OSMWay MakePedestrianWay(ulong id)
    {
        var way = new OSMWay(id);
        way.SetRoadClass(RoadClass.ServiceOther);
        way.SetUse(Use.Footway);
        way.SetSpeed(5);
        way.SetPedestrianForward(true);
        way.SetPedestrianBackward(true);
        way.SetDriveOnRight(true);
        return way;
    }

    private static OSMWayNode MakeWayNode(OSMNode node, uint wayIndex, uint shapeIndex)
        => new OSMWayNode { Node = node, WayIndex = wayIndex, WayShapeNodeIndex = shapeIndex };

    private static string MakeTempTileDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tm_graphfilter_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
