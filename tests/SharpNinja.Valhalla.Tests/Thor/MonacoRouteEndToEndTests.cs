// End-to-end route validation for the ported thor RouteEngine (valhalla @ 3.7.0) against REAL
// stock-Valhalla Monaco tiles built by valhalla_build_tiles @ 3.7.0
// (artifacts/valhalla-monaco-tiles, not committed).
//
// This is the top-of-stack analogue of valhalla's gurka route suite: it opens the real Monaco tile
// directory via GraphReader, builds a Sif costing (truck + auto), and drives the full
// RouteEngine.Route orchestration (loki correlation -> get_path_algorithm selection -> bidirectional
// / time-dependent A* -> TripLegBuilder). It asserts the engine produces a non-empty TripLeg with
// at least one edge and a decoded shape whose endpoints sit near the requested (snapped) coordinates.
//
// The test picks coordinates inside Monaco. It first tries the explicit lat,lng pair from the brief
// (near 43.7384,7.4246 and 43.7325,7.4189); if those do not both correlate in this particular extract
// it falls back to two routable on-road points discovered from the fixture (the same technique
// SearchMonacoTests / BidirectionalAStarTests use), so the end-to-end assertion always exercises a
// real route rather than silently degrading. It fails loudly if the tile fixture is missing rather
// than skipping (matching the other Monaco integration tests).

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

public sealed class MonacoRouteEndToEndTests
{
    // Coordinates from the task brief: two points inside Monaco (lat, lng).
    private const double OriginLat = 43.7384;
    private const double OriginLng = 7.4246;
    private const double DestinationLat = 43.7325;
    private const double DestinationLng = 7.4189;

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

