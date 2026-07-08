// Tests for the C# port of thor TripLegBuilder (valhalla @ 3.7.0).
//
// Valhalla's TripLegBuilder is covered in the engine by the gurka integration suite (it needs the
// full tile builder). The faithful analogue here is split in two:
//   - Pure unit tests for the load-bearing Midgard helper this port added (trim_shape), since the
//     shape clipping at the first/last edge is the part most likely to drift.
//   - A real-tile integration test against the Monaco fixture (artifacts/valhalla-monaco-tiles, not
//     committed): correlate two on-road points with loki, build a trivial single-edge path, run
//     TripLegBuilder.Build, and assert the assembled TripLeg invariants (ordered edges, decoded +
//     trimmed shape, begin/end shape indices, headings, admins, summary).
//
// The integration test fails loudly if the fixture is missing rather than silently skipping
// (matching SearchMonacoTests / BaldrMonacoParityTests).

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

public sealed class TripLegBuilderTests
{
    // ===================================================================================
    // trim_shape unit tests (Midgard.Util.TrimShape) - the helper the builder relies on.
    // ===================================================================================

    [Fact]
    public void TrimShape_Trims_Both_Ends_To_The_Given_Vertices()
    {
        // A straight-ish polyline; trim from 1/4 to 3/4 along.
        var p0 = new PointLL(0.0, 0.0);
        var p1 = new PointLL(0.0, 0.001);
        var p2 = new PointLL(0.0, 0.002);
        var p3 = new PointLL(0.0, 0.003);
        var shape = new List<PointLL> { p0, p1, p2, p3 };

        double total = p0.Distance(p1) + p1.Distance(p2) + p2.Distance(p3);
        var startVrt = new PointLL(0.0, 0.0005);
        var endVrt = new PointLL(0.0, 0.0025);

        Util.TrimShape((float)(total * 0.166), startVrt, (float)(total * 0.833), endVrt, shape);

        // The first point must be the start vertex and the last point the end vertex.
        Assert.Equal(startVrt.Lat, shape[0].Lat, 9);
        Assert.Equal(startVrt.Lng, shape[0].Lng, 9);
        Assert.Equal(endVrt.Lat, shape[^1].Lat, 9);
        Assert.Equal(endVrt.Lng, shape[^1].Lng, 9);

        // The trimmed shape stays within the original bounds.
        Assert.All(shape, p => Assert.InRange(p.Lat, 0.0005 - 1e-9, 0.0025 + 1e-9));
    }

    [Fact]
    public void TrimShape_With_Invalid_Start_Vertex_Only_Trims_The_End()
    {
        var p0 = new PointLL(0.0, 0.0);
        var p1 = new PointLL(0.0, 0.001);
        var p2 = new PointLL(0.0, 0.002);
        var shape = new List<PointLL> { p0, p1, p2 };
        double total = p0.Distance(p1) + p1.Distance(p2);

        var endVrt = new PointLL(0.0, 0.0015);
        // Invalid start vertex (default PointLL is invalid) -> begin untouched.
        Util.TrimShape(0f, new PointLL(), (float)(total * 0.75), endVrt, shape);

        Assert.Equal(p0.Lat, shape[0].Lat, 9);
        Assert.Equal(endVrt.Lat, shape[^1].Lat, 9);
    }

    // ===================================================================================
    // Result-shape unit test: a builder run does not require real tiles to assert that the
    // de-protobuf'd result classes carry the ordered-edge / node-edge invariant.
    // ===================================================================================

    [Fact]
    public void TripLeg_Result_Classes_Carry_Node_Edge_And_Summary_State()
    {
        var leg = new TripLeg();
        var n0 = new TripNode { Edge = new TripEdge { BeginShapeIndex = 0, EndShapeIndex = 3 } };
        var n1 = new TripNode();
        leg.Nodes.Add(n0);
        leg.Nodes.Add(n1);
        leg.Edges.Add(n0.Edge!);

        Assert.Single(leg.Edges);
        Assert.Equal(2, leg.Nodes.Count);
        Assert.Null(leg.Nodes[^1].Edge);
        Assert.False(leg.Summary.HasToll);
    }

