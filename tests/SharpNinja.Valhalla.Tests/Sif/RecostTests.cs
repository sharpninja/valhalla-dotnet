// Tests for the C# port of the sif recost_forward (valhalla @ 3.7.0).
//
// Valhalla exercises recost_forward through the routing suite (it is the function
// BidirectionalAStar::FormPath uses to recompute the final path's per-edge elapsed/transition costs
// and cumulative path distance). The faithful analogue here splits into:
//   * Unit tests for the pieces that do not need a tile graph: Recost.FindPercentAlong (the helper
//     that maps a correlated location + edge id to the percent-along, mirroring the file-local
//     find_percent_along in bidirectional_astar.cc) and the out-of-range percent guard on
//     Recost.Forward.
//   * A Monaco integration test (mirroring BidirectionalAStarTests) that snaps a real origin +
//     destination with loki::Search, reconstructs the primary path's ordered edge-id sequence from
//     GetBestPath, and drives Recost.Forward directly over that sequence. It asserts the emitted
//     labels' cumulative cost is non-decreasing (monotonic) and the cumulative path_distance matches
//     the running sum of DirectedEdge.Length across the path (first/last edge trimmed by
//     source/target percent). It fails loudly if the fixture is missing rather than silently skipping.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Sif;

public sealed class RecostTests
{
    // ===================== unit tests (no tile graph) =====================

    [Fact]
    public void FindPercentAlong_Returns_Matching_Edge_Percent()
    {
        var loc = new PathLocation(new Location(new PointLL(7.42, 43.73)));
        var id1 = new GraphId(100, 1, 5);
        var id2 = new GraphId(100, 1, 6);
        loc.Edges.Add(new PathLocation.PathEdge(id1, 0.25, new PointLL(7.42, 43.73), 0.0));
        loc.Edges.Add(new PathLocation.PathEdge(id2, 0.75, new PointLL(7.43, 43.74), 0.0));

        Assert.Equal(0.25f, Recost.FindPercentAlong(loc, id1));
        Assert.Equal(0.75f, Recost.FindPercentAlong(loc, id2));
    }

    [Fact]
    public void FindPercentAlong_Throws_When_Edge_Absent()
    {
        var loc = new PathLocation(new Location(new PointLL(7.42, 43.73)));
        loc.Edges.Add(new PathLocation.PathEdge(new GraphId(100, 1, 5), 0.25, new PointLL(7.42, 43.73), 0.0));

        Assert.Throws<InvalidOperationException>(() => Recost.FindPercentAlong(loc, new GraphId(100, 1, 9)));
    }

