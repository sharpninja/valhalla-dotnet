// Tests for the faithful C# port of the Valhalla mjolnir ShortcutBuilder (building shortcut edges
// through contractible degree-2 nodes on a hierarchy level).
//
// Valhalla exercises shortcuts through the gurka pipeline (test/gurka/test_shortcut.cc). These tests
// build a local-level tile set via the ported GraphBuilder.Build, promote it with HierarchyBuilder,
// then run ShortcutBuilder and assert the shortcut invariants:
//   - the build completes and returns stats, and every produced tile round-trips through the Baldr
//     GraphTile reader (byte compatibility),
//   - when a shortcut edge is created it is flagged as a shortcut and supersedes base edges, and the
//     forward-star invariant holds,
//   - a graph with no contractible nodes (a single edge) produces no shortcuts.

using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class ShortcutBuilderTests
{
    [Fact]
    public void Build_CompletesAndTilesRoundTrip()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            WritePromotedTiles(tileDir);

            ShortcutBuilder.ShortcutStats stats = ShortcutBuilder.Build(new GraphReader.Config { TileDir = tileDir });

            Assert.NotNull(stats);

            // Every tile produced by the shortcut build must round-trip through the reader, and the
            // forward-star invariant must hold. If shortcuts were created, the superseding shortcut
            // edges are flagged.
            var reader = new GraphReader(new GraphReader.Config { TileDir = tileDir });
            uint shortcutEdges = 0;
            uint supersededEdges = 0;
            foreach (GraphId tileId in reader.GetTileSet())
            {
                GraphTile? tile = reader.GetGraphTile(tileId);
                Assert.NotNull(tile);

                for (int n = 0; n < tile!.NodeCount(); n++)
                {
                    NodeInfo ni = tile.Node(n);
                    Assert.True(ni.EdgeIndex + ni.EdgeCount <= tile.DirectedEdgeCount());
                }

                for (int e = 0; e < tile.DirectedEdgeCount(); e++)
                {
                    DirectedEdge de = tile.DirectedEdge(e);

                    // Edge info must still be parseable on every edge.
                    EdgeInfo info = tile.EdgeInfo(de);
                    Assert.True(info.Shape().Count >= 2);

                    if (de.IsShortcut)
                    {
                        shortcutEdges++;
                        // Shortcuts use way id 0 and carry no exit signs.
                        Assert.False(de.Sign, "Shortcut edges must not carry exit signs.");
                    }

                    if (de.Superseded != 0)
                    {
                        supersededEdges++;
                    }
                }
            }

            // The reported shortcut count is consistent with the flagged shortcut edges in the tiles.
            Assert.Equal(stats.ShortcutCount, shortcutEdges);

            // If any shortcut formed, at least one base edge must be superseded.
            if (stats.ShortcutCount > 0)
            {
                Assert.True(supersededEdges > 0, "A shortcut should supersede at least one base edge.");
            }
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    [Fact]
    public void Build_SingleEdgeGraph_ProducesNoShortcuts()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            // A single A-B edge: no degree-2 contractible interior node, so no shortcut can form.
            WriteSingleEdgeTiles(tileDir);
            HierarchyBuilder.Build(new GraphReader.Config { TileDir = tileDir });

            ShortcutBuilder.ShortcutStats stats = ShortcutBuilder.Build(new GraphReader.Config { TileDir = tileDir });

            Assert.Equal(0u, stats.ShortcutCount);
            Assert.Equal(0u, stats.EdgeCount);
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    // Build a Primary-class chain, write local tiles, then promote to the highway/arterial levels so
    // the ShortcutBuilder has a higher-level graph to contract over.
    private static void WritePromotedTiles(string tileDir)
    {
        BuildChainInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);
        var osmdata = new OSMData();
        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);
        WriteTiles(tileDir, tiles);

        HierarchyBuilder.Build(new GraphReader.Config { TileDir = tileDir });
    }

    private static void WriteSingleEdgeTiles(string tileDir)
    {
        var ways = new List<OSMWay>();
        var wayNodes = new List<OSMWayNode>();

        OSMNode a = MakeNode(1, -76.890, 40.270);
        OSMNode b = MakeNode(2, -76.870, 40.275);

        OSMWay way0 = MakeWay(100, RoadClass.Primary);
        way0.SetNodeCount(2);
        ways.Add(way0);
        wayNodes.Add(MakeWayNode(a, 0, 0));
        wayNodes.Add(MakeWayNode(b, 0, 1));

        var osmdata = new OSMData();
        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(osmdata, ways, wayNodes, graph);
        WriteTiles(tileDir, tiles);
    }

    private static void BuildChainInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes)
    {
        ways = new List<OSMWay>();
        wayNodes = new List<OSMWayNode>();

        OSMNode a = MakeNode(1, -76.900, 40.270);
        OSMNode m = MakeNode(2, -76.890, 40.272);
        OSMNode b = MakeNode(3, -76.880, 40.274);
        OSMNode n = MakeNode(4, -76.870, 40.272);
        OSMNode c = MakeNode(5, -76.860, 40.270);

        // way0: A - M - B - N - C of Primary class (promotes to highway level; M, B, N are interior
        // degree-2 nodes eligible for contraction into a shortcut).
        OSMWay way0 = MakeWay(100, RoadClass.Primary);
        way0.SetNodeCount(5);
        ways.Add(way0);
        wayNodes.Add(MakeWayNode(a, 0, 0));
        wayNodes.Add(MakeWayNode(m, 0, 1));
        wayNodes.Add(MakeWayNode(b, 0, 2));
        wayNodes.Add(MakeWayNode(n, 0, 3));
        wayNodes.Add(MakeWayNode(c, 0, 4));
    }

    private static void WriteTiles(string tileDir, Dictionary<GraphId, byte[]> tiles)
    {
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

    private static OSMNode MakeNode(ulong id, double lng, double lat)
    {
        var node = new OSMNode(id, lat, lng);
        node.SetIntersection(true);
        return node;
    }

    private static OSMWay MakeWay(ulong id, RoadClass roadClass)
    {
        var way = new OSMWay(id);
        way.SetRoadClass(roadClass);
        way.SetUse(Use.Road);
        way.SetSpeed(80);
        way.SetAutoForward(true);
        way.SetAutoBackward(true);
        way.SetTruckForward(true);
        way.SetTruckBackward(true);
        way.SetDriveOnRight(true);
        return way;
    }

    private static OSMWayNode MakeWayNode(OSMNode node, uint wayIndex, uint shapeIndex)
        => new OSMWayNode { Node = node, WayIndex = wayIndex, WayShapeNodeIndex = shapeIndex };

    private static string MakeTempTileDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tm_shortcut_" + System.Guid.NewGuid().ToString("N"));
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
