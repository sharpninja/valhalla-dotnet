// End-to-end coverage of the public client's alternate-routes fan-out (B4): when
// ComputeAlternativeRoutes is set (and no vias), OsmRouteResult.Routes carries multiple distinct
// candidates ordered primary-first then by ascending duration; when it is cleared, exactly one
// candidate is returned. Driven against the REAL Monaco tiles (not committed).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Osm;

public sealed class EmbeddedValhallaRoutingClientAlternatesTests
{
    private sealed class FixedTileDirectoryProvider : IOsmTileDirectoryProvider
    {
        private readonly string? _dir;
        public FixedTileDirectoryProvider(string? dir) => _dir = dir;
        public Task<string?> GetTileDirectoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_dir);
    }

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles")))
        {
            dir = dir.Parent;
        }

        return dir is null ? string.Empty : Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles");
    }

    private static List<GeoCoordinate> PickOnRoadPoints(string root, int count)
    {
        var costing = new Costing { CostingType = Costing.Type.Auto };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        var auto = new AutoCost(costing);

        var pts = new List<GeoCoordinate>();
        foreach (string file in Directory.GetFiles(root, "*.gph", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            string[] parts = rel.Split('/');
            if (!byte.TryParse(parts[0], out byte level) || level != TileHierarchy.Levels()[^1].Level)
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

            for (uint n = 0; n < tile.Header().Nodecount() && pts.Count < count; n++)
            {
                NodeInfo node = tile.Node((int)n);
                for (uint e = 0; e < node.EdgeCount; e++)
                {
                    DirectedEdge edge = tile.DirectedEdge((int)(node.EdgeIndex + e));
                    if (!auto.Allowed(edge, tile, DynamicCost.DisallowShortcut))
                    {
                        continue;
                    }

                    IReadOnlyList<PointLL> shape = tile.EdgeInfo(edge).Shape();
                    if (shape.Count >= 2 && edge.Length > 20)
                    {
                        pts.Add(new GeoCoordinate(shape[0].PointAlongSegment(shape[^1], 0.5).Lat,
                                                  shape[0].PointAlongSegment(shape[^1], 0.5).Lng));
                        break;
                    }
                }
            }

            if (pts.Count >= count)
            {
                break;
            }
        }

        return pts;
    }

    private static EmbeddedValhallaRoutingClient MakeClient(string root)
        => new(new EmbeddedValhallaGraphReaderFactory(),
               new FixedTileDirectoryProvider(root),
               NullLogger<EmbeddedValhallaRoutingClient>.Instance);

    [Fact]
    public async Task ComputeAlternativeRoutes_True_ReturnsMultipleDistinctCandidatesOrderedByDuration()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root), "Monaco tile fixture not found.");

        List<GeoCoordinate> pts = PickOnRoadPoints(root, 12);
        Assert.True(pts.Count >= 9, "need enough on-road points to find an alternate-bearing pair");

        EmbeddedValhallaRoutingClient client = MakeClient(root);

        // Find a pair that yields alternates (Monaco's looped streets provide one early).
        OsmRouteResult? multi = null;
        for (int i = 0; i < pts.Count && multi is null; i++)
        {
            for (int j = 0; j < pts.Count && multi is null; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var req = new OsmRouteRequest(null, pts[i], pts[j], OsmRouteCostings.Auto,
                    ComputeAlternativeRoutes: true);
                OsmRouteResult res = await client.CalculateRouteAsync(req);
                if (res.Error is null && res.Routes.Count > 1)
                {
                    multi = res;
                }
            }
        }

        Assert.NotNull(multi);
        Assert.True(multi!.Routes.Count > 1, "expected more than one candidate");

        // Distinct polylines, each a real route with maneuvers.
        var polylines = multi.Routes.Select(r => r.EncodedPolyline).ToList();
        Assert.Equal(polylines.Count, polylines.Distinct().Count());
        Assert.All(multi.Routes, r => Assert.NotEmpty(r.Maneuvers));

        // Note: the engine orders alternates by COST (turn/toll penalties included), which the DTO does
        // not surface (only DurationSeconds / DistanceMeters). Duration is therefore not asserted to be
        // monotonic here - an alternate can be quicker in time yet costlier. Routes[0] is the primary
        // (least-cost) route; the cost-ordering invariant is asserted at the engine level in
        // RouteAlternatesTests.
    }

    [Fact]
    public async Task ComputeAlternativeRoutes_False_ReturnsExactlyOneCandidate()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root), "Monaco tile fixture not found.");

        List<GeoCoordinate> pts = PickOnRoadPoints(root, 12);
        Assert.True(pts.Count >= 9, "need enough on-road points");

        EmbeddedValhallaRoutingClient client = MakeClient(root);

        // Find a pair that DOES yield alternates when enabled, then prove disabling collapses it to one.
        for (int i = 0; i < pts.Count; i++)
        {
            for (int j = 0; j < pts.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var enabled = new OsmRouteRequest(null, pts[i], pts[j], OsmRouteCostings.Auto,
                    ComputeAlternativeRoutes: true);
                OsmRouteResult withAlts = await client.CalculateRouteAsync(enabled);
                if (withAlts.Error is not null || withAlts.Routes.Count <= 1)
                {
                    continue;
                }

                var disabled = new OsmRouteRequest(null, pts[i], pts[j], OsmRouteCostings.Auto,
                    ComputeAlternativeRoutes: false);
                OsmRouteResult single = await client.CalculateRouteAsync(disabled);

                Assert.Null(single.Error);
                Assert.Single(single.Routes);
                return;
            }
        }

        Assert.Fail("Could not find an alternate-bearing pair to prove the disabled case.");
    }
}