    [Theory]
    [InlineData(-0.1f, 1.0f)]
    [InlineData(1.1f, 1.0f)]
    [InlineData(0.0f, -0.1f)]
    [InlineData(0.0f, 1.1f)]
    public void Forward_Throws_When_Percent_Out_Of_Range(float sourcePct, float targetPct)
    {
        // The bounds check happens before any edge callback is invoked, so a never-dereferenced reader
        // and a default truck costing suffice.
        var reader = new GraphReader(new GraphReader.Config { TileDir = string.Empty });
        DynamicCost costing = new CostFactory().Create(Costing.Type.Truck);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Recost.Forward(reader, costing, () => GraphId.Invalid, _ => { }, sourcePct, targetPct));
    }

    [Fact]
    public void Forward_Returns_Immediately_When_First_Edge_Invalid()
    {
        // An edge callback that yields an invalid id first must make Forward return without emitting.
        var reader = new GraphReader(new GraphReader.Config { TileDir = string.Empty });
        DynamicCost costing = new CostFactory().Create(Costing.Type.Truck);

        var emitted = new List<PathEdgeLabel>();
        Recost.Forward(reader, costing, () => GraphId.Invalid, emitted.Add);

        Assert.Empty(emitted);
    }

    // ===================== Monaco integration (real tiles) =====================

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles")))
        {
            dir = dir.Parent;
        }

        return dir is null ? string.Empty : Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles");
    }

    private static GraphReader MakeReader(string root)
        => new GraphReader(new GraphReader.Config { TileDir = root });

    private static AutoCost MakeAutoCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Auto };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return new AutoCost(costing);
    }

    // Picks two distinct on-road points from the fixture (midpoints of two routable highest-level
    // edges). Mirrors BidirectionalAStarTests.PickTwoOnRoadPoints so the route lands in the same
    // connected component the engine can route across.
    private static (PointLL A, PointLL B) PickTwoOnRoadPoints(string root, AutoCost costing)
    {
        string[] gph = Directory.GetFiles(root, "*.gph", SearchOption.AllDirectories);
        byte topLevel = TileHierarchy.Levels()[^1].Level;
        var mids = new List<PointLL>();

        foreach (string file in gph)
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            string[] parts = rel.Split('/');
            if (!byte.TryParse(parts[0], out byte level) || level != topLevel)
            {
                continue;
            }

            string digits = string.Concat(parts.Skip(1).Select(p => p.Replace(".gph", string.Empty)));
            var baseId = new GraphId(uint.Parse(digits), level, 0);
            GraphTile? tile = GraphTile.Create(root, baseId);
            if (tile is null)
            {
                continue;
            }

            for (uint n = 0; n < tile.Header().Nodecount() && mids.Count < 2; n++)
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
                    if (shape.Count >= 2 && edge.Length > 30)
                    {
                        mids.Add(shape[0].PointAlongSegment(shape[^1], 0.5));
                        break;
                    }
                }
            }

            if (mids.Count >= 2)
            {
                break;
            }
        }

        if (mids.Count < 2)
        {
            throw new Xunit.Sdk.XunitException("Could not find two routable edges in the Monaco fixture.");
        }

        return (mids[0], mids[1]);
    }

    [Fact]
    public void Recost_Forward_Reproduces_Monotonic_Cost_And_Cumulative_Distance_On_Monaco_Path()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        GraphReader reader = MakeReader(root);
        AutoCost costing = MakeAutoCosting();
        (PointLL a, PointLL b) = PickTwoOnRoadPoints(root, costing);

        var origin = new PathLocation(new Location(a) { Radius = 50 });
        var dest = new PathLocation(new Location(b) { Radius = 50 });
        new Search(reader).DoSearch(new[] { origin, dest }, costing);

        Assert.NotEmpty(origin.Edges);
        Assert.NotEmpty(dest.Edges);

        var modeCosting = new ModeCosting();
        modeCosting[(int)costing.TravelMode()] = costing;

        var algo = new BidirectionalAStar();
        List<List<PathInfo>> paths = algo.GetBestPath(origin, dest, reader, modeCosting, costing.TravelMode());
        Assert.NotEmpty(paths);
        List<PathInfo> path = paths[0];
        Assert.NotEmpty(path);

        // Reconstruct the ordered edge-id sequence from the primary path.
        var edgeIds = path.Select(pi => pi.Edgeid).ToList();

        // Recompute source/target percent from the correlated endpoints (same as FormPath does). The
        // primary path's first/last edges are, by construction, the origin/destination candidate edges.
        float sourcePct = Recost.FindPercentAlong(origin, edgeIds[0]);
        float targetPct = Recost.FindPercentAlong(dest, edgeIds[^1]);

        // Drive Recost.Forward over the reconstructed edge list, ignoring access (as FormPath does).
        var labels = new List<PathEdgeLabel>();
        int itr = 0;
        GraphId EdgeCb() => itr >= edgeIds.Count ? GraphId.Invalid : edgeIds[itr++];
        Recost.Forward(reader, costing, EdgeCb, labels.Add, sourcePct, targetPct, ignoreAccess: true);

        // Exactly one emitted label per edge.
        Assert.Equal(edgeIds.Count, labels.Count);

        // Cumulative elapsed cost is non-decreasing (transition + partial-edge costs are non-negative).
        float prevCost = -1.0f;
        foreach (PathEdgeLabel lab in labels)
        {
            Assert.True(lab.Cost().CostValue >= 0.0f);
            Assert.True(lab.Cost().CostValue + 1e-3f >= prevCost,
                $"cumulative cost decreased: {lab.Cost().CostValue} < {prevCost}");
            prevCost = lab.Cost().CostValue;
        }

        // Cumulative path_distance equals the running sum of DirectedEdge.Length with first/last trimmed.
        double expected = 0.0;
        GraphTile? tile = null;
        for (int i = 0; i < edgeIds.Count; i++)
        {
            DirectedEdge? de = reader.Directededge(edgeIds[i], ref tile);
            Assert.True(de.HasValue, $"edge {edgeIds[i]} not found");

            float pct = 1.0f;
            if (i == 0)
            {
                pct -= sourcePct;
            }

            if (i == edgeIds.Count - 1)
            {
                pct -= 1.0f - targetPct;
                pct = Math.Max(0.0f, pct);
            }

            expected += de.Value.Length * pct;

            // The label stores (uint)cumulative-length; allow truncation + float tolerance.
            Assert.True(Math.Abs(labels[i].PathDistance() - expected) <= 2.0,
                $"path_distance mismatch at edge {i}: label={labels[i].PathDistance()} expected~{expected:F2}");
        }

        // The recost distance must agree with the engine's PathInfo path distance for the same edges
        // (FormPath produced the primary path via the very same Recost.Forward), proving recost did not
        // regress the primary path.
        Assert.True(Math.Abs(path[^1].PathDistance - labels[^1].PathDistance()) <= 1.0,
            $"engine path distance {path[^1].PathDistance} vs recost {labels[^1].PathDistance()}");
    }
}
