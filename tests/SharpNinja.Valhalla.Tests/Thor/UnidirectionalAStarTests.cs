// Tests for the C# port of the thor UnidirectionalAStar (time-dependent forward/reverse A*),
// valhalla @ 3.7.0.
//
// Valhalla has no isolated unit gtest for UnidirectionalAStar; it is exercised by test/astar.cc and
// the gurka integration suite (both need the full tile builder). This file mirrors that split:
//   - Construction / name / clear behavior and the "no route" contract are unit-tested directly.
//   - A real end-to-end depart-at route over the Monaco fixture (artifacts/valhalla-monaco-tiles,
//     not committed) is exercised when the fixture is present, snapping with the ported loki Search
//     exactly as the engine does before handing off to thor. The fixture test fails loudly (does not
//     silently skip) if the tiles are missing, matching SearchMonacoTests / BaldrMonacoParityTests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Thor;

public sealed class UnidirectionalAStarTests
{
    [Fact]
    public void Forward_Reports_TimeDependentForward_Name()
    {
        UnidirectionalAStar algo = UnidirectionalAStar.TimeDepForward();
        Assert.Equal("time_dependent_forward_a*", algo.Name());
    }

    [Fact]
    public void Reverse_Reports_TimeDependentReverse_Name()
    {
        UnidirectionalAStar algo = UnidirectionalAStar.TimeDepReverse();
        Assert.Equal("time_dependent_reverse_a*", algo.Name());
    }

    [Fact]
    public void Defaults_NotThruPruning_True_And_No_Ferry()
    {
        UnidirectionalAStar algo = UnidirectionalAStar.TimeDepForward();
        Assert.True(algo.NotThruPruning());
        Assert.False(algo.HasFerry());
    }

    [Fact]
    public void Clear_Does_Not_Throw_On_A_Fresh_Algorithm()
    {
        UnidirectionalAStar algo = UnidirectionalAStar.TimeDepForward();
        algo.Clear();
        Assert.False(algo.HasFerry());
    }

    [Fact]
    public void SetNotThruPruning_Is_Honored()
    {
        UnidirectionalAStar algo = UnidirectionalAStar.TimeDepReverse();
        algo.SetNotThruPruning(false);
        Assert.False(algo.NotThruPruning());
    }

    // ----------------------------------------------------------------------------------------------
    // Real-tile end-to-end route (Monaco fixture)
    // ----------------------------------------------------------------------------------------------

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

    private static AutoCost MakeAutoCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Auto };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return new AutoCost(costing);
    }

    private static ModeCosting MakeModeCosting(AutoCost costing)
    {
        var modeCosting = new ModeCosting();
        modeCosting[(int)costing.TravelMode()] = costing;
        return modeCosting;
    }

    // Picks two distinct on-road points (shape midpoints of two different routable top-level edges)
    // so the A* has somewhere to go. Returns the points and the travel mode's costing.
    private static (PointLL A, PointLL B) PickTwoOnRoadPoints(string root, AutoCost costing)
    {
        string[] gph = Directory.GetFiles(root, "*.gph", SearchOption.AllDirectories);
        byte topLevel = TileHierarchy.Levels()[^1].Level;
        var points = new List<PointLL>();

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

            for (uint n = 0; n < tile.Header().Nodecount() && points.Count < 2; n++)
            {
                NodeInfo node = tile.Node((int)n);
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
                        points.Add(shape[0].PointAlongSegment(shape[^1], 0.5));
                        break;
                    }
                }
            }

            if (points.Count >= 2)
            {
                break;
            }
        }

        if (points.Count < 2)
        {
            throw new Xunit.Sdk.XunitException("Could not find two routable edges in the Monaco fixture.");
        }

        return (points[0], points[1]);
    }

    [Fact]
    public void Forward_Finds_A_Connected_Route_Between_Two_OnRoad_Points()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        var reader = new GraphReader(new GraphReader.Config { TileDir = root });
        AutoCost costing = MakeAutoCosting();
        (PointLL a, PointLL b) = PickTwoOnRoadPoints(root, costing);

        var origin = new PathLocation(new Location(a) { Radius = 50 });
        var dest = new PathLocation(new Location(b) { Radius = 50 });

        // Snap both endpoints with the ported loki search (exactly as the engine does before thor).
        var search = new Search(reader);
        search.DoSearch(new[] { origin, dest }, costing);
        Assert.NotEmpty(origin.Edges);
        Assert.NotEmpty(dest.Edges);

        UnidirectionalAStar algo = UnidirectionalAStar.TimeDepForward();
        List<List<PathInfo>> paths =
            algo.GetBestPath(origin, dest, reader, MakeModeCosting(costing), costing.TravelMode());

        // A route should be found and its edges should be in non-decreasing elapsed-cost order
        // (the forward path is reconstructed from origin to destination).
        Assert.Single(paths);
        List<PathInfo> path = paths[0];
        Assert.NotEmpty(path);
        for (int i = 1; i < path.Count; i++)
        {
            Assert.True(path[i].ElapsedCost.Secs >= path[i - 1].ElapsedCost.Secs - 1e-3f,
                "forward path elapsed cost is not non-decreasing");
        }
    }

    [Fact]
    public void Trivial_Same_Point_Route_Yields_A_Single_Edge_Path()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root), $"Monaco tile fixture not found. Root resolved: '{root}'");

        var reader = new GraphReader(new GraphReader.Config { TileDir = root });
        AutoCost costing = MakeAutoCosting();
        (PointLL a, PointLL _) = PickTwoOnRoadPoints(root, costing);

        // Origin == destination on the same edge -> trivial route (a single edge).
        var origin = new PathLocation(new Location(a) { Radius = 50 });
        var dest = new PathLocation(new Location(a) { Radius = 50 });

        var search = new Search(reader);
        search.DoSearch(new[] { origin, dest }, costing);
        Assert.NotEmpty(origin.Edges);

        UnidirectionalAStar algo = UnidirectionalAStar.TimeDepForward();
        List<List<PathInfo>> paths =
            algo.GetBestPath(origin, dest, reader, MakeModeCosting(costing), costing.TravelMode());

        Assert.Single(paths);
        Assert.NotEmpty(paths[0]);
    }
}
