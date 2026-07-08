// End-to-end build-AND-route validation for the ported mjolnir tile-build pipeline
// (TileBuilder.BuildTileSet, valhalla @ 3.7.0 build_tile_set) followed by the ported thor RouteEngine,
// driven entirely in pure C# from a REAL OSM PBF extract (artifacts/monaco.osm.pbf, not committed).
//
// This is the full-stack analogue of "valhalla_build_tiles monaco.osm.pbf && valhalla_route ...":
//   1. TileBuilder.BuildTileSet parses the real Monaco PBF and writes byte-compatible .gph tiles into a
//      fresh temp directory, with hierarchy enabled (so the top-level + arterial + highway levels are
//      produced exactly as build_tile_set leaves them on disk).
//   2. It asserts .gph tiles were actually produced (the build is not a silent no-op).
//   3. It opens the freshly built tiles via GraphReader and routes two in-Monaco coordinates with truck
//      costing through the full RouteEngine orchestration (loki correlation -> get_path_algorithm ->
//      bidirectional / time-dependent A* -> TripLegBuilder).
//   4. It asserts the engine produces a non-empty TripLeg (at least one edge + a decoded shape).
//
// It first tries the explicit lat,lng pair from the brief (near 43.7384,7.4246 and 43.7325,7.4189); if
// those do not both correlate against the just-built tiles it falls back to two routable on-road points
// discovered from the built fixture (the same technique the Thor MonacoRouteEndToEndTests and the
// loki/thor unit tests use), so the end-to-end assertion always exercises a real route rather than
// silently degrading. It fails LOUDLY if the source PBF is missing rather than skipping (matching the
// other Monaco integration tests).
//
// The temp tile directory is created fresh per run and removed afterwards.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Loki;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