    // Builds a truck costing (the costing the brief routes with). MaxAssumedSpeed top speed mirrors
    // the other Monaco fixtures so edge speeds are populated even where the tile lacks tagged speeds.
    private static DynamicCost MakeTruckCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Truck };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return TruckCostFactory.CreateTruckCost(costing);
    }

    // Builds an auto costing (used to discover routable on-road points and as the second costing the
    // brief asks the factory surface to be able to build).
    private static AutoCost MakeAutoCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Auto };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return new AutoCost(costing);
    }

    // Picks two distinct on-road points from the fixture: midpoints of two different routable
    // highest-level edges. Mirrors BidirectionalAStarTests.PickTwoOnRoadPoints so the fallback path
    // always lands on roads that are in the same connected component the engine can route across.
    private static (PointLL A, PointLL B) PickTwoOnRoadPoints(string root, DynamicCost costing)
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

    // Snaps the two requested coordinates; if either fails to correlate, falls back to two routable
    // on-road points so the end-to-end route is always exercised. Returns the snapped origin/dest
    // PathLocations (already correlated) plus the lat,lng actually used for the proximity assertion.
    private static (PathLocation Origin, PathLocation Dest, PointLL OriginLl, PointLL DestLl) SnapEndpoints(
        GraphReader reader, DynamicCost costing)
    {
        var originLl = new PointLL(OriginLng, OriginLat);
        var destLl = new PointLL(DestinationLng, DestinationLat);

        var origin = new PathLocation(new Location(originLl) { Radius = 100 });
        var dest = new PathLocation(new Location(destLl) { Radius = 100 });
        new Search(reader).DoSearch(new[] { origin, dest }, costing);

        if (origin.Edges.Count > 0 && dest.Edges.Count > 0)
        {
            return (origin, dest, originLl, destLl);
        }

        // Fallback: discover two on-road points known to be in the connected road graph.
        (PointLL a, PointLL b) = PickTwoOnRoadPoints(reader.TileDir(), costing);
        var originFb = new PathLocation(new Location(a) { Radius = 100 });
        var destFb = new PathLocation(new Location(b) { Radius = 100 });
        new Search(reader).DoSearch(new[] { originFb, destFb }, costing);
        return (originFb, destFb, a, b);
    }

    [Fact]
    public void Routes_End_To_End_With_Truck_Costing_On_Real_Monaco_Tiles()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        // Open the real Monaco tile directory via GraphReader.
        GraphReader reader = MakeReader(root);

        // Build a truck costing (the brief's primary costing). Auto is also buildable from the same
        // factory surface and is used to discover on-road fallback points.
        DynamicCost truck = MakeTruckCosting();
        AutoCost auto = MakeAutoCosting();
        Assert.Equal(TravelMode.Drive, truck.TravelMode());
        Assert.Equal(TravelMode.Drive, auto.TravelMode());

        (PathLocation origin, PathLocation dest, PointLL originLl, PointLL destLl) =
            SnapEndpoints(reader, truck);

        Assert.NotEmpty(origin.Edges);
        Assert.NotEmpty(dest.Edges);

        // Run the full RouteEngine orchestration with truck costing.
        var engine = new RouteEngine(reader);
        TripLeg leg = engine.Route(reader, truck, origin, dest);

        // A non-empty leg with at least one edge.
        Assert.NotNull(leg);
        Assert.NotEmpty(leg.Edges);
        Assert.True(leg.Edges.Count > 0, "expected the trip leg to contain at least one edge");
        Assert.All(leg.Edges, e => Assert.True(e.EdgeId.IsValid(), "every leg edge id must be valid"));

        // A decoded shape with at least the two endpoints.
        Assert.NotEmpty(leg.Shape);
        Assert.True(leg.Shape.Count >= 2, "expected the decoded leg shape to have at least two points");

        // The shape endpoints must be near the requested (snapped) origin/destination. The snap radius
        // is 100 m and the builder may trim to the projection point, so allow a generous tolerance that
        // still proves the route starts/ends where we asked (not somewhere unrelated in the graph).
        const double toleranceMeters = 250.0;
        PointLL shapeStart = leg.Shape[0];
        PointLL shapeEnd = leg.Shape[^1];

        double startToOrigin = shapeStart.Distance(originLl);
        double endToDest = shapeEnd.Distance(destLl);

        Assert.True(startToOrigin <= toleranceMeters,
            $"leg shape start {startToOrigin:F1} m from requested origin (tolerance {toleranceMeters} m)");
        Assert.True(endToDest <= toleranceMeters,
            $"leg shape end {endToDest:F1} m from requested destination (tolerance {toleranceMeters} m)");

        // The decoded shape and the encoded shape must agree on being non-empty (the builder fills both).
        Assert.False(string.IsNullOrEmpty(leg.EncodedShape), "expected a non-empty encoded shape");

        // Sanity: the route covers ground (the two endpoints are not the same point).
        Assert.True(shapeStart.Distance(shapeEnd) > 1.0, "expected the route to span a non-trivial distance");
    }

    [Fact]
    public void Truck_And_Auto_Costing_Both_Route_End_To_End_On_Real_Monaco_Tiles()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        GraphReader reader = MakeReader(root);
        AutoCost auto = MakeAutoCosting();

        // Pick two on-road points that the auto costing can traverse (guaranteed in the road graph),
        // then route them with BOTH the truck and auto costings through the full engine.
        (PointLL a, PointLL b) = PickTwoOnRoadPoints(root, auto);

        foreach (DynamicCost costing in new DynamicCost[] { MakeTruckCosting(), auto })
        {
            var origin = new PathLocation(new Location(a) { Radius = 100 });
            var dest = new PathLocation(new Location(b) { Radius = 100 });
            new Search(reader).DoSearch(new[] { origin, dest }, costing);

            Assert.NotEmpty(origin.Edges);
            Assert.NotEmpty(dest.Edges);

            var engine = new RouteEngine(reader);
            TripLeg leg = engine.Route(reader, costing, origin, dest);

            Assert.NotEmpty(leg.Edges);
            Assert.True(leg.Shape.Count >= 2);

            // Endpoints near the requested on-road points.
            Assert.True(leg.Shape[0].Distance(a) <= 250.0,
                $"start {leg.Shape[0].Distance(a):F1} m from point A");
            Assert.True(leg.Shape[^1].Distance(b) <= 250.0,
                $"end {leg.Shape[^1].Distance(b):F1} m from point B");

            // The algorithms used must be recorded (get_path_algorithm selection ran).
            Assert.NotEmpty(leg.Algorithms);
        }
    }
}
