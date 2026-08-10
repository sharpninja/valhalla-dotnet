// Tests for the faithful C# port of the Valhalla mjolnir HierarchyBuilder (dividing the local/base
// graph into hierarchy levels: highway / arterial / local).
//
// Valhalla has no standalone gtest for hierarchybuilder.cc - it is exercised through the full build
// pipeline + gurka route tests. These tests build a small local-level (level 2) tile set on disk via
// the ported GraphBuilder.Build, run HierarchyBuilder.Build, and assert the hierarchy invariants:
//   - higher-classification edges are promoted to a higher level tile (a level-0 highway tile is
//     produced for Primary-class ways), with node transitions linking the levels,
//   - the produced tiles round-trip through the Baldr GraphTile reader (byte compatibility),
//   - the build is idempotent w.r.t. reader state (no exceptions on a fresh reader each run).

using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class HierarchyBuilderTests
{
    [Fact]
    public void Build_PromotesPrimaryEdges_ToHighwayLevel_AndTilesRoundTrip()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            WriteLocalTiles(tileDir, RoadClass.Primary);

            // Sanity: before the hierarchy build, only local (level 2) tiles exist.
            var preReader = new GraphReader(new GraphReader.Config { TileDir = tileDir });
            HashSet<GraphId> preTiles = preReader.GetTileSet();
            Assert.NotEmpty(preTiles);
            foreach (GraphId t in preTiles)
            {
                Assert.Equal(TileHierarchy.Levels()[^1].Level, (byte)t.Level());
            }

            HierarchyBuilder.Build(new GraphReader.Config { TileDir = tileDir });

            // After the build, a highway-level (level 0) tile must exist for the Primary-class edges.
            var reader = new GraphReader(new GraphReader.Config { TileDir = tileDir });
            HashSet<GraphId> tiles = reader.GetTileSet();
            Assert.NotEmpty(tiles);

            bool foundHighwayLevel = false;
            foreach (GraphId tileId in tiles)
            {
                GraphTile? tile = reader.GetGraphTile(tileId);
                Assert.NotNull(tile);

                if (tileId.Level() == 0)
                {
                    foundHighwayLevel = true;
                }

                // Forward-star invariant + edge info round-trips on every produced tile.
                for (int n = 0; n < tile!.NodeCount(); n++)
                {
                    NodeInfo ni = tile.Node(n);
                    Assert.True(ni.EdgeIndex + ni.EdgeCount <= tile.DirectedEdgeCount());
                }

                for (int e = 0; e < tile.DirectedEdgeCount(); e++)
                {
                    DirectedEdge de = tile.DirectedEdge(e);
                    EdgeInfo info = tile.EdgeInfo(de);
                    Assert.True(info.Shape().Count >= 2);
                }
            }

            Assert.True(foundHighwayLevel, "Primary-class edges should be promoted to a highway (level 0) tile.");
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    [Fact]
    public void Build_HighwayNodes_HaveDownwardTransitionsToLocal()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            WriteLocalTiles(tileDir, RoadClass.Primary);
            HierarchyBuilder.Build(new GraphReader.Config { TileDir = tileDir });

            var reader = new GraphReader(new GraphReader.Config { TileDir = tileDir });

            // A highway-level node that also exists on the local level must carry a node transition
            // (the hierarchy linking between levels).
            uint totalTransitions = 0;
            foreach (GraphId tileId in reader.GetTileSet())
            {
                GraphTile? tile = reader.GetGraphTile(tileId);
                Assert.NotNull(tile);
                totalTransitions += tile!.Header().Transitioncount();
            }

            Assert.True(totalTransitions > 0, "Expected node transitions linking hierarchy levels.");
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    [Fact]
    public void Build_ResidentialEdges_StayOnLocalLevel()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            // Residential is below the arterial cutoff (Tertiary), so it stays on the local level.
            WriteLocalTiles(tileDir, RoadClass.Residential);
            HierarchyBuilder.Build(new GraphReader.Config { TileDir = tileDir });

            var reader = new GraphReader(new GraphReader.Config { TileDir = tileDir });
            bool anyHigherLevel = false;
            bool anyLocal = false;
            foreach (GraphId tileId in reader.GetTileSet())
            {
                if (tileId.Level() < 2)
                {
                    anyHigherLevel = true;
                }

                if (tileId.Level() == 2)
                {
                    anyLocal = true;
                }
            }

            Assert.False(anyHigherLevel, "Residential edges should not be promoted above the local level.");
            Assert.True(anyLocal, "Local tiles should remain for residential-only graphs.");
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    [Fact]
    public void Build_ReturnsMeasuredSubstageReceipts()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            WriteLocalTiles(tileDir, RoadClass.Primary);

            HierarchyBuildResult result =
                HierarchyBuilder.Build(new GraphReader.Config { TileDir = tileDir });

            Assert.True(result.BaseNodeAssociationCount > 0);
            Assert.True(result.NewNodeAssociationCount >= result.BaseNodeAssociationCount);
            Assert.Equal(
                new[] { "associations", "sort", "form-tiles", "cleanup" },
                result.StageDurations.Keys);
            Assert.All(result.StageDurations.Values, duration => Assert.True(duration >= TimeSpan.Zero));
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    [Fact]
    public void Build_ParallelOutputMatchesSequentialOutput()
    {
        string sequentialDir = MakeTempTileDir();
        string parallelDir = MakeTempTileDir();
        try
        {
            WriteLocalTiles(sequentialDir, RoadClass.Primary);
            WriteLocalTiles(parallelDir, RoadClass.Primary);

            HierarchyBuilder.Build(new GraphReader.Config { TileDir = sequentialDir });
            HierarchyBuilder.Build(
                new GraphReader.Config { TileDir = parallelDir },
                maxDegreeOfParallelism: 4,
                TestContext.Current.CancellationToken);

            string[] sequentialFiles = Directory.GetFiles(sequentialDir, "*.gph", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(sequentialDir, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            string[] parallelFiles = Directory.GetFiles(parallelDir, "*.gph", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(parallelDir, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(sequentialFiles, parallelFiles);
            foreach (string relativePath in sequentialFiles)
            {
                Assert.Equal(
                    File.ReadAllBytes(Path.Combine(sequentialDir, relativePath)),
                    File.ReadAllBytes(Path.Combine(parallelDir, relativePath)));
            }
        }
        finally
        {
            Cleanup(sequentialDir);
            Cleanup(parallelDir);
        }
    }

    [Fact]
    public void Build_RejectsInvalidParallelism()
    {
        string tileDir = MakeTempTileDir();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => HierarchyBuilder.Build(
                    new GraphReader.Config { TileDir = tileDir },
                    maxDegreeOfParallelism: 0,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(tileDir);
        }
    }

    // ------------------------------------------------------------------
    // Fixture: build a local (level 2) tile set on disk
    // ------------------------------------------------------------------

    // Builds a chain A - M - B - N - C of the given road class (so all edges share a class and can be
    // promoted together) plus enough geometry to land in a single Harrisburg-area tiling, and writes
    // the level-2 tiles to disk.
    private static void WriteLocalTiles(string tileDir, RoadClass roadClass)
    {
        BuildInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes, roadClass);
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

    private static void BuildInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes, RoadClass roadClass)
    {
        ways = new List<OSMWay>();
        wayNodes = new List<OSMWayNode>();

        OSMNode a = MakeNode(1, -76.890, 40.270);
        OSMNode m = MakeNode(2, -76.880, 40.273);
        OSMNode b = MakeNode(3, -76.870, 40.275);
        OSMNode n = MakeNode(4, -76.860, 40.272);
        OSMNode c = MakeNode(5, -76.850, 40.270);

        // way0: A - M - B - N - C (5 nodes); internal nodes M, B, N are intersections (way shape
        // nodes), endpoints A and C are way ends. A through road of the given class.
        OSMWay way0 = MakeWay(100, roadClass);
        way0.SetNodeCount(5);
        ways.Add(way0);
        wayNodes.Add(MakeWayNode(a, 0, 0));
        wayNodes.Add(MakeWayNode(m, 0, 1));
        wayNodes.Add(MakeWayNode(b, 0, 2));
        wayNodes.Add(MakeWayNode(n, 0, 3));
        wayNodes.Add(MakeWayNode(c, 0, 4));

        // A short cross way at M so M is a real intersection (degree > 2) and the build keeps nodes.
        OSMNode d = MakeNode(6, -76.882, 40.283);
        OSMWay way1 = MakeWay(200, roadClass);
        way1.SetNodeCount(2);
        ways.Add(way1);
        wayNodes.Add(MakeWayNode(m, 1, 0));
        wayNodes.Add(MakeWayNode(d, 1, 1));
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
        string dir = Path.Combine(Path.GetTempPath(), "tm_hierarchy_" + System.Guid.NewGuid().ToString("N"));
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
