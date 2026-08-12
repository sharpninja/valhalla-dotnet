using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledWayEdgeSemanticsTests
{
    [Fact]
    public void Project_TransformedDirectionalTagsPreservesCoreEdgeSemantics()
    {
        IReadOnlyDictionary<string, string> tags =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["auto_forward"] = "true",
                ["truck_forward"] = "true",
                ["bus_forward"] = "false",
                ["pedestrian_forward"] = "false",
                ["auto_backward"] = "false",
                ["truck_backward"] = "false",
                ["pedestrian_backward"] = "true",
                ["road_class"] = ((byte)RoadClass.Motorway).ToString(),
                ["link"] = "true",
                ["ferry"] = "true",
                ["oneway"] = "true",
                ["roundabout"] = "true",
                ["private"] = "true",
                ["private_hgv"] = "true",
                ["no_thru_traffic"] = "true",
                ["name"] = "Interstate 40",
            };

        PooledWayEdgeSemantics result =
            PooledWayEdgeSemantics.Project(tags, attributeReference: 1234);

        Assert.Equal(
            (uint)(GraphConstants.AutoAccess | GraphConstants.TruckAccess),
            result.ForwardAccess);
        Assert.Equal((uint)GraphConstants.PedestrianAccess, result.ReverseAccess);
        Assert.Equal((byte)RoadClass.Primary, result.Importance);
        Assert.True(result.Flags.HasFlag(EdgeSemanticFlags.Ferry));
        Assert.True(result.Flags.HasFlag(EdgeSemanticFlags.Link));
        Assert.True(result.Flags.HasFlag(EdgeSemanticFlags.Oneway));
        Assert.True(result.Flags.HasFlag(EdgeSemanticFlags.Roundabout));
        Assert.True(result.Flags.HasFlag(EdgeSemanticFlags.DestinationOnly));
        Assert.True(result.Flags.HasFlag(EdgeSemanticFlags.DestinationOnlyHgv));
        Assert.True(result.Flags.HasFlag(EdgeSemanticFlags.NoThruTraffic));
        Assert.True(result.HasNames);
        Assert.Equal(1234, result.AttributeReference);
    }

    [Fact]
    public void BeginWay_SemanticsCanonicalIdentityAndTrafficControlPersistOnEveryEdge()
    {
        using var arena = new PooledNodeArena(slabCapacity: 4, memoryBudgetBytes: 4096);
        var sink = new RecordingEdgeSink();
        var frontier = new PooledPathFrontier(arena, sink);
        var semantics = new PooledWayEdgeSemantics(
            EdgeSemanticFlags.Link,
            GraphConstants.AutoAccess | GraphConstants.TruckAccess,
            GraphConstants.PedestrianAccess,
            AttributeReference: 812,
            Importance: (byte)RoadClass.Primary,
            HasNames: true);

        using PooledPathWaySession session = frontier.BeginWay(
            wayId: long.MaxValue - 1,
            canonicalOrdinal: 7,
            semantics);
        session.Append(
            Node(1, isAnchor: true, new GraphId(5, 0, 1)),
            TestContext.Current.CancellationToken);
        session.Append(
            Node(
                2,
                isAnchor: false,
                GraphId.Invalid,
                NodeSemanticFlags.TrafficSignal),
            TestContext.Current.CancellationToken);
        session.Append(
            Node(3, isAnchor: true, new GraphId(5, 0, 2)),
            TestContext.Current.CancellationToken);
        session.Append(
            Node(4, isAnchor: true, new GraphId(5, 0, 3)),
            TestContext.Current.CancellationToken);
        session.Complete(TestContext.Current.CancellationToken);

        Assert.Equal(2, sink.Edges.Count);
        Assert.Equal((7L << 32) | 0L, sink.Edges[0].EdgeRecordId);
        Assert.Equal((7L << 32) | 1L, sink.Edges[1].EdgeRecordId);
        Assert.Equal(semantics.ForwardAccess, sink.Edges[0].ForwardAccess);
        Assert.Equal(semantics.ReverseAccess, sink.Edges[0].ReverseAccess);
        Assert.Equal(semantics.AttributeReference, sink.Edges[0].AttributeReference);
        Assert.Equal(semantics.Importance, sink.Edges[0].Importance);
        Assert.True(sink.Edges[0].HasNames);
        Assert.True(sink.Edges[0].Flags.HasFlag(EdgeSemanticFlags.Link));
        Assert.True(sink.Edges[0].Flags.HasFlag(EdgeSemanticFlags.HasTrafficControl));
        Assert.False(sink.Edges[1].Flags.HasFlag(EdgeSemanticFlags.HasTrafficControl));
    }

    private static PooledPathNode Node(
        long osmNodeId,
        bool isAnchor,
        GraphId graphId,
        NodeSemanticFlags flags = NodeSemanticFlags.None) =>
        new(
            new GenerationNodeRecord(
                osmNodeId,
                LatitudeE7: checked(360000000 + (int)osmNodeId),
                LongitudeE7: checked(-860000000 + (int)osmNodeId),
                flags,
                TagReference: 0),
            isAnchor,
            graphId);

    private sealed class RecordingEdgeSink : IFrontierEdgeSink
    {
        private long nextOffset;

        internal List<GenerationEdgeRecord> Edges { get; } = [];

        public IFrontierShapeWriter BeginShape(long wayId) => new ShapeWriter(this);

        public void PersistEdge(GenerationEdgeRecord edge) => Edges.Add(edge);

        private sealed class ShapeWriter(RecordingEdgeSink owner) : IFrontierShapeWriter
        {
            private int count;
            private bool complete;

            public void Append(in GenerationNodeRecord node)
            {
                ObjectDisposedException.ThrowIf(complete, this);
                count = checked(count + 1);
            }

            public EdgeShapeReference Complete()
            {
                ObjectDisposedException.ThrowIf(complete, this);
                complete = true;
                long offset = owner.nextOffset;
                int byteLength = checked(count * 24);
                owner.nextOffset = checked(owner.nextOffset + byteLength);
                return new EdgeShapeReference(offset, count, byteLength);
            }

            public void Dispose() => complete = true;
        }
    }
}
