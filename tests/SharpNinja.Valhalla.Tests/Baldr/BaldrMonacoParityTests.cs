using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpNinja.Valhalla.Baldr;
using Xunit;

namespace SharpNinja.Valhalla.Tests.Baldr;

/// <summary>
/// Fidelity gate for the baldr tile reader: parses REAL stock-Valhalla tiles (Monaco, built by
/// valhalla_build_tiles @ 3.7.0) and asserts the C# reader reproduces a sane graph + valid edge
/// names. The synthetic unit tests cannot catch on-disk-layout regressions (e.g. the EdgeInfo
/// names-buffer defect); this can. The fixture lives under artifacts/ (not committed); the test
/// fails loudly if it is missing rather than silently skipping.
/// </summary>
public sealed class BaldrMonacoParityTests
{
    private static string FixtureDir()
    {
        // Walk up from the test bin dir to the repo root, then artifacts/valhalla-monaco-tiles.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles")))
        {
            dir = dir.Parent;
        }
        return dir is null ? string.Empty : Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles");
    }

    private static (uint level, uint tileId) ParseTilePath(string root, string gphPath)
    {
        var rel = Path.GetRelativePath(root, gphPath).Replace('\\', '/');
        var parts = rel.Split('/');
        var level = uint.Parse(parts[0]);
        var digits = string.Concat(parts.Skip(1).Select(p => p.Replace(".gph", string.Empty)));
        return (level, uint.Parse(digits));
    }

    [Fact]
    public void Reads_real_monaco_tiles_with_sane_graph_and_valid_names()
    {
        var root = FixtureDir();
        Assert.True(Directory.Exists(root), $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        var gph = Directory.GetFiles(root, "*.gph", SearchOption.AllDirectories);
        Assert.NotEmpty(gph);

        long totalNodes = 0, totalEdges = 0, namedEdges = 0;
        var sampleNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in gph)
        {
            var (level, tileId) = ParseTilePath(root, file);
            var baseId = new GraphId(tileId, level, 0);
            var tile = GraphTile.Create(root, baseId);
            Assert.NotNull(tile);

            var nodeCount = tile!.Header().Nodecount();
            var edgeCount = tile.Header().Directededgecount();
            totalNodes += nodeCount;
            totalEdges += edgeCount;

            for (var i = 0; i < (int)edgeCount; i++)
            {
                var de = tile.DirectedEdge(i);
                List<string> names;
                try { names = tile.EdgeInfo(de).GetNames(); }
                catch (Exception ex) { throw new Xunit.Sdk.XunitException($"EdgeInfo/GetNames threw on {level}/{tileId} edge {i}: {ex.Message}"); }

                foreach (var n in names)
                {
                    Assert.False(string.IsNullOrEmpty(n), "edge name was empty");
                    // Corrupt name-buffer reads produce control/garbage bytes; valid OSM names are printable.
                    Assert.DoesNotContain(n, s => char.IsControl(s));
                    namedEdges++;
                    if (sampleNames.Count < 50) sampleNames.Add(n);
                }
            }
        }

        Assert.True(totalNodes > 0, "no nodes parsed from Monaco tiles");
        Assert.True(totalEdges > 0, "no directed edges parsed from Monaco tiles");
        Assert.True(namedEdges > 0, "no named edges parsed - EdgeInfo names buffer likely wrong");

        // Monaco streets are predominantly French; a faithful read yields recognizable prefixes.
        var hasFrenchStreet = sampleNames.Any(n =>
            n.Contains("Boulevard", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Avenue", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Rue", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Quai", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Place", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasFrenchStreet, "no recognizable Monaco street name parsed; sample: " + string.Join(" | ", sampleNames.Take(15)));
    }
}
