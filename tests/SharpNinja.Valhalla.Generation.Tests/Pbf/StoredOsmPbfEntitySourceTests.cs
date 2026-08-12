using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Pbf;

public sealed class StoredOsmPbfEntitySourceTests
{
    [Theory]
    [InlineData(IntermediateStorageMode.Memory)]
    [InlineData(IntermediateStorageMode.MemoryMapped)]
    [InlineData(IntermediateStorageMode.Auto)]
    public async Task OnePassSource_ReplaysLegacySemanticModel(
        IntermediateStorageMode storageMode)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-dotnet-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pbfPath = Path.Combine(root, "fixture.osm.pbf");
        await File.WriteAllBytesAsync(
            pbfPath,
            TestOsmPbfFixtureBuilder.Create(OsmPbfCompressionKind.Zlib),
            TestContext.Current.CancellationToken);

        try
        {
            using var source = await StoredOsmPbfEntitySource.CreateAsync(
                [pbfPath],
                Path.Combine(root, "intermediate"),
                storageMode,
                memoryBudgetBytes: 4 * 1024 * 1024,
                scratchDiskBudgetBytes: 32 * 1024 * 1024,
                TestContext.Current.CancellationToken);

            var optimized = new PbfGraphParser();
            OSMData optimizedData = optimized.Parse(
                source,
                TestContext.Current.CancellationToken);
            var legacy = new PbfGraphParser();
            OSMData legacyData = legacy.Parse([pbfPath]);

            Assert.Equal(legacy.Ways.Count, optimized.Ways.Count);
            Assert.Equal(legacy.WayNodes.Count, optimized.WayNodes.Count);
            Assert.Equal(legacy.Access.Count, optimized.Access.Count);
            Assert.Equal(
                legacy.ComplexRestrictionsFrom.Count,
                optimized.ComplexRestrictionsFrom.Count);
            Assert.Equal(legacyData.Initialized, optimizedData.Initialized);
            Assert.Equal(
                source.ReadResult.Metrics.DataBlockCount,
                source.ReadResult.Metrics.DecompressionCount);
            Assert.Equal(1, source.ReadResult.Metrics.DataBlockCount);
            Assert.Equal(3, source.CompletedReplayPassCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Replay_ReusesCallbackScopedDataAndInternedStrings()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-dotnet-source-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pbfPath = Path.Combine(root, "fixture.osm.pbf");
        await File.WriteAllBytesAsync(
            pbfPath,
            TestOsmPbfFixtureBuilder.Create(
                OsmPbfCompressionKind.Raw,
                dataBlockCount: 2),
            TestContext.Current.CancellationToken);

        try
        {
            using var source = await StoredOsmPbfEntitySource.CreateAsync(
                [pbfPath],
                Path.Combine(root, "intermediate"),
                IntermediateStorageMode.Memory,
                memoryBudgetBytes: 8 * 1024 * 1024,
                scratchDiskBudgetBytes: 32 * 1024 * 1024,
                TestContext.Current.CancellationToken);
            var visitor = new TagIdentityVisitor();

            source.VisitFile(
                0,
                OsmPbfEntityPass.Ways,
                visitor,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, visitor.WayCount);
            Assert.Equal(2, visitor.SpanWayCount);
            Assert.True(visitor.ReusedTransientTags);
            Assert.True(visitor.ReusedInternedStrings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Replay_HonorsCancellationDuringPass()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-dotnet-source-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pbfPath = Path.Combine(root, "fixture.osm.pbf");
        await File.WriteAllBytesAsync(
            pbfPath,
            TestOsmPbfFixtureBuilder.Create(
                OsmPbfCompressionKind.Raw,
                dataBlockCount: 16),
            TestContext.Current.CancellationToken);

        try
        {
            using var source = await StoredOsmPbfEntitySource.CreateAsync(
                [pbfPath],
                Path.Combine(root, "intermediate"),
                IntermediateStorageMode.Memory,
                memoryBudgetBytes: 8 * 1024 * 1024,
                scratchDiskBudgetBytes: 32 * 1024 * 1024,
                TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => source.VisitFile(
                    0,
                    OsmPbfEntityPass.Ways,
                    new NoOpVisitor(),
                    cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TagIdentityVisitor : IOsmPbfSpanVisitor
    {
        private IReadOnlyDictionary<string, string>? firstTags;
        private string? firstKey;
        private string? firstValue;

        public int WayCount { get; private set; }

        public int SpanWayCount { get; private set; }

        public bool ReusedTransientTags { get; private set; } = true;

        public bool ReusedInternedStrings { get; private set; } = true;

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
            IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Way(
            ulong id,
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags) =>
            ObserveWay(nodeRefs, tags);

        public void Way(
            ulong id,
            ReadOnlySpan<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags)
        {
            SpanWayCount++;
            ObserveWay(nodeRefs.ToArray(), tags);
        }

        private void ObserveWay(
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags)
        {
            Assert.Equal(2, nodeRefs.Count);
            Assert.Equal(1UL, nodeRefs[0]);
            Assert.Equal(2UL, nodeRefs[1]);
            Assert.IsType<OsmPbfTransientTagDictionary>(tags);
            KeyValuePair<string, string> tag = Assert.Single(tags);
            if (firstTags is null)
            {
                firstTags = tags;
                firstKey = tag.Key;
                firstValue = tag.Value;
            }
            else
            {
                ReusedTransientTags &= ReferenceEquals(firstTags, tags);
                ReusedInternedStrings &=
                    ReferenceEquals(firstKey, tag.Key) &&
                    ReferenceEquals(firstValue, tag.Value);
            }

            Assert.Equal("highway", tag.Key);
            Assert.Equal("residential", tag.Value);
            WayCount++;
        }

        public void Relation(
            ulong id,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> tags)
        {
        }
    }

    private sealed class NoOpVisitor : IOsmPbfVisitor
    {
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
            IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Way(
            ulong id,
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Relation(
            ulong id,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> tags)
        {
        }
    }
}
