// Integration test for the C# loki edge-candidate search (valhalla @ 3.7.0) against REAL Monaco
// tiles built by valhalla_build_tiles @ 3.7.0 (artifacts/valhalla-monaco-tiles, not committed).
//
// Valhalla has no standalone gtest for loki::Search (it is covered by the gurka integration suite
// which needs the full tile builder). This is the faithful analogue: it correlates real lat,lngs to
// the real graph and asserts the snapping behaves like the engine. The test fails loudly if the
// fixture is missing rather than silently skipping (matching BaldrMonacoParityTests).
//
// Reachability note: the search runs with the default AllReachableProvider (reachability disabled,
// i.e. the engine's max_reach_limit==0 configuration used for plain point-to-point routing), since
// loki::Reach depends on thor::Dijkstras which is excluded from this slice.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Loki;

public sealed class SearchMonacoTests
{
    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles")))
        {
            dir = dir.Parent;
        }

        return dir is null ? string.Empty : Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles");
    }

    private static (uint Level, uint TileId) ParseTilePath(string root, string gphPath)
    {
        string rel = Path.GetRelativePath(root, gphPath).Replace('\\', '/');
        string[] parts = rel.Split('/');
        uint level = uint.Parse(parts[0]);
        string digits = string.Concat(parts.Skip(1).Select(p => p.Replace(".gph", string.Empty)));
        return (level, uint.Parse(digits));
    }

    private static GraphReader MakeReader(string root)
        => new GraphReader(new GraphReader.Config { TileDir = root });

    private static AutoCost MakeAutoCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Auto };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return new AutoCost(costing);
    }

    // Picks a routable highest-level edge from the fixture and returns the midpoint of its shape (a
    // lat,lng guaranteed to be on a road) plus the begin-node lat,lng (for the node-snap test).
    private static (PointLL Mid, PointLL NodeLl) PickOnRoadPoint(string root, AutoCost costing)
    {
        string[] gph = Directory.GetFiles(root, "*.gph", SearchOption.AllDirectories);
        byte topLevel = TileHierarchy.Levels()[^1].Level;

        foreach (string file in gph)
        {
            (uint level, uint tileId) = ParseTilePath(root, file);
            if (level != topLevel)
            {
                continue;
            }

            var baseId = new GraphId(tileId, level, 0);
            GraphTile? tile = GraphTile.Create(root, baseId);
            if (tile is null)
            {
                continue;
            }

            for (uint n = 0; n < tile.Header().Nodecount(); n++)
            {
                NodeInfo node = tile.Node((int)n);
                PointLL nodeLl = node.LatLng(tile.BaseLl());
                for (uint e = 0; e < node.EdgeCount; e++)
                {
                    DirectedEdge edge = tile.DirectedEdge((int)(node.EdgeIndex + e));
                    if (!costing.Allowed(edge, tile, DynamicCost.DisallowShortcut))
                    {
                        continue;
                    }

                    IReadOnlyList<PointLL> shape = tile.EdgeInfo(edge).Shape();
                    if (shape.Count >= 2 && edge.Length > 20)
                    {
                        PointLL mid = shape[0].PointAlongSegment(shape[^1], 0.5);
                        return (mid, nodeLl);
                    }
                }
            }
        }

        throw new Xunit.Sdk.XunitException("No routable edge found in the Monaco fixture.");
    }

    [Fact]
    public void Snaps_An_On_Road_Point_To_At_Least_One_Edge()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        GraphReader reader = MakeReader(root);
        AutoCost costing = MakeAutoCosting();
        (PointLL mid, PointLL _) = PickOnRoadPoint(root, costing);

        var location = new PathLocation(new Location(mid) { Radius = 50 });
        var search = new Search(reader);
        search.DoSearch(new[] { location }, costing);

        Assert.NotEmpty(location.Edges);

        // The closest correlated edge should be very close to the on-road point (it is on a road).
        double bestDistance = location.Edges.Min(e => e.Distance);
        Assert.True(bestDistance < 50.0, $"closest snap distance {bestDistance} m exceeded 50 m");

        // percent_along is a fraction in [0,1] and the projected point is finite.
        foreach (PathLocation.PathEdge pe in location.Edges)
        {
            Assert.InRange(pe.PercentAlong, 0.0, 1.0);
            Assert.False(double.IsNaN(pe.Projected.Lat));
            Assert.False(double.IsNaN(pe.Projected.Lng));
        }
    }

    [Fact]
    public void Snaps_A_Node_Point_And_Produces_Begin_Or_End_Node_Correlations()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found. Root resolved: '{root}'");

        GraphReader reader = MakeReader(root);
        AutoCost costing = MakeAutoCosting();
        (PointLL _, PointLL nodeLl) = PickOnRoadPoint(root, costing);

        // Snap exactly at a node with a small node-snap tolerance -> should node-snap (percent_along
        // 0 or 1 on the correlated edges).
        var location = new PathLocation(new Location(nodeLl) { Radius = 50, NodeSnapTolerance = 5 });
        var search = new Search(reader);
        search.DoSearch(new[] { location }, costing);

        Assert.NotEmpty(location.Edges);
        Assert.Contains(location.Edges, e => e.BeginNode() || e.EndNode());
    }

    [Fact]
    public void Empty_Location_List_Is_A_Noop()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root), $"Monaco tile fixture not found. Root resolved: '{root}'");

        GraphReader reader = MakeReader(root);
        AutoCost costing = MakeAutoCosting();
        var search = new Search(reader);

        // Should not throw and should simply do nothing.
        search.DoSearch(Array.Empty<PathLocation>(), costing);
    }

    [Fact]
    public void Null_Costing_Throws()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root), $"Monaco tile fixture not found. Root resolved: '{root}'");

        GraphReader reader = MakeReader(root);
        var search = new Search(reader);
        var loc = new PathLocation(new Location(new PointLL(7.42, 43.73)));

        Assert.Throws<InvalidOperationException>(() => search.DoSearch(new[] { loc }, null!));
    }
}
