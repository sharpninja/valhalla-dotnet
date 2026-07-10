// End-to-end coverage of the PUBLIC routing surface (A5): EmbeddedValhallaRoutingClient must now
// surface Odin NarrativeBuilder prose on OsmRouteManeuver.Instruction. Drives the real embedded stack
// (tile provider -> reader factory -> RouteEngine -> DirectionsBuilder with DirectionsType.Instructions
// -> NarrativeBuilder) against the REAL Monaco tiles (artifacts/valhalla-monaco-tiles, not committed).
//
// This is the first test on the public client. It resolves two routable on-road points from the
// fixture (so the route always exercises real roads, matching the sibling Monaco integration tests),
// feeds them to CalculateRouteAsync, and asserts every maneuver carries non-empty written prose, the
// first maneuver is a start verb, and the last is the arrival phrase. Fails loudly if the fixture is
// absent (no Skip).

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

public sealed class EmbeddedValhallaRoutingClientInstructionTests
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

    // Two distinct routable on-road points from the fixture (midpoints of two routable highest-level
    // edges). Mirrors MonacoRouteEndToEndTests.PickTwoOnRoadPoints. Returned as (lat, lng).
    private static (GeoCoordinate A, GeoCoordinate B) PickTwoOnRoadPoints(string root, DynamicCost costing)
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

        // PointLL is (lng, lat); GeoCoordinate is (lat, lng).
        return (new GeoCoordinate(mids[0].Lat, mids[0].Lng), new GeoCoordinate(mids[1].Lat, mids[1].Lng));
    }

    private static AutoCost MakeAutoCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Auto };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return new AutoCost(costing);
    }

    private static readonly string[] StartVerbs = { "Head ", "Drive ", "Walk ", "Bike " };

    [Fact]
    public async Task CalculateRouteAsync_PopulatesNonEmptyInstructionOnEveryManeuver()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        (GeoCoordinate origin, GeoCoordinate destination) = PickTwoOnRoadPoints(root, MakeAutoCosting());

        var client = new EmbeddedValhallaRoutingClient(
            new EmbeddedValhallaGraphReaderFactory(),
            new FixedTileDirectoryProvider(root),
            NullLogger<EmbeddedValhallaRoutingClient>.Instance);

        var request = new OsmRouteRequest(
            Endpoint: null,
            Origin: origin,
            Destination: destination,
            Costing: OsmRouteCostings.Auto);

        OsmRouteResult result = await client.CalculateRouteAsync(request, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.NotEmpty(result.Routes);

        OsmRouteCandidate candidate = result.Routes[0];
        Assert.NotEmpty(candidate.Maneuvers);

        // Every maneuver in a driving route carries written prose now (all driving maneuver types are
        // covered by the ported NarrativeBuilder written path).
        Assert.All(candidate.Maneuvers, m => Assert.False(string.IsNullOrWhiteSpace(m.Instruction),
            $"maneuver type {m.Type} had empty Instruction"));

        // First maneuver is a start/depart verb; last is the arrival phrase.
        string first = candidate.Maneuvers[0].Instruction;
        Assert.True(StartVerbs.Any(v => first.StartsWith(v, StringComparison.Ordinal)),
            $"first instruction should start with a start verb but was: '{first}'");

        string last = candidate.Maneuvers[^1].Instruction;
        Assert.Contains("arrived at your destination", last, StringComparison.OrdinalIgnoreCase);
    }
}
