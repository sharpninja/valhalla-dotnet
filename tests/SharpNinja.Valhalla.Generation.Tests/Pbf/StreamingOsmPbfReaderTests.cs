using SharpNinja.Valhalla.Generation.Pbf;
using Xunit;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Tests.Pbf;

public sealed class StreamingOsmPbfReaderTests
{
    [Fact]
    public async Task RawZlibAndLz4Blocks_ProduceEquivalentEntities()
    {
        var results = new List<(InMemoryOsmEntityStore Store, StreamingOsmPbfReadResult Result)>();
        foreach (var compression in Enum.GetValues<OsmPbfCompressionKind>())
        {
            results.Add(await ReadAsync(TestOsmPbfFixtureBuilder.Create(compression)));
        }

        var expected = results[0].Store;
        foreach (var (actual, result) in results)
        {
            Assert.Equivalent(expected.Nodes, actual.Nodes, strict: true);
            Assert.Equivalent(expected.Ways, actual.Ways, strict: true);
            Assert.Equivalent(expected.Relations, actual.Relations, strict: true);
            Assert.Single(result.Metrics.BlockReceipts);
        }

        Assert.Equal(OsmPbfCompressionKind.Raw, results[0].Result.Metrics.BlockReceipts[0].Compression);
        Assert.Equal(OsmPbfCompressionKind.Zlib, results[1].Result.Metrics.BlockReceipts[0].Compression);
        Assert.Equal(OsmPbfCompressionKind.Lz4, results[2].Result.Metrics.BlockReceipts[0].Compression);
    }

    [Fact]
    public async Task FullBuild_DecodesEachDataBlockExactlyOnce()
    {
        var (_, result) = await ReadAsync(
            TestOsmPbfFixtureBuilder.Create(OsmPbfCompressionKind.Zlib, dataBlockCount: 3));

        Assert.Equal(3, result.Metrics.DataBlockCount);
        Assert.Equal(3, result.Metrics.DecompressionCount);
        Assert.All(result.Metrics.BlockReceipts, receipt => Assert.Equal(1, receipt.DecompressionCount));
    }

