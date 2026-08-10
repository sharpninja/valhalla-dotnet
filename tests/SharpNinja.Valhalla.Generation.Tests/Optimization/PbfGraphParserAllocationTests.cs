using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Optimization;

public sealed class PbfGraphParserAllocationTests
{
    [Fact]
    public void UntaggedNodeTransform_ReusesPrecomputedDefaults()
    {
        const int nodeCount = 50_000;
        var parser = new PbfGraphParser();
        var source = new SyntheticUntaggedNodeSource(nodeCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        OSMData data = parser.Parse(
            source,
            TestContext.Current.CancellationToken);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((ulong)nodeCount, data.OsmWayNodeCount);
        Assert.InRange(allocated, 0, 60_000_000);
    }

    [Fact]
    public void TransientWayTags_AvoidSecondDictionaryMaterialization()
    {
        const int wayCount = 10_000;

        _ = MeasureWayAllocations(100, transientTags: false);
        _ = MeasureWayAllocations(100, transientTags: true);

        long copiedAllocations = MeasureWayAllocations(wayCount, transientTags: false);
        long transientAllocations = MeasureWayAllocations(wayCount, transientTags: true);

        Assert.True(
            transientAllocations <= copiedAllocations - 2_000_000,
            $"Expected transient tags to avoid at least 2 MB of allocation; " +
            $"copied={copiedAllocations}, transient={transientAllocations}.");
    }

    private static long MeasureWayAllocations(int wayCount, bool transientTags)
    {
        var parser = new PbfGraphParser();
        var source = new SyntheticTaggedWaySource(wayCount, transientTags);
        long before = GC.GetAllocatedBytesForCurrentThread();
        OSMData data = parser.Parse(
            source,
            TestContext.Current.CancellationToken);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((ulong)wayCount, data.OsmWayCount);
        return allocated;
    }

    private sealed class SyntheticTaggedWaySource : IOsmPbfEntitySource
    {
        private readonly int wayCount;
        private readonly bool transientTags;

        public SyntheticTaggedWaySource(int wayCount, bool transientTags)
        {
            this.wayCount = wayCount;
            this.transientTags = transientTags;
        }

        public int FileCount => 1;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, fileOrdinal);
            if (pass != OsmPbfEntityPass.Ways)
            {
                return;
            }

            for (var index = 0; index < wayCount; index++)
            {
                if ((index & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                IReadOnlyDictionary<string, string> tags = transientTags
                    ? new OsmPbfTransientTagDictionary(1)
                    {
                        ["highway"] = "residential",
                    }
                    : new Dictionary<string, string>(1, StringComparer.Ordinal)
                    {
                        ["highway"] = "residential",
                    };
                ulong firstNode = checked((ulong)(index * 2 + 1));
                visitor.Way(
                    checked((ulong)(index + 1)),
                    [firstNode, firstNode + 1],
                    tags);
            }
        }
    }

    private sealed class SyntheticUntaggedNodeSource : IOsmPbfEntitySource
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyTags =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string, string> RoadTags =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = "residential",
            };

        private readonly ulong[] nodeReferences;

        public SyntheticUntaggedNodeSource(int nodeCount)
        {
            nodeReferences = Enumerable.Range(1, nodeCount)
                .Select(static id => checked((ulong)id))
                .ToArray();
        }

        public int FileCount => 1;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, fileOrdinal);
            switch (pass)
            {
                case OsmPbfEntityPass.Ways:
                    visitor.Way(1, nodeReferences, RoadTags);
                    break;
                case OsmPbfEntityPass.Nodes:
                    for (var index = 0; index < nodeReferences.Length; index++)
                    {
                        if ((index & 4095) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        ulong id = nodeReferences[index];
                        visitor.Node(
                            id,
                            36.0 + (index * 0.000001),
                            -86.0,
                            EmptyTags);
                    }

                    break;
                case OsmPbfEntityPass.Relations:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pass), pass, null);
            }
        }
    }
}
