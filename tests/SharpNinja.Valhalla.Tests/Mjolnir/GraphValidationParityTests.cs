using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class GraphValidationParityTests
{
    [Fact]
    public void RefreshTilesetFiles_AtomicallyWritesEachTileOnce()
    {
        string tileDirectory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-383-checksum-io-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tileDirectory);

        try
        {
            var firstId = new GraphId(1, 2, 0);
            var secondId = new GraphId(2, 2, 0);
            new GraphTileBuilder(firstId).StoreTileData(tileDirectory);
            new GraphTileBuilder(secondId).StoreTileData(tileDirectory);

            var writes = new Dictionary<string, int>(StringComparer.Ordinal);
            ushort buildId = GraphTileChecksum.RefreshTilesetFiles(
                tileDirectory,
                TestContext.Current.CancellationToken,
                path =>
                {
                    writes.TryGetValue(path, out int count);
                    writes[path] = count + 1;
                });

            string[] tilePaths =
                Directory.GetFiles(tileDirectory, "*.gph", SearchOption.AllDirectories);
            Assert.Equal(tilePaths.Length, writes.Count);
            Assert.All(writes.Values, static count => Assert.Equal(1, count));

            foreach (string tilePath in tilePaths)
            {
                byte[] tileBytes = File.ReadAllBytes(tilePath);
                GraphTileHeader header = GraphTileHeader.FromBytes(tileBytes);
                Assert.Equal(buildId, header.BuildId());
                Assert.Equal(
                    GraphTileChecksum.ComputeTileHash(
                        tileBytes.AsSpan(GraphTileHeader.HeaderSize)),
                    header.TileChecksum());
            }
        }
        finally
        {
            Directory.Delete(tileDirectory, recursive: true);
        }
    }

    [Fact]
    public void ValidatorHotPathAccessors_MatchDecodedValuesWithoutPerEdgeAllocations()
    {
        BuildSyntheticInput(out List<OSMWay> ways, out List<OSMWayNode> wayNodes);
        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
        Dictionary<GraphId, byte[]> generated =
            GraphBuilder.Build(new OSMData(), ways, wayNodes, graph);

        byte[] tileBytes = generated.Values.First(
            static bytes => GraphTileHeader.FromBytes(bytes).Directededgecount() > 0);
        GraphTile tile = GraphTile.Create(
            GraphTileHeader.FromBytes(tileBytes).Graphid(),
            tileBytes);
        DirectedEdge edge = tile.DirectedEdge(0);
        Admin admin = tile.Admin(0);

        Assert.Equal(tile.EdgeInfo(edge).WayId, tile.EdgeInfoWayId(edge));
        string countryIso = admin.CountryIsoCode();
        ushort expectedCountryIso = countryIso.Length == 0
            ? (ushort)0
            : (ushort)(countryIso[0] | (countryIso[1] << 8));
        Assert.Equal(expectedCountryIso, admin.CountryIsoCodeValue);

        _ = tile.EdgeInfoWayId(edge);
        _ = admin.CountryIsoCodeValue;
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong checksum = 0;
        for (int index = 0; index < 100_000; index++)
        {
            checksum ^= tile.EdgeInfoWayId(edge);
            checksum ^= admin.CountryIsoCodeValue;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.InRange(allocated, 0, 1_024);
    }

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
    public void MinimumBoundingCircle_SmallShapeHotPathLimitsAllocationToReturnedCenters()
    {
        PointLL[] shape =
        [
            new PointLL(-86.6800, 36.1200),
            new PointLL(-86.6750, 36.1250),
            new PointLL(-86.6700, 36.1200),
            new PointLL(-86.6750, 36.1150),
        ];
        (PointLL Center, double RadiusMeters)? expected =
            MinimumBoundingCircle.Compute(shape, 5_000);
        Assert.NotNull(expected);

        _ = MinimumBoundingCircle.Compute(shape, 5_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double checksum = 0;
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            (PointLL Center, double RadiusMeters)? result =
                MinimumBoundingCircle.Compute(shape, 5_000);
            checksum += result!.Value.Center.Lng + result.Value.RadiusMeters;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.InRange(allocated, 0, 400_000);
    }

    [Fact]
    public void ReusableBitmaskIntersection_MatchesReferenceAcrossShapeCorpus()
    {
        Tiles<PointLL, double> tiles = TileHierarchy.Levels()[^1].Tiles;
        IReadOnlyList<PointLL>[] shapes =
        [
            [new PointLL(-86.6774, 36.1263)],
            [new PointLL(-86.6774, 36.1263), new PointLL(-86.6710, 36.1310)],
            [new PointLL(-86.9000, 36.0500), new PointLL(-86.5000, 36.2500)],
            [
                new PointLL(-87.0500, 35.9500),
                new PointLL(-86.8500, 36.0500),
                new PointLL(-86.6500, 36.2500),
                new PointLL(-86.4500, 36.1500),
            ],
        ];
        var reusable = new Dictionary<int, uint>();

        foreach (IReadOnlyList<PointLL> shape in shapes)
        {
            Dictionary<int, HashSet<ushort>> reference = EdgeBinner.Intersect(tiles, shape);
            reusable[int.MaxValue] = uint.MaxValue;

            EdgeBinner.IntersectBitMasks(tiles, shape, reusable);

            Assert.Equal(reference.Keys.Order(), reusable.Keys.Order());
            foreach ((int tileId, HashSet<ushort> bins) in reference)
            {
                uint expectedMask = 0;
                foreach (ushort bin in bins)
                {
                    expectedMask |= 1u << bin;
                }

                Assert.Equal(expectedMask, reusable[tileId]);
            }
        }
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
            Assert.Equal(
                new[] { "tiles", "tweeners", "checksums" },
                stats.StageDurations.Keys);
            Assert.All(stats.StageDurations.Values, duration => Assert.True(duration >= TimeSpan.Zero));
            Assert.Equal(
                new[] { "deserialize", "edges", "binning", "update", "add-bins" },
                stats.TileStageDurations.Keys);
            Assert.All(
                stats.TileStageDurations.Values,
                duration => Assert.True(duration >= TimeSpan.Zero));

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

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(8, 4)]
    [InlineData(16, 4)]
    public void ParallelValidationDegree_UsesProfiledContentionCap(
        int requested,
        int expected)
    {
        Assert.Equal(
            expected,
            GraphValidator.ResolveParallelValidationDegree(requested));
    }

    [Fact]
    public void ParallelValidationWorkerCache_SharesConfiguredBudget()
    {
        var config = new GraphReader.Config
        {
            TileDir = "tiles",
            MaxCacheSize = 1_024,
            UseLruMemCache = true,
            LruMemCacheHardControl = true,
            MaxConcurrentReaderUsers = 8,
        };

        GraphReader.Config workerConfig =
            GraphValidator.CreateParallelWorkerConfig(
                config,
                maxDegreeOfParallelism: 4);

        Assert.Equal(config.TileDir, workerConfig.TileDir);
        Assert.Equal(256, workerConfig.MaxCacheSize);
        Assert.True(workerConfig.UseLruMemCache);
        Assert.True(workerConfig.LruMemCacheHardControl);
        Assert.Equal(config.MaxConcurrentReaderUsers, workerConfig.MaxConcurrentReaderUsers);
    }

    [Fact]
    public void ParallelValidation_MatchesSerialOutput()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-383-parallel-validation-" + Guid.NewGuid().ToString("N"));
        string serialDirectory = Path.Combine(rootDirectory, "serial");
        string parallelDirectory = Path.Combine(rootDirectory, "parallel");
        Directory.CreateDirectory(serialDirectory);
        Directory.CreateDirectory(parallelDirectory);

        try
        {
            OSMNode west = MakeNode(1, -87.1000, 36.1200, intersection: true);
            OSMNode middle = MakeNode(2, -86.6800, 36.1200, intersection: true);
            OSMNode east = MakeNode(3, -86.2000, 36.1200, intersection: true);
            OSMWay firstWay = MakeWay(100);
            firstWay.SetNodeCount(2);
            OSMWay secondWay = MakeWay(101);
            secondWay.SetNodeCount(2);
            var ways = new List<OSMWay> { firstWay, secondWay };
            var wayNodes = new List<OSMWayNode>
            {
                MakeWayNode(west, 0, 0),
                MakeWayNode(middle, 0, 1),
                MakeWayNode(middle, 1, 0),
                MakeWayNode(east, 1, 1),
            };
            GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
            Dictionary<GraphId, byte[]> generated =
                GraphBuilder.Build(new OSMData(), ways, wayNodes, graph);
            Assert.True(generated.Count > 1, "The fixture must span multiple graph tiles.");

            foreach ((GraphId graphId, byte[] tileBytes) in generated)
            {
                string relativePath = GraphTile.FileSuffix(graphId);
                string serialPath = Path.Combine(serialDirectory, relativePath);
                string parallelPath = Path.Combine(parallelDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(serialPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(parallelPath)!);
                File.WriteAllBytes(serialPath, tileBytes);
                File.WriteAllBytes(parallelPath, tileBytes);
            }

            GraphValidator.ValidatorStats serialStats =
                GraphValidator.Validate(
                    new GraphReader.Config { TileDir = serialDirectory },
                    TestContext.Current.CancellationToken);
            GraphValidator.ValidatorStats parallelStats =
                GraphValidator.Validate(
                    new GraphReader.Config { TileDir = parallelDirectory },
                    maxDegreeOfParallelism: 4,
                    TestContext.Current.CancellationToken);

            string[] serialFiles = Directory
                .GetFiles(serialDirectory, "*.gph", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(serialDirectory, path))
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] parallelFiles = Directory
                .GetFiles(parallelDirectory, "*.gph", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(parallelDirectory, path))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(serialFiles, parallelFiles);
            foreach (string relativePath in serialFiles)
            {
                Assert.Equal(
                    File.ReadAllBytes(Path.Combine(serialDirectory, relativePath)),
                    File.ReadAllBytes(Path.Combine(parallelDirectory, relativePath)));
            }

            Assert.Equal(serialStats.TileCount, parallelStats.TileCount);
            Assert.Equal(serialStats.Duplicates, parallelStats.Duplicates);
            for (var level = 0; level < serialStats.Densities.Length; level++)
            {
                Assert.Equal(
                    serialStats.Densities[level].Order(),
                    parallelStats.Densities[level].Order());
            }
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public void TweenOnlyTiles_PreserveDatasetIdentity()
    {
        const ulong datasetId = 123456789;
        string tileDirectory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-383-tween-dataset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tileDirectory);

        try
        {
            OSMNode west = MakeNode(1, -87.1000, 36.1200, intersection: true);
            OSMNode east = MakeNode(2, -86.2000, 36.1200, intersection: true);
            OSMWay way = MakeWay(100);
            way.SetNodeCount(2);
            var ways = new List<OSMWay> { way };
            var wayNodes = new List<OSMWayNode>
            {
                MakeWayNode(west, 0, 0),
                MakeWayNode(east, 0, 1),
            };
            var osmData = new OSMData { MaxChangesetId = datasetId };
            GraphBuilder.Graph graph = GraphBuilder.BuildEdges(ways, wayNodes);
            Dictionary<GraphId, byte[]> generated =
                GraphBuilder.Build(osmData, ways, wayNodes, graph);

            foreach ((GraphId graphId, byte[] tileBytes) in generated)
            {
                string path = Path.Combine(tileDirectory, GraphTile.FileSuffix(graphId));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, tileBytes);
            }

            GraphValidator.Validate(
                new GraphReader.Config { TileDir = tileDirectory },
                TestContext.Current.CancellationToken);

            string[] validatedTilePaths =
                Directory.GetFiles(tileDirectory, "*.gph", SearchOption.AllDirectories);
            Assert.True(
                validatedTilePaths.Length > generated.Count,
                "The fixture must create at least one tween-only tile.");
            Assert.All(
                validatedTilePaths,
                tilePath => Assert.Equal(
                    datasetId,
                    GraphTileHeader.FromBytes(File.ReadAllBytes(tilePath)).DatasetId()));
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
