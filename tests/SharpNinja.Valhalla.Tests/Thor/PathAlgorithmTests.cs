// Unit tests for the C# port of the thor PathAlgorithm base helpers (valhalla @ 3.7.0).
// Covers the free IsTrivial helper (same-edge origin-before-destination detection) and the
// EdgeMetadata iterator's advance/validity contract, plus the algorithm-base flag accessors.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Thor;

using CoreSif = SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Thor;

public class PathAlgorithmTests
{
    private static PathLocation Loc(double lng, double lat)
        => new PathLocation(new Location(new PointLL(lng, lat)));

    private static PathLocation.PathEdge Edge(GraphId id, double percentAlong)
        => new PathLocation.PathEdge(id, percentAlong, new PointLL(0, 0), 0);

    [Fact]
    public void IsTrivial_True_When_Same_Edge_And_Origin_Before_Destination()
    {
        var edge = new GraphId(100, 0, 5);
        PathLocation origin = Loc(7.42, 43.73);
        PathLocation dest = Loc(7.43, 43.74);
        origin.Edges.Add(Edge(edge, 0.25));
        dest.Edges.Add(Edge(edge, 0.75));

        Assert.True(PathAlgorithm.IsTrivial(edge, origin, dest));
    }

    [Fact]
    public void IsTrivial_False_When_Origin_After_Destination()
    {
        var edge = new GraphId(100, 0, 5);
        PathLocation origin = Loc(7.42, 43.73);
        PathLocation dest = Loc(7.43, 43.74);
        origin.Edges.Add(Edge(edge, 0.80));
        dest.Edges.Add(Edge(edge, 0.20));

        Assert.False(PathAlgorithm.IsTrivial(edge, origin, dest));
    }

    [Fact]
    public void IsTrivial_False_When_Different_Edges()
    {
        PathLocation origin = Loc(7.42, 43.73);
        PathLocation dest = Loc(7.43, 43.74);
        origin.Edges.Add(Edge(new GraphId(100, 0, 5), 0.10));
        dest.Edges.Add(Edge(new GraphId(100, 0, 6), 0.90));

        Assert.False(PathAlgorithm.IsTrivial(new GraphId(100, 0, 5), origin, dest));
    }

    [Fact]
    public void EdgeMetadata_Increment_Walks_Sequential_Edges_And_Goes_Invalid()
    {
        // Header-only tile with a small directed-edge count and a single node whose edge_index is 0
        // and edge_count is 3. We only exercise EdgeMetadata.Make/Increment over the edge ids and the
        // edge-status backing array (DirectedEdge reads against a header-only tile are not needed
        // because we only check ids and validity here).
        GraphTile tile = GraphTile.CreateForTest(new GraphId(7, 0, 0), GraphTileHeader.HeaderSize, 100);
        var edgeStatus = new EdgeStatus();

        var node = default(NodeInfo);
        node.SetEdgeIndex(0);
        node.SetEdgeCount(3);
        EdgeMetadata md = EdgeMetadata.Make(new GraphId(7, 0, 0), node, tile, edgeStatus);

        Assert.True(md.IsValid);
        Assert.Equal(0u, md.EdgeId.Id());

        md = md.Increment();
        Assert.True(md.IsValid);
        Assert.Equal(1u, md.EdgeId.Id());

        md = md.Increment();
        Assert.True(md.IsValid);
        Assert.Equal(2u, md.EdgeId.Id());

        md = md.Increment();
        Assert.False(md.IsValid);
    }

    private sealed class TestAlgorithm : PathAlgorithm
    {
        public TestAlgorithm()
            : base(0, false)
        {
        }

        public override System.Collections.Generic.List<System.Collections.Generic.List<PathInfo>> GetBestPath(
            PathLocation origin, PathLocation dest, GraphReader graphreader,
            CoreSif.ModeCosting modeCosting, CoreSif.TravelMode mode, CoreSif.Options? options = null)
            => new();

        public override string Name() => "test";

        public override void Clear()
        {
        }
    }

    [Fact]
    public void NotThruPruning_Defaults_True_And_Is_Settable()
    {
        var algo = new TestAlgorithm();
        Assert.True(algo.NotThruPruning());
        algo.SetNotThruPruning(false);
        Assert.False(algo.NotThruPruning());
    }

    [Fact]
    public void HasFerry_Defaults_False()
    {
        var algo = new TestAlgorithm();
        Assert.False(algo.HasFerry());
    }
}