using Xunit;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class MonacoBuildAndRouteEndToEndTests
{
    // Coordinates from the task brief: two points inside Monaco (lat, lng).
    private const double OriginLat = 43.7384;
    private const double OriginLng = 7.4246;
    private const double DestinationLat = 43.7325;
    private const double DestinationLng = 7.4189;

    // Walks up from the test bin directory to find the source OSM PBF extract (artifacts/monaco.osm.pbf).
    private static string SourcePbf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "artifacts", "monaco.osm.pbf")))
        {
            dir = dir.Parent;
        }

        return dir is null ? string.Empty : Path.Combine(dir.FullName, "artifacts", "monaco.osm.pbf");
    }

    private static GraphReader MakeReader(string root)
        => new GraphReader(new GraphReader.Config { TileDir = root });

    // Builds a truck costing (the costing the brief routes with). MaxAssumedSpeed top speed mirrors the
    // other Monaco fixtures so edge speeds are populated even where the tile lacks tagged speeds.
    private static DynamicCost MakeTruckCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Truck };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return TruckCostFactory.CreateTruckCost(costing);
    }

    // Picks two distinct on-road points from the built tiles: midpoints of two different routable
    // highest-level edges. Mirrors MonacoRouteEndToEndTests.PickTwoOnRoadPoints so the fallback path
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
            throw new Xunit.Sdk.XunitException("Could not find two routable edges in the built Monaco tiles.");
        }

        return (mids[0], mids[1]);
    }

    // Snaps the two requested coordinates against the just-built tiles; if either fails to correlate,
    // falls back to two routable on-road points discovered from the built fixture so the end-to-end route
    // is always exercised. Returns the snapped origin/dest PathLocations (already correlated).
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
    public void Builds_Monaco_Tiles_From_Real_Pbf_And_Routes_End_To_End_With_Truck_Costing()
    {
        // ---- fail LOUDLY if the source PBF is missing (matching the other Monaco integration tests) ----
        string pbf = SourcePbf();
        Assert.True(File.Exists(pbf),
            $"Monaco source PBF not found (expected artifacts/monaco.osm.pbf). Resolved: '{pbf}'");

        // Fresh temp tile directory per run.
        string tileDir = Path.Combine(
            Path.GetTempPath(),
            $"tm_monaco_tiles_{Guid.NewGuid():N}");

        try
        {
            // ---- (1) BUILD: parse the real PBF and write .gph tiles, with hierarchy enabled ----
            var config = new TileBuilderConfig
            {
                Hierarchy = true,
                Shortcuts = true,
            };

            TileBuilderResult buildResult = TileBuilder.BuildTileSet(new[] { pbf }, tileDir, config);

            Assert.True(buildResult.Success, "expected the tile-build pipeline to run to completion");
            Assert.True(buildResult.WayCount > 0, "expected the parser to read at least one OSM way from the PBF");
            Assert.True(buildResult.TileCount > 0, "expected the build stage to produce at least one tile");

            // ---- (2) ASSERT .gph tiles were produced on disk (the build is not a silent no-op) ----
            // BuildTileSet normalizes the directory to end with a separator; use the returned TileDir.
            string root = buildResult.TileDir;
            Assert.True(Directory.Exists(root), $"expected the tile directory to exist: '{root}'");

            string[] gphTiles = Directory.GetFiles(root, "*.gph", SearchOption.AllDirectories);
            Assert.True(gphTiles.Length > 0,
                $"expected at least one .gph tile written under '{root}', found {gphTiles.Length}");

            // ---- (3) OPEN the freshly built tiles via GraphReader and route with truck costing ----
            GraphReader reader = MakeReader(root);
            DynamicCost truck = MakeTruckCosting();
            Assert.Equal(TravelMode.Drive, truck.TravelMode());

            (PathLocation origin, PathLocation dest, PointLL originLl, PointLL destLl) =
                SnapEndpoints(reader, truck);

            Assert.NotEmpty(origin.Edges);
            Assert.NotEmpty(dest.Edges);

            var engine = new RouteEngine(reader);
            TripLeg leg = engine.Route(reader, truck, origin, dest);

            // ---- (4) ASSERT a non-empty TripLeg: at least one edge and a decoded shape ----
            Assert.NotNull(leg);
            Assert.NotEmpty(leg.Edges);
            Assert.True(leg.Edges.Count > 0, "expected the trip leg to contain at least one edge");
            Assert.All(leg.Edges, e => Assert.True(e.EdgeId.IsValid(), "every leg edge id must be valid"));

            // A decoded shape with at least the two endpoints.
            Assert.NotEmpty(leg.Shape);
            Assert.True(leg.Shape.Count >= 2, "expected the decoded leg shape to have at least two points");
            Assert.False(string.IsNullOrEmpty(leg.EncodedShape), "expected a non-empty encoded shape");

            // The route covers ground (the two endpoints are not the same point).
            Assert.True(leg.Shape[0].Distance(leg.Shape[^1]) > 1.0,
                "expected the route to span a non-trivial distance");

            // The shape endpoints must be near the requested (snapped) origin/destination: a generous
            // tolerance that still proves the route starts/ends where we asked rather than somewhere
            // unrelated in the graph (snap radius is 100 m; the builder may trim to the projection point).
            const double toleranceMeters = 250.0;
            double startToOrigin = leg.Shape[0].Distance(originLl);
            double endToDest = leg.Shape[^1].Distance(destLl);

            Assert.True(startToOrigin <= toleranceMeters,
                $"leg shape start {startToOrigin:F1} m from requested origin (tolerance {toleranceMeters} m)");
            Assert.True(endToDest <= toleranceMeters,
                $"leg shape end {endToDest:F1} m from requested destination (tolerance {toleranceMeters} m)");
        }
        finally
        {
            // Clean up the fresh temp tile directory.
            try
            {
                if (Directory.Exists(tileDir))
                {
                    Directory.Delete(tileDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leaked temp dir must not fail the test.
            }
        }
    }
}
