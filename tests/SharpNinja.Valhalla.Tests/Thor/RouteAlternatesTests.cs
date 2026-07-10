// Integration coverage for the alternate-routes reshape (B3): RouteEngine.RouteAlternates must emit
// one TripLeg per distinct route (primary at index 0), keep the leg axis (via/through) separate from
// the route axis (alternates), and keep RouteEngine.Route back-compatible (the primary leg only).
// Driven against the REAL Monaco tiles (artifacts/valhalla-monaco-tiles, not committed).

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

public sealed class RouteAlternatesTests
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

    private static GraphReader MakeReader(string root) => new(new GraphReader.Config { TileDir = root });

    private static AutoCost MakeAutoCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Auto };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return new AutoCost(costing);
    }

    private static List<PointLL> PickOnRoadPoints(string root, DynamicCost costing, int count)
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

            for (uint n = 0; n < tile.Header().Nodecount() && mids.Count < count; n++)
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

            if (mids.Count >= count)
            {
                break;
            }
        }

        return mids;
    }

    private static Options AlternatesOptions(uint count) => new()
    {
        Alternates = count,
        HasAlternates = count != 0,
    };

    // Hard proof that the alternates machinery genuinely emits multiple DISTINCT routes end to end on
    // the real Monaco graph. Monaco has looped streets, so at least one on-road pair yields >= 2 distinct
    // routes (empirically up to 3). We search a bounded set of on-road pairs and stop at the first pair
    // that produces alternates, asserting such a pair exists and its routes are distinct and cost-ordered.
    [Fact]
    public void RouteAlternates_EmitsMultipleDistinctRoutes_OnRealMonacoGraph()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        GraphReader reader = MakeReader(root);
        DynamicCost auto = MakeAutoCosting();

        // A bounded set of on-road points; a pair yielding alternates is found early (pts[0] -> pts[8]).
        List<PointLL> pts = PickOnRoadPoints(root, auto, 12);
        Assert.True(pts.Count >= 9, "need enough on-road points to find an alternate-bearing pair");

        IReadOnlyList<TripLeg>? multi = null;
        for (int i = 0; i < pts.Count && multi is null; i++)
        {
            for (int j = 0; j < pts.Count && multi is null; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var o = new Location(pts[i], Location.StopTypeValue.Break) { Radius = 100 };
                var d = new Location(pts[j], Location.StopTypeValue.Break) { Radius = 100 };
                try
                {
                    IReadOnlyList<TripLeg> legs = new RouteEngine(reader)
                        .RouteAlternates(reader, MakeAutoCosting(), o, d, vias: null, options: AlternatesOptions(2));
                    if (legs.Count > 1)
                    {
                        multi = legs;
                    }
                }
                catch (InvalidOperationException)
                {
                    // No route for this pair; keep searching.
                }
            }
        }

        Assert.NotNull(multi);
        Assert.True(multi!.Count > 1, "expected the engine to emit more than one route for some pair");

        // The routes must be genuinely distinct (different encoded shapes).
        var shapes = multi.Select(l => l.EncodedShape).ToList();
        Assert.Equal(shapes.Count, shapes.Distinct().Count());
        Assert.All(multi, leg => Assert.NotEmpty(leg.Edges));

        // Primary first: the engine orders alternates by COST (the stretch-sorted connection order), not
        // by elapsed time, so the primary route has the minimum total cost. (Cost includes turn/toll
        // penalties, so an alternate can have a lower elapsed-time yet a higher cost - hence we assert on
        // cost, the invariant the ordering actually guarantees.)
        var costs = multi.Select(l => l.Nodes[^1].ElapsedCost.CostValue).ToList();
        for (int k = 1; k < costs.Count; k++)
        {
            Assert.True(costs[k] >= costs[0],
                $"alternate {k} (cost {costs[k]}) should not be cheaper than the primary (cost {costs[0]})");
        }
    }

    [Fact]
    public void Route_BackCompat_ReturnsPrimaryRouteOnly()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root), "Monaco tile fixture not found.");

        GraphReader reader = MakeReader(root);
        List<PointLL> pts = PickOnRoadPoints(root, MakeAutoCosting(), 2);
        Assert.True(pts.Count >= 2, "need two on-road points in the Monaco fixture");

        var origin = new Location(pts[0], Location.StopTypeValue.Break) { Radius = 100 };
        var destination = new Location(pts[1], Location.StopTypeValue.Break) { Radius = 100 };

        // Route (back-compat) returns exactly the primary route; RouteAlternates[0] is the same route.
        TripLeg primary = new RouteEngine(reader).Route(reader, MakeAutoCosting(), origin, destination);
        IReadOnlyList<TripLeg> legs = new RouteEngine(reader)
            .RouteAlternates(reader, MakeAutoCosting(), origin, destination, vias: null, options: AlternatesOptions(2));

        Assert.NotEmpty(primary.Edges);
        Assert.Equal(legs[0].Edges[0].EdgeId, primary.Edges[0].EdgeId);
        Assert.Equal(legs[0].Edges[^1].EdgeId, primary.Edges[^1].EdgeId);
    }

    [Fact]
    public void RouteAlternates_WithVia_ReturnsExactlyOneStitchedLeg_EvenWhenAlternatesRequested()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root), "Monaco tile fixture not found.");

        GraphReader reader = MakeReader(root);
        DynamicCost auto = MakeAutoCosting();

        List<PointLL> pts = PickOnRoadPoints(root, auto, 3);
        Assert.True(pts.Count >= 3, "need three on-road points for a via route");

        var origin = new Location(pts[0], Location.StopTypeValue.Break) { Radius = 100 };
        var via = new Location(pts[1], Location.StopTypeValue.Through) { Radius = 100 };
        var destination = new Location(pts[2], Location.StopTypeValue.Break) { Radius = 100 };

        var engine = new RouteEngine(reader);

        // Even with alternates requested, a via route stays on the leg axis: exactly one stitched route.
        IReadOnlyList<TripLeg> legs = engine.RouteAlternates(reader, auto, origin, destination,
            vias: new[] { via }, options: AlternatesOptions(2));

        Assert.Single(legs);
        Assert.NotEmpty(legs[0].Edges);
    }
}
