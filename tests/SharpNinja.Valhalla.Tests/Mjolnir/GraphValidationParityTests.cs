using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class GraphValidationParityTests
{
    [Fact]
    public void DiscretizedBoundingCircle_MatchesOfficialPackingAndCoverage()
    {
        var binCenter = new PointLL(-86.6750, 36.1225);
        var circle = new DiscretizedBoundingCircle(binCenter, binCenter, 10);

        Assert.True(circle.IsValid);
        Assert.Equal(4096u, circle.XOffset);
        Assert.Equal(4096u, circle.YOffset);
        Assert.Equal(10u, circle.RadiusIndex);

        (PointLL center, double radiusMeters) = circle.Get(binCenter);
        Assert.Equal(13, radiusMeters);
        Assert.True(center.Distance(binCenter) + 10 <= radiusMeters);
        Assert.Equal(circle, DiscretizedBoundingCircle.FromRaw(circle.RawValue));
    }

    [Fact]
    public void ValidatedGraphStructure_MatchesOfficial()
    {
        string tileDirectory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-383-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tileDirectory);

        try
        {
            BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);
            GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
            Dictionary<GraphId, byte[]> generated =
                GraphBuilder.Build(new OSMData(), ways, wayNodes, graph);

            foreach ((GraphId graphId, byte[] tileBytes) in generated)
            {
                string path = Path.Combine(tileDirectory, GraphTile.FileSuffix(graphId));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, tileBytes);
            }

            GraphValidator.ValidatorStats stats =
                GraphValidator.Validate(
                    new GraphReader.Config { TileDir = tileDirectory },
                    TestContext.Current.CancellationToken);
            Assert.NotEqual(0, stats.TileCount);

            string[] tilePaths =
                Directory.GetFiles(tileDirectory, "*.gph", SearchOption.AllDirectories);
            Assert.NotEmpty(tilePaths);

            var tileHashes = new List<ulong>(tilePaths.Length);
            var buildIds = new HashSet<ushort>();
            foreach (string tilePath in tilePaths)
            {
                byte[] tileBytes = File.ReadAllBytes(tilePath);
                GraphTileHeader header = GraphTileHeader.FromBytes(tileBytes);
                ulong expectedHash = GraphTileChecksum.ComputeTileHash(
                    tileBytes.AsSpan(GraphTileHeader.HeaderSize));

                Assert.Equal(expectedHash, header.TileChecksum());
                Assert.True(header.HasBoundingCircles());
                tileHashes.Add(header.TileChecksum());
                buildIds.Add(header.BuildId());

                GraphTile tile = GraphTile.Create(header.Graphid(), tileBytes);
                for (int bin = 0; bin < GraphTileHeader.BinCount; bin++)
                {
                    GraphId[] ids = tile.GetBin(
                        bin % GraphTileHeader.BinsDim,
                        bin / GraphTileHeader.BinsDim).ToArray();
                    DiscretizedBoundingCircle[] circles = tile.GetBoundingCircles(
                        bin % GraphTileHeader.BinsDim,
                        bin / GraphTileHeader.BinsDim).ToArray();
                    Assert.Equal(ids.Length, circles.Length);
                    Assert.All(circles, static circle => Assert.True(circle.IsValid));

                    Tiles<PointLL, double> localTiles = TileHierarchy.Levels()[^1].Tiles;
                    Aabb2T<double> tileBounds =
                        localTiles.TileBounds((int)header.Graphid().Tileid());
                    double subdivisionSize = localTiles.SubdivisionSize();
                    var binCenter = new PointLL(
                        tileBounds.Minx +
                        ((bin % GraphTileHeader.BinsDim) * subdivisionSize) +
                        (subdivisionSize * 0.5),
                        tileBounds.Miny +
                        ((bin / GraphTileHeader.BinsDim) * subdivisionSize) +
                        (subdivisionSize * 0.5));
                    for (int entryIndex = 0; entryIndex < ids.Length; entryIndex++)
                    {
                        DirectedEdge indexedEdge =
                            tile.DirectedEdge((int)ids[entryIndex].Id());
                        IReadOnlyList<PointLL> indexedShape =
                            tile.EdgeInfo(indexedEdge).Shape();
                        (PointLL circleCenter, double radiusMeters) =
                            circles[entryIndex].Get(binCenter);
                        Assert.All(
                            indexedShape,
                            point => Assert.True(
                                point.Distance(circleCenter) <= radiusMeters,
                                $"Bin {bin} circle does not cover {point}."));
                    }

                    GraphId[] sorted = ids
                        .OrderByDescending(id => id.Level())
                        .ThenBy(id => id.Tileid())
                        .ThenBy(id => id.Id())
                        .ToArray();
                    Assert.Equal(sorted, ids);
                }

                for (uint edgeIndex = 0; edgeIndex < header.Directededgecount(); edgeIndex++)
                {
                    DirectedEdge edge = tile.DirectedEdge((int)edgeIndex);
                    NodeInfo endNode = tile.Node(edge.EndNode);
                    Assert.True(edge.OppLocalIdx < endNode.EdgeCount);
                }
            }

            Assert.Single(buildIds);
            Assert.Equal(GraphTileChecksum.ComputeTilesetBuildId(tileHashes), buildIds.Single());
        }
        finally
        {
            Directory.Delete(tileDirectory, recursive: true);
        }
    }

    private static void BuildSyntheticInput(
        out List<OSMWay> ways,
        out List<OSMWayNode> wayNodes)
    {
        ways = [];
        wayNodes = [];

        OSMNode a = MakeNode(1, -86.6800, 36.1200, intersection: true);
        OSMNode b = MakeNode(2, -86.6750, 36.1225, intersection: true);
        OSMNode c = MakeNode(3, -86.6700, 36.1200, intersection: true);

        OSMWay way = MakeWay(100);
        way.SetNodeCount(3);
        ways.Add(way);
        wayNodes.Add(MakeWayNode(a, 0, 0));
        wayNodes.Add(MakeWayNode(b, 0, 1));
        wayNodes.Add(MakeWayNode(c, 0, 2));
    }

    private static OSMNode MakeNode(ulong id, double longitude, double latitude, bool intersection)
    {
        var node = new OSMNode(id, latitude, longitude);
        node.SetIntersection(intersection);
        return node;
    }

    private static OSMWay MakeWay(ulong id)
    {
        var way = new OSMWay(id);
        way.SetRoadClass(RoadClass.Residential);
        way.SetUse(Use.Road);
        way.SetSpeed(50);
        way.SetAutoForward(true);
        way.SetAutoBackward(true);
        way.SetDriveOnRight(true);
        return way;
    }

    private static OSMWayNode MakeWayNode(OSMNode node, uint wayIndex, uint shapeIndex)
    {
        return new OSMWayNode
        {
            Node = node,
            WayIndex = wayIndex,
            WayShapeNodeIndex = shapeIndex,
        };
    }
}
