using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Generation.Tests.Pbf;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class CompactOsmSemanticStoreTests
{
    [Fact]
    public async Task BuildAsync_PersistsTransformedNodesWaysRelationsAndCompleteIncidence()
    {
        string root = CreateRoot();
        try
        {
            var source = new SemanticFixtureSource();
            using CompactOsmSemanticStore store =
                await CompactOsmSemanticStore.BuildAsync(
                    source,
                    CreateOptions(root),
                    TestContext.Current.CancellationToken);

            Assert.Equal(
                [
                    OsmPbfEntityPass.Ways,
                    OsmPbfEntityPass.Relations,
                    OsmPbfEntityPass.Nodes,
                ],
                source.CompletedPasses);
            Assert.Equal(1, store.WayCount);
            Assert.Equal(3, store.WayNodeReferenceCount);
            Assert.Equal(1, store.RelationCount);
            Assert.Equal(3, store.RelationMemberCount);
            Assert.Equal(3, store.NodeCount);
            Assert.Equal(4, store.IncidenceCount);
            Assert.Equal(3, store.IncidenceSummaryCount);

            GenerationWayRecord way = store.ReadWay(0);
            Assert.Equal(10, way.OsmWayId);
            Assert.Equal(0, way.NodeReferenceOffset);
            Assert.Equal(3, way.NodeReferenceCount);
            Assert.Equal("residential", store.ReadTags(way.TagReference)["highway"]);
            Assert.True(store.ReadTags(way.TagReference).ContainsKey("auto_forward"));

            Assert.Equal(1, store.ReadWayNodeReference(0).OsmNodeId);
            Assert.Equal(2, store.ReadWayNodeReference(1).OsmNodeId);
            Assert.Equal(3, store.ReadWayNodeReference(2).OsmNodeId);

            GenerationRelationRecord relation = store.ReadRelation(0);
            Assert.Equal(20, relation.OsmRelationId);
            Assert.Equal(
                ((byte)RestrictionType.NoLeftTurn).ToString(),
                store.ReadTags(relation.TagReference)["restriction"]);
            Assert.Equal("from", store.ReadRole(store.ReadRelationMember(0).RoleReference));
            Assert.Equal("via", store.ReadRole(store.ReadRelationMember(1).RoleReference));
            Assert.Equal("to", store.ReadRole(store.ReadRelationMember(2).RoleReference));

            GenerationNodeRecord signal = store.ReadNode(1);
            Assert.Equal(2, signal.OsmNodeId);
            Assert.True(signal.Flags.HasFlag(NodeSemanticFlags.TrafficSignal));
            Assert.Equal(
                "traffic_signals",
                store.ReadTags(signal.TagReference)["highway"]);

            GenerationNodeRecord gate = store.ReadNode(2);
            Assert.Equal(3, gate.OsmNodeId);
            Assert.True(gate.Flags.HasFlag(NodeSemanticFlags.Barrier));
            Assert.True(gate.Flags.HasFlag(NodeSemanticFlags.Gate));

            Assert.True(store.TryFindIncidenceSummary(2, out NodeIncidenceSummary summary));
            Assert.True(summary.AnchorFlags.HasFlag(NodeAnchorFlags.RestrictionBoundary));
            Assert.Equal(2, summary.IncidenceCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_StoredPbfSource_UsesSharedSemanticStoreWithoutLegacyObjects()
    {
        string root = CreateRoot();
        string pbfPath = Path.Combine(root, "fixture.osm.pbf");
        await File.WriteAllBytesAsync(
            pbfPath,
            TestOsmPbfFixtureBuilder.Create(OsmPbfCompressionKind.Zlib),
            TestContext.Current.CancellationToken);

        try
        {
            using var source = await StoredOsmPbfEntitySource.CreateAsync(
                [pbfPath],
                Path.Combine(root, "decoded"),
                IntermediateStorageMode.Memory,
                memoryBudgetBytes: 8 * 1024 * 1024,
                scratchDiskBudgetBytes: 32 * 1024 * 1024,
                TestContext.Current.CancellationToken);
            using CompactOsmSemanticStore store =
                await CompactOsmSemanticStore.BuildAsync(
                    source,
                    CreateOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(3, source.CompletedReplayPassCount);
            Assert.Equal(1, store.WayCount);
            Assert.Equal(2, store.WayNodeReferenceCount);
            Assert.Equal(1, store.RelationCount);
            Assert.Equal(1, store.RelationMemberCount);
            Assert.Equal(1, store.NodeCount);
            Assert.Equal("residential", store.ReadTags(store.ReadWay(0).TagReference)["highway"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_PersistsArrayFreeRestrictionRecordsWithoutManagedViaArrays()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore store =
                await CompactOsmSemanticStore.BuildAsync(
                    new SemanticFixtureSource(),
                    CreateOptions(root),
                    TestContext.Current.CancellationToken);

            Assert.False(
                System.Runtime.CompilerServices.RuntimeHelpers
                    .IsReferenceOrContainsReferences<GenerationRestrictionRecord>());
            Assert.False(
                System.Runtime.CompilerServices.RuntimeHelpers
                    .IsReferenceOrContainsReferences<GenerationRestrictionViaRecord>());
            Assert.Equal(1, store.RestrictionCount);
            Assert.Equal(1, store.RestrictionViaCount);

            GenerationRestrictionRecord restriction = store.ReadRestriction(0);
            Assert.Equal(20, restriction.OsmRelationId);
            Assert.Equal(10, restriction.FromWayId);
            Assert.Equal(12, restriction.ToWayId);
            Assert.Equal(0, restriction.ViaOffset);
            Assert.Equal(1, restriction.ViaCount);
            Assert.Equal(
                ((byte)RestrictionType.NoLeftTurn).ToString(),
                store.ReadTags(restriction.TagReference)["restriction"]);

            GenerationRestrictionViaRecord via = store.ReadRestrictionVia(0);
            Assert.Equal(20, via.OsmRelationId);
            Assert.Equal(2, via.MemberId);
            Assert.Equal(OsmMemberType.Node, via.MemberType);
            Assert.Equal(0, via.ViaOrdinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public async Task BuildAsync_RelationFileOrderingDoesNotChangeDurableRestrictionRecords()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore relationFirst =
                await CompactOsmSemanticStore.BuildAsync(
                    new SplitRestrictionSource(relationBeforeWays: true),
                    CreateOptions(Path.Combine(root, "relation-first")),
                    TestContext.Current.CancellationToken);
            using CompactOsmSemanticStore waysFirst =
                await CompactOsmSemanticStore.BuildAsync(
                    new SplitRestrictionSource(relationBeforeWays: false),
                    CreateOptions(Path.Combine(root, "ways-first")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(relationFirst.ReadRestriction(0), waysFirst.ReadRestriction(0));
            Assert.Equal(relationFirst.ReadRestrictionVia(0), waysFirst.ReadRestrictionVia(0));
            Assert.Equal(
                relationFirst.ReadTags(relationFirst.ReadRestriction(0).TagReference),
                waysFirst.ReadTags(waysFirst.ReadRestriction(0).TagReference));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SplitRestrictionSource(bool relationBeforeWays) : IOsmPbfEntitySource
    {
        public int FileCount => 2;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int relationFile = relationBeforeWays ? 0 : 1;
            int wayFile = relationBeforeWays ? 1 : 0;
            if (pass == OsmPbfEntityPass.Ways && fileOrdinal == wayFile)
            {
                visitor.Way(10, [1UL, 2UL], RoadTags());
                visitor.Way(12, [2UL, 3UL], RoadTags());
            }
            else if (pass == OsmPbfEntityPass.Relations && fileOrdinal == relationFile)
            {
                visitor.Relation(
                    20,
                    [
                        new OsmRelationMember(10, OsmMemberType.Way, "from"),
                        new OsmRelationMember(2, OsmMemberType.Node, "via"),
                        new OsmRelationMember(12, OsmMemberType.Way, "to"),
                    ],
                    RestrictionTags());
            }
            else if (pass == OsmPbfEntityPass.Nodes && fileOrdinal == wayFile)
            {
                visitor.Node(1, 36.10, -86.70, EmptyTags());
                visitor.Node(2, 36.11, -86.71, EmptyTags());
                visitor.Node(3, 36.12, -86.72, EmptyTags());
            }
        }

        private static IReadOnlyDictionary<string, string> RoadTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["highway"] = "residential",
            };

        private static IReadOnlyDictionary<string, string> RestrictionTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "restriction",
                ["restriction"] = "no_left_turn",
            };

        private static IReadOnlyDictionary<string, string> EmptyTags() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }


    [Fact]
    public async Task BuildAsync_RestrictionStructureMatrixPublishesOnlyCompleteDurableRecords()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore validComplex =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionStructureSource(
                        [
                            new OsmRelationMember(10, OsmMemberType.Way, "from"),
                            new OsmRelationMember(20, OsmMemberType.Way, "via"),
                            new OsmRelationMember(21, OsmMemberType.Way, "via"),
                            new OsmRelationMember(12, OsmMemberType.Way, "to"),
                        ]),
                    CreateOptions(Path.Combine(root, "valid-complex")),
                    TestContext.Current.CancellationToken);
            using CompactOsmSemanticStore invalidMixed =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionStructureSource(
                        [
                            new OsmRelationMember(10, OsmMemberType.Way, "from"),
                            new OsmRelationMember(2, OsmMemberType.Node, "via"),
                            new OsmRelationMember(20, OsmMemberType.Way, "via"),
                            new OsmRelationMember(12, OsmMemberType.Way, "to"),
                        ]),
                    CreateOptions(Path.Combine(root, "invalid-mixed")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(1, validComplex.RestrictionCount);
            Assert.Equal(2, validComplex.RestrictionViaCount);
            Assert.Equal(OsmMemberType.Way, validComplex.ReadRestrictionVia(0).MemberType);
            Assert.Equal(OsmMemberType.Way, validComplex.ReadRestrictionVia(1).MemberType);
            Assert.Equal(0, invalidMixed.RestrictionCount);
            Assert.Equal(0, invalidMixed.RestrictionViaCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RestrictionStructureSource(
        IReadOnlyList<OsmRelationMember> members) : IOsmPbfEntitySource
    {
        public int FileCount => 1;

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, fileOrdinal);
            cancellationToken.ThrowIfCancellationRequested();
            if (pass != OsmPbfEntityPass.Relations)
            {
                return;
            }

            visitor.Relation(
                20,
                members,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction"] = "no_left_turn",
                });
        }
    }


    private static CompactOsmSemanticStoreOptions CreateOptions(string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 16 * 1024 * 1024,
            ScratchDiskBudgetBytes: 64 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Generation.Tests",
            nameof(CompactOsmSemanticStoreTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class SemanticFixtureSource : IOsmPbfEntitySource
    {
        public int FileCount => 1;

        public List<OsmPbfEntityPass> CompletedPasses { get; } = [];

        public void VisitFile(
            int fileOrdinal,
            OsmPbfEntityPass pass,
            IOsmPbfVisitor visitor,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, fileOrdinal);
            cancellationToken.ThrowIfCancellationRequested();
            switch (pass)
            {
                case OsmPbfEntityPass.Ways:
                    visitor.Way(
                        10,
                        [1UL, 2UL, 3UL],
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["highway"] = "residential",
                        });
                    visitor.Way(
                        11,
                        [4UL],
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["highway"] = "service",
                        });
                    break;
                case OsmPbfEntityPass.Relations:
                    visitor.Relation(
                        20,
                        [
                            new OsmRelationMember(10, OsmMemberType.Way, "from"),
                            new OsmRelationMember(2, OsmMemberType.Node, "via"),
                            new OsmRelationMember(12, OsmMemberType.Way, "to"),
                        ],
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["type"] = "restriction",
                            ["restriction"] = "no_left_turn",
                        });
                    break;
                case OsmPbfEntityPass.Nodes:
                    visitor.Node(
                        1,
                        36.10,
                        -86.70,
                        new Dictionary<string, string>(StringComparer.Ordinal));
                    visitor.Node(
                        2,
                        36.11,
                        -86.71,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["highway"] = "traffic_signals",
                        });
                    visitor.Node(
                        3,
                        36.12,
                        -86.72,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["barrier"] = "gate",
                        });
                    visitor.Node(
                        99,
                        36.13,
                        -86.73,
                        new Dictionary<string, string>(StringComparer.Ordinal));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pass), pass, null);
            }

            CompletedPasses.Add(pass);
        }
    }
}
