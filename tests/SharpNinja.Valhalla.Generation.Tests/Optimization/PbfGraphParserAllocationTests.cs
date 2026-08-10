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
