using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class CompactNodeLookupIndexTests
{
    [Fact]
    public async Task BuildAsync_DeduplicatesIdenticalNodesAndSupportsBoundedLookup()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore store =
                await CompactOsmSemanticStore.BuildAsync(
                    new OverlappingExtractSource(conflict: false),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using CompactNodeLookupIndex index =
                await CompactNodeLookupIndex.BuildAsync(
                    store,
                    LookupOptions(Path.Combine(root, "lookup")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(3, index.UniqueNodeCount);
            Assert.Equal(1, index.DuplicateNodeCount);
            Assert.True(index.TryGetNode(1, out GenerationNodeRecord first));
            Assert.Equal(361000000, first.LatitudeE7);
            Assert.True(index.TryGetNode(2, out GenerationNodeRecord shared));
            Assert.Equal(361100000, shared.LatitudeE7);
            Assert.True(index.TryGetNode(3, out GenerationNodeRecord last));
            Assert.Equal(361200000, last.LatitudeE7);
            Assert.False(index.TryGetNode(4, out _));
            Assert.True(index.PeakMemoryBytes <= LookupOptions(root).MemoryBudgetBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ConflictingDuplicateCoordinatesFailClosed()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore store =
                await CompactOsmSemanticStore.BuildAsync(
                    new OverlappingExtractSource(conflict: true),
                    SemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await CompactNodeLookupIndex.BuildAsync(
                    store,
                    LookupOptions(Path.Combine(root, "lookup")),
                    TestContext.Current.CancellationToken));

            Assert.Contains("OSM node 2", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CompactOsmSemanticStoreOptions SemanticOptions(string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 16 * 1024 * 1024,
            ScratchDiskBudgetBytes: 64 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static CompactNodeLookupIndexOptions LookupOptions(string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 2 * 1024 * 1024,
            ScratchDiskBudgetBytes: 16 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Generation.Tests",
            nameof(CompactNodeLookupIndexTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class OverlappingExtractSource(bool conflict) : IOsmPbfEntitySource
    {
        public int FileCount => 2;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pass == OsmPbfEntityPass.Ways)
            {
                visitor.Way(
                    checked((ulong)(10 + fileOrdinal)),
                    fileOrdinal == 0 ? [1UL, 2UL] : [2UL, 3UL],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["highway"] = "residential",
                    });
                return;
            }

            if (pass == OsmPbfEntityPass.Relations)
            {
                return;
            }

            if (fileOrdinal == 0)
            {
                visitor.Node(1, 36.10, -86.70, EmptyTags());
                visitor.Node(2, 36.11, -86.71, EmptyTags());
                return;
            }

            visitor.Node(2, conflict ? 36.115 : 36.11, -86.71, EmptyTags());
            visitor.Node(3, 36.12, -86.72, EmptyTags());
        }

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
