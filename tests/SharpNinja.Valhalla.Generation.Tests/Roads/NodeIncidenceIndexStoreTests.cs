using System.Runtime.CompilerServices;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class NodeIncidenceIndexStoreTests
{
    [Fact]
    public async Task BuildAsync_SortsSpillsAndPersistsBoundedSummaries()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-incidence-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            int recordSize = Unsafe.SizeOf<NodeIncidenceRecord>();
            using var input = new IntermediateSequenceStore<NodeIncidenceRecord>(
                new IntermediateSequenceStoreOptions(
                    root,
                    "input",
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: recordSize * 2L,
                    ScratchDiskBudgetBytes: 4 * 1024 * 1024,
                    SegmentSizeBytes: recordSize * 4));

            for (int node = 9; node >= 0; node--)
            {
                for (int way = 9; way >= 0; way--)
                {
                    input.Append(new NodeIncidenceRecord(
                        OsmNodeId: node + 1,
                        OwnerId: way + 100,
                        OwnerOrdinal: way,
                        NodeOrdinal: node,
                        Roles: way == 0
                            ? NodeIncidenceRole.WayStart
                            : NodeIncidenceRole.WayIntermediate,
                        CanonicalOrdinal: (node * 10L) + way));
                }
            }

            await input.CompleteAsync(TestContext.Current.CancellationToken);
            using NodeIncidenceIndex index = await NodeIncidenceIndex.BuildAsync(
                input,
                new NodeIncidenceIndexOptions(
                    root,
                    IntermediateStorageMode.Auto,
                    MemoryBudgetBytes: 1024,
                    ScratchDiskBudgetBytes: 8 * 1024 * 1024,
                    SegmentSizeBytes: recordSize * 4),
                TestContext.Current.CancellationToken);

            Assert.Equal(100, index.IncidenceCount);
            Assert.Equal(10, index.SummaryCount);
            Assert.Equal(
                IntermediateStorageMode.MemoryMapped,
                input.State.ActiveStorageMode);
            for (long ordinal = 1; ordinal < index.IncidenceCount; ordinal++)
            {
                NodeIncidenceRecord previous = index.ReadIncidence(ordinal - 1);
                NodeIncidenceRecord current = index.ReadIncidence(ordinal);
                Assert.True(
                    NodeIncidenceIndexBuilder.Compare(previous, current) <= 0);
            }

            for (long ordinal = 0; ordinal < index.SummaryCount; ordinal++)
            {
                NodeIncidenceSummary summary = index.ReadSummary(ordinal);
                Assert.Equal(ordinal + 1, summary.OsmNodeId);
                Assert.Equal(10, summary.IncidenceCount);
                Assert.Equal(10, summary.DistinctWayCount);
                Assert.Equal(10, summary.InitialPendingReferenceCount);
                Assert.True(summary.AnchorFlags.HasFlag(NodeAnchorFlags.SharedWay));
            }

            Assert.True(index.SortReceipt.InitialRunCount > 1);
            Assert.Equal(100, index.IncidenceManifest.RecordCount);
            Assert.Equal(10, index.SummaryManifest.RecordCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