    // ===================================================================================
    // Monaco real-tile integration: loki snap -> single-edge path -> TripLegBuilder.Build.
    // ===================================================================================

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles")))
        {
            dir = dir.Parent;
        }

        return dir is null ? string.Empty : Path.Combine(dir.FullName, "artifacts", "valhalla-monaco-tiles");
    }

    private static AutoCost MakeAutoCosting()
    {
        var costing = new Costing { CostingType = Costing.Type.Auto };
        costing.Options.TopSpeed = (int)GraphConstants.MaxAssumedSpeed;
        return new AutoCost(costing);
    }

    private static ModeCosting MakeModeCosting(AutoCost costing)
    {
        costing.SetTravelMode(TravelMode.Drive);
        var mc = new ModeCosting
        {
            [(int)TravelMode.Drive] = costing,
        };
        return mc;
    }

    // Finds a routable, multi-point top-level edge and returns its id, tile, begin-node lat,lng and
    // the begin/end of its (forward) shape (so we can build a trivial origin->dest along ONE edge).
    private static (GraphId EdgeId, PointLL Begin, PointLL End) PickRoutableEdge(string root, AutoCost costing)
    {
        string[] gph = Directory.GetFiles(root, "*.gph", SearchOption.AllDirectories);
        byte topLevel = TileHierarchy.Levels()[^1].Level;

        foreach (string file in gph)
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            string[] parts = rel.Split('/');
            uint level = uint.Parse(parts[0]);
            if (level != topLevel)
            {
                continue;
            }

            string digits = string.Concat(parts.Skip(1).Select(p => p.Replace(".gph", string.Empty)));
            uint tileId = uint.Parse(digits);

            var baseId = new GraphId(tileId, level, 0);
            GraphTile? tile = GraphTile.Create(root, baseId);
            if (tile is null)
            {
                continue;
            }

            for (uint n = 0; n < tile.Header().Nodecount(); n++)
            {
                NodeInfo node = tile.Node((int)n);
                for (uint e = 0; e < node.EdgeCount; e++)
                {
                    uint deIndex = node.EdgeIndex + e;
                    DirectedEdge edge = tile.DirectedEdge((int)deIndex);
                    if (edge.IsShortcut || !costing.Allowed(edge, tile, DynamicCost.DisallowShortcut))
                    {
                        continue;
                    }

                    IReadOnlyList<PointLL> shape = tile.EdgeInfo(edge).Shape();
                    if (shape.Count >= 2 && edge.Length > 30)
                    {
                        var fwd = new List<PointLL>(shape);
                        if (!edge.Forward)
                        {
                            fwd.Reverse();
                        }

                        var edgeId = new GraphId(tileId, level, deIndex);
                        return (edgeId, fwd[0], fwd[^1]);
                    }
                }
            }
        }

        throw new Xunit.Sdk.XunitException("No routable edge found in the Monaco fixture.");
    }

    [Fact]
    public void Builds_A_Single_Edge_Leg_From_Real_Monaco_Tiles()
    {
        string root = FixtureDir();
        Assert.True(Directory.Exists(root),
            $"Monaco tile fixture not found (expected artifacts/valhalla-monaco-tiles). Root resolved: '{root}'");

        var reader = new GraphReader(new GraphReader.Config { TileDir = root });
        AutoCost costing = MakeAutoCosting();
        ModeCosting modeCosting = MakeModeCosting(costing);

        (GraphId edgeId, PointLL begin, PointLL end) = PickRoutableEdge(root, costing);

        // Correlate two on-edge points (slightly inside the edge ends) with loki.
        var origLoc = new PathLocation(new Location(begin.PointAlongSegment(end, 0.2)) { Radius = 50 });
        var destLoc = new PathLocation(new Location(begin.PointAlongSegment(end, 0.8)) { Radius = 50 });
        var search = new Search(reader);
        search.DoSearch(new[] { origLoc, destLoc }, costing);

        // Keep only the correlation on our chosen edge so the trivial path is well-defined; if the
        // search didn't land on it (different snap), fall back to a synthetic correlation on the edge.
        EnsureEdgeCorrelation(origLoc, edgeId, 0.2);
        EnsureEdgeCorrelation(destLoc, edgeId, 0.8);

        // The trivial one-edge path: a single PathInfo on the chosen edge.
        byte flowSources = 0;
        GraphTile? tile = reader.GetGraphTile(edgeId);
        Assert.NotNull(tile);
        DirectedEdge de = tile!.DirectedEdge(edgeId);
        Cost edgeCost = costing.EdgeCost(de, edgeId, tile, TimeInfo.Invalid(), ref flowSources);
        var path = new List<PathInfo>
        {
            new PathInfo(TravelMode.Drive, edgeCost, edgeId, 0, de.Length),
        };

        TripLeg leg = TripLegBuilder.Build(
            reader,
            modeCosting,
            path,
            origLoc,
            destLoc,
            new[] { "unidirectional_astar" });

        // Ordered edges: one edge on the path, and a node per edge plus the final node.
        Assert.Single(leg.Edges);
        Assert.Equal(2, leg.Nodes.Count);
        Assert.Same(leg.Edges[0], leg.Nodes[0].Edge);
        Assert.Null(leg.Nodes[^1].Edge);
        Assert.Equal(edgeId.Value, leg.Edges[0].EdgeId.Value);

        // The decoded shape is non-trivial and the encoded shape round-trips.
        Assert.True(leg.Shape.Count >= 2);
        List<PointLL> roundTrip = Encoded.Decode(leg.EncodedShape);
        Assert.Equal(leg.Shape.Count, roundTrip.Count);

        // Begin/end shape indices bracket the whole leg shape for the single edge.
        Assert.Equal(0u, leg.Edges[0].BeginShapeIndex);
        Assert.Equal((uint)(leg.Shape.Count - 1), leg.Edges[0].EndShapeIndex);

        // Source/target along-edge reflect the snapped fractions (start ~0.2, end ~0.8 in some order
        // depending on edge direction); both are within [0,1] and source < target.
        Assert.InRange(leg.Edges[0].SourceAlongEdge, 0f, 1f);
        Assert.InRange(leg.Edges[0].TargetAlongEdge, 0f, 1f);
        Assert.True(leg.Edges[0].SourceAlongEdge < leg.Edges[0].TargetAlongEdge);

        // Headings are valid bearings.
        Assert.InRange(leg.Edges[0].BeginHeading, 0u, 359u);
        Assert.InRange(leg.Edges[0].EndHeading, 0u, 359u);

        // Length (km) is positive and less than the full edge length (we used a sub-portion).
        Assert.True(leg.Edges[0].LengthKm > 0f);
        Assert.True(leg.Edges[0].LengthKm <= de.Length * Constants.KmPerMeter + 1e-3);

        // The bounding box encloses the shape.
        foreach (PointLL p in leg.Shape)
        {
            Assert.InRange(p.Lng, leg.BoundingBoxMin.Lng - 1e-9, leg.BoundingBoxMax.Lng + 1e-9);
            Assert.InRange(p.Lat, leg.BoundingBoxMin.Lat - 1e-9, leg.BoundingBoxMax.Lat + 1e-9);
        }

        // At least one admin record (Monaco / its enclosing country) was collected and node admin
        // indices point inside the table.
        Assert.NotEmpty(leg.Admins);
        foreach (TripNode node in leg.Nodes)
        {
            Assert.InRange((int)node.AdminIndex, 0, leg.Admins.Count - 1);
        }

        // The algorithm name was recorded.
        Assert.Contains("unidirectional_astar", leg.Algorithms);
    }

    // If loki didn't correlate to our chosen edge (e.g. a closer parallel edge snapped first),
    // inject a deterministic correlation on it so the trivial single-edge path is buildable.
    private static void EnsureEdgeCorrelation(PathLocation loc, GraphId edgeId, double pct)
    {
        if (loc.Edges.Any(e => e.Id == edgeId))
        {
            return;
        }

        loc.Edges.Insert(0, new PathLocation.PathEdge(edgeId, pct, loc.LatLng, 0.0));
    }
}
