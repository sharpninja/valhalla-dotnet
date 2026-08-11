using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledNodeArenaHostileTests
{
    [Fact]
    public void Release_DoubleReleaseIsRejectedAsStale()
    {
        using var arena = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);
        NodeHandle handle = arena.Rent(CompletedNode(1));

        arena.Release(handle);

        Assert.Throws<StaleNodeHandleException>(() => arena.Release(handle));
        Assert.Equal(1, arena.Metrics.StaleHandleRejections);
    }

    [Fact]
    public void Resolve_CrossArenaHandleIsRejected()
    {
        using var first = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);
        using var second = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);
        NodeHandle handle = first.Rent(CompletedNode(1));

        Assert.Throws<StaleNodeHandleException>(() => second.Resolve(handle));
        Assert.Equal(1, second.Metrics.StaleHandleRejections);
    }

    [Fact]
    public void GenerationWrap_QuarantinesSlotInsteadOfAliasing()
    {
        using var arena = new PooledNodeArena(
            slabCapacity: 1,
            memoryBudgetBytes: 4096,
            initialGeneration: uint.MaxValue);
        NodeHandle handle = arena.Rent(CompletedNode(1));

        arena.Release(handle);
        NodeHandle replacement = arena.Rent(CompletedNode(2));

        Assert.NotEqual(handle.SlabIndex, replacement.SlabIndex);
        Assert.Equal(1, arena.Metrics.QuarantinedSlotCount);
        Assert.Equal(2, arena.Metrics.TotalSlabsRented);
    }

    [Fact]
    public void MemoryBudget_RejectsUnboundedSlabGrowth()
    {
        using var arena = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 64);
        arena.Rent(default);

        Assert.Throws<ValhallaGenerationResourceLimitException>(
            () => arena.Rent(default));
        Assert.Equal(1, arena.Metrics.TotalSlabsRented);
        Assert.Equal(1, arena.Metrics.LiveSlotCount);
    }

    [Fact]
    public void MillionRentReleaseCycles_ReuseOneClearedSlot()
    {
        const int iterations = 1_000_000;
        using var arena = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);

        for (int index = 0; index < iterations; index++)
        {
            NodeHandle handle = arena.Rent(CompletedNode(index + 1L));
            arena.Release(handle);
        }

        Assert.Equal(iterations, arena.Metrics.TotalSlotRents);
        Assert.Equal(iterations - 1, arena.Metrics.SlotReuseCount);
        Assert.Equal(1, arena.Metrics.TotalSlabsRented);
        Assert.Equal(1, arena.Metrics.PeakLiveSlotCount);
        Assert.Equal(0, arena.Metrics.LiveSlotCount);
    }

    private static NodeWorkItem CompletedNode(long osmNodeId) => new()
    {
        OsmNodeId = osmNodeId,
        StableGraphId = GraphId.Invalid,
        LifecycleFlags = NodeLifecycleFlags.AllDurableStateWritten,
    };
}
