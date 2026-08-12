using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledRestrictionMutationPlanTests
{
    [Fact]
    public async Task DuplicateCandidates_DedupeByExactPayloadAndPreserveFirstCanonicalOrder()
    {
        using var workspace = new TemporaryDirectory();
        using var sink = new PooledRestrictionMutationPlanSink(
            Options(workspace.Path));
        var tile = new GraphId(44, 2, 0);
        var from = new GraphId(44, 2, 1);
        var to = new GraphId(44, 2, 2);
        GraphId[] vias = [new GraphId(44, 2, 3)];

        sink.EmitRestriction(
            PlannedRestrictionDirection.Forward,
            tile,
            from,
            to,
            vias,
            RestrictionType.NoTurn,
            GraphConstants.AutoAccess,
            0,
            0,
            crossTile: true,
            canonicalOrdinal: 9);
        sink.EmitRestriction(
            PlannedRestrictionDirection.Forward,
            tile,
            from,
            to,
            vias,
            RestrictionType.NoTurn,
            GraphConstants.AutoAccess,
            0,
            0,
            3);
        sink.EmitRestriction(
            PlannedRestrictionDirection.Reverse,
            tile,
            to,
            from,
            vias,
            RestrictionType.NoTurn,
            GraphConstants.AutoAccess,
            0,
            0,
            4);

        sink.EmitEdgePatch(
            tile,
            7,
            1,
            0,
            false,
            crossTile: true,
            canonicalOrdinal: 8);
        sink.EmitEdgePatch(tile, 7, 2, 4, true, 2);

        using BoundedRestrictionMutationPlan plan =
            await sink.CompleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, plan.Receipt.ProjectedRestrictionCount);
        Assert.Equal(2, plan.Receipt.UniqueRestrictionCount);
        Assert.Equal(2, plan.Receipt.ProjectedEdgePatchCount);
        Assert.Equal(1, plan.Receipt.UniqueEdgePatchCount);

        PlannedRestrictionRecord forward = plan.ReadRestriction(0);
        Assert.Equal(3UL, forward.CanonicalOrdinal);
        Assert.Equal(PlannedRestrictionDirection.Forward, forward.Direction);
        Assert.Equal((byte)1, forward.CrossTile);
        Assert.Equal(
            1,
            plan.CountCrossTileRestrictions(
                PlannedRestrictionDirection.Forward));
        PlannedEdgePatchRecord patch = plan.ReadEdgePatch(0);
        Assert.Equal(3U, patch.StartMaskOr);
        Assert.Equal(4U, patch.EndMaskOr);
        Assert.Equal((byte)1, patch.CrossTile);
        Assert.Equal(1, plan.CountCrossTileEdgePatches());
        Assert.Equal((byte)1, patch.SetComplexRestriction);
        Assert.Equal(2UL, patch.CanonicalOrdinal);
    }

    [Fact]
    public async Task ManyLocalRestrictions_UsesFixedRecordsAndSpills()
    {
        using var workspace = new TemporaryDirectory();
        using var sink = new PooledRestrictionMutationPlanSink(
            Options(workspace.Path));
        var tile = new GraphId(9, 2, 0);

        for (ulong ordinal = 0; ordinal < 10_000; ordinal++)
        {
            var from = new GraphId(9, 2, (uint)(ordinal % 100));
            var to = new GraphId(9, 2, (uint)((ordinal + 1) % 100));
            sink.EmitRestriction(
                PlannedRestrictionDirection.Forward,
                tile,
                from,
                to,
                ReadOnlySpan<GraphId>.Empty,
                RestrictionType.NoTurn,
                GraphConstants.AutoAccess,
                0,
                0,
                ordinal);
        }

        using BoundedRestrictionMutationPlan plan =
            await sink.CompleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10_000, plan.Receipt.ProjectedRestrictionCount);
        Assert.Equal(100, plan.Receipt.UniqueRestrictionCount);
        Assert.True(plan.Receipt.RestrictionStoreBytes > 0);
        Assert.Equal(
            0,
            plan.Receipt.MissingDestinationCount);
        Assert.InRange(
            plan.Receipt.PeakAggregateMemoryBytes,
            1,
            plan.Receipt.MemoryBudgetBytes);
        Assert.InRange(
            plan.Receipt.PeakAggregateScratchBytes,
            1,
            plan.Receipt.ScratchDiskBudgetBytes);
        Assert.Equal(
            plan.Receipt.PeakAggregateScratchBytes,
            plan.Receipt.ScratchHighWaterMarkBytes);
    }
    private static PooledRestrictionMutationPlanOptions Options(
        string workingDirectory)
        => new(
            workingDirectory,
            MemoryBudgetBytes: 512 * 1024,
            ScratchDiskBudgetBytes: 128 * 1024 * 1024,
            SegmentSizeBytes: 64 * 1024);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pooled-restriction-plan-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