    [Fact]
    public async Task NodeCoordinates_MatchLibOsmiumDivisionSemantics()
    {
        const long latitude = 437_315_839;
        const long longitude = 74_123_195;
        var (store, _) = await ReadAsync(
            TestOsmPbfFixtureBuilder.Create(
                OsmPbfCompressionKind.Raw,
                latitude: latitude,
                longitude: longitude));

        OsmNodeEntity node = Assert.Single(store.Nodes);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(latitude / 10_000_000d),
            BitConverter.DoubleToInt64Bits(node.Latitude));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(longitude / 10_000_000d),
            BitConverter.DoubleToInt64Bits(node.Longitude));
    }

    private static async Task<(InMemoryOsmEntityStore Store, StreamingOsmPbfReadResult Result)> ReadAsync(
        byte[] pbf)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, pbf, TestContext.Current.CancellationToken);
            var store = new InMemoryOsmEntityStore();
            var result = await new StreamingOsmPbfReader().ReadAsync(path, store, TestContext.Current.CancellationToken);
            return (store, result);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class StreamingOsmPbfReaderAllocationTests
{
    [Fact]
    public async Task IrrelevantEntities_DoNotMaterializeCollections()
    {
        var path = await WriteFixtureAsync();
        try
        {
            var store = new InMemoryOsmEntityStore(kind => kind == OsmEntityKind.Way);
            var result = await new StreamingOsmPbfReader().ReadAsync(path, store, TestContext.Current.CancellationToken);

            Assert.Empty(store.Nodes);
            Assert.Single(store.Ways);
            Assert.Empty(store.Relations);
            Assert.Equal(1, result.Metrics.SkippedNodeCount);
            Assert.Equal(1, result.Metrics.SkippedRelationCount);
            Assert.Equal(1, result.Metrics.MaterializedTagCount);
            Assert.Equal(0, result.Metrics.MaterializedTagDictionaryCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnretainedTags_DoNotAllocateDictionaries()
    {
        var path = await WriteFixtureAsync();
        try
        {
            var sink = new NonMaterializingSink();
            var result = await new StreamingOsmPbfReader().ReadAsync(path, sink, TestContext.Current.CancellationToken);

            Assert.Equal(1, sink.NodeCount);
            Assert.Equal(1, sink.WayCount);
            Assert.Equal(1, sink.RelationCount);
            Assert.Equal(0, result.Metrics.MaterializedTagCount);
            Assert.Equal(0, result.Metrics.MaterializedTagDictionaryCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteFixtureAsync()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(
            path,
            TestOsmPbfFixtureBuilder.Create(OsmPbfCompressionKind.Raw),
            TestContext.Current.CancellationToken);
        return path;
    }

    private sealed class NonMaterializingSink : IStreamingOsmEntitySink
    {
        public int NodeCount { get; private set; }

        public int WayCount { get; private set; }

        public int RelationCount { get; private set; }

        public bool ShouldRetain(OsmEntityKind kind) => true;

        public void AddNode(scoped in OsmNodeView node) => NodeCount++;

        public void AddWay(scoped in OsmWayView way) => WayCount++;

        public void AddRelation(scoped in OsmRelationView relation) => RelationCount++;
    }
}

public sealed class StreamingOsmPbfReaderHostileTests
{
    [Fact]
    public async Task MalformedInputMatrix_FailsSafely()
    {
        var cases = new[]
        {
            (
                Name: "truncated header length",
                Bytes: new byte[] { 0x00, 0x00, 0x00 },
                Code: StreamingOsmPbfFailureCode.TruncatedInput),
            (
                Name: "oversized header",
                Bytes: TestOsmPbfFixtureBuilder.CreateOversizedHeaderPrefix(65 * 1024),
                Code: StreamingOsmPbfFailureCode.OversizedBlobHeader),
            (
                Name: "unsupported compression",
                Bytes: TestOsmPbfFixtureBuilder.CreateUnsupportedCompression(),
                Code: StreamingOsmPbfFailureCode.UnsupportedCompression),
        };

        foreach (var hostileCase in cases)
        {
            var path = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(path, hostileCase.Bytes, TestContext.Current.CancellationToken);
                var exception = await Assert.ThrowsAsync<StreamingOsmPbfException>(
                    () => new StreamingOsmPbfReader()
                        .ReadAsync(
                            path,
                            new InMemoryOsmEntityStore(),
                            TestContext.Current.CancellationToken)
                        .AsTask());

                Assert.Equal(hostileCase.Code, exception.FailureCode);
                Assert.DoesNotContain(path, exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

public sealed class PbfParserDifferentialTests
{
    [Fact]
    public async Task OptimizedParser_MatchesLegacySemanticModel()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                path,
                TestOsmPbfFixtureBuilder.Create(OsmPbfCompressionKind.Raw),
                TestContext.Current.CancellationToken);

            var managedStore = new InMemoryOsmEntityStore();
            await new StreamingOsmPbfReader().ReadAsync(
                path,
                managedStore,
                TestContext.Current.CancellationToken);

            var legacy = new LegacyCaptureVisitor();
            new OsmPbfReader(legacy).Parse(path);

            var managedNode = Assert.Single(managedStore.Nodes);
            var legacyNode = Assert.Single(legacy.Nodes);
            Assert.Equal(legacyNode.Id, managedNode.Id);
            Assert.Equal(legacyNode.Latitude, managedNode.Latitude, precision: 9);
            Assert.Equal(legacyNode.Longitude, managedNode.Longitude, precision: 9);
            Assert.Equal(legacyNode.Tags["highway"], Assert.Single(managedNode.Tags).Value);

            var managedWay = Assert.Single(managedStore.Ways);
            var legacyWay = Assert.Single(legacy.Ways);
            Assert.Equal(legacyWay.Id, managedWay.Id);
            Assert.Equal(legacyWay.NodeReferences, managedWay.NodeReferences);
            Assert.Equal(legacyWay.Tags["highway"], Assert.Single(managedWay.Tags).Value);

            var managedRelation = Assert.Single(managedStore.Relations);
            var legacyRelation = Assert.Single(legacy.Relations);
            Assert.Equal(legacyRelation.Id, managedRelation.Id);
            Assert.Equal(legacyRelation.Members[0].Id, managedRelation.Members[0].Id);
            Assert.Equal(legacyRelation.Members[0].Type, managedRelation.Members[0].Type);
            Assert.Equal(legacyRelation.Members[0].Role, managedRelation.Members[0].Role);
            Assert.Equal(legacyRelation.Tags["type"], Assert.Single(managedRelation.Tags).Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class LegacyCaptureVisitor : IOsmPbfVisitor
    {
        public List<LegacyNode> Nodes { get; } = [];

        public List<LegacyWay> Ways { get; } = [];

        public List<LegacyRelation> Relations { get; } = [];

        public void Header(
            double? minLat,
            double? minLon,
            double? maxLat,
            double? maxLon,
            IReadOnlyList<string> requiredFeatures)
        {
        }

        public void Node(
            ulong id,
            double lat,
            double lon,
            IReadOnlyDictionary<string, string> tags) =>
            Nodes.Add(new LegacyNode(id, lat, lon, tags));

        public void Way(
            ulong id,
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags) =>
            Ways.Add(new LegacyWay(id, nodeRefs, tags));

        public void Relation(
            ulong id,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> tags) =>
            Relations.Add(new LegacyRelation(id, members, tags));
    }

    private sealed record LegacyNode(
        ulong Id,
        double Latitude,
        double Longitude,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record LegacyWay(
        ulong Id,
        IReadOnlyList<ulong> NodeReferences,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record LegacyRelation(
        ulong Id,
        IReadOnlyList<OsmRelationMember> Members,
        IReadOnlyDictionary<string, string> Tags);
}
