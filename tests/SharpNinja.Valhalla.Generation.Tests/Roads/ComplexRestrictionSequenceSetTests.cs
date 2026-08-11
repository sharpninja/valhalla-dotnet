using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class ComplexRestrictionSequenceSetTests
{
    private const uint DefaultModes =
        (uint)(GraphConstants.AutoAccess |
               GraphConstants.MopedAccess |
               GraphConstants.TaxiAccess |
               GraphConstants.BusAccess |
               GraphConstants.BicycleAccess |
               GraphConstants.TruckAccess |
               GraphConstants.EmergencyAccess |
               GraphConstants.MotorcycleAccess);

    [Fact]
    public void ProjectionRecords_AreUnmanagedReferenceFree()
    {
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<
                ComplexRestrictionProjectionRecord>());
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<
                ReverseRestrictionProjectionRecord>());
    }

    [Fact]
    public async Task BuildAsync_ProjectsLegacyFieldsOrderingAndNodeViaSentinelLazily()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionMatrixSource(
                    [
                        RestrictionSpec.HgvNodeVia(10, 100, 20),
                        RestrictionSpec.ExceptHgvNodeVia(20, 101, 30),
                        RestrictionSpec.ViaWays(30, [40, 41], 50),
                        RestrictionSpec.ProbableNodeVia(40, 102, 50, 75),
                    ]),
                    CreateSemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            using ComplexRestrictionSequenceSet sequenceSet =
                await ComplexRestrictionSequenceSet.BuildAsync(
                    semanticStore,
                    CreateSequenceOptions(Path.Combine(root, "projection")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(4, sequenceSet.Forward.Count);
            Assert.Equal(4, sequenceSet.Reverse.Count);
            Assert.Equal(0, sequenceSet.ForwardMaterializationCount);
            Assert.Equal(0, sequenceSet.ReverseMaterializationCount);
            Assert.Equal(
                IntermediateStorageMode.MemoryMapped,
                sequenceSet.ForwardStorageMode);

            OSMRestriction hgv = sequenceSet.Forward[0];
            Assert.Equal(10UL, hgv.From());
            Assert.Equal(20UL, hgv.To());
            Assert.Equal([20UL], hgv.Vias());
            Assert.Equal(RestrictionType.NoRightTurn, hgv.TypeValue());
            Assert.Equal((uint)GraphConstants.TruckAccess, hgv.Modes());

            OSMRestriction exceptHgv = sequenceSet.Forward[1];
            Assert.Equal(20UL, exceptHgv.From());
            Assert.Equal([30UL], exceptHgv.Vias());
            Assert.Equal(
                DefaultModes & ~(uint)GraphConstants.TruckAccess,
                exceptHgv.Modes());

            OSMRestriction viaWays = sequenceSet.Forward[2];
            Assert.Equal(30UL, viaWays.From());
            Assert.Equal(50UL, viaWays.To());
            Assert.Equal([40UL, 41UL], viaWays.Vias());
            Assert.Equal(RestrictionType.NoLeftTurn, viaWays.TypeValue());
            Assert.Equal(DefaultModes, viaWays.Modes());

            OSMRestriction probable = sequenceSet.Forward[3];
            Assert.Equal(RestrictionType.NoProbable, probable.TypeValue());
            Assert.Equal((byte)75, probable.Probability());
            Assert.Equal([50UL], probable.Vias());

            Assert.Equal(4, sequenceSet.ForwardMaterializationCount);
            Assert.Equal(1, sequenceSet.PeakCachedRestrictionCount);

            OSMRestriction reverse = sequenceSet.Reverse[0];
            Assert.Equal(20UL, reverse.From());
            Assert.Equal(10UL, reverse.To());
            Assert.Empty(reverse.Vias());
            Assert.Equal((uint)GraphConstants.TruckAccess, reverse.Modes());
            Assert.Equal(1, sequenceSet.ReverseMaterializationCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_MultiViaPreservesRelationOrderAndRejectsMixedOrOversizedVias()
    {
        string root = CreateRoot();
        try
        {
            var oversizedVias = Enumerable.Range(0, OSMRestriction.MaxViasPerRestriction + 1)
                .Select(index => 1_000L + index)
                .ToArray();
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionMatrixSource(
                    [
                        RestrictionSpec.ViaWays(10, [31, 30, 32], 20),
                        RestrictionSpec.MixedVias(20, 200, 40, 30),
                        RestrictionSpec.ViaWays(30, oversizedVias, 40),
                    ]),
                    CreateSemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);

            Assert.Equal(1, semanticStore.RestrictionCount);
            using ComplexRestrictionSequenceSet sequenceSet =
                await ComplexRestrictionSequenceSet.BuildAsync(
                    semanticStore,
                    CreateSequenceOptions(Path.Combine(root, "projection")),
                    TestContext.Current.CancellationToken);

            OSMRestriction restriction = Assert.Single(sequenceSet.Forward);
            Assert.Equal([31UL, 30UL, 32UL], restriction.Vias());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConditionalRestriction_FailsClosedUntilOfficialTimeDomainParserAndRuntimeEvaluatorExist()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionMatrixSource(
                    [
                        RestrictionSpec.ConditionalNodeVia(
                            10,
                            100,
                            20,
                            "no_left_turn @ Mo-Fr 07:00-09:00"),
                    ]),
                    CreateSemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);

            InvalidDataException error =
                await Assert.ThrowsAsync<InvalidDataException>(
                    async () =>
                    {
                        using ComplexRestrictionSequenceSet _ =
                            await ComplexRestrictionSequenceSet.BuildAsync(
                                semanticStore,
                                CreateSequenceOptions(
                                    Path.Combine(root, "projection")),
                                TestContext.Current.CancellationToken);
                    });

            Assert.Contains(
                "conditional restriction",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("hour_on")]
    [InlineData("hour_off")]
    [InlineData("day_on")]
    [InlineData("day_off")]
    public async Task LegacyConditionalTag_FailsClosedAndCleansProjection(
        string conditionalTag)
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionMatrixSource(
                    [
                        RestrictionSpec.LegacyConditionalNodeVia(
                            10,
                            100,
                            20,
                            conditionalTag),
                    ]),
                    CreateSemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string projectionRoot = Path.Combine(root, "projection");
            Directory.CreateDirectory(projectionRoot);

            InvalidDataException error =
                await Assert.ThrowsAsync<InvalidDataException>(
                    async () =>
                    {
                        using ComplexRestrictionSequenceSet _ =
                            await ComplexRestrictionSequenceSet.BuildAsync(
                                semanticStore,
                                CreateSequenceOptions(projectionRoot),
                                TestContext.Current.CancellationToken);
                    });

            Assert.Contains(
                "conditional restriction",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(
                Directory.EnumerateDirectories(
                    projectionRoot,
                    "complex-restrictions-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConflictingTypeSpecificRestrictions_FailClosedAndCleanProjection()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionMatrixSource(
                    [
                        RestrictionSpec.ConflictingTypeSpecificNodeVia(
                            10,
                            100,
                            20),
                    ]),
                    CreateSemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string projectionRoot = Path.Combine(root, "projection");
            Directory.CreateDirectory(projectionRoot);

            InvalidDataException error =
                await Assert.ThrowsAsync<InvalidDataException>(
                    async () =>
                    {
                        using ComplexRestrictionSequenceSet _ =
                            await ComplexRestrictionSequenceSet.BuildAsync(
                                semanticStore,
                                CreateSequenceOptions(projectionRoot),
                                TestContext.Current.CancellationToken);
                    });

            Assert.Contains(
                "conflicting type-specific",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(
                Directory.EnumerateDirectories(
                    projectionRoot,
                    "complex-restrictions-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(1L, 1024L)]
    [InlineData(1024L, 1023L)]
    public async Task InsufficientAggregateBudget_FailsBeforeCreatingWorkDirectory(
        long memoryBudgetBytes,
        long scratchDiskBudgetBytes)
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionMatrixSource(
                    [
                        RestrictionSpec.HgvNodeVia(10, 100, 20),
                    ]),
                    CreateSemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string projectionRoot = Path.Combine(root, "projection");
            Directory.CreateDirectory(projectionRoot);
            var options = new ComplexRestrictionSequenceSetOptions(
                projectionRoot,
                IntermediateStorageMode.Auto,
                memoryBudgetBytes,
                scratchDiskBudgetBytes,
                SegmentSizeBytes: 256);

            await Assert.ThrowsAsync<
                ValhallaGenerationResourceLimitException>(
                async () =>
                {
                    using ComplexRestrictionSequenceSet _ =
                        await ComplexRestrictionSequenceSet.BuildAsync(
                            semanticStore,
                            options,
                            TestContext.Current.CancellationToken);
                });

            Assert.Empty(
                Directory.EnumerateDirectories(
                    projectionRoot,
                    "complex-restrictions-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringProjection_CleansEveryWorkingStore()
    {
        string root = CreateRoot();
        try
        {
            RestrictionSpec[] restrictions = Enumerable.Range(0, 20_000)
                .Select(
                    index => RestrictionSpec.HgvNodeVia(
                        10L + (index * 3L),
                        11L + (index * 3L),
                        12L + (index * 3L)))
                .ToArray();
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionMatrixSource(restrictions),
                    CreateSemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string projectionRoot = Path.Combine(root, "projection");
            Directory.CreateDirectory(projectionRoot);
            using var cancellation = CancellationTokenSource
                .CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);
            Task cancelWhenStarted = Task.Run(
                async () =>
                {
                    while (!Directory.EnumerateDirectories(
                                   projectionRoot,
                                   "complex-restrictions-*")
                               .Any())
                    {
                        TestContext.Current.CancellationToken
                            .ThrowIfCancellationRequested();
                        await Task.Yield();
                    }

                    await cancellation.CancelAsync();
                },
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () =>
                {
                    using ComplexRestrictionSequenceSet _ =
                        await ComplexRestrictionSequenceSet.BuildAsync(
                            semanticStore,
                            CreateSequenceOptions(projectionRoot),
                            cancellation.Token);
                });
            await cancelWhenStarted;

            Assert.Empty(
                Directory.EnumerateDirectories(
                    projectionRoot,
                    "complex-restrictions-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_RemovesWorkingStoresAndIsIdempotent()
    {
        string root = CreateRoot();
        try
        {
            using CompactOsmSemanticStore semanticStore =
                await CompactOsmSemanticStore.BuildAsync(
                    new RestrictionMatrixSource(
                    [
                        RestrictionSpec.HgvNodeVia(10, 100, 20),
                    ]),
                    CreateSemanticOptions(Path.Combine(root, "semantic")),
                    TestContext.Current.CancellationToken);
            string projectionRoot = Path.Combine(root, "projection");
            Directory.CreateDirectory(projectionRoot);
            ComplexRestrictionSequenceSet sequenceSet =
                await ComplexRestrictionSequenceSet.BuildAsync(
                    semanticStore,
                    CreateSequenceOptions(projectionRoot),
                    TestContext.Current.CancellationToken);
            string workingDirectory = Assert.Single(
                Directory.EnumerateDirectories(
                    projectionRoot,
                    "complex-restrictions-*"));

            sequenceSet.Dispose();
            sequenceSet.Dispose();

            Assert.False(Directory.Exists(workingDirectory));
            Assert.Empty(
                Directory.EnumerateDirectories(
                    projectionRoot,
                    "complex-restrictions-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CompactOsmSemanticStoreOptions CreateSemanticOptions(
        string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 16 * 1024 * 1024,
            ScratchDiskBudgetBytes: 64 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private static ComplexRestrictionSequenceSetOptions CreateSequenceOptions(
        string root) =>
        new(
            root,
            IntermediateStorageMode.Auto,
            MemoryBudgetBytes: 256,
            ScratchDiskBudgetBytes: 4 * 1024 * 1024,
            SegmentSizeBytes: 256);

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Generation.Tests",
            nameof(ComplexRestrictionSequenceSetTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RestrictionMatrixSource(
        IReadOnlyList<RestrictionSpec> restrictions) : IOsmPbfEntitySource
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

            for (int index = 0; index < restrictions.Count; index++)
            {
                RestrictionSpec restriction = restrictions[index];
                visitor.Relation(
                    checked((ulong)(1_000 + index)),
                    restriction.Members,
                    restriction.Tags);
            }
        }
    }

    private sealed record RestrictionSpec(
        IReadOnlyList<OsmRelationMember> Members,
        IReadOnlyDictionary<string, string> Tags)
    {
        internal static RestrictionSpec ViaWays(
            long from,
            IReadOnlyList<long> vias,
            long to)
        {
            var members = new List<OsmRelationMember>
            {
                new(checked((ulong)from), OsmMemberType.Way, "from"),
            };
            members.AddRange(
                vias.Select(
                    via => new OsmRelationMember(
                        checked((ulong)via),
                        OsmMemberType.Way,
                        "via")));
            members.Add(
                new OsmRelationMember(
                    checked((ulong)to),
                    OsmMemberType.Way,
                    "to"));
            return new RestrictionSpec(members, GenericTags("no_left_turn"));
        }

        internal static RestrictionSpec HgvNodeVia(
            long from,
            long via,
            long to) =>
            NodeVia(
                from,
                via,
                to,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction:hgv"] = "no_right_turn",
                });

        internal static RestrictionSpec ExceptHgvNodeVia(
            long from,
            long via,
            long to) =>
            NodeVia(
                from,
                via,
                to,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction"] = "no_straight_on",
                    ["except"] = "hgv",
                });

        internal static RestrictionSpec ProbableNodeVia(
            long from,
            long via,
            long to,
            byte probability) =>
            NodeVia(
                from,
                via,
                to,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction:probable"] =
                        $"no_left_turn @ probability={probability}",
                });

        internal static RestrictionSpec ConditionalNodeVia(
            long from,
            long via,
            long to,
            string condition) =>
            NodeVia(
                from,
                via,
                to,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction:conditional"] = condition,
                });

        internal static RestrictionSpec LegacyConditionalNodeVia(
            long from,
            long via,
            long to,
            string conditionalTag) =>
            NodeVia(
                from,
                via,
                to,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction"] = "no_left_turn",
                    [conditionalTag] = "legacy-condition",
                });

        internal static RestrictionSpec ConflictingTypeSpecificNodeVia(
            long from,
            long via,
            long to) =>
            NodeVia(
                from,
                via,
                to,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "restriction",
                    ["restriction:hgv"] = "no_left_turn",
                    ["restriction:bus"] = "no_right_turn",
                });

        internal static RestrictionSpec MixedVias(
            long from,
            long nodeVia,
            long wayVia,
            long to) =>
            new(
                [
                    new OsmRelationMember(
                        checked((ulong)from),
                        OsmMemberType.Way,
                        "from"),
                    new OsmRelationMember(
                        checked((ulong)nodeVia),
                        OsmMemberType.Node,
                        "via"),
                    new OsmRelationMember(
                        checked((ulong)wayVia),
                        OsmMemberType.Way,
                        "via"),
                    new OsmRelationMember(
                        checked((ulong)to),
                        OsmMemberType.Way,
                        "to"),
                ],
                GenericTags("no_left_turn"));

        private static RestrictionSpec NodeVia(
            long from,
            long via,
            long to,
            IReadOnlyDictionary<string, string> tags) =>
            new(
                [
                    new OsmRelationMember(
                        checked((ulong)from),
                        OsmMemberType.Way,
                        "from"),
                    new OsmRelationMember(
                        checked((ulong)via),
                        OsmMemberType.Node,
                        "via"),
                    new OsmRelationMember(
                        checked((ulong)to),
                        OsmMemberType.Way,
                        "to"),
                ],
                tags);

        private static IReadOnlyDictionary<string, string> GenericTags(
            string restriction) =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "restriction",
                ["restriction"] = restriction,
            };
    }
}
