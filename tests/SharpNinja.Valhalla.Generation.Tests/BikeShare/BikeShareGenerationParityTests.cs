using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.BikeShare;
using SharpNinja.Valhalla.Midgard;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.BikeShare;

public sealed class BikeShareGenerationParityTests
{
    [Fact]
    public async Task ManagedBikeShareGraph_MatchesOfficialFixture()
    {
        string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BikeShare");
        string pbfPath = Path.Combine(fixtureRoot, "ParisBss", "paris_bss.osm.pbf");
        string baseDirectory = Path.Combine(fixtureRoot, "OfficialValhalla383ParisBase");
        string officialDirectory = Path.Combine(fixtureRoot, "OfficialValhalla383ParisBss");
        string scratch = ManagedBikeShareGenerationTests.NewScratch();
        string input = Path.Combine(scratch, "input");
        string output = Path.Combine(scratch, "output");

        try
        {
            ManagedBikeShareGenerationTests.CopyDirectory(baseDirectory, input);
            IBikeShareTileBuilder builder = new ManagedBikeShareTileBuilder();
            BikeShareTileBuildResult result = await builder.BuildAsync(
                ManagedBikeShareGenerationTests.Request(
                    input,
                    pbfPath,
                    Path.Combine(scratch, "work"),
                    output,
                    maxDegreeOfParallelism: 4),
                TestContext.Current.CancellationToken);

            GraphId graphId = new(799929, 2, 0);
            GraphTile official = GraphTile.Create(
                graphId,
                await File.ReadAllBytesAsync(
                    Path.Combine(officialDirectory, GraphTile.FileSuffix(graphId)),
                    TestContext.Current.CancellationToken));
            GraphTile managed = GraphTile.Create(
                graphId,
                await File.ReadAllBytesAsync(
                    Path.Combine(output, GraphTile.FileSuffix(graphId)),
                    TestContext.Current.CancellationToken));

            Assert.Equal(46, result.StationCount);
            Assert.Equal(46, result.AddedNodeCount);
            Assert.Equal(368, result.AddedDirectedEdgeCount);
            BikeShareSemanticSnapshot expected = Snapshot(official);
            BikeShareSemanticSnapshot actual = Snapshot(managed);
            Assert.Equal(expected.Nodes, actual.Nodes);
            Assert.True(EveryBikeShareEdgeHasOpposingPair(managed));
            uint[] expectedLengths = GetBikeShareEdgeLengths(official);
            uint[] actualLengths = GetBikeShareEdgeLengths(managed);
            Assert.Equal(expectedLengths.Length, actualLengths.Length);
            Assert.All(
                expectedLengths.Zip(actualLengths),
                pair => Assert.InRange(
                    Math.Abs((long)pair.First - pair.Second),
                    0,
                    1));
            string[] missingEdges = expected.Edges.Except(actual.Edges, StringComparer.Ordinal).Take(8).ToArray();
            string[] unexpectedEdges = actual.Edges.Except(expected.Edges, StringComparer.Ordinal).Take(8).ToArray();
            Assert.True(
                missingEdges.Length == 0 && unexpectedEdges.Length == 0,
                "Missing official edges:" + Environment.NewLine
                + string.Join(Environment.NewLine, missingEdges)
                + Environment.NewLine + "Unexpected managed edges:" + Environment.NewLine
                + string.Join(Environment.NewLine, unexpectedEdges));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    internal static BikeShareSemanticSnapshot Snapshot(GraphTile tile)
    {
        GraphTileHeader header = tile.Header();
        var nodes = new List<string>();
        var edges = new List<string>();

        for (int nodeIndex = 0; nodeIndex < header.Nodecount(); nodeIndex++)
        {
            NodeInfo node = tile.Node(nodeIndex);
            PointLL origin = node.LatLng(tile.BaseLl());
            if (node.Type == NodeType.BikeShare)
            {
                nodes.Add(FormattableString.Invariant(
                    $"{origin.Lat:F7}|{origin.Lng:F7}|{node.Access}|{node.EdgeCount}|{node.ModeChange}"));
            }

            for (uint localIndex = 0; localIndex < node.EdgeCount; localIndex++)
            {
                DirectedEdge edge = tile.DirectedEdge((int)(node.EdgeIndex + localIndex));
                if (!edge.BssConnection)
                {
                    continue;
                }

                if (edge.EndNode.Tileid() != tile.Id().Tileid())
                {
                    throw new InvalidOperationException("Fixture BSS edge unexpectedly crosses a tile.");
                }

                NodeInfo endNode = tile.Node((int)edge.EndNode.Id());
                PointLL end = endNode.LatLng(tile.BaseLl());
                EdgeInfo info = tile.EdgeInfo(edge);
                string tags = string.Join(
                    ",",
                    info.GetTags()
                        .OrderBy(pair => pair.Key)
                        .Select(pair => $"{pair.Key}:{Convert.ToHexString(pair.Value)}"));
                string names = string.Join("|", info.GetNames());
                string shape = string.Join(
                    ";",
                    info.Shape().Select(point => FormattableString.Invariant($"{point.Lat:F5},{point.Lng:F5}")));
                edges.Add(FormattableString.Invariant(
                    $"{origin.Lat:F7},{origin.Lng:F7}->{end.Lat:F7},{end.Lng:F7}|{edge.Use}|{edge.Speed}|{edge.Surface}|{edge.CycleLane}|{edge.Classification}|{edge.ForwardAccess}|{edge.ReverseAccess}|{edge.Forward}|{info.WayId}|{names}|{tags}|{shape}"));
            }
        }

        return new BikeShareSemanticSnapshot(
            nodes.Order(StringComparer.Ordinal).ToArray(),
            edges.Order(StringComparer.Ordinal).ToArray());
    }

    private static uint[] GetBikeShareEdgeLengths(GraphTile tile)
    {
        var lengths = new List<uint>();
        for (int index = 0; index < tile.Header().Directededgecount(); index++)
        {
            DirectedEdge edge = tile.DirectedEdge(index);
            if (edge.BssConnection)
            {
                lengths.Add(edge.Length);
            }
        }

        return lengths.Order().ToArray();
    }

    private static bool EveryBikeShareEdgeHasOpposingPair(GraphTile tile)
    {
        for (int nodeIndex = 0; nodeIndex < tile.Header().Nodecount(); nodeIndex++)
        {
            NodeInfo node = tile.Node(nodeIndex);
            for (uint localIndex = 0; localIndex < node.EdgeCount; localIndex++)
            {
                DirectedEdge edge = tile.DirectedEdge(
                    checked((int)(node.EdgeIndex + localIndex)));
                if (!edge.BssConnection)
                {
                    continue;
                }

                if (edge.EndNode.Tileid() != tile.Id().Tileid())
                {
                    return false;
                }

                NodeInfo endNode = tile.Node(checked((int)edge.EndNode.Id()));
                if (edge.LocalEdgeIdx >= endNode.EdgeCount
                    || !IsOpposingBikeShareEdge(
                        tile,
                        nodeIndex,
                        endNode,
                        edge.LocalEdgeIdx))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsOpposingBikeShareEdge(
        GraphTile tile,
        int originNodeIndex,
        NodeInfo endNode,
        uint opposingLocalIndex)
    {
        DirectedEdge opposing = tile.DirectedEdge(
            checked((int)(endNode.EdgeIndex + opposingLocalIndex)));
        return opposing.BssConnection
               && opposing.EndNode.Tileid() == tile.Id().Tileid()
               && opposing.EndNode.Id() == checked((uint)originNodeIndex);
    }

    internal sealed record BikeShareSemanticSnapshot(
        IReadOnlyList<string> Nodes,
        IReadOnlyList<string> Edges);
}
